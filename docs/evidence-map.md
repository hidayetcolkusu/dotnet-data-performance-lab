# Evidence map

Each technical claim this lab makes, mapped to the test, result or artifact that supports it.

The rule: **a row is not written before its evidence exists.** A claim without an artifact does
not belong in this table, or anywhere else in the repository. Every row below points at evidence
that is already committed or produced by an existing command.

Paths under `results/published/` are committed. Paths under `results/local/` are git-ignored by
design: regenerate them, do not cite them.

---

## Claims and their evidence

### 1. The index baseline was measured before anything changed

| | |
|---|---|
| **Claim** | The baseline was measured and its plan captured *before* anything changed. |
| **Evidence** | `results/published/*/q1-cat1/index-cat1-IndexAbsent.sql` (the exact parameterized statement), `.sqlplan` (the real execution plan), `.txt` (`STATISTICS IO` / `TIME`, including logical reads), `manifest.json` |
| **Method** | [experiments.md § Q1/Q2](experiments.md) |
| **Do not claim** | that the baseline was slow "in production" — there is no production. |

### 2. The index and the projection were measured separately

| | |
|---|---|
| **Claim** | Two separate experiments, each changing exactly one factor, with the result provably unchanged. |
| **Evidence** | Separate `q1-cat*/` and `q2-cat*/` directories, each with `query-samples.json` carrying per-round min/mean/median/p95/max **and** `resultHashByVariant` showing both variants produced the identical ordered result |
| **Key detail** | Q2 keeps the index present on both sides, so the projection effect is not blended with an index *change*. It is still not a pure payload effect: the entity variant cannot be covered by the index and adds a key lookup per row (see the `.sqlplan`), so Q2 measures columns and lookups together. |
| **Do not claim** | a combined "we made it N× faster" number that merges Q1 and Q2. |

### 3. A CSV import with partial failures rejects rows, not files

| | |
|---|---|
| **Claim** | Bad rows are rejected individually with a specific reason; the file still loads; the counts reconcile exactly. |
| **Evidence** | `data/samples/catalog-mixed.csv` + the import report (`report --job-id ... --output ...`), `ImportStagingTests`, `ImportValidationTests` |
| **Key detail** | `Inserted + SkippedExisting + Rejected == TotalRecords`, always. Exit `2` distinguishes "loaded with rejections" from "did not load". |
| **Do not claim** | that the import repairs or normalizes bad data. It rejects and reports. |

### 4. Re-running an import is safe, and a crash cannot lose or duplicate rows

| | |
|---|---|
| **Claim** | Re-running is safe and cheap; a crash mid-import cannot lose or duplicate rows. |
| **Evidence** | `ImportRecoveryTests` — including a fault injected *between* the product insert and the checkpoint write — plus `results/published/20260913/import-scenarios/`, seven real CLI runs with their recorded exit codes (0/1/2/1/2/2/3) and reports regenerated from SQL without the CSV |
| **Key detail** | The checkpoint commits in the same transaction as the rows it describes. Session 4 of the [learning guide](learning-guide.md) breaks this on purpose and shows the test catching it. |
| **Key detail 2** | Re-running the **same bytes** exits `2`, not `0`: it replays the stored verdict of a job that had rejections. A *different* file with the same products is a new job whose rows come back `skipped`. |
| **Do not claim** | idempotent *updates*. The import is insert-only ([ADR 002](decisions/002-import-semantics.md)). Nor that a re-import "succeeds": name which of the two re-import cases is meant. |

### 4b. The first read after an eviction is measured separately

| | |
|---|---|
| **Claim** | The cold read path is measured separately from the warm one, with the cache counters — not the clock — as the test. |
| **Evidence** | `results/published/20260913/cold-cache/cold-cache.json` — per-read counter deltas, miss then hits |
| **Key detail** | Only one key is evicted; no `FLUSHALL`. "Cold" means Redis only — SQL's buffer pool and plan cache and the .NET JIT stay warm. The per-read latencies fall across the *hits* too, which is warm-up, not cache behaviour. |
| **Do not claim** | a cold-database or cold-start number, or a latency conclusion from a single request. |

### 5. The cache's benefit and its cost were measured, not assumed

