using DataPerformanceLab.Caching;
using DataPerformanceLab.Configuration;
using Microsoft.Extensions.Options;

namespace DataPerformanceLab.Api.Endpoints;

public static class CacheMetricsEndpoints
{
    /// <summary>
    /// The cache settings this process is actually running with. A manifest that copied them from
    /// appsettings.json would describe the file rather than the run — an environment override
    /// would make the record quietly wrong. No key prefix or connection material is exposed:
    /// only the knobs that change what the measurement means.
    /// </summary>
    private sealed record CacheConfigurationResponse(
        bool Enabled,
        int TtlSeconds,
        string InstanceId,
        int ConnectTimeoutMilliseconds,
        int OperationTimeoutMilliseconds);

    private sealed record CacheMetricsResponse(
        bool CacheEnabled,
        long Hits,
        long Misses,
        long Bypasses,
        long ReadFailures,
        long WriteFailures,
        long InvalidationFailures,
        long SqlFallbacks,
        long TotalLookups,
        double? HitRate,
        CacheConfigurationResponse Configuration);

    /// <summary>
    /// Process-wide cache counters, so a load run can record <i>why</i> it measured what it
    /// measured. Without this, "the cache was slower" is an observation with no mechanism —
    /// a hit rate distinguishes a cache that is working and still not helping from one that
    /// is mostly missing.
    ///
    /// Lab-only: the application refuses to start outside Development/Testing, so this is
    /// never exposed anywhere else. It is read after a measured run, never during one.
    /// </summary>
    public static void MapCacheMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/internal/cache-metrics", (
            IProductCache cache, CacheMetrics metrics, IOptions<CacheOptions> cacheOptions) =>
        {
            var snapshot = metrics.Snapshot();
            var options = cacheOptions.Value;

            return Results.Ok(new CacheMetricsResponse(
                cache.Enabled,
                snapshot.Hits,
                snapshot.Misses,
                snapshot.Bypasses,
                snapshot.ReadFailures,
                snapshot.WriteFailures,
                snapshot.InvalidationFailures,
                snapshot.SqlFallbacks,
                snapshot.TotalLookups,
                snapshot.HitRate,
                new CacheConfigurationResponse(
                    options.Enabled,
                    options.TtlSeconds,
                    options.InstanceId,
                    options.ConnectTimeoutMilliseconds,
                    options.OperationTimeoutMilliseconds)));
        });
    }
}
