# Dapper vs Entity Framework Core: benchmark results

> **Status:** Current · **Last updated:** 2026-10-17 · **Related docs:** [Handoff](../handoffs/2026-10-17-ef-vs-dapper-benchmark.md) · [ADR 0001 — SQLite and Dapper](../architecture/decisions/0001-sqlite-and-dapper.md) · [Benchmark harness README](../../benchmarks/README.md)

This report answers the handoff's question: how much faster or slower is EF Core than Tuvima's current Dapper code on its heaviest reads, and how much of the code would a move touch? This was a measurement only; nothing that ships changed. **Outcome:** on 2026-10-17 Shy chose to stay on Dapper, and ADR 0001 is reaffirmed.

## Plain English summary

On speed, the two tools tie. For quick lookups, EF was about as fast or slightly faster, by one or two thousandths of a second. For the big lookups, both spend almost all their time inside SQLite running the same SQL, so the results were within measurement noise. EF used **8–45% more memory** on reads and about **3× more** when saving data. Neither tool caused the data store to lock up under a heavy background write load.

Switching would not remove hand-written SQL either. The heaviest lookups can't be written in EF's own style, so about 40% of the roughly 1,900 query points would stay hand-written SQL inside EF. The other 60% would be rewritten, along with every data-access module and about ten guardrail test suites. Users would see no difference.

**Recommendation: stay on Dapper.** Add a small numbered-upgrade-script system for data structure changes, plus a short contributor guide for data changes. The reasons in terms of your goals:

- **Performance:** no gain, and more memory use on home servers.
- **Maintenance:** a very large rewrite with no user benefit, and SQL would still be needed.
- **Extensibility:** EF is better known, but the guide and numbered scripts give contributors most of what EF migrations would, at a fraction of the cost.
- **Reliability:** during the port, EF silently read IDs wrong in one case.
- **Privacy:** no difference.

**Separate finding, unrelated to the EF choice.** Two of the five lookups are already very slow at 4,000 works: the library list takes **about 14 seconds** with no filter, and the Home page lookup takes **about 6 seconds** when it isn't cached. Both tools have this problem, because the cause is the SQL itself. It is unconfirmed whether real libraries hit this or whether it comes from the pretend data. A separate investigation session has been proposed (see "Follow-up").

## How it was measured

- **Harness:** `benchmarks/MediaEngine.Storage.Benchmarks/`. It sits outside `MediaEngine.slnx` and ships nothing; see its README for commands, licences and design.
  - **Tools used** (benchmark only): BenchmarkDotNet 0.15.8 and Microsoft.EntityFrameworkCore.Sqlite.Core 10.0.3, both MIT and AGPLv3-compatible.
  - **Same SQLite build:** both stacks run on the product's own `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3, confirmed in the resolved package graph.
  - **Same settings:** EF gets its connection from `IDatabaseConnection.CreateConnection()`, so it has the production pragmas: WAL, `synchronous=NORMAL`, `busy_timeout=5000`, memory temp store, 16 MiB cache, 256 MiB mmap.
- **Variants per query:**
  - **Dapper:** the real production class, which is the baseline.
  - **EF raw SQL:** the identical SQL text through EF. This is the "mix" option.
  - **EF LINQ:** idiomatic EF, `AsNoTracking`, pooled context; only where LINQ can express the query.
  - Before timing, a `verify` step checked that every variant returns the same rows in the same order with every field equal to production. It passed at every size used.
- **Fairness review:** an independent review checked the harness before the full run and found no must-fix issues. Its suggestions were applied first:
  - EF reads are synchronous where production's are.
  - The concurrency writer runs at a fixed pace.
  - The remaining differences are written down.
- **Machine:** AMD Ryzen 7 9800X3D (8 cores / 16 threads), 31 GiB RAM, NVMe SSD, Windows 11 Pro 26200, .NET 10.0.12 (SDK 10.0.401). Shy's desktop, 2026-10-17.
- **Data (synthetic):**

  | Size | Works | Editions / files | Canonical values | Claims | Artwork | Persons | Data store file |
  |---|---|---|---|---|---|---|---|
  | `full` | 102,500 (100,000 + 2,500 parents) | 100,000 / 100,000 | 1,025,417 | 500,000 | 50,000 | 20,000 | 511 MiB |
  | `projection` | 4,100 | 4,000 / 4,000 | 41,017 | 20,000 | 2% scale | 2% scale | 23 MiB |

  Shy agreed the size split (2026-10-17): **Q1–Q3 ran at 100,000 works**, while **Q4, Q5 and the concurrency test ran at 4,000 works**. At 100,000 works, one Q5 call took 352 s and one Q4 call hadn't finished after 15 minutes, so the full benchmark would have taken many hours. Because both stacks run the same SQL for Q4 and Q5, the size doesn't change the Dapper-vs-EF comparison.

