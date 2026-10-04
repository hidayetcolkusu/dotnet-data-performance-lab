using DataPerformanceLab.Catalog;
using DataPerformanceLab.Import;
using Microsoft.EntityFrameworkCore;

namespace DataPerformanceLab.Data;

public sealed class LabDbContext(DbContextOptions<LabDbContext> options) : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ImportJob> ImportJobs => Set<ImportJob>();

    public DbSet<ImportRow> ImportRows => Set<ImportRow>();

    public DbSet<LabMetadataEntry> LabMetadata => Set<LabMetadataEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LabDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
