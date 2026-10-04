using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace DataPerformanceLab.IntegrationTests;

public sealed class RedisContainerSmokeTests
{
    [Fact]
    public async Task RedisContainer_Starts_AndRespondsToPing()
    {
        await using var container = new RedisBuilder("redis:7.4").Build();
        await container.StartAsync();

        var endpoint = $"{container.Hostname}:{container.GetMappedPublicPort(6379)}";
        await using var mux = await ConnectionMultiplexer.ConnectAsync(endpoint);

        var latency = await mux.GetDatabase().PingAsync();

        Assert.True(latency >= TimeSpan.Zero);
    }
}
