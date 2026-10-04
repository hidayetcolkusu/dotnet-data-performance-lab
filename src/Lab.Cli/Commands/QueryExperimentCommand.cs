using System.Globalization;
using System.Text.Json;
using DataPerformanceLab.Cli.Experiments;
using DataPerformanceLab.Configuration;
using DataPerformanceLab.Data;

namespace DataPerformanceLab.Cli.Commands;

/// <summary>
/// Runs one controlled query experiment against the isolated experiment database and writes
/// the raw evidence: every timed sample, the real execution plans, the parameterized SQL,
/// the STATISTICS IO/TIME text and a run manifest.
/// </summary>
public sealed class QueryExperimentCommand(ILabDbContextFactory dbFactory) : ICliCommand
{
    private const string HelpText = """
        query --experiment index|projection --category <1-20> --output <directory> [--skip-plans]

        Runs one A/B query experiment against DataPerformanceLab_Experiment only.
        The experiment database must already be migrated and seeded.

          index       same DTO projection, candidate index absent vs present
          projection  candidate index present in both, entity materialization vs SQL projection

        Writes manifest.json, query-samples.json, and per-variant .sqlplan / .sql / .txt files.
        A failed run still writes a manifest with "status": "failed" and exits nonzero.
        """;

    public string Name => "query";

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length is 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        var parsed = new CliArguments(args, ["skip-plans", "help"]);

        var kind = ParseExperiment(parsed.GetOption("experiment"));
        var categoryId = ParseCategory(parsed.GetOption("category"));
        var outputDirectory = parsed.GetOption("output")
            ?? throw new ArgumentException("--output <directory> is required.");

        // Experiment DDL never reaches the database the API serves.
        var database = LabDataOptions.ExperimentDatabaseName;
        CatalogIndexState.EnsureExperimentDatabase(database);

        Directory.CreateDirectory(outputDirectory);

        var connectionString = dbFactory.GetConnectionString(database);
        var indexState = new CatalogIndexState(connectionString, database);

