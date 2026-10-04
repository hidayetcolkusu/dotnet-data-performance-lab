using DataPerformanceLab;
using DataPerformanceLab.Cli;
using DataPerformanceLab.Cli.Commands;
using DataPerformanceLab.Data;
using DataPerformanceLab.Import;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Length is 0 || args[0] is "--help" or "-h" or "help")
{
    PrintHelp();
    return 0;
}

var commandName = args[0].ToLowerInvariant();

var builder = Host.CreateApplicationBuilder();

LabEnvironment.EnsureLocalLab(builder.Environment);

builder.Services.AddLabCore(builder.Configuration);
builder.Services.AddTransient<ICliCommand, DbCommand>();
builder.Services.AddTransient<ICliCommand, SeedCommand>();
builder.Services.AddTransient<ICliCommand, QueryExperimentCommand>();
builder.Services.AddTransient<ICliCommand, ImportCommand>();
builder.Services.AddTransient<ICliCommand, ResumeCommand>();
builder.Services.AddTransient<ICliCommand, ReportCommand>();
builder.Services.AddTransient<ICliCommand, LoadFixtureCommand>();
builder.Services.AddTransient<ICliCommand, CompareReportCommand>();
builder.Services.AddTransient<ICliCommand, ColdCacheCommand>();

using var host = builder.Build();

var command = host.Services.GetServices<ICliCommand>()
    .FirstOrDefault(c => c.Name.Equals(commandName, StringComparison.OrdinalIgnoreCase));

if (command is null)
{
    Console.Error.WriteLine($"Unknown command '{args[0]}'. Run with --help for usage.");
    return 1;
}

try
{
    return await command.ExecuteAsync(args[1..], CancellationToken.None);
}
catch (ImportInterruptedException ex)
{
    // The job id is the actionable part, so it goes on its own line rather than buried in prose.
    Console.Error.WriteLine($"error: {ex.Message}");
    Console.Error.WriteLine($"Job {ex.JobId} is resumable.");
    return 1;
}
catch (NotALabDatabaseException ex)
{
    // The database was left untouched; say so, because the alternative reading of a failed
    // reset is "it half-dropped my data".
    Console.Error.WriteLine($"error: {ex.Message}");
    Console.Error.WriteLine("No schema or data was changed.");
    return 1;
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine("""
        dotnet-data-performance-lab CLI

        Usage: dpl <command> [options]
        (dpl = dotnet run --project src/Lab.Cli --)

        Commands:
          db migrate [--database DataPerformanceLab|DataPerformanceLab_Experiment] [--reset]
          seed --profile ci|default|large [--database ...] [--reset]
          import --file <path> [--batch-size 1-2000]
          resume --job-id <guid> [--batch-size 1-2000]
          report --job-id <guid> --output <directory>
          query --experiment index|projection --category <id> --output <directory>
          fixture --output <path> [--count 1000] [--database ...]
          compare-report --input <run-directory>
          cold-cache --sku <sku> --output <directory> [--base-url ...] [--reads 3]

        Exit codes:
          0  success (import: all rows accepted or skipped)
          1  infrastructure, parse or argument error
          2  import completed with row rejections / compare-report: comparison invalid /
             cold-cache: the reads did not show the cold-then-warm pattern
          3  import busy (another importer holds the lock)
        """);
}
