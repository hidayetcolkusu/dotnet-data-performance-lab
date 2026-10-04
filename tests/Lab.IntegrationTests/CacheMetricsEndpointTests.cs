using System.Net;
using System.Net.Http.Json;
using DataPerformanceLab.Catalog;
using DataPerformanceLab.Data;
using Xunit;

namespace DataPerformanceLab.IntegrationTests;

/// <summary>
/// The counters a load run records alongside its latency. A measured difference with no hit
/// rate beside it is an observation without a mechanism, so these have to be readable and
/// they have to be right.
/// </summary>
[Collection("sql+redis")]
public sealed class CacheMetricsEndpointTests(SqlServerFixture sql, RedisFixture redis) : IAsyncLifetime
{
    private const string Sku = "SKU-0000000001";

    private string _database = string.Empty;

    public async Task InitializeAsync()
    {
        _database = await sql.CreateAndMigrateTestDatabaseAsync();

        await using var db = sql.CreateContext(_database);
        var seeder = new CatalogSeeder();
        db.Categories.AddRange(seeder.CreateCategories());
        db.Products.AddRange(seeder.GenerateProducts(5));
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record MetricsResponse(
        bool CacheEnabled,
        long Hits,
        long Misses,
        long Bypasses,
        long ReadFailures,
        long WriteFailures,
        long InvalidationFailures,
        long SqlFallbacks,
        long TotalLookups,
        double? HitRate);

    private static async Task<MetricsResponse> ReadMetricsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/internal/cache-metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MetricsResponse>())!;
    }

    [Fact]
    public async Task AMissThenAHitAreCountedSeparatelyAndProduceAHitRate()
    {
        await using var factory = new LabApiFactory(
            sql, _database, cacheEnabled: true, redisHost: redis.Host, redisPort: redis.Port);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/products/{Sku}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/products/{Sku}")).StatusCode);

        var metrics = await ReadMetricsAsync(client);

        Assert.True(metrics.CacheEnabled);
        Assert.Equal(1, metrics.Misses);
        Assert.Equal(1, metrics.Hits);
        Assert.Equal(0, metrics.Bypasses);
        // The miss went to SQL; the hit did not.
        Assert.Equal(1, metrics.SqlFallbacks);
        Assert.Equal(0.5, metrics.HitRate);
        Assert.Equal(2, metrics.TotalLookups);
    }

    [Fact]
    public async Task WithTheCacheOffEveryLookupIsABypassAndThereIsNoHitRate()
    {
        await using var factory = new LabApiFactory(
            sql, _database, cacheEnabled: false, redisHost: redis.Host, redisPort: redis.Port);
        var client = factory.CreateClient();

        await client.GetAsync($"/api/products/{Sku}");
        await client.GetAsync($"/api/products/{Sku}");

        var metrics = await ReadMetricsAsync(client);

        Assert.False(metrics.CacheEnabled);
        Assert.Equal(2, metrics.Bypasses);
        Assert.Equal(0, metrics.Hits);
        Assert.Equal(0, metrics.Misses);

        // A run with the cache disabled has no hit rate to speak of; reporting 0 would read
        // as "the cache missed everything" rather than "there was no cache".
        Assert.Null(metrics.HitRate);
    }

    [Fact]
    public async Task AMissingProductIsCountedAsAMissButNeverCachedAsOne()
    {
        await using var factory = new LabApiFactory(
            sql, _database, cacheEnabled: true, redisHost: redis.Host, redisPort: redis.Port);
        var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await client.GetAsync("/api/products/SKU-0000009999")).StatusCode);
        }

        var metrics = await ReadMetricsAsync(client);

        // Every one of the three is a miss: nothing was ever stored, so none could be a hit.
        Assert.Equal(3, metrics.Misses);
        Assert.Equal(0, metrics.Hits);
        Assert.Equal(3, metrics.SqlFallbacks);
    }

    [Fact]
    public async Task AnUnreachableRedisIsCountedAsReadFailuresWhileTheApiKeepsServing()
    {
        // Port 1 is closed, so every cache operation fails fast into the SQL fallback.
        await using var factory = new LabApiFactory(
            sql, _database, cacheEnabled: true, redisHost: "127.0.0.1", redisPort: 1);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/products/{Sku}")).StatusCode);

        var metrics = await ReadMetricsAsync(client);

        Assert.True(metrics.ReadFailures > 0);
        Assert.Equal(1, metrics.SqlFallbacks);
    }
}
