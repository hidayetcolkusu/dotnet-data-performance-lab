using System.Text.Json;
using DataPerformanceLab.Cli.Experiments;
using Xunit;

namespace DataPerformanceLab.UnitTests;

public sealed class ExperimentSummaryTests
{
    private const string Off = LoadRunSummary.CacheOff;

    private const string On = LoadRunSummary.CacheOn;

    private static string SummaryJson(
        string arm = Off,
        int repetition = 1,
        string order = "AB",
        long droppedIterations = 0,
        long checksFailed = 0,
        long httpReqFailed = 0,
        long iterations = 1200,
        long latencyCount = 1200,
        double p95 = 12.5,
        double med = 4.0,
        string schema = LoadRunSummary.ExpectedSchema)
    {
        var document = new
        {
            schema,
            arm,
            repetition,
            order,
            baseUrl = "http://127.0.0.1:8080",
            fixtureHash = "ABCDEF0123456789",
            fixtureSkuCount = 1000,
            rate = 20,
            duration = "60s",
            metrics = new
            {
                iterations,
                httpReqs = iterations,
                httpReqFailed,
                checksTotal = iterations * 9,
                checksFailed,
                droppedIterations,
                detailLatencyMs = new
                {
                    avgMs = 5.0,
                    medMs = med,
                    p95Ms = p95,
                    p99Ms = p95 + 5,
                    minMs = 1.0,
                    maxMs = p95 + 20,
                    count = latencyCount
                }
            }
        };

        return JsonSerializer.Serialize(document, ExperimentManifest.JsonOptions);
    }

    /// <summary>Positional wrapper so the tests read as one run at a time.</summary>
    private static LoadRunRecord Read(
        int repetition,
        string order,
        string arm,
        string summaryFile,
        string? json,
        LoadRunExpectation? expectation = null,
        int? warmupExit = null,
        int? measuredExit = null,
        IReadOnlyList<string>? missingArtifacts = null,
        long? measuredWindowLookups = null) =>
        LoadComparison.ReadRun(
            new LoadRunInput(
                repetition, order, arm, summaryFile, warmupExit, measuredExit,
                RequiredArtifacts: null, MeasuredWindowLookups: measuredWindowLookups),
            json,
            expectation,
            missingArtifacts);

    private static LoadRunRecord Run(
        int repetition, string arm, double p95, double med = 4.0, string order = "AB") =>
        Read(
            repetition,
            order,
            arm,
            $"runs/{repetition}-{order}-{arm}.summary.json",
            SummaryJson(arm, repetition, order, p95: p95, med: med));

    [Fact]
    public void AValidSummaryIsParsedIntoAUsableRun()
    {
        var run = Run(1, Off, p95: 12.5);

        Assert.True(run.IsValid);
        Assert.Null(run.InvalidReason);
        Assert.Equal(12.5, run.P95Ms);
        Assert.Equal(Off, run.Summary!.Arm);
        Assert.Equal(1000, run.Summary.FixtureSkuCount);
    }

    [Fact]
    public void ARunWithDroppedIterationsIsInvalidBecauseTheRateWasNotSustained()
    {
        var run = Read(
            1, "AB", Off, "runs/1-AB-cache-off.summary.json", SummaryJson(droppedIterations: 3));

        Assert.False(run.IsValid);
        Assert.Contains("dropped iterations", run.InvalidReason);
    }

    [Fact]
    public void ARunWithFailedChecksIsInvalidEvenThoughItHasLatencyNumbers()
    {
        var run = Read(
            1, "AB", Off, "runs/1-AB-cache-off.summary.json", SummaryJson(checksFailed: 1));

        Assert.False(run.IsValid);
        Assert.Contains("failed checks", run.InvalidReason);
    }

    [Fact]
    public void ARunWithFailedRequestsIsInvalid()
    {
        var run = Read(
            1, "AB", Off, "runs/1-AB-cache-off.summary.json", SummaryJson(httpReqFailed: 2));

        Assert.False(run.IsValid);
        Assert.Contains("failed HTTP requests", run.InvalidReason);
    }

    [Fact]
    public void ARunThatExecutedNothingIsInvalidRatherThanAZeroLatencyResult()
    {
        var run = Read(
            1, "AB", Off, "runs/1-AB-cache-off.summary.json", SummaryJson(iterations: 0, latencyCount: 0));

        Assert.False(run.IsValid);
        Assert.Contains("no iterations", run.InvalidReason);
    }

