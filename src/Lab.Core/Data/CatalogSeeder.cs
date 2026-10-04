using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataPerformanceLab.Catalog;
using Microsoft.EntityFrameworkCore;

namespace DataPerformanceLab.Data;

public sealed record SeedProfile(string Name, int ProductCount)
{
    public static readonly SeedProfile Ci = new("ci", 1_000);

    public static readonly SeedProfile Default = new("default", 100_000);

    public static readonly SeedProfile Large = new("large", 1_000_000);

    public static SeedProfile Resolve(string? name) => name?.ToLowerInvariant() switch
    {
        "ci" => Ci,
        "default" => Default,
        "large" => Large,
        _ => throw new ArgumentException(
            $"Unknown or missing seed profile '{name}'. Allowed: ci, default, large.", nameof(name)),
    };
}

public sealed record SeedManifest(
    int Seed,
    int GeneratorVersion,
    string Profile,
    int ProductCount,
    string DataHash,
    IReadOnlyDictionary<int, int> CategoryCounts,
    int ActiveCount,
    int InactiveCount);

public static class SeedDataHasher
{
    public static string ComputeHash(IEnumerable<Product> products)
    {
        using var incremental = new Incremental();
        foreach (var product in products)
        {
            incremental.Append(product);
        }

        return incremental.ToHashString();
    }

    public sealed class Incremental : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public void Append(Product p)
        {
            var line = FormattableString.Invariant(
                $"{p.Sku}\t{p.Name}\t{p.CategoryId}\t{p.UnitPrice:0.00}\t{p.IsActive}\t{p.Description}\t{p.CreatedAtUtc:yyyy-MM-dd'T'HH:mm:ss.fffffff}\n");
            _hash.AppendData(Encoding.UTF8.GetBytes(line));
        }

        public string ToHashString() => Convert.ToHexString(_hash.GetHashAndReset());

        public void Dispose() => _hash.Dispose();
    }
}

internal struct SplitMix64(ulong state)
{
    public ulong NextUlong()
    {
        state += 0x9E3779B97F4A7C15UL;
        var z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public uint NextUInt(uint exclusiveBound) => (uint)(NextUlong() % exclusiveBound);
}

public sealed class CatalogSeeder
{
    public const int SeedValue = 20260910;
    public const int GeneratorVersion = 1;
    public const int ChunkSize = 2000;

    public static readonly DateTime BaseCreatedAtUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Adjectives =
    [
        "Ergonomic", "Compact", "Industrial", "Refined", "Practical", "Aerodynamic", "Durable",
        "Lightweight", "Modular", "Precision", "Classic", "Modern", "Rugged", "Sleek", "Robust", "Versatile"
    ];

    private static readonly string[] Nouns =
    [
        "Widget", "Gadget", "Module", "Assembly", "Toolkit", "Device", "Unit", "System",
        "Component", "Fixture", "Bracket", "Harness", "Panel", "Console", "Adapter", "Controller"
    ];

    private static readonly string[] DescriptionSentences =
    [
        "This synthetic catalog row exists only for local data-access experiments.",
        "Every field is generated deterministically from a fixed seed and a versioned algorithm.",
        "The payload length of this column is intentional for projection experiments.",
        "No real product, customer or vendor data is stored in this laboratory.",
        "Identical seeds reproduce identical rows across databases and machines.",
        "Reads and writes on this table stay inside loopback-published lab containers.",
        "Row counts and distributions are recorded in the seed manifest.",
        "The description never influences ordering or filtering in catalog queries."
    ];

    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<Category> CreateCategories() =>
        [.. Enumerable.Range(1, 20).Select(i => new Category
        {
            Id = i,
            Name = FormattableString.Invariant($"Category-{i:D2}")
        })];

