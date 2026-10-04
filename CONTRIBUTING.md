# Contributing

This is a local .NET data-performance lab. Its value is that its measurements can be believed, so
most of the rules below protect the evidence rather than the code style.

Read these before changing anything substantial:

- [README.md](README.md): what the lab shows, and what it does not
- [docs/decisions/](docs/decisions/): measurement boundary, import semantics, cache consistency, scope
- [docs/experiments.md](docs/experiments.md): the measurement protocol
- [docs/runbook.md](docs/runbook.md): commands and troubleshooting

## Verifying a change

```powershell
./scripts/verify.ps1
dotnet format --verify-no-changes
```

`verify.ps1` runs a locked restore, a Release build (warnings are errors), then the unit and
integration suites. The integration tests use real SQL Server 2022 and Redis 7.4 containers via
Testcontainers. Docker must be running. If it is not, the tests **fail**; they are never skipped,
because a green run that touched no database proves nothing.

`verify.ps1` does not run the format check, and neither does CI, so run it yourself.

## Ground rules

- **Local and synthetic only.** No auth, no real data. The API and CLI refuse to start outside
  `Development` or `Testing` ([ADR 004](docs/decisions/004-local-only-scope.md)).
- **No secrets in the repository.** The local SQL password comes from `.env` (copied from
  `.env.example`) or the `MSSQL_SA_PASSWORD` environment variable. CI scans tracked files for
  connection material and absolute user paths.
- **The API never migrates or seeds.** Schema changes and seeding are explicit CLI actions, and
  experiment DDL runs only against lab databases.
- **A lab database is identified by its `LabMetadata` marker, not by its name.** Migrate, seed and
  reset refuse a database that does not carry it.
- **Everything is pinned; nothing floats.** The SDK is pinned in `global.json`
  (`rollForward: disable`). NuGet versions are pinned in `Directory.Packages.props` plus the
  committed `packages.lock.json` files. Container images are pinned by digest in `compose.yaml`,
  and `ImagePinTests` keeps the test fixtures on the same digests. Do not use `latest` tags.
- **Docs describe what exists.** Do not document an unimplemented feature as if it works.

## Measurement rules

These follow from [ADR 001](docs/decisions/001-measurement-boundary.md).

- **A claim needs a run with a manifest.** Every published number traces to a `manifest.json`
  that records the commit, the dirty flag, the environment, the dataset hash and the parameters.
  [docs/evidence-map.md](docs/evidence-map.md) maps each claim to its artifact.
- **One changed factor per experiment.** Q1 changes only the index. Q2 changes only the projection.
  Q3 changes only `Cache:Enabled`.
- **Do not pool run percentiles.** Per-run p95s are never merged into a "global p95"; across-run
  columns are named "median of run p95".
- **Failed runs are reported, not deleted.** An invalid run is published with the reason it is
  invalid.
- **A counter reading must prove which window it describes.** Cache counters are process-wide and
  cumulative, and the warmup sends traffic through the same process. The measured window is the
  difference between two snapshots, and the lookups in that delta must equal the run's measured
  GET count. If they are not equal, the rate describes some other experiment.
- **Dirty-tree measurements carry their diff.** A measurement taken from an uncommitted tree is
  published only with its diff beside it (`results/published/<date>/source-diff/`). Otherwise,
  commit and re-measure from the clean commit.
- **Large raw output is hashed, not committed.** `*.raw.json` and `*.api.log` stay local. Each is
  recorded in the manifest with its SHA-256, size and reproduce command. A file that does not
  exist is recorded as missing and never given a hash.
- **The cache is not strongly consistent.** The TTL bounds the stale window; it does not remove
  it ([ADR 003](docs/decisions/003-cache-consistency.md)). Do not describe it otherwise.
