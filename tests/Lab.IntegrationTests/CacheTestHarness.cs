using DataPerformanceLab.Caching;
using DataPerformanceLab.Catalog;
using DataPerformanceLab.Configuration;
using DataPerformanceLab.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataPerformanceLab.IntegrationTests;

/// <summary>
/// Wires the real cache-aside stack — real SQL Server, real Redis — with a per-test key
/// namespace, and counts the SQL round trips so "did this read reach SQL?" is a fact rather
/// than an inference from timing.
/// </summary>
public sealed class CacheTestHarness : IAsyncDisposable
{
    private readonly SqlServerFixture _sql;
    private readonly string _database;
    private readonly SqlCallCountingInterceptor _sqlCalls = new();
    private readonly List<LabDbContext> _contexts = [];

    public CacheTestHarness(
        SqlServerFixture sql,
        RedisFixture? redis,
        string database,
        bool cacheEnabled = true,
        int ttlSeconds = 60,
        string? redisHostOverride = null,
        int? redisPortOverride = null)
    {
        _sql = sql;
        _database = database;

        // A unique instance id per harness: keys are namespaced, so tests are isolated without
        // ever flushing a shared Redis.
        CacheOptions = new CacheOptions
        {
            Enabled = cacheEnabled,
            TtlSeconds = ttlSeconds,
            InstanceId = "test-" + Guid.NewGuid().ToString("N")[..8],
            ConnectTimeoutMilliseconds = 1_000,
            OperationTimeoutMilliseconds = 1_000
        };

        var dataOptions = Options.Create(new LabDataOptions
        {
            DatabaseName = database,
            SqlServer = new LabDataOptions.SqlServerOptions
            {
                Host = sql.Host,
                Port = sql.Port,
                PasswordOverride = sql.Password
            },
            Redis = new LabDataOptions.RedisOptions
            {
                Host = redisHostOverride ?? redis?.Host ?? "127.0.0.1",
                Port = redisPortOverride ?? redis?.Port ?? 6379
            }
        });

        Connection = new ProductCacheConnection(dataOptions, Options.Create(CacheOptions));
        Cache = cacheEnabled
            ? new RedisProductCache(
                Connection, Options.Create(CacheOptions), Metrics, NullLogger<RedisProductCache>.Instance)
            : new DisabledProductCache();
    }

    public CacheOptions CacheOptions { get; }

    public CacheMetrics Metrics { get; } = new();

    public IProductCache Cache { get; }

    public IProductCacheConnection Connection { get; }

    /// <summary>How many commands actually reached SQL Server since the last reset.</summary>
    public int SqlCallCount => _sqlCalls.Count;

    public void ResetSqlCallCount() => _sqlCalls.Reset();

    public LabDbContext CreateContext()
    {
        var context = _sql.CreateContext(_database, _sqlCalls);
        _contexts.Add(context);
        return context;
    }

    public CachedProductReader CreateReader(out LabDbContext context)
    {
        context = CreateContext();
        return new CachedProductReader(new ProductQueries(context), Cache, Metrics);
    }

    public UpdatePrice CreateUpdater(out LabDbContext context)
    {
        context = CreateContext();
        return new UpdatePrice(context, Cache);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var context in _contexts)
        {
            await context.DisposeAsync();
        }

        await Connection.DisposeAsync();
    }
}
