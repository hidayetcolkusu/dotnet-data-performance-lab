using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataPerformanceLab.Cli.Experiments;

/// <summary>Latency statistics for one k6 run, exactly as that run reported them.</summary>
public sealed record LoadLatencyStats(
    double AvgMs,
    double MedMs,
    double P95Ms,
    double P99Ms,
    double MinMs,
    double MaxMs,
    long Count);

public sealed record LoadRunMetrics(
    long Iterations,
    long HttpReqs,
    long HttpReqFailed,
    long ChecksTotal,
    long ChecksFailed,
    long DroppedIterations,
    LoadLatencyStats DetailLatencyMs);

/// <summary>
/// One k6 summary file, in the schema <c>experiments/k6/product-detail.js</c> writes.
/// The schema is ours rather than k6's internal end-of-test shape, so a k6 upgrade
/// cannot silently change the meaning of a recorded measurement.
/// </summary>
public sealed record LoadRunSummary(
    string Schema,
    string Arm,
    int Repetition,
    string Order,
    string BaseUrl,
    string FixtureHash,
    int FixtureSkuCount,
    int Rate,
    string Duration,
    LoadRunMetrics Metrics)
{
    public const string ExpectedSchema = "dpl.load-summary.v1";

    public const string CacheOff = "cache-off";

    public const string CacheOn = "cache-on";
}

/// <summary>
/// What the orchestrator said it was going to measure, recorded before the runs happen. Without
/// it a comparison can only prove its runs agree with each other — a one-repetition smoke would
/// then look exactly as complete as the five-repetition default it is not.
/// </summary>
public sealed record LoadRunExpectation(
    int Repetitions,
    string Profile,
    int Rate,
    string Duration,
    string WarmupDuration,
    string FixtureHash,
    int FixtureSkuCount);

/// <summary>
/// One entry of the run index: which run this is, where its summary lives, how the k6 processes
/// exited, and which small artifacts the orchestrator promised to leave behind.
/// </summary>
public sealed record LoadRunInput(
    int Repetition,
    string Order,
    string Arm,
    string SummaryFile,
    int? WarmupExitCode = null,
    int? MeasuredExitCode = null,
    IReadOnlyList<string>? RequiredArtifacts = null,
    /// <summary>
    /// Cache lookups counted inside the measured window, from that run's
    /// <c>cache-metrics-delta.json</c>. Null when the run has no delta file.
    /// </summary>
    long? MeasuredWindowLookups = null);

/// <summary>
/// A run that was parsed and checked. <see cref="InvalidReason"/> being non-null means the
/// run is evidence of a broken measurement, not of a latency difference.
/// </summary>
public sealed record LoadRunRecord(
    int Repetition,
    string Order,
    string Arm,
    string SummaryFile,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LoadRunSummary? Summary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InvalidReason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? WarmupExitCode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MeasuredExitCode = null)
{
    public bool IsValid => InvalidReason is null;

    public double? P95Ms => Summary?.Metrics.DetailLatencyMs.P95Ms;
}

/// <summary>
/// Across-repetition aggregate for one arm.
///
/// There is deliberately no <c>P95Ms</c> here. A p95 of a set of p95s is not the p95 of the
/// underlying requests, and the individual runs' raw samples are not retained across
/// processes, so no pooled percentile can honestly be computed. Every across-run figure
/// is named for what it actually is: a median (or min/max) *of the per-run statistic*.
/// </summary>
public sealed record ArmAggregate(
    string Arm,
    int RunCount,
    int ValidRunCount,
    long TotalRequests,
    double? MedianOfRunP95Ms,
    double? MinRunP95Ms,
    double? MaxRunP95Ms,
    double? MedianOfRunMedianMs);

public sealed record LoadComparisonReport(
    string Schema,
    string Status,
    IReadOnlyList<LoadRunRecord> Runs,
    IReadOnlyList<ArmAggregate> Arms,
    IReadOnlyList<string> InvalidReasons)
{
    public const string ReportSchema = "dpl.load-comparison.v1";

    public const string StatusValid = "valid";

    public const string StatusInvalid = "invalid";
}

