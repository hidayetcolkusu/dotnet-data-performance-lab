using System.Text;
using DataPerformanceLab.Catalog;
using DataPerformanceLab.Configuration;
using DataPerformanceLab.Data;
using DataPerformanceLab.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataPerformanceLab.IntegrationTests;

/// <summary>
/// Proves the import recovery contract (ADR 002) against real SQL Server: atomic batches, a checkpoint
/// that commits with the outcomes it describes, and a resume that neither loses nor duplicates
/// work — including when the commit reply is lost.
/// </summary>
[Collection("sql")]
public sealed class ImportRecoveryTests(SqlServerFixture sql) : IAsyncLifetime
{
    private const int BatchSize = 500;

    /// <summary>1,201 records over a 500 batch: two full batches, then a partial third.</summary>
    private const int RecordCount = 1_201;

    private string _database = string.Empty;

    public async Task InitializeAsync()
    {
        _database = await sql.CreateAndMigrateTestDatabaseAsync();

        // Import needs the 20 categories to satisfy the FK; products are left empty.
        await using var db = sql.CreateContext(_database);
        db.Categories.AddRange(new CatalogSeeder().CreateCategories());
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private ILabDbContextFactory Factory => new LabDbContextFactory(Options.Create(new LabDataOptions
    {
        DatabaseName = _database,
        SqlServer = new LabDataOptions.SqlServerOptions
        {
            Host = sql.Host,
            Port = sql.Port,
            PasswordOverride = sql.Password
        }
    }));

    private ImportRunner Runner(ImportFaultInjector? faults = null) =>
        new(Factory, _database, faults);

    private static byte[] BuildFile(int recordCount, int invalidEvery = 0, string prefix = "IMP")
    {
        var builder = new StringBuilder(string.Join(',', CsvImportParser.ExpectedHeader)).Append('\n');
        for (var i = 1; i <= recordCount; i++)
        {
            var invalid = invalidEvery > 0 && i % invalidEvery == 0;
            builder
                .Append(prefix).Append('-').Append(i.ToString("D10")).Append(',')
                .Append(invalid ? string.Empty : "Product " + i).Append(',')
                .Append((i % 20) + 1).Append(',')
                .Append("10.00,true,Row ").Append(i).Append('\n');
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static byte[] CustomFile(params string[] records)
    {
        var builder = new StringBuilder(string.Join(',', CsvImportParser.ExpectedHeader)).Append('\n');
        foreach (var record in records)
        {
            builder.Append(record).Append('\n');
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private async Task<ImportSummary> ReadJobAsync(Guid jobId)
    {
        await using var db = sql.CreateContext(_database);
        return ImportBatchProcessor.ToSummary(await db.ImportJobs.AsNoTracking().SingleAsync(j => j.Id == jobId));
    }

    private async Task<Guid> SingleJobIdAsync()
    {
        await using var db = sql.CreateContext(_database);
        return (await db.ImportJobs.AsNoTracking().SingleAsync()).Id;
    }

    private async Task<int> ProductCountAsync()
    {
        await using var db = sql.CreateContext(_database);
        return await db.Products.AsNoTracking().CountAsync();
    }

    /// <summary>
    /// An aborted batch surfaces as <see cref="ImportInterruptedException"/> carrying the job id,
    /// with the original fault as its inner exception. Asserting both keeps two things pinned: the
    /// caller is handed a resumable handle, and the underlying cause is not swallowed.
    /// </summary>
    private static async Task<ImportInterruptedException> AssertInterruptedAsync(
        Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<ImportInterruptedException>(action);

        Assert.NotEqual(Guid.Empty, ex.JobId);
        Assert.IsType<InjectedImportFaultException>(ex.InnerException);
        Assert.Contains(ex.JobId.ToString(), ex.Message);

        return ex;
    }

    // ---- the happy path ---------------------------------------------------------------------

    [Fact]
    public async Task AValidFileIsImportedCompletelyWithConsistentCounts()
    {
        using var stream = new MemoryStream(BuildFile(RecordCount));

        var result = await Runner().ImportAsync(stream, BatchSize, CancellationToken.None);

        var finished = Assert.IsType<ImportRunResult.Finished>(result);
        var summary = finished.Summary;

        Assert.Equal(ImportJobStatus.Completed, summary.Status);
        Assert.Equal(RecordCount, summary.TotalRecords);
        Assert.Equal(RecordCount, summary.Inserted);
        Assert.Equal(0, summary.Skipped);
        Assert.Equal(0, summary.Rejected);
        Assert.Equal(RecordCount, summary.LastProcessedRecord);
        Assert.True(summary.CountsAreConsistent);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(RecordCount, await ProductCountAsync());
    }

    [Fact]
    public async Task AFileWithNoRecordsCompletesWithEveryCounterAtZero()
    {
        using var stream = new MemoryStream(CustomFile());

        var result = await Runner().ImportAsync(stream, BatchSize, CancellationToken.None);

        var summary = Assert.IsType<ImportRunResult.Finished>(result).Summary;
        Assert.Equal(ImportJobStatus.Completed, summary.Status);
        Assert.Equal(0, summary.TotalRecords);
        Assert.Equal(0, summary.Inserted);
        Assert.Equal(0, summary.Skipped);
        Assert.Equal(0, summary.Rejected);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RowRejectionsCompleteTheJobWithExitCodeTwo()
    {
        // Every tenth record has an empty name and is rejected.
        using var stream = new MemoryStream(BuildFile(100, invalidEvery: 10));

        var result = await Runner().ImportAsync(stream, BatchSize, CancellationToken.None);

        var summary = Assert.IsType<ImportRunResult.Finished>(result).Summary;
        Assert.Equal(ImportJobStatus.CompletedWithErrors, summary.Status);
        Assert.Equal(90, summary.Inserted);
        Assert.Equal(10, summary.Rejected);
        Assert.True(summary.CountsAreConsistent);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(90, await ProductCountAsync());
    }

    // ---- failure before commit ---------------------------------------------------------------

    [Fact]
    public async Task AFailureInTheSecondBatchLeavesTheFirstBatchAndNothingElse()
    {
        using var stream = new MemoryStream(BuildFile(RecordCount));

        await AssertInterruptedAsync(() =>
            Runner(ImportFaultInjector.BeforeCommitOnBatch(2))
                .ImportAsync(stream, BatchSize, CancellationToken.None));

        var jobId = await SingleJobIdAsync();
        var summary = await ReadJobAsync(jobId);

        // Batch one committed; batch two rolled back entirely.
        Assert.Equal(BatchSize, summary.LastProcessedRecord);
        Assert.Equal(BatchSize, summary.Inserted);
        Assert.Equal(BatchSize, await ProductCountAsync());
        Assert.Equal(ImportJobStatus.Interrupted, summary.Status);

        // Exactly the first 500 records carry an outcome; the rest are untouched.
        await using var db = sql.CreateContext(_database);
        var withOutcome = await db.ImportRows.AsNoTracking()
            .CountAsync(r => r.JobId == jobId && r.Outcome != null);
        Assert.Equal(BatchSize, withOutcome);
    }

    [Fact]
    public async Task ResumeAfterAFailedBatchGivesEveryRecordExactlyOneOutcome()
    {
        using var stream = new MemoryStream(BuildFile(RecordCount));
        await AssertInterruptedAsync(() =>
            Runner(ImportFaultInjector.BeforeCommitOnBatch(2))
                .ImportAsync(stream, BatchSize, CancellationToken.None));

        var jobId = await SingleJobIdAsync();
        var result = await Runner().ResumeAsync(jobId, BatchSize, CancellationToken.None);

        var summary = Assert.IsType<ImportRunResult.Finished>(result).Summary;
        Assert.Equal(ImportJobStatus.Completed, summary.Status);
        Assert.Equal(RecordCount, summary.Inserted);
        Assert.True(summary.CountsAreConsistent);
        Assert.Equal(RecordCount, await ProductCountAsync());

        await using var db = sql.CreateContext(_database);
        var rows = await db.ImportRows.AsNoTracking()
            .Where(r => r.JobId == jobId)
            .Select(r => r.Outcome)
            .ToListAsync();

        Assert.Equal(RecordCount, rows.Count);
        Assert.All(rows, outcome => Assert.NotNull(outcome));
    }

    // ---- failure after commit (lost commit reply) --------------------------------------------

    [Fact]
    public async Task ResumeAfterALostCommitReplyDoesNotDuplicateProductsOrCounters()
    {
        using var stream = new MemoryStream(BuildFile(RecordCount));

        // Batch two committed, but the caller never learned that it did.
        await AssertInterruptedAsync(() =>
            Runner(ImportFaultInjector.AfterCommitOnBatch(2))
                .ImportAsync(stream, BatchSize, CancellationToken.None));

        var jobId = await SingleJobIdAsync();
        var afterFault = await ReadJobAsync(jobId);
        Assert.Equal(BatchSize * 2, afterFault.LastProcessedRecord);
        Assert.Equal(BatchSize * 2, await ProductCountAsync());

        // Resume reads the durable checkpoint rather than any in-memory position.
        var summary = Assert.IsType<ImportRunResult.Finished>(
            await Runner().ResumeAsync(jobId, BatchSize, CancellationToken.None)).Summary;

        Assert.Equal(ImportJobStatus.Completed, summary.Status);
        Assert.Equal(RecordCount, summary.Inserted);
        Assert.Equal(0, summary.Skipped);
        Assert.True(summary.CountsAreConsistent);
        Assert.Equal(RecordCount, await ProductCountAsync());
    }

    [Fact]
    public async Task TheCheckpointAndTheOutcomesItDescribesCommitTogether()
    {
        using var stream = new MemoryStream(BuildFile(RecordCount));
        await AssertInterruptedAsync(() =>
            Runner(ImportFaultInjector.BeforeCommitOnBatch(3))
                .ImportAsync(stream, BatchSize, CancellationToken.None));

        var jobId = await SingleJobIdAsync();

        await using var db = sql.CreateContext(_database);
        var job = await db.ImportJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        var highestWithOutcome = await db.ImportRows.AsNoTracking()
            .Where(r => r.JobId == jobId && r.Outcome != null)
            .MaxAsync(r => (int?)r.RecordNumber) ?? 0;
        var outcomeCount = await db.ImportRows.AsNoTracking()
            .CountAsync(r => r.JobId == jobId && r.Outcome != null);

        // The checkpoint is exactly the highest record that has an outcome, and the counters
        // account for precisely those records. Nothing is ahead of or behind the checkpoint.
        Assert.Equal(job.LastProcessedRecord, highestWithOutcome);
        Assert.Equal(job.LastProcessedRecord, outcomeCount);
        Assert.Equal(
            job.LastProcessedRecord,
            job.InsertedCount + job.SkippedCount + job.RejectedCount);
    }

    // ---- re-running the same file ------------------------------------------------------------

    [Fact]
    public async Task ReimportingAFinishedFileReturnsTheStoredResultWithoutRedoingWork()
    {
        var bytes = BuildFile(RecordCount);

        using (var first = new MemoryStream(bytes))
        {
            await Runner().ImportAsync(first, BatchSize, CancellationToken.None);
        }

        using var second = new MemoryStream(bytes);
        var result = await Runner().ImportAsync(second, BatchSize, CancellationToken.None);

        var finished = Assert.IsType<ImportRunResult.Finished>(result);
        Assert.Equal(StagingOutcome.AlreadyStaged, finished.Staging);
        Assert.Equal(RecordCount, finished.Summary.Inserted);
        Assert.Equal(0, finished.Summary.Skipped);

        // Still one job and one product per record: nothing was reprocessed.
        await using var db = sql.CreateContext(_database);
        Assert.Single(await db.ImportJobs.AsNoTracking().ToListAsync());
        Assert.Equal(RecordCount, await ProductCountAsync());
    }

    [Fact]
    public async Task ADifferentFileWithTheSameProductsIsSkippedRatherThanDuplicated()
    {
        using (var first = new MemoryStream(CustomFile(
            "IMP-0000000001,Product 1,2,10.00,true,Row 1")))
        {
            await Runner().ImportAsync(first, BatchSize, CancellationToken.None);
        }

        // Different bytes (an added record) so a new job is created, but the first record is
        // byte-for-byte the same product.
        using var second = new MemoryStream(CustomFile(
            "IMP-0000000001,Product 1,2,10.00,true,Row 1",
            "IMP-0000000002,Product 2,3,20.00,false,Row 2"));

        var summary = Assert.IsType<ImportRunResult.Finished>(
            await Runner().ImportAsync(second, BatchSize, CancellationToken.None)).Summary;

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(1, summary.Inserted);
        Assert.Equal(0, summary.Rejected);
        Assert.Equal(ImportJobStatus.Completed, summary.Status);
        Assert.Equal(2, await ProductCountAsync());
    }

    [Fact]
    public async Task AnExistingSkuWithDifferentValuesIsRejectedRatherThanUpdated()
    {
        using (var first = new MemoryStream(CustomFile(
            "IMP-0000000001,Original Name,2,10.00,true,Row 1")))
        {
            await Runner().ImportAsync(first, BatchSize, CancellationToken.None);
        }

        using var second = new MemoryStream(CustomFile(
            "IMP-0000000001,Changed Name,2,99.00,true,Row 1"));

        var result = await Runner().ImportAsync(second, BatchSize, CancellationToken.None);
        var summary = Assert.IsType<ImportRunResult.Finished>(result).Summary;

        Assert.Equal(1, summary.Rejected);
        Assert.Equal(0, summary.Inserted);
        Assert.Equal(ImportJobStatus.CompletedWithErrors, summary.Status);
        Assert.Equal(2, result.ExitCode);

        // Import is insert-only: the stored product is untouched.
        await using var db = sql.CreateContext(_database);
        var product = await db.Products.AsNoTracking().SingleAsync();
        Assert.Equal("Original Name", product.Name);
        Assert.Equal(10.00m, product.UnitPrice);

        var jobId = (await db.ImportJobs.AsNoTracking()
            .OrderByDescending(j => j.CreatedAtUtc).FirstAsync()).Id;
        var row = await db.ImportRows.AsNoTracking().SingleAsync(r => r.JobId == jobId);
        Assert.Equal(ImportRowErrorCodes.ExistingSkuConflict, row.ValidationErrorCode);
        Assert.Equal(ImportRowOutcome.Rejected, row.Outcome);
    }

    // ---- concurrency and file-level failure ---------------------------------------------------

    [Fact]
    public async Task ASecondImporterReportsBusyInsteadOfProceeding()
    {
        await using var held = await ImportLock.TryAcquireAsync(
            sql.ConnectionStringFor(_database), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.NotNull(held);

        using var stream = new MemoryStream(BuildFile(10));
        var result = await Runner().ImportAsync(stream, BatchSize, CancellationToken.None);

        Assert.IsType<ImportRunResult.Busy>(result);
        Assert.Equal(3, result.ExitCode);

        // Nothing was staged or written while the lock was held elsewhere.
        await using var db = sql.CreateContext(_database);
        Assert.Empty(await db.ImportJobs.AsNoTracking().ToListAsync());
        Assert.Equal(0, await ProductCountAsync());
    }

    public static TheoryData<string, byte[]> FileLevelFailures()
    {
        var header = string.Join(',', CsvImportParser.ExpectedHeader);
        var utf16 = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);

        return new TheoryData<string, byte[]>
        {
            {
                ImportFileErrorCodes.InvalidHeader,
                Encoding.UTF8.GetBytes("sku,name,wrong,unitPrice,isActive,description\n")
            },
            {
                ImportFileErrorCodes.UnsupportedEncoding,
                [.. utf16.GetPreamble(),
                 .. utf16.GetBytes($"{header}\nIMP-0000000001,Name,1,10.00,true,Desc\n")]
            },
            {
                ImportFileErrorCodes.ColumnCountMismatch,
                Encoding.UTF8.GetBytes(
                    $"{header}\nIMP-0000000001,Name,1,10.00,true,Desc\n\nIMP-0000000002,Name,1,10.00,true,Desc\n")
            },
            {
                ImportFileErrorCodes.ColumnCountMismatch,
                Encoding.UTF8.GetBytes($"{header}\nIMP-0000000001,Name,1,10.00,true,Desc\n\"\"\n")
            }
        };
    }

    [Theory]
    [MemberData(nameof(FileLevelFailures))]
    public async Task AFileLevelFailureCreatesNoJobAndExitsOne(string expectedCode, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);

        var result = await Runner().ImportAsync(stream, BatchSize, CancellationToken.None);

        var rejected = Assert.IsType<ImportRunResult.FileRejected>(result);
        Assert.Equal(expectedCode, rejected.Error.Code);
        Assert.Equal(1, result.ExitCode);

        await using var db = sql.CreateContext(_database);
        Assert.Empty(await db.ImportJobs.AsNoTracking().ToListAsync());
        Assert.Empty(await db.ImportRows.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task AUtf8BomDoesNotChangeTheJobOrTheRecordNumbers()
    {
        var withoutBom = BuildFile(5);
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. withoutBom];

        using var plain = new MemoryStream(withoutBom);
        var first = Assert.IsType<ImportRunResult.Finished>(
            await Runner().ImportAsync(plain, BatchSize, CancellationToken.None)).Summary;

        using var bom = new MemoryStream(withBom);
        var second = Assert.IsType<ImportRunResult.Finished>(
            await Runner().ImportAsync(bom, BatchSize, CancellationToken.None)).Summary;

        // Different bytes, so a different job — but the same logical records, so the second run
        // finds every product already present and skips it.
        Assert.NotEqual(first.JobId, second.JobId);
        Assert.Equal(5, first.Inserted);
        Assert.Equal(5, second.Skipped);
        Assert.Equal(0, second.Rejected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2_001)]
    public async Task AnOutOfRangeBatchSizeIsRejectedBeforeAnyWork(int batchSize)
    {
        using var stream = new MemoryStream(BuildFile(10));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Runner().ImportAsync(stream, batchSize, CancellationToken.None));

        await using var db = sql.CreateContext(_database);
        Assert.Empty(await db.ImportJobs.AsNoTracking().ToListAsync());
    }

    // ---- the report --------------------------------------------------------------------------

    [Fact]
    public async Task TheReportIsRegeneratedFromSqlAndEscapesSpreadsheetFormulas()
    {
        using var stream = new MemoryStream(BuildFile(30, invalidEvery: 10));
        await Runner().ImportAsync(stream, BatchSize, CancellationToken.None);

        var jobId = await SingleJobIdAsync();

        await using var db = sql.CreateContext(_database);
        var report = await new ImportReportWriter(db).BuildAsync(jobId, CancellationToken.None);

        Assert.Equal(3, report.Rejected);
        Assert.Equal([10, 20, 30], report.RejectedRows.Select(r => r.RecordNumber));
        Assert.All(report.RejectedRows, r => Assert.Equal(ImportRowErrorCodes.MissingName, r.ErrorCode));

        var csv = ImportReportWriter.BuildCsv(report);
        Assert.StartsWith("recordNumber,sku,errorCode,outcome\n", csv, StringComparison.Ordinal);
        Assert.Equal(4, csv.TrimEnd('\n').Split('\n').Length);
    }

    [Theory]
    [InlineData("=cmd|'/c calc'!A1", "'=cmd|'/c calc'!A1")]
    [InlineData("+1234", "'+1234")]
    [InlineData("-SUM(A1)", "'-SUM(A1)")]
    [InlineData("@import", "'@import")]
    [InlineData("IMP-0000000001", "IMP-0000000001")]
    public void FormulaLeadingCellsAreEscapedButOrdinaryOnesAreNot(string value, string expected)
    {
        Assert.Equal(expected, ImportReportWriter.EscapeCsvCell(value));
    }

    [Fact]
    public void ACellContainingASeparatorIsQuoted()
    {
        Assert.Equal("\"a,b\"", ImportReportWriter.EscapeCsvCell("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", ImportReportWriter.EscapeCsvCell("say \"hi\""));
    }

    [Fact]
    public async Task TheReportSurvivesLosingTheOriginalFile()
    {
        using (var stream = new MemoryStream(BuildFile(20, invalidEvery: 5)))
        {
            await Runner().ImportAsync(stream, BatchSize, CancellationToken.None);
        }

        var jobId = await SingleJobIdAsync();
        var output = Path.Combine(Path.GetTempPath(), "dpl-report-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            // Only SQL is read here; the byte array above is already gone.
            await using var db = sql.CreateContext(_database);
            var writer = new ImportReportWriter(db);
            var files = await writer.WriteAsync(
                await writer.BuildAsync(jobId, CancellationToken.None), output, CancellationToken.None);

            Assert.Equal(["report.json", "rejected-rows.csv"], files);
            Assert.Contains("\"rejected\": 4", await File.ReadAllTextAsync(Path.Combine(output, "report.json")));
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    // ---- the fault injector is not reachable from a normal run --------------------------------

    [Fact]
    public void FaultInjectionIsRefusedOutsideTheTestingEnvironment()
    {
        var development = new StubEnvironment("Development");

        Assert.Throws<InvalidOperationException>(
            () => ImportFaultInjector.ForExperiment(development, failBeforeCommitOnBatch: 1, null));

        Assert.False(ImportFaultInjector.Disabled.IsArmed);
        Assert.True(ImportFaultInjector
            .ForExperiment(new StubEnvironment(LabEnvironment.Testing), 1, null).IsArmed);
    }

    private sealed class StubEnvironment(string environmentName) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Lab.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