## The five queries and why they were chosen

They cover the query mix found in the code: mostly simple single-table reads, then join/aggregate queries, then SQLite-specific ones.

| # | Production code | Shape | Why |
|---|---|---|---|
| Q1 | `CanonicalValueRepository.GetByEntitiesAsync`, 500 ids | Single table, batched `IN` | The most common query shape in the code |
| Q2 | `MetadataClaimRepository.GetByEntitiesAsync`, 500 ids over 500k claims | Single table, `is_current`, ordered | High-volume claims read (enrichment, detail) |
| Q3 | `LibraryWorkFeedReadService.GetWorksAsync`, first page and offset 50,000 | Window functions + paging + batched follow-up loads | Moderate; partly expressible in LINQ |
| Q4 | `LibraryItemRepository.GetPageAsync`, 50 rows, no filter / media-type filter | ~700-line CTE chain, `ROW_NUMBER`, ~20 `MAX(CASE)` aggregates, COUNT + page | The heaviest read (library list) |
| Q5 | `DisplayWorkProjectionReader.LoadAsync`, Home limit 1000, cache bypassed | `MATERIALIZED` CTEs, ~40 correlated sub-selects per row, `json_extract` | The Home page projection |

**Where LINQ was not practical:**
- **Q3:** the paging query needs `MIN() OVER` / `ROW_NUMBER() OVER (PARTITION BY …)`, which EF Core cannot translate. Its LINQ variant covers only the follow-up loads.
- **Q4 and Q5:** most of the query would need raw SQL fragments.

This is part of the result: **for the queries that matter most for speed, an EF port keeps hand-written SQL.**

## Results: single-query speed and memory

Median and P95 are per call. "Δ" is EF's median against Dapper's: negative means EF was faster. Allocated memory is per call.

| Query | Variant | Median | P95 | Δ median | Allocated | Δ allocated |
|---|---|---|---|---|---|---|
| Q1 (100k) | Dapper | 7.21 ms | 8.48 ms | — | 1.93 MB | — |
| | EF raw SQL | 6.62 ms | 7.23 ms | −8% | 2.09 MB | +8% |
| | EF LINQ, pooled context | 6.94 ms | 8.11 ms | −4% | 2.38 MB | +23% |
| | EF LINQ, new context per call | 7.29 ms | 7.90 ms | +1% | 2.41 MB | +25% |
| Q2 (100k) | Dapper | 6.40 ms | 6.89 ms | — | 1.30 MB | — |
| | EF raw SQL | 4.68 ms | 5.19 ms | −27% | 1.26 MB | −3% |
| | EF LINQ | 5.23 ms | 5.68 ms | −18% | 1.54 MB | +18% |
| Q3 first page (100k) | Dapper | 2.87 s | 3.05 s | — | 665 KB | — |
| | EF raw SQL | 2.95 s | 3.19 s | +3% | 808 KB | +21% |
| | EF LINQ (follow-ups only) | 2.77 s | 2.96 s | −4% | 955 KB | +44% |
| Q3 deep page (100k) | Dapper | 2.99 s | 3.11 s | — | 713 KB | — |
| | EF raw SQL | 3.64 s | 4.05 s | +22% | 867 KB | +22% |
| | EF LINQ (follow-ups only) | 3.05 s | 3.21 s | +2% | 1,020 KB | +43% |
| Q4 media-type filter (4k) | Dapper | 1.95 s | 2.04 s | — | 4.15 MB | — |
| | EF raw SQL | 2.12 s | 2.21 s | +8% | 5.31 MB | +28% |
| Q4 no filter (4k) | Dapper | 14.40 s | 17.24 s | — | 4.15 MB | — |
| | EF raw SQL | 12.51 s | 12.91 s | −13% | 5.30 MB | +28% |
| Q5 Home (4k) | Dapper | 6.43 s | 6.61 s | — | 7.70 MB | — |
| | EF raw SQL | 6.53 s | 6.84 s | +2% | 8.61 MB | +12% |