/// <summary>
/// Turns the per-run k6 summaries of a cache off/on comparison into a report.
///
/// Two rules drive everything here: a run whose own thresholds did not hold is
/// marked invalid rather than averaged away, and per-run percentiles are never merged into
/// a single "global p95".
/// </summary>
public static class LoadComparison
{
    private static readonly JsonSerializerOptions ParseOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads one run summary and applies the run-level validity rules. Returns a record with
    /// <see cref="LoadRunRecord.InvalidReason"/> set instead of throwing, so one broken run
    /// still appears in the report rather than aborting the whole comparison.
    /// </summary>
    /// <param name="missingArtifacts">
    /// Required small artifacts the orchestrator promised but did not leave behind. Large raw
    /// sample streams are deliberately not in this list: they are kept locally by design, and
    /// treating their absence from a published directory as a defect would invalidate every
    /// honest publication (see the manifest's storage contract).
    /// </param>
    public static LoadRunRecord ReadRun(
        LoadRunInput input,
        string? json,
        LoadRunExpectation? expectation = null,
        IReadOnlyList<string>? missingArtifacts = null)
    {
        var summaryFile = input.SummaryFile;

        LoadRunRecord Invalid(string reason) => new(
            input.Repetition, input.Order, input.Arm, summaryFile, null, reason,
            input.WarmupExitCode, input.MeasuredExitCode);

        // A nonzero k6 exit is the tool's own verdict on its run. It is checked before the
        // numbers, because a run k6 itself failed must never be read as a latency observation.
        if (input.WarmupExitCode is { } warmupExit and not 0)
        {
            return Invalid($"'{summaryFile}': the warmup process exited {warmupExit}; "
                + "the measured run did not start from a warmed state.");
        }

        if (input.MeasuredExitCode is { } measuredExit and not 0)
        {
            return Invalid($"'{summaryFile}': k6 exited {measuredExit}; its own thresholds did not hold.");
        }

        if (missingArtifacts is { Count: > 0 })
        {
            return Invalid($"'{summaryFile}': required artifacts are missing: "
                + string.Join(", ", missingArtifacts) + ".");
        }

        if (json is null)
        {
            return Invalid($"summary file '{summaryFile}' is missing; the k6 run produced no artifact.");
        }

        LoadRunSummary? summary;
        try
        {
            summary = JsonSerializer.Deserialize<LoadRunSummary>(json, ParseOptions);
        }
        catch (JsonException ex)
        {
            return Invalid($"summary file '{summaryFile}' is not valid JSON: {ex.Message}");
        }

        if (summary is null)
        {
            return Invalid($"summary file '{summaryFile}' deserialized to null.");
        }

        if (summary.Schema != LoadRunSummary.ExpectedSchema)
        {
            return Invalid(
                $"summary file '{summaryFile}' has schema '{summary.Schema}', expected '{LoadRunSummary.ExpectedSchema}'.");
        }

        if (!string.Equals(summary.Arm, input.Arm, StringComparison.Ordinal))
        {
            return Invalid($"summary file '{summaryFile}' reports arm '{summary.Arm}', expected '{input.Arm}'.");
        }

        // The index says which run this file belongs to; the file says so itself. When those
        // disagree, the run was mislabelled somewhere and neither claim can be trusted.
        if (summary.Repetition != input.Repetition)
        {
            return Invalid($"summary file '{summaryFile}' reports repetition {summary.Repetition}, "
                + $"but the run index places it at repetition {input.Repetition}.");
        }

        if (!string.Equals(summary.Order, input.Order, StringComparison.Ordinal))
        {
            return Invalid($"summary file '{summaryFile}' reports order '{summary.Order}', "
                + $"but the run index places it in order '{input.Order}'.");
        }

        if (expectation is not null
            && DescribeWorkloadMismatch(summary, expectation) is { } mismatch)
        {
            return Invalid($"summary file '{summaryFile}' did not run the declared workload: {mismatch}");
        }

        // The measured window's cache counters must account for exactly the requests whose latency
        // this run reports. This is what the original single cumulative read could never satisfy —
        // and it independently catches a run k6 abandoned while the API was still serving it, where
        // the counters keep climbing after the summary has been written.
        if (input.MeasuredWindowLookups is { } lookups && lookups != summary.Metrics.HttpReqs)
        {
            return Invalid(
                $"'{summaryFile}': the measured window counted {lookups} cache lookups but the run "
                + $"reports {summary.Metrics.HttpReqs} requests. The counters and the latency do not "
                + "describe the same window, so neither can be quoted for this run.");
        }

        var reason = CheckMetrics(summary.Metrics, summaryFile);
        return reason is null
            ? new LoadRunRecord(
                input.Repetition, input.Order, input.Arm, summaryFile, summary, null,
                input.WarmupExitCode, input.MeasuredExitCode)
            : Invalid(reason);
    }

