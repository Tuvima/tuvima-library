# 0001 — SQLite with hand-written SQL (Dapper), not Entity Framework

**Date:** 2026-03-17 (SQLite since 2026-02-27; Dapper added in `4fc05393`) · **Status:** Accepted — reaffirmed by Shy on 2026-10-17 after the [EF benchmark](../../proposals/ef-vs-dapper-benchmark-results-2026-10.md)

## What
The library catalogue is one SQLite file per installation. Code talks to it with hand-written SQL through Dapper, in `*Repository` classes in `MediaEngine.Storage`. The data structure is built by numbered upgrade steps in `Schema/Upgrades/`, applied once each at startup. Entity Framework (EF) is not used.

## Why
- **Performance:** large libraries (hundreds of thousands of files, claims and artwork rows) depend on tuned queries; `LargeLibraryQueryGuardrailTests` and the storage execution-model guardrails lock that in.
- **Reliability:** explicit SQL keeps locking, WAL settings, short-lived connections and transactions visible and testable.
- **Maintenance:** no hidden query generation or change-tracking surprises in long-running background jobs.

## Trade-offs accepted
More SQL to write by hand; no generated migrations (pre-release resets are acceptable instead); less compile-time checking of queries (covered by real-SQLite tests).

## Revisited 2026-10-17: stay on Dapper
Shy asked whether moving to EF Core would make contributing easier, since Tuvima is open source, and whether speed matters much for an app that runs locally. We measured both on the same SQLite build and settings before deciding ([results](../../proposals/ef-vs-dapper-benchmark-results-2026-10.md), harness in `benchmarks/`).

**What the measurements showed:**
- **Speed is a tie.**
  - On simple batched reads, EF was 0.5–1.7 ms faster per call.
  - On the heavy library/Home/feed reads, SQLite's own work dominates. The differences there were mixed and within noise.
- **EF uses more memory.** Reads allocated 8–45% more. With change tracking, writes allocated about 2.9× more.
- **Locking was the same.** Neither stack produced locking errors under a paced background writer.
- **A move would not remove SQL.** The heaviest reads cannot practically be written in LINQ (window functions, recursive and materialised CTEs, FTS5, many correlated sub-selects). About 40% of the ~1,900 Dapper call sites would stay hand-written SQL inside EF, so we would end up with two data-access styles.
- **EF carries a silent-error risk for our ID format.** It misreads 16-byte GUID BLOBs unless every property and ad-hoc result type has an explicit converter.

**Decision (Shy, 2026-10-17):** stay on Dapper. None of the revisit triggers below has fired, and users would see no benefit from a move. The contributor-friendliness concern is addressed differently:
- numbered upgrade scripts for data structure changes, replacing the growing idempotent `SchemaMigrator`;
- a contributor guide for data changes.

Both are specified in [Spec: Numbered data store upgrades and contributor guide](../../specs/archive/data-store-upgrade-scripts.md).

## Revisit if
- Pre-release ends and real users need versioned, reversible migrations that numbered upgrade scripts cannot provide.
- Query-writing becomes the main source of bugs.
- A second database engine must be supported.

Switching would touch every repository (106 classes, ~1,900 Dapper call sites), the ~3,500-line `schema.sql`, the data store upgrade code and about ten guardrail suites. Re-run the benchmark harness in `benchmarks/` before reopening this.
