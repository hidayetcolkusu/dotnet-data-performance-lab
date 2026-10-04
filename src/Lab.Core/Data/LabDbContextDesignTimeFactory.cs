using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DataPerformanceLab.Data;

public sealed class LabDbContextDesignTimeFactory : IDesignTimeDbContextFactory<LabDbContext>
{
    public LabDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LabDbContext>()
            .UseSqlServer(
                "Server=127.0.0.1,1433;Database=DesignTime;User ID=sa;Password=design-time-placeholder;" +
                "Encrypt=True;TrustServerCertificate=True")
            .Options;

        return new LabDbContext(options);
    }
}
