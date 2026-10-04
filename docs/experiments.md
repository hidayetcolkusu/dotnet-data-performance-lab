# Experiments

How the controlled experiments are defined, run and recorded: two SQL query experiments and
one cache load comparison. The rules it works within are in
[ADR 001](decisions/001-measurement-boundary.md).

Measured results live under [`results/published/`](../results/published/). This page describes
the method; it does not restate the numbers.

## Q1 / Q2: what is being compared

Both experiments run the same business query: **the first 50 active products in one category,
ordered by `CreatedAtUtc` then `Id`**. That is the query [`ProductQueries.ListAsync`](../src/Lab.Core/Catalog/ProductQueries.cs)
serves over HTTP, and [`QueryVariantTests`](../tests/Lab.IntegrationTests/QueryVariantTests.cs)
asserts that the experiment variant and the API query return the identical ordered DTO.

| Experiment | A variant | B variant | The single changed factor |
|---|---|---|---|
| **Q1 index** | `IndexAbsent` | `IndexPresent` | whether the candidate index exists |
| **Q2 projection** | `EntityThenMap` | `SqlProjection` | where the DTO is built |

The candidate index is

```sql
CREATE INDEX IX_Products_Category_Active_Created_Id
ON dbo.Products(CategoryId, IsActive, CreatedAtUtc, Id)
INCLUDE(Sku, Name, UnitPrice);
```

It covers the filter, the ordering and every column the list DTO exposes. It ships in the
normal API schema via the `AddCatalogIndexAndWiderImportStaging` migration; the standalone
DDL is kept readable in [`experiments/sql/catalog-index.sql`](../experiments/sql/catalog-index.sql).

**Q2 keeps the index present on both sides.** Mixing an index change and a projection change
into one measurement would produce a number that cannot be attributed to either.

`EntityThenMap` materializes the whole `Product` entity — including the ~1 KB `Description`
column the DTO never returns — and maps in memory. `SqlProjection` selects the DTO in SQL.
Both use `AsNoTracking()`: tracking is not the variable under test.

**What Q2 does not separate.** Selecting the whole entity changes the plan, not only the payload:
the candidate index cannot cover `Description` and `Version`, so `EntityThenMap` seeks the index
and then does a key lookup into the clustered index for each of the 50 rows (`Nested Loops` +
`Clustered Index Seek`, visible in the published `.sqlplan`). `SqlProjection` is a single covering
seek. Q2 therefore measures *entity materialization as it actually executes against this schema*
— extra columns **and** the lookups they force — and cannot say how much of the difference is
bytes on the wire versus lookups. Separating those would need a third variant whose index also
covers the wide columns; it was not run.

## Isolation

Experiments run **only** against `DataPerformanceLab_Experiment`. The database the API serves
never receives experiment DDL. [`CatalogIndexState.EnsureExperimentDatabase`](../src/Lab.Cli/Experiments/CatalogIndexState.cs)
enforces this, and `ExperimentToolingRefusesTheApiDatabase` covers it in the test suite.

Before dropping the index, the runner writes a restore marker into `LabMetadata`. The restoring
`finally` deletes it. If a run crashes in between, the **next** run's preflight finds the marker,
reports the half-finished state in plain words and repairs it — rather than silently measuring a
database whose index state nobody knows.

Do not run a SQL query experiment and the cache load experiment at the same time; they would
compete for the same local CPU and disk.

## Protocol

Per experiment and per category profile:

- 5 warmup executions per variant, discarded.
- 5 rounds; each round runs **20 sequential measurements per variant**.
- Round order alternates **AB / BA / AB / BA / AB**, so a monotonic drift in machine state
  cannot masquerade as a difference between the variants.
- Every measurement builds a **fresh `DbContext`** and materializes the result completely.
- Elapsed time is application-side `Stopwatch` time.

Categories **1** and **20** are separate parameter profiles and are never averaged together:
the seed puts ~60% of products in category 1, so the two exercise very different row counts.

Every variant must produce the **same ordered result hash**. If they do not, the run fails
instead of reporting a timing comparison — an unequal result makes the timings meaningless.

## Plan and statistics collection

Plan collection is a **separate instrumented pass that runs after every timed sample**, so its
overhead never lands inside a latency measurement.

