using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataPerformanceLab.Data;

namespace DataPerformanceLab.Cli.Experiments;

public sealed record GitInfo(string CommitSha, bool IsDirty);

/// <summary>
/// The machine and toolchain a measurement actually ran on. <see cref="Unavailable"/> is used
/// wherever a value genuinely could not be read — a run must never borrow today's machine values
/// to describe a measurement taken on another one.
/// </summary>
public sealed record EnvironmentInfo(
    string OperatingSystem,
    string Architecture,
    int ProcessorCount,
    string RuntimeVersion,
    long WorkingSetLimitBytes,
    string SdkVersion,
    IReadOnlyDictionary<string, string> PackageVersions,
    IReadOnlyList<ContainerInfo> Containers)
{
    public const string Unavailable = "unavailable";
}

/// <summary>
/// One infrastructure container as the run found it: the image it was actually running (resolved
/// to a digest where the daemon could tell us) and the resource limits it was running under.
/// A limit of 0 from the daemon means "unlimited", and is reported as such rather than as zero.
/// </summary>
public sealed record ContainerInfo(
    string Service,
    string Image,
    string ImageDigest,
    string CpuLimit,
    string MemoryLimit);

public sealed record DatasetInfo(
    int Seed,
    int GeneratorVersion,
    string Profile,
    int ProductCount,
    string DataHash,
    /// <summary>Products per category id, straight from the seed manifest, so a reader can see
    /// the skew the experiment was measured against instead of inferring it from the profile
    /// name.</summary>
    IReadOnlyDictionary<int, int>? CategoryCounts = null,
    int? ActiveCount = null,
    int? InactiveCount = null)
{
    public static DatasetInfo FromSeedManifest(SeedManifest manifest) => new(
        manifest.Seed,
        manifest.GeneratorVersion,
        manifest.Profile,
        manifest.ProductCount,
        manifest.DataHash,
        manifest.CategoryCounts,
        manifest.ActiveCount,
        manifest.InactiveCount);
}

/// <summary>
/// A result file that was produced but is not published alongside the manifest — a raw k6 sample
/// stream, typically. The hash and size identify the exact bytes that were measured, and the
/// command says how to make them again; together they are what keeps "kept locally" honest.
/// </summary>
public sealed record StoredResultFile(
    string Path,
    string Storage,
    long SizeBytes,
    string Sha256,
    string ReproduceCommand)
{
    public const string StoragePublished = "published";

    public const string StorageLocalOnly = "local-only";
}

