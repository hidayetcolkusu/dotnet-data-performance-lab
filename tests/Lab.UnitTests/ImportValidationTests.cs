using System.Text;
using DataPerformanceLab.Import;
using Xunit;

namespace DataPerformanceLab.UnitTests;

public sealed class ImportValidationTests
{
    private static readonly string SamplesDirectory = LocateSamples();

    private static ImportFieldValues Valid(
        string sku = "IMP-0000000001",
        string name = "Valid Name",
        string categoryId = "1",
        string unitPrice = "10.00",
        string isActive = "true",
        string description = "A description.") =>
        new(sku, name, categoryId, unitPrice, isActive, description);

    // ---- field rules ------------------------------------------------------------

    [Fact]
    public void AValidRecordCarriesNoErrorAndNormalizedValues()
    {
        var row = ImportValidator.Validate(1, Valid(sku: "  imp-0000000001  "));

        Assert.Null(row.ValidationErrorCode);
        Assert.Equal("IMP-0000000001", row.Sku);
        Assert.Equal(1, row.CategoryId);
        Assert.Equal(10.00m, row.UnitPrice);
        Assert.True(row.IsActive);
        Assert.Null(row.RawUnitPrice);
    }

    [Theory]
    [InlineData("")]
    [InlineData("imp 0000000001")]
    [InlineData("IMP_0000000001")]
    [InlineData("IMP-000000000000000000000000000000001")]
    [InlineData("ÜRÜN-1")]
    public void InvalidSkusAreRejected(string sku)
    {
        Assert.Equal(ImportRowErrorCodes.InvalidSku, ImportValidator.Validate(1, Valid(sku: sku)).ValidationErrorCode);
    }

    [Fact]
    public void SkuIsNormalizedBeforeValidation()
    {
        Assert.Null(ImportValidator.Validate(1, Valid(sku: "\tabc-123 ")).ValidationErrorCode);
    }

    [Theory]
    [InlineData("", ImportRowErrorCodes.MissingName)]
    [InlineData("   ", ImportRowErrorCodes.MissingName)]
    public void EmptyNamesAreRejected(string name, string expected)
    {
        Assert.Equal(expected, ImportValidator.Validate(1, Valid(name: name)).ValidationErrorCode);
    }

