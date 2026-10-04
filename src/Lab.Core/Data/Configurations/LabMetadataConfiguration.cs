using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataPerformanceLab.Data.Configurations;

public sealed class LabMetadataConfiguration : IEntityTypeConfiguration<LabMetadataEntry>
{
    public void Configure(EntityTypeBuilder<LabMetadataEntry> builder)
    {
        builder.ToTable("LabMetadata");

        builder.HasKey(m => m.MetadataKey);

        builder.Property(m => m.MetadataKey).HasMaxLength(64).IsRequired();

        builder.Property(m => m.MetadataValue).IsRequired();

        builder.Property(m => m.UpdatedAtUtc).HasColumnType("datetime2");
    }
}