    [Fact]
    public void AMissingSummaryFileIsReportedRatherThanSkipped()
    {
        var run = Read(2, "BA", On, "runs/2-BA-cache-on.summary.json", null);

        Assert.False(run.IsValid);
        Assert.Contains("missing", run.InvalidReason);
        Assert.Null(run.Summary);
    }

    [Fact]
    public void AnUnknownSchemaIsRejectedInsteadOfPartiallyRead()
    {
        var run = Read(
            1, "AB", Off, "runs/1-AB-cache-off.summary.json", SummaryJson(schema: "dpl.load-summary.v9"));

        Assert.False(run.IsValid);
        Assert.Contains("schema", run.InvalidReason);
    }

    [Fact]
    public void ASummaryReportingADifferentArmThanItWasRunForIsRejected()
    {
        // Guards against an orchestration bug copying the wrong file into a run slot.
        var run = Read(
            1, "AB", On, "runs/1-AB-cache-on.summary.json", SummaryJson(arm: Off));

        Assert.False(run.IsValid);
        Assert.Contains("reports arm", run.InvalidReason);
    }

    [Fact]
    public void MalformedJsonIsReportedAsAnInvalidRun()
    {
        var run = Read(1, "AB", Off, "runs/1-AB-cache-off.summary.json", "{ not json");

        Assert.False(run.IsValid);
        Assert.Contains("not valid JSON", run.InvalidReason);
    }

    [Fact]
    public void TheAggregateNeverExposesAPooledPercentile()
    {
        // The whole point of the aggregation rule: per-run p95 values may be summarized, but
        // no property may claim to be *the* p95 of the arm. That number cannot be computed
        // from per-run percentiles, and a reader would take it as if it could.
        var names = typeof(ArmAggregate).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain("P95Ms", names);
        Assert.DoesNotContain("P99Ms", names);
        Assert.DoesNotContain("GlobalP95Ms", names);
        Assert.Contains("MedianOfRunP95Ms", names);
        Assert.Contains("MedianOfRunMedianMs", names);
    }

    [Fact]
    public void AcrossRunsTheAggregateIsTheMedianOfTheRunP95sNotAPercentileOfThem()
    {
        var runs = new[]
        {
            Run(1, Off, p95: 10),
            Run(2, Off, p95: 20),
            Run(3, Off, p95: 30),
            Run(4, Off, p95: 40),
            Run(5, Off, p95: 50)
        };

        var aggregate = LoadComparison.Aggregate(Off, runs);

        Assert.Equal(5, aggregate.ValidRunCount);
        Assert.Equal(30, aggregate.MedianOfRunP95Ms);
        Assert.Equal(10, aggregate.MinRunP95Ms);
        Assert.Equal(50, aggregate.MaxRunP95Ms);

        // A "p95 of the p95s" would be 50 here. The spread is published as min/max instead,
        // so nobody can read the top of the range as a percentile of the requests.
        Assert.NotEqual(QueryExperimentRunner.Percentile([10, 20, 30, 40, 50], 95), aggregate.MedianOfRunP95Ms);
    }

    [Fact]
    public void AnInvalidRunIsExcludedFromTheAggregateButStillCounted()
    {
        var runs = new[]
        {
            Run(1, Off, p95: 10),
            Run(2, Off, p95: 30),
            Read(3, "AB", Off, "runs/3-AB-cache-off.summary.json", SummaryJson(droppedIterations: 1))
        };

        var aggregate = LoadComparison.Aggregate(Off, runs);

        Assert.Equal(3, aggregate.RunCount);
        Assert.Equal(2, aggregate.ValidRunCount);

        // Nearest-rank over [10, 30], so the lower middle rather than the interpolated 20.
        // Every published figure stays a value some run actually produced.
        Assert.Equal(10, aggregate.MedianOfRunP95Ms);
        Assert.Equal(30, aggregate.MaxRunP95Ms);
    }

    [Fact]
    public void EveryAcrossRunFigureIsAValueSomeRunActuallyProduced()
    {
        var p95s = new double[] { 11, 17, 23, 29 };
        var runs = p95s.Select((p95, i) => Run(i + 1, Off, p95)).ToArray();

        var aggregate = LoadComparison.Aggregate(Off, runs);

        Assert.Contains(aggregate.MedianOfRunP95Ms!.Value, p95s);
        Assert.Contains(aggregate.MinRunP95Ms!.Value, p95s);
        Assert.Contains(aggregate.MaxRunP95Ms!.Value, p95s);
    }

