using System.Text.RegularExpressions;

namespace DataPerformanceLab.Catalog;

public partial class SkuValidator
{
    [GeneratedRegex("^[A-Z0-9-]{1,32}$")]
    private static partial Regex Pattern();

    public static string Normalize(string rawSku) => rawSku.Trim().ToUpperInvariant();

    public static bool IsValid(string normalizedSku) => Pattern().IsMatch(normalizedSku);
}