    public IEnumerable<Product> GenerateProducts(int count)
    {
        for (var i = 1; i <= count; i++)
        {
            var rng = new SplitMix64(((ulong)(uint)SeedValue << 32) ^ (ulong)(uint)i);
            var categoryId = rng.NextUInt(100) < 60 ? 1 : 2 + (int)rng.NextUInt(19);
            var isActive = rng.NextUInt(100) < 85;
            var unitPrice = (rng.NextUInt(9_999_900) + 1) / 100m;
            var name = FormattableString.Invariant(
                $"{Adjectives[rng.NextUInt((uint)Adjectives.Length)]} {Nouns[rng.NextUInt((uint)Nouns.Length)]} {i:D10}");
            var description = CreateDescription(ref rng);

            yield return new Product
            {
                Sku = FormattableString.Invariant($"SKU-{i:D10}"),
                Name = name,
                CategoryId = categoryId,
                UnitPrice = unitPrice,
                IsActive = isActive,
                Description = description,
                CreatedAtUtc = BaseCreatedAtUtc.AddSeconds(7L * i),
            };
        }
    }

    public async Task<SeedManifest> SeedAsync(LabDbContext db, SeedProfile profile, CancellationToken ct)
    {
        var existing = await TryReadManifestAsync(db, ct);
        if (existing is not null)
        {
            if (existing.Profile == profile.Name
                && existing.Seed == SeedValue
                && existing.GeneratorVersion == GeneratorVersion)
            {
                return existing;
            }

            throw new InvalidOperationException(
                $"Database already seeded with profile '{existing.Profile}' (seed {existing.Seed}, " +
                $"generator v{existing.GeneratorVersion}); re-seeding requires an explicit reset (--reset --database <name>).");
        }

        if (await db.Products.AsNoTracking().AnyAsync(ct))
        {
            throw new InvalidOperationException(
                "Products table is not empty; refusing to seed over existing data (use --reset).");
        }

        var categories = CreateCategories();
        db.Categories.AddRange(categories);
        await db.SaveChangesAsync(ct);

        var categoryCounts = new SortedDictionary<int, int>();
        for (var i = 1; i <= 20; i++)
        {
            categoryCounts[i] = 0;
        }

        var activeCount = 0;
        var inactiveCount = 0;
        var chunk = new List<Product>(ChunkSize);

        using var hasher = new SeedDataHasher.Incremental();
        foreach (var product in GenerateProducts(profile.ProductCount))
        {
            hasher.Append(product);
            chunk.Add(product);
            categoryCounts[product.CategoryId]++;
            if (product.IsActive)
            {
                activeCount++;
            }
            else
            {
                inactiveCount++;
            }

            if (chunk.Count == ChunkSize)
            {
                db.Products.AddRange(chunk);
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                chunk.Clear();
            }
        }

        if (chunk.Count > 0)
        {
            db.Products.AddRange(chunk);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        var manifest = new SeedManifest(
            SeedValue,
            GeneratorVersion,
            profile.Name,
            profile.ProductCount,
            hasher.ToHashString(),
            categoryCounts,
            activeCount,
            inactiveCount);

        await WriteManifestAsync(db, manifest, ct);
        return manifest;
    }

    public static async Task<SeedManifest?> TryReadManifestAsync(LabDbContext db, CancellationToken ct)
    {
        var entry = await db.LabMetadata.AsNoTracking()
            .SingleOrDefaultAsync(m => m.MetadataKey == LabDbMigrator.SeedManifestKey, ct);
        if (entry is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize<SeedManifest>(entry.MetadataValue, ManifestJsonOptions)
            ?? throw new InvalidOperationException("Stored seed manifest is invalid JSON.");
    }

    private static async Task WriteManifestAsync(LabDbContext db, SeedManifest manifest, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
        var entry = await db.LabMetadata.SingleOrDefaultAsync(m => m.MetadataKey == LabDbMigrator.SeedManifestKey, ct);
        if (entry is null)
        {
            db.LabMetadata.Add(new LabMetadataEntry
            {
                MetadataKey = LabDbMigrator.SeedManifestKey,
                MetadataValue = json,
                UpdatedAtUtc = DateTime.UtcNow
            });
        }
        else
        {
            entry.MetadataValue = json;
            entry.UpdatedAtUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    private static string CreateDescription(ref SplitMix64 rng)
    {
        var sb = new StringBuilder(1200);
        while (sb.Length < 1000)
        {
            sb.Append(DescriptionSentences[rng.NextUInt((uint)DescriptionSentences.Length)]).Append(' ');
        }

        return sb.ToString();
    }
}