    [Fact]
    public void AnArmWithNoValidRunReportsNoLatencyAtAll()
    {
        var aggregate = LoadComparison.Aggregate(
            On,
            [Read(1, "AB", On, "runs/1-AB-cache-on.summary.json", null)]);

        Assert.Equal(0, aggregate.ValidRunCount);
        Assert.Null(aggregate.MedianOfRunP95Ms);
        Assert.Null(aggregate.MinRunP95Ms);
        Assert.Equal(0, aggregate.TotalRequests);
    }

    [Fact]
    public void AggregatesOnlyReadTheirOwnArm()
    {
        var runs = new[] { Run(1, Off, p95: 10), Run(1, On, p95: 90) };

        Assert.Equal(10, LoadComparison.Aggregate(Off, runs).MedianOfRunP95Ms);
        Assert.Equal(90, LoadComparison.Aggregate(On, runs).MedianOfRunP95Ms);
    }

    [Fact]
    public void ABalancedComparisonOfValidRunsIsValid()
    {
        var runs = new[]
        {
            Run(1, Off, p95: 30),
            Run(1, On, p95: 8),
            Run(2, Off, p95: 32, order: "BA"),
            Run(2, On, p95: 9, order: "BA")
        };

        var report = LoadComparison.BuildReport(runs);

        Assert.Equal(LoadComparisonReport.StatusValid, report.Status);
        Assert.Empty(report.InvalidReasons);
        Assert.Equal(2, report.Arms.Count);
    }

    [Fact]
    public void OneBrokenRunInvalidatesTheWholeComparison()
    {
        var runs = new[]
        {
            Run(1, Off, p95: 30),
            Run(1, On, p95: 8),
            Read(2, "BA", Off, "runs/2-BA-cache-off.summary.json", null),
            Run(2, On, p95: 9, order: "BA")
        };

        var report = LoadComparison.BuildReport(runs);

        Assert.Equal(LoadComparisonReport.StatusInvalid, report.Status);
        Assert.Contains(report.InvalidReasons, r => r.Contains("missing"));
        Assert.Contains(report.InvalidReasons, r => r.Contains("unbalanced"));
    }

    [Fact]
    public void AComparisonMissingAnEntireArmIsInvalid()
    {
        var report = LoadComparison.BuildReport([Run(1, Off, p95: 30)]);

        Assert.Equal(LoadComparisonReport.StatusInvalid, report.Status);
        Assert.Contains(report.InvalidReasons, r => r.Contains($"arm '{On}' has no valid run"));
    }

    [Fact]
    public void TheMarkdownKeepsOneRowPerRunAndLabelsTheAcrossRunStatistic()
    {
        var runs = new[]
        {
            Run(1, Off, p95: 30.5),
            Run(1, On, p95: 8.25),
            Run(2, Off, p95: 31.5, order: "BA"),
            Run(2, On, p95: 9.25, order: "BA")
        };

        var markdown = LoadComparison.ToMarkdown(LoadComparison.BuildReport(runs));

        // Every run keeps its own row: the spread between repetitions stays visible.
        Assert.Equal(4, markdown.Split('\n').Count(line => line.StartsWith("| 1 |") || line.StartsWith("| 2 |")));
        Assert.Contains("30.50", markdown);
        Assert.Contains("31.50", markdown);

        // The across-run column says what it is, and the table does not offer a bare "p95".
        Assert.Contains("median of run p95 ms", markdown);
        Assert.Contains("Percentiles are **not** pooled across runs", markdown);
    }

    // ---- the comparison must measure one workload ---------------------------

    private const string DeclaredFixtureHash = "ABCDEF0123456789";

    private static LoadRunExpectation Expected(
        int repetitions = 2,
        int rate = 20,
        string duration = "60s",
        string fixtureHash = DeclaredFixtureHash,
        int fixtureSkuCount = 1000) =>
        new(repetitions, "default", rate, duration, "30s", fixtureHash, fixtureSkuCount);

    [Fact]
    public void ARunThatFacedADifferentFixtureIsInvalid()
    {
        var json = SummaryJson();
        var run = Read(
            1, "AB", Off, "runs/1-AB-cache-off.summary.json", json,
            Expected(fixtureHash: "0000000000000000"));

        Assert.False(run.IsValid);
        Assert.Contains("fixtureHash", run.InvalidReason);
    }