    /// <summary>
    /// Compares one run against the workload the orchestrator declared. Both arms must face the
    /// same fixture at the same arrival rate for the same duration, or the difference between
    /// them measures the parameters rather than the cache.
    /// </summary>
    private static string? DescribeWorkloadMismatch(LoadRunSummary summary, LoadRunExpectation expected)
    {
        var differences = new List<string>();

        if (!string.Equals(summary.FixtureHash, expected.FixtureHash, StringComparison.Ordinal))
        {
            differences.Add($"fixtureHash '{summary.FixtureHash}' != declared '{expected.FixtureHash}'");
        }

        if (summary.FixtureSkuCount != expected.FixtureSkuCount)
        {
            differences.Add($"fixtureSkuCount {summary.FixtureSkuCount} != declared {expected.FixtureSkuCount}");
        }

        if (summary.Rate != expected.Rate)
        {
            differences.Add($"rate {summary.Rate} != declared {expected.Rate}");
        }

        if (!string.Equals(summary.Duration, expected.Duration, StringComparison.Ordinal))
        {
            differences.Add($"duration '{summary.Duration}' != declared '{expected.Duration}'");
        }

        return differences.Count == 0 ? null : string.Join("; ", differences) + ".";
    }

    /// <summary>
    /// A run only measures latency if it served every request correctly and k6 never fell
    /// behind the arrival rate. Dropped iterations mean the requested load was not applied.
    /// </summary>
    private static string? CheckMetrics(LoadRunMetrics metrics, string summaryFile)
    {
        if (metrics.DroppedIterations > 0)
        {
            return $"'{summaryFile}': {metrics.DroppedIterations} dropped iterations; "
                + "the target arrival rate was not sustained, so the latency is not comparable.";
        }

        if (metrics.HttpReqFailed > 0)
        {
            return $"'{summaryFile}': {metrics.HttpReqFailed} failed HTTP requests.";
        }

        if (metrics.ChecksFailed > 0)
        {
            return $"'{summaryFile}': {metrics.ChecksFailed} failed checks; "
                + "at least one response did not match the fixture.";
        }

        if (metrics.ChecksTotal == 0 || metrics.Iterations == 0)
        {
            return $"'{summaryFile}': the run executed no iterations.";
        }

        if (metrics.DetailLatencyMs.Count == 0)
        {
            return $"'{summaryFile}': no successful-request latency samples were recorded.";
        }

        return null;
    }

    public static ArmAggregate Aggregate(string arm, IReadOnlyList<LoadRunRecord> runs)
    {
        var armRuns = runs.Where(r => string.Equals(r.Arm, arm, StringComparison.Ordinal)).ToList();
        var valid = armRuns.Where(r => r is { IsValid: true, Summary: not null }).ToList();

        if (valid.Count == 0)
        {
            return new ArmAggregate(arm, armRuns.Count, 0, 0, null, null, null, null);
        }

        var p95s = valid.Select(r => r.Summary!.Metrics.DetailLatencyMs.P95Ms).Order().ToArray();
        var medians = valid.Select(r => r.Summary!.Metrics.DetailLatencyMs.MedMs).Order().ToArray();

        return new ArmAggregate(
            arm,
            armRuns.Count,
            valid.Count,
            valid.Sum(r => r.Summary!.Metrics.HttpReqs),
            QueryExperimentRunner.Percentile(p95s, 50),
            p95s[0],
            p95s[^1],
            QueryExperimentRunner.Percentile(medians, 50));
    }