[`CommandCaptureInterceptor`](../src/Lab.Cli/Experiments/SqlPlanCollector.cs) records the SQL
EF Core actually sent, with parameter types and values. The collector replays that exact
statement over `Microsoft.Data.SqlClient` with `SET STATISTICS IO/TIME/XML ON` and writes:

| File | Contents |
|---|---|
| `<experiment>-cat<N>-<variant>.sqlplan` | real execution plan XML |
| `<experiment>-cat<N>-<variant>.sql` | the parameterized statement and its parameter values |
| `<experiment>-cat<N>-<variant>.txt` | `STATISTICS IO` / `STATISTICS TIME` messages, including logical reads |
| `query-samples.json` | every timed sample plus per-round min/mean/median/p95/max |
| `manifest.json` | run manifest: commit + dirty flag, environment, dataset hash, parameters, files |

The tests assert the plan **parses as valid XML**. They do not assert which physical operator
the optimizer chose, and they do not assert a speedup. If the optimizer ignores the candidate
index, the real plan is reported as-is.

## Running them

The experiment database must be migrated and seeded first:

```powershell
dotnet run --project src/Lab.Cli -- db migrate --database DataPerformanceLab_Experiment
dotnet run --project src/Lab.Cli -- seed --profile default --database DataPerformanceLab_Experiment

dotnet run --project src/Lab.Cli -- query --experiment index --category 1 --output results/local/q1-cat1
dotnet run --project src/Lab.Cli -- query --experiment index --category 20 --output results/local/q1-cat20
dotnet run --project src/Lab.Cli -- query --experiment projection --category 1 --output results/local/q2-cat1
dotnet run --project src/Lab.Cli -- query --experiment projection --category 20 --output results/local/q2-cat20
```

Exit code 0 means the run completed and every variant agreed. A failure still writes
`manifest.json` with `"status": "failed"` and a `failureReason`, keeps whatever partial evidence
was produced, and exits nonzero. Failed runs are kept, not deleted.

## What these experiments do not establish

- Nothing about write cost. The index makes inserts and updates more expensive; that cost was
  not measured here, so no numeric write trade-off is claimed. See
  [ADR 001](decisions/001-measurement-boundary.md).
- Nothing about a server. Everything runs on one local machine against synthetic data, with
  SQL Server in a container sharing CPU with the client process.
- Nothing about other categories, page sizes or data volumes than the ones recorded in the
  manifest of a given run.

## Correctness tests

```powershell
dotnet test tests/Lab.IntegrationTests -c Release --filter FullyQualifiedName~QueryVariantTests
dotnet test tests/Lab.UnitTests -c Release --filter FullyQualifiedName~ExperimentStatisticsTests
```

These are deterministic correctness tests over a 1,000-product CI fixture. Timing is never a
test assertion.

---

# Q3: the cache load comparison

An HTTP load comparison of `GET /api/products/{sku}` with the cache **off** and **on**. Unlike
Q1/Q2 this is not a sequential in-process timing loop: it measures the endpoint under a
sustained arrival rate, through the whole API stack.

## The single changed factor

Both arms run the **same** API binary, the same configuration file, the same fixture and the
same load profile. The only difference is `Cache:Enabled`.

Each arm is a **fresh API process**, started by the orchestration script and stopped by its
own PID when the run ends. Both arms share one per-run cache namespace
(`Cache:InstanceId = compare-<runId>`), identical on each side, so neither arm can inherit
entries from an earlier run while the differing factor stays exactly one setting.

## The fixture

`dpl fixture --output <path> --count 1000` writes the read-only fixture the load drives:

- 1,000 SKUs spread **evenly across the ordered catalog** — not the first 1,000, which would
  confine the measurement to one corner of the index and of the buffer pool.
- For each SKU, the `Id`, name, category, price and active flag the detail response must carry.

The fixture is generated **from the database**, not re-derived from the seed algorithm, so it
cannot drift from what the API will actually return. It records the dataset hash and its own
`fixtureHash`; every k6 summary repeats that hash, so a comparison accidentally built from two
different fixtures is detectable afterwards.

Nothing in the measurement writes to the catalog or to the cache by any path other than the
read being measured.

## Protocol

Per arm, per repetition:

