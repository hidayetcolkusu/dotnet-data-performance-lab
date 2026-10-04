# Verification history, 2026-09-11 to 2026-10-04

**Historical record. For the current verified state, see [../verification.md](../verification.md).**

These entries were written during development, before the public history was squashed into the
`v1.0.0-lab` release commit. The commit SHAs they name (`397be24`, `cecc218`, `f588b54`,
`2527d19`, `18e8860`, `b185ba9`, `fa19ab8`, `9542211`) belong to that pre-release history and are
not reachable in this repository. Test counts, CI notes and open items describe those commits
only. Some open items were closed later; the current page says which are still open.

Entries are kept as written, newest first, apart from neutral wording edits. Failed attempts are
listed, not dropped.

---

## 2026-10-04 — release closure (`2527d19`)

A newer entry than the one below. It covers the commit with the Redis connection fix, the
digest-pinned test images and `ImagePinTests`, and it does not change anything recorded below.

### Environment

| | |
|---|---|
| Machine | Windows 11 Pro 10.0.26200, PowerShell 7.6.6 (same machine as below) |
| .NET SDK | 10.0.400 (`global.json`, `rollForward: disable`), runtime 10.0.12 |
| Docker | Engine 29.6.1, Compose v5.3.0 |
| SQL Server image | `mcr.microsoft.com/mssql/server:2022-latest@sha256:97b448857967be55e005424a660056fe6d51814435804dc07e8f79f028bab5fb` — `compose.yaml` and the test fixture request this exact `tag@digest` |
| Redis image | `redis:7.4@sha256:71da9275c5f3fcb97d0fa0c8c5b36cc995327265420f17a04bfd544f458059f7` — likewise |
| k6 | v2.2.0 (local binary) |
| Warnings | `TreatWarningsAsErrors` is on (`Directory.Build.props`); the Release build reported 0 warnings, 0 errors |

### Run C — working tree before the commit, then `2527d19`

| Step | Tree | Exit | Result |
|---|---|---:|---|
| `./scripts/verify.ps1` | uncommitted fixes on `fa19ab8` | 0 | unit **185**, integration **106** passed; 0 failed, 0 skipped; 135 s |
| `dotnet format --verify-no-changes` | same | **2** | 9 `WHITESPACE` errors in 3 files. The edits had written LF line endings into the working copy; under `core.autocrlf=true` a checkout gives CRLF. Content unchanged after normalising (diff hash identical) |
| `dotnet format --verify-no-changes` | same, after normalising | 0 | — |
| `./scripts/verify.ps1` | `2527d19` + docs/results only (code identical) | 0 | locked restore, Release build 0 warnings; unit **185**, integration **106** passed; 0 failed, 0 skipped; 120 s |
| `dotnet format --verify-no-changes` | same | 0 | — |

Integration tests ran against real SQL Server and Redis containers started by Testcontainers. The
4 integration tests added since the previous entry are the Redis outage and recovery tests and
`ImagePinTests`. `verify.ps1` itself does not run `dotnet format`, and neither does CI. The format
check above was run by hand.

### Q3 re-measurement on `2527d19`

