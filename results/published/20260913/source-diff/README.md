# Source diff for the 2026-09-13 runs

Every measurement in `../` was taken from a **dirty working tree** on top of commit
`1268385f904e9e6f600bd3180e592ec3c1276421`. Each `manifest.json` says so — `git.isDirty` is
`true` — and this directory is the other half of that statement.

The lab's measurement rules ([CONTRIBUTING.md](../../../../CONTRIBUTING.md#measurement-rules))
allow a measurement from a dirty tree on one condition: the diff is kept alongside the
numbers, so a reader can see exactly what the measured binaries were built from. That is what
these files are.

| File | What it is |
|---|---|
| `working-tree.diff` | `git diff` of every tracked file that differs from `1268385`, excluding `results/` |
| `working-tree-status.txt` | `git status --porcelain`, so added files are visible as well as modified ones |
| `untracked-files.txt` | the full contents of the four new source files, which a diff against HEAD cannot show |

## Why the tree was dirty

These runs exist *because* of the changes in this diff. The measurement they produce could not
have been taken from `1268385`:

- the cache hit rate is bounded by two counter snapshots around the measured run — before this
  diff, the orchestrator read the cumulative counters once and the recorded rate silently
  included the warmup;
- the import scenario package, the cold-cache experiment and the manifest's storage records did
  not exist at `1268385` at all.

So "re-measure from a clean commit first" is circular here. The honest order is: measure, keep
the diff, then commit and re-measure to publish without this caveat.

## What this means for citing the numbers

Treat `../q3-cache/` as a **correction** of the `20260911` run's hit-rate interpretation, not as
its replacement. The `20260911` artifacts are untouched and remain the published latency result.

Before quoting any figure here as settled, commit this diff and re-run:

```powershell
./scripts/compare-cache.ps1 -Profile default -Output results/local/<run-id>
./scripts/import-experiment.ps1 -Output results/local/import-<run-id>
```

A clean-tree run writes `"isDirty": false`, and this directory becomes unnecessary.

## What happened to this diff (checked 2026-10-04)

This diff was committed in the pre-release history as `443ebd3`. It was checked by rebuilding the
measured tree (`1268385` + this diff + the files in `untracked-files.txt`) and comparing it with
that commit: every file under `src/`, `tests/`, `scripts/`, `.github/`, the docs and the root
config was identical. The only extra path in `working-tree-status.txt` is an untracked planning
note, which no experiment compiled or ran.

`untracked-files.txt` separates files with a blank line, so a naive split adds one trailing
newline to each file. Strip it before comparing.

Neither `1268385` nor `443ebd3` is in the public history, which starts at the `v1.0.0-lab`
release commit (see
[Commit SHAs in the manifests](../../README.md#commit-shas-in-the-manifests)). The manifests
still say `isDirty: true` with `commitSha` `1268385`. That remains the literal truth of the run
and is not rewritten.

The cache comparison was later re-measured from a clean commit whose source the release carries
unchanged: [`20261004/q3-cache/`](../../20261004/q3-cache/). The import and cold-cache packages
have not been re-measured; cite them together with this note.
