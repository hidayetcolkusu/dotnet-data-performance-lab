using System.Security.Cryptography;
using System.Text;
using DataPerformanceLab.Catalog;
using DataPerformanceLab.Data;
using Microsoft.EntityFrameworkCore;

namespace DataPerformanceLab.Cli.Experiments;

/// <summary>The two controlled A/B experiments.</summary>
public enum QueryExperimentKind
{
    /// <summary>Q1: identical DTO projection, candidate index absent vs present.</summary>
    Index,

    /// <summary>Q2: candidate index present in both, entity materialization vs SQL projection.</summary>
    Projection
}

/// <summary>
/// One measurable side of an experiment. "A"/"B" naming matches the AB/BA round order
/// recorded in the manifest.
/// </summary>
public enum QueryVariantId
{
    /// <summary>Q1-A: candidate index dropped.</summary>
    IndexAbsent,

    /// <summary>Q1-B: candidate index present.</summary>
    IndexPresent,

    /// <summary>Q2-A: whole entity materialized with AsNoTracking, mapped to the DTO in memory.</summary>
    EntityThenMap,

    /// <summary>Q2-B: the same DTO selected in SQL.</summary>
    SqlProjection
}

/// <summary>
/// The catalog query under test, in the two shapes the experiment compares. Both shapes must
/// return the identical ordered DTO sequence; only how the rows are produced differs.
/// </summary>
public static class QueryVariants
{
    /// <summary>The business query returns the first 50 results.</summary>
    public const int PageSize = 50;

    /// <summary>Category parameter profiles are measured separately, never averaged together.</summary>
    public static readonly int[] CategoryProfiles = [1, 20];

    /// <summary>Which variants each experiment alternates between, in AB order.</summary>
    public static (QueryVariantId A, QueryVariantId B) VariantsFor(QueryExperimentKind kind) => kind switch
    {
        QueryExperimentKind.Index => (QueryVariantId.IndexAbsent, QueryVariantId.IndexPresent),
        QueryExperimentKind.Projection => (QueryVariantId.EntityThenMap, QueryVariantId.SqlProjection),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>
    /// Runs the variant to full materialization. Q1's two sides run the same SQL-projection
    /// query on purpose: the only changed factor is whether the candidate index exists.
    /// </summary>
    public static Task<List<ProductListItem>> ExecuteAsync(
        LabDbContext db,
        QueryVariantId variant,
        int categoryId,
        CancellationToken ct) => variant switch
        {
            QueryVariantId.EntityThenMap => ExecuteEntityThenMapAsync(db, categoryId, ct),
            _ => ExecuteSqlProjectionAsync(db, categoryId, ct)
        };

    /// <summary>Selects the DTO in SQL — the shape the API itself uses.</summary>
    public static Task<List<ProductListItem>> ExecuteSqlProjectionAsync(
        LabDbContext db,
        int categoryId,
        CancellationToken ct) =>
        BaseQuery(db, categoryId)
            .Select(p => new ProductListItem(p.Id, p.Sku, p.Name, p.CategoryId, p.UnitPrice, p.CreatedAtUtc))
            .Take(PageSize)
            .ToListAsync(ct);

    /// <summary>
    /// Materializes the whole entity — including the ~1 KB Description the DTO never
    /// exposes — and maps afterwards. Tracking stays off in both variants so change tracking is
    /// not part of the difference. The wide columns also defeat the covering index, so this
    /// variant pays a key lookup per row as well: payload and lookups are measured together.
    /// </summary>
    public static async Task<List<ProductListItem>> ExecuteEntityThenMapAsync(
        LabDbContext db,
        int categoryId,
        CancellationToken ct)
    {
        var entities = await BaseQuery(db, categoryId)
            .Take(PageSize)
            .ToListAsync(ct);

        return [.. entities.Select(p =>
            new ProductListItem(p.Id, p.Sku, p.Name, p.CategoryId, p.UnitPrice, p.CreatedAtUtc))];
    }

    private static IOrderedQueryable<Product> BaseQuery(LabDbContext db, int categoryId) =>
        db.Products.AsNoTracking()
            .Where(p => p.CategoryId == categoryId && p.IsActive)
            .OrderBy(p => p.CreatedAtUtc)
            .ThenBy(p => p.Id);

    /// <summary>
    /// Ordered hash over the DTO fields. Equal hashes across variants are the correctness
    /// gate that makes a timing comparison meaningful at all.
    /// </summary>
    public static string HashResults(IEnumerable<ProductListItem> items)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var item in items)
        {
            var line = FormattableString.Invariant(
                $"{item.Id}\t{item.Sku}\t{item.Name}\t{item.CategoryId}\t{item.UnitPrice:0.00}\t{item.CreatedAtUtc:yyyy-MM-dd'T'HH:mm:ss.fffffff}\n");
            hash.AppendData(Encoding.UTF8.GetBytes(line));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