`./scripts/compare-cache.ps1 … -Repetitions 5 -Rate 20 -Duration 60s -WarmupDuration 30s
-FixtureCount 1000 -Port 8080 -SkipBuild`, exit 0, status `valid`, clean tree. The numbers and what
they do and do not support are in
[results/published/README.md](../published/README.md#2026-10-04).

### Remote CI and fresh clone

Recorded in the entries that follow this commit, not here.

### Run D — fresh clone of `18e8860`

`18e8860` is `2527d19` plus the Q3 package, the doc updates and Run C's entry, with no code
changes.

| | |
|---|---|
| Commit cloned | `18e8860120c5d3574852e5672d45c5ba36e7a788` |
| Clone | `git clone --no-local` into a new short temp directory, then `git status --porcelain` → empty |
| Carried over | nothing: no `.env`, 0 `bin/`/`obj/` directories, no `results/local/` (checked before the first step) |
| Shell | new `pwsh -NoProfile`; `MSSQL_SA_PASSWORD`, `DOTNET_ENVIRONMENT`, `ASPNETCORE_ENVIRONMENT`, `DPL_*`, `Data__*` and `ASPNETCORE_URLS` removed before the first step |
| Isolation | `.env` from `.env.example` plus the runbook's second-checkout settings: SQL `14334`, Redis `16380`, compose project `dpl-fresh-clone`, container prefix `dpl-fc`. The existing `dpl-sql`/`dpl-redis` containers and their volumes were not used |

| Step (README / runbook order) | Exit | Time |
|---|---:|---:|
| `./scripts/bootstrap.ps1 -StartContainers` (dot-sourced) | 0 | 17 s |
| `dotnet run --project src/Lab.Cli -- db migrate` | 0 | 12 s |
| `dotnet run --project src/Lab.Cli -- seed --profile ci` | 0 | 4 s |
| `dotnet run --project src/Lab.Cli -- fixture --output results/local/fc-fixture.json --count 200` | 0 | 3 s |
| `dotnet run --project src/Lab.Api` → `GET /health/ready` on `127.0.0.1:8080` | 200 | 2 s |
| `k6 run … -e SMOKE_ITERATIONS=100 experiments/k6/smoke.js` | 0 | 2 s |
| `./scripts/verify.ps1` (locked restore from scratch, Release build, all tests) | 0 | 118 s |
| `dotnet format --verify-no-changes` | 0 | 10 s |
| `./scripts/compare-cache.ps1 -Profile ci … -Repetitions 1 -Duration 10s -WarmupDuration 5s -FixtureCount 200` | 0 | 78 s |
| `docker compose down -v` | 0 | 2 s |

- **Tests:** unit **185 passed**, integration **106 passed**; 0 failed, 0 skipped. Release build
  0 warnings.
- **k6 smoke:** 100 SKUs, **900 checks passed, 0 failed**, fixture hash `A42F50D5F848677C…`, the
  same as Run B. This is a correctness smoke, not a measurement.
- **Q3 harness:** a 10-second, one-repetition run on the `ci` profile. It shows that the
  experiment path runs end to end from a clean clone: fixture, an API process per arm, warmup,
  measured k6, counter snapshots and `compare-report` (status `valid`). It is **not** a measurement
  and was not published.
- **Tree after the run:** `git status --porcelain` empty. Nothing the flow wrote escaped the ignore
  rules.

#### What the clone exposed

1. **Ten published files were never committed.** The 2026-09-13 import package lists its
   per-scenario CLI logs (`import-scenarios/logs/*.log`) as `published` in its manifest, and
   `results/published/README.md` cites them. `.gitignore`'s `*.log` rule had kept them out of every
   commit. The local copies match their manifest SHA-256 byte for byte and contain no paths or
   credentials. They are committed after this entry, together with a narrow un-ignore rule. They
   are the original files, not regenerated ones.
2. **33 published files do not match their manifest hash in a Windows checkout.** Every one of them
   matches once CRLF is normalised to LF. They are `summary.json` files and `rejected-rows.csv`
   files that were written with LF, and `core.autocrlf=true` turns them into CRLF on checkout. The
   cause is line-ending conversion, not altered content. It applies equally to the 2026-09-13
   packages and is not fixed here; see *Open items*.

### Final HEAD `b185ba9`

`b185ba9` adds the ten import logs, the `.gitignore` rule and Run D's entry, with no code
changes. A second fresh clone of it ran:

- the CI hygiene checks by hand: ignore rules, no local paths, no secrets (all clean);
- no manifest-`published` file missing (0);
- `verify.ps1` exit 0, with **185 + 106** passed and 0 skipped;
- `dotnet format` exit 0;
- `git status --porcelain` empty afterwards.

### Remote CI — `b185ba9`

The repository is public now, so the GitHub API answers without credentials. This is read from the
API, not from screenshots.

| | |
|---|---|
| Run | [37207535057](https://github.com/hidayetcolkusu/dotnet-data-performance-lab/actions/runs/37207535057), `push` to `main`, head `b185ba98cd101904dfdaa848692be33f036d3e45` |
| Conclusion | **success** |
| `build-and-test` | success, all 17 steps. Locked restore 8 s, Release build 13 s, unit tests 6 s, **integration tests (real SQL Server + Redis) 121 s**, k6 smoke 1 s |
| `hygiene` | success, all 3 checks |
| Artifact | `ci-reports`, 74,784 bytes (the `.trx` files, API log, smoke summary) |
| Annotations | 1 warning per job: `actions/checkout@v4` and `actions/setup-dotnet@v4` target the deprecated Node.js 20 and are forced onto Node.js 24 |

**Not claimed:** the per-suite test counts inside CI. Job logs and artifact downloads need an
authenticated token (the log endpoint returned 403), and none was used. The 121 s integration
step matches the 100–125 s the same 106 tests took locally. A run that discovered no tests would
finish in seconds. That is consistent with a real run, but it does not prove the count.

### Open items

- Published artifacts have no `.gitattributes` rule, so their checked-out bytes depend on the
  reader's `core.autocrlf`. Files written with LF fail on a Windows checkout, and the CRLF `k6.log`
  files would fail on an LF checkout. To check a hash, compare with line endings normalised both
  ways. Fixing this would rewrite
  the stored bytes of already-published files, which is out of scope for this entry.
- `verify.ps1` and CI do not run `dotnet format`. It passes today because it was run by hand.

---

## 2026-10-04

### Environment

| | |
|---|---|
| Machine | Windows 11 Pro 10.0.26200, PowerShell 7.6.6 |
| .NET SDK | 10.0.400 (`global.json`, `rollForward: disable`) |
| Docker | Engine 29.6.1, Compose v5.3.0 |
| SQL Server image | `mcr.microsoft.com/mssql/server@sha256:97b448857967be55e005424a660056fe6d51814435804dc07e8f79f028bab5fb` |
| Redis image | `redis@sha256:71da9275c5f3fcb97d0fa0c8c5b36cc995327265420f17a04bfd544f458059f7` |
| k6 | v2.2.0 (local binary; CI uses the digest-pinned image) |
| NuGet | reachable (`api.nuget.org` returned 200) |

The digests are the ones `compose.yaml` pins. The integration tests ask Testcontainers for the tags
`mcr.microsoft.com/mssql/server:2022-latest` and `redis:7.4`; on this machine those tags resolved
to the same two digests. The test fixtures do not pin a digest themselves — see *Open items*.

### Run A — working tree at `cecc218`

| | |
|---|---|
| Commit | `cecc218bdb708d650c1c6e89bfd5dc5a1600c709` |
| Tree | clean apart from one untracked local note outside the build |
| Command | `pwsh -NoProfile -File ./scripts/verify.ps1` |
| Locked restore | exit 0, but a **no-op** ("all projects are up to date") — existing `obj/` assets were reused |
| Unit tests | **185 passed**, 0 failed, 0 skipped |
| Integration tests | **102 passed**, 0 failed, 0 skipped (real SQL Server + Redis via Testcontainers) |
| Duration | 206 s |
| Exit code | **0** |

Because the restore was a no-op here, Run A alone does not prove the lock files restore from the
network. Run B does.

### Run B — clean clone at `f588b54`

`f588b54` is `cecc218` plus the fix for the hidden-state problem found in the first attempt (below):
the compose project and container names became overridable, defaults unchanged.

| | |
|---|---|
| Commit cloned | `f588b54a09c940f4568168fb5e141c3e35cd8103` |
| Clone | `git clone` into a new temp directory, then `git status --porcelain` → empty |
| Carried over | nothing: no `.env`, no `bin/`/`obj/`, no `results/local/` (checked before the first step) |
| Shell | a new `pwsh -NoProfile` process with `MSSQL_SA_PASSWORD`, `DOTNET_ENVIRONMENT` and `ASPNETCORE_ENVIRONMENT` cleared |
| Isolation | `.env` from `.env.example`, plus the runbook's second-checkout settings: SQL `14334`, Redis `16380`, compose project `dpl-e2-clean`, container prefix `dpl-e2`. The existing `dpl-sql`/`dpl-redis` containers and their volumes were not started or touched. |

| Step (README / runbook order) | Exit | Time |
|---|---:|---:|
| `./scripts/bootstrap.ps1 -StartContainers` (dot-sourced in the same session) | 0 | 23 s |
| `dotnet run --project src/Lab.Cli -- db migrate` | 0 | 10 s |
| `dotnet run --project src/Lab.Cli -- seed --profile ci` | 0 | 5 s |
| `dotnet run --project src/Lab.Cli -- fixture --output results/local/e2-fixture.json --count 200` | 0 | 4 s |
| `dotnet run --project src/Lab.Api` → `GET /health/ready` on `127.0.0.1:8080` | 200 | 2 s |
| `k6 run -e BASE_URL=… -e FIXTURE=… -e SMOKE_ITERATIONS=100 experiments/k6/smoke.js` | 0 | 1 s |
| `./scripts/verify.ps1` | 0 | 135 s |
| `docker compose down -v` | 0 | 3 s |

- **Locked restore:** real this time — all five projects restored from scratch against the
  committed `packages.lock.json` files, no NU1xxx error.
- **Tests:** unit **185 passed**, integration **102 passed**; 0 failed, 0 skipped — **287 total**.
- **k6 smoke:** 100 of 100 iterations, **900 checks passed, 0 failed**, fixture hash
  `A42F50D5F848677C…`. A correctness smoke, not a measurement.

#### Failed attempts before Run B (kept, not dropped)

1. **Hidden state: a second checkout attached to the first one's database.** `compose.yaml` fixed
   the project name (`dotnet-data-performance-lab`) and the container names (`dpl-sql`,
   `dpl-redis`). Following the README in a clone on this machine would have reused the existing
   containers and volumes — migrating and seeding the development database. Found by inspection
   before any container was started. Fixed in `f588b54` (`DPL_COMPOSE_PROJECT`,
   `DPL_CONTAINER_PREFIX`; runbook section *A second checkout on the same machine*).
2. **Clone into a deep temp path.** Checkout failed on Windows path length; every step then
   failed with exit 1 because the files were missing. No container was started. Re-cloned into a
   short path.
3. **API on a non-default port.** The driver set `ASPNETCORE_URLS` to another port, but
   `dotnet run` applies `launchSettings.json` (`127.0.0.1:8080`), so the readiness probe never
   reached it and the smoke exited 107. A mistake in the driver, not the repo; the remaining steps
   in that attempt (locked restore, 185 + 102 tests, teardown) all exited 0. Run B uses the README's
   port.

### Remote CI

`gh` is not installed on this machine and the repository is private, so the GitHub API could not
be queried from here (404 without credentials). Both runs below are recorded from screenshots of
the Actions run pages, taken by the owner on 2026-10-04.

| Run | Head | Status | `build-and-test` | `hygiene` | Total | Artifacts |
|---|---|---|---:|---:|---:|---:|
| 1 — push of `main` + tag `v1.0.0-lab` | not shown (push contained `f588b54`, `9542211`) | **Success** | 3 m 52 s | 5 s | — | — |
| 2 — push of `main` | **`fa19ab8`** (shown on the page) | **Success** | 3 m 37 s | 7 s | 3 m 41 s | 1 |

`build-and-test` is locked restore, Release build, unit + integration tests against real SQL
Server and Redis, and the k6 correctness smoke (per `ci.yml`); a red step fails the job.
`fa19ab8` differs from the tagged `9542211` only in this file, so run 2 is also a green CI result
for the tagged code.

Annotations, identical on both runs: 2 warnings — `actions/checkout@v4`,
`actions/setup-dotnet@v4` and `actions/upload-artifact@v4` target the deprecated Node.js 20 and
are forced onto Node.js 24; 2 notices — `ubuntu-latest` moves to Ubuntu 26 from 2026-10-19.

**Not visible in the screenshots, so not claimed:** the run URLs and the per-suite test counts
and smoke summary inside CI. They are in the job logs and the uploaded artifact.

### Open items

- The CI runs' URLs and in-CI test counts (above).
- `ci.yml` runs on `ubuntu-latest`, a moving label that becomes Ubuntu 26 on 2026-10-19, and uses
  actions that target Node.js 20. Neither has failed a run yet.
- The integration-test fixtures request image **tags** (`2022-latest`, `7.4`), not the digests
  `compose.yaml` pins. They matched on this run; a future pull could move them.
  *Closed after this entry:* the fixtures now request `tag@digest`, and `ImagePinTests` fails if
  they drift from `compose.yaml`. This entry's numbers still describe `f588b54`.

---

## Earlier record

- `397be24` — clean checkout, `verify.ps1` exit 0, 206/206 tests. Historical; the suite has grown
  since and that figure describes that commit only.
