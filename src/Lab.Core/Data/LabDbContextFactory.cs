using DataPerformanceLab.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace DataPerformanceLab.Data;

public interface ILabDbContextFactory
{
    string MasterConnectionString { get; }

    /// <summary>
    /// Raw connection string for a lab database. Needed by the experiment tooling, which
    /// talks to SqlClient directly to collect plans and statistics. Never written to a manifest.
    /// </summary>
    string GetConnectionString(string databaseName);

    LabDbContext CreateForDatabase(string databaseName);

    /// <summary>Creates a context with extra interceptors, used to capture the SQL EF sends.</summary>
    LabDbContext CreateForDatabase(string databaseName, params IInterceptor[] interceptors);
}

public sealed class LabDbContextFactory(IOptions<LabDataOptions> options) : ILabDbContextFactory
{
    public string MasterConnectionString => options.Value.SqlServer.GetMasterConnectionString();

    public string GetConnectionString(string databaseName) =>
        options.Value.SqlServer.GetConnectionString(databaseName);

    public LabDbContext CreateForDatabase(string databaseName) =>
        CreateForDatabase(databaseName, []);

    public LabDbContext CreateForDatabase(string databaseName, params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<LabDbContext>()
            .UseSqlServer(GetConnectionString(databaseName));

        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }

        return new LabDbContext(builder.Options);
    }
}
