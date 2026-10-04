using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace DataPerformanceLab.Import;

/// <summary>
/// File-level failure codes. A file-level failure means no job is created at all — it is
/// categorically different from a row rejection, which still produces a job.
/// </summary>
public static class ImportFileErrorCodes
{
    public const string FileTooLarge = "FileTooLarge";
    public const string TooManyRecords = "TooManyRecords";
    public const string RecordTooLarge = "RecordTooLarge";
    public const string InvalidUtf8 = "InvalidUtf8";
    public const string UnsupportedEncoding = "UnsupportedEncoding";
    public const string InvalidHeader = "InvalidHeader";
    public const string MalformedCsv = "MalformedCsv";
    public const string ColumnCountMismatch = "ColumnCountMismatch";
    public const string EmptyFile = "EmptyFile";
}

public sealed record ImportFileError(string Code, string Message);

/// <summary>Raised by the CSV reader when quoting or record structure is broken.</summary>
public sealed class MalformedCsvException(string message) : Exception(message);

public sealed record ParsedImportFile(string FileHash, int ParserVersion, IReadOnlyList<ImportRow> Rows)
{
    public int TotalRecords => Rows.Count;
}

/// <summary>
/// The outcome of parsing a file: either a staged-ready file or a file-level rejection.
/// Row-level problems live inside <see cref="ParsedImportFile"/>, never here.
/// </summary>
public abstract record ImportParseResult
{
    public sealed record Success(ParsedImportFile File) : ImportParseResult;

    public sealed record Failure(ImportFileError Error) : ImportParseResult;
}

/// <summary>
/// Reads a bounded UTF-8 CSV file into validated staging rows. Quoting, embedded
/// commas and multi-line fields are handled by CsvHelper; the file is never split on commas.
/// </summary>
public sealed class CsvImportParser
{
    public const int ParserVersion = 1;

    public const int MaxFileBytes = 10 * 1024 * 1024;

    public const int MaxRecords = 50_000;

    public const int MaxRecordBytes = 64 * 1024;

    public static readonly string[] ExpectedHeader =
        ["sku", "name", "categoryId", "unitPrice", "isActive", "description"];

    public async Task<ImportParseResult> ParseAsync(Stream stream, CancellationToken ct)
    {
        var read = await ReadBoundedAsync(stream, ct);
        if (read.Error is not null)
        {
            return new ImportParseResult.Failure(read.Error);
        }

        var bytes = read.Bytes!;
        if (bytes.Length == 0)
        {
            return new ImportParseResult.Failure(
                new ImportFileError(ImportFileErrorCodes.EmptyFile, "The file is empty."));
        }

        // Hash the raw bytes, so a file that differs by a single byte is a different job even
        // when it parses to the same logical records.
        var fileHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        if (DetectNonUtf8Bom(bytes) is { } bomEncoding)
        {
            return new ImportParseResult.Failure(new ImportFileError(
                ImportFileErrorCodes.UnsupportedEncoding,
                $"The file begins with a {bomEncoding} byte order mark; the import contract is "
                + "UTF-8 (with or without a BOM)."));
        }

        try
        {
            // A UTF-8 BOM is accepted and consumed here rather than by StreamReader's encoding
            // sniffer, so the decoder stays pinned to UTF-8 and the header still compares equal.
            var rows = ParseRows(StripUtf8Bom(bytes), out var error);
            if (error is not null)
            {
                return new ImportParseResult.Failure(error);
            }

            ImportValidator.MarkDuplicateSkusInFile(rows);
            return new ImportParseResult.Success(new ParsedImportFile(fileHash, ParserVersion, rows));
        }
        catch (DecoderFallbackException)
        {
            return new ImportParseResult.Failure(new ImportFileError(
                ImportFileErrorCodes.InvalidUtf8, "The file is not valid UTF-8 text."));
        }
        catch (MalformedCsvException ex)
        {
            return new ImportParseResult.Failure(new ImportFileError(
                ImportFileErrorCodes.MalformedCsv, $"The CSV structure is malformed: {ex.Message}"));
        }
    }

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Names the encoding of a non-UTF-8 BOM, or null when the file carries no such mark. The
    /// UTF-32 LE mark is checked before the UTF-16 LE mark, because the former starts with the
    /// latter's two bytes and would otherwise be misreported.
    /// </summary>
    private static string? DetectNonUtf8Bom(byte[] bytes)
    {
        if (bytes.Length >= 4)
        {
            if (bytes[0] is 0xFF && bytes[1] is 0xFE && bytes[2] is 0x00 && bytes[3] is 0x00)
            {
                return "UTF-32 LE";
            }

            if (bytes[0] is 0x00 && bytes[1] is 0x00 && bytes[2] is 0xFE && bytes[3] is 0xFF)
            {
                return "UTF-32 BE";
            }
        }

        if (bytes.Length >= 2)
        {
            if (bytes[0] is 0xFF && bytes[1] is 0xFE)
            {
                return "UTF-16 LE";
            }

            if (bytes[0] is 0xFE && bytes[1] is 0xFF)
            {
                return "UTF-16 BE";
            }
        }

        return null;
    }