/// <summary>
/// The run manifest (ADR 001). Fields are written explicitly rather than reflected off configuration,
/// so a secret can never reach the file by being added to a config section later.
/// </summary>
public sealed record ExperimentManifest(
    string RunId,
    string Experiment,
    string Status,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    string Database,
    GitInfo Git,
    EnvironmentInfo Environment,
    DatasetInfo? Dataset,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<string> ResultFiles,
    string Command,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FailureReason = null,
    /// <summary>
    /// Storage records for files the run produced, including the large ones kept out of the
    /// published directory. Each carries a hash, a size and the command that makes it again.
    /// </summary>
    IReadOnlyList<StoredResultFile>? StoredFiles = null)
{
    public const string StatusCompleted = "completed";

    public const string StatusFailed = "failed";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static EnvironmentInfo CaptureEnvironment(
        IReadOnlyList<ContainerInfo>? containers = null) => new(
        RuntimeInformation.OSDescription,
        RuntimeInformation.OSArchitecture.ToString(),
        System.Environment.ProcessorCount,
        RuntimeInformation.FrameworkDescription,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        CaptureSdkVersion(),
        CapturePackageVersions(),
        containers ?? CaptureContainers());

    private static string CaptureSdkVersion() =>
        RunProcess("dotnet", "--version")?.Trim() ?? EnvironmentInfo.Unavailable;

    /// <summary>
    /// The assembly versions of the data-path packages as loaded into this process — the versions
    /// that actually served the measurement, rather than what a lock file says should have.
    /// </summary>
    private static IReadOnlyDictionary<string, string> CapturePackageVersions()
    {
        var wanted = new[]
        {
            "Microsoft.EntityFrameworkCore",
            "Microsoft.EntityFrameworkCore.SqlServer",
            "Microsoft.Data.SqlClient",
            "StackExchange.Redis",
            "CsvHelper"
        };

        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .GroupBy(a => a.GetName().Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        return wanted.ToDictionary(
            name => name,
            name => loaded.TryGetValue(name, out var assembly)
                ? assembly.GetName().Version?.ToString() ?? EnvironmentInfo.Unavailable
                : EnvironmentInfo.Unavailable,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Asks the Docker daemon what the lab containers are running and under which limits. When
    /// Docker is not reachable the containers list is empty rather than invented; a reader then
    /// sees that the infrastructure could not be described, which is the truth.
    /// </summary>
    public static IReadOnlyList<ContainerInfo> CaptureContainers()
    {
        var containers = new List<ContainerInfo>();

        // compose.yaml names the containers <prefix>-sql / <prefix>-redis; the prefix is only
        // changed for a second checkout on the same machine.
        var prefix = System.Environment.GetEnvironmentVariable("DPL_CONTAINER_PREFIX") is { Length: > 0 } p ? p : "dpl";

        foreach (var (service, name) in new[] { ("sql", $"{prefix}-sql"), ("redis", $"{prefix}-redis") })
        {
            // One inspect per container, tab-separated, so a field that is itself empty does not
            // shift the meaning of the others.
            var format = "{{.Config.Image}}\t{{.Image}}\t{{.HostConfig.NanoCpus}}\t{{.HostConfig.Memory}}";
            var output = RunProcess("docker", $"inspect {name} --format \"{format}\"");
            if (output is null)
            {
                continue;
            }

            var parts = output.Trim().Split('\t');
            if (parts.Length < 4)
            {
                continue;
            }

            containers.Add(new ContainerInfo(
                service,
                parts[0],
                parts[1],
                DescribeCpuLimit(parts[2]),
                DescribeMemoryLimit(parts[3])));
        }

        return containers;
    }

    private static string DescribeCpuLimit(string nanoCpus) =>
        long.TryParse(nanoCpus, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value == 0
                ? "unlimited (all host CPUs)"
                : string.Create(CultureInfo.InvariantCulture, $"{value / 1_000_000_000d:0.##} CPU")
            : EnvironmentInfo.Unavailable;

    private static string DescribeMemoryLimit(string bytes) =>
        long.TryParse(bytes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value == 0
                ? "unlimited (all host memory)"
                : string.Create(CultureInfo.InvariantCulture, $"{value} bytes")
            : EnvironmentInfo.Unavailable;

    /// <summary>
    /// Computes the storage record for a produced file. A file that is not on disk is reported as
    /// missing rather than given a fabricated hash.
    /// </summary>
    public static StoredResultFile DescribeResultFile(
        string absolutePath, string relativePath, string storage, string reproduceCommand)
    {
        if (!File.Exists(absolutePath))
        {
            return new StoredResultFile(
                relativePath, "missing", 0, EnvironmentInfo.Unavailable, reproduceCommand);
        }

        using var stream = File.OpenRead(absolutePath);
        var hash = System.Security.Cryptography.SHA256.HashData(stream);

        return new StoredResultFile(
            relativePath,
            storage,
            new FileInfo(absolutePath).Length,
            Convert.ToHexString(hash).ToLowerInvariant(),
            reproduceCommand);
    }

    private static string? RunProcess(string fileName, string arguments, string? workingDirectory = null)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                WorkingDirectory = workingDirectory ?? string.Empty,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records the commit the measurement ran on. A dirty tree is reported rather than
    /// hidden: ADR 001 requires re-measuring from a clean commit or keeping the diff.
    ///
    /// The results directory is excluded from the check. A run writes its own artifacts
    /// there, so counting it would make every published run report a dirty tree and the
    /// flag would mean nothing. Everything else — including untracked source files — still
    /// counts, because it is what the measured binaries were built from.
    /// </summary>
    public static GitInfo CaptureGit(string workingDirectory)
    {
        var sha = RunGit("rev-parse HEAD", workingDirectory) ?? "unknown";
        var status = RunGit("status --porcelain -- :!results", workingDirectory);
        return new GitInfo(sha.Trim(), !string.IsNullOrWhiteSpace(status));
    }

    private static string? RunGit(string arguments, string workingDirectory) =>
        RunProcess("git", arguments, workingDirectory);

    public static string NewRunId() =>
        string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}");

    public async Task WriteAsync(string path, CancellationToken ct)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, JsonOptions, ct);
    }
}
