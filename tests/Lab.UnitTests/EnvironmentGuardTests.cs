using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DataPerformanceLab.UnitTests;

public sealed class EnvironmentGuardTests
{
    private static WebApplicationFactory<Program> CreateFactory(string environment) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment(environment));

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void LocalLabEnvironments_StartSuccessfully(string environment)
    {
        using var factory = CreateFactory(environment);
        using var client = factory.CreateClient();
        Assert.NotNull(client);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void OtherEnvironments_FailAtStartup(string environment)
    {
        using var factory = CreateFactory(environment);

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        Assert.Contains("Local lab requires Development or Testing", exception.ToString());
    }
}
