using DataPerformanceLab.Data;
using Microsoft.Extensions.Hosting;

namespace DataPerformanceLab.Cli.Commands;

public sealed class DbCommand(ILabDbContextFactory dbFactory, IHostEnvironment environment) : ICliCommand
{
    private const string HelpText = """
        db migrate [--database DataPerformanceLab|DataPerformanceLab_Experiment] [--reset]

        Applies EF Core migrations to a lab database.
        The first migrate on a missing database creates it.
        --reset drops and recreates the database; it requires an explicit --database.
        """;

    public string Name => "db";

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length is 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        if (!args[0].Equals("migrate", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Unknown db subcommand '{args[0]}'. Expected: db migrate");
            return 1;
        }

        var parsed = new CliArguments(args[1..], ["reset", "help"]);
        var requestedDatabase = parsed.GetOption("database");
        var reset = parsed.HasFlag("reset");

        if (reset && requestedDatabase is null)
        {
            Console.Error.WriteLine("--reset requires an explicit --database <name>.");
            return 1;
        }

        var database = LabDatabases.ResolveTarget(
            requestedDatabase,
            allowTestDatabases: environment.IsEnvironment(LabEnvironment.Testing));

        if (reset)
        {
            await LabDbMigrator.ResetLabDatabaseAsync(dbFactory.MasterConnectionString, database, ct);
            Console.WriteLine($"Reset and migrated lab database '{database}'.");
        }
        else
        {
            await LabDbMigrator.EnsureLabDatabaseAsync(dbFactory.MasterConnectionString, database, ct);
            Console.WriteLine($"Migrated lab database '{database}'.");
        }

        return 0;
    }
}
