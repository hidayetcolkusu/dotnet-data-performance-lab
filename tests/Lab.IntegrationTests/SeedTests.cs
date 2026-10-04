using DataPerformanceLab.Catalog;
using DataPerformanceLab.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DataPerformanceLab.IntegrationTests;

[Collection("sql")]
public sealed class SeedTests(SqlServerFixture fixture)
{
    private async Task<string> CreateMigratedDatabaseAsync() =>
        await fixture.CreateAndMigrateTestDatabaseAsync();

    private async Task<SeedManifest> SeedCisAsync(string databaseName)
    {
        await using var db = fixture.CreateContext(databaseName);
        var seeder = new CatalogSeeder();
        return await seeder.SeedAsync(db, SeedProfile.Ci, CancellationToken.None);
    }

    [Fact]
    public async Task SameProfile_ProducesIdenticalDataHash_InTwoDatabases()
    {
        var first = await CreateMigratedDatabaseAsync();
        var second = await CreateMigratedDatabaseAsync();

        var manifest1 = await SeedCisAsync(first);
        var manifest2 = await SeedCisAsync(second);

        Assert.NotEqual(manifest1.DataHash, string.Empty);
        Assert.Equal(manifest1.DataHash, manifest2.DataHash);
        Assert.Equal(SeedProfile.Ci.ProductCount, manifest1.ProductCount);
        Assert.Equal(SeedProfile.Ci.ProductCount, manifest2.ProductCount);
    }

    [Fact]
    public async Task ManifestDataHash_MatchesDatabaseReadback()
    {
        var databaseName = await CreateMigratedDatabaseAsync();
        var manifest = await SeedCisAsync(databaseName);

        await using var db = fixture.CreateContext(databaseName);
        var products = await db.Products.AsNoTracking().OrderBy(p => p.Id).ToListAsync();

        Assert.Equal(manifest.ProductCount, products.Count);
        Assert.Equal(manifest.DataHash, SeedDataHasher.ComputeHash(products));
        Assert.Equal("SKU-0000000001", products[0].Sku);
        Assert.InRange(products[0].Description.Length, 900, 1100);
    }

    [Fact]
    public async Task Manifest_TracksSkewedCategoryDistributionAndActiveCounts()
    {
        var databaseName = await CreateMigratedDatabaseAsync();
        var manifest = await SeedCisAsync(databaseName);

        Assert.Equal(manifest.ProductCount, manifest.CategoryCounts.Values.Sum());
        Assert.All(manifest.CategoryCounts.Keys, key => Assert.InRange(key, 1, 20));
        Assert.InRange(manifest.CategoryCounts[1], 550, 650);
        Assert.Equal(manifest.ProductCount, manifest.ActiveCount + manifest.InactiveCount);
        Assert.True(manifest.ActiveCount > manifest.InactiveCount);
    }

    [Fact]
    public async Task SameProfile_SecondSeed_IsNoOp()
    {
        var databaseName = await CreateMigratedDatabaseAsync();
        var first = await SeedCisAsync(databaseName);
        var second = await SeedCisAsync(databaseName);

        Assert.Equal(first.DataHash, second.DataHash);
        Assert.Equal(first.ProductCount, second.ProductCount);

        await using var db = fixture.CreateContext(databaseName);
        Assert.Equal(SeedProfile.Ci.ProductCount, await db.Products.CountAsync());
    }

    [Fact]
    public async Task DifferentProfile_SecondSeed_Throws()
    {
        var databaseName = await CreateMigratedDatabaseAsync();
        await SeedCisAsync(databaseName);

        await using var db = fixture.CreateContext(databaseName);
        var seeder = new CatalogSeeder();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => seeder.SeedAsync(db, SeedProfile.Default, CancellationToken.None));
        Assert.Contains("reset", exception.Message);
    }

    [Fact]
    public async Task NonEmptyProducts_Seeding_Throws()
    {
        var databaseName = await CreateMigratedDatabaseAsync();

        await using (var db = fixture.CreateContext(databaseName))
        {
            db.Categories.Add(new Category { Id = 1, Name = "Blocker" });
            db.Products.Add(new Product
            {
                Sku = "SKU-BLOCKER",
                Name = "Blocker",
                CategoryId = 1,
                UnitPrice = 1m,
                IsActive = true,
                Description = "synthetic",
                CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await db.SaveChangesAsync();
        }

        await using var seedDb = fixture.CreateContext(databaseName);
        var seeder = new CatalogSeeder();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => seeder.SeedAsync(seedDb, SeedProfile.Ci, CancellationToken.None));
        Assert.Contains("not empty", exception.Message);
    }
}