    public static LoadComparisonReport BuildReport(
        IReadOnlyList<LoadRunRecord> runs, LoadRunExpectation? expectation = null)
    {
        var invalidReasons = runs
            .Where(r => r.InvalidReason is not null)
            .Select(r => r.InvalidReason!)
            .ToList();

        invalidReasons.AddRange(CheckPairing(runs, expectation));
        invalidReasons.AddRange(CheckWorkloadIsUniform(runs));

        var arms = new[] { LoadRunSummary.CacheOff, LoadRunSummary.CacheOn }
            .Select(arm => Aggregate(arm, runs))
            .ToList();

        // An arm with no runs at all is as invalid as an arm with failed runs: the
        // comparison it is half of cannot be drawn.
        foreach (var arm in arms.Where(a => a.ValidRunCount == 0))
        {
            invalidReasons.Add($"arm '{arm.Arm}' has no valid run.");
        }

        if (arms.Select(a => a.ValidRunCount).Distinct().Count() > 1)
        {
            invalidReasons.Add(
                "the arms have different valid run counts; the comparison is unbalanced. "
                + string.Join(", ", arms.Select(a => $"{a.Arm}={a.ValidRunCount}")));
        }

        return new LoadComparisonReport(
            LoadComparisonReport.ReportSchema,
            invalidReasons.Count == 0 ? LoadComparisonReport.StatusValid : LoadComparisonReport.StatusInvalid,
            runs,
            arms,
            invalidReasons);
    }

    /// <summary>
    /// Every repetition must be one off/on pair, exactly once, and the set of repetitions must be
    /// the declared 1..N with no gaps. A comparison that silently ran three of its five
    /// repetitions is not a shorter version of the same evidence.
    /// </summary>
    private static IEnumerable<string> CheckPairing(
        IReadOnlyList<LoadRunRecord> runs, LoadRunExpectation? expectation)
    {
        var duplicates = runs
            .GroupBy(r => (r.Repetition, r.Arm))
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var duplicate in duplicates)
        {
            yield return $"repetition {duplicate.Key.Repetition} has {duplicate.Count()} "
                + $"'{duplicate.Key.Arm}' runs; each repetition is one run per arm.";
        }

        foreach (var repetition in runs.GroupBy(r => r.Repetition).OrderBy(g => g.Key))
        {
            var arms = repetition.Select(r => r.Arm).Distinct(StringComparer.Ordinal).ToList();
            if (!arms.Contains(LoadRunSummary.CacheOff) || !arms.Contains(LoadRunSummary.CacheOn))
            {
                yield return $"repetition {repetition.Key} is not a complete pair; it has "
                    + $"[{string.Join(", ", arms.Order(StringComparer.Ordinal))}] but needs both "
                    + $"'{LoadRunSummary.CacheOff}' and '{LoadRunSummary.CacheOn}'.";
            }

            // AB means off-then-on and BA means on-then-off. An order label that contradicts the
            // alternation the orchestrator claimed makes the drift control unverifiable.
            var expectedOrder = repetition.Key % 2 == 1 ? "AB" : "BA";
            foreach (var wrong in repetition.Where(r => !string.Equals(r.Order, expectedOrder, StringComparison.Ordinal)))
            {
                yield return $"repetition {repetition.Key} run '{wrong.Arm}' is labelled order "
                    + $"'{wrong.Order}'; the alternation makes repetition {repetition.Key} an "
                    + $"'{expectedOrder}' round.";
            }
        }

        if (expectation is null)
        {
            yield break;
        }

        var present = runs.Select(r => r.Repetition).Distinct().Order().ToList();
        var declared = Enumerable.Range(1, expectation.Repetitions).ToList();