        var startedAtUtc = DateTime.UtcNow;
        var runId = ExperimentManifest.NewRunId();
        var resultFiles = new List<string>();
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["experiment"] = kind.ToString(),
            ["categoryId"] = categoryId.ToString(CultureInfo.InvariantCulture),
            ["pageSize"] = QueryVariants.PageSize.ToString(CultureInfo.InvariantCulture),
            ["warmupPerVariant"] = QueryExperimentRunner.WarmupCount.ToString(CultureInfo.InvariantCulture),
            ["rounds"] = QueryExperimentRunner.RoundCount.ToString(CultureInfo.InvariantCulture),
            ["samplesPerRoundPerVariant"] = QueryExperimentRunner.SamplesPerRound.ToString(CultureInfo.InvariantCulture),
            ["roundOrder"] = "AB/BA/AB/BA/AB"
        };

        DatasetInfo? dataset = null;
        string? failureReason = null;
        var originalIndexPresent = false;

        try
        {
            var repair = await indexState.PreflightAsync(ct);
            if (repair is not null)
            {
                Console.WriteLine($"preflight: {repair}");
                parameters["preflightRepair"] = repair;
            }

            originalIndexPresent = await indexState.ExistsAsync(ct);
            dataset = await ReadDatasetAsync(database, ct);
            parameters["indexStateBeforeRun"] = originalIndexPresent ? "present" : "absent";

            var runner = new QueryExperimentRunner(() => dbFactory.CreateForDatabase(database), indexState);
            var result = await runner.RunAsync(kind, categoryId, ct);

            if (!result.ResultsIdentical)
            {
                throw new InvalidOperationException(
                    "Variants returned different ordered results; a timing comparison would be meaningless. "
                    + string.Join(", ", result.ResultHashByVariant.Select(kv => $"{kv.Key}={kv.Value[..16]}")));
            }

            resultFiles.Add(await WriteSamplesAsync(outputDirectory, result, ct));

            if (!parsed.HasFlag("skip-plans"))
            {
                // Plan collection runs only after every timed sample, so its instrumentation
                // overhead cannot land inside the latency measurements.
                var collector = new SqlPlanCollector(connectionString);
                var (variantA, variantB) = QueryVariants.VariantsFor(kind);
                foreach (var variant in (QueryVariantId[])[variantA, variantB])
                {
                    resultFiles.AddRange(
                        await CollectAndWritePlanAsync(
                            runner, collector, database, kind, variant, categoryId, outputDirectory, ct));
                }
            }

            Console.WriteLine(
                $"{kind} experiment on category {categoryId}: {result.Samples.Count} samples, "
                + $"{result.RowCount} rows, identical result hash {result.ResultHashByVariant.Values.First()[..16]}.");
            foreach (var stats in result.RoundStats)
            {
                Console.WriteLine(FormattableString.Invariant(
                    $"  round {stats.Round} {stats.Variant,-14} median {stats.MedianMs,8:0.000} ms  p95 {stats.P95Ms,8:0.000} ms"));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Partial evidence is kept deliberately: ADR 001 forbids hiding failed runs.
            failureReason = ex.Message;
            Console.Error.WriteLine($"experiment failed: {ex.Message}");
        }
        finally
        {
            await indexState.SetIndexAsync(originalIndexPresent, CancellationToken.None);
        }

        // The full effective command, including the flag that changes what was produced, so the
        // recorded text actually reproduces this run rather than the default shape of one.
        var command = $"dpl query --experiment {kind.ToString().ToLowerInvariant()} "
            + $"--category {categoryId} --output {outputDirectory}"
            + (parsed.HasFlag("skip-plans") ? " --skip-plans" : string.Empty);

        var manifest = new ExperimentManifest(
            runId,
            $"query-{kind.ToString().ToLowerInvariant()}",
            failureReason is null ? ExperimentManifest.StatusCompleted : ExperimentManifest.StatusFailed,
            startedAtUtc,
            DateTime.UtcNow,
            database,
            ExperimentManifest.CaptureGit(Directory.GetCurrentDirectory()),
            ExperimentManifest.CaptureEnvironment(),
            dataset,
            parameters,
            resultFiles,
            command,
            failureReason,
            // Every artifact of a query run is small and published, so each gets a hash and a
            // size here too: the published bytes are then verifiable, not merely listed.
            [.. resultFiles.Select(relative => ExperimentManifest.DescribeResultFile(
                Path.Combine(outputDirectory, relative),
                relative,
                StoredResultFile.StoragePublished,
                command))]);

        await manifest.WriteAsync(Path.Combine(outputDirectory, "manifest.json"), CancellationToken.None);

        return failureReason is null ? 0 : 1;
    }

    private async Task<IEnumerable<string>> CollectAndWritePlanAsync(
        QueryExperimentRunner runner,
        SqlPlanCollector collector,
        string database,
        QueryExperimentKind kind,
        QueryVariantId variant,
        int categoryId,
        string outputDirectory,
        CancellationToken ct)
    {
        var (command, plan) = await runner.CollectPlanAsync(
            kind,
            variant,
            categoryId,
            collector,
            interceptor => dbFactory.CreateForDatabase(database, interceptor),
            ct);

        var stem = FormattableString.Invariant($"{kind.ToString().ToLowerInvariant()}-cat{categoryId}-{variant}");
        var planPath = Path.Combine(outputDirectory, stem + ".sqlplan");
        var sqlPath = Path.Combine(outputDirectory, stem + ".sql");
        var statsPath = Path.Combine(outputDirectory, stem + ".txt");

        await File.WriteAllTextAsync(planPath, plan.PlanXml, ct);
        await File.WriteAllTextAsync(sqlPath, SqlPlanCollector.FormatSqlFile(command), ct);
        await File.WriteAllTextAsync(statsPath, plan.StatisticsText, ct);

        return [Path.GetFileName(planPath), Path.GetFileName(sqlPath), Path.GetFileName(statsPath)];
    }

    private async Task<string> WriteSamplesAsync(
        string outputDirectory, QueryExperimentResult result, CancellationToken ct)
    {
        var path = Path.Combine(outputDirectory, "query-samples.json");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(
            stream,
            new
            {
                experiment = result.Kind.ToString(),
                categoryId = result.CategoryId,
                rowCount = result.RowCount,
                resultsIdentical = result.ResultsIdentical,
                resultHashByVariant = result.ResultHashByVariant,
                roundStats = result.RoundStats,
                samples = result.Samples
            },
            ExperimentManifest.JsonOptions,
            ct);

        return Path.GetFileName(path);
    }

    private async Task<DatasetInfo?> ReadDatasetAsync(string database, CancellationToken ct)
    {
        await using var db = dbFactory.CreateForDatabase(database);
        var manifest = await CatalogSeeder.TryReadManifestAsync(db, ct);
        return manifest is null
            ? throw new InvalidOperationException(
                $"'{database}' has no seed manifest. Run: dpl db migrate --database {database} "
                + $"&& dpl seed --profile default --database {database}")
            : DatasetInfo.FromSeedManifest(manifest);
    }

    private static QueryExperimentKind ParseExperiment(string? value) => value?.ToLowerInvariant() switch
    {
        "index" => QueryExperimentKind.Index,
        "projection" => QueryExperimentKind.Projection,
        _ => throw new ArgumentException("--experiment must be 'index' or 'projection'.")
    };

    private static int ParseCategory(string? value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var categoryId)
            || categoryId is < 1 or > 20)
        {
            throw new ArgumentException("--category must be an integer between 1 and 20.");
        }

        return categoryId;
    }
}