1. **Warmup in a separate k6 process** ([`warmup.js`](../experiments/k6/warmup.js)), so none of
   its traffic can reach the measured summary. It preloads every fixture SKU once — identical
   work in both arms — then settles at the measured rate for 30s.
2. **Measured run** ([`product-detail.js`](../experiments/k6/product-detail.js)): 60s at a
   constant arrival rate of 20/s. One iteration is exactly one `GET`.

The measured run lasts as long as the 60s TTL, so entries **do** expire and get re-read during
measurement. Hiding TTL renewal would overstate the cache.

Five repetitions alternate **AB / BA / AB / BA / AB**, the same reasoning as Q1/Q2.

## Bounding the measured window's cache counters

The cache counters are **process-wide and cumulative**, and the warmup drives its traffic through
the same API process the measured run uses. Reading them once at the end therefore reports warmup
lookups and measured lookups added together — which is not the hit rate of the window whose
latency is being reported. That is exactly the mistake the `20260911` run made: its recorded
`0.286` covered 2,802 lookups against 1,201 measured GETs.

So the orchestrator takes **two** snapshots:

1. immediately after the warmup process exits, before the measured run starts;
2. immediately after the measured run exits.

The difference is written to `runs/<stem>.cache-metrics-delta.json` with both window timestamps
stamped on it, and that difference is the only figure allowed to be quoted as a run's hit rate.
Both cumulative snapshots are kept beside it, so the subtraction is auditable rather than assumed.

The check that makes this self-verifying: **cache lookups in the delta must equal the measured GET
count in that run's `summary.json`.** A cumulative read can never satisfy it. If a counter goes
backwards, the API process restarted mid-run and the run is failed rather than reported.

Reading the counters only after the measured run has finished still matters for the same reason it
always did — an in-flight scrape would be load the experiment did not intend.

## What counts as a success

Every response is checked against the fixture: status 200, a parseable body, and a matching
`sku`, `id`, `name`, `categoryId`, `unitPrice` and `isActive`, plus the presence of the detail
fields. **A 200 carrying the wrong row is a failure, not a fast response.** Only successful,
correct responses contribute to the latency trend, so a quick 404 cannot flatter an arm.

A run is invalid — reported, never averaged away — if any artifact is missing, any check
failed, any request failed, or k6 **dropped iterations** (which means the requested arrival
rate was never applied, so the run does not measure what it claims to).

## No pooled percentiles

Each run is one row in the report. Across repetitions the report publishes the **median of the
per-run p95**, with the min and max per-run p95 as the spread — never a single "global p95".

A percentile of percentiles is not the percentile of the underlying requests, and the raw
samples are not retained across processes, so no pooled figure could honestly be computed.
Every across-run number is a value some run actually produced (nearest-rank, no interpolation).
[`ExperimentSummaryTests`](../tests/Lab.UnitTests/ExperimentSummaryTests.cs) asserts this,
including that no `P95Ms` property exists on the aggregate at all.

## Running it

