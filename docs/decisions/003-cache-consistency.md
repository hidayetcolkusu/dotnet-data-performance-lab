# ADR 003: Cache-aside with a bounded stale window, not consistency

**Status:** accepted · **Date:** 2026-09-11

## Context

Product detail reads go through Redis. Any cache introduces a window in which a reader can see
data that is no longer true. The decision is not *whether* to accept staleness — it is how
large the window is, and being explicit that it exists.

## Decision

**Cache-aside.** Read the cache; on a miss read SQL and fill with an absolute TTL. The cache is
never the system of record and is never written to except from a read that just consulted SQL.

**Absolute 60-second TTL, no sliding expiration.** A sliding window would let a popular key stay
stale indefinitely, which is the opposite of a bound.

**Write path invalidates after committing.** A price update commits to SQL under optimistic
concurrency, then deletes the key. A failed invalidation is *counted*, not swallowed silently —
and the TTL still bounds it.

**Misses are not cached.** A product that does not exist is not stored as a negative entry.
Negative caching would turn one 404 into a TTL-long window where a freshly imported product
still reads as missing.

**Redis being down degrades, it does not fail.** An unreachable cache falls back to SQL, is
counted as a read failure, and `/health/ready` reports `cache: degraded` while the API stays
ready — it can still serve every request from SQL.

**The cache holds product detail only.** The keyset-paginated list is never cached: its cache
key would be the whole query shape, and invalidating it correctly on any product change is a
much larger problem than this lab is solving.

## Consequences — stated plainly

- **A reader can observe a stale price for up to 60 seconds**, and for a shorter window even
  after a successful write, because of an unavoidable read-modify race: a reader that missed
  the cache may fill it with the value it read from SQL *just before* a writer committed. The
  integration suite reproduces exactly this race with a test seam rather than pretending it
  away.
- **The cache does not make reads consistent, and does not reduce write contention.**
- **Whether it reduces read latency at all is a measured question, not a given** — and in
  this lab the answer came out **against the cache**. The mechanism it offers is avoiding a
  SQL round-trip on a hit; what it charges is a Redis round-trip on *every* lookup plus a
  serialize/deserialize. Measured off/on over five alternating repetitions, enabling the
  cache made p95 about **1.7× worse** (2.48 ms → 4.15 ms). See
  [the published comparison](../../results/published/README.md). This does not generalize:
  a more expensive origin read or a hotter key subset would plausibly reverse it.
- **The hit rate that explains it was initially measured over the wrong window.** The `0.286`
  recorded in the `20260911` run came from one read of the process-wide counters after the
  measured run, and those counters also include the warmup's lookups against the same process.
  It is the rate of *warmup plus measurement*, not of the window the latency came from. The
  orchestrator now bounds the measured window with a snapshot on each side and records the
  difference. Re-measured that way the rate is **0.167** — roughly 83% of measured lookups
  miss — and it reconciles exactly against the measured GET count, which the cumulative read
  never could. With ~1.2 requests per SKU inside a 60 s window there is little reuse to exploit,
  so the mechanism now has a measured number behind it rather than a plausible story. The re-run
  is published at `results/published/20260913/q3-cache/` with the dirty-tree diff beside it. It
  also reproduced the latency result independently — cache slower in all five repetitions on both
  median and p95, with the arms' p95 ranges not overlapping — so the two runs agree on the
  direction while disagreeing on absolute milliseconds, as two different machine states should.
  A third run on 2026-10-04, from a clean commit and after the Redis reconnect fix, repeated
  the same direction: 0.167–0.177 hit rate, cache slower in every repetition, though this time
  the arms' p95 ranges overlapped across repetitions.
  See [the published results](../../results/published/README.md).
- Anything needing a guaranteed-current value must not go through this path.
- Key layout carries an instance id and a payload version (`dpl:<instance>:product:v1`), so two
  databases can share one Redis and a DTO change can retire old entries.

## Alternatives considered

**Write-through.** Rejected: it makes every write depend on cache availability, converting a
cache outage into a write outage, in exchange for a stale window that the read-modify race
reopens anyway.

**Read-through with a library.** Rejected for a lab whose point is to *show* the read path. An
explicit cache-aside reader is the artifact being explained.

**Locking or single-flight on miss.** Not implemented. It would narrow the race, not close it,
and the measured workload does not exhibit the stampede it addresses. Recording that this is a
known, deliberate gap is more useful than a partial mitigation presented as a solution.

**Longer TTL for a better hit rate.** Rejected: the TTL is a correctness bound first. The
measured run is deliberately as long as the TTL, so entries genuinely expire and are re-read
during measurement — hiding TTL renewal would overstate the cache.

## What is explicitly not claimed

This cache does **not** give strong consistency, read-your-writes, or monotonic reads. Any
sentence to that effect anywhere in this repository is a bug in the documentation.

See [ADR 001](001-measurement-boundary.md) for what the off/on measurement may claim.
