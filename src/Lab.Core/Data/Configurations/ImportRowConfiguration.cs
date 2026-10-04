using DataPerformanceLab.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataPerformanceLab.Data.Configurations;

public sealed class ImportRowConfiguration : IEntityTypeConfiguration<ImportRow>
{
    public void Configure(EntityTypeBuilder<ImportRow> builder)
    {
        builder.ToTable("ImportRows");

        builder.HasKey(r => new { r.JobId, r.RecordNumber });

        // Staging columns are deliberately wider than the Products contract: a rejected row
        // keeps the (truncated) raw value that failed validation, so the error report can
        // show what arrived. Products still enforces the narrow product limits.
        builder.Property(r => r.Sku).HasMaxLength(64);

        builder.Property(r => r.Name).HasMaxLength(200);

        builder.Property(r => r.UnitPrice).HasPrecision(18, 2);

        builder.Property(r => r.Description).HasMaxLength(4000);

        // A row whose price text is not a valid decimal cannot be coerced into UnitPrice;
        // the raw text is kept here so the report is reproducible from SQL alone.
        builder.Property(r => r.RawUnitPrice).HasMaxLength(64);

        builder.Property(r => r.RawIsActive).HasMaxLength(64);

        builder.Property(r => r.RawCategoryId).HasMaxLength(64);

        builder.Property(r => r.ValidationErrorCode).HasMaxLength(64);

        builder.Property(r => r.Outcome).HasConversion<string>().HasMaxLength(32);
    }
}
