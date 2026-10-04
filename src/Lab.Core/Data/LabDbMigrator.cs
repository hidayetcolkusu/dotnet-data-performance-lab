using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DataPerformanceLab.Data;

/// <summary>
/// Raised when a database the lab was asked to migrate, seed or reset does not prove it belongs
/// to the lab. Nothing has been written when this is thrown: the caller's database is intact.
/// </summary>
public sealed class NotALabDatabaseException(string message) : InvalidOperationException(message);

public partial class LabDbMigrator
{
    public const string LabIdentityKey = "LabIdentity";
    public const string LabIdentityValue = "dotnet-data-performance-lab";
    public const string SchemaVersionKey = "SchemaVersion";
    public const string SeedManifestKey = "SeedManifest";

    [GeneratedRegex(@"^[A-Za-z0-9_]{1,128}$")]
    private static partial Regex DatabaseNamePattern();

    public static async Task EnsureLabDatabaseAsync(string masterConnectionString, string databaseName, CancellationToken ct)
    {
        ValidateDatabaseName(databaseName);

        if (!await DatabaseExistsAsync(masterConnectionString, databaseName, ct))
        {
            await CreateDatabaseAsync(masterConnectionString, databaseName, ct);
        }

        await using var db = CreateContext(masterConnectionString, databaseName);
        await EnsureLabMarkerAndMigrateAsync(db, ct);
    }

    /// <summary>
    /// Drops and recreates a lab database. An existing database is only dropped once it has
    /// proven it belongs to the lab: the name allowlist is a filter, never the authority. A
    /// database that exists but carries no lab marker is left byte-for-byte untouched.
    /// </summary>
    public static async Task ResetLabDatabaseAsync(string masterConnectionString, string databaseName, CancellationToken ct)
    {
        ValidateDatabaseName(databaseName);

        if (await DatabaseExistsAsync(masterConnectionString, databaseName, ct))
        {
            // Verify ownership on its own connection and close it before the drop, so the
            // guard never becomes the session that blocks SET SINGLE_USER.
            await using (var existing = CreateContext(masterConnectionString, databaseName))
            {
                await EnsureOwnedByLabAsync(existing, databaseName, "reset", ct);
            }

            await DropDatabaseAsync(masterConnectionString, databaseName, ct);
        }

        await CreateDatabaseAsync(masterConnectionString, databaseName, ct);

        await using var db = CreateContext(masterConnectionString, databaseName);
        await db.Database.MigrateAsync(ct);
        await WriteLabIdentityAsync(db, ct);
    }

    private static async Task EnsureLabMarkerAndMigrateAsync(LabDbContext db, CancellationToken ct)
    {
        await EnsureOwnedByLabAsync(db, db.Database.GetDbConnection().Database, "migrate", ct);

        await db.Database.MigrateAsync(ct);
        await WriteLabIdentityAsync(db, ct);
    }

    /// <summary>
    /// The single ownership gate shared by migrate, seed-reset and db-reset. An empty database
    /// is adoptable — that is the half-finished state a previous create-then-fail leaves behind
    /// — but a database holding tables must present the lab marker to be touched at all.
    /// </summary>
    private static async Task EnsureOwnedByLabAsync(
        LabDbContext db, string databaseName, string operation, CancellationToken ct)
    {
        if (await TableExistsAsync(db, "LabMetadata", ct))
        {
            var identity = await TryReadLabIdentityAsync(db, ct);
            if (identity != LabIdentityValue)
            {
                throw new NotALabDatabaseException(
                    $"Database '{databaseName}' has a LabMetadata table but LabIdentity is "
                    + $"'{identity ?? "<missing>"}' instead of '{LabIdentityValue}'; "
                    + $"refusing to {operation} it.");
            }

            return;
        }

        if (await HasAnyUserTableAsync(db, ct))
        {
            throw new NotALabDatabaseException(
                $"Database '{databaseName}' contains user tables but no lab metadata; "
                + $"refusing to {operation} a database the lab does not own.");
        }
    }

