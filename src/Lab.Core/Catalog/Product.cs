namespace DataPerformanceLab.Catalog;

public sealed class Product
{
    public long Id { get; set; }

    public string Sku { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int CategoryId { get; set; }

    public decimal UnitPrice { get; set; }

    public bool IsActive { get; set; }

    public string Description { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public byte[] Version { get; set; } = [];

    public Category? Category { get; set; }
}
