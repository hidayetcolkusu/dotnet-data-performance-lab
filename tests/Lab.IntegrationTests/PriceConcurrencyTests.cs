using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DataPerformanceLab.Catalog;
using DataPerformanceLab.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DataPerformanceLab.IntegrationTests;

/// <summary>
/// Optimistic concurrency on the SQL rowversion: two writers holding the same
/// version cannot both win, and the loser is told so rather than silently overwriting.
/// </summary>
[Collection("sql+redis")]
public sealed class PriceConcurrencyTests(SqlServerFixture sql, RedisFixture redis) : IAsyncLifetime
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

    private CacheTestHarness Harness() => new(sql, redis, _database);

    private async Task<ProductDetail> ReadDetailAsync()
    {
        await using var db = sql.CreateContext(_database);
        return (await new ProductQueries(db).FindAsync(Sku, CancellationToken.None))!;
    }

    [Fact]
    public async Task OfTwoUpdatesSharingAVersionOnlyOneSucceeds()
    {
        await using var harness = Harness();
        var detail = await ReadDetailAsync();
        var version = Convert.FromBase64String(detail.Version);

        var first = harness.CreateUpdater(out _);
        var second = harness.CreateUpdater(out _);

        // Both hold the same rowversion. SQL Server decides the winner in the WHERE clause.
        var firstResult = await first.UpdatePriceAsync(Sku, 11.11m, version, CancellationToken.None);
        var secondResult = await second.UpdatePriceAsync(Sku, 22.22m, version, CancellationToken.None);

        Assert.IsType<UpdatePriceResult.Updated>(firstResult);
        Assert.IsType<UpdatePriceResult.VersionConflict>(secondResult);

        await using var verify = sql.CreateContext(_database);
        var stored = await verify.Products.AsNoTracking().SingleAsync(p => p.Sku == Sku);
        Assert.Equal(11.11m, stored.UnitPrice);
    }

    [Fact]
    public async Task ConcurrentUpdatesWithTheSameVersionProduceExactlyOneWinner()
    {
        await using var harness = Harness();
        var version = Convert.FromBase64String((await ReadDetailAsync()).Version);

        // Fired together rather than in sequence, so the race is real rather than simulated.
        var updaters = Enumerable.Range(1, 5)
            .Select(i => (Price: 10m + i, Updater: harness.CreateUpdater(out _)))
            .ToList();

        var results = await Task.WhenAll(updaters.Select(u =>
            u.Updater.UpdatePriceAsync(Sku, u.Price, version, CancellationToken.None)));

        Assert.Single(results.OfType<UpdatePriceResult.Updated>());
        Assert.Equal(4, results.OfType<UpdatePriceResult.VersionConflict>().Count());
    }

    [Fact]
    public async Task ASuccessfulUpdateReturnsTheNewVersionThatTheNextUpdateNeeds()
    {
        await using var harness = Harness();
        var detail = await ReadDetailAsync();

        var updater = harness.CreateUpdater(out _);
        var first = Assert.IsType<UpdatePriceResult.Updated>(await updater.UpdatePriceAsync(
            Sku, 33.33m, Convert.FromBase64String(detail.Version), CancellationToken.None));

        Assert.NotEqual(detail.Version, first.Detail.Version);
        Assert.Equal(33.33m, first.Detail.UnitPrice);

        // The returned version is immediately usable for the follow-up update.
        var second = await updater.UpdatePriceAsync(
            Sku, 44.44m, Convert.FromBase64String(first.Detail.Version), CancellationToken.None);
        Assert.IsType<UpdatePriceResult.Updated>(second);
    }

    [Fact]
    public async Task AMissingProductIsReportedAsNotFoundRatherThanAsAConflict()
    {
        await using var harness = Harness();
        var updater = harness.CreateUpdater(out _);

        var result = await updater.UpdatePriceAsync(
            "SKU-DOES-NOT-EXIST", 10m, Convert.FromBase64String((await ReadDetailAsync()).Version),
            CancellationToken.None);

        Assert.IsType<UpdatePriceResult.NotFound>(result);
    }

    // Prices are given as strings: an [InlineData] decimal is routed through double by xUnit,
    // which would silently round the boundary value under test.
    [Theory]
    [InlineData("0")]
    [InlineData("10.5")]
    [InlineData("9999999999999999.99")]
    public void ValidPricesAreAccepted(string unitPrice) =>
        Assert.True(UpdatePrice.IsValidPrice(decimal.Parse(unitPrice, CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("-0.01")]
    [InlineData("1.005")]
    [InlineData("10000000000000000.00")]
    public void InvalidPricesAreRejectedBeforeReachingSql(string unitPrice) =>
        Assert.False(UpdatePrice.IsValidPrice(decimal.Parse(unitPrice, CultureInfo.InvariantCulture)));

    // ---- over HTTP ----------------------------------------------------------------------------

    [Fact]
    public async Task TheHttpEndpointReturns409WithTheProblemDetailsContract()
    {
        await using var factory = new LabApiFactory(sql, _database);
        using var client = factory.CreateClient();

        var detail = await client.GetFromJsonAsync<ProductDetail>($"/api/products/{Sku}");
        Assert.NotNull(detail);

        var firstResponse = await client.PutAsJsonAsync(
            $"/api/products/{Sku}/price", new { unitPrice = 55.55m, expectedVersion = detail.Version });
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        var updated = await firstResponse.Content.ReadFromJsonAsync<ProductDetail>();
        Assert.Equal(55.55m, updated!.UnitPrice);
        Assert.NotEqual(detail.Version, updated.Version);

        // Replaying the now-stale version conflicts.
        var conflict = await client.PutAsJsonAsync(
            $"/api/products/{Sku}/price", new { unitPrice = 66.66m, expectedVersion = detail.Version });

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        using var problem = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        Assert.Equal("VersionConflict", problem.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("traceId").GetString()));

        var body = await conflict.Content.ReadAsStringAsync();
        Assert.DoesNotContain("UPDATE dbo.Products", body, StringComparison.Ordinal);
        Assert.DoesNotContain("at DataPerformanceLab", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHttpEndpointRejectsAMalformedExpectedVersion()
    {
        await using var factory = new LabApiFactory(sql, _database);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            $"/api/products/{Sku}/price", new { unitPrice = 10m, expectedVersion = "not base64!" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("InvalidExpectedVersion", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheHttpEndpointRejectsAnInvalidPrice()
    {
        await using var factory = new LabApiFactory(sql, _database);
        using var client = factory.CreateClient();

        var detail = await client.GetFromJsonAsync<ProductDetail>($"/api/products/{Sku}");
        var response = await client.PutAsJsonAsync(
            $"/api/products/{Sku}/price", new { unitPrice = -1m, expectedVersion = detail!.Version });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("InvalidUnitPrice", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ReadinessReportsSqlAndCacheSeparately()
    {
        await using var factory = new LabApiFactory(sql, _database);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ready", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("reachable", body.RootElement.GetProperty("sql").GetString());

        // The API factory points Redis at a closed port, so the cache reports degraded while
        // the API itself stays ready — SQL is what readiness requires.
        Assert.Equal("degraded", body.RootElement.GetProperty("cache").GetString());
    }
}
