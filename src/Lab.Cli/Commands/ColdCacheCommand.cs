using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using DataPerformanceLab.Caching;
using DataPerformanceLab.Cli.Experiments;
using DataPerformanceLab.Configuration;
using Microsoft.Extensions.Options;

namespace DataPerformanceLab.Cli.Commands;

/// <summary>
/// The cold-cache read experiment (plan step 10): one SKU's entry is evicted, then the same SKU is
/// read twice against a running API. The first read must miss and fall through to SQL; the second
/// must hit.
///
/// Deliberately separate from the warm load comparison. Mixing a cold first read into a
/// sixty-second warm run would dilute it into invisibility, and the warm run's numbers would then
/// describe neither state.
///
/// Only the lab's own key for the requested SKU is deleted. There is no FLUSHALL and no pattern
/// sweep: the cache may be shared with something else on the machine, and a measurement is not a
/// licence to clear somebody else's data.
/// </summary>
public sealed class ColdCacheCommand(
    IProductCache cache,
    IOptions<CacheOptions> cacheOptions) : ICliCommand
{
    private const string HelpText = """
        cold-cache --sku <sku> --output <directory> [--base-url http://127.0.0.1:8080] [--reads 3]

        Evicts the lab's cache entry for one SKU, then reads that SKU from a running API and
        records the cache counter delta of each read.

        Requires Cache:Enabled=true and a running API on --base-url (start it with the same
        Cache:InstanceId this CLI is configured with, or the key namespaces will not match).

        Exit codes:
          0  the first read missed and every later read hit
          1  configuration, connectivity or argument error
          2  the reads did not show the cold-then-warm pattern
        """;

    public string Name => "cold-cache";

    private sealed record CacheCounters(
        long Hits,
        long Misses,
        long Bypasses,
        long SqlFallbacks,
        long ReadFailures)
    {
        public CacheCounters Minus(CacheCounters earlier) => new(
            Hits - earlier.Hits,
            Misses - earlier.Misses,
            Bypasses - earlier.Bypasses,
            SqlFallbacks - earlier.SqlFallbacks,
            ReadFailures - earlier.ReadFailures);
    }

    private sealed record ReadObservation(
        int ReadNumber,
        string Expectation,
        int StatusCode,
        double ElapsedMs,
        CacheCounters Delta)
    {
        /// <summary>
        /// A read is as expected when the counters say so. Latency is recorded but never used as
        /// the test: a single request's timing on a developer machine proves nothing.
        /// </summary>
        public bool MatchedExpectation =>
            StatusCode == 200
            && Expectation switch
            {
                "miss" => Delta is { Misses: 1, Hits: 0 },
                _ => Delta is { Hits: 1, Misses: 0 }
            };
    }

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length is 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        var parsed = new CliArguments(args, ["help"]);
        var sku = parsed.GetOption("sku")?.Trim().ToUpperInvariant()
            ?? throw new ArgumentException("--sku <sku> is required.");
        var outputDirectory = parsed.GetOption("output")
            ?? throw new ArgumentException("--output <directory> is required.");
        var baseUrl = (parsed.GetOption("base-url") ?? "http://127.0.0.1:8080").TrimEnd('/');
        var reads = ParseReads(parsed.GetOption("reads"));

        if (!cache.Enabled)
        {
            Console.Error.WriteLine(
                "Cache:Enabled is false for this CLI process, so there is no cache entry to evict. "
                + "This experiment only has meaning with the cache on.");
            return 1;
        }

        Directory.CreateDirectory(outputDirectory);

        var startedAtUtc = DateTime.UtcNow;
        var runId = ExperimentManifest.NewRunId();
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };

        var observations = new List<ReadObservation>();
        string? failureReason = null;

        try
        {
            await AssertApiIsReadyAsync(http, baseUrl, ct);

            // The only mutation this command performs, and it is scoped to one key.
            await cache.InvalidateAsync(sku, ct);
            Console.WriteLine($"Evicted the lab cache entry for '{sku}'.");

            var before = await ReadCountersAsync(http, ct);

            for (var read = 1; read <= reads; read++)
            {
                var expectation = read == 1 ? "miss" : "hit";
                var (status, elapsedMs) = await TimeProductReadAsync(http, sku, ct);
                var after = await ReadCountersAsync(http, ct);

                var observation = new ReadObservation(
                    read, expectation, status, elapsedMs, after.Minus(before));
                observations.Add(observation);
                before = after;

                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"""
                      read {read} ({expectation}): HTTP {status}, {elapsedMs:0.00} ms, hits+{observation.Delta.Hits} misses+{observation.Delta.Misses} sqlFallbacks+{observation.Delta.SqlFallbacks} -> {(observation.MatchedExpectation ? "as expected" : "NOT as expected")}
                      """));
            }

            if (observations.Any(o => !o.MatchedExpectation))
            {
                failureReason = "At least one read did not show the expected cold-then-warm pattern.";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failureReason = ex.Message;
            Console.Error.WriteLine($"cold-cache experiment failed: {ex.Message}");
        }

        var reportPath = Path.Combine(outputDirectory, "cold-cache.json");
        await WriteReportAsync(reportPath, runId, sku, baseUrl, observations, failureReason, ct);

        var command = $"dpl cold-cache --sku {sku} --output {outputDirectory} "
            + $"--base-url {baseUrl} --reads {reads}";

        var manifest = new ExperimentManifest(
            runId,
            "cache-cold-read",
            failureReason is null ? ExperimentManifest.StatusCompleted : ExperimentManifest.StatusFailed,
            startedAtUtc,
            DateTime.UtcNow,
            LabDataOptions.ApiDatabaseName,
            ExperimentManifest.CaptureGit(Directory.GetCurrentDirectory()),
            ExperimentManifest.CaptureEnvironment(),
            null,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sku"] = sku,
                ["baseUrl"] = baseUrl,
                ["reads"] = reads.ToString(CultureInfo.InvariantCulture),
                ["cacheTtlSeconds"] = cacheOptions.Value.TtlSeconds.ToString(CultureInfo.InvariantCulture),
                ["cacheInstanceId"] = cacheOptions.Value.InstanceId,
                ["evictionScope"] = "one key: the requested SKU in this lab's namespace",
                // Said plainly in the evidence, because the opposite reading is the easy one.
                ["coldnessScope"] =
                    "Redis only. SQL Server's buffer pool, its plan cache and the .NET JIT are all "
                    + "still warm, so this is not a cold-database measurement."
            },
            [Path.GetFileName(reportPath)],
            command,
            failureReason,
            [ExperimentManifest.DescribeResultFile(
                reportPath, Path.GetFileName(reportPath), StoredResultFile.StoragePublished, command)]);

        await manifest.WriteAsync(Path.Combine(outputDirectory, "manifest.json"), CancellationToken.None);

        Console.WriteLine($"Wrote {reportPath} and manifest.json.");
        Console.WriteLine(
            "Note: only Redis was cold. SQL's buffer pool and plan cache stayed warm, so the first "
            + "read is not a cold-database read.");

        if (failureReason is null)
        {
            return 0;
        }

        // A connectivity or configuration problem is a 1; reads that ran but disagreed are a 2.
        return observations.Count == 0 ? 1 : 2;
    }

    private static async Task AssertApiIsReadyAsync(HttpClient http, string baseUrl, CancellationToken ct)
    {
        using var response = await http.GetAsync("/health/ready", ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"{baseUrl}/health/ready returned {(int)response.StatusCode}; start the API first.");
        }

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (body.TryGetProperty("cache", out var cacheStatus)
            && cacheStatus.GetString() is { } status and not "reachable")
        {
            throw new InvalidOperationException(
                $"The API reports its cache as '{status}'. A cold-read experiment needs a reachable, "
                + "enabled cache on the API side too.");
        }
    }

    private static async Task<(int StatusCode, double ElapsedMs)> TimeProductReadAsync(
        HttpClient http, string sku, CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var response = await http.GetAsync($"/api/products/{Uri.EscapeDataString(sku)}", ct);
        // Drain the body before stopping the clock: a measurement that ignores the response
        // content is timing the headers.
        _ = await response.Content.ReadAsByteArrayAsync(ct);
        stopwatch.Stop();

        return ((int)response.StatusCode, stopwatch.Elapsed.TotalMilliseconds);
    }

    private static async Task<CacheCounters> ReadCountersAsync(HttpClient http, CancellationToken ct)
    {
        var body = await http.GetFromJsonAsync<JsonElement>("/internal/cache-metrics", ct);

        return new CacheCounters(
            body.GetProperty("hits").GetInt64(),
            body.GetProperty("misses").GetInt64(),
            body.GetProperty("bypasses").GetInt64(),
            body.GetProperty("sqlFallbacks").GetInt64(),
            body.GetProperty("readFailures").GetInt64());
    }

    private async Task WriteReportAsync(
        string path,
        string runId,
        string sku,
        string baseUrl,
        IReadOnlyList<ReadObservation> observations,
        string? failureReason,
        CancellationToken ct)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(
            stream,
            new
            {
                schema = "dpl.cold-cache.v1",
                runId,
                sku,
                baseUrl,
                cache = new
                {
                    ttlSeconds = cacheOptions.Value.TtlSeconds,
                    instanceId = cacheOptions.Value.InstanceId
                },
                evictionScope = "one key: the requested SKU in this lab's namespace. No FLUSHALL.",
                coldnessScope =
                    "Redis only. SQL Server's buffer pool and plan cache, and the .NET JIT, were "
                    + "warm throughout; a cold Redis read is not a cold-database read.",
                reads = observations,
                status = failureReason is null ? "completed" : "failed",
                failureReason
            },
            ExperimentManifest.JsonOptions,
            ct);
    }

    private static int ParseReads(string? value)
    {
        if (value is null)
        {
            return 3;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var reads)
            || reads is < 2 or > 50)
        {
            throw new ArgumentException("--reads must be an integer between 2 and 50.");
        }

        return reads;
    }
}
