# 0001 — SQLite with hand-written SQL (Dapper), not Entity Framework

**Date:** 2026-03-17 (SQLite since 2026-02-27; Dapper added in `4fc05393`) · **Status:** Accepted — Shy asked to revisit this after 2026-10-17 (follow-up session)

## What
The library catalogue is one SQLite file per installation. Code talks to it with hand-written SQL through Dapper, in `*Repository` classes in `MediaEngine.Storage`. The data structure is defined in `schema.sql` and upgraded at startup by `SchemaMigrator`. Entity Framework (EF) is not used.

## Why
- **Performance:** large libraries (hundreds of thousands of files, claims and artwork rows) depend on tuned queries; `LargeLibraryQueryGuardrailTests` and the storage execution-model guardrails lock that in.
- **Reliability:** explicit SQL keeps locking, WAL settings, short-lived connections and transactions visible and testable.
- **Maintenance:** no hidden query generation or change-tracking surprises in long-running background jobs.

## Trade-offs accepted
More SQL to write by hand; no generated migrations (pre-release resets are acceptable instead); less compile-time checking of queries (covered by real-SQLite tests).

## Revisit if
- Pre-release ends and real users need versioned, reversible migrations.
- Query-writing becomes the main source of bugs.
- A second database engine must be supported.

Switching would touch every repository, the ~3,500-line `schema.sql` and the ~2,400-line `SchemaMigrator`; plan it as its own spec with a performance comparison first.
