using System.Diagnostics;
using DataPerformanceLab.Data;
using Microsoft.EntityFrameworkCore;

namespace DataPerformanceLab.Cli.Experiments;

public sealed record QuerySample(
    int Round,
    string RoundOrder,
    QueryVariantId Variant,
    int SampleIndex,
    double ElapsedMs,
    int RowCount,
    string ResultHash);

public sealed record VariantRoundStats(
    int Round,
    QueryVariantId Variant,
    int SampleCount,
    double MinMs,
    double MeanMs,
    double MedianMs,
    double P95Ms,
    double MaxMs);

public sealed record QueryExperimentResult(
    QueryExperimentKind Kind,
    int CategoryId,
    IReadOnlyList<QuerySample> Samples,
    IReadOnlyList<VariantRoundStats> RoundStats,
    IReadOnlyDictionary<string, string> ResultHashByVariant,
    bool ResultsIdentical,
    int RowCount);

/// <summary>
/// Implements the timing protocol: 5 warmup executions per variant, then 5 rounds
/// of 20 sequential measurements per variant, with the round order alternating AB/BA.
/// Every measurement uses a fresh DbContext and materializes the result completely.
/// </summary>
public sealed class QueryExperimentRunner(
    Func<LabDbContext> contextFactory,
    CatalogIndexState indexState)
{
    public const int WarmupCount = 5;

    public const int RoundCount = 5;

    public const int SamplesPerRound = 20;

    public async Task<QueryExperimentResult> RunAsync(
        QueryExperimentKind kind,
        int categoryId,
        CancellationToken ct)
    {
        var (variantA, variantB) = QueryVariants.VariantsFor(kind);
        var samples = new List<QuerySample>(RoundCount * SamplesPerRound * 2);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var rowCount = 0;

        foreach (var variant in (QueryVariantId[])[variantA, variantB])
        {
            await ApplyVariantPrerequisiteAsync(kind, variant, ct);
            for (var i = 0; i < WarmupCount; i++)
            {
                await MeasureOnceAsync(variant, categoryId, ct);
            }
        }

        for (var round = 1; round <= RoundCount; round++)
        {
            // AB / BA / AB / BA / AB — alternating so a monotonic drift in machine state
            // cannot be mistaken for a difference between the variants.
            var forward = round % 2 == 1;
            var order = forward ? new[] { variantA, variantB } : [variantB, variantA];
            var orderLabel = forward ? "AB" : "BA";

            foreach (var variant in order)
            {
                await ApplyVariantPrerequisiteAsync(kind, variant, ct);

                for (var i = 1; i <= SamplesPerRound; i++)
                {
                    var (elapsedMs, count, hash) = await MeasureOnceAsync(variant, categoryId, ct);
                    samples.Add(new QuerySample(round, orderLabel, variant, i, elapsedMs, count, hash));
                    hashes[variant.ToString()] = hash;
                    rowCount = count;
                }
            }
        }

        var roundStats = samples
            .GroupBy(s => (s.Round, s.Variant))
            .Select(g => Summarize(g.Key.Round, g.Key.Variant, [.. g.Select(s => s.ElapsedMs)]))
            .OrderBy(s => s.Round)
            .ThenBy(s => s.Variant)
            .ToList();

        return new QueryExperimentResult(
            kind,
            categoryId,
            samples,
            roundStats,
            hashes,
            hashes.Values.Distinct(StringComparer.Ordinal).Count() == 1,
            rowCount);
    }

    private async Task<(double ElapsedMs, int RowCount, string Hash)> MeasureOnceAsync(
        QueryVariantId variant, int categoryId, CancellationToken ct)
    {
        await using var db = contextFactory();
        var stopwatch = Stopwatch.StartNew();
        var items = await QueryVariants.ExecuteAsync(db, variant, categoryId, ct);
        stopwatch.Stop();

        return (stopwatch.Elapsed.TotalMilliseconds, items.Count, QueryVariants.HashResults(items));
    }

    /// <summary>
    /// Q1 changes exactly one factor — whether the candidate index exists. Q2 keeps the index
    /// present on both sides so the projection difference is not blended with an index difference.
    /// </summary>
    private Task ApplyVariantPrerequisiteAsync(QueryExperimentKind kind, QueryVariantId variant, CancellationToken ct) =>
        kind switch
        {
            QueryExperimentKind.Index => indexState.SetIndexAsync(variant is QueryVariantId.IndexPresent, ct),
            QueryExperimentKind.Projection => indexState.SetIndexAsync(true, ct),
            _ => Task.CompletedTask
        };

    /// <summary>Captures the SQL EF sends for a variant, then collects its real plan and IO statistics.</summary>
    public async Task<(CapturedCommand Command, PlanCollectionResult Plan)> CollectPlanAsync(
        QueryExperimentKind kind,
        QueryVariantId variant,
        int categoryId,
        SqlPlanCollector collector,
        Func<CommandCaptureInterceptor, LabDbContext> instrumentedContextFactory,
        CancellationToken ct)
    {
        await ApplyVariantPrerequisiteAsync(kind, variant, ct);

        var interceptor = new CommandCaptureInterceptor();
        await using (var db = instrumentedContextFactory(interceptor))
        {
            await QueryVariants.ExecuteAsync(db, variant, categoryId, ct);
        }

        var captured = interceptor.LastCommand
            ?? throw new InvalidOperationException($"No SQL command was captured for variant {variant}.");

        return (captured, await collector.CollectAsync(captured, ct));
    }

    public static VariantRoundStats Summarize(int round, QueryVariantId variant, double[] samples)
    {
        var sorted = samples.Order().ToArray();
        return new VariantRoundStats(
            round,
            variant,
            sorted.Length,
            sorted[0],
            samples.Average(),
            Percentile(sorted, 50),
            Percentile(sorted, 95),
            sorted[^1]);
    }

    /// <summary>Nearest-rank percentile over an already sorted sample array.</summary>
    public static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
        {
            throw new ArgumentException("Cannot take a percentile of an empty sample set.", nameof(sorted));
        }

        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}
