using DataPerformanceLab.Caching;
using DataPerformanceLab.Catalog;
using DataPerformanceLab.Data;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Xunit;

namespace DataPerformanceLab.IntegrationTests;

/// <summary>
/// Cache-aside behaviour against real SQL Server and real Redis, including the
/// consistency limit this design accepts rather than hides.
/// </summary>
[Collection("sql+redis")]
public sealed class CacheBehaviorTests(SqlServerFixture sql, RedisFixture redis) : IAsyncLifetime
{
    private const string Sku = "SKU-0000000001";

    private string _database = string.Empty;

    public async Task InitializeAsync()
    {
        _database = await sql.CreateAndMigrateTestDatabaseAsync();

        await using var db = sql.CreateContext(_database);
        var seeder = new CatalogSeeder();
        db.Categories.AddRange(seeder.CreateCategories());
        db.Products.AddRange(seeder.GenerateProducts(20));
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private CacheTestHarness Harness(bool cacheEnabled = true, int ttlSeconds = 60) =>
        new(sql, redis, _database, cacheEnabled, ttlSeconds);

    // ---- the normal cache-aside flow ---------------------------------------------------------

    [Fact]
    public async Task AMissReadsSqlAndFillsTheCacheAndTheNextReadIsAHit()
    {
        await using var harness = Harness();

        var reader = harness.CreateReader(out _);
        harness.ResetSqlCallCount();

        var first = await reader.ReadAsync(Sku, CancellationToken.None);
        Assert.NotNull(first);
        var sqlCallsAfterMiss = harness.SqlCallCount;
        Assert.True(sqlCallsAfterMiss > 0, "A cache miss must reach SQL.");

        harness.ResetSqlCallCount();
        var second = await reader.ReadAsync(Sku, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(0, harness.SqlCallCount);

        var metrics = harness.Metrics.Snapshot();
        Assert.Equal(1, metrics.Misses);
        Assert.Equal(1, metrics.Hits);
        Assert.Equal(1, metrics.SqlFallbacks);
        Assert.Equal(0, metrics.Bypasses);
    }

    [Fact]
    public async Task AProductThatDoesNotExistIsNotCached()
    {
        await using var harness = Harness();
        var reader = harness.CreateReader(out _);

        Assert.Null(await reader.ReadAsync("SKU-DOES-NOT-EXIST", CancellationToken.None));

        harness.ResetSqlCallCount();
        Assert.Null(await reader.ReadAsync("SKU-DOES-NOT-EXIST", CancellationToken.None));

        // Negative caching would make this second read skip SQL — and would keep a freshly
        // imported product invisible for a whole TTL.
        Assert.True(harness.SqlCallCount > 0, "A missing product must not be served from cache.");
        Assert.Equal(2, harness.Metrics.Snapshot().Misses);
    }

    [Fact]
    public async Task WithTheCacheOffRedisIsNeverContactedAndEveryReadHitsSql()
    {
        await using var harness = Harness(cacheEnabled: false);
        var reader = harness.CreateReader(out _);

        await reader.ReadAsync(Sku, CancellationToken.None);
        harness.ResetSqlCallCount();
        await reader.ReadAsync(Sku, CancellationToken.None);

        Assert.True(harness.SqlCallCount > 0);

        var metrics = harness.Metrics.Snapshot();
        Assert.Equal(2, metrics.Bypasses);
        Assert.Equal(0, metrics.Hits);
        Assert.Equal(0, metrics.Misses);

        // Nothing was written to Redis under this namespace either.
        await using var mux = await ConnectionMultiplexer.ConnectAsync(redis.Endpoint);
        Assert.False(await mux.GetDatabase().KeyExistsAsync($"{harness.CacheOptions.ProductKeyPrefix}:{Sku}"));
    }

    [Fact]
    public async Task ACommittedPriceUpdateInvalidatesTheKeyAndTheNextReadSeesTheNewPrice()
    {
        await using var harness = Harness();

        var reader = harness.CreateReader(out _);
        var cached = await reader.ReadAsync(Sku, CancellationToken.None);
        Assert.NotNull(cached);

        var updater = harness.CreateUpdater(out _);
        var result = await updater.UpdatePriceAsync(
            Sku, cached.UnitPrice + 5m, Convert.FromBase64String(cached.Version), CancellationToken.None);

        var updated = Assert.IsType<UpdatePriceResult.Updated>(result);
        Assert.Equal(cached.UnitPrice + 5m, updated.Detail.UnitPrice);

        // The key is gone, so the next read is a miss that reloads from SQL.
        await using var mux = await ConnectionMultiplexer.ConnectAsync(redis.Endpoint);
        Assert.False(await mux.GetDatabase().KeyExistsAsync($"{harness.CacheOptions.ProductKeyPrefix}:{Sku}"));

        var afterUpdate = await reader.ReadAsync(Sku, CancellationToken.None);
        Assert.Equal(cached.UnitPrice + 5m, afterUpdate!.UnitPrice);
        Assert.NotEqual(cached.Version, afterUpdate.Version);
    }

    [Fact]
    public async Task ACorruptCacheEntryIsDiscardedAndRepairedFromSql()
    {
        await using var harness = Harness();
        var key = $"{harness.CacheOptions.ProductKeyPrefix}:{Sku}";

        await using var mux = await ConnectionMultiplexer.ConnectAsync(redis.Endpoint);
        await mux.GetDatabase().StringSetAsync(key, "{ this is not a product detail ]");

        var reader = harness.CreateReader(out _);
        var detail = await reader.ReadAsync(Sku, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(Sku, detail.Sku);
        Assert.Equal(1, harness.Metrics.Snapshot().ReadFailures);

        // The refill overwrote the corrupt entry, so the next read is a clean hit.
        harness.ResetSqlCallCount();
        Assert.Equal(detail, await reader.ReadAsync(Sku, CancellationToken.None));
        Assert.Equal(0, harness.SqlCallCount);
    }

    // ---- Redis unavailable --------------------------------------------------------------------

    [Fact]
    public async Task WhenRedisIsUnreachableReadsStillSucceedFromSqlAndDegradationIsRecorded()
    {
        // Port 1 is closed, so every Redis operation fails inside the configured timeout.
        await using var harness = new CacheTestHarness(
            sql, redis, _database, redisHostOverride: "127.0.0.1", redisPortOverride: 1);

        var reader = harness.CreateReader(out _);
        var detail = await reader.ReadAsync(Sku, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(Sku, detail.Sku);

        var metrics = harness.Metrics.Snapshot();
        Assert.True(metrics.ReadFailures > 0, "An unreachable Redis must be recorded as a read failure.");
        Assert.Equal(1, metrics.SqlFallbacks);
        Assert.False(await harness.Cache.IsReachableAsync(CancellationToken.None));
    }

    [Fact]
    public async Task APriceUpdateStillCommitsWhenInvalidationCannotReachRedis()
    {
        await using var harness = new CacheTestHarness(
            sql, redis, _database, redisHostOverride: "127.0.0.1", redisPortOverride: 1);

        await using var readContext = sql.CreateContext(_database);
        var before = await new ProductQueries(readContext).FindAsync(Sku, CancellationToken.None);
        Assert.NotNull(before);

        var updater = harness.CreateUpdater(out _);
        var result = await updater.UpdatePriceAsync(
            Sku, before.UnitPrice + 1m, Convert.FromBase64String(before.Version), CancellationToken.None);

        // The SQL commit stands; the failed delete is degradation, not a failed update.
        Assert.IsType<UpdatePriceResult.Updated>(result);
        Assert.True(harness.Metrics.Snapshot().InvalidationFailures > 0);

        await using var verify = sql.CreateContext(_database);
        var stored = await verify.Products.AsNoTracking().SingleAsync(p => p.Sku == Sku);
        Assert.Equal(before.UnitPrice + 1m, stored.UnitPrice);
    }

    [Fact]
    public async Task ConcurrentReadsDuringARedisOutageFailOverInParallelNotOneAtATime()
    {
        // A listener that accepts connections and never answers: the Redis handshake hangs until
        // the connect timeout, which is what an unresponsive Redis looks like (unlike a closed
        // port, which refuses instantly and hides the cost of reconnecting).
        using var blackHole = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        blackHole.Start();
        var port = ((System.Net.IPEndPoint)blackHole.LocalEndpoint).Port;

        await using var harness = new CacheTestHarness(
            sql, redis, _database, redisHostOverride: "127.0.0.1", redisPortOverride: port);

        const int concurrentReads = 12;
        var readers = Enumerable.Range(0, concurrentReads).Select(_ => harness.CreateReader(out var _)).ToList();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var details = await Task.WhenAll(readers.Select(r => r.ReadAsync(Sku, CancellationToken.None)));
        stopwatch.Stop();

        Assert.All(details, d => Assert.Equal(Sku, d?.Sku));
        Assert.Equal(concurrentReads, harness.Metrics.Snapshot().SqlFallbacks);

        // One connect attempt is bounded by ConnectTimeout (1 s). If every request re-dialled
        // Redis behind a lock, these reads would queue for roughly concurrentReads seconds.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"{concurrentReads} concurrent reads took {stopwatch.Elapsed.TotalSeconds:F1} s during the outage.");
    }

    [Fact]
    public async Task TheCacheIsUsedAgainAfterRedisRecoversWithoutRestartingTheProcess()
    {
        await using var harness = Harness();

        await harness.CreateReader(out _).ReadAsync(Sku, CancellationToken.None);
        Assert.Equal(1, harness.Metrics.Snapshot().Misses);

        await redis.PauseAsync();
        try
        {
            var duringOutage = await harness.CreateReader(out _).ReadAsync(Sku, CancellationToken.None);
            Assert.Equal(Sku, duringOutage?.Sku);
            Assert.True(harness.Metrics.Snapshot().ReadFailures > 0);
        }
        finally
        {
            await redis.UnpauseAsync();
        }

        // The same connection object must find its way back; the process is not restarted.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await harness.Cache.IsReachableAsync(CancellationToken.None))
        {
            Assert.True(DateTime.UtcNow < deadline, "Redis was not reachable again 30 s after it recovered.");
            await Task.Delay(250);
        }

        var hitsBefore = harness.Metrics.Snapshot().Hits;
        await harness.CreateReader(out _).ReadAsync(Sku, CancellationToken.None);
        await harness.CreateReader(out _).ReadAsync(Sku, CancellationToken.None);
        Assert.True(harness.Metrics.Snapshot().Hits > hitsBefore, "Reads after recovery never hit the cache.");
    }

    // ---- the consistency limit this design accepts ---------------------------------------------

    [Fact]
    public async Task AReaderCanRefillAStaleValueAfterInvalidationAndTheTtlBoundsIt()
    {
        // A deliberately short TTL so the bound can be observed inside a test.
        await using var harness = Harness(ttlSeconds: 3);

        await using var readContext = sql.CreateContext(_database);
        var original = await new ProductQueries(readContext).FindAsync(Sku, CancellationToken.None);
        Assert.NotNull(original);
        var newPrice = original.UnitPrice + 7m;

        var writerReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readerParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var reader = harness.CreateReader(out _);

        // Two explicit barriers rather than a sleep: the reader has already read the old value
        // from SQL, and parks there until the writer has committed and invalidated.
        reader.AfterSqlReadAsync = async () =>
        {
            readerParked.TrySetResult();
            await writerReleased.Task;
        };

        var readerTask = reader.ReadAsync(Sku, CancellationToken.None);
        await readerParked.Task;

        var updater = harness.CreateUpdater(out _);
        var updateResult = await updater.UpdatePriceAsync(
            Sku, newPrice, Convert.FromBase64String(original.Version), CancellationToken.None);
        Assert.IsType<UpdatePriceResult.Updated>(updateResult);

        // Now let the reader write what it read before the update into the cache.
        writerReleased.SetResult();
        var readerResult = await readerTask;
        Assert.Equal(original.UnitPrice, readerResult!.UnitPrice);

        // The stale value is observable. This cache is not strongly consistent, and this test
        // documents that rather than claiming otherwise.
        var staleReader = harness.CreateReader(out _);
        Assert.Equal(original.UnitPrice, (await staleReader.ReadAsync(Sku, CancellationToken.None))!.UnitPrice);

        // What the TTL guarantees is that the stale entry's lifetime is bounded. Poll until it
        // expires rather than trusting a fixed delay.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        decimal observed;
        do
        {
            await Task.Delay(250);
            observed = (await staleReader.ReadAsync(Sku, CancellationToken.None))!.UnitPrice;
        }
        while (observed != newPrice && DateTime.UtcNow < deadline);

        Assert.Equal(newPrice, observed);
    }

    // ---- metrics ------------------------------------------------------------------------------

    [Fact]
    public async Task MetricDeltasIsolateOneWindowOfActivity()
    {
        await using var harness = Harness();
        var reader = harness.CreateReader(out _);

        await reader.ReadAsync(Sku, CancellationToken.None);
        var baseline = harness.Metrics.Snapshot();

        await reader.ReadAsync(Sku, CancellationToken.None);
        await reader.ReadAsync(Sku, CancellationToken.None);

        var delta = harness.Metrics.Snapshot().Delta(baseline);

        Assert.Equal(2, delta.Hits);
        Assert.Equal(0, delta.Misses);
        Assert.Equal(1.0, delta.HitRate);
    }

    [Fact]
    public void HitRateIsUndefinedWhenNoCacheEligibleLookupHappened()
    {
        Assert.Null(new CacheMetricsSnapshot(0, 0, 25, 0, 0, 0, 25).HitRate);
    }
}
