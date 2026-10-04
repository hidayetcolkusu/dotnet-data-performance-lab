namespace DataPerformanceLab.Import;

public enum ImportJobStatus
{
    Ready,
    Running,
    Completed,
    CompletedWithErrors,
    Interrupted
}

/// <summary>
/// What the processor did with a staged record. The reason a row was rejected lives in
/// <see cref="ImportRow.ValidationErrorCode"/>, so the outcome stays a small closed set and
/// Inserted + SkippedExisting + Rejected always sums to TotalRecords.
/// </summary>
public enum ImportRowOutcome
{
    Inserted,
    SkippedExisting,
    Rejected
}

public sealed class ImportJob
{
    public Guid Id { get; set; }

    public string FileHash { get; set; } = string.Empty;

    public int ParserVersion { get; set; }

    public ImportJobStatus Status { get; set; }

    public int TotalRecords { get; set; }

    public int LastProcessedRecord { get; set; }

    public int InsertedCount { get; set; }

    public int SkippedCount { get; set; }

    public int RejectedCount { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public string? LastErrorCode { get; set; }

    public List<ImportRow> Rows { get; set; } = [];
}

public sealed class ImportRow
{
    public Guid JobId { get; set; }

    public int RecordNumber { get; set; }

    public string? Sku { get; set; }

    public string? Name { get; set; }

    public int? CategoryId { get; set; }

    public decimal? UnitPrice { get; set; }

    public bool? IsActive { get; set; }

    public string? Description { get; set; }

    /// <summary>Raw CSV text for fields that failed conversion; null once the field parsed.</summary>
    public string? RawCategoryId { get; set; }

    public string? RawUnitPrice { get; set; }

    public string? RawIsActive { get; set; }

    public string? ValidationErrorCode { get; set; }

    public ImportRowOutcome? Outcome { get; set; }

    public ImportJob? Job { get; set; }
}
