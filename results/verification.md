# Verification

The current verified state of this repository. Earlier runs, from before the public history
began, are in [history/](history/verification-2026-09-11-to-10-04.md). Nothing below depends on them.

## Current state

| | |
|---|---|
| Release | `v1.0.0-lab` |
| Verified source | `src/` `db329eb9bb90df77a0de7e9921a9807a2b5c278e` · `tests/` `fb5e1ce5ebfba360e887daa31aab447257937c56` · `scripts/` `153447ae0b96401bbcc371c44bac812cd0c59a74` · `experiments/` `59c107bff077a8a19a44833485fb0ec8dbce9385` (git tree hashes) |
| Unit tests | **185 passed**, 0 failed, 0 skipped |
| Integration tests | **106 passed**, 0 failed, 0 skipped, against real SQL Server and Redis containers |
| Release build | 0 warnings, 0 errors (`TreatWarningsAsErrors` is on) |
| `dotnet format --verify-no-changes` | exit 0 |
| Fresh clone of the release commit | recorded in the next commit after the release |
| CI on the release commit | recorded in the next commit after the release |

The tree hashes identify the code independently of any commit SHA. They are the same trees the
canonical Q3 measurement was taken on
([2026-10-04 package](published/README.md#commit-shas-in-the-manifests)). To check a checkout:

```powershell
git rev-parse HEAD:src HEAD:tests HEAD:scripts HEAD:experiments
```

## Environment

| | |
|---|---|
| Machine | Windows 11 Pro 10.0.26200, PowerShell 7.6.6 |
| .NET SDK | 10.0.400 (`global.json`, `rollForward: disable`), runtime 10.0.11 |
| Docker | Engine 29.6.1, Compose 5.3.0 |
| SQL Server image | `mcr.microsoft.com/mssql/server:2022-latest@sha256:97b448857967be55e005424a660056fe6d51814435804dc07e8f79f028bab5fb` |
| Redis image | `redis:7.4@sha256:71da9275c5f3fcb97d0fa0c8c5b36cc995327265420f17a04bfd544f458059f7` |
| k6 | v2.2.0 locally; CI uses the digest-pinned `grafana/k6` image and asserts the version |

`compose.yaml` and the Testcontainers fixtures request the same `tag@digest` references.
`ImagePinTests` fails if they drift apart.

## Commands

```powershell
./scripts/verify.ps1                 # locked restore, Release build, unit + integration tests
dotnet format --verify-no-changes
```

| Step | Exit | Result |
|---|---:|---|
| `./scripts/verify.ps1` (working tree with the verified source above, 2026-10-04) | 0 | 185 + 106 passed, 0 skipped; 130 s |
| `dotnet format --verify-no-changes` | 0 | — |

## Known limitations of this verification

- **`verify.ps1` and CI do not run `dotnet format`.** The format result above comes from running
  it by hand.
- **CI does not gate on performance.** It runs restore, build, both test suites and a k6
  correctness smoke on a shared runner. Latency results come only from the local runs published
  under [published/](published/README.md).
- **In-CI test counts are not claimed.** The public API exposes the run conclusion and step
  timings without authentication, but not the job logs.
- **Published artifact hashes depend on line endings.** There is no `.gitattributes` rule for
  `results/published/`, so the checked-out bytes depend on the reader's `core.autocrlf`. Some
  files were written with LF and some (the `k6.log` files) with CRLF. To check a manifest hash,
  compare with line endings normalised both ways. Fixing this would rewrite the stored bytes of
  published files.
- **The GitHub Actions used in CI target Node.js 20**, which GitHub runs on Node.js 24 with a
  deprecation warning. This has not failed a run.
