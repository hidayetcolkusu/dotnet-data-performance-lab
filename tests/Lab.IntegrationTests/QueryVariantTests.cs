using System.Xml.Linq;
using DataPerformanceLab.Catalog;
using DataPerformanceLab.Cli.Experiments;
using DataPerformanceLab.Configuration;
using DataPerformanceLab.Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DataPerformanceLab.IntegrationTests;

/// <summary>
/// Correctness gate for the query experiments. These tests assert that the variants are
/// interchangeable and that the tooling collects real evidence; they deliberately never
/// assert on a speedup or on which physical operator the optimizer picked.
/// </summary>
[Collection("sql")]
public sealed class QueryVariantTests(SqlServerFixture sql) : IAsyncLifetime
{
    private string _database = string.Empty;

    public async Task InitializeAsync()
    {
        _database = await sql.CreateAndMigrateTestDatabaseAsync();
        await using var db = sql.CreateContext(_database);
        await new CatalogSeeder().SeedAsync(db, SeedProfile.Ci, CancellationToken.None);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private CatalogIndexState IndexState =>
        new(sql.ConnectionStringFor(_database), _database);

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    public async Task AllVariantsReturnTheSameOrderedResult(int categoryId)
    {
        var index = IndexState;
        var hashes = new List<string>();
        var rowCounts = new List<int>();

        foreach (var variant in Enum.GetValues<QueryVariantId>())
        {
            // Q1's two variants only differ by index presence, so force each state explicitly.
            await index.SetIndexAsync(variant is not QueryVariantId.IndexAbsent, CancellationToken.None);

            await using var db = sql.CreateContext(_database);
            var items = await QueryVariants.ExecuteAsync(db, variant, categoryId, CancellationToken.None);

            hashes.Add(QueryVariants.HashResults(items));
            rowCounts.Add(items.Count);
        }

        await index.SetIndexAsync(true, CancellationToken.None);

        Assert.Single(hashes.Distinct(StringComparer.Ordinal));
        Assert.Single(rowCounts.Distinct());
        Assert.NotEmpty(rowCounts);
        Assert.True(rowCounts[0] > 0, $"Category {categoryId} produced no rows; the fixture cannot compare variants.");
    }

    [Fact]
    public async Task ExperimentVariantMatchesTheQueryTheApiServes()
    {
        await using var db = sql.CreateContext(_database);

        var apiPage = await new ProductQueries(db).ListAsync(
            categoryId: 1, pageSize: QueryVariants.PageSize, cursor: null, CancellationToken.None);
        var experimentItems = await QueryVariants.ExecuteSqlProjectionAsync(db, 1, CancellationToken.None);

        Assert.Equal(
            QueryVariants.HashResults(apiPage.Items),
            QueryVariants.HashResults(experimentItems));
    }

    [Fact]
    public async Task EntityMaterializationAndSqlProjectionAgree()
    {
        await IndexState.SetIndexAsync(true, CancellationToken.None);
        await using var db = sql.CreateContext(_database);

        var entityMapped = await QueryVariants.ExecuteEntityThenMapAsync(db, 1, CancellationToken.None);
        var sqlProjected = await QueryVariants.ExecuteSqlProjectionAsync(db, 1, CancellationToken.None);

        Assert.Equal(entityMapped, sqlProjected);
    }

    [Fact]
    public async Task PlanCollectorWritesValidShowplanXmlAndStatistics()
    {
        await IndexState.SetIndexAsync(true, CancellationToken.None);

        var interceptor = new CommandCaptureInterceptor();
        await using (var db = sql.CreateContext(_database, interceptor))
        {
            await QueryVariants.ExecuteSqlProjectionAsync(db, 1, CancellationToken.None);
        }

        var captured = interceptor.LastCommand;
        Assert.NotNull(captured);
        Assert.NotEmpty(captured.Parameters);

        var collector = new SqlPlanCollector(sql.ConnectionStringFor(_database));
        var plan = await collector.CollectAsync(captured, CancellationToken.None);

        // The plan must parse as XML. Which operator the optimizer chose is reported, not asserted.
        var document = XDocument.Parse(plan.PlanXml);
        Assert.Equal("ShowPlanXML", document.Root!.Name.LocalName);
        Assert.Contains("logical reads", plan.StatisticsText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(QueryVariants.PageSize, plan.RowCount);
    }

    [Fact]
    public async Task RunnerCompletesTheProtocolWithIdenticalResultHashes()
    {
        var runner = new QueryExperimentRunner(() => sql.CreateContext(_database), IndexState);

        var result = await runner.RunAsync(QueryExperimentKind.Projection, 1, CancellationToken.None);

        Assert.True(result.ResultsIdentical);
        Assert.Equal(
            QueryExperimentRunner.RoundCount * QueryExperimentRunner.SamplesPerRound * 2,
            result.Samples.Count);
        Assert.Equal(
            new[] { "AB", "BA", "AB", "BA", "AB" },
            result.Samples.GroupBy(s => s.Round).OrderBy(g => g.Key).Select(g => g.First().RoundOrder));
        Assert.All(result.RoundStats, s => Assert.Equal(QueryExperimentRunner.SamplesPerRound, s.SampleCount));
    }

    [Fact]
    public async Task IndexToggleRestoresTheStateItFound()
    {
        var index = IndexState;
        await index.SetIndexAsync(true, CancellationToken.None);

        var observedInside = false;
        await index.WithIndexAsync(false, async () =>
        {
            observedInside = await index.ExistsAsync(CancellationToken.None);
        }, CancellationToken.None);

        Assert.False(observedInside);
        Assert.True(await index.ExistsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task IndexToggleRestoresStateEvenWhenTheBodyThrows()
    {
        var index = IndexState;
        await index.SetIndexAsync(true, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            index.WithIndexAsync(false, () => throw new InvalidOperationException("boom"), CancellationToken.None));

        Assert.True(await index.ExistsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PreflightDetectsAndRepairsAHalfFinishedRun()
    {
        var index = IndexState;
        await index.SetIndexAsync(true, CancellationToken.None);

        // Simulate a crash between the DROP and the restoring finally: the marker survives,
        // the index does not.
        await WriteRestoreMarkerAsync();
        await index.SetIndexAsync(false, CancellationToken.None);

        var note = await index.PreflightAsync(CancellationToken.None);

        Assert.NotNull(note);
        Assert.Contains("crashed", note, StringComparison.OrdinalIgnoreCase);
        Assert.True(await index.ExistsAsync(CancellationToken.None));

        // The marker is cleared, so a healthy second run reports nothing.
        Assert.Null(await index.PreflightAsync(CancellationToken.None));
    }

    [Fact]
    public void ExperimentToolingRefusesTheApiDatabase()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            CatalogIndexState.EnsureExperimentDatabase(LabDataOptions.ApiDatabaseName));

        Assert.Contains(LabDataOptions.ExperimentDatabaseName, ex.Message, StringComparison.Ordinal);
        Assert.Contains(LabDataOptions.ApiDatabaseName, ex.Message, StringComparison.Ordinal);

        // The one allowed target does not throw.
        CatalogIndexState.EnsureExperimentDatabase(LabDataOptions.ExperimentDatabaseName);
    }

    private async Task WriteRestoreMarkerAsync()
    {
        await using var connection = new SqlConnection(sql.ConnectionStringFor(_database));
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.LabMetadata (MetadataKey, MetadataValue, UpdatedAtUtc)
            VALUES (@key, @value, SYSUTCDATETIME());
            """,
            connection);
        command.Parameters.AddWithValue("@key", CatalogIndexState.MarkerMetadataKey);
        command.Parameters.AddWithValue(
            "@value",
            $$"""{"indexName":"{{CatalogIndexes.CategoryActiveCreatedId}}","shouldExistAfterRun":true,"startedAtUtc":"2026-09-11T00:00:00Z"}""");
        await command.ExecuteNonQueryAsync();
    }
}
