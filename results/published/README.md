# Published measurements

Every number here comes from a run in this directory, each with a `manifest.json` naming the
commit it was measured on, whether that tree was dirty, the environment, the dataset hash and
the parameters. Nothing is quoted that an artifact does not support.

Read [ADR 001](../../docs/decisions/001-measurement-boundary.md) before citing any of it — it
states what these measurements may and may not be used to claim. The method is in
[docs/experiments.md](../../docs/experiments.md).

See [Commit SHAs in the manifests](#commit-shas-in-the-manifests) for how the recorded commits
relate to the public history.

**Machine:** Windows 11, 32 logical CPUs, SQL Server 2022 and Redis 7.4 in loopback-published
containers sharing that CPU with the API and the load generator. One laptop. No number here is
a capacity figure.

---

## 2026-09-11

Dataset: `default` profile — 100,000 products, seed `20260910`, generator v1,
`DataHash DC7DE2FF4977DE5F…`.

### Q1 — covering index absent vs present

Same query, same DTO, same result; the **only** difference is whether the candidate index
exists. 5 rounds × 20 samples per variant, order alternating AB/BA.

| Category | Index absent | Index present | Ratio | Logical reads |
|---|---:|---:|---:|---|
| 1 (~60% of catalog) | **45.47 ms** | **1.32 ms** | ~34× | 35,218 → **6** |
| 20 | **45.85 ms** | **1.69 ms** | ~27× | 35,218 → **5** |

Median of the five per-round medians. Per-round spread: category 1 `42.21–64.24` vs
`0.92–1.71`; category 20 `35.64–75.83` vs `1.47–2.64`. The distributions do not overlap.

Both variants produced the identical ordered result hash — without that the timing comparison
would have been refused.

The logical-read collapse is the mechanism: the index covers the filter, the ordering and every
column the DTO exposes, so the plan seeks instead of scanning 35,218 pages.

**Not claimed:** any write cost. The index makes inserts and updates more expensive and that
was not measured.

Artifacts: [`q1-cat1/`](20260911/q1-cat1/), [`q1-cat20/`](20260911/q1-cat20/) — real execution
plans (`.sqlplan`), the exact parameterized SQL (`.sql`), `STATISTICS IO`/`TIME` (`.txt`),
every timed sample (`query-samples.json`), manifest.

### Q2 — entity materialization vs SQL projection

The candidate index is present on **both** sides, so no index change is blended in.
`EntityThenMap` materializes the whole `Product` entity — including the ~1 KB `Description` the
DTO never returns — and maps in memory. `SqlProjection` selects the six DTO columns in SQL.

| Category | Entity then map | SQL projection | Ratio | Logical reads |
|---|---:|---:|---:|---|
| 1 | **2.52 ms** | **1.43 ms** | ~1.8× | 168 → **6** |
| 20 | **2.83 ms** | **1.62 ms** | ~1.7× | 167 → **5** |

A real but modest effect, an order of magnitude smaller than Q1's. That ranking is the useful
part: on a slow endpoint, the index is where the first effort goes.

**Not claimed:** that this is a pure payload effect. The plans show the mechanism behind
168 → 6 reads: the index cannot cover `Description`, so `EntityThenMap` adds a key lookup per
row (`Nested Loops` + `Clustered Index Seek`) where `SqlProjection` is one covering seek. Q2
measures wide columns and the lookups they force *together*; it does not split the two.

Artifacts: [`q2-cat1/`](20260911/q2-cat1/), [`q2-cat20/`](20260911/q2-cat20/).

### Q3 — cache off vs on: the cache made it slower

`GET /api/products/{sku}` under a constant arrival rate of 20/s for 60 s, 1,000-SKU fixture,
5 repetitions alternating AB/BA. Same binary, same config, same fixture; the only difference is
`Cache:Enabled`. Each arm is a fresh API process with its own cache namespace.

| Arm | median of run p95 | run p95 range | median of run median |
|---|---:|---|---:|
| cache-off | **2.48 ms** | 2.44 – 2.60 | **1.59 ms** |
| cache-on | **4.15 ms** | 3.93 – 4.38 | **2.62 ms** |

**Enabling the cache made p95 about 1.7× worse**, consistently, in all five repetitions and in
both orders. The two arms' ranges do not overlap at any repetition. The whole comparison was
run twice from two clean commits and reproduced within 0.1 ms.

#### Why — the likely mechanism, not yet a measured one

> **Correction (2026-09-12).** The cache counters for this run were read **once, after the
> measured run finished**. They are process-wide and cumulative, and the warmup drives traffic
> through the same API process — so the figures below cover **warmup plus measurement combined**,
> not the sixty seconds the latency was measured over. Each run's `cache-metrics.json` records
> 2,802 lookups against 1,201 measured GETs in the matching `summary.json`, which is the
> discrepancy made visible. The recorded `0.286` is therefore **not** the measured window's hit
> rate and must not be quoted as one.
>
> The artifacts are kept exactly as they were produced; only their interpretation is corrected.
> `scripts/compare-cache.ps1` now takes a counter snapshot after the warmup exits and another
> after the measured run, and writes the difference as `*.cache-metrics-delta.json` with both
> window boundaries stamped on it.

Counters as recorded here, with the window they actually describe:

| | |
|---|---|
| Hit rate over warmup **+** measurement | **0.286**, identical to three decimals in all five repetitions |
| Per run, warmup **+** measurement | ~800 hits, ~2,000 misses over 2,802 lookups |
| Measured GETs alone, per run | 1,201 |

The latency result does **not** depend on any of this. It comes from the measured k6 process, which
ran after the warmup process had exited.

#### What a delta-bounded re-run measured

The re-run lives at [`20260913/q3-cache/`](20260913/q3-cache/). It gives the measured window's own
figures, and they reconcile exactly — lookups equal measured GETs in **all ten runs**, which is the
check the single cumulative read could never pass:

| | |
|---|---|
| Hit rate, measured window only | **0.1667 – 0.1682** across the five repetitions |
| Per run | ~201 hits, ~1,000 misses over ~1,201 lookups |
| Measured GETs per run | ~1,201 — equal to the lookups, as it must be |

So about **83% of measured lookups miss**, not the ~71% the cumulative figure implied. The
arithmetic is then unkind:

- a **miss** costs a Redis `GET` + the SQL read + a Redis `SET` — strictly more work than the
  SQL read alone;
- a **hit** saves a single-row lookup on a unique index, which cache-off served in ~1.7 ms.

And the low hit rate is structural rather than a misconfiguration: 1,000 fixture SKUs at 20/s over
60 s is about **1.2 requests per SKU**, so most requests are the only request for that SKU inside
the window — and the warmup's entries age past the 60 s TTL partway through it. There is little
reuse for a cache to exploit. A longer TTL would raise the hit rate, and widen the stale window it
exists to bound ([ADR 003](../../docs/decisions/003-cache-consistency.md)).

The re-run also reproduced the latency finding independently, on a different day and a busier
machine. Absolute numbers differ — everything is slower here — but the separation is, if anything,
cleaner:

| Arm | median of run p95 | min – max run p95 | median of run medians |
|---|---:|---:|---:|
| cache-off | 3.96 ms | 3.69 – 5.10 ms | 1.99 ms |
| cache-on | 7.63 ms | 7.50 – 8.60 ms | 3.66 ms |

The cache lost in **all five repetitions on both the median and the p95**, and the two arms' p95
ranges do not overlap at all: cache-off never exceeded 5.10 ms, cache-on never dropped below
7.50 ms. Compare ratios rather than milliseconds across the two runs — the machines were not in
the same state.

#### What this does and does not mean

- It means: **for this workload, on this data, this cache is a cost, not a benefit.** A cache in
  front of an indexed single-row lookup has very little to save.
- It does **not** mean caching is useless generally. The result would plausibly reverse with an
  expensive origin read, a hot subset far smaller than the key space, or a slower link to SQL —
  none of which were measured here.
- The measurement is **biased in the cache's favour** and it lost anyway: the API logs every EF
  command at Information level, and cache-off issues a SQL command on every request while
  cache-on issues one only on a miss, so cache-off carried more logging overhead.

This is the result the measurement discipline exists for. An unalternated, un-repeated,
status-code-only load test on a warm cache would have reported the opposite.

Artifacts: [`q3-cache/`](20260911/q3-cache/) — `comparison.md`, per-run summaries, per-run cache
counters, k6 run output, manifest.

### Runs that were kept because they failed

ADR 001 requires failed runs to be reported rather than deleted.

| Run | Why it is not evidence |
|---|---|
| [`q3-cache-attempt1-invalid/`](20260911/q3-cache-attempt1-invalid/) | One run dropped **313 iterations** — the requested arrival rate was never applied — so it was marked invalid, which left the arms unbalanced and failed the whole comparison (exit 2). |
| [`q3-cache-attempt2-dirty-tree/`](20260911/q3-cache-attempt2-dirty-tree/) | Valid, and its numbers match the published run closely, but its manifest reports `dirty: true`. It was re-measured from a clean commit rather than published from a dirty tree. |
| [`20260913/q3-cache-attempt-redis-stall/`](20260913/q3-cache-attempt-redis-stall/) | Redis became unreachable 23 times during one arm: **133 dropped iterations**, a 103-second request, and a measured window counting 976 lookups against 963 requests. Every request still succeeded via the SQL fallback — the latency did not. Invalid, exit 2. |

An earlier attempt also exposed a real bug in the harness: with one cache namespace shared
across the comparison, the AB/BA alternation put two `cache-on` runs back to back and the
second started on entries the first had written. The captured hit rate made it obvious — ~0.29
on AB repetitions against ~0.64 on BA. Each run now gets its own namespace, and the hit rate is
uniform. The counters were added to explain a result and immediately found a defect instead.

---

## 2026-09-13

> **Measured from a dirty tree.** Every manifest in `20260913/` reports `"isDirty": true`, and
> [`20260913/source-diff/`](20260913/source-diff/) carries the diff those binaries were built
> from — the condition ADR 001 attaches to publishing a dirty-tree run. The changes being
> measured are the ones that make these measurements possible at all, so "measure from a clean
> commit first" is circular here. Commit the diff and re-run to drop this caveat.

### Q3 re-run — the measured window's own hit rate

The correction described under 2026-09-11 above, with its artifacts:
[`q3-cache/`](20260913/q3-cache/). Same protocol, same dataset; the orchestration now bounds the
measured window with a counter snapshot on each side.

| | |
|---|---|
| Hit rate, measured window | **0.1667 – 0.1682** over five repetitions |
| Lookups vs measured GETs | equal in **all ten** runs — the invariant that makes the figure checkable |
| Cache read failures | 0 |
| Comparison status | `valid`, 5/5 runs per arm |
| Latency | cache-off p95 3.69–5.10 ms · cache-on p95 7.50–8.60 ms — **no overlap**, cache slower in every repetition |

Each run carries `cache-metrics-start.json`, `cache-metrics-end.json` and
`cache-metrics-delta.json`; the delta stamps both window boundaries and keeps both cumulative
snapshots, so the subtraction can be audited rather than trusted.

`compare-report` now refuses a run whose measured-window lookups do not equal its request count.
That check is what a single cumulative read could never satisfy, and it catches a second failure
mode too — a run k6 abandoned while the API kept serving and kept counting.

#### The attempt that was thrown away

[`q3-cache-attempt-redis-stall/`](20260913/q3-cache-attempt-redis-stall/) is the run before this
one. Redis became unreachable **23 times** during repetition 2's cache-on arm: 133 dropped
iterations, one request stalling for **103 seconds**, and a measured window that counted 976
lookups against 963 requests.

Every request still succeeded — the degradation path did its job, falling back to SQL with zero
failed checks. The latency did not survive it. `compare-report` marked the run invalid and failed
the whole comparison (exit 2) rather than averaging it in, which is the entire point of the
validity rules. It is published because ADR 001 requires failed runs to be reported, and because
it is the clearest demonstration in this repository of the harness earning its keep.

**Found afterwards (2026-10-04).** A 103-second request is not what 1-second
Redis timeouts should allow. The cause was in the connection holder: it disposed and
re-dialled the multiplexer under a lock whenever it was disconnected, so during an outage every
request queued behind a full connect attempt in turn. A test now reproduces it: 12 concurrent
reads against an unresponsive Redis took **25.3 s** with that code. The multiplexer is now
created once and left to reconnect in the background, with commands failing fast while it is
down; the same test passes well inside its 5 s bound and a second test proves the cache is used
again after Redis recovers. The published numbers above were measured with the old code and are
left as they are. Q3 was then re-measured on the fixed code from a clean commit — see
[2026-10-04](#2026-10-04) below; the stalled attempt itself is not something a re-run can repeat.

### Import — the seven scenarios, with their real exit codes

Every row is an actual `dpl` invocation and the exit code it returned, not a test assertion.
Run against a throwaway `DataPerformanceLab_Test_*` database, so no published catalog was
touched.

| Scenario | Exit | What it shows |
|---|---:|---|
| success | **0** | 6 records, all inserted, checkpoint 6 |
| file-rejected | **1** | bad header rejects the whole file; **no job is created**, so there is nothing to resume |
| row-rejected | **2** | 9 records → 2 inserted, 7 rejected; the file still loads |
| interrupted | **1** | crash before batch 2 commits; job left `Interrupted` and resumable, id printed |
| resumed | **2** | same job continued from its SQL checkpoint — the CSV is never re-read |
| re-import, same bytes | **2** | **same job id**, same counts, same exit — the stored result replayed |
| busy | **3** | a second importer is refused while the applock is held; nothing changed |

The counts reconcile across the crash: the interrupted job ends at `total=3 inserted=2
rejected=1`, and 2 + 1 = 3. Nothing was lost and nothing was done twice.

**Re-import is the row worth reading.** It exits `2`, not `0`. A file is identified by the
SHA-256 of its bytes, so the same bytes find the finished job and return its recorded verdict —
which had rejections. That is a replay, not a second failure. A *different* file carrying the
same products is a different job, and its rows come back `skipped` because the SKUs already
exist.

Artifacts: [`import-scenarios/`](20260913/import-scenarios/) — per-scenario CLI logs,
`import-experiment.json` (command, expected vs actual exit, job ids), and `report.json` /
`rejected-rows.csv` regenerated **from SQL with the original CSV absent**.

Reproduce: `./scripts/import-experiment.ps1 -Output results/local/import-<run-id>`

### Cold cache — first read vs the reads after it

A separate experiment from the warm load comparison below, and deliberately so: a single cold
read inside a 60-second warm run would be diluted into invisibility.

Only the lab's own key for one SKU is deleted — no `FLUSHALL`, no pattern sweep. The cache may
be shared with something else on the machine, and a measurement is not a licence to clear
somebody else's data.

| Read | Expected | Counter delta |
|---|---|---|
| 1 | miss | `hits+0 misses+1 sqlFallbacks+1` |
| 2 | hit | `hits+1 misses+0 sqlFallbacks+0` |
| 3 | hit | `hits+1 misses+0 sqlFallbacks+0` |
| 4 | hit | `hits+1 misses+0 sqlFallbacks+0` |

The test is the **counters**, not the latency. The per-read times fall steeply across all four
reads, including between the three hits — that is JIT and connection warm-up, not cache
behaviour, and reading it as a cache effect would be the exact mistake this package exists to
avoid.

**"Cold" means Redis only.** SQL Server's buffer pool and plan cache, and the .NET JIT, were
warm throughout. This is not a cold-database measurement and must not be quoted as one.

Artifacts: [`cold-cache/`](20260913/cold-cache/) — `cold-cache.json` with per-read deltas and
both scope caveats recorded in the file itself.

Reproduce: start the API, then
`dotnet run --project src/Lab.Cli -- cold-cache --sku <sku> --output results/local/cold`

### The controlled cache tests behind these packages

Invalidation, degraded Redis, the stale-refill race, counter-delta isolation and optimistic
concurrency are proven by integration tests against **real** SQL Server and Redis, not by the
measurement packages. Their recorded outcome is published so the claim is checkable without
re-running xUnit: [`cache-behaviour-tests/`](20260913/cache-behaviour-tests/) — **28 passed, 0
failed**, one row per test with its outcome and duration.

The raw `.trx` is deliberately not published: it embeds absolute machine paths. The JSON is
derived from it and carries none.

---

## 2026-10-04

### Q3 re-measured on the current cache connection code

Every Q3 package above was measured with the old connection holder — the one that re-dialled
Redis under a lock (see [the attempt that was thrown away](#the-attempt-that-was-thrown-away)).
This run repeats the **same experiment** on the fixed code. It is a repetition, not a tuning
pass: nothing in the contract changed.

| | |
|---|---|
| Commit | `2527d191e2b82d5b7b4ddff3f813cdbca8ba1858`, **clean tree** (`isDirty: false`) — no source-diff caveat |
| Command | `./scripts/compare-cache.ps1 -Profile default -Output results/local/q3-closure -Repetitions 5 -Rate 20 -Duration 60s -WarmupDuration 30s -FixtureCount 1000 -Port 8080 -SkipBuild` (Release built from the same commit first) |
| Dataset | `default`, 100,000 products, seed `20260910`, `DataHash DC7DE2FF…` — identical |
| Fixture | 1,000 SKUs, hash `45D7164B…`; `fixture.json` is **byte-identical** to the 2026-09-13 one |
| Contract | 20 req/s constant arrival, 60 s measured, 30 s warmup in a separate k6 process, 5 repetitions AB/BA, fresh API process and cache namespace per arm, TTL 60 s, Redis timeouts 1 s |
| Toolchain | .NET SDK 10.0.400 / runtime 10.0.12, k6 v2.2.0, Docker 29.6.1 |
| Images | SQL Server `sha256:97b44885…`, Redis `sha256:71da9275…` — the same digests as 2026-09-13 |
| Machine | the same Windows 11 / 32-CPU machine; **13 unrelated containers were running** (≈10% of one core combined at the start), which the earlier runs did not record |

| Arm | median of run p95 | min – max run p95 | median of run medians |
|---|---:|---:|---:|
| cache-off | **4.71 ms** | 2.85 – 5.74 ms | 2.20 ms |
| cache-on | **7.69 ms** | 4.70 – 10.95 ms | 3.76 ms |

Per repetition:

| Rep | Order | cache-off p95 | cache-on p95 | cache-off median | cache-on median |
|---:|:---:|---:|---:|---:|---:|
| 1 | AB | 5.74 | 8.97 | 3.01 | 4.80 |
| 2 | BA | 4.71 | 10.95 | 2.61 | 4.37 |
| 3 | AB | 4.63 | 7.69 | 2.09 | 3.61 |
| 4 | BA | 4.75 | 7.36 | 2.20 | 3.76 |
| 5 | AB | 2.85 | 4.70 | 1.82 | 3.07 |

| Validity | |
|---|---|
| Comparison status | `valid`, 5/5 runs per arm, exit 0 |
| Requests | 1,200–1,201 per run (20/s × 60 s); 0 failed requests, 0 failed checks |
| Dropped iterations | **0** in all ten runs |
| Cache read / write failures | **0** / **0** |
| Lookups vs measured GETs | equal in **all ten** runs |
| Hit rate, measured window | **0.1674 – 0.1774** (≈201–213 hits per ~1,201 lookups) |
| Slowest single request | 60.0 ms (cache-on, rep 2); cache-off 16.3 ms |

**What reproduced.** The direction and the mechanism. The cache lost in **every repetition, on both
the median and the p95**. The median-of-p95 ratio is ~1.6× (7.69 / 4.71), next to ~1.7× on
2026-09-11 and ~1.9× on 2026-09-13. About 82–83% of measured lookups still miss.

**What did not reproduce.** The earlier runs' "the two arms' p95 ranges do not overlap". Here they
do: cache-on's best repetition (4.70 ms, rep 5) is below cache-off's worst (5.74 ms, rep 1).
Repetition 5 was faster for **both** arms, so this is drift in machine state between repetitions,
which AB/BA alternation exists to separate from the arms. Within each repetition the cache-on arm
was still the slower one. The claim that survives all three packages is "slower in every
repetition", not "non-overlapping across repetitions".

The hit rate is slightly higher than on 2026-09-13 (0.167–0.177 against 0.167–0.168). Repetitions 1–3
counted 208–213 hits and repetitions 4–5 counted 201. The cause was not investigated, and nothing
above depends on it.

**What this run does not show.** No Redis outage happened during it, so it says nothing about
outage behaviour. The fix's evidence for that is the integration tests (12 concurrent reads against
an unresponsive Redis now finish well inside 5 s, where the old code took 25.3 s). What the run
does show is that the steady-state result was not an artefact of the old connection code.

Artifacts: [`20261004/q3-cache/`](20261004/q3-cache/) — the same file set as 2026-09-13:
`comparison.md`/`.json`, `runs.json`, fixture, per-run summaries, k6 and warmup logs, and the
start/end/delta cache counters. `*.raw.json`, `*.api.log` and `*.api.err` stay local, and each has
its SHA-256, size and reproduce command in `manifest.json`.

---

## What is kept here, and what is not

Committed: summaries, `comparison.md`/`comparison.json`, cache counters (including the
measured-window deltas), k6 run output, plans, SQL, `STATISTICS IO`, timed samples, CLI logs,
manifests.

Not committed: the raw k6 sample stream (`*.raw.json`) and the API's own log (`*.api.log`) —
together ~85 MB per comparison, regenerated by re-running.

**"Kept locally" is a verifiable claim, not an excuse.** Each manifest carries a `storedFiles`
array in which every file the run produced — published or not — is listed with its byte size,
its SHA-256 and the command that recreates it:

```json
{
  "path": "runs/1-AB-cache-off.raw.json",
  "storage": "local-only",
  "sizeBytes": 5583021,
  "sha256": "…",
  "reproduceCommand": "./scripts/compare-cache.ps1 -Profile default -Output … -Rate 20 …"
}
```

So a reader with a local copy can prove it is the same file, and a reader without one can make
it again. A file that was expected and is genuinely absent is recorded as `"storage":
"missing"` with no hash — never given a fabricated one.

`compare-report` knows the difference: the small artifacts are required and their absence
invalidates a run, while `*.raw.json` and `*.api.log` are never on that list. Publishing without
the large files therefore cannot be mistaken for a broken comparison.

## Commit SHAs in the manifests

The public history starts at the `v1.0.0-lab` release commit. The development history before it
was squashed into that commit on 2026-10-04, so the commit SHAs that the manifests record are not
reachable in this repository. The manifests are left exactly as they were produced. They are
measurement records, and rewriting their `commitSha` would falsify them.

| Recorded commit | Packages | Relation to the public history |
|---|---|---|
| `2527d191e2b8…` (clean) | `20261004/q3-cache` | Its `src/`, `tests/`, `scripts/` and `experiments/` trees are **identical** to the release commit's. Check with the tree hashes below. |
| `1268385f904e…` (dirty) | every `20260913/` package | Pre-release. The measured tree is that commit plus [`20260913/source-diff/`](20260913/source-diff/); the base commit is not public. |
| `1268385f904e…` (clean) | `20260911/q3-cache` | Pre-release. Older connection code; superseded by `20261004/q3-cache`. |
| `2cd9f19e4318…` | `20260911/q3-cache-attempt1-invalid`, `-attempt2-dirty-tree` | Pre-release. Failed or unpublished attempts, kept as a record. |
| `4d22206264039…` (clean) | `20260911/q1-*`, `20260911/q2-*` | Pre-release. Not re-measured on the release code; the commands below re-run them. |

Tree hashes of the measured `2527d19` source, which the release commit carries unchanged:

| Path | Git tree hash |
|---|---|
| `src` | `db329eb9bb90df77a0de7e9921a9807a2b5c278e` |
| `tests` | `fb5e1ce5ebfba360e887daa31aab447257937c56` |
| `scripts` | `153447ae0b96401bbcc371c44bac812cd0c59a74` |
| `experiments` | `59c107bff077a8a19a44833485fb0ec8dbce9385` |

```powershell
git rev-parse v1.0.0-lab:src v1.0.0-lab:tests v1.0.0-lab:scripts v1.0.0-lab:experiments
```

## Reproducing

```powershell
./scripts/bootstrap.ps1 -StartContainers
dotnet run --project src/Lab.Cli -- db migrate --database DataPerformanceLab_Experiment
dotnet run --project src/Lab.Cli -- seed --profile default --database DataPerformanceLab_Experiment
dotnet run --project src/Lab.Cli -- query --experiment index --category 1 --output results/local/q1-cat1

dotnet run --project src/Lab.Cli -- db migrate
dotnet run --project src/Lab.Cli -- seed --profile default
./scripts/compare-cache.ps1 -Profile default -Output results/local/q3

# The 2026-09-13 packages
./scripts/import-experiment.ps1 -Output results/local/import
dotnet run --project src/Lab.Api                      # then, in another shell:
dotnet run --project src/Lab.Cli -- cold-cache --sku SKU-0000000004 --output results/local/cold
```

Run `./scripts/bootstrap.ps1` **in the shell you will work in** — not `pwsh -File`, which sets
the environment in a child process that then exits. The script warns if you do.

Expect different absolute numbers on different hardware. If a **ratio** comes out materially
different, the manifests are where to start — they now record the SDK, the data-path package
versions, the container images by digest and the CPU/memory limits those containers ran under.

A load comparison wants an otherwise idle machine. Running anything else heavy alongside it —
another experiment, a build, a test suite — contaminates the latency it reports, and the
manifest cannot detect that for you.
