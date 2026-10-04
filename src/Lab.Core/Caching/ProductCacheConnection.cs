using DataPerformanceLab.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace DataPerformanceLab.Caching;

public interface IProductCacheConnection : IAsyncDisposable
{
    Task<IDatabase> GetDatabaseAsync(CancellationToken ct);
}

/// <summary>
/// Owns the one Redis connection for the process. StackExchange.Redis multiplexes,
/// so opening a connection per request would be both slower and a way to exhaust sockets.
///
/// The multiplexer is created once, lazily, and never replaced. With
/// <c>AbortOnConnectFail = false</c> it reconnects in the background on its own; while it is
/// disconnected, commands fail immediately (<see cref="BacklogPolicy.FailFast"/>) and the
/// caller falls back to SQL. An earlier version disposed and re-dialled the multiplexer under a
/// lock whenever it was disconnected, which serialized every request behind a full connect
/// timeout during an outage — see the concurrency test in <c>CacheBehaviorTests</c>.
/// </summary>
public sealed class ProductCacheConnection(IOptions<LabDataOptions> dataOptions, IOptions<CacheOptions> cacheOptions)
    : IProductCacheConnection
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IConnectionMultiplexer? _multiplexer;

    public async Task<IDatabase> GetDatabaseAsync(CancellationToken ct)
    {
        var existing = _multiplexer;
        if (existing is not null)
        {
            return existing.GetDatabase();
        }

        await _gate.WaitAsync(ct);
        try
        {
            _multiplexer ??= await ConnectionMultiplexer.ConnectAsync(BuildConfiguration());
            return _multiplexer.GetDatabase();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Timeouts are bounded and configured rather than left at their defaults: an unreachable
    /// Redis must fail fast into the SQL fallback instead of holding a request open. The values
    /// are recorded in the measurement manifest and in the runbook.
    /// </summary>
    private ConfigurationOptions BuildConfiguration()
    {
        var redis = dataOptions.Value.Redis;
        var cache = cacheOptions.Value;

        var configuration = new ConfigurationOptions
        {
            ConnectTimeout = cache.ConnectTimeoutMilliseconds,
            SyncTimeout = cache.OperationTimeoutMilliseconds,
            AsyncTimeout = cache.OperationTimeoutMilliseconds,
            ConnectRetry = 1,
            // The lab reports Redis as unreachable instead of blocking startup on it, and the
            // multiplexer keeps retrying in the background.
            AbortOnConnectFail = false,
            // A cache read that would wait for a reconnect is slower than the SQL read it is
            // meant to avoid; while disconnected, fail now and let the caller go to SQL.
            BacklogPolicy = BacklogPolicy.FailFast
        };

        configuration.EndPoints.Add(redis.Host, redis.Port);
        return configuration;
    }

    public async ValueTask DisposeAsync()
    {
        if (_multiplexer is not null)
        {
            await _multiplexer.DisposeAsync();
            _multiplexer = null;
        }

        _gate.Dispose();
    }
}
