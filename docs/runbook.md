# Runbook

Operating the lab: first run, the everyday commands, and what to do when something breaks.

Everything here runs on one machine against synthetic data. Containers publish on `127.0.0.1`
only — this is not a service anyone else can reach, and it is not meant to become one.

## Prerequisites

| Requirement | Why | Check |
|---|---|---|
| .NET SDK **10.0.400** | pinned in `global.json` with `rollForward: disable` | `dotnet --version` |
| Docker engine | SQL Server + Redis containers, and Testcontainers for the tests | `docker version` |
| PowerShell 7+ | the scripts use `pwsh` syntax | `pwsh -Version` |
| [k6](https://grafana.com/docs/k6/latest/set-up/install-k6/) | only for the cache load comparison | `k6 version` |

k6 is **not** needed to build, test or run the API.

## First run

```powershell
./scripts/bootstrap.ps1 -StartContainers
dotnet run --project src/Lab.Cli -- db migrate
dotnet run --project src/Lab.Cli -- seed --profile ci
dotnet run --project src/Lab.Api            # http://127.0.0.1:8080
```

`bootstrap.ps1` creates `.env` from `.env.example` if missing, loads it into the session, and
derives the application's `Data__SqlServer__Port` / `Data__Redis__Port` from the same
`DPL_SQL_PORT` / `DPL_REDIS_PORT` values `compose.yaml` publishes. Run it in **every new
shell** — it sets process environment, which does not persist.

> **Run it in the shell you will work in** — `./scripts/bootstrap.ps1` as shown, not
> `pwsh -File scripts/bootstrap.ps1`. The `-File` form starts a second PowerShell, sets the
> variables there and discards them on exit: the containers come up, but the shell you are typing
> in still has no password, ports or environment, and the next `dotnet run` fails. The script
> warns when it detects this. Dot-sourcing (`. ./scripts/bootstrap.ps1 -StartContainers`) works
> too and is what you need if you wrap it in another script.

The API and CLI refuse to start outside `Development` / `Testing`.

## Seed profiles

| Profile | Products | Use |
|---|---|---|
| `ci` | 1,000 | tests, smoke checks, quick local work |
| `default` | 100,000 | the profile measurements are published from |
| `large` | 1,000,000 | volume behaviour; slow to seed |

Seeding is deterministic: the same profile always produces the same rows and the same
`DataHash`. A database already seeded with a different profile refuses to re-seed without an
explicit `--reset --database <name>`.

## Everyday commands

```powershell
# Schema
dotnet run --project src/Lab.Cli -- db migrate [--database DataPerformanceLab_Experiment]

# Import
dotnet run --project src/Lab.Cli -- import --file data/samples/catalog-valid.csv
dotnet run --project src/Lab.Cli -- resume --job-id <guid>
dotnet run --project src/Lab.Cli -- report --job-id <guid> --output results/local/import

# Query experiments (experiment database only)
dotnet run --project src/Lab.Cli -- query --experiment index --category 1 --output results/local/q1

# Cache load comparison (needs k6)
./scripts/compare-cache.ps1 -Profile default -Output results/local/<run-id>

# Import scenarios end to end: success, file-rejected, row-rejected, interrupted, resumed,
# re-imported. Records the real exit code of each. Uses its own throwaway test database.
./scripts/import-experiment.ps1 -Output results/local/import-<run-id>

# Cold-cache read: evicts one SKU's entry, then reads it twice against a running API.
# Start the API first, with the same Cache:InstanceId the CLI is configured with.
dotnet run --project src/Lab.Cli -- cold-cache --sku SKU-0000000001 --output results/local/cold
```

CLI exit codes: `0` success · `1` infrastructure/argument error · `2` import had row
rejections, or `compare-report` found the comparison invalid · `3` import busy (another
importer holds the lock).

## Verify

```powershell
./scripts/verify.ps1               # locked restore, Release build, unit + integration tests
```

Integration tests start their own SQL Server and Redis containers through Testcontainers, each
test using its own database and cache namespace. They are **not skippable**: if Docker is
unreachable they fail with an explanation rather than reporting green.

## Troubleshooting

### `ports are not available: ... bind: ... 1433`

Something already owns the port — very often a SQL Server installed directly on the machine,
or another project's container. Do not stop it; move the lab instead:

```powershell
# .env
DPL_SQL_PORT=14333
```

Then `./scripts/bootstrap.ps1 -StartContainers` again. Because the application reads
its port from the same variable, the container and the app cannot end up disagreeing.

Check what holds a port with `netstat -ano | findstr :1433` (Windows) or
`ss -ltnp | grep 1433` (Linux).

### A second checkout on the same machine

`compose.yaml` defaults to the project `dotnet-data-performance-lab` and the containers
`dpl-sql` / `dpl-redis`. A second clone (for a clean-checkout check, for example) that runs
`bootstrap.ps1 -StartContainers` with those defaults attaches to the **first** checkout's
containers and volumes — it would migrate and seed the database you are already using. Give the
second checkout its own names and ports in its `.env`:

```powershell
# .env of the second checkout
DPL_SQL_PORT=14334
DPL_REDIS_PORT=16380
DPL_COMPOSE_PROJECT=dotnet-data-performance-lab-clone   # separate volumes
DPL_CONTAINER_PREFIX=dpl-clone                         # dpl-clone-sql / dpl-clone-redis
```

The scripts and the manifest collector read `DPL_CONTAINER_PREFIX`, so they inspect the
second checkout's containers, not the first's. Remove them with `docker compose down -v` from
that checkout.

### `SQL password is not available. Set the 'MSSQL_SA_PASSWORD' environment variable`

The shell has not been bootstrapped. Run `./scripts/bootstrap.ps1` **in this shell**. Process
environment does not survive a new terminal — and it does not survive `pwsh -File` either, which
sets it in a child process that then exits.

### `Local lab requires Development or Testing (was 'Production')`

`DOTNET_ENVIRONMENT` / `ASPNETCORE_ENVIRONMENT` is unset or wrong. `bootstrap.ps1` sets both.

### `Database already seeded with profile 'x'`

Deliberate. Re-seeding destroys data, so it needs an explicit target:

```powershell
dotnet run --project src/Lab.Cli -- seed --profile default --database DataPerformanceLab --reset
```

### `Another importer holds the import lock` (exit 3)

Another import is running, or one died holding the lock. Nothing was changed. Wait for it, or
inspect the job and resume it with `resume --job-id <guid>`.

### An experiment reports a preflight repair

A previous query experiment crashed between dropping and restoring the candidate index. The
next run detects the marker it left in `LabMetadata`, says so, and repairs the index state
before measuring. That message is the system working, not a failure.

### The cache comparison exits 2

The runs completed but the comparison is not evidence — `comparison.md` names the reason
(dropped iterations, failed checks, a missing artifact, unbalanced arms). Do not quote the
numbers. Re-run on an otherwise idle machine.

### `db migrate --reset` refuses with "refusing to reset"

The database exists but does not carry the lab marker (`LabIdentity` in `LabMetadata`), so it is
not the lab's to drop. Nothing was changed. The allowed-name list is only a filter; ownership is
what authorizes a drop. Either point `--database` at the right name, or rename the database that
is in the way. An *empty* database of an allowed name is adopted — that is the half-finished state
a previous failed create leaves behind.

### `cold-cache` exits 2

The reads ran but did not show miss-then-hit. Usual causes: the API was started with a different
`Cache:InstanceId` than the CLI is configured with, so the two are writing different key
namespaces; or something else is serving the same SKU and refilled the entry between the eviction
and the first read. `cold-cache.json` has the per-read counter deltas.

Note what "cold" means here: only the Redis entry. SQL Server's buffer pool and plan cache, and
the .NET JIT, are all still warm — this is not a cold-database measurement.

### `import-experiment.ps1` fails to arm the fault injector

It needs `DOTNET_ENVIRONMENT=Testing`; the script sets that for itself. Running the underlying
`--fail-before-commit-on-batch` flag by hand in `Development` is refused by design, so a normal
run can never be made to crash on purpose.

### Integration tests are slow on the first run

Testcontainers pulls SQL Server and Redis images once. Subsequent runs reuse them.

## Teardown

```powershell
docker compose down          # stop containers, keep the data volumes
docker compose down -v       # also delete the seeded databases
```

Local results under `results/local/` and generated data under `data/generated/` are
git-ignored and safe to delete.

## What is committed and what is not

| Committed | Not committed |
|---|---|
| `.env.example`, `compose.yaml`, `global.json`, lock files | `.env` |
| migrations, sample CSV fixtures | `data/generated/` |
| published results with their manifests | `results/local/` |
| every script and k6 script | build output, `*.log` |

CI enforces this: a hygiene job fails if `.env` stops being ignored, if a tracked file grows a
local absolute user path, or if connection material appears outside the one allowlisted EF
design-time placeholder.
