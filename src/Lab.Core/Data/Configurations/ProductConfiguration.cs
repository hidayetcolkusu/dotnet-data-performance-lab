using DataPerformanceLab.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataPerformanceLab.Data.Configurations;

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products", table =>
        {
            table.HasCheckConstraint("CK_Products_UnitPrice_NonNegative", "[UnitPrice] >= 0");
        });

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id).ValueGeneratedOnAdd();

        builder.Property(p => p.Sku)
            .HasColumnType("varchar(32)")
            .UseCollation("Latin1_General_100_BIN2")
            .IsRequired();

        builder.HasIndex(p => p.Sku)
            .HasDatabaseName(CatalogIndexes.SkuUnique)
            .IsUnique();

        // The candidate index ships with the normal API schema; the Q1
        // experiment only drops it inside the isolated experiment database.
        builder.HasIndex(p => new { p.CategoryId, p.IsActive, p.CreatedAtUtc, p.Id })
            .HasDatabaseName(CatalogIndexes.CategoryActiveCreatedId)
            .IncludeProperties(p => new { p.Sku, p.Name, p.UnitPrice });

        builder.Property(p => p.Name).HasMaxLength(120).IsRequired();

        builder.Property(p => p.UnitPrice).HasPrecision(18, 2);

        builder.Property(p => p.Description).HasMaxLength(2000).IsRequired();

        builder.Property(p => p.CreatedAtUtc).HasColumnType("datetime2");

        builder.Property(p => p.Version).IsRowVersion();

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(p => p.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