Requires [k6](https://grafana.com/docs/k6/latest/set-up/install-k6/) on `PATH`, plus a migrated
and seeded `DataPerformanceLab`.

```powershell
./scripts/bootstrap.ps1 -StartContainers
dotnet run --project src/Lab.Cli -- db migrate
dotnet run --project src/Lab.Cli -- seed --profile default

./scripts/compare-cache.ps1 -Profile default -Output results/local/<run-id>
```

| File | Contents |
|---|---|
| `fixture.json` | the SKUs and expected values both arms drove |
| `runs/<rep>-<order>-<arm>.summary.json` | that run's own metrics, in the `dpl.load-summary.v1` schema |
| `runs/<rep>-<order>-<arm>.raw.json` | the raw k6 sample stream |
| `runs/<rep>-<order>-<arm>.k6.log` / `.warmup.log` / `.api.log` | stdout of each process |
| `runs.json` | which runs were supposed to exist, rewritten after every run |
| `comparison.json` / `comparison.md` | the aggregated report |
| `manifest.json` | run manifest: commit + dirty flag, environment, k6 version, dataset and fixture hash |

Exit code 0 means every run was valid. Exit 2 means the comparison is not evidence; the
artifacts are kept either way and `comparison.md` says exactly why.

The summary schema is **ours**, written by `handleSummary`, not k6's end-of-test shape — a k6
upgrade cannot silently change the meaning of a recorded measurement.

## Correctness smoke

[`smoke.js`](../experiments/k6/smoke.js) walks a slice of the fixture once and asserts the same
identity checks, with **no latency threshold**. It is the script CI runs: CI hardware makes no
promise about timing, so a performance comparison is never a CI gate.

```powershell
k6 run -e BASE_URL=http://127.0.0.1:8080 -e FIXTURE=<fixture.json> experiments/k6/smoke.js
```

Exit 0 means the API served the fixture correctly. A fixture holding a SKU the catalog does not
contain, or a tampered expected value, exits nonzero.

## What Q3 does not establish

- Nothing about a server, a network, or concurrent writers. One machine, loopback HTTP,
  containers sharing CPU with the load generator.
- Nothing about cache behaviour under invalidation pressure: the measured load is read-only.
- Nothing about rates, durations, TTLs or payloads other than those in the run's manifest.
- Nothing about the **cold** read path — the warmup deliberately removes it. That is a separate
  experiment; see below.

---

# The cold-cache read experiment

Q3 measures a warm cache under sustained load. It says nothing about the first read of an entry,
because the warmup exists precisely to eliminate that. Folding a single cold read into a
sixty-second warm run would dilute it into invisibility and muddy both numbers, so it is its own
command: `dpl cold-cache`.

## Protocol

1. Evict **one key** — the requested SKU, in this lab's namespace.
2. Snapshot the counters, `GET` the product, snapshot again. That first read must miss and fall
   through to SQL.
3. Repeat for the remaining reads. Each must hit.

## What is asserted, and what is merely recorded

The test is the **counter delta per read**: `misses+1, sqlFallbacks+1` for the first,
`hits+1, misses+0` for the rest. Latency is recorded but is never the pass condition — a single
request's timing on a developer machine proves nothing, and in practice the observed times fall
steeply across the *hits* too, which is JIT and connection warm-up rather than cache behaviour.

## Eviction scope

Only the one key is deleted. There is no `FLUSHALL` and no pattern sweep: the Redis instance may
be shared with something else on the machine, and a measurement is not a licence to clear somebody
else's data.

## "Cold" means Redis, and only Redis

SQL Server's buffer pool and its plan cache, and the .NET JIT, are all warm throughout. This is
**not** a cold-database measurement, and the report says so in the artifact itself rather than
relying on the reader to remember.

---

# The import scenario package

`ImportRecoveryTests` and `ImportStagingTests` already prove the import contract. This package
exists for a different reason: a test source file is not a recorded command and exit code, and the
plan asks for evidence a reader can check without running xUnit.

`./scripts/import-experiment.ps1` drives seven real `dpl` invocations and records what each one
actually returned:

| Scenario | Exit | Contract |
|---|---:|---|
| success | 0 | every row accepted |
| file-rejected | 1 | file-level failure creates **no job** |
| row-rejected | 2 | rows rejected individually; the file still loads |
| interrupted | 1 | crash before commit leaves a resumable job, id reported |
| resumed | 2 | continues from the SQL checkpoint, CSV not required |
| re-import, same bytes | 2 | returns the stored result: same job id, same counts |
| busy | 3 | refused while another session holds the applock |

## How the awkward two are made deterministic

**interrupted** uses `--fail-before-commit-on-batch`, which `ImportFaultInjector.ForExperiment`
refuses outside `DOTNET_ENVIRONMENT=Testing`. A normal Development run cannot be made to crash on
purpose even if the flag is passed.

**busy** does not race two importers. A session inside the SQL container takes the applock with
`sp_getapplock` and sleeps, so the refusal is deterministic. The password is read from the
container's own environment and never appears on a host command line.

## Isolation

The package runs against a throwaway `DataPerformanceLab_Test_*` database seeded with the `ci`
profile — the categories exist to satisfy the foreign key, and its `SKU-*` products cannot collide
with the samples' `IMP-*` ones. The catalog a published load measurement was taken against is
never touched.

Reports are regenerated with `dpl report` **from SQL, with the original CSV absent**, which is the
part of the contract a stored file could otherwise hide.
