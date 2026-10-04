using DataPerformanceLab.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataPerformanceLab.Data.Configurations;

public sealed class ImportJobConfiguration : IEntityTypeConfiguration<ImportJob>
{
    public void Configure(EntityTypeBuilder<ImportJob> builder)
    {
        builder.ToTable("ImportJobs");

        builder.HasKey(j => j.Id);

        builder.Property(j => j.Id).ValueGeneratedNever();

        builder.Property(j => j.FileHash).HasColumnType("char(64)").IsRequired();

        builder.Property(j => j.ParserVersion);

        builder.HasIndex(j => new { j.FileHash, j.ParserVersion }).IsUnique();

        builder.Property(j => j.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(j => j.CreatedAtUtc).HasColumnType("datetime2");

        builder.Property(j => j.CompletedAtUtc).HasColumnType("datetime2");

        builder.Property(j => j.LastErrorCode).HasMaxLength(128);

        builder.HasMany(j => j.Rows)
            .WithOne(r => r.Job!)
            .HasForeignKey(r => r.JobId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