    [Theory]
    [InlineData(40, "60s", "rate")]
    [InlineData(20, "30s", "duration")]
    public void ARunAtADifferentRateOrDurationIsInvalid(int rate, string duration, string expectedWord)
    {
        var run = Read(
            1, "AB", Off, "runs/1-AB-cache-off.summary.json", SummaryJson(),
            Expected(rate: rate, duration: duration));

        Assert.False(run.IsValid);
        Assert.Contains(expectedWord, run.InvalidReason);
    }

    [Fact]
    public void AFixtureOfADifferentSizeIsInvalidEvenWhenItsHashIsDeclared()
    {
        var run = Read(
            1, "AB", Off, "runs/1-AB-cache-off.summary.json", SummaryJson(),
            Expected(fixtureSkuCount: 500));

        Assert.False(run.IsValid);
        Assert.Contains("fixtureSkuCount", run.InvalidReason);
    }

    [Fact]
    public void ASummaryFiledUnderTheWrongRepetitionIsInvalid()
    {
        // The file says repetition 3; the index filed it as repetition 1. One of them is wrong
        // and there is no way to tell which, so the run is not evidence.
        var run = Read(
            1, "AB", Off, "runs/1-AB-cache-off.summary.json", SummaryJson(repetition: 3));

        Assert.False(run.IsValid);
        Assert.Contains("repetition 3", run.InvalidReason);
    }

    [Fact]
    public void ASummaryFiledUnderTheWrongOrderIsInvalid()
    {
        var run = Read(
            1, "AB", Off, "runs/1-AB-cache-off.summary.json", SummaryJson(order: "BA"));

        Assert.False(run.IsValid);
        Assert.Contains("order 'BA'", run.InvalidReason);
    }

    [Fact]
    public void ANonzeroK6ExitIsInvalidEvenWhenTheSummaryLooksPerfect()
    {
        var run = Read(
            1, "AB", Off, "runs/1-AB-cache-off.summary.json", SummaryJson(), measuredExit: 99);

        Assert.False(run.IsValid);
        Assert.Contains("k6 exited 99", run.InvalidReason);
    }

    [Fact]
    public void AFailedWarmupIsInvalidBecauseTheMeasuredRunDidNotStartWarm()
    {
        var run = Read(
            1, "AB", On, "runs/1-AB-cache-on.summary.json", SummaryJson(arm: On), warmupExit: 1);

        Assert.False(run.IsValid);
        Assert.Contains("warmup process exited 1", run.InvalidReason);
    }

    [Fact]
    public void AMissingRequiredArtifactIsInvalid()
    {
        var run = Read(
            1, "AB", Off, "runs/1-AB-cache-off.summary.json", SummaryJson(),
            missingArtifacts: ["runs/1-AB-cache-off.cache-metrics-delta.json"]);

        Assert.False(run.IsValid);
        Assert.Contains("cache-metrics-delta.json", run.InvalidReason);
    }

    // ---- the counters and the latency must describe the same window --------------------

    [Fact]
    public void CountersThatMatchTheRequestCountAreValid()
    {
        // 1,200 iterations in SummaryJson's default, so 1,200 cache lookups is exactly right.
        var run = Read(
            1, "AB", On, "runs/1-AB-cache-on.summary.json", SummaryJson(arm: On, iterations: 1200),
            measuredWindowLookups: 1200);

        Assert.True(run.IsValid, run.InvalidReason);
    }

    [Theory]
    [InlineData(1201)]
    [InlineData(1199)]
    public void CountersThatDoNotAccountForTheRequestsAreInvalid(long lookups)
    {
        // This is the shape of the original defect: counters describing a different window than the
        // latency. It also catches a run k6 abandoned while the API kept serving and kept counting.
        var run = Read(
            1, "AB", On, "runs/1-AB-cache-on.summary.json", SummaryJson(arm: On, iterations: 1200),
            measuredWindowLookups: lookups);

        Assert.False(run.IsValid);
        Assert.Contains("do not describe the same window", run.InvalidReason);
    }

    [Fact]
    public void ARunWithNoDeltaFileIsNotHeldToTheCounterCheck()
    {
        // A directory written before the two-snapshot orchestration still reports; it simply
        // cannot be checked this way, and is not failed for that.
        var run = Read(1, "AB", On, "runs/1-AB-cache-on.summary.json", SummaryJson(arm: On));

        Assert.True(run.IsValid, run.InvalidReason);
    }