| | |
|---|---|
| **Claim** | The cache off/on difference was measured rather than assumed, and the stale window it costs is demonstrated rather than hand-waved. The measured direction is reported as it came out. |
| **Evidence** | `results/published/*/q3-cache/comparison.md` + `manifest.json` (off/on), `CacheBehaviorTests` (invalidation, degraded-Redis fallback, the stale-refill race via a test seam), `PriceConcurrencyTests` (optimistic concurrency, `409`) |
| **Key detail** | The stale window has two causes: TTL, and a read-modify race the code cannot prevent. Both are documented in [ADR 003](decisions/003-cache-consistency.md). |
| **Do not claim** | that the cache gives consistency, read-your-writes, or reduced write contention — nor that it is faster, without pointing at the run that shows it for that workload. |

### 6. Every p95 comes with its load profile and every run behind it

| | |
|---|---|
| **Claim** | The load profile, the error budget and every individual run are published — not just a headline percentile. |
| **Evidence** | `q3-cache/manifest.json` (rate, duration, repetitions, fixture hash, SDK/package/image versions, container limits, k6 version, commit + dirty flag, `storedFiles` hashes), `runs.json` (the declared workload, each run's k6 exit code and required artifacts), `comparison.md` (one row per run, plus failed checks and dropped iterations), `runs/*.cache-metrics-delta.json` (the measured window's own counters). Raw sample streams and API logs stay local and are recorded in `storedFiles` by hash + size + reproduce command |
| **Key detail** | Per-run p95 values are never pooled into a global p95; across-run columns are named "median of run p95". `ExperimentSummaryTests` enforces this on the report *type*, and also that both arms faced one fixture, one rate and one duration, with every repetition a complete pair. |
| **Do not claim** | an RPS capacity figure, or a percentile computed across repetitions. |

### 6b. The published hit rate describes only the measured window

| | |
|---|---|
| **Claim** | The measured window's cache counters are bounded by a snapshot on each side, so the published hit rate describes only the run whose latency is reported. |
| **Evidence** | `results/published/20260913/q3-cache/runs/*.cache-metrics-delta.json` — both window timestamps, the difference, and both cumulative snapshots so the subtraction is auditable |
| **The check** | lookups in the delta **equal** the measured GET count in that run's `summary.json`. A cumulative read can never satisfy it, which is how the original error would have been caught. |
| **Key detail** | The counters are process-wide and cumulative, and the warmup drives traffic through the same API process. The `20260911` run read them only once, at the end, so its `0.286` covers warmup **and** measurement together; it is labelled as such and must not be quoted as a measured-window rate. A delta-bounded re-run measures **0.167**, with lookups equal to measured GETs — that equality is the check a cumulative read can never pass. |
| **Do not claim** | a hit rate for any run that has no `cache-metrics-delta.json`, nor a causal story about latency built on a cumulative counter read. The re-run (`results/published/20260913/q3-cache/`) was taken from a dirty tree with its diff published beside it. Compare runs by ratio, not by milliseconds — they were measured in different machine states. The 2026-10-04 re-run (`results/published/20261004/q3-cache/`, clean commit `2527d19`, current connection code) measures 0.167–0.177 with the same equality in all ten runs; it does **not** support "non-overlapping p95 ranges" — only "cache slower in every repetition". |

---

## Claims this project can never support

These sentences are easy to write about a project like this one. None of them is supported:

| Tempting sentence | Why it is unsupported |
|---|---|
| "Handles N requests per second." | Loopback HTTP on one laptop, containers sharing CPU. No capacity was measured. |
| "Reduced production latency by X%." | There is no production and no real traffic. |
| "The cache keeps data consistent." | It bounds staleness; it does not remove it ([ADR 003](decisions/003-cache-consistency.md)). |
| "Index made the system N× faster." | It made *one measured query* ~34× faster on this data, at an unmeasured write cost. |
| "Adding Redis made reads faster." | Measured, it made them **slower** here: ~1.6× on the median run p95 in the 2026-10-04 re-measurement, ~1.7× and ~1.9× in the earlier packages. Any caching claim must name the workload and the run. |
| "Production-ready." | No deployment surface, auth, tenancy or operational history exists ([ADR 004](decisions/004-local-only-scope.md)). |
| "Zero-downtime migrations." | Migrations were never exercised against a live workload. |

---

## Reproducing any row above

```powershell
./scripts/bootstrap.ps1 -StartContainers
./scripts/verify.ps1
```

Then the specific command from [the runbook](runbook.md). Every published run directory carries
a `manifest.json` naming the commit it was measured on and whether that tree was dirty — start
there when a number you reproduce does not match.
