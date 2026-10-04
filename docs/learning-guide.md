# Learning guide

Six sessions. Each one names the real files, asks you to change something small, tells you what
you should observe, and ends with three questions whose answers are in the repository — not in
the guide's prose.

Work through them in order; each assumes the previous one's setup. Start with
[the runbook](runbook.md) so the containers and the CI-profile seed exist.

> Every session's "expected observation" was produced by actually running it. If yours differs,
> that difference is the interesting part — check the manifest of your run first.

---

## Session 1 — Keyset pagination and why the cursor is signed to a query

**Files:** [`ProductCursor.cs`](../src/Lab.Core/Catalog/ProductCursor.cs) ·
[`ProductQueries.cs`](../src/Lab.Core/Catalog/ProductQueries.cs) ·
[`ProductEndpoints.cs`](../src/Lab.Api/Endpoints/ProductEndpoints.cs) ·
[`CursorTests.cs`](../tests/Lab.UnitTests/CursorTests.cs)

**Do this.** Page through a category and keep the `nextCursor`:

```powershell
curl "http://127.0.0.1:8080/api/products?categoryId=1&pageSize=5"
curl "http://127.0.0.1:8080/api/products?categoryId=1&pageSize=5&cursor=<nextCursor>"
```

Now take that same cursor and send it with `categoryId=2`.

**Expected observation.** A `400` with `"code": "InvalidCursor"` — not a wrong page, and not a
`500`. The cursor carries the category it was issued for, and `ProductCursor.TryParse` takes the
current `categoryId` as an argument specifically so a cursor cannot be replayed against a
different query.

**Then try.** Truncate the base64 cursor by one character. Same shape of answer: a `400` with a
message, because a malformed cursor is a client error, not a server failure.

**Questions**

1. Why does the ordering use `CreatedAtUtc` **then `Id`**, rather than `CreatedAtUtc` alone?
   (What happens on the page boundary if two products share a timestamp?)
2. What would `OFFSET 50000 ROWS` have to do that this query does not?
3. The cursor is base64 of a composite value. Why is that *not* a security measure, and what
   would break if a user edited it? (Look at what `TryParse` validates.)

---

## Session 2 — Measure before you change: the index experiment

**Files:** [`QueryVariants.cs`](../src/Lab.Cli/Experiments/QueryVariants.cs) ·
[`QueryExperimentRunner.cs`](../src/Lab.Cli/Experiments/QueryExperimentRunner.cs) ·
[`CatalogIndexState.cs`](../src/Lab.Cli/Experiments/CatalogIndexState.cs) ·
[`catalog-index.sql`](../experiments/sql/catalog-index.sql)

**Do this.** Seed the experiment database with the `default` profile, then run Q1 and open the
two `.sqlplan` files it writes:

```powershell
dotnet run --project src/Lab.Cli -- db migrate --database DataPerformanceLab_Experiment
dotnet run --project src/Lab.Cli -- seed --profile default --database DataPerformanceLab_Experiment
dotnet run --project src/Lab.Cli -- query --experiment index --category 1 --output results/local/q1
```

**Expected observation.** A large, consistent difference — the published run measured
**45.5 ms → 1.3 ms** median on 100,000 products — and, in the `.txt` files, a logical-reads count that collapses by orders of
magnitude. The `IndexAbsent` plan scans; the `IndexPresent` plan seeks a covering index.

**Then try.** Open `query-samples.json` and check the `resultHashByVariant` field. Both variants
hash identically. Now imagine they had not: `QueryExperimentCommand` would have failed the run
instead of reporting the timings.

**Questions**

1. The index includes `Sku`, `Name` and `UnitPrice` rather than only the filter and sort columns.
   What does that change about the plan, and what does it cost?
2. Why does the runner alternate AB / BA / AB / BA / AB instead of running all of A then all of B?
3. Q1 makes the read ~34× faster. Why does no document in this repository state a write-cost
   number alongside it? (See [ADR 001](decisions/001-measurement-boundary.md).)

---

## Session 3 — What EF actually materializes

**Files:** [`QueryVariants.cs`](../src/Lab.Cli/Experiments/QueryVariants.cs) ·
[`ProductDtos.cs`](../src/Lab.Core/Catalog/ProductDtos.cs) ·
[`QueryVariantTests.cs`](../tests/Lab.IntegrationTests/QueryVariantTests.cs)

**Do this.** Run Q2 and compare the generated `.sql` files for the two variants:

