using DataPerformanceLab.Cli.Experiments;
using Xunit;

namespace DataPerformanceLab.UnitTests;

public sealed class ExperimentStatisticsTests
{
    [Theory]
    [InlineData(50, 10)]
    [InlineData(95, 19)]
    [InlineData(99, 20)]
    [InlineData(100, 20)]
    public void PercentileUsesNearestRank(double percentile, double expected)
    {
        var sorted = Enumerable.Range(1, 20).Select(i => (double)i).ToArray();

        Assert.Equal(expected, QueryExperimentRunner.Percentile(sorted, percentile));
    }

    [Fact]
    public void PercentileOfASingleSampleIsThatSample()
    {
        Assert.Equal(4.2, QueryExperimentRunner.Percentile([4.2], 95));
    }

    [Fact]
    public void PercentileRejectsAnEmptySampleSet()
    {
        Assert.Throws<ArgumentException>(() => QueryExperimentRunner.Percentile([], 95));
    }

    [Fact]
    public void SummarizeReportsTheRealSpreadOfTheSamples()
    {
        var stats = QueryExperimentRunner.Summarize(3, QueryVariantId.SqlProjection, [5, 1, 3, 2, 4]);

        Assert.Equal(3, stats.Round);
        Assert.Equal(QueryVariantId.SqlProjection, stats.Variant);
        Assert.Equal(5, stats.SampleCount);
        Assert.Equal(1, stats.MinMs);
        Assert.Equal(5, stats.MaxMs);
        Assert.Equal(3, stats.MeanMs);
        Assert.Equal(3, stats.MedianMs);
    }

    [Fact]
    public void EachExperimentAlternatesBetweenExactlyTwoVariants()
    {
        var (indexA, indexB) = QueryVariants.VariantsFor(QueryExperimentKind.Index);
        var (projectionA, projectionB) = QueryVariants.VariantsFor(QueryExperimentKind.Projection);

        Assert.Equal(QueryVariantId.IndexAbsent, indexA);
        Assert.Equal(QueryVariantId.IndexPresent, indexB);
        Assert.Equal(QueryVariantId.EntityThenMap, projectionA);
        Assert.Equal(QueryVariantId.SqlProjection, projectionB);
    }

    [Fact]
    public void ResultHashIsOrderSensitive()
    {
        var first = new Catalog.ProductListItem(1, "SKU-1", "A", 1, 10m, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var second = new Catalog.ProductListItem(2, "SKU-2", "B", 1, 20m, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        Assert.NotEqual(
            QueryVariants.HashResults([first, second]),
            QueryVariants.HashResults([second, first]));
        Assert.Equal(
            QueryVariants.HashResults([first, second]),
            QueryVariants.HashResults([first, second]));
    }
}
