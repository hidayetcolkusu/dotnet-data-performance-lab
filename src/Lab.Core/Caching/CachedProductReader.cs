using DataPerformanceLab.Catalog;

namespace DataPerformanceLab.Caching;

/// <summary>
/// The cache-aside read path for product detail: cache, then SQL on a miss, then
/// fill with a bounded TTL.
///
/// A product that does not exist is <b>not</b> cached. Negative caching would turn one 404
/// into a TTL-long window where a freshly imported product still reads as missing.
/// </summary>
public sealed class CachedProductReader(ProductQueries queries, IProductCache cache, CacheMetrics metrics)
{
    /// <summary>
    /// Test seam for the stale-refill race: awaited after the SQL read but before the cache
    /// fill, so a test can let a writer commit in between. Null in every normal run.
    /// </summary>
    public Func<Task>? AfterSqlReadAsync { get; set; }

    public async Task<ProductDetail?> ReadAsync(string sku, CancellationToken ct)
    {
        var normalized = SkuValidator.Normalize(sku);

        if (!cache.Enabled)
        {
            metrics.RecordBypass();
            return await queries.FindAsync(normalized, ct);
        }

        var cached = await cache.TryGetAsync(normalized, ct);
        if (cached is not null)
        {
            metrics.RecordHit();
            return cached;
        }

        metrics.RecordMiss();
        metrics.RecordSqlFallback();

        var detail = await queries.FindAsync(normalized, ct);

        if (AfterSqlReadAsync is not null)
        {
            await AfterSqlReadAsync();
        }

        if (detail is not null)
        {
            await cache.SetAsync(detail, ct);
        }

        return detail;
    }
}
