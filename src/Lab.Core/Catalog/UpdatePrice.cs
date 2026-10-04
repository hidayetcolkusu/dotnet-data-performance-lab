using DataPerformanceLab.Caching;
using DataPerformanceLab.Data;
using Microsoft.EntityFrameworkCore;

namespace DataPerformanceLab.Catalog;

/// <summary>
/// The outcome of a price update. "Not found" and "version conflict" are separate cases:
/// collapsing them would tell a caller with a stale version that the product disappeared.
/// </summary>
public abstract record UpdatePriceResult
{
    public sealed record NotFound : UpdatePriceResult;

    public sealed record VersionConflict : UpdatePriceResult;

    public sealed record Updated(ProductDetail Detail) : UpdatePriceResult;
}

/// <summary>
/// Optimistic concurrency on the SQL rowversion, followed by cache invalidation.
///
/// The order matters and is not interchangeable: SQL commits first, then the key is deleted.
/// A failed delete is recorded as degradation — it never rolls back or disguises a committed
/// update.
/// </summary>
public sealed class UpdatePrice(LabDbContext db, IProductCache cache)
{
    public const decimal MaxUnitPrice = 9999999999999999.99m;

    public static bool IsValidPrice(decimal unitPrice) =>
        unitPrice >= 0m && unitPrice <= MaxUnitPrice && decimal.Round(unitPrice, 2) == unitPrice;

    public async Task<UpdatePriceResult> UpdatePriceAsync(
        string sku, decimal unitPrice, byte[] expectedVersion, CancellationToken ct)
    {
        var normalized = SkuValidator.Normalize(sku);

        ProductDetail detail;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            // The rowversion is part of the WHERE clause, so two concurrent updates holding the
            // same version cannot both match: SQL Server decides the winner, not the process.
            var affected = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE dbo.Products
                 SET UnitPrice = {unitPrice}
                 WHERE Sku = {normalized} AND [Version] = {expectedVersion}
                 """,
                ct);

            if (affected == 0)
            {
                var exists = await db.Products.AsNoTracking().AnyAsync(p => p.Sku == normalized, ct);
                await transaction.RollbackAsync(ct);

                return exists
                    ? new UpdatePriceResult.VersionConflict()
                    : new UpdatePriceResult.NotFound();
            }

            // Read back inside the transaction so the returned Version is the one this update
            // produced rather than one a later writer may already have replaced.
            var updated = await db.Products.AsNoTracking().SingleAsync(p => p.Sku == normalized, ct);
            detail = ProductQueries.ToDetail(updated);

            await transaction.CommitAsync(ct);
        }

        // Invalidation happens strictly after the commit. Deleting first would let a concurrent
        // reader refill the key with the pre-update value and keep it for a whole TTL.
        // Not cancellable: once the commit has happened, a client hanging up must not leave the
        // old price cached for a whole TTL. The delete is bounded by the Redis timeouts instead.
        await cache.InvalidateAsync(normalized, CancellationToken.None);

        return new UpdatePriceResult.Updated(detail);
    }
}
