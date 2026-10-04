# ADR 001: What a measurement in this lab is allowed to claim

**Status:** accepted · **Date:** 2026-09-11

## Context

This project exists to show that a data-access decision was *measured*, not guessed. That only
means something if the measurements are hard to fool — including hard for the author to fool
himself with.

The failure mode is familiar: change three things, observe an improvement, attribute it to the
interesting one. Or run a benchmark once on a busy laptop and publish the number.

## Decision

**One variable per experiment.** Q1 changes only whether the candidate index exists. Q2 keeps
the index present on *both* sides and changes only where the DTO is built. Q3 changes only
`Cache:Enabled`, with the same binary, config, fixture and load profile on both arms.

**Every variant must produce the identical ordered result.** Q1/Q2 hash the ordered DTO list
and fail the run if the hashes differ. Q3 checks every response against the fixture, so a 200
carrying the wrong row is a failure rather than a fast response. An unequal result makes a
timing comparison meaningless.

**Alternating order.** Rounds run AB / BA / AB / BA / AB so a monotonic drift in machine state
cannot masquerade as a difference between variants.

**No pooled percentiles across runs.** A p95 of p95s is not the p95 of the requests. Across
repetitions the report publishes the median, min and max *of the per-run statistic*, named as
such, and every published figure is a value some run actually produced (nearest-rank, no
interpolation).

**Failed runs are kept.** A run that crashed, dropped iterations or failed a check is written
out with `"status": "failed"` or marked invalid, not deleted and not silently retried.

**Every run carries a manifest**: commit SHA *and whether the tree was dirty*,
environment, dataset hash, parameters and the produced files.

## Consequences

- Some experiments are less impressive than a less careful setup would make them look. Q2's
  projection difference is roughly 1.5×, not the order of magnitude a measurement that also
  quietly changed the index would have shown.
- Runs are slow. Warmup happens in a separate process, five repetitions alternate order, and
  the cache comparison restarts the API for every arm.
- A dirty working tree is reported rather than hidden, which sometimes means re-measuring.

## What these measurements may never be used to claim

- **Anything about a server.** One machine, loopback HTTP, containers sharing CPU with the
  client process and with whatever else the machine is running.
- **Anything about write cost.** The candidate index makes inserts and updates more expensive.
  That cost was not measured, so no numeric write trade-off is claimed anywhere.
- **Anything outside the recorded parameters.** A run says something about the categories, page
  sizes, rates, durations and data volume in *its* manifest, and nothing about others.
- **Capacity.** No number here is an RPS a service can sustain.

## Alternatives considered

**Averaging repetitions into a single headline number.** Rejected: it hides the spread, and the
spread is the most honest part of a local measurement. Real runs in this lab have produced a
repetition where the cache arm was *slower*; a mean would have erased that.

**Gating CI on latency.** Rejected: a shared GitHub runner makes no promise about timing. CI
checks correctness; performance is measured locally and published with a manifest.

**Dropping outlier runs.** Rejected. There is no principled outlier rule here, and "the run I
didn't like" is not one.

See also [ADR 004](004-local-only-scope.md).
