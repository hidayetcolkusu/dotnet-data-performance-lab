using DataPerformanceLab.Data;
using Microsoft.EntityFrameworkCore;

namespace DataPerformanceLab.Catalog;

public sealed class ProductQueries(LabDbContext db)
{
    public const int MaxPageSize = 100;

    public async Task<ProductPage> ListAsync(
        int categoryId,
        int pageSize,
        ProductCursor? cursor,
        CancellationToken ct)
    {
        var query = db.Products.AsNoTracking()
            .Where(p => p.CategoryId == categoryId && p.IsActive);

        if (cursor is not null)
        {
            query = query.Where(p =>
                p.CreatedAtUtc > cursor.CreatedAtUtc ||
                (p.CreatedAtUtc == cursor.CreatedAtUtc && p.Id > cursor.Id));
        }

        var items = await query
            .OrderBy(p => p.CreatedAtUtc)
            .ThenBy(p => p.Id)
            .Select(p => new ProductListItem(p.Id, p.Sku, p.Name, p.CategoryId, p.UnitPrice, p.CreatedAtUtc))
            .Take(pageSize + 1)
            .ToListAsync(ct);

        if (items.Count <= pageSize)
        {
            return new ProductPage(items, null);
        }

        var pageItems = items.Take(pageSize).ToList();
        var last = pageItems[^1];
        return new ProductPage(pageItems, new ProductCursor(last.CreatedAtUtc, last.Id, categoryId));
    }

    public async Task<ProductDetail?> FindAsync(string sku, CancellationToken ct)
    {
        var normalized = SkuValidator.Normalize(sku);
        var entity = await db.Products.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Sku == normalized, ct);

        return entity is null ? null : ToDetail(entity);
    }

    public static ProductDetail ToDetail(Product product) => new(
        product.Id,
        product.Sku,
        product.Name,
        product.CategoryId,
        product.UnitPrice,
        product.IsActive,
        product.Description,
        product.CreatedAtUtc,
        Convert.ToBase64String(product.Version));
}