    [Fact]
    public void NameAtTheLimitIsAcceptedAndOneCharacterOverIsRejected()
    {
        Assert.Null(ImportValidator.Validate(1, Valid(name: new string('a', 120))).ValidationErrorCode);
        Assert.Equal(
            ImportRowErrorCodes.NameTooLong,
            ImportValidator.Validate(1, Valid(name: new string('a', 121))).ValidationErrorCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("21")]
    [InlineData("-1")]
    [InlineData("1.0")]
    [InlineData("")]
    [InlineData(" 1")]
    [InlineData("+1")]
    public void InvalidCategoriesAreRejected(string categoryId)
    {
        Assert.Equal(
            ImportRowErrorCodes.InvalidCategoryId,
            ImportValidator.Validate(1, Valid(categoryId: categoryId)).ValidationErrorCode);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("20")]
    public void BoundaryCategoriesAreAccepted(string categoryId)
    {
        Assert.Null(ImportValidator.Validate(1, Valid(categoryId: categoryId)).ValidationErrorCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.00")]
    [InlineData("9999999999999999.99")]
    [InlineData("42")]
    public void ValidPricesAreAccepted(string unitPrice)
    {
        Assert.Null(ImportValidator.Validate(1, Valid(unitPrice: unitPrice)).ValidationErrorCode);
    }

    [Theory]
    [InlineData("1.005")]          // three decimals must not be rounded into acceptance
    [InlineData("-1.00")]
    [InlineData("10000000000000000.00")]
    [InlineData("1,00")]           // comma decimal separator is not invariant
    [InlineData("1e3")]
    [InlineData(" 1.00")]
    [InlineData("")]
    [InlineData("abc")]
    public void InvalidPricesAreRejected(string unitPrice)
    {
        Assert.Equal(
            ImportRowErrorCodes.InvalidUnitPrice,
            ImportValidator.Validate(1, Valid(unitPrice: unitPrice)).ValidationErrorCode);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void OnlyTheExactBooleanTokensAreAccepted(string isActive, bool expected)
    {
        var row = ImportValidator.Validate(1, Valid(isActive: isActive));

        Assert.Null(row.ValidationErrorCode);
        Assert.Equal(expected, row.IsActive);
    }

    [Theory]
    [InlineData("True")]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("")]
    public void OtherBooleanSpellingsAreRejectedRatherThanGuessed(string isActive)
    {
        Assert.Equal(
            ImportRowErrorCodes.InvalidIsActive,
            ImportValidator.Validate(1, Valid(isActive: isActive)).ValidationErrorCode);
    }

    [Fact]
    public void DescriptionAtTheLimitIsAcceptedAndOneCharacterOverIsRejected()
    {
        Assert.Null(ImportValidator.Validate(1, Valid(description: new string('d', 2000))).ValidationErrorCode);
        Assert.Equal(
            ImportRowErrorCodes.DescriptionTooLong,
            ImportValidator.Validate(1, Valid(description: new string('d', 2001))).ValidationErrorCode);
    }

    [Fact]
    public void ARejectedRowKeepsTheRawTextThatFailed()
    {
        var row = ImportValidator.Validate(7, Valid(categoryId: "99", unitPrice: "nope", isActive: "maybe"));

        Assert.Equal(ImportRowErrorCodes.InvalidCategoryId, row.ValidationErrorCode);
        Assert.Equal("99", row.RawCategoryId);
        Assert.Equal("nope", row.RawUnitPrice);
        Assert.Equal("maybe", row.RawIsActive);
        Assert.Null(row.CategoryId);
        Assert.Null(row.UnitPrice);
        Assert.Null(row.IsActive);
    }

    // ---- duplicate handling across the whole file ------------------------------------------

    [Fact]
    public void TheFirstValidRecordForASkuWins()
    {
        var rows = new[]
        {
            ImportValidator.Validate(1, Valid(sku: "IMP-1")),
            ImportValidator.Validate(2, Valid(sku: "IMP-2")),
            ImportValidator.Validate(3, Valid(sku: "IMP-1"))
        };

        ImportValidator.MarkDuplicateSkusInFile(rows);

        Assert.Null(rows[0].ValidationErrorCode);
        Assert.Null(rows[1].ValidationErrorCode);
        Assert.Equal(ImportRowErrorCodes.DuplicateSkuInFile, rows[2].ValidationErrorCode);
    }

    [Fact]
    public void AnInvalidRowDoesNotClaimTheSkuForLaterValidRows()
    {
        var rows = new[]
        {
            ImportValidator.Validate(1, Valid(sku: "IMP-1", unitPrice: "bad")),
            ImportValidator.Validate(2, Valid(sku: "IMP-1"))
        };

        ImportValidator.MarkDuplicateSkusInFile(rows);

        Assert.Equal(ImportRowErrorCodes.InvalidUnitPrice, rows[0].ValidationErrorCode);
        Assert.Null(rows[1].ValidationErrorCode);
    }

    [Fact]
    public void DuplicateDetectionIsCaseInsensitiveThroughNormalization()
    {
        var rows = new[]
        {
            ImportValidator.Validate(1, Valid(sku: "imp-1")),
            ImportValidator.Validate(2, Valid(sku: "IMP-1"))
        };

        ImportValidator.MarkDuplicateSkusInFile(rows);

        Assert.Equal(ImportRowErrorCodes.DuplicateSkuInFile, rows[1].ValidationErrorCode);
    }

    // ---- file-level parsing ----------------------------------------------------------------

    private static async Task<ImportParseResult> ParseAsync(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return await new CsvImportParser().ParseAsync(stream, CancellationToken.None);
    }

    private static Task<ImportParseResult> ParseAsync(string text) => ParseAsync(Encoding.UTF8.GetBytes(text));

    private static ParsedImportFile AssertSuccess(ImportParseResult result)
    {
        var success = Assert.IsType<ImportParseResult.Success>(result);
        return success.File;
    }

    private static string AssertFailureCode(ImportParseResult result) => AssertFailure(result).Code;

    private static ImportFileError AssertFailure(ImportParseResult result) =>
        Assert.IsType<ImportParseResult.Failure>(result).Error;

    [Fact]
    public async Task TheValidSampleParsesWithQuotingCommasAndMultilineFields()
    {
        var file = AssertSuccess(await ParseAsync(
            await File.ReadAllBytesAsync(Path.Combine(SamplesDirectory, "catalog-valid.csv"))));

        Assert.Equal(6, file.TotalRecords);
        Assert.All(file.Rows, r => Assert.Null(r.ValidationErrorCode));

        // The embedded comma stayed inside one field rather than splitting the record.
        Assert.Equal("Gadget, Compact", file.Rows[1].Name);

        // The multi-line description is one logical record, so numbering is not thrown off.
        Assert.Contains("\n", file.Rows[2].Description);
        Assert.Equal(3, file.Rows[2].RecordNumber);
        Assert.Equal("Quoted \"Inner\" Name Unit", file.Rows[5].Name);
    }

    [Fact]
    public async Task TheMixedSampleRejectsRowsWithoutRejectingTheFile()
    {
        var file = AssertSuccess(await ParseAsync(
            await File.ReadAllBytesAsync(Path.Combine(SamplesDirectory, "catalog-mixed.csv"))));

        Assert.Equal(9, file.TotalRecords);
        Assert.Equal(
            [
                null,
                ImportRowErrorCodes.InvalidSku,
                ImportRowErrorCodes.MissingName,
                ImportRowErrorCodes.InvalidCategoryId,
                ImportRowErrorCodes.InvalidUnitPrice,
                ImportRowErrorCodes.InvalidUnitPrice,
                ImportRowErrorCodes.InvalidIsActive,
                null,
                ImportRowErrorCodes.DuplicateSkuInFile
            ],
            file.Rows.Select(r => r.ValidationErrorCode));
    }

    [Fact]
    public async Task AWrongHeaderIsAFileLevelFailure()
    {
        Assert.Equal(
            ImportFileErrorCodes.InvalidHeader,
            AssertFailureCode(await ParseAsync(
                await File.ReadAllBytesAsync(Path.Combine(SamplesDirectory, "catalog-bad-header.csv")))));
    }

    [Fact]
    public async Task AMissingOrExtraColumnIsAFileLevelFailure()
    {
        var header = string.Join(',', CsvImportParser.ExpectedHeader);

        Assert.Equal(
            ImportFileErrorCodes.ColumnCountMismatch,
            AssertFailureCode(await ParseAsync($"{header}\nIMP-1,Name,1,10.00,true\n")));
        Assert.Equal(
            ImportFileErrorCodes.ColumnCountMismatch,
            AssertFailureCode(await ParseAsync($"{header}\nIMP-1,Name,1,10.00,true,Desc,extra\n")));
    }

    [Fact]
    public async Task InvalidUtf8IsAFileLevelFailure()
    {
        var header = Encoding.UTF8.GetBytes(string.Join(',', CsvImportParser.ExpectedHeader) + "\nIMP-1,");
        // 0xC3 starts a two-byte sequence; 0x28 is not a valid continuation byte.
        byte[] bytes = [.. header, 0xC3, 0x28, .. Encoding.UTF8.GetBytes(",1,10.00,true,Desc\n")];

        Assert.Equal(ImportFileErrorCodes.InvalidUtf8, AssertFailureCode(await ParseAsync(bytes)));
    }

    // ---- encoding: UTF-8 is the contract, BOM or not -----------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidUtf8IsAcceptedWithAndWithoutABom(bool withBom)
    {
        var header = string.Join(',', CsvImportParser.ExpectedHeader);
        var text = $"{header}\nIMP-0000000001,Name,1,10.00,true,Desc\n";
        byte[] bytes = withBom
            ? [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)]
            : Encoding.UTF8.GetBytes(text);

        var file = AssertSuccess(await ParseAsync(bytes));

        Assert.Equal(1, file.TotalRecords);
        Assert.Equal("IMP-0000000001", file.Rows[0].Sku);
        Assert.Null(file.Rows[0].ValidationErrorCode);
    }

    [Fact]
    public async Task TheBomDoesNotChangeTheHeaderComparison()
    {
        // Were the BOM left in the stream it would become part of the first header field, so this
        // would fail as InvalidHeader rather than succeed.
        var header = string.Join(',', CsvImportParser.ExpectedHeader);
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes($"{header}\n")];

        Assert.Equal(0, AssertSuccess(await ParseAsync(bytes)).TotalRecords);
    }

    [Fact]
    public async Task ABomOnlyFileIsEmpty()
    {
        Assert.Equal(
            ImportFileErrorCodes.EmptyFile,
            AssertFailureCode(await ParseAsync([0xEF, 0xBB, 0xBF])));
    }

    [Theory]
    [InlineData("utf-16LE")]
    [InlineData("utf-16BE")]
    [InlineData("utf-32LE")]
    [InlineData("utf-32BE")]
    public async Task NonUtf8BomsAreRejectedAtFileLevel(string encodingName)
    {
        var header = string.Join(',', CsvImportParser.ExpectedHeader);
        var text = $"{header}\nIMP-0000000001,Name,1,10.00,true,Desc\n";

        Encoding encoding = encodingName switch
        {
            "utf-16LE" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            "utf-16BE" => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
            "utf-32LE" => new UTF32Encoding(bigEndian: false, byteOrderMark: true),
            _ => new UTF32Encoding(bigEndian: true, byteOrderMark: true)
        };

        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];

        Assert.Equal(
            ImportFileErrorCodes.UnsupportedEncoding,
            AssertFailureCode(await ParseAsync(bytes)));
    }

