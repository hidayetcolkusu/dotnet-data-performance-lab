# ADR 002: Insert-only import with a resumable, atomic checkpoint

**Status:** accepted · **Date:** 2026-09-11

## Context

The import takes a CSV of catalog rows and loads it into a table with a unique `Sku`. Two
questions had to be answered before any code: what happens when a SKU already exists, and what
happens when the process dies halfway through a 100,000-row file.

## Decision

### Insert-only, not upsert

An existing SKU is **skipped and counted**, never overwritten. Every record ends as exactly one
of `Inserted`, `SkippedExisting` or `Rejected`, and those three always sum to `TotalRecords`.

### Row rejection does not abort the file

A malformed row is rejected with a specific error code stored against the record, and the
import continues. The CLI exits `2` — distinct from `0` (clean) and from `1` (infrastructure or
parse error) — so a caller can tell "loaded, with rejections" from "did not load".

A *file*-level problem (bad header, unreadable encoding) rejects the whole file before any job
row is created, so there is nothing to resume.

### Staging and the checkpoint are atomic

The job row and all of its record rows are written in **one transaction**. The checkpoint
(`LastProcessedRecord`) and the products of that batch are committed in **the same
transaction** as each other.

### Resume is keyed on file hash + parser version

Re-running the same file finds the existing job rather than creating a second one. A changed
parser version means the same bytes may parse differently, so it is part of the identity.

## Consequences

- The import cannot be used to update prices. That is a deliberate, separate operation with its
  own optimistic-concurrency endpoint.
- Re-running a completed import is cheap and safe: every row is skipped.
- A crash at any point leaves the database consistent. The next `resume` continues from the
  last committed record, and no row is inserted twice.
- Counters are meaningful evidence rather than estimates, because they are written in the same
  transaction as the work they count.

## Alternatives considered

**Upsert (`MERGE` or check-then-update).** Rejected. An import that silently overwrites is
destructive in exactly the case a user is least prepared for — re-running a file "just to be
sure". Making the destructive path explicit and separate is worth the lost convenience. `MERGE`
additionally carries well-documented correctness caveats under concurrency.

**Abort the whole file on the first bad row.** Rejected. On a 100,000-row file this turns one
typo into "nothing loaded and no useful diagnosis". The row-level report names every rejected
record and why.

**Checkpoint written outside the batch transaction.** Rejected, and this is the important one:
if the checkpoint commits separately from the rows it describes, a crash between the two leaves
a checkpoint that claims work that was rolled back, and resume skips rows that were never
inserted. The integration suite proves this by injecting a fault between the two points.

**Truncate-and-reload.** Rejected: it destroys rows the file does not mention, and makes resume
meaningless.

## A bug this ADR's reasoning caught

An early version rolled back the transaction on *any* exception in the batch path — including
an exception raised after the commit had already succeeded (the "commit response was lost"
case). Rolling back a committed transaction throws, masking the real error. Rollback is now
guarded on the commit state, and a test for that specific scenario covers it.