```powershell
dotnet run --project src/Lab.Cli -- query --experiment projection --category 1 --output results/local/q2
```

**Expected observation.** A modest but consistent difference — the published run measured
**2.5 ms → 1.4 ms**, about 1.8×. `EntityThenMap` selects every column including the ~1 KB `Description`; `SqlProjection`
selects only the six the DTO exposes. Both sides have the index — but open the two `.sqlplan`
files: the index cannot cover `Description`, so the entity side adds a key lookup per row. The
difference is wide columns *plus* the lookups they force, and Q2 does not separate the two.

**Then try.** Predict the effect *before* running Q2 on category 20 (far fewer rows). Does the
ratio hold, or the absolute difference?

**Questions**

1. Both variants use `AsNoTracking()`. What would change if one did not, and why would including
   that in this experiment be a mistake?
2. Q2 is ~1.8× while Q1 is ~34×. What does that ranking tell you about where to spend effort
   first on a slow endpoint?
3. `QueryVariantTests` asserts the experiment variant returns the same ordered DTO as the API's
   own query. Why is that test load-bearing for the whole experiment?

---

## Session 4 — Atomic import, and the checkpoint that must not drift

**Files:** [`ImportBatchProcessor.cs`](../src/Lab.Core/Import/ImportBatchProcessor.cs) ·
[`ImportStager.cs`](../src/Lab.Core/Import/ImportStager.cs) ·
[`ImportFaultInjector.cs`](../src/Lab.Core/Import/ImportFaultInjector.cs) ·
[`ImportRecoveryTests.cs`](../tests/Lab.IntegrationTests/ImportRecoveryTests.cs)

**Do this.** Import a file with deliberate problems, then re-import the same file:

```powershell
dotnet run --project src/Lab.Cli -- import --file data/samples/catalog-mixed.csv
dotnet run --project src/Lab.Cli -- import --file data/samples/catalog-mixed.csv
dotnet run --project src/Lab.Cli -- report --job-id <guid> --output results/local/import
```

**Expected observation.** The first run exits `2` (loaded, with rejections) and reports
`Inserted + SkippedExisting + Rejected == TotalRecords`. The second run inserts nothing: every
row is `SkippedExisting`, and the job is matched by file hash + parser version rather than
duplicated.

**Then try — the central experiment.** In `ImportRecoveryTests`, find the test that injects a
fault *between* the product insert and the checkpoint write. Change the production code so the
checkpoint is committed in its **own** transaction, and run the suite.

**Expected observation.** The test fails, and it fails in the informative direction: after the
simulated crash the checkpoint claims records whose rows were rolled back, so `resume` skips
them and they are never inserted. This is the concrete reason the checkpoint lives in the same
transaction as the work it describes. Revert your change.

**Questions**

1. Why is an existing SKU *skipped and counted* instead of updated?
   (See [ADR 002](decisions/002-import-semantics.md).)
2. Why does the file hash alone not identify a job — what does `ParserVersion` protect against?
3. Exit code `3` means "another importer holds the lock". Why is that a distinct code rather
   than a generic failure, and what has changed in the database when you see it?

---

## Session 5 — Cache-aside and the window you are accepting

**Files:** [`CachedProductReader.cs`](../src/Lab.Core/Caching/CachedProductReader.cs) ·
[`ProductCache.cs`](../src/Lab.Core/Caching/ProductCache.cs) ·
[`UpdatePrice.cs`](../src/Lab.Core/Catalog/UpdatePrice.cs) ·
[`CacheBehaviorTests.cs`](../tests/Lab.IntegrationTests/CacheBehaviorTests.cs) ·
[`PriceConcurrencyTests.cs`](../tests/Lab.IntegrationTests/PriceConcurrencyTests.cs)

**Do this.** Read a product, update its price with the `version` you got back, then read again.
Then repeat the update using the *old* `version`.

```powershell
curl http://127.0.0.1:8080/api/products/SKU-0000000001
curl -X PUT http://127.0.0.1:8080/api/products/SKU-0000000001/price `
  -H "Content-Type: application/json" -d '{"unitPrice": 42.50, "expectedVersion": "<version>"}'
