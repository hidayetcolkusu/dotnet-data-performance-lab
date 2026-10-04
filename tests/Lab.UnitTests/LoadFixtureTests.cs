using DataPerformanceLab.Cli.Experiments;
using Xunit;

namespace DataPerformanceLab.UnitTests;

public sealed class LoadFixtureTests
{
    private static IReadOnlyList<long> Catalog(int count) =>
        [.. Enumerable.Range(1, count).Select(i => (long)i)];

    [Fact]
    public void TheFixtureIsSpreadEvenlyAcrossTheCatalogNotTakenFromTheHead()
    {
        var selected = LoadFixture.SelectIds(Catalog(100_000), 1_000);

        Assert.Equal(1_000, selected.Count);
        Assert.Equal(1, selected[0]);
        Assert.Equal(101, selected[1]);
        Assert.Equal(99_901, selected[^1]);
        Assert.Equal(selected.Count, selected.Distinct().Count());
    }

    [Fact]
    public void SelectionIsDeterministicSoTwoRunsDriveTheSameSkus()
    {
        var catalog = Catalog(12_345);

        Assert.Equal(LoadFixture.SelectIds(catalog, 500), LoadFixture.SelectIds(catalog, 500));
    }

    [Fact]
    public void AFixtureAsLargeAsTheCatalogCoversEveryProduct()
    {
        Assert.Equal(Catalog(1_000), LoadFixture.SelectIds(Catalog(1_000), 1_000));
    }

    [Fact]
    public void SelectionSurvivesNonContiguousIds()
    {
        // Ids come from an identity column, so a reseed or a delete leaves gaps.
        var catalog = new long[] { 5, 17, 23, 44, 61, 62 };

        Assert.Equal([5, 23, 61], LoadFixture.SelectIds(catalog, 3));
    }

    [Fact]
    public void ACatalogSmallerThanTheFixtureIsAnErrorRatherThanAShortFixture()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadFixture.SelectIds(Catalog(999), 1_000));

        Assert.Contains("999 products", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveFixtureSizeIsRejected(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LoadFixture.SelectIds(Catalog(10), count));
    }

    [Fact]
    public void TheFixtureHashCoversTheExpectedValuesNotOnlyTheSkus()
    {
        var baseline = new LoadFixtureProduct(1, "SKU-0000000001", "Widget", 3, 12.50m, true);

        Assert.Equal(LoadFixture.ComputeHash([baseline]), LoadFixture.ComputeHash([baseline]));
        Assert.NotEqual(
            LoadFixture.ComputeHash([baseline]),
            LoadFixture.ComputeHash([baseline with { UnitPrice = 12.51m }]));
        Assert.NotEqual(
            LoadFixture.ComputeHash([baseline]),
            LoadFixture.ComputeHash([baseline with { IsActive = false }]));
    }

    [Fact]
    public void TheFixtureHashIsOrderSensitive()
    {
        var first = new LoadFixtureProduct(1, "SKU-0000000001", "A", 1, 10m, true);
        var second = new LoadFixtureProduct(2, "SKU-0000000002", "B", 2, 20m, true);

        Assert.NotEqual(
            LoadFixture.ComputeHash([first, second]),
            LoadFixture.ComputeHash([second, first]));
    }
}
