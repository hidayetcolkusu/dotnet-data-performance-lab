using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DataPerformanceLab.Data;
using Microsoft.EntityFrameworkCore;

namespace DataPerformanceLab.Import;

public sealed record ImportReportRow(int RecordNumber, string? Sku, string? ErrorCode, string? Outcome);

public sealed record ImportReport(
    Guid JobId,
    string FileHash,
    int ParserVersion,
    string Status,
    int TotalRecords,
    int Inserted,
    int Skipped,
    int Rejected,
    int LastProcessedRecord,
    string? LastErrorCode,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    IReadOnlyList<ImportReportRow> RejectedRows);

/// <summary>
/// Regenerates the error report from the durable SQL outcomes. The original CSV is not needed
/// and is never read here, so a report stays reproducible long after the file is gone.
///
/// Only the record number, the SKU and the error code are published — never the raw field
/// values, even though the fixtures are synthetic.
/// </summary>
public sealed class ImportReportWriter(LabDbContext db)
{
    /// <summary>Characters that force a CSV cell to be quoted.</summary>
    private static readonly SearchValues<char> CharactersNeedingQuotes =
        SearchValues.Create([',', '"', '\n', '\r']);

    /// <summary>Leading characters a spreadsheet would treat as the start of a formula.</summary>
    private static readonly SearchValues<char> FormulaLeadingCharacters =
        SearchValues.Create(['=', '+', '-', '@', '\t', '\r']);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<ImportReport> BuildAsync(Guid jobId, CancellationToken ct)
    {
        var job = await db.ImportJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId, ct)
            ?? throw new InvalidOperationException($"Import job '{jobId}' does not exist.");

        var rejected = await db.ImportRows.AsNoTracking()
            .Where(r => r.JobId == jobId && r.Outcome == ImportRowOutcome.Rejected)
            .OrderBy(r => r.RecordNumber)
            .Select(r => new ImportReportRow(
                r.RecordNumber, r.Sku, r.ValidationErrorCode, r.Outcome!.Value.ToString()))
            .ToListAsync(ct);

        return new ImportReport(
            job.Id,
            job.FileHash,
            job.ParserVersion,
            job.Status.ToString(),
            job.TotalRecords,
            job.InsertedCount,
            job.SkippedCount,
            job.RejectedCount,
            job.LastProcessedRecord,
            job.LastErrorCode,
            job.CreatedAtUtc,
            job.CompletedAtUtc,
            rejected);
    }

    /// <summary>Writes report.json and rejected-rows.csv; returns the file names written.</summary>
    public async Task<IReadOnlyList<string>> WriteAsync(
        ImportReport report, string outputDirectory, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDirectory);

        var jsonPath = Path.Combine(outputDirectory, "report.json");
        await using (var stream = File.Create(jsonPath))
        {
            await JsonSerializer.SerializeAsync(stream, report, JsonOptions, ct);
        }

        var csvPath = Path.Combine(outputDirectory, "rejected-rows.csv");
        await File.WriteAllTextAsync(csvPath, BuildCsv(report), new UTF8Encoding(false), ct);

        return [Path.GetFileName(jsonPath), Path.GetFileName(csvPath)];
    }

    public static string BuildCsv(ImportReport report)
    {
        var builder = new StringBuilder("recordNumber,sku,errorCode,outcome\n");
        foreach (var row in report.RejectedRows)
        {
            builder
                .Append(row.RecordNumber.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(EscapeCsvCell(row.Sku)).Append(',')
                .Append(EscapeCsvCell(row.ErrorCode)).Append(',')
                .Append(EscapeCsvCell(row.Outcome)).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Quotes for CSV and neutralizes spreadsheet formula injection: a cell a spreadsheet would
    /// evaluate is prefixed with a single quote so it opens as text instead of running.
    /// </summary>
    public static string EscapeCsvCell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var cell = FormulaLeadingCharacters.Contains(value[0]) ? "'" + value : value;

        return cell.AsSpan().ContainsAny(CharactersNeedingQuotes)
            ? '"' + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + '"'
            : cell;
    }
}
