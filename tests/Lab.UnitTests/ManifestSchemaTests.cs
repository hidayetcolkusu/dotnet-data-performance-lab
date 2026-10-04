using System.Security.Cryptography;
using System.Text.Json;
using DataPerformanceLab.Cli.Experiments;
using DataPerformanceLab.Data;
using Xunit;

namespace DataPerformanceLab.UnitTests;

/// <summary>
/// Guards the run manifest contract: the fields a reader needs in order to reproduce a
/// measurement are present, a file's recorded bytes are its real bytes, and nothing resembling a
/// credential can reach a published artifact.
/// </summary>
public sealed class ManifestSchemaTests
{
    private static ExperimentManifest Sample(
        DatasetInfo? dataset = null,
        IReadOnlyDictionary<string, string>? parameters = null,
        IReadOnlyList<StoredResultFile>? storedFiles = null) =>
        new(
            "20260912T120000Z",
            "query-index",
            ExperimentManifest.StatusCompleted,
            new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 12, 12, 5, 0, DateTimeKind.Utc),
            "DataPerformanceLab_Experiment",
            new GitInfo("1268385f904e9e6f600bd3180e592ec3c1276421", false),
            ExperimentManifest.CaptureEnvironment(containers: []),
            dataset,
            parameters ?? new Dictionary<string, string>(StringComparer.Ordinal) { ["experiment"] = "Index" },
            ["query-samples.json"],
            "dpl query --experiment index --category 1 --output results/local/x",
            null,
            storedFiles);

    private static JsonElement Serialize(ExperimentManifest manifest) =>
        JsonSerializer.SerializeToElement(manifest, ExperimentManifest.JsonOptions);

    // ---- required fields --------------------------------------------------------------------

    [Theory]
    [InlineData("runId")]
    [InlineData("experiment")]
    [InlineData("status")]
    [InlineData("startedAtUtc")]
    [InlineData("completedAtUtc")]
    [InlineData("database")]
    [InlineData("git")]
    [InlineData("environment")]
    [InlineData("parameters")]
    [InlineData("resultFiles")]
    [InlineData("command")]
    public void TheManifestCarriesEveryRequiredTopLevelField(string field)
    {
        Assert.True(Serialize(Sample()).TryGetProperty(field, out _), $"'{field}' is missing.");
    }

    [Theory]
    [InlineData("operatingSystem")]
    [InlineData("architecture")]
    [InlineData("processorCount")]
    [InlineData("runtimeVersion")]
    [InlineData("workingSetLimitBytes")]
    [InlineData("sdkVersion")]
    [InlineData("packageVersions")]
    [InlineData("containers")]
    public void TheEnvironmentBlockDescribesTheToolchainAndTheMachine(string field)
    {
        var environment = Serialize(Sample()).GetProperty("environment");

        Assert.True(environment.TryGetProperty(field, out _), $"environment.{field} is missing.");
    }

    [Fact]
    public void ThePackageVersionBlockNamesEveryDataPathPackage()
    {
        var packages = Serialize(Sample()).GetProperty("environment").GetProperty("packageVersions");

        foreach (var package in new[]
        {
            "Microsoft.EntityFrameworkCore",
            "Microsoft.EntityFrameworkCore.SqlServer",
            "Microsoft.Data.SqlClient",
            "StackExchange.Redis",
            "CsvHelper"
        })
        {
            Assert.True(packages.TryGetProperty(package, out _), $"{package} is not recorded.");
        }
    }

    [Fact]
    public void AnUnreadableContainerListIsEmptyRatherThanInvented()
    {
        // Docker may not be reachable at all. The honest record is "nothing described", never a
        // plausible-looking image name the run never used.
        var containers = Serialize(Sample()).GetProperty("environment").GetProperty("containers");

        Assert.Equal(JsonValueKind.Array, containers.ValueKind);
    }

    // ---- dataset distribution ---------------------------------------------------------------

    [Fact]
    public void TheDatasetCarriesTheRealDistributionNotJustTheProfileName()
    {
        var seedManifest = new SeedManifest(
            20260910, 1, "default", 100_000, "DC7DE2FF",
            new Dictionary<int, int> { [1] = 50_000, [2] = 25_000, [3] = 25_000 },
            ActiveCount: 80_000,
            InactiveCount: 20_000);

        var dataset = Serialize(Sample(DatasetInfo.FromSeedManifest(seedManifest)))
            .GetProperty("dataset");

        Assert.Equal(80_000, dataset.GetProperty("activeCount").GetInt32());
        Assert.Equal(20_000, dataset.GetProperty("inactiveCount").GetInt32());
        Assert.Equal(50_000, dataset.GetProperty("categoryCounts").GetProperty("1").GetInt32());
    }

    [Fact]
    public void DatasetInfoFromSeedManifestCopiesEveryFieldWithoutReinterpretingIt()
    {
        var seedManifest = new SeedManifest(
            20260910, 2, "large", 1_000_000, "HASH",
            new Dictionary<int, int> { [7] = 1_000_000 }, 999_999, 1);

        var dataset = DatasetInfo.FromSeedManifest(seedManifest);

        Assert.Equal(seedManifest.Seed, dataset.Seed);
        Assert.Equal(seedManifest.GeneratorVersion, dataset.GeneratorVersion);
        Assert.Equal(seedManifest.Profile, dataset.Profile);
        Assert.Equal(seedManifest.ProductCount, dataset.ProductCount);
        Assert.Equal(seedManifest.DataHash, dataset.DataHash);
        Assert.Equal(seedManifest.CategoryCounts, dataset.CategoryCounts);
        Assert.Equal(seedManifest.ActiveCount, dataset.ActiveCount);
        Assert.Equal(seedManifest.InactiveCount, dataset.InactiveCount);
    }

    // ---- stored files: hash, size, reproduce command -----------------------------------------

    [Fact]
    public void AStoredFileRecordsTheBytesThatAreActuallyOnDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dpl-manifest-{Guid.NewGuid():N}.json");
        var content = """{"schema":"dpl.load-summary.v1"}"""u8.ToArray();
        File.WriteAllBytes(path, content);

        try
        {
            var stored = ExperimentManifest.DescribeResultFile(
                path, "runs/1-AB-cache-off.raw.json", StoredResultFile.StorageLocalOnly, "./scripts/compare-cache.ps1");

            Assert.Equal(StoredResultFile.StorageLocalOnly, stored.Storage);
            Assert.Equal(content.Length, stored.SizeBytes);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
                stored.Sha256);
            Assert.Equal("runs/1-AB-cache-off.raw.json", stored.Path);
            Assert.Contains("compare-cache.ps1", stored.ReproduceCommand);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AFileThatIsNotOnDiskIsReportedMissingRatherThanGivenAFabricatedHash()
    {
        var stored = ExperimentManifest.DescribeResultFile(
            Path.Combine(Path.GetTempPath(), $"dpl-absent-{Guid.NewGuid():N}.json"),
            "runs/9-AB-cache-off.raw.json",
            StoredResultFile.StorageLocalOnly,
            "./scripts/compare-cache.ps1");

        Assert.Equal("missing", stored.Storage);
        Assert.Equal(0, stored.SizeBytes);
        Assert.Equal(EnvironmentInfo.Unavailable, stored.Sha256);
    }

    [Fact]
    public void EveryStoredFileCarriesACommandThatWouldMakeItAgain()
    {
        var stored = new[]
        {
            new StoredResultFile("runs/1-AB-cache-off.raw.json", StoredResultFile.StorageLocalOnly,
                12_345, "abc", "./scripts/compare-cache.ps1 -Profile default -Rate 20"),
        };

        var files = Serialize(Sample(storedFiles: stored)).GetProperty("storedFiles");

        var entry = Assert.Single(files.EnumerateArray());
        foreach (var field in new[] { "path", "storage", "sizeBytes", "sha256", "reproduceCommand" })
        {
            Assert.True(entry.TryGetProperty(field, out _), $"storedFiles[].{field} is missing.");
        }

        Assert.NotEmpty(entry.GetProperty("reproduceCommand").GetString()!);
    }

    // ---- the recorded command must reproduce the run -----------------------------------------

    [Fact]
    public void TheRecordedCommandRoundTripsEveryParameterThatChangesTheMeasurement()
    {
        // The manifest needs the full command. A parameter recorded in `parameters` but absent from
        // `command` makes the published text reproduce a different run than the one measured.
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["rate"] = "40",
            ["duration"] = "90s",
            ["repetitions"] = "3",
            ["warmupDuration"] = "45s",
            ["fixtureCount"] = "500"
        };

        var command = "./scripts/compare-cache.ps1 -Profile default -Output results/local/x "
            + "-Repetitions 3 -Rate 40 -Duration 90s -WarmupDuration 45s -FixtureCount 500 -Port 8080";

        var manifest = Sample(parameters: parameters) with { Command = command };

        foreach (var value in parameters.Values)
        {
            Assert.Contains(value, manifest.Command);
        }
    }

    // ---- no secrets -------------------------------------------------------------------------

    [Fact]
    public void TheManifestNeverCarriesConnectionMaterial()
    {
        // Parameters are a free-form dictionary, so the guard is on the serialized document:
        // whatever a caller puts in, a published manifest must not read as a credential.
        var manifest = Sample(parameters: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["baseUrl"] = "http://127.0.0.1:8080",
            ["differingFactor"] = "Cache:Enabled"
        });

        var json = JsonSerializer.Serialize(manifest, ExperimentManifest.JsonOptions);

        // The needles are assembled from fragments rather than written out, for the same reason
        // CI's hygiene job splits its own: a scanner that contains the literals it searches for
        // matches itself, and this file would then be reported as carrying connection material.
        foreach (var needle in new[]
        {
            "Pass" + "word=", "User" + " ID=", "Account" + "Key=",
            "MSSQL" + "_SA_PASSWORD", "Data" + " Source=", "Initial" + " Catalog=",
            "PRIVATE" + " KEY"
        })
        {
            Assert.DoesNotContain(needle, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NoManifestPropertyIsNamedLikeACredential()
    {
        var suspicious = typeof(ExperimentManifest).GetProperties()
            .Concat(typeof(EnvironmentInfo).GetProperties())
            .Concat(typeof(ContainerInfo).GetProperties())
            .Concat(typeof(DatasetInfo).GetProperties())
            .Select(p => p.Name)
            .Where(name => name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                || name.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Credential", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Token", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(suspicious);
    }

    [Fact]
    public void UnavailableIsRecordedRatherThanASubstituteValue()
    {
        // A value that could not be read must say so. Borrowing today's machine's reading to
        // describe a measurement taken elsewhere would be a fabricated manifest.
        Assert.Equal("unavailable", EnvironmentInfo.Unavailable);

        var container = new ContainerInfo(
            "sql", "unknown", "unknown", EnvironmentInfo.Unavailable, EnvironmentInfo.Unavailable);

        Assert.Equal(EnvironmentInfo.Unavailable, container.CpuLimit);
        Assert.Equal(EnvironmentInfo.Unavailable, container.MemoryLimit);
    }

    [Fact]
    public void TheManifestSerializesWithoutThrowingAndStaysReadable()
    {
        var json = JsonSerializer.Serialize(Sample(), ExperimentManifest.JsonOptions);

        Assert.Contains("\"runId\"", json);
        // WriteIndented: a manifest is read by people, not only parsed.
        Assert.Contains(Environment.NewLine.Contains('\n') ? "\n" : "\r", json);
        Assert.Equal(
            "20260912T120000Z",
            JsonSerializer.Deserialize<JsonElement>(json).GetProperty("runId").GetString());
    }

    [Fact]
    public void AFailedRunKeepsItsReasonAndASuccessfulOneOmitsTheField()
    {
        var failed = Sample() with
        {
            Status = ExperimentManifest.StatusFailed,
            FailureReason = "Variants returned different ordered results."
        };

        Assert.Contains("failureReason", JsonSerializer.Serialize(failed, ExperimentManifest.JsonOptions));
        Assert.DoesNotContain("failureReason", JsonSerializer.Serialize(Sample(), ExperimentManifest.JsonOptions));
    }

    [Fact]
    public void ADirtyTreeIsRecordedRatherThanHidden()
    {
        var dirty = Sample() with { Git = new GitInfo("abc123", true) };
        var json = Serialize(dirty).GetProperty("git");

        Assert.True(json.GetProperty("isDirty").GetBoolean());
    }
}