    // ---- blank logical records ---------------------------------------------------

    [Fact]
    public async Task ASingleTrailingNewlineDoesNotProduceAnExtraRecord()
    {
        var header = string.Join(',', CsvImportParser.ExpectedHeader);

        var file = AssertSuccess(await ParseAsync(
            $"{header}\nIMP-0000000001,Name,1,10.00,true,Desc\nIMP-0000000002,Name,1,10.00,true,Desc\n"));

        Assert.Equal(2, file.TotalRecords);
        Assert.Equal([1, 2], file.Rows.Select(r => r.RecordNumber));
    }

    [Fact]
    public async Task AFileWithNoTrailingNewlineParsesTheSameWay()
    {
        var header = string.Join(',', CsvImportParser.ExpectedHeader);

        Assert.Equal(
            2,
            AssertSuccess(await ParseAsync(
                $"{header}\nIMP-0000000001,Name,1,10.00,true,Desc\nIMP-0000000002,Name,1,10.00,true,Desc"))
                .TotalRecords);
    }

    [Fact]
    public async Task ABlankLineBetweenTwoRecordsIsAColumnCountFailure()
    {
        var header = string.Join(',', CsvImportParser.ExpectedHeader);

        Assert.Equal(
            ImportFileErrorCodes.ColumnCountMismatch,
            AssertFailureCode(await ParseAsync(
                $"{header}\nIMP-0000000001,Name,1,10.00,true,Desc\n\nIMP-0000000002,Name,1,10.00,true,Desc\n")));
    }

