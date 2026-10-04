using System.Globalization;
using DataPerformanceLab.Data;
using DataPerformanceLab.Import;
using Microsoft.Extensions.Hosting;

namespace DataPerformanceLab.Cli.Commands;

/// <summary>Shared argument parsing and result reporting for the three import commands.</summary>
internal static class ImportCommandSupport
{
    public static int ParseBatchSize(CliArguments args)
    {
        var raw = args.GetOption("batch-size");
        if (raw is null)
        {
            return ImportBatchProcessor.DefaultBatchSize;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var batchSize))
        {
            throw new ArgumentException($"--batch-size must be an integer, was '{raw}'.");
        }

        return ImportBatchProcessor.ValidateBatchSize(batchSize);
    }

    public static Guid ParseJobId(CliArguments args)
    {
        var raw = args.GetOption("job-id")
            ?? throw new ArgumentException("--job-id <guid> is required.");

        return Guid.TryParse(raw, out var jobId)
            ? jobId
            : throw new ArgumentException($"--job-id must be a GUID, was '{raw}'.");
    }

    /// <summary>
    /// Builds the fault injector from the command line, or <see cref="ImportFaultInjector.Disabled"/>
    /// when neither option was given. <see cref="ImportFaultInjector.ForExperiment"/> refuses to arm
    /// outside the Testing environment, so a normal Development run cannot be made to crash on
    /// purpose even if these flags are passed.
    /// </summary>
    public static ImportFaultInjector ParseFaults(CliArguments args, IHostEnvironment environment)
    {
        var before = ParseBatchNumber(args, "fail-before-commit-on-batch");
        var after = ParseBatchNumber(args, "fail-after-commit-on-batch");

        if (before is null && after is null)
        {
            return ImportFaultInjector.Disabled;
        }

        return ImportFaultInjector.ForExperiment(environment, before, after);
    }

    private static int? ParseBatchNumber(CliArguments args, string optionName)
    {
        var raw = args.GetOption(optionName);
        if (raw is null)
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var batch) || batch < 1)
        {
            throw new ArgumentException($"--{optionName} must be a positive integer, was '{raw}'.");
        }

        return batch;
    }

    public static string ResolveDatabase(CliArguments args, IHostEnvironment environment) =>
        LabDatabases.ResolveTarget(
            args.GetOption("database"),
            allowTestDatabases: environment.IsEnvironment(LabEnvironment.Testing));

    public static int Report(ImportRunResult result)
    {
        switch (result)
        {
            case ImportRunResult.Busy:
                Console.Error.WriteLine(
                    "Another importer holds the import lock. Nothing was changed; retry when it finishes.");
                break;

            case ImportRunResult.FileRejected rejected:
                Console.Error.WriteLine(
                    $"The file was rejected as a whole ({rejected.Error.Code}): {rejected.Error.Message}");
                Console.Error.WriteLine("No import job was created, so there is nothing to resume.");
                break;

            case ImportRunResult.Finished finished:
                var s = finished.Summary;
                Console.WriteLine(
                    finished.Staging is StagingOutcome.AlreadyStaged
                        ? $"Job {s.JobId} was already staged; continued from its stored checkpoint."
                        : $"Job {s.JobId} staged and processed.");
                Console.WriteLine(FormattableString.Invariant(
                    $"  status={s.Status} total={s.TotalRecords} inserted={s.Inserted} skipped={s.Skipped} rejected={s.Rejected} checkpoint={s.LastProcessedRecord}"));

                if (s.Rejected > 0)
                {
                    Console.WriteLine(
                        $"  {s.Rejected} record(s) were rejected. "
                        + $"Run: dpl report --job-id {s.JobId} --output <directory>");
                }

                break;
        }

        return result.ExitCode;
    }
}