```

**Expected observation.** The first update returns the new detail; the second returns `409` with
`"code": "VersionConflict"`. The read after the update shows the new price — the write
invalidated the key after committing.

**Then try.** Stop Redis (`docker compose stop redis`) and hit the detail endpoint, then
`/health/ready`.

**Expected observation.** Detail still returns `200`, served from SQL. `/health/ready` returns
`200` with `cache: "degraded"` — the API can serve every request, so it is ready. Restart Redis.

**Then read** the test in `CacheBehaviorTests` that uses the `AfterSqlReadAsync` seam. It
reproduces a race the code cannot prevent: a reader that missed the cache fills it with a value
read from SQL just before a writer committed.

**Questions**

1. Why is a *missing* product not cached, and what would break if it were?
2. The key is `dpl:<instance>:product:v1:<sku>`. What does each of the instance id and the `v1`
   let you do?
3. After a successful price update, for how long can another reader still see the old price, and
   which of the two causes is bounded by the TTL? (See [ADR 003](decisions/003-cache-consistency.md).)

---

## Session 6 — Making a measurement you are allowed to believe

**Files:** [`product-detail.js`](../experiments/k6/product-detail.js) ·
[`warmup.js`](../experiments/k6/warmup.js) ·
[`compare-cache.ps1`](../scripts/compare-cache.ps1) ·
[`LoadComparison.cs`](../src/Lab.Cli/Experiments/LoadComparison.cs) ·
[`ExperimentSummaryTests.cs`](../tests/Lab.UnitTests/ExperimentSummaryTests.cs)

**Do this.** Run a short comparison, then read `comparison.md`:

```powershell
./scripts/compare-cache.ps1 -Profile default -Output results/local/cache -Repetitions 2 -Duration 20s
```

**Expected observation.** One table row per run, and an across-repetition table whose columns are
named "median of run p95" rather than "p95". On a quiet machine the published run reproduces:
**cache-on is slower**, in every repetition and both orders. Check the per-run
`cache-metrics.json` — a hit rate near **0.29** is the reason, and it is why the counters exist.
On a busy machine the between-repetition spread may swamp the difference entirely; then the
honest reading is "this machine could not measure it", not "there is no difference".

**Then try — the falsification experiment.** Edit your fixture copy and change one product's
`unitPrice` by 1.00, then run the smoke:

```powershell
k6 run -e BASE_URL=http://127.0.0.1:8080 -e FIXTURE=<edited fixture> experiments/k6/smoke.js
```

**Expected observation.** Exit code `99`, one failed check. The API returned `200` — the run
fails because the *content* was wrong. A load test that only checked status codes would have
called this a success and measured it as a fast response.

**Then try.** In `ExperimentSummaryTests`, look at `TheAggregateNeverExposesAPooledPercentile`.
Add a `P95Ms` property to `ArmAggregate` and run the unit tests.

**Expected observation.** The test fails. It asserts on the *shape* of the report type, because
the easiest way to publish a dishonest number is to add a plausible-looking field.

**Questions**

1. Why does the warmup run in a separate k6 process instead of a warmup stage in the same run?
2. The measured run lasts 60 s against a 60 s TTL, so many entries should expire just before
   they are asked for again. A longer TTL would raise the hit rate — so what exactly would it
   cost, and who decides that trade?
3. The published `0.286` hit rate turned out to cover the warmup *and* the measured run, because
   the counters are cumulative and both phases hit the same process. Open
   `results/published/20260911/q3-cache/runs/1-AB-cache-on.cache-metrics.json` next to
   `1-AB-cache-on.summary.json`: 2,802 lookups against 1,201 measured GETs. What is the smallest
   change that makes the recorded rate describe only the measured window — and why is taking a
   *second* snapshot the fix rather than resetting the counters between phases?
   (Measured properly, the rate is 0.167, and lookups then equal measured GETs exactly. That
   equality is the invariant worth internalizing: a hit rate whose denominator does not match the
   request count you are reporting latency for is describing some other experiment.)
4. `dropped_iterations: ['count==0']` fails a run. What has gone wrong when iterations are
   dropped, and why can the latency of such a run not be compared to another's?

---

## Where the evidence lives

| Question | Evidence |
|---|---|
| What did the index change? | `results/published/*/q1-cat*/` — samples, plans, `STATISTICS IO`, manifest |
| What did the projection change? | `results/published/*/q2-cat*/` |
| What did the cache change? | `results/published/*/q3-cache/comparison.md` + `manifest.json` |
| Is the import really atomic? | `ImportRecoveryTests`, `ImportStagingTests` |
| Is the cache really stale-bounded? | `CacheBehaviorTests`, `PriceConcurrencyTests` |
| What may these numbers claim? | [ADR 001](decisions/001-measurement-boundary.md) |
