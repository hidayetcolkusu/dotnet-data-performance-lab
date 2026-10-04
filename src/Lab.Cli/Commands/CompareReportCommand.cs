using System.Text.Json;
using DataPerformanceLab.Cli.Experiments;

namespace DataPerformanceLab.Cli.Commands;

/// <summary>
/// Aggregates the k6 summaries a cache off/on comparison produced into comparison.json and
/// comparison.md. Kept out of PowerShell on purpose: the aggregation rules — one row per run,
/// no pooled percentile, invalid runs surfaced rather than averaged — are unit tested.
/// </summary>
public sealed class CompareReportCommand : ICliCommand
{
    private const string HelpText = """
        compare-report --input <run-directory>

        Reads every runs/<repetition>-<order>-<arm>.summary.json in the run directory and
        writes comparison.json and comparison.md next to them.

        Exit codes:
          0  every run is valid and the arms are balanced
          1  the input directory or its run index is unusable
          2  the comparison is invalid (missing artifacts, dropped iterations, failed checks)
        """;

    public string Name => "compare-report";

    /// <summary>
    /// The run index compare-cache.ps1 writes: which summary file belongs to which
    /// repetition, order and arm. Reading it beats inferring the layout from file names.
    /// </summary>
    private sealed record RunIndexEntry(
        int Repetition,
        string Order,
        string Arm,
        string SummaryFile,
        int? WarmupExitCode = null,
        int? MeasuredExitCode = null,
        IReadOnlyList<string>? RequiredArtifacts = null);

    /// <summary>
    /// <c>Expected</c> is what the orchestrator declared before measuring. It is optional so a
    /// run directory written by an older version of the script still reports, but the report then
    /// says which checks it could not perform rather than passing them silently.
    /// </summary>
    private sealed record RunIndex(
        string Schema,
        IReadOnlyList<RunIndexEntry> Runs,
        LoadRunExpectation? Expected = null)
    {
        public const string ExpectedSchema = "dpl.load-run-index.v1";
    }

    /// <summary>
    /// Reads the measured window's total lookups from the run's cache-metrics delta, so the counters
    /// can be checked against the request count the same run reported. Returns null when the run has
    /// no delta file — a directory written before the two-snapshot orchestration existed still
    /// reports, it just cannot be held to this check.
    /// </summary>
    private static async Task<long?> ReadMeasuredWindowLookupsAsync(
        string inputDirectory, RunIndexEntry entry, CancellationToken ct)
    {
        // runs/<stem>.summary.json -> runs/<stem>.cache-metrics-delta.json
        const string summarySuffix = ".summary.json";
        if (!entry.SummaryFile.EndsWith(summarySuffix, StringComparison.Ordinal))
        {
            return null;
        }

        var deltaRelative = entry.SummaryFile[..^summarySuffix.Length] + ".cache-metrics-delta.json";
        var path = Path.Combine(inputDirectory, deltaRelative);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
            return document.RootElement.TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("totalLookups", out var total)
                && total.TryGetInt64(out var lookups)
                    ? lookups
                    : null;
        }
        catch (JsonException)
        {
            // An unreadable delta is not a lookup count; the missing-artifact check covers its
            // absence, and inventing a number here would be worse than declining to check.
            return null;
        }
    }

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length is 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        var parsed = new CliArguments(args, ["help"]);
        var inputDirectory = parsed.GetOption("input")
            ?? throw new ArgumentException("--input <run-directory> is required.");

        if (!Directory.Exists(inputDirectory))
        {
            Console.Error.WriteLine($"Run directory '{inputDirectory}' does not exist.");
            return 1;
        }

        var indexPath = Path.Combine(inputDirectory, "runs.json");
        if (!File.Exists(indexPath))
        {
            Console.Error.WriteLine(
                $"'{indexPath}' is missing; compare-cache.ps1 writes it as runs complete. "
                + "Without it there is no record of which runs were supposed to exist.");
            return 1;
        }

        var index = JsonSerializer.Deserialize<RunIndex>(
            await File.ReadAllTextAsync(indexPath, ct), ExperimentManifest.JsonOptions);

        if (index is null || index.Schema != RunIndex.ExpectedSchema)
        {
            Console.Error.WriteLine($"'{indexPath}' is not a {RunIndex.ExpectedSchema} document.");
            return 1;
        }

        var records = new List<LoadRunRecord>(index.Runs.Count);
        foreach (var entry in index.Runs)
        {
            var path = Path.Combine(inputDirectory, entry.SummaryFile);
            var json = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;

            // The index names the small artifacts the orchestrator promised; their absence is
            // checked here rather than trusted, and large raw streams are never on that list.
            var missing = (entry.RequiredArtifacts ?? [])
                .Where(relative => !File.Exists(Path.Combine(inputDirectory, relative)))
                .ToList();

            var lookups = await ReadMeasuredWindowLookupsAsync(inputDirectory, entry, ct);

            records.Add(LoadComparison.ReadRun(
                new LoadRunInput(
                    entry.Repetition,
                    entry.Order,
                    entry.Arm,
                    entry.SummaryFile,
                    entry.WarmupExitCode,
                    entry.MeasuredExitCode,
                    entry.RequiredArtifacts,
                    lookups),
                json,
                index.Expected,
                missing));
        }

        var report = LoadComparison.BuildReport(records, index.Expected);

        if (index.Expected is null)
        {
            Console.Error.WriteLine(
                "note: this run directory declares no expected workload (no 'expected' block in "
                + "runs.json), so the declared-repetition and declared-workload checks were not "
                + "performed. The runs were still checked against each other.");
        }

        var jsonPath = Path.Combine(inputDirectory, "comparison.json");
        await using (var stream = File.Create(jsonPath))
        {
            await JsonSerializer.SerializeAsync(stream, report, ExperimentManifest.JsonOptions, ct);
        }

        var markdownPath = Path.Combine(inputDirectory, "comparison.md");
        await File.WriteAllTextAsync(markdownPath, LoadComparison.ToMarkdown(report), ct);

        Console.WriteLine(LoadComparison.ToMarkdown(report));
        Console.WriteLine($"Wrote {jsonPath} and {markdownPath}.");

        if (report.Status == LoadComparisonReport.StatusValid)
        {
            return 0;
        }

        Console.Error.WriteLine("The comparison is invalid; do not quote these numbers as a result.");
        return 2;
    }
}
