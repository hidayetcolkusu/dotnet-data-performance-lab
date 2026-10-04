namespace DataPerformanceLab.Data;

/// <summary>
/// Names of the indexes the catalog query experiments toggle. They live in Core because
/// both the EF mapping and the CLI experiment runner have to agree on the exact name.
/// </summary>
public static class CatalogIndexes
{
    /// <summary>
    /// Candidate index for the Q1 A/B experiment: covers the category/active filter,
    /// the CreatedAtUtc/Id ordering and every column the list DTO projects.
    /// </summary>
    public const string CategoryActiveCreatedId = "IX_Products_Category_Active_Created_Id";

    /// <summary>Unique index that backs the SKU contract; experiments never drop it.</summary>
    public const string SkuUnique = "IX_Products_Sku";
}
