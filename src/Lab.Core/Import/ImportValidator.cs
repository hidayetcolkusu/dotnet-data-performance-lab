using System.Globalization;
using System.Text.RegularExpressions;
using DataPerformanceLab.Catalog;

namespace DataPerformanceLab.Import;

/// <summary>
/// Row-level validation error codes. These are stored in SQL and printed in the error report,
/// so they are part of the contract and must stay stable.
/// </summary>
public static class ImportRowErrorCodes
{
    public const string InvalidSku = "InvalidSku";
    public const string MissingName = "MissingName";
    public const string NameTooLong = "NameTooLong";
    public const string InvalidCategoryId = "InvalidCategoryId";
    public const string InvalidUnitPrice = "InvalidUnitPrice";
    public const string InvalidIsActive = "InvalidIsActive";
    public const string DescriptionTooLong = "DescriptionTooLong";
    public const string DuplicateSkuInFile = "DuplicateSkuInFile";

    /// <summary>
    /// Assigned during processing, not parsing: the SKU already exists in Products with
    /// different values. Import is insert-only and never updates an existing product.
    /// </summary>
    public const string ExistingSkuConflict = "ExistingSkuConflict";
}

/// <summary>The six raw CSV fields of one logical record, before any conversion.</summary>
public sealed record ImportFieldValues(
    string Sku,
    string Name,
    string CategoryId,
    string UnitPrice,
    string IsActive,
    string Description);

/// <summary>
/// Applies the import field rules. A failure here rejects the row, never the file.
/// Validation is pure and culture-independent: everything parses under the invariant culture.
/// </summary>
public static partial class ImportValidator
{
    public const int MaxNameLength = 120;

    public const int MaxDescriptionLength = 2000;

    public const decimal MaxUnitPrice = 9999999999999999.99m;

    /// <summary>Staging column widths; a rejected row keeps a truncated copy of what arrived.</summary>
    private const int StagedSkuLength = 64;
    private const int StagedNameLength = 200;
    private const int StagedDescriptionLength = 4000;
    private const int StagedRawLength = 64;

    /// <summary>
    /// Non-negative, at most 16 integer digits and at most two decimals, written plainly.
    /// Matching on the text rather than on the parsed value means "1.005" is rejected instead
    /// of quietly rounded into an accepted price.
    /// </summary>
    [GeneratedRegex(@"^[0-9]{1,16}(\.[0-9]{1,2})?$")]
    private static partial Regex UnitPricePattern();

    /// <summary>Plain non-negative integer; no sign, no separators, no whitespace inside.</summary>
    [GeneratedRegex("^[0-9]{1,9}$")]
    private static partial Regex CategoryIdPattern();

    /// <summary>
    /// Validates one record into a staging row. The first failing rule wins, so each row
    /// carries exactly one error code.
    /// </summary>
    public static ImportRow Validate(int recordNumber, ImportFieldValues fields)
    {
        var row = new ImportRow { RecordNumber = recordNumber };

        var sku = SkuValidator.Normalize(fields.Sku);
        row.Sku = Truncate(sku, StagedSkuLength);

        var name = fields.Name.Trim();
        row.Name = Truncate(name, StagedNameLength);

        var description = fields.Description;
        row.Description = Truncate(description, StagedDescriptionLength);

        if (!SkuValidator.IsValid(sku))
        {
            return Reject(row, ImportRowErrorCodes.InvalidSku, fields);
        }

        if (name.Length == 0)
        {
            return Reject(row, ImportRowErrorCodes.MissingName, fields);
        }

        if (name.Length > MaxNameLength)
        {
            return Reject(row, ImportRowErrorCodes.NameTooLong, fields);
        }

        if (!CategoryIdPattern().IsMatch(fields.CategoryId)
            || !int.TryParse(fields.CategoryId, NumberStyles.None, CultureInfo.InvariantCulture, out var categoryId)
            || categoryId is < 1 or > 20)
        {
            return Reject(row, ImportRowErrorCodes.InvalidCategoryId, fields);
        }

        if (!UnitPricePattern().IsMatch(fields.UnitPrice)
            || !decimal.TryParse(
                fields.UnitPrice, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var unitPrice)
            || unitPrice < 0m
            || unitPrice > MaxUnitPrice)
        {
            return Reject(row, ImportRowErrorCodes.InvalidUnitPrice, fields);
        }

        // Only the exact tokens "true" and "false" are accepted. 1/0/yes/no/True are
        // rejected on purpose rather than guessed at.
        if (fields.IsActive is not ("true" or "false"))
        {
            return Reject(row, ImportRowErrorCodes.InvalidIsActive, fields);
        }

        if (description.Length > MaxDescriptionLength)
        {
            return Reject(row, ImportRowErrorCodes.DescriptionTooLong, fields);
        }

        row.Sku = sku;
        row.Name = name;
        row.CategoryId = categoryId;
        row.UnitPrice = unitPrice;
        row.IsActive = fields.IsActive is "true";
        row.Description = description;
        return row;
    }

    /// <summary>
    /// First valid record for a SKU wins; later valid records for the same SKU are rejected.
    /// This decision is made once over the whole file, so it cannot reset at a batch boundary.
    /// A row that already failed field validation does not claim the SKU.
    /// </summary>
    public static void MarkDuplicateSkusInFile(IEnumerable<ImportRow> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.ValidationErrorCode is not null)
            {
                continue;
            }

            if (!seen.Add(row.Sku!))
            {
                row.ValidationErrorCode = ImportRowErrorCodes.DuplicateSkuInFile;
            }
        }
    }

    /// <summary>
    /// A rejected row keeps the raw text of the fields that could not be converted, so the
    /// report can be regenerated from SQL without the original file.
    /// </summary>
    private static ImportRow Reject(ImportRow row, string code, ImportFieldValues fields)
    {
        row.ValidationErrorCode = code;
        row.CategoryId = null;
        row.UnitPrice = null;
        row.IsActive = null;
        row.RawCategoryId = Truncate(fields.CategoryId, StagedRawLength);
        row.RawUnitPrice = Truncate(fields.UnitPrice, StagedRawLength);
        row.RawIsActive = Truncate(fields.IsActive, StagedRawLength);
        return row;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
