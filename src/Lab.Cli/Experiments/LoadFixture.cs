using System.Security.Cryptography;
using System.Text;

namespace DataPerformanceLab.Cli.Experiments;

/// <summary>
/// One fixture entry: the SKU to request plus the identity the response must have.
/// The k6 check compares against these fields, so a 200 carrying the wrong row fails.
/// </summary>
public sealed record LoadFixtureProduct(
    long Id,
    string Sku,
    string Name,
    int CategoryId,
    decimal UnitPrice,
    bool IsActive);

public sealed record LoadFixture(
    string Schema,
    string Database,
    DatasetInfo Dataset,
    string FixtureHash,
    int Count,
    int Stride,
    IReadOnlyList<LoadFixtureProduct> Products)
{
    public const string ExpectedSchema = "dpl.load-fixture.v1";

    /// <summary>
    /// Identifies the exact SKU set and expected values. It is recorded in every k6 summary,
    /// so an off/on comparison built from two different fixtures is detectable afterwards.
    /// </summary>
    public static string ComputeHash(IEnumerable<LoadFixtureProduct> products)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var p in products)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(FormattableString.Invariant(
                $"{p.Id}\t{p.Sku}\t{p.Name}\t{p.CategoryId}\t{p.UnitPrice:0.00}\t{p.IsActive}\n")));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>
    /// Picks <paramref name="count"/> ids spread evenly across the ordered catalog. A
    /// contiguous head would confine the measurement to one corner of the index and of the
    /// buffer pool; an even stride keeps the working set representative and reproducible.
    /// </summary>
    public static IReadOnlyList<long> SelectIds(IReadOnlyList<long> orderedIds, int count)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Fixture size must be at least 1.");
        }

        if (orderedIds.Count < count)
        {
            throw new InvalidOperationException(
                $"The catalog holds {orderedIds.Count} products; a {count}-SKU fixture cannot be built. "
                + "Seed a larger profile or pass a smaller --count.");
        }

        var stride = orderedIds.Count / count;
        return [.. Enumerable.Range(0, count).Select(k => orderedIds[k * stride])];
    }

    public static int StrideFor(int catalogCount, int fixtureCount) => catalogCount / fixtureCount;
}