**What this means:**
- **Quick lookups (Q1, Q2):** EF was as fast or faster, by 0.5–1.7 ms per call.
  - A plausible but **unmeasured** cause: Dapper's custom GUID/date type handlers go through a slower per-value path than EF's compiled converters.
  - Creating a fresh EF context per call costs about 0.35 ms, which pooling removes.
  - At these sizes, neither difference is something a person would notice.
- **Heavy lookups (Q3–Q5):** SQLite's own work dominates. The differences of −13% to +22% don't follow a consistent direction, and they are within run-to-run noise. Dapper's Q4 "no filter" had a 2.0 s standard deviation. The Q3 deep-page EF raw result (+22%) runs the identical SQL as Dapper and is not explained; treat it as noise unless a re-run repeats it.
- **Memory:** EF allocated more in 10 of 11 comparisons, typically +8% to +45%.

## Results: background writes during reads (locking)

Setup:
- Four reader tasks looped Q2 and Q4 for 60 s on the 4,000-work data store.
- A writer committed 50 claim inserts plus 20 canonical-value updates per transaction, at a fixed 50 transactions/s.
- **Scenario A:** both stacks share the same Dapper writer. **Scenario B:** the writer uses the stack under test.
- Every run used a fresh copy of the file in its own process.

| Scenario | Stack | Writer (tx/s) | Q2 reads p50 / p95 | Q4 reads p50 / p95 | Busy/locked errors | Peak memory | Allocated |
|---|---|---|---|---|---|---|---|
| A | Dapper | 3,000 (50.0) | 46.9 / 101.9 ms | 3,753 / 4,797 ms | 0 | 393 MiB | 1,069 MiB |
| A | EF | 2,999 (50.0) | 42.4 / 89.0 ms | 2,313 / 3,380 ms | 0 | 443 MiB | 1,540 MiB |
| B | Dapper | 3,000 (50.0) | 47.6 / 86.5 ms | 2,358 / 2,574 ms | 0 | 421 MiB | 1,375 MiB |
| B | EF | 2,999 (50.0) | 40.2 / 115.2 ms | 2,455 / 3,708 ms | 0 | 446 MiB | 3,958 MiB |

**What this means:**
- **No locking errors on either stack.** Both writers hit the target rate, and both go through the same single write lock.
- **The latency differences are noise.** For Dapper, A and B are the same setup, yet its Q4 median moved from 3,753 ms to 2,358 ms between them. The EF numbers fall inside that range.
- **Memory is the clear difference.** EF used about 12% more peak memory, and its writer allocated **about 2.9× more** (3,958 vs 1,375 MiB). That comes from EF's change tracking.
  - The EF writer used EF's classic load-modify-save pattern, which reads the rows before updating them (Dapper updates blindly).
  - A tuned EF writer (`ExecuteUpdate`) would close part of that gap.
- With only 68–104 reads per run, p99 is effectively the slowest read, so it isn't reported here; it is in the CSV.

## How much code a move would touch

Counted across `src/` with grep heuristics; the method is described in the harness work notes.

| Item | Count |
|---|---|
| Dapper call sites | ~1,938 in 219 files (1,429 in 121 Storage files) |
| — simple single-table reads/writes (would port to LINQ) | ~1,159 (~60%) |
| — multi-join / aggregate | ~505 (~26%) |
| — SQLite-specific (FTS5 `MATCH`, `ON CONFLICT`, `WITH RECURSIVE`, window functions, `json_*`, `PRAGMA`, `INSERT OR IGNORE`) | ~274 (~14%) |
| Raw ADO.NET command uses (outside Dapper) | 166 in 28 files |
| `*Repository` classes | 106 |
| Data structure file (`schema.sql`) | 3,519 lines: 182 tables, 245 indexes, 14 triggers, 3 FTS5 tables |
| `SchemaMigrator` | 2,446 lines, ~93 `ALTER TABLE … ADD COLUMN`, plus data migrations |

Most of the join/aggregate and SQLite-specific ~40% would stay hand-written SQL under EF (`SqlQuery`/`FromSql`). An EF move therefore gives **two** data-access styles, not one.

**Differences found while porting** (each affects the risk of a move):
- EF **silently** reads a 16-byte GUID BLOB in its own byte order unless a converter is attached to that exact property. A convention-level converter does not reach ad-hoc `SqlQuery<T>` result types. The first attempt returned plausible-looking wrong ids, and only the field-by-field `verify` caught it.
- Dapper leaves `0` when a NULL column maps to a non-nullable `int`, whereas EF throws. EF is stricter here, which is arguably safer, but every row type would need checking.
- Every internal GUID, date and enum property needs an explicit converter that matches today's storage formats.

