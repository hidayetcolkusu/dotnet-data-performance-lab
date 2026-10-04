using DataPerformanceLab.Catalog;
using DataPerformanceLab.Configuration;
using DataPerformanceLab.Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DataPerformanceLab.IntegrationTests;

/// <summary>
/// Proves that the allowed-name list is a filter and never the authority: a database the lab does
/// not own survives a reset attempt untouched, marker and all. Every database here is created with
/// the test prefix and a fresh GUID, so nothing outside this test's own scope is ever a candidate
/// for the drop being exercised.
/// </summary>
[Collection("sql")]
public sealed class DatabaseSafetyTests(SqlServerFixture sql)
{
    private const string SentinelTable = "PleaseDoNotDropMe";

    private static string NewDatabaseName() =>
        LabDataOptions.TestDatabasePrefix + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task ResettingAnUnmarkedDatabaseLeavesItCompletelyIntact()
    {
        var database = NewDatabaseName();
        await CreateForeignDatabaseAsync(database, withFakeLabMetadata: false);

        var ex = await Assert.ThrowsAsync<NotALabDatabaseException>(
            () => LabDbMigrator.ResetLabDatabaseAsync(
                sql.MasterConnectionString, database, CancellationToken.None));

        Assert.Contains("no lab metadata", ex.Message);
        await AssertSentinelSurvivedAsync(database);
    }

    [Fact]
    public async Task ResettingADatabaseWithTheWrongLabIdentityLeavesItCompletelyIntact()
    {
        var database = NewDatabaseName();
        await CreateForeignDatabaseAsync(database, withFakeLabMetadata: true);

        var ex = await Assert.ThrowsAsync<NotALabDatabaseException>(
            () => LabDbMigrator.ResetLabDatabaseAsync(
                sql.MasterConnectionString, database, CancellationToken.None));

        Assert.Contains("LabIdentity", ex.Message);
        await AssertSentinelSurvivedAsync(database);
    }

    [Fact]
    public async Task MigratingAnUnmarkedDatabaseIsAlsoRefused()
    {
        var database = NewDatabaseName();
        await CreateForeignDatabaseAsync(database, withFakeLabMetadata: false);

        await Assert.ThrowsAsync<NotALabDatabaseException>(
            () => LabDbMigrator.EnsureLabDatabaseAsync(
                sql.MasterConnectionString, database, CancellationToken.None));

        await AssertSentinelSurvivedAsync(database);
    }

    [Fact]
    public async Task ResettingAProperlyMarkedLabDatabaseRebuildsIt()
    {
        var database = await sql.CreateAndMigrateTestDatabaseAsync();

        await using (var seeded = sql.CreateContext(database))
        {
            seeded.Categories.Add(new Category { Id = 4242, Name = "Doomed" });
            await seeded.SaveChangesAsync();
        }

        await LabDbMigrator.ResetLabDatabaseAsync(
            sql.MasterConnectionString, database, CancellationToken.None);

        await using var db = sql.CreateContext(database);
        Assert.Empty(db.Categories);
        // The rebuilt database is migrated and marked, so it is usable immediately.
        Assert.Contains(
            LabDbMigrator.LabIdentityValue,
            db.LabMetadata.Select(m => m.MetadataValue).ToList());
    }

    [Fact]
    public async Task AnEmptyDatabaseIsAdoptableBecauseThereIsNothingToLose()
    {
        // This is the half-finished state a create-then-migration-failure leaves behind; refusing
        // it would strand the lab with no way forward but a manual drop.
        var database = NewDatabaseName();
        await ExecuteOnMasterAsync($"CREATE DATABASE [{database}]");

        await LabDbMigrator.ResetLabDatabaseAsync(
            sql.MasterConnectionString, database, CancellationToken.None);

        await using var db = sql.CreateContext(database);
        Assert.Contains(
            LabDbMigrator.LabIdentityValue,
            db.LabMetadata.Select(m => m.MetadataValue).ToList());
    }

    [Fact]
    public async Task ADatabaseNameOutsideThePatternIsRefusedBeforeAnyConnection()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => LabDbMigrator.ResetLabDatabaseAsync(
                sql.MasterConnectionString, "master];DROP DATABASE [x", CancellationToken.None));
    }

    /// <summary>
    /// Builds a database that is not the lab's: a sentinel table with a row, optionally plus a
    /// LabMetadata table carrying somebody else's identity value.
    /// </summary>
    private async Task CreateForeignDatabaseAsync(string database, bool withFakeLabMetadata)
    {
        await ExecuteOnMasterAsync($"CREATE DATABASE [{database}]");

        await ExecuteAsync(
            database,
            $"CREATE TABLE dbo.{SentinelTable} (Id int NOT NULL PRIMARY KEY, Payload nvarchar(64) NOT NULL)");
        await ExecuteAsync(database, $"INSERT INTO dbo.{SentinelTable} (Id, Payload) VALUES (1, N'precious')");

        if (withFakeLabMetadata)
        {
            await ExecuteAsync(
                database,
                """
                CREATE TABLE dbo.LabMetadata (
                    MetadataKey nvarchar(64) NOT NULL PRIMARY KEY,
                    MetadataValue nvarchar(256) NOT NULL,
                    UpdatedAtUtc datetime2 NOT NULL)
                """);
            await ExecuteAsync(
                database,
                $"INSERT INTO dbo.LabMetadata (MetadataKey, MetadataValue, UpdatedAtUtc) "
                + $"VALUES (N'{LabDbMigrator.LabIdentityKey}', N'someone-elses-app', SYSUTCDATETIME())");
        }
    }

    private async Task AssertSentinelSurvivedAsync(string database)
    {
        await using var connection = new SqlConnection(sql.ConnectionStringFor(database));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Payload FROM dbo.{SentinelTable} WHERE Id = 1";

        Assert.Equal("precious", await command.ExecuteScalarAsync());
    }

    private Task ExecuteOnMasterAsync(string sqlText) => ExecuteAsync(null, sqlText);

    private async Task ExecuteAsync(string? database, string sqlText)
    {
        var connectionString = database is null
            ? sql.MasterConnectionString
            : sql.ConnectionStringFor(database);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        await command.ExecuteNonQueryAsync();
    }
}
