using System.Globalization;
using DataPerformanceLab.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DataPerformanceLab.IntegrationTests;

/// <summary>
/// Boots the real API against a test database.
///
/// Redis defaults to a closed port, so a test that does not care about caching gets the SQL
/// fallback path deterministically instead of depending on whether a Redis container happens
/// to be running. Tests that do care pass a real endpoint.
/// </summary>
public sealed class LabApiFactory(
    SqlServerFixture sql,
    string databaseName,
    bool cacheEnabled = true,
    string redisHost = "127.0.0.1",
    int redisPort = 1) : WebApplicationFactory<Program>
{
    /// <summary>A fresh key namespace per factory, so no two tests share cache entries.</summary>
    public string CacheInstanceId { get; } = "apitest-" + Guid.NewGuid().ToString("N")[..8];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Data:DatabaseName"] = databaseName,
                ["Data:SqlServer:Host"] = sql.Host,
                ["Data:SqlServer:Port"] = sql.Port.ToString(CultureInfo.InvariantCulture),
                ["Data:SqlServer:PasswordOverride"] = sql.Password,
                ["Data:Redis:Host"] = redisHost,
                ["Data:Redis:Port"] = redisPort.ToString(CultureInfo.InvariantCulture),
                ["Cache:Enabled"] = cacheEnabled ? "true" : "false",
                ["Cache:InstanceId"] = CacheInstanceId,
                // Short timeouts keep a test against a closed port fast.
                ["Cache:ConnectTimeoutMilliseconds"] = "500",
                ["Cache:OperationTimeoutMilliseconds"] = "500"
            });
        });
    }
}

public sealed class UnreachableSqlApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Data:DatabaseName"] = LabDataOptions.TestDatabasePrefix + "unreachable",
                ["Data:SqlServer:Host"] = "127.0.0.1",
                ["Data:SqlServer:Port"] = "1",
                ["Data:SqlServer:PasswordOverride"] = "irrelevant",
                ["Cache:Enabled"] = "false"
            });
        });
    }
}
