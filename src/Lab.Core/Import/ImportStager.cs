using DataPerformanceLab.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DataPerformanceLab.Import;

/// <summary>How an existing job was matched, so the CLI can say what it did.</summary>
public enum StagingOutcome
{
    /// <summary>A new job and all of its rows were written.</summary>
    Created,

    /// <summary>A job for this file hash and parser version already existed.</summary>
    AlreadyStaged
}

public sealed record StagingResult(Guid JobId, StagingOutcome Outcome, ImportJobStatus Status, int TotalRecords);

/// <summary>
/// Writes a parsed file into staging. The job row and every one of its record rows are
/// committed in a single transaction, so a crash mid-insert can never leave a
/// partially staged job that resume would then continue from.
/// </summary>
public sealed class ImportStager(LabDbContext db)
{
    /// <summary>SQL Server error numbers for a unique index/constraint violation.</summary>
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;

    /// <summary>
    /// Test seam for the atomicity proof: invoked after the rows are written but before the
    /// transaction commits. Null in every normal run.
    /// </summary>
    public Func<Task>? BeforeCommitAsync { get; set; }

    public async Task<StagingResult> StageAsync(ParsedImportFile file, CancellationToken ct)
    {
        // Re-importing the same bytes must not create a second job: return the existing one so
        // the caller can resume or report it.
        var existing = await FindExistingAsync(file, ct);
        if (existing is not null)
        {
            return existing;
        }

        var job = new ImportJob
        {
            Id = Guid.NewGuid(),
            FileHash = file.FileHash,
            ParserVersion = file.ParserVersion,
            Status = ImportJobStatus.Ready,
            TotalRecords = file.TotalRecords,
            LastProcessedRecord = 0,
            CreatedAtUtc = DateTime.UtcNow
        };

        foreach (var row in file.Rows)
        {
            row.JobId = job.Id;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            db.ImportJobs.Add(job);
            db.ImportRows.AddRange(file.Rows);
            await db.SaveChangesAsync(ct);

            if (BeforeCommitAsync is not null)
            {
                await BeforeCommitAsync();
            }

            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Another importer staged the same file between the lookup and the insert.
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();

            return await FindExistingAsync(file, ct)
                ?? throw new InvalidOperationException(
                    "Staging hit a unique violation but no existing job for this file hash was found.", ex);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }

        db.ChangeTracker.Clear();
        return new StagingResult(job.Id, StagingOutcome.Created, job.Status, job.TotalRecords);
    }

    private async Task<StagingResult?> FindExistingAsync(ParsedImportFile file, CancellationToken ct)
    {
        var job = await db.ImportJobs.AsNoTracking()
            .SingleOrDefaultAsync(
                j => j.FileHash == file.FileHash && j.ParserVersion == file.ParserVersion, ct);

        return job is null
            ? null
            : new StagingResult(job.Id, StagingOutcome.AlreadyStaged, job.Status, job.TotalRecords);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException sql
        && sql.Number is UniqueIndexViolation or UniqueConstraintViolation;
}
