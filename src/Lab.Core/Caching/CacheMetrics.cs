namespace DataPerformanceLab.Caching;

public sealed record CacheMetricsSnapshot(
    long Hits,
    long Misses,
    long Bypasses,
    long ReadFailures,
    long WriteFailures,
    long InvalidationFailures,
    long SqlFallbacks)
{
    public long TotalLookups => Hits + Misses + Bypasses;

    /// <summary>
    /// Hit rate over cache-eligible lookups only. Bypasses (cache off) are excluded because a
    /// run with the cache disabled has no hit rate to speak of.
    /// </summary>
    public double? HitRate => Hits + Misses == 0 ? null : (double)Hits / (Hits + Misses);

    public CacheMetricsSnapshot Delta(CacheMetricsSnapshot earlier) => new(
        Hits - earlier.Hits,
        Misses - earlier.Misses,
        Bypasses - earlier.Bypasses,
        ReadFailures - earlier.ReadFailures,
        WriteFailures - earlier.WriteFailures,
        InvalidationFailures - earlier.InvalidationFailures,
        SqlFallbacks - earlier.SqlFallbacks);
}

/// <summary>
/// Process-wide cache counters.
///
/// Deliberately unlabelled: a per-SKU label would be unbounded cardinality on a catalog of
/// 100,000 products. Correlating an individual key belongs in a trace or a log line, not here.
/// </summary>
public sealed class CacheMetrics
{
    private long _hits;
    private long _misses;
    private long _bypasses;
    private long _readFailures;
    private long _writeFailures;
    private long _invalidationFailures;
    private long _sqlFallbacks;

    public void RecordHit() => Interlocked.Increment(ref _hits);

    public void RecordMiss() => Interlocked.Increment(ref _misses);

    /// <summary>The cache was off, so the lookup never went to Redis at all.</summary>
    public void RecordBypass() => Interlocked.Increment(ref _bypasses);

    public void RecordReadFailure() => Interlocked.Increment(ref _readFailures);

    public void RecordWriteFailure() => Interlocked.Increment(ref _writeFailures);

    public void RecordInvalidationFailure() => Interlocked.Increment(ref _invalidationFailures);

    public void RecordSqlFallback() => Interlocked.Increment(ref _sqlFallbacks);

    public CacheMetricsSnapshot Snapshot() => new(
        Interlocked.Read(ref _hits),
        Interlocked.Read(ref _misses),
        Interlocked.Read(ref _bypasses),
        Interlocked.Read(ref _readFailures),
        Interlocked.Read(ref _writeFailures),
        Interlocked.Read(ref _invalidationFailures),
        Interlocked.Read(ref _sqlFallbacks));
}