    private static async Task WriteLabIdentityAsync(LabDbContext db, CancellationToken ct)
    {
        var applied = await db.Database.GetAppliedMigrationsAsync(ct);
        var schemaVersion = applied.LastOrDefault() ?? "none";

        await UpsertMetadataAsync(db, LabIdentityKey, LabIdentityValue, ct);
        await UpsertMetadataAsync(db, SchemaVersionKey, schemaVersion, ct);
    }

    private static async Task UpsertMetadataAsync(LabDbContext db, string key, string value, CancellationToken ct)
    {
        var entry = await db.LabMetadata.SingleOrDefaultAsync(m => m.MetadataKey == key, ct);
        if (entry is null)
        {
            db.LabMetadata.Add(new LabMetadataEntry { MetadataKey = key, MetadataValue = value, UpdatedAtUtc = DateTime.UtcNow });
        }
        else
        {
            entry.MetadataValue = value;
            entry.UpdatedAtUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Reads the lab marker with raw SQL rather than the EF model. A foreign database may own a
    /// table that happens to be called LabMetadata with entirely different columns; that must
    /// read as "not a lab database", not crash the guard with a column-binding error.
    /// </summary>
    private static async Task<string?> TryReadLabIdentityAsync(LabDbContext db, CancellationToken ct)
    {
        var connection = await OpenConnectionAsync(db, ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF COL_LENGTH('dbo.LabMetadata', 'MetadataKey') IS NOT NULL
               AND COL_LENGTH('dbo.LabMetadata', 'MetadataValue') IS NOT NULL
                SELECT TOP 1 MetadataValue FROM dbo.LabMetadata WHERE MetadataKey = @key;
            """;
        command.Parameters.Add(new SqlParameter("@key", LabIdentityKey));

        try
        {
            return await command.ExecuteScalarAsync(ct) as string;
        }
        catch (SqlException)
        {
            // Unreadable marker table — treat as unowned rather than as permission to proceed.
            return null;
        }
    }

    private static async Task<System.Data.Common.DbConnection> OpenConnectionAsync(
        LabDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        return connection;
    }

    private static async Task<bool> TableExistsAsync(LabDbContext db, string tableName, CancellationToken ct)
    {
        var connection = await OpenConnectionAsync(db, ct);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name = @tableName";
        command.Parameters.Add(new SqlParameter("@tableName", tableName));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task<bool> HasAnyUserTableAsync(LabDbContext db, CancellationToken ct)
    {
        var connection = await OpenConnectionAsync(db, ct);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task<bool> DatabaseExistsAsync(string masterConnectionString, string databaseName, CancellationToken ct)
    {
        await using var connection = new SqlConnection(masterConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = @name";
        command.Parameters.Add(new SqlParameter("@name", databaseName));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task CreateDatabaseAsync(string masterConnectionString, string databaseName, CancellationToken ct)
    {
        await using var connection = new SqlConnection(masterConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{databaseName}]";
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task DropDatabaseAsync(string masterConnectionString, string databaseName, CancellationToken ct)
    {
        await using var connection = new SqlConnection(masterConnectionString);
        await connection.OpenAsync(ct);
        await using var singleUser = connection.CreateCommand();
        singleUser.CommandText = $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE";
        await singleUser.ExecuteNonQueryAsync(ct);
        await using var drop = connection.CreateCommand();
        drop.CommandText = $"DROP DATABASE [{databaseName}]";
        await drop.ExecuteNonQueryAsync(ct);
    }

    private static void ValidateDatabaseName(string databaseName)
    {
        if (!DatabaseNamePattern().IsMatch(databaseName))
        {
            throw new InvalidOperationException($"Database name '{databaseName}' is not a valid lab database name.");
        }
    }

    private static LabDbContext CreateContext(string masterConnectionString, string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(masterConnectionString) { InitialCatalog = databaseName };
        return new LabDbContext(
            new DbContextOptionsBuilder<LabDbContext>().UseSqlServer(builder.ConnectionString).Options);
    }
}
