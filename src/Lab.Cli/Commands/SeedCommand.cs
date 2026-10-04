using DataPerformanceLab.Data;
using Microsoft.Extensions.Hosting;

namespace DataPerformanceLab.Cli.Commands;

public sealed class SeedCommand(
    ILabDbContextFactory dbFactory,
    CatalogSeeder seeder,
    IHostEnvironment environment) : ICliCommand
{
    private const string HelpText = """
        seed --profile ci|default|large [--database DataPerformanceLab|DataPerformanceLab_Experiment] [--reset]

        Seeds deterministic synthetic catalog data (seed 20260910, generator v1).
        The target database must already be migrated (db migrate).
        Same profile on an already-seeded database is a no-op.
        A different profile requires an explicit reset (--reset --database <name>).
        """;

    public string Name => "seed";

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length is 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        var parsed = new CliArguments(args, ["reset", "help"]);
        var profileName = parsed.GetOption("profile");
        if (profileName is null)
        {
            Console.Error.WriteLine("--profile is required (ci|default|large).");
            return 1;
        }

        SeedProfile profile;
        try
        {
            profile = SeedProfile.Resolve(profileName);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

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
        }
        else
        {
            await LabDbMigrator.EnsureLabDatabaseAsync(dbFactory.MasterConnectionString, database, ct);
        }

        await using var db = dbFactory.CreateForDatabase(database);
        var manifest = await seeder.SeedAsync(db, profile, ct);

        Console.WriteLine(
            $"Seeded '{manifest.Profile}': {manifest.ProductCount} products (active {manifest.ActiveCount}, inactive {manifest.InactiveCount}), seed {manifest.Seed}, generator v{manifest.GeneratorVersion}.");
        Console.WriteLine($"DataHash: {manifest.DataHash}");
        return 0;
    }
}
