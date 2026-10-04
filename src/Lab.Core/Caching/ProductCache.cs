using System.Text.Json;
using DataPerformanceLab.Catalog;
using DataPerformanceLab.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace DataPerformanceLab.Caching;

public interface IProductCache
{
    /// <summary>False when Cache:Enabled is off; the reader then never touches Redis.</summary>
    bool Enabled { get; }

    Task<ProductDetail?> TryGetAsync(string normalizedSku, CancellationToken ct);

    Task SetAsync(ProductDetail detail, CancellationToken ct);

    Task InvalidateAsync(string normalizedSku, CancellationToken ct);

    /// <summary>Whether Redis is currently reachable, for the readiness endpoint.</summary>
    Task<bool> IsReachableAsync(CancellationToken ct);
}

/// <summary>
/// The cache when <c>Cache:Enabled</c> is false. Every method is a no-op, so an A/B run with
/// the cache off provably never opens a Redis connection.
/// </summary>
public sealed class DisabledProductCache : IProductCache
{
    public bool Enabled => false;

    public Task<ProductDetail?> TryGetAsync(string normalizedSku, CancellationToken ct) =>
        Task.FromResult<ProductDetail?>(null);

    public Task SetAsync(ProductDetail detail, CancellationToken ct) => Task.CompletedTask;

    public Task InvalidateAsync(string normalizedSku, CancellationToken ct) => Task.CompletedTask;

    public Task<bool> IsReachableAsync(CancellationToken ct) => Task.FromResult(false);
}

/// <summary>
/// Cache-aside product detail storage on Redis.
///
/// Redis is a discardable read copy: any connection, timeout or deserialization problem is
/// recorded and turned into a SQL read, never into a failed request. Cancellation is
/// deliberately not swallowed — a cancelled request must not look like a cache miss.
/// </summary>
public sealed class RedisProductCache(
    IProductCacheConnection connection,
    IOptions<CacheOptions> options,
    CacheMetrics metrics,
    ILogger<RedisProductCache> logger) : IProductCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private CacheOptions Options => options.Value;

    public bool Enabled => Options.Enabled;

    public string KeyFor(string normalizedSku) => $"{Options.ProductKeyPrefix}:{normalizedSku}";

    public async Task<ProductDetail?> TryGetAsync(string normalizedSku, CancellationToken ct)
    {
        try
        {
            var database = await connection.GetDatabaseAsync(ct);
            var value = await database.StringGetAsync(KeyFor(normalizedSku));
            if (value.IsNullOrEmpty)
            {
                return null;
            }

            return JsonSerializer.Deserialize<ProductDetail>((string)value!, JsonOptions);
        }
        catch (JsonException ex)
        {
            // A corrupt entry is repaired by falling back to SQL and overwriting on the refill.
            metrics.RecordReadFailure();
            logger.LogWarning(ex, "Discarding a cache entry that did not deserialize into a product detail.");
            return null;
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            metrics.RecordReadFailure();
            logger.LogWarning(ex, "Cache read failed; falling back to SQL.");
            return null;
        }
    }

    public async Task SetAsync(ProductDetail detail, CancellationToken ct)
    {
        try
        {
            var database = await connection.GetDatabaseAsync(ct);
            await database.StringSetAsync(
                KeyFor(detail.Sku),
                JsonSerializer.Serialize(detail, JsonOptions),
                // Absolute TTL, no sliding expiration: the entry's lifetime is bounded even
                // when it is read constantly.
                TimeSpan.FromSeconds(Options.TtlSeconds));
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            metrics.RecordWriteFailure();
            logger.LogWarning(ex, "Cache write failed; the response is unaffected.");
        }
    }

    public async Task InvalidateAsync(string normalizedSku, CancellationToken ct)
    {
        try
        {
            var database = await connection.GetDatabaseAsync(ct);
            await database.KeyDeleteAsync(KeyFor(normalizedSku));
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            // The SQL commit already happened and stands. This is recorded degradation, not a
            // reason to pretend the price update failed.
            metrics.RecordInvalidationFailure();
            logger.LogWarning(ex, "Cache invalidation failed after a committed price update.");
        }
    }

    public async Task<bool> IsReachableAsync(CancellationToken ct)
    {
        try
        {
            var database = await connection.GetDatabaseAsync(ct);
            await database.PingAsync();
            return true;
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Only Redis transport and protocol problems are absorbed. Cancellation and programming
    /// errors keep propagating, so this never becomes a blanket catch.
    /// </summary>
    private static bool IsRedisFailure(Exception ex) =>
        ex is RedisException or ObjectDisposedException or TimeoutException
        && ex is not OperationCanceledException;
}