    [Fact]
    public void ADuplicatePairIsInvalid()
    {
        var runs = new[]
        {
            Run(1, Off, p95: 30),
            Run(1, Off, p95: 31),
            Run(1, On, p95: 8)
        };

        var report = LoadComparison.BuildReport(runs);

        Assert.Equal(LoadComparisonReport.StatusInvalid, report.Status);
        Assert.Contains(report.InvalidReasons, r => r.Contains("each repetition is one run per arm"));
    }

    [Fact]
    public void AnIncompletePairIsInvalid()
    {
        var runs = new[]
        {
            Run(1, Off, p95: 30),
            Run(1, On, p95: 8),
            Run(2, Off, p95: 32, order: "BA")
        };

        var report = LoadComparison.BuildReport(runs);

        Assert.Equal(LoadComparisonReport.StatusInvalid, report.Status);
        Assert.Contains(report.InvalidReasons, r => r.Contains("repetition 2 is not a complete pair"));
    }

    [Fact]
    public void AnOrderLabelThatContradictsTheAlternationIsInvalid()
    {
        // Repetition 2 must be a BA round; labelling it AB makes the drift control unverifiable.
        var runs = new[]
        {
            Run(1, Off, p95: 30),
            Run(1, On, p95: 8),
            Run(2, Off, p95: 32),
            Run(2, On, p95: 9)
        };

        var report = LoadComparison.BuildReport(runs);

        Assert.Equal(LoadComparisonReport.StatusInvalid, report.Status);
        Assert.Contains(report.InvalidReasons, r => r.Contains("'BA' round"));
    }

    [Fact]
    public void FewerRepetitionsThanDeclaredIsInvalidRatherThanAShorterResult()
    {
        // A one-repetition smoke must never pass as the five-repetition default benchmark.
        var runs = new[] { Run(1, Off, p95: 30), Run(1, On, p95: 8) };

        var report = LoadComparison.BuildReport(runs, Expected(repetitions: 5));

        Assert.Equal(LoadComparisonReport.StatusInvalid, report.Status);
        Assert.Contains(report.InvalidReasons, r => r.Contains("declared 5"));
    }

    [Fact]
    public void AShortProfileIsValidWhenItIsTheProfileThatWasDeclared()
    {
        var runs = new[] { Run(1, Off, p95: 30), Run(1, On, p95: 8) };

        var report = LoadComparison.BuildReport(runs, Expected(repetitions: 1));

        Assert.Equal(LoadComparisonReport.StatusValid, report.Status);
        Assert.Empty(report.InvalidReasons);
    }

    [Fact]
    public void TheCompleteDefaultProfileIsValid()
    {
        var runs = Enumerable.Range(1, 5)
            .SelectMany(rep => new[]
            {
                Run(rep, Off, p95: 30 + rep, order: rep % 2 == 1 ? "AB" : "BA"),
                Run(rep, On, p95: 8 + rep, order: rep % 2 == 1 ? "AB" : "BA")
            })
            .ToArray();

        var report = LoadComparison.BuildReport(runs, Expected(repetitions: 5));

        Assert.Equal(LoadComparisonReport.StatusValid, report.Status);
        Assert.Empty(report.InvalidReasons);
        Assert.All(report.Arms, arm => Assert.Equal(5, arm.ValidRunCount));
    }

    [Fact]
    public void RunsThatDisagreeWithEachOtherAreInvalidEvenWithNoDeclaredExpectation()
    {
        // No expectation block at all, so the only available check is mutual consistency —
        // and these two arms did not face the same load.
        var runs = new[]
        {
            Read(1, "AB", Off, "runs/1-AB-cache-off.summary.json", SummaryJson(arm: Off)),
            LoadComparison.ReadRun(
                new LoadRunInput(1, "AB", On, "runs/1-AB-cache-on.summary.json"),
                SummaryJson(arm: On).Replace("\"rate\": 20", "\"rate\": 200"))
        };

        var report = LoadComparison.BuildReport(runs);

        Assert.Equal(LoadComparisonReport.StatusInvalid, report.Status);
        Assert.Contains(report.InvalidReasons, r => r.Contains("do not share one workload"));
    }

    [Fact]
    public void TheMarkdownExplainsWhyAnInvalidComparisonIsInvalid()
    {
        var runs = new[]
        {
            Run(1, Off, p95: 30),
            Read(1, "AB", On, "runs/1-AB-cache-on.summary.json", null)
        };

        var markdown = LoadComparison.ToMarkdown(LoadComparison.BuildReport(runs));

        Assert.Contains("Why this comparison is invalid", markdown);
        Assert.Contains("runs/1-AB-cache-on.summary.json", markdown);
    }
}
