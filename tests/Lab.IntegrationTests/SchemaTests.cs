using DataPerformanceLab.Catalog;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DataPerformanceLab.IntegrationTests;

[Collection("sql")]
public sealed class SchemaTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private string _databaseName = null!;

    public async Task InitializeAsync() =>
        _databaseName = await fixture.CreateAndMigrateTestDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static Product NewProduct(string sku, int categoryId = 1, decimal unitPrice = 10m) => new()
    {
        Sku = sku,
        Name = "Schema test product",
        CategoryId = categoryId,
        UnitPrice = unitPrice,
        IsActive = true,
        Description = "synthetic",
        CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task DuplicateSku_IsRejectedByDatabase()
    {
        await using var db = fixture.CreateContext(_databaseName);
        await using var tx = await db.Database.BeginTransactionAsync();

        db.Categories.Add(new Category { Id = 1, Name = "Schema" });
        await db.SaveChangesAsync();

        db.Products.Add(NewProduct("SKU-DUP"));
        db.Products.Add(NewProduct("SKU-DUP"));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var sql = Assert.IsType<SqlException>(exception.InnerException);
        Assert.True(sql.Number is 2601 or 2627,
            $"expected unique-index violation (2601/2627), got {sql.Number}");

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task UnknownCategoryId_IsRejectedByDatabase()
    {
        await using var db = fixture.CreateContext(_databaseName);
        await using var tx = await db.Database.BeginTransactionAsync();

        db.Categories.Add(new Category { Id = 1, Name = "Schema" });
        await db.SaveChangesAsync();

        db.Products.Add(NewProduct("SKU-FK", categoryId: 999));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var sql = Assert.IsType<SqlException>(exception.InnerException);
        Assert.Equal(547, sql.Number);

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task NegativeUnitPrice_IsRejectedByDatabase()
    {
        await using var db = fixture.CreateContext(_databaseName);
        await using var tx = await db.Database.BeginTransactionAsync();

        db.Categories.Add(new Category { Id = 1, Name = "Schema" });
        await db.SaveChangesAsync();

        db.Products.Add(NewProduct("SKU-NEG", unitPrice: -1m));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var sql = Assert.IsType<SqlException>(exception.InnerException);
        Assert.Equal(547, sql.Number);

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task RowVersion_IsDatabaseGenerated_AndChangesOnUpdate()
    {
        await using var db = fixture.CreateContext(_databaseName);

        db.Categories.Add(new Category { Id = 1, Name = "Schema" });
        var product = NewProduct("SKU-ROWVERSION");
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var original = product.Version.ToArray();
        Assert.Equal(8, original.Length);

        product.UnitPrice = 12m;
        await db.SaveChangesAsync();

        Assert.False(product.Version.AsSpan().SequenceEqual(original));
    }
}