        if (!present.SequenceEqual(declared))
        {
            yield return $"the run index holds repetitions [{string.Join(", ", present)}] but the "
                + $"orchestrator declared {expectation.Repetitions} "
                + $"([{string.Join(", ", declared)}]).";
        }
    }

    /// <summary>
    /// Even with no declared expectation, the runs must at least agree with each other: one
    /// fixture, one rate, one duration across the whole comparison.
    /// </summary>
    private static IEnumerable<string> CheckWorkloadIsUniform(IReadOnlyList<LoadRunRecord> runs)
    {
        var summaries = runs.Where(r => r.Summary is not null).Select(r => r.Summary!).ToList();
        if (summaries.Count < 2)
        {
            yield break;
        }

        foreach (var field in new (string Name, Func<LoadRunSummary, string> Read)[]
        {
            ("fixtureHash", s => s.FixtureHash),
            ("fixtureSkuCount", s => s.FixtureSkuCount.ToString(CultureInfo.InvariantCulture)),
            ("rate", s => s.Rate.ToString(CultureInfo.InvariantCulture)),
            ("duration", s => s.Duration),
            ("baseUrl", s => s.BaseUrl)
        })
        {
            var distinct = summaries.Select(field.Read).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (distinct.Count > 1)
            {
                yield return $"the runs do not share one workload: {field.Name} takes "
                    + $"{distinct.Count} different values [{string.Join(", ", distinct)}]; "
                    + "the arms were not measured against the same load.";
            }
        }
    }

    /// <summary>
    /// One table row per run — never a single merged row per arm — so a reader can see the
    /// spread between repetitions instead of a number that hides it.
    /// </summary>
    public static string ToMarkdown(LoadComparisonReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("# Cache off/on load comparison");
        text.AppendLine();
        text.AppendLine(FormattableString.Invariant($"Status: **{report.Status}**"));
        text.AppendLine();
        text.AppendLine("## Runs");
        text.AppendLine();
        text.AppendLine("| Repetition | Order | Arm | Requests | avg ms | med ms | p95 ms | p99 ms | Valid |");
        text.AppendLine("| ---: | :--- | :--- | ---: | ---: | ---: | ---: | ---: | :--- |");

        foreach (var run in report.Runs.OrderBy(r => r.Repetition).ThenBy(r => r.Arm, StringComparer.Ordinal))
        {
            if (run.Summary is null)
            {
                text.AppendLine(FormattableString.Invariant(
                    $"| {run.Repetition} | {run.Order} | {run.Arm} | - | - | - | - | - | no |"));
                continue;
            }

            var l = run.Summary.Metrics.DetailLatencyMs;
            var head = FormattableString.Invariant(
                $"| {run.Repetition} | {run.Order} | {run.Arm} | {run.Summary.Metrics.HttpReqs} |");
            text.AppendLine(head + FormattableString.Invariant(
                $" {l.AvgMs:0.00} | {l.MedMs:0.00} | {l.P95Ms:0.00} | {l.P99Ms:0.00} | yes |"));
        }

        text.AppendLine();
        text.AppendLine("## Across repetitions");
        text.AppendLine();
        text.AppendLine("Percentiles are **not** pooled across runs. Each column below is a statistic *of the");
        text.AppendLine("per-run statistic*, over the valid runs of that arm.");
        text.AppendLine();
        text.AppendLine("| Arm | Valid runs | Requests | median of run p95 ms | min run p95 ms | max run p95 ms | median of run median ms |");
        text.AppendLine("| :--- | ---: | ---: | ---: | ---: | ---: | ---: |");

        foreach (var arm in report.Arms)
        {
            var head = FormattableString.Invariant(
                $"| {arm.Arm} | {arm.ValidRunCount}/{arm.RunCount} | {arm.TotalRequests} |");
            text.AppendLine(
                $"{head} {Format(arm.MedianOfRunP95Ms)} | {Format(arm.MinRunP95Ms)} | "
                + $"{Format(arm.MaxRunP95Ms)} | {Format(arm.MedianOfRunMedianMs)} |");
        }

        if (report.InvalidReasons.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Why this comparison is invalid");
            text.AppendLine();
            foreach (var reason in report.InvalidReasons)
            {
                text.AppendLine($"- {reason}");
            }
        }

        return text.ToString();
    }

    private static string Format(double? value) =>
        value is null ? "-" : value.Value.ToString("0.00", CultureInfo.InvariantCulture);
}
