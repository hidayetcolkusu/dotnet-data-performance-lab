namespace DataPerformanceLab.Configuration;

public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>
    /// The single A/B factor of the cache load experiment. When false, nothing in the read
    /// path opens a Redis connection at all.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Absolute entry lifetime; there is no sliding expiration.</summary>
    public int TtlSeconds { get; init; } = 60;

    /// <summary>
    /// Namespaces the keys per database instance, so two lab databases — or two test runs —
    /// can share one Redis without reading each other's entries.
    /// </summary>
    public string InstanceId { get; init; } = "local";

    /// <summary>Bounded so an unreachable Redis fails fast into the SQL fallback.</summary>
    public int ConnectTimeoutMilliseconds { get; init; } = 1_000;

    public int OperationTimeoutMilliseconds { get; init; } = 1_000;

    /// <summary>The v1 in the key is a payload version: a DTO change can retire old entries.</summary>
    public string ProductKeyPrefix => $"dpl:{InstanceId}:product:v1";
}
