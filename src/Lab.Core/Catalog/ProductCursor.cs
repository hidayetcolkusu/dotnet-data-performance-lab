using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace DataPerformanceLab.Catalog;

public sealed record ProductCursor(DateTime CreatedAtUtc, long Id, int CategoryId)
{
    public const int MaxEncodedLength = 512;

    private const int SupportedVersion = 1;

    public string Encode()
    {
        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{SupportedVersion}|{CreatedAtUtc.Ticks}|{Id}|{CategoryId}");
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));
    }

    public static bool TryParse(
        string encoded,
        int requestCategoryId,
        [NotNullWhen(true)] out ProductCursor? cursor,
        [NotNullWhen(false)] out string? error)
    {
        cursor = null;
        error = null;

        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > MaxEncodedLength)
        {
            error = "Cursor is empty or longer than 512 characters.";
            return false;
        }

        byte[] payload;
        try
        {
            payload = Base64Url.DecodeFromChars(encoded.AsSpan());
        }
        catch (FormatException)
        {
            error = "Cursor is not valid base64url data.";
            return false;
        }

        var parts = Encoding.UTF8.GetString(payload).Split('|');
        if (parts.Length != 4
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || version != SupportedVersion)
        {
            error = "Cursor payload is malformed.";
            return false;
        }

        if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var cursorCategoryId))
        {
            error = "Cursor payload is malformed.";
            return false;
        }

        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            error = "Cursor timestamp is out of range.";
            return false;
        }

        if (cursorCategoryId != requestCategoryId)
        {
            error = $"Cursor belongs to category {cursorCategoryId}, but the request targets category {requestCategoryId}.";
            return false;
        }

        cursor = new ProductCursor(new DateTime(ticks, DateTimeKind.Utc), id, cursorCategoryId);
        return true;
    }
}
