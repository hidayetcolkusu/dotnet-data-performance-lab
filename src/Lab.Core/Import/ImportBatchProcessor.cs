using System.Data;
using DataPerformanceLab.Catalog;
using DataPerformanceLab.Data;
using Microsoft.EntityFrameworkCore;

namespace DataPerformanceLab.Import;

public sealed record ImportSummary(
    Guid JobId,
    ImportJobStatus Status,
    int TotalRecords,
    int Inserted,
    int Skipped,
    int Rejected,
    int LastProcessedRecord,
    string? LastErrorCode)
{
    /// <summary>The three outcome counters must account for every staged record.</summary>
    public bool CountsAreConsistent => Inserted + Skipped + Rejected == LastProcessedRecord;

    public bool IsFinished => Status is ImportJobStatus.Completed or ImportJobStatus.CompletedWithErrors;
}

/// <summary>
/// Processes one batch of staged records per call. Product writes, row outcomes, job counters
/// and the checkpoint all commit in the <b>same</b> transaction, which is what makes
/// resume-after-crash exact rather than approximate.
/// </summary>
public sealed class ImportBatchProcessor(LabDbContext db, ImportFaultInjector faults)
{
    public const int DefaultBatchSize = 500;

    public const int MinBatchSize = 1;

    public const int MaxBatchSize = 2_000;

    public static int ValidateBatchSize(int batchSize) =>
        batchSize is >= MinBatchSize and <= MaxBatchSize
            ? batchSize
            : throw new ArgumentOutOfRangeException(
                nameof(batchSize), batchSize, $"Batch size must be between {MinBatchSize} and {MaxBatchSize}.");

    /// <summary>
    /// Processes the next batch. Returns true while work remains. A DB failure propagates and
    /// rolls the batch back; it is never converted into row rejections.
    /// </summary>
    public async Task<bool> ProcessNextBatchAsync(Guid jobId, int batchSize, int batchNumber, CancellationToken ct)
    {
        // SERIALIZABLE: the decision "this SKU does not exist yet" has to still hold at commit
        // time. The unique index on Products.Sku remains the last line of defence.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var committed = false;
        bool more;

        try
        {
            // The checkpoint is always re-read from SQL inside the transaction; an in-memory
            // checkpoint could be stale after a lost commit reply.
            var job = await db.ImportJobs.SingleAsync(j => j.Id == jobId, ct);

            var rows = await db.ImportRows
                .Where(r => r.JobId == jobId && r.RecordNumber > job.LastProcessedRecord)
                .OrderBy(r => r.RecordNumber)
                .Take(batchSize)
                .ToListAsync(ct);

            if (rows.Count == 0)
            {
                // Nothing left to process — including the zero-record file, which finishes
                // here with every counter still at zero.
                Finish(job);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                committed = true;
                db.ChangeTracker.Clear();
                return false;
            }

            await ApplyRowsAsync(job, rows, ct);

            job.Status = ImportJobStatus.Running;
            job.LastProcessedRecord = rows[^1].RecordNumber;
            if (job.LastProcessedRecord >= job.TotalRecords)
            {
                Finish(job);
            }

            await db.SaveChangesAsync(ct);

            faults.ThrowIfFailingBeforeCommit(batchNumber);

            await transaction.CommitAsync(ct);
            committed = true;
            db.ChangeTracker.Clear();

            more = !IsFinished(job);
        }
        catch
        {
            // Only an uncommitted batch can be rolled back; a committed one is already durable.
            if (!committed)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            db.ChangeTracker.Clear();
            throw;
        }

        // Raised outside the transaction scope on purpose: this simulates losing the commit
        // reply, where the work is durable but the caller never learns that it is.
        faults.ThrowIfFailingAfterCommit(batchNumber);

        return more;
    }

    private async Task ApplyRowsAsync(ImportJob job, List<ImportRow> rows, CancellationToken ct)
    {
        var candidateSkus = rows
            .Where(r => r.ValidationErrorCode is null)
            .Select(r => r.Sku!)
            .ToList();

        var existing = candidateSkus.Count == 0
            ? []
            : await db.Products
                .Where(p => candidateSkus.Contains(p.Sku))
                .ToDictionaryAsync(p => p.Sku, StringComparer.Ordinal, ct);

        foreach (var row in rows)
        {
            if (row.ValidationErrorCode is not null)
            {
                row.Outcome = ImportRowOutcome.Rejected;
                job.RejectedCount++;
                continue;
            }

            if (!existing.TryGetValue(row.Sku!, out var product))
            {
                // Insert-only: a SKU that is not in Products yet becomes a new product.
                db.Products.Add(new Product
                {
                    Sku = row.Sku!,
                    Name = row.Name!,
                    CategoryId = row.CategoryId!.Value,
                    UnitPrice = row.UnitPrice!.Value,
                    IsActive = row.IsActive!.Value,
                    Description = row.Description ?? string.Empty,
                    CreatedAtUtc = DateTime.UtcNow
                });

                row.Outcome = ImportRowOutcome.Inserted;
                job.InsertedCount++;
                continue;
            }

            if (MatchesImportFields(product, row))
            {
                // Same SKU, same importable values: nothing to do, and not an error.
                row.Outcome = ImportRowOutcome.SkippedExisting;
                job.SkippedCount++;
                continue;
            }

            // Import never updates an existing product; a differing row is a conflict.
            row.Outcome = ImportRowOutcome.Rejected;
            row.ValidationErrorCode = ImportRowErrorCodes.ExistingSkuConflict;
            job.RejectedCount++;
        }
    }

    /// <summary>
    /// Compares only the fields the CSV carries. CreatedAtUtc and Version are physical values
    /// the file cannot express, so they never make a row a conflict.
    /// </summary>
    private static bool MatchesImportFields(Product product, ImportRow row) =>
        string.Equals(product.Name, row.Name, StringComparison.Ordinal)
        && product.CategoryId == row.CategoryId
        && product.UnitPrice == row.UnitPrice
        && product.IsActive == row.IsActive
        && string.Equals(product.Description, row.Description ?? string.Empty, StringComparison.Ordinal);

    private static void Finish(ImportJob job)
    {
        job.Status = job.RejectedCount > 0
            ? ImportJobStatus.CompletedWithErrors
            : ImportJobStatus.Completed;
        job.CompletedAtUtc = DateTime.UtcNow;
    }

    private static bool IsFinished(ImportJob job) =>
        job.Status is ImportJobStatus.Completed or ImportJobStatus.CompletedWithErrors;

    public static ImportSummary ToSummary(ImportJob job) => new(
        job.Id,
        job.Status,
        job.TotalRecords,
        job.InsertedCount,
        job.SkippedCount,
        job.RejectedCount,
        job.LastProcessedRecord,
        job.LastErrorCode);
}