/// <summary>
/// Stages a bounded CSV file and processes it to completion under a single import lock.
/// </summary>
public sealed class ImportCommand(ILabDbContextFactory dbFactory, IHostEnvironment environment) : ICliCommand
{
    private const string HelpText = """
        import --file <path> [--batch-size 1-2000] [--database <lab database>]

        Parses, stages and processes a UTF-8 CSV file with the header
        sku,name,categoryId,unitPrice,isActive,description

        Limits: 10 MiB, 50000 records, 64 KiB per logical record.
        Import is insert-only: an existing SKU is skipped when identical and rejected when not.
        Re-running the same bytes reuses the existing job instead of creating a second one.

        Exit codes: 0 accepted, 2 completed with row rejections, 1 parse/infrastructure error,
        3 another importer holds the lock.

        Recovery experiment options (DOTNET_ENVIRONMENT=Testing only — they are refused
        anywhere else, so a normal run cannot be made to crash):
          --fail-before-commit-on-batch <n>  abort batch n before it commits
          --fail-after-commit-on-batch <n>   abort batch n after it commits
        Either leaves a resumable job; continue it with `resume --job-id <guid>`.
        """;

    public string Name => "import";

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length is 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        var parsed = new CliArguments(args, ["help"]);
        var path = parsed.GetOption("file")
            ?? throw new ArgumentException("--file <path> is required.");

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"File not found: {path}");
            return 1;
        }

        var faults = ImportCommandSupport.ParseFaults(parsed, environment);
        if (faults.IsArmed)
        {
            Console.WriteLine(
                "fault injection is armed for this run; the import is expected to abort and leave "
                + "a resumable job.");
        }

        var runner = new ImportRunner(
            dbFactory, ImportCommandSupport.ResolveDatabase(parsed, environment), faults);

        await using var stream = File.OpenRead(path);
        return ImportCommandSupport.Report(
            await runner.ImportAsync(stream, ImportCommandSupport.ParseBatchSize(parsed), ct));
    }
}

/// <summary>Continues a job from its durable checkpoint. The original file is not needed.</summary>
public sealed class ResumeCommand(ILabDbContextFactory dbFactory, IHostEnvironment environment) : ICliCommand
{
    private const string HelpText = """
        resume --job-id <guid> [--batch-size 1-2000] [--database <lab database>]

        Continues an unfinished import from the checkpoint stored in SQL.
        The original CSV file is not required. A job left in Running by a killed process is
        resumed, not treated as broken. A finished job returns its recorded result unchanged.

        Exit codes: 0 accepted, 2 completed with row rejections, 1 infrastructure error,
        3 another importer holds the lock.
        """;

    public string Name => "resume";

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length is 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        var parsed = new CliArguments(args, ["help"]);
        var runner = new ImportRunner(dbFactory, ImportCommandSupport.ResolveDatabase(parsed, environment));

        return ImportCommandSupport.Report(await runner.ResumeAsync(
            ImportCommandSupport.ParseJobId(parsed), ImportCommandSupport.ParseBatchSize(parsed), ct));
    }
}

/// <summary>Regenerates the JSON and CSV error report from the stored outcomes.</summary>
public sealed class ReportCommand(ILabDbContextFactory dbFactory, IHostEnvironment environment) : ICliCommand
{
    private const string HelpText = """
        report --job-id <guid> --output <directory> [--database <lab database>]

        Writes report.json and rejected-rows.csv from the outcomes stored in SQL.
        The original CSV file is not read. Cells that a spreadsheet would evaluate are escaped.
        """;

    public string Name => "report";

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length is 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        var parsed = new CliArguments(args, ["help"]);
        var jobId = ImportCommandSupport.ParseJobId(parsed);
        var output = parsed.GetOption("output")
            ?? throw new ArgumentException("--output <directory> is required.");

        await using var db = dbFactory.CreateForDatabase(ImportCommandSupport.ResolveDatabase(parsed, environment));
        var writer = new ImportReportWriter(db);

        var report = await writer.BuildAsync(jobId, ct);
        var files = await writer.WriteAsync(report, output, ct);

        Console.WriteLine(FormattableString.Invariant(
            $"Job {report.JobId} status={report.Status} total={report.TotalRecords} inserted={report.Inserted} skipped={report.Skipped} rejected={report.Rejected}"));
        Console.WriteLine($"Wrote {string.Join(", ", files)} to {output}");

        return report.Rejected > 0 ? 2 : 0;
    }
}
