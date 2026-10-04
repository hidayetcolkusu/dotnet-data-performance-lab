using System.Globalization;
using System.Text.Json;
using DataPerformanceLab.Configuration;
using DataPerformanceLab.Data;
using Microsoft.Data.SqlClient;

namespace DataPerformanceLab.Cli.Experiments;

/// <summary>
/// A durable note that the runner is about to change index state, so that a crash between
/// the DROP and the restoring finally can be detected on the next run instead of silently
/// leaving the experiment database in a half-configured state.
/// </summary>
public sealed record IndexRestoreMarker(string IndexName, bool ShouldExistAfterRun, DateTime StartedAtUtc);

/// <summary>
/// Creates and drops the Q1 candidate index inside the experiment database only.
/// The primary key and the unique SKU index are never touched.
/// </summary>
public sealed class CatalogIndexState(string connectionString, string databaseName)
{
    public const string MarkerMetadataKey = "experiment.indexRestore";

    private static readonly JsonSerializerOptions MarkerJsonOptions = new(JsonSerializerDefaults.Web);

    private const string CreateSql = $"""
        CREATE INDEX {CatalogIndexes.CategoryActiveCreatedId}
        ON dbo.Products(CategoryId, IsActive, CreatedAtUtc, Id)
        INCLUDE(Sku, Name, UnitPrice);
        """;

    private const string DropSql =
        $"DROP INDEX {CatalogIndexes.CategoryActiveCreatedId} ON dbo.Products;";

    /// <summary>
    /// Guards the measurement boundary (ADR 001): experiment DDL is only ever applied to the
    /// isolated experiment database, never to the database the API serves.
    /// </summary>
    public static void EnsureExperimentDatabase(string requestedDatabase)
    {
        if (!string.Equals(requestedDatabase, LabDataOptions.ExperimentDatabaseName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Query experiments only run against '{LabDataOptions.ExperimentDatabaseName}'. "
                + $"Refusing to touch '{requestedDatabase}'.");
        }
    }

    public async Task<bool> ExistsAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return await ExistsAsync(connection, ct);
    }

    private static async Task<bool> ExistsAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.Products') AND name = @name",
            connection);
        command.Parameters.AddWithValue("@name", CatalogIndexes.CategoryActiveCreatedId);
        var count = (int)(await command.ExecuteScalarAsync(ct))!;
        return count > 0;
    }

    /// <summary>
    /// Reports and repairs a state left behind by a crashed run. Returns a human-readable
    /// note when a repair happened, otherwise null.
    /// </summary>
    public async Task<string?> PreflightAsync(CancellationToken ct)
    {
        var marker = await ReadMarkerAsync(ct);
        if (marker is null)
        {
            return null;
        }

        var exists = await ExistsAsync(ct);
        var note = exists == marker.ShouldExistAfterRun
            ? $"Found a stale index-restore marker from {marker.StartedAtUtc:O} (UTC); index state was already correct."
            : $"Previous run crashed at {marker.StartedAtUtc:O} (UTC) and left "
              + $"'{marker.IndexName}' {(exists ? "present" : "absent")} while it should be "
              + $"{(marker.ShouldExistAfterRun ? "present" : "absent")}. Restoring it now.";

        await SetIndexAsync(marker.ShouldExistAfterRun, ct);
        await ClearMarkerAsync(ct);
        return note;
    }

    /// <summary>
    /// Runs <paramref name="body"/> with the candidate index forced to
    /// <paramref name="indexPresent"/>, then always restores the state it found.
    /// </summary>
    public async Task WithIndexAsync(bool indexPresent, Func<Task> body, CancellationToken ct)
    {
        var originalState = await ExistsAsync(ct);
        if (originalState == indexPresent)
        {
            await body();
            return;
        }

        await WriteMarkerAsync(new IndexRestoreMarker(
            CatalogIndexes.CategoryActiveCreatedId, originalState, DateTime.UtcNow), ct);

        try
        {
            await SetIndexAsync(indexPresent, ct);
            await body();
        }
        finally
        {
            // CancellationToken.None: restoring schema state matters even when the run is
            // being cancelled, otherwise the next run starts from a half-configured database.
            await SetIndexAsync(originalState, CancellationToken.None);
            await ClearMarkerAsync(CancellationToken.None);
        }
    }

    public async Task SetIndexAsync(bool shouldExist, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        var exists = await ExistsAsync(connection, ct);
        if (exists == shouldExist)
        {
            return;
        }

        await using var command = new SqlCommand(shouldExist ? CreateSql : DropSql, connection)
        {
            CommandTimeout = 300
        };
        await command.ExecuteNonQueryAsync(ct);
    }

    public string DescribeTarget() =>
        string.Create(CultureInfo.InvariantCulture, $"{databaseName}.dbo.Products");

    private async Task<IndexRestoreMarker?> ReadMarkerAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            "SELECT MetadataValue FROM dbo.LabMetadata WHERE MetadataKey = @key", connection);
        command.Parameters.AddWithValue("@key", MarkerMetadataKey);
        if (await command.ExecuteScalarAsync(ct) is not string json)
        {
            return null;
        }

        return JsonSerializer.Deserialize<IndexRestoreMarker>(json, MarkerJsonOptions);
    }

    private async Task WriteMarkerAsync(IndexRestoreMarker marker, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            """
            MERGE dbo.LabMetadata AS target
            USING (SELECT @key AS MetadataKey) AS source ON target.MetadataKey = source.MetadataKey
            WHEN MATCHED THEN UPDATE SET MetadataValue = @value, UpdatedAtUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (MetadataKey, MetadataValue, UpdatedAtUtc)
                VALUES (@key, @value, SYSUTCDATETIME());
            """,
            connection);
        command.Parameters.AddWithValue("@key", MarkerMetadataKey);
        command.Parameters.AddWithValue("@value", JsonSerializer.Serialize(marker, MarkerJsonOptions));
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task ClearMarkerAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            "DELETE FROM dbo.LabMetadata WHERE MetadataKey = @key", connection);
        command.Parameters.AddWithValue("@key", MarkerMetadataKey);
        await command.ExecuteNonQueryAsync(ct);
    }
}
