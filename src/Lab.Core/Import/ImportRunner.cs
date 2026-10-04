using DataPerformanceLab.Data;
using Microsoft.EntityFrameworkCore;

namespace DataPerformanceLab.Import;

/// <summary>
/// The outcome of a CLI-level import or resume. Each case maps to one documented exit code,
/// so the shell contract is decided here rather than guessed at by the command.
/// </summary>
public abstract record ImportRunResult
{
    /// <summary>Another importer holds the session lock. Exit 3.</summary>
    public sealed record Busy : ImportRunResult;

    /// <summary>The file was rejected as a whole, so no job exists. Exit 1.</summary>
    public sealed record FileRejected(ImportFileError Error) : ImportRunResult;

    /// <summary>The job reached a final state. Exit 0, or 2 when rows were rejected.</summary>
    public sealed record Finished(ImportSummary Summary, StagingOutcome Staging) : ImportRunResult;

    public int ExitCode => this switch
    {
        Busy => 3,
        FileRejected => 1,
        Finished finished => finished.Summary.Rejected > 0 ? 2 : 0,
        _ => 1
    };
}

/// <summary>
/// Thrown when a batch aborted and left the job resumable. It carries the job id because that is
/// the one thing the operator needs next: without it, a crashed import leaves the caller knowing
/// only that something failed, with no handle on the work already committed.
/// </summary>
public sealed class ImportInterruptedException(Guid jobId, Exception innerException)
    : Exception(
        $"Import job {jobId} was interrupted ({Describe(innerException)}). "
        + $"The committed batches are intact; resume with --job-id {jobId}.",
        innerException)
{
    public Guid JobId { get; } = jobId;

    /// <summary>
    /// Names the innermost cause, not just the wrapper. EF reports a constraint violation as a
    /// generic "An error occurred while saving the entity changes"; the actionable sentence — which
    /// constraint, on which table — is two levels down, and an operator should not have to attach a
    /// debugger to read it. This is the console message only: the code stored in SQL stays the bare
    /// exception type name, so no server or file detail reaches the database.
    /// </summary>
    private static string Describe(Exception exception)
    {
        var root = exception;
        while (root.InnerException is { } inner)
        {
            root = inner;
        }

        return ReferenceEquals(root, exception)
            ? $"{exception.GetType().Name}: {exception.Message}"
            : $"{exception.GetType().Name}: {exception.Message} -> {root.GetType().Name}: {root.Message}";
    }
}

/// <summary>
/// Drives a whole import: take the session lock, stage the file, then process batches until
/// the job reaches a final state. Staging and processing share one lock lifetime.
/// </summary>
public sealed class ImportRunner(
    ILabDbContextFactory dbFactory,
    string databaseName,
    ImportFaultInjector? faults = null)
{
    private readonly ImportFaultInjector _faults = faults ?? ImportFaultInjector.Disabled;

    public async Task<ImportRunResult> ImportAsync(Stream fileStream, int batchSize, CancellationToken ct)
    {
        ImportBatchProcessor.ValidateBatchSize(batchSize);

        var parsed = await new CsvImportParser().ParseAsync(fileStream, ct);
        if (parsed is ImportParseResult.Failure failure)
        {
            // A file-level failure creates no job at all, so there is nothing to resume.
            return new ImportRunResult.FileRejected(failure.Error);
        }

        var file = ((ImportParseResult.Success)parsed).File;

        await using var importLock = await ImportLock.TryAcquireAsync(
            dbFactory.GetConnectionString(databaseName), ImportLock.DefaultTimeout, ct);
        if (importLock is null)
        {
            return new ImportRunResult.Busy();
        }

        StagingResult staging;
        await using (var db = dbFactory.CreateForDatabase(databaseName))
        {
            staging = await new ImportStager(db).StageAsync(file, ct);
        }

        var summary = await ProcessLockedAsync(staging.JobId, batchSize, ct);
        return new ImportRunResult.Finished(summary, staging.Outcome);
    }

    public async Task<ImportRunResult> ResumeAsync(Guid jobId, int batchSize, CancellationToken ct)
    {
        ImportBatchProcessor.ValidateBatchSize(batchSize);

        await using var importLock = await ImportLock.TryAcquireAsync(
            dbFactory.GetConnectionString(databaseName), ImportLock.DefaultTimeout, ct);
        if (importLock is null)
        {
            return new ImportRunResult.Busy();
        }

        return new ImportRunResult.Finished(
            await ProcessLockedAsync(jobId, batchSize, ct), StagingOutcome.AlreadyStaged);
    }

    /// <summary>
    /// Runs batches until the job is final. The caller must already hold the import lock:
    /// job state is only judged after the lock is held, so a job left in Running by a killed
    /// process is resumed from its durable checkpoint rather than declared broken.
    /// </summary>
    private async Task<ImportSummary> ProcessLockedAsync(Guid jobId, int batchSize, CancellationToken ct)
    {
        var current = await ReadJobAsync(jobId, ct);
        if (current.IsFinished)
        {
            // Re-importing a finished file returns the recorded result instead of redoing work.
            return current;
        }

        var batchNumber = 0;
        while (true)
        {
            batchNumber++;

            // A fresh context per batch: the change tracker never carries state across a
            // transaction boundary.
            await using var db = dbFactory.CreateForDatabase(databaseName);
            var processor = new ImportBatchProcessor(db, _faults);

            bool more;
            try
            {
                more = await processor.ProcessNextBatchAsync(jobId, batchSize, batchNumber, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The batch rolled back. Record why, leave the checkpoint where it was, and
                // let the caller decide: there is no blind automatic retry.
                await RecordInterruptionAsync(jobId, ex, ct);
                throw new ImportInterruptedException(jobId, ex);
            }

            if (!more)
            {
                return await ReadJobAsync(jobId, ct);
            }
        }
    }

    private async Task<ImportSummary> ReadJobAsync(Guid jobId, CancellationToken ct)
    {
        await using var db = dbFactory.CreateForDatabase(databaseName);
        var job = await db.ImportJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId, ct)
            ?? throw new InvalidOperationException($"Import job '{jobId}' does not exist in '{databaseName}'.");

        return ImportBatchProcessor.ToSummary(job);
    }

    /// <summary>
    /// Marks the job Interrupted with a sanitized error code. Best effort: if SQL itself is
    /// what failed, the original exception still wins.
    /// </summary>
    private async Task RecordInterruptionAsync(Guid jobId, Exception ex, CancellationToken ct)
    {
        try
        {
            await using var db = dbFactory.CreateForDatabase(databaseName);
            var job = await db.ImportJobs.SingleOrDefaultAsync(j => j.Id == jobId, ct);
            if (job is null || ImportBatchProcessor.ToSummary(job).IsFinished)
            {
                return;
            }

            job.Status = ImportJobStatus.Interrupted;
            // Only the exception type is stored: messages can carry file content or server detail.
            job.LastErrorCode = ex.GetType().Name;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception recordFailure) when (recordFailure is not OperationCanceledException)
        {
            // Swallowed on purpose: the caller is already being handed the real failure.
        }
    }
}