**Guardrail suites that assume today's design** (each would need rewriting or replacing):
- `tests/MediaEngine.Api.Tests/DatabaseConnectionGuardrailTests.cs`: `Open()` only in startup/schema; no raw `BeginTransaction` outside `DatabaseConnection`.
- `tests/MediaEngine.Storage.Tests/StorageExecutionModelGuardrailTests.cs`: no misleading async SQLite IO; writes go through the synchronous callback transaction template.
- `tests/MediaEngine.Storage.Tests/StorageGuardrailTests.cs`: GUIDs are never stringified as parameters.
- `tests/MediaEngine.Performance.Tests/LargeLibraryQueryGuardrailTests.cs`: query plans name specific covering indexes; `COLLATE NOCASE` path indexes; deep-page response time.
- Source-text checks for `HomeVisibilitySql.VisibleWorkPredicate(...)`: `CollectionEndpointRouteTests.cs`, `DisplayContractTests.cs`.
- Also reference SQL/schema text, not reviewed in depth: `AccountCreationGuardrailTests`, `ClientApiV1GuardrailTests`, `RetiredLocalOnlyAccountsGuardrailTests`, `ViewDocumentsGuardrailTests`.

## Data structure upgrade options

| Option | How it works | What it would take | Trade-offs |
|---|---|---|---|
| **Today: idempotent `SchemaMigrator`** | `schema.sql` runs whole at startup (`CREATE … IF NOT EXISTS`), then a series of `Ensure*` checks add missing columns and tables. Pre-release resets via the storage epoch guard. | Nothing | No version number and no ordered history. Contributors must write their own "does this already exist?" checks, and the class keeps growing. |
| **EF Core migrations** | An EF model of every table. `dotnet ef` generates up/down migrations from model changes and records them in a history table. | An EF model for all 182 tables, kept in sync even where code uses raw SQL. Hand-written `migrationBuilder.Sql` for FTS5 tables and triggers, which EF doesn't model. One baseline migration (a pre-release reset makes that acceptable). | EF's SQLite provider handles many `ALTER`s by **rebuilding the table**, which is slow on a large library and delicate around triggers and FTS. It only really pays off if the code moves to EF. |
| **Numbered upgrade scripts** (planning-side recommendation) | `PRAGMA user_version` (or a version table) plus ordered `0001_baseline.sql`, `0002_….sql` files. Each is applied once in a transaction at startup. | A small runner (estimated a few hundred lines including tests). The baseline is today's `schema.sql`. Existing `Ensure*` steps fold into the baseline at the next pre-release reset. Works with Dapper unchanged. | Down scripts are optional, written by hand if wanted. It gives contributors one clear rule: "add the next numbered file". |

## Known limits of this test

- **Synthetic data.** Real libraries have other distributions: claims per item, metadata per item, the share of items in review. Absolute times will differ, and the Q4/Q5 slowness might be partly a data-shape effect.
- **One machine.** A fast desktop CPU and NVMe disk. A NAS or low-power server will be slower in absolute terms, and EF's extra memory matters more there.
- **The machine was not idle.** Another session's test run (several `dotnet` processes) was active during the run, which adds noise. The Engine and Dashboard were not running.
- **Q4/Q5 ran at 4,000 works**, not 100,000 (agreed; see above).
- **Coarse memory counters for heavy queries.** The heavy-query jobs used one call per iteration (15 iterations, 3 warm-ups), so the garbage-collection columns for Q3–Q5 are too coarse to compare.
- **SQL text caching differs slightly.** Production rebuilds its SQL text on every call while the EF variants cache it. That costs microseconds against seconds per call.
- **The EF model covers only the tables these queries touch.** The code-change estimate comes from counting, not from a port.
- **The concurrency EF writer uses load-modify-save**, not EF's faster bulk `ExecuteUpdate` (disclosed above).

## Follow-up

- **Decided (Shy, 2026-10-17): stay on Dapper.** Recorded in [ADR 0001](../architecture/decisions/0001-sqlite-and-dapper.md). The numbered upgrade scripts and the contributor guide are specified (Locked 2026-10-18) in [Spec: Numbered data store upgrades and contributor guide](../specs/data-store-upgrade-scripts.md).
- **Proposed separately, independent of this decision:** investigate why the library list (Q4) and Home projection (Q5) take seconds at 4,000 works and minutes at 100,000, including whether real libraries are affected. This is diagnosis only, with no changes. It can use this harness: `seed --works N`, `explain --query q4`.
