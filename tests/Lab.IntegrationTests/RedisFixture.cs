using Testcontainers.Redis;
using Xunit;

namespace DataPerformanceLab.IntegrationTests;

/// <summary>
/// A real Redis 7.4 container shared by the cache tests. Each test uses its own key namespace
/// via <c>Cache:InstanceId</c>, so tests never read each other's entries and no test ever needs
/// a global flush.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    /// <summary>The same digest <c>compose.yaml</c> pins; the tag alone could move under the tests.</summary>
    public const string Image =
        "redis:7.4@sha256:71da9275c5f3fcb97d0fa0c8c5b36cc995327265420f17a04bfd544f458059f7";

    private readonly RedisContainer _container = new RedisBuilder(Image).Build();

    public Task InitializeAsync() =>
        DockerRequirement.StartAsync("Redis 7.4", () => _container.StartAsync());

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public string Host => _container.Hostname;

    public int Port => _container.GetMappedPublicPort(6379);

    public string Endpoint => $"{Host}:{Port}";

    /// <summary>
    /// Freezes the container: connections stay open but nothing answers, which is an outage the
    /// client has to detect by timeout. The mapped port survives, unlike a stop/start.
    /// </summary>
    public Task PauseAsync() => _container.PauseAsync();

    public Task UnpauseAsync() => _container.UnpauseAsync();
}

/// <summary>
/// Cache tests need both containers. They share one collection so SQL Server and Redis start
/// once, and so no two of them run in parallel against the same database.
/// </summary>
[CollectionDefinition("sql+redis")]
public sealed class SqlRedisCollection : ICollectionFixture<SqlServerFixture>, ICollectionFixture<RedisFixture>;