    private static byte[] StripUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes.AsSpan(0, 3).SequenceEqual(Utf8Bom)
            ? bytes[3..]
            : bytes;

    private static List<ImportRow> ParseRows(byte[] bytes, out ImportFileError? error)
    {
        error = null;
        var rows = new List<ImportRow>();

        // throwOnInvalidBytes: a corrupt byte sequence must fail the file rather than silently
        // become U+FFFD replacement characters inside a product name.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        using var memory = new MemoryStream(bytes, writable: false);
        // detectEncodingFromByteOrderMarks stays off: the contract is UTF-8, and letting the
        // reader switch decoders on a BOM is exactly how a UTF-16 file would be silently accepted.
        using var reader = new StreamReader(memory, encoding, detectEncodingFromByteOrderMarks: false);
        using var parser = new CsvParser(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            DetectColumnCountChanges = false,
            IgnoreBlankLines = false,
            BadDataFound = args => throw new MalformedCsvException(
                $"Malformed quoting near physical row {args.Context.Parser?.RawRow}.")
        });

        if (!parser.Read())
        {
            error = new ImportFileError(ImportFileErrorCodes.EmptyFile, "The file has no header row.");
            return rows;
        }

        var header = parser.Record ?? [];
        if (!header.SequenceEqual(ExpectedHeader, StringComparer.Ordinal))
        {
            error = new ImportFileError(
                ImportFileErrorCodes.InvalidHeader,
                $"Header must be exactly '{string.Join(',', ExpectedHeader)}' but was "
                + $"'{string.Join(',', header)}'.");
            return rows;
        }

        var recordNumber = 0;

        // A physically blank line is only excusable as the file's single trailing newline, so the
        // decision is deferred: blank lines are counted, and judged once we know whether more data
        // follows. A blank line between two records is a one-column record and must fail the file.
        var pendingBlankLines = 0;

        while (parser.Read())
        {
            var record = parser.Record!;

            if (IsBlankPhysicalLine(parser.RawRecord))
            {
                pendingBlankLines++;
                continue;
            }

            if (pendingBlankLines > 0)
            {
                error = new ImportFileError(
                    ImportFileErrorCodes.ColumnCountMismatch,
                    $"Record {recordNumber + 1} is preceded by {pendingBlankLines} blank line(s); "
                    + $"a blank record has 1 column but {ExpectedHeader.Length} are expected.");
                return rows;
            }

            recordNumber++;

            if (recordNumber > MaxRecords)
            {
                error = new ImportFileError(
                    ImportFileErrorCodes.TooManyRecords,
                    $"The file holds more than {MaxRecords} data records.");
                return rows;
            }

            var recordBytes = Encoding.UTF8.GetByteCount(parser.RawRecord);
            if (recordBytes > MaxRecordBytes)
            {
                error = new ImportFileError(
                    ImportFileErrorCodes.RecordTooLarge,
                    $"Record {recordNumber} is {recordBytes} bytes; the limit is {MaxRecordBytes}.");
                return rows;
            }

            if (record.Length != ExpectedHeader.Length)
            {
                error = new ImportFileError(
                    ImportFileErrorCodes.ColumnCountMismatch,
                    $"Record {recordNumber} has {record.Length} columns; expected {ExpectedHeader.Length}.");
                return rows;
            }

            rows.Add(ImportValidator.Validate(
                recordNumber,
                new ImportFieldValues(record[0], record[1], record[2], record[3], record[4], record[5])));
        }

        // One blank line at the end is the file's terminating newline. More than one is data.
        if (pendingBlankLines > 1)
        {
            error = new ImportFileError(
                ImportFileErrorCodes.ColumnCountMismatch,
                $"The file ends with {pendingBlankLines} blank lines; only a single trailing "
                + "newline is permitted.");
        }

        return rows;
    }

    /// <summary>
    /// True when the raw record holds nothing but its line ending. A line containing <c>""</c> is
    /// not blank — it is a one-column record, and must be reported as a column count mismatch.
    /// </summary>
    private static bool IsBlankPhysicalLine(string rawRecord) =>
        rawRecord.AsSpan().TrimEnd("\r\n").IsEmpty;

    /// <summary>
    /// Reads at most <see cref="MaxFileBytes"/> + 1 bytes. The limit is enforced on the bytes
    /// actually read rather than on <c>FileInfo.Length</c>, so a stream that misreports its
    /// size — or grows while being read — cannot get past it.
    /// </summary>
    private static async Task<(byte[]? Bytes, ImportFileError? Error)> ReadBoundedAsync(
        Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxFileBytes)
            {
                return (null, new ImportFileError(
                    ImportFileErrorCodes.FileTooLarge,
                    $"The file exceeds the {MaxFileBytes} byte limit."));
            }
        }

        return (buffer.ToArray(), null);
    }
}