    [Fact]
    public async Task ASingleEmptyQuotedFieldRecordIsAColumnCountFailure()
    {
        var header = string.Join(',', CsvImportParser.ExpectedHeader);

        Assert.Equal(
            ImportFileErrorCodes.ColumnCountMismatch,
            AssertFailureCode(await ParseAsync(
                $"{header}\nIMP-0000000001,Name,1,10.00,true,Desc\n\"\"\n")));
    }

    [Fact]
    public async Task MoreThanOneTrailingBlankLineIsAColumnCountFailure()
    {
        var header = string.Join(',', CsvImportParser.ExpectedHeader);

        Assert.Equal(
            ImportFileErrorCodes.ColumnCountMismatch,
            AssertFailureCode(await ParseAsync(
                $"{header}\nIMP-0000000001,Name,1,10.00,true,Desc\n\n\n")));
    }

    [Fact]
    public async Task ABlankLineDoesNotAdvanceTheRecordNumber()
    {
        // The third record must still be numbered 3: a skipped blank line used to consume no
        // number, which made the reported record number drift from the file's own line order.
        var header = string.Join(',', CsvImportParser.ExpectedHeader);
        var result = await ParseAsync(
            $"{header}\nIMP-0000000001,Name,1,10.00,true,Desc\n\nIMP-0000000002,Name,1,10.00,true,Desc\n");

        Assert.Contains("Record 2", AssertFailure(result).Message);
    }

    [Fact]
    public async Task MalformedQuotingIsAFileLevelFailure()
    {
        var header = string.Join(',', CsvImportParser.ExpectedHeader);

        Assert.Equal(
            ImportFileErrorCodes.MalformedCsv,
            AssertFailureCode(await ParseAsync($"{header}\nIMP-1,\"unterminated,1,10.00,true,Desc\n")));
    }

    [Fact]
    public async Task AnEmptyFileIsAFileLevelFailure()
    {
        Assert.Equal(ImportFileErrorCodes.EmptyFile, AssertFailureCode(await ParseAsync([])));
    }

    // ---- boundaries: just under and just over ----------------------------------------------

