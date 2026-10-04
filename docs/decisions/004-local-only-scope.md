# ADR 004: Local-only scope, and what SQL Server + Redis costs

**Status:** accepted · **Date:** 2026-09-11

## Context

This is a local laboratory. The temptation in a project like this is to make it look deployable —
add a cloud target, a Dockerfile for the API, a CI deploy job — and quietly imply an operational
maturity that was never demonstrated.

## Decision

**Everything runs on one machine.** SQL Server 2022 and Redis 7.4 run as containers published
on `127.0.0.1` only. The API and CLI run on the host.

**The application refuses to start outside `Development` or `Testing`.** Not a config default —
a guard that throws, covered by a test.

**Synthetic data only,** generated deterministically from a fixed seed and a versioned
algorithm. No real product, customer or vendor data exists anywhere in the repository.

**The API never migrates or seeds on startup.** Schema changes are an explicit CLI action, so
starting the API can never alter a database.

**Experiment DDL is confined to `DataPerformanceLab_Experiment`.** The database the API serves
never receives it; the tooling refuses the API database by name, with a test covering the
refusal.

**No deployment surface.** No Dockerfile for the API, no cloud manifests, no deploy workflow.

## Consequences — the honest cost of two data stores

Running SQL Server *and* Redis for one lab is not free, and the project should say so:

- **~2 GB of RAM** before a single row is seeded, and a slow first run while images pull.
- **Two failure modes instead of one.** Redis being unreachable is a state the code has to
  handle — hence the degraded-but-ready health contract and the counted read failures.
- **A consistency problem that did not previously exist** ([ADR 003](003-cache-consistency.md)).
  A single-store version of this API would be strictly more correct.
- **Measurement noise.** Containers share CPU with the load generator and the API, which is a
  standing limitation of every number this lab publishes.

The cache earns its place here because the project is *about* showing a measured cache
trade-off. In a real service serving this traffic, a covering index alone — Q1 measured roughly
a 35× improvement on the list query — would be the first and possibly the only change worth
making.

## Consequences — what this project therefore cannot show

- No evidence about production operability, deployment, rollback, or on-call behaviour.
- No multi-node, replication, failover or network-partition behaviour.
- No authentication, authorization, rate limiting or tenancy.
- No capacity figure. Loopback HTTP on a developer laptop is not a throughput result.

## Alternatives considered

**Add a Dockerfile and a cloud deploy to look production-ready.** Rejected. It would be the
single most misleading change available: a deploy pipeline that has never served a user
implies operational experience the artifacts do not support.

**Drop Redis and keep only SQL Server.** Genuinely tempting, and strictly simpler. Rejected
because the cache trade-off — including its stale window and its measured benefit — is one of
the things worth demonstrating. The cost is documented above rather than omitted.

**Use in-memory fakes instead of containers in tests.** Rejected: a green suite that never
touched a real database proves nothing about SQL behaviour, migrations, or concurrency. The
integration tests fail rather than skip when Docker is unavailable, for the same reason.

**Real product data.** Never considered seriously; it would be a licensing and privacy problem
in exchange for nothing measurable.
