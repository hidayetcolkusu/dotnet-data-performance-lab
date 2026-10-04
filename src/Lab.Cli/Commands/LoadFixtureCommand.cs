using System.Globalization;
using System.Text.Json;
using DataPerformanceLab.Cli.Experiments;
using DataPerformanceLab.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace DataPerformanceLab.Cli.Commands;

/// <summary>
/// Writes the read-only fixture the k6 comparison drives: a deterministic sample of existing
/// SKUs together with the identity each detail response must carry.
///
/// The fixture is generated from the database rather than re-derived from the seed algorithm,
/// so it cannot drift from what the API will actually return.
/// </summary>
public sealed class LoadFixtureCommand(ILabDbContextFactory dbFactory, IHostEnvironment environment) : ICliCommand
{
    public const int DefaultCount = 1_000;

    private const int ReadChunkSize = 500;

    private const string HelpText = """
        fixture --output <path> [--count 1000] [--database DataPerformanceLab]

        Writes the k6 load fixture: --count existing SKUs spread evenly across the catalog,
        each with the Id, name, category, price and active flag the detail endpoint must return.

        The database must already be migrated and seeded. Nothing is written to the database.
        """;

    public string Name => "fixture";

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length is 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        var parsed = new CliArguments(args, ["help"]);
        var outputPath = parsed.GetOption("output")
            ?? throw new ArgumentException("--output <path> is required.");
        var count = ParseCount(parsed.GetOption("count"));
        var database = ImportCommandSupport.ResolveDatabase(parsed, environment);

        await using var db = dbFactory.CreateForDatabase(database);

        var dataset = await ReadDatasetAsync(db, database, ct);

        var orderedIds = await db.Products
            .AsNoTracking()
            .OrderBy(p => p.Id)
            .Select(p => p.Id)
            .ToListAsync(ct);

        var selectedIds = LoadFixture.SelectIds(orderedIds, count);
        var products = await ReadProductsAsync(db, selectedIds, ct);

        var fixture = new LoadFixture(
            LoadFixture.ExpectedSchema,
            database,
            dataset,
            LoadFixture.ComputeHash(products),
            products.Count,
            LoadFixture.StrideFor(orderedIds.Count, count),
            products);

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using (var stream = File.Create(outputPath))
        {
            await JsonSerializer.SerializeAsync(stream, fixture, ExperimentManifest.JsonOptions, ct);
        }

        Console.WriteLine(FormattableString.Invariant(
            $"Wrote {products.Count} SKUs to {outputPath} (stride {fixture.Stride} over {orderedIds.Count} products)."));
        Console.WriteLine($"  dataset {dataset.Profile} seed {dataset.Seed} hash {dataset.DataHash[..16]}");
        Console.WriteLine($"  fixture hash {fixture.FixtureHash}");

        return 0;
    }

    /// <summary>
    /// Reads the selected rows in chunks. One <c>WHERE Id IN (...)</c> with a thousand
    /// parameters would be a needlessly large plan for a tooling query.
    /// </summary>
    private static async Task<List<LoadFixtureProduct>> ReadProductsAsync(
        LabDbContext db, IReadOnlyList<long> ids, CancellationToken ct)
    {
        var byId = new Dictionary<long, LoadFixtureProduct>(ids.Count);

        foreach (var chunk in ids.Chunk(ReadChunkSize))
        {
            var rows = await db.Products
                .AsNoTracking()
                .Where(p => chunk.Contains(p.Id))
                .Select(p => new LoadFixtureProduct(
                    p.Id, p.Sku, p.Name, p.CategoryId, p.UnitPrice, p.IsActive))
                .ToListAsync(ct);

            foreach (var row in rows)
            {
                byId[row.Id] = row;
            }
        }

        var missing = ids.Where(id => !byId.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"{missing.Count} selected products disappeared while the fixture was being built "
                + "(first missing Id " + missing[0].ToString(CultureInfo.InvariantCulture) + "). "
                + "Re-run against a quiescent database.");
        }

        // Keep the caller's even-stride order: it is what makes the fixture reproducible.
        return [.. ids.Select(id => byId[id])];
    }

    private static async Task<DatasetInfo> ReadDatasetAsync(LabDbContext db, string database, CancellationToken ct)
    {
        var manifest = await CatalogSeeder.TryReadManifestAsync(db, ct)
            ?? throw new InvalidOperationException(
                $"'{database}' has no seed manifest. Run: dpl db migrate --database {database} "
                + $"&& dpl seed --profile default --database {database}");

        return DatasetInfo.FromSeedManifest(manifest);
    }

    private static int ParseCount(string? value)
    {
        if (value is null)
        {
            return DefaultCount;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || count is < 1 or > 100_000)
        {
            throw new ArgumentException("--count must be an integer between 1 and 100000.");
        }

        return count;
    }
}
