namespace DataPerformanceLab.Catalog;

public sealed record ProductListItem(
    long Id,
    string Sku,
    string Name,
    int CategoryId,
    decimal UnitPrice,
    DateTime CreatedAtUtc);

public sealed record ProductDetail(
    long Id,
    string Sku,
    string Name,
    int CategoryId,
    decimal UnitPrice,
    bool IsActive,
    string Description,
    DateTime CreatedAtUtc,
    string Version);

public sealed record ProductPage(IReadOnlyList<ProductListItem> Items, ProductCursor? NextCursor);
