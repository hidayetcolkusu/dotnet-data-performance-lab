using System.Text;
using DataPerformanceLab.Import;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DataPerformanceLab.IntegrationTests;

/// <summary>
/// Proves the staging contract against real SQL Server: one job per file, all rows written in
/// a single transaction, and one importer at a time.
/// </summary>
[Collection("sql")]
public sealed class ImportStagingTests(SqlServerFixture sql) : IAsyncLifetime
{
    private string _database = string.Empty;

    public async Task InitializeAsync() => _database = await sql.CreateAndMigrateTestDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static byte[] BuildFile(params string[] records)
    {
        var builder = new StringBuilder(string.Join(',', CsvImportParser.ExpectedHeader)).Append('\n');
        foreach (var record in records)
        {
            builder.Append(record).Append('\n');
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static async Task<ParsedImportFile> ParseAsync(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var result = await new CsvImportParser().ParseAsync(stream, CancellationToken.None);
        return Assert.IsType<ImportParseResult.Success>(result).File;
    }

    private static byte[] ThreeRowFile() => BuildFile(
        "IMP-0000000001,First,1,10.00,true,Accepted.",
        "IMP-0000000002,,2,20.00,true,Name is empty so this row is rejected.",
        "IMP-0000000003,Third,3,30.00,false,Accepted.");

    [Fact]
    public async Task StagingWritesTheJobAndEveryRowIncludingRejectedOnes()
    {
        var file = await ParseAsync(ThreeRowFile());

        await using var db = sql.CreateContext(_database);
        var result = await new ImportStager(db).StageAsync(file, CancellationToken.None);

        Assert.Equal(StagingOutcome.Created, result.Outcome);
        Assert.Equal(3, result.TotalRecords);
        Assert.Equal(ImportJobStatus.Ready, result.Status);

        await using var verify = sql.CreateContext(_database);
        var job = await verify.ImportJobs.AsNoTracking().SingleAsync(j => j.Id == result.JobId);
        Assert.Equal(file.FileHash, job.FileHash);
        Assert.Equal(CsvImportParser.ParserVersion, job.ParserVersion);
        Assert.Equal(0, job.LastProcessedRecord);

        var rows = await verify.ImportRows.AsNoTracking()
            .Where(r => r.JobId == result.JobId)
            .OrderBy(r => r.RecordNumber)
            .ToListAsync();

        Assert.Equal([1, 2, 3], rows.Select(r => r.RecordNumber));
        Assert.Equal(
            [null, ImportRowErrorCodes.MissingName, null],
            rows.Select(r => r.ValidationErrorCode));
        Assert.All(rows, r => Assert.Null(r.Outcome));

        // A rejected row still stages, with its converted fields left null.
        Assert.Null(rows[1].CategoryId);
        Assert.Null(rows[1].UnitPrice);
    }

    [Fact]
    public async Task AFailureBeforeCommitLeavesNoJobAndNoRows()
    {
        var file = await ParseAsync(ThreeRowFile());

        await using (var db = sql.CreateContext(_database))
        {
            var stager = new ImportStager(db)
            {
                BeforeCommitAsync = () => throw new InvalidOperationException("injected staging failure")
            };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => stager.StageAsync(file, CancellationToken.None));
            Assert.Equal("injected staging failure", ex.Message);
        }

        // The rows were written inside the transaction before the failure; the rollback must
        // have removed every one of them along with the job.
        await using var verify = sql.CreateContext(_database);
        Assert.Empty(await verify.ImportJobs.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.ImportRows.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task StagingTheSameFileTwiceReusesTheSameJob()
    {
        var bytes = ThreeRowFile();

        await using var db = sql.CreateContext(_database);
        var first = await new ImportStager(db).StageAsync(await ParseAsync(bytes), CancellationToken.None);
        var second = await new ImportStager(db).StageAsync(await ParseAsync(bytes), CancellationToken.None);

        Assert.Equal(StagingOutcome.Created, first.Outcome);
        Assert.Equal(StagingOutcome.AlreadyStaged, second.Outcome);
        Assert.Equal(first.JobId, second.JobId);

        await using var verify = sql.CreateContext(_database);
        Assert.Single(await verify.ImportJobs.AsNoTracking().ToListAsync());
        Assert.Equal(3, await verify.ImportRows.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task AFileDifferingByASingleByteBecomesADifferentJob()
    {
        await using var db = sql.CreateContext(_database);

        var first = await new ImportStager(db).StageAsync(
            await ParseAsync(BuildFile("IMP-0000000001,First,1,10.00,true,Accepted.")),
            CancellationToken.None);
        var second = await new ImportStager(db).StageAsync(
            await ParseAsync(BuildFile("IMP-0000000001,First,1,10.00,true,Accepted!")),
            CancellationToken.None);

        Assert.NotEqual(first.JobId, second.JobId);
        Assert.Equal(StagingOutcome.Created, second.Outcome);
    }

    [Fact]
    public async Task TheDuplicateSkuDecisionIsStagedForTheWholeFile()
    {
        var file = await ParseAsync(BuildFile(
            "IMP-0000000001,First Wins,1,10.00,true,Claims the SKU.",
            "IMP-0000000002,Other,2,20.00,true,Unrelated.",
            "IMP-0000000001,Second Loses,3,30.00,false,Rejected."));

        await using var db = sql.CreateContext(_database);
        var result = await new ImportStager(db).StageAsync(file, CancellationToken.None);

        await using var verify = sql.CreateContext(_database);
        var rows = await verify.ImportRows.AsNoTracking()
            .Where(r => r.JobId == result.JobId)
            .OrderBy(r => r.RecordNumber)
            .ToListAsync();

        Assert.Equal(
            [null, null, ImportRowErrorCodes.DuplicateSkuInFile],
            rows.Select(r => r.ValidationErrorCode));
    }

    // ---- the import lock --------------------------------------------------------------------

    [Fact]
    public async Task ASecondImporterCannotTakeTheLockWhileTheFirstHoldsIt()
    {
        var connectionString = sql.ConnectionStringFor(_database);

        await using var first = await ImportLock.TryAcquireAsync(
            connectionString, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.NotNull(first);

        // A short timeout keeps the test quick; the production default is 5 seconds.
        var second = await ImportLock.TryAcquireAsync(
            connectionString, TimeSpan.FromMilliseconds(500), CancellationToken.None);

        Assert.Null(second);
    }

    [Fact]
    public async Task TheLockIsReleasedWhenTheHoldersConnectionCloses()
    {
        var connectionString = sql.ConnectionStringFor(_database);

        var first = await ImportLock.TryAcquireAsync(
            connectionString, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.NotNull(first);
        await first.DisposeAsync();

        await using var second = await ImportLock.TryAcquireAsync(
            connectionString, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.NotNull(second);
    }
}
