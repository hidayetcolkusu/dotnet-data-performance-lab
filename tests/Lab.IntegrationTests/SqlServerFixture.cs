using DataPerformanceLab.Configuration;
using DataPerformanceLab.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Testcontainers.MsSql;
using Xunit;

namespace DataPerformanceLab.IntegrationTests;

public sealed class SqlServerFixture : IAsyncLifetime
{
    /// <summary>The same digest <c>compose.yaml</c> pins; the tag alone could move under the tests.</summary>
    public const string Image =
        "mcr.microsoft.com/mssql/server:2022-latest@sha256:97b448857967be55e005424a660056fe6d51814435804dc07e8f79f028bab5fb";

    private readonly MsSqlContainer _container = new MsSqlBuilder(Image)
        .WithPassword("Test_ContaineR_Passw0rd!")
        .Build();

    public Task InitializeAsync() =>
        DockerRequirement.StartAsync("SQL Server 2022", () => _container.StartAsync());

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public string MasterConnectionString =>
        new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = "master" }
            .ConnectionString;

    public string Host => _container.Hostname;

    public int Port => _container.GetMappedPublicPort(1433);

    public string Password => "Test_ContaineR_Passw0rd!";

    public async Task<string> CreateAndMigrateTestDatabaseAsync()
    {
        var name = LabDataOptions.TestDatabasePrefix + Guid.NewGuid().ToString("N")[..12];
        await LabDbMigrator.EnsureLabDatabaseAsync(MasterConnectionString, name, CancellationToken.None);
        return name;
    }

    public string ConnectionStringFor(string databaseName) =>
        new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = databaseName
        }.ConnectionString;

    public LabDbContext CreateContext(string databaseName) =>
        new(new DbContextOptionsBuilder<LabDbContext>()
            .UseSqlServer(ConnectionStringFor(databaseName))
            .Options);

    public LabDbContext CreateContext(string databaseName, params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<LabDbContext>()
            .UseSqlServer(ConnectionStringFor(databaseName));
        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }

        return new LabDbContext(builder.Options);
    }
}

[CollectionDefinition("sql")]
public sealed class SqlCollection : ICollectionFixture<SqlServerFixture>;