    [Fact]
    public async Task AFileJustUnderTheSizeLimitIsAcceptedAndJustOverIsRejected()
    {
        Assert.IsType<ImportParseResult.Success>(await ParseAsync(BuildPaddedFile(CsvImportParser.MaxFileBytes)));
        Assert.Equal(
            ImportFileErrorCodes.FileTooLarge,
            AssertFailureCode(await ParseAsync(BuildPaddedFile(CsvImportParser.MaxFileBytes + 1))));
    }

    [Fact]
    public async Task TheRecordCountLimitIsEnforcedOnTheRecordJustOverIt()
    {
        Assert.Equal(
            CsvImportParser.MaxRecords,
            AssertSuccess(await ParseAsync(BuildRecords(CsvImportParser.MaxRecords))).TotalRecords);
        Assert.Equal(
            ImportFileErrorCodes.TooManyRecords,
            AssertFailureCode(await ParseAsync(BuildRecords(CsvImportParser.MaxRecords + 1))));
    }

    [Fact]
    public async Task ASingleRecordOverTheRecordSizeLimitIsAFileLevelFailure()
    {
        var header = string.Join(',', CsvImportParser.ExpectedHeader);

        // Just under: the record fits, though its description is rejected for being too long.
        var underRecord = $"IMP-1,Name,1,10.00,true,{new string('d', CsvImportParser.MaxRecordBytes - 200)}";
        Assert.IsType<ImportParseResult.Success>(await ParseAsync($"{header}\n{underRecord}\n"));

        var overRecord = $"IMP-1,Name,1,10.00,true,{new string('d', CsvImportParser.MaxRecordBytes + 1)}";
        Assert.Equal(
            ImportFileErrorCodes.RecordTooLarge,
            AssertFailureCode(await ParseAsync($"{header}\n{overRecord}\n")));
    }

    [Fact]
    public async Task TheRecordSizeLimitCountsBytesNotCharacters()
    {
        var header = string.Join(',', CsvImportParser.ExpectedHeader);
        // 'ş' is two UTF-8 bytes, so half the limit in characters is over the limit in bytes.
        var description = new string('ş', (CsvImportParser.MaxRecordBytes / 2) + 10);

        Assert.Equal(
            ImportFileErrorCodes.RecordTooLarge,
            AssertFailureCode(await ParseAsync($"{header}\nIMP-1,Name,1,10.00,true,{description}\n")));
    }

    [Fact]
    public async Task TheSizeLimitIsEnforcedOnBytesReadNotOnReportedLength()
    {
        // A stream that lies about its Length must still be bounded by what is actually read.
        using var stream = new LengthLyingStream(BuildPaddedFile(CsvImportParser.MaxFileBytes + 4096));

        var result = await new CsvImportParser().ParseAsync(stream, CancellationToken.None);

        Assert.Equal(ImportFileErrorCodes.FileTooLarge, AssertFailureCode(result));
    }

    /// <summary>
    /// An all-ASCII file of exactly <paramref name="totalBytes"/> bytes, made of many ordinary
    /// records. The size limit has to be reached with records that are individually legal,
    /// otherwise the record-size limit would fire first and the file limit would go untested.
    /// </summary>
    private static byte[] BuildPaddedFile(int totalBytes)
    {
        var builder = new StringBuilder(string.Join(',', CsvImportParser.ExpectedHeader)).Append('\n');
        const int descriptionLength = 1000;

        for (var i = 1; ; i++)
        {
            var prefix = $"IMP-{i:D10},Name,1,10.00,true,";
            var remaining = totalBytes - builder.Length - prefix.Length - 1;

            if (remaining <= descriptionLength)
            {
                builder.Append(prefix).Append('d', remaining).Append('\n');
                break;
            }

            builder.Append(prefix).Append('d', descriptionLength).Append('\n');
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static byte[] BuildRecords(int count)
    {
        var builder = new StringBuilder(string.Join(',', CsvImportParser.ExpectedHeader)).Append('\n');
        for (var i = 1; i <= count; i++)
        {
            builder.Append("IMP-").Append(i.ToString("D10")).Append(",Name,1,10.00,true,D\n");
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>A stream whose reported Length is far smaller than what it actually yields.</summary>
    private sealed class LengthLyingStream(byte[] payload) : MemoryStream(payload, writable: false)
    {
        public override long Length => 16;
    }

    private static string LocateSamples()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "data", "samples");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate data/samples from the test output directory.");
    }
}
