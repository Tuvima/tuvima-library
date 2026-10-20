# Dapper vs Entity Framework Core benchmark

Throwaway **measurement harness** for the decision recorded in
[`engineering/handoffs/2026-10-17-ef-vs-dapper-benchmark.md`](../engineering/handoffs/2026-10-17-ef-vs-dapper-benchmark.md)
(it revisits ADR 0001). It runs the same five production queries through the real Dapper code and through EF Core on
the same SQLite file, and reports time, allocations and lock behaviour. Nothing here ships: the project is not in
`MediaEngine.slnx`, nothing under `src/`, `tests/` or `config/` was changed, and no database or result file is committed.

## What is compared

| # | Production code | Why it was picked | Dapper | EF raw SQL | EF LINQ |
|---|---|---|---|---|---|
| Q1 | `CanonicalValueRepository.GetByEntitiesAsync`, 500 ids | The most common shape: a batched single-table read | production code | same SQL text | idiomatic LINQ (pooled context) and a labelled run with a fresh non-pooled context |
| Q2 | `MetadataClaimRepository.GetByEntitiesAsync`, 500 ids over the claims table | High-volume claims read behind enrichment and detail pages | production code | same SQL text | idiomatic LINQ |
| Q3 | `LibraryWorkFeedReadService.GetWorksAsync`, first page and a page at offset works/2 (50,000 at full scale) | Window functions, paging and three batched follow-up loads | production code | same SQL text, follow-ups as raw SQL | follow-up loads only; paging query stays raw SQL |
| Q4 | `LibraryItemRepository.GetPageAsync`, 50 rows, no filter and media-type filter | The heaviest read: nine-CTE chain, ROW_NUMBER, about twenty `MAX(CASE ...)` aggregates, COUNT plus page | production code | the product's own SQL text | not practical |
| Q5 | `DisplayWorkProjectionReader.LoadAsync`, Home limit 1000 | MATERIALIZED CTEs, about forty correlated sub-selects per row, `json_extract`; the 30 s cache is bypassed | production code | the product's own SQL text | not practical |

The "Dapper" variant always calls the real production class with the real `DatabaseConnection`; it is the baseline.

### Not practical (`NotPracticalReasons`)

| Variant | Why it was not built |
|---|---|
| Q3, LINQ for the paging query | Needs `MIN() OVER` and `ROW_NUMBER() OVER (PARTITION BY ...)`. EF Core cannot translate window functions; rewriting them as correlated sub-selects changes the query instead of porting it. The Q3 LINQ variant therefore covers only the three follow-up loads and keeps the paging query as raw SQL. |
| Q4, LINQ | About 700 lines of SQL: a nine-CTE chain, `ROW_NUMBER`, about twenty `MAX(CASE ...)` aggregates, `GROUP_CONCAT`, correlated sub-selects with `LIMIT/OFFSET`, SQLite date functions. LINQ would need raw SQL fragments for most of it. |
| Q5, LINQ | `MATERIALIZED` CTEs, window functions, about forty correlated sub-selects per row, `json_extract` and a view with a four-way `UNION` of credit sources. EF Core cannot express these without raw SQL. |

This is part of the result: for the queries that matter most for speed, an EF port keeps hand-written SQL.

### Production behaviour copied rather than called

All of it is marked with a comment naming the source and commit `2210bc763`.

* Q1, Q2: the SQL text and grouping step of the two repository methods (the batch size 500 is `SqliteBatching`, which is internal).
* Q3: the paging SQL and the composition helpers of `LibraryWorkFeedReadService` (inline and private). Only change: Dapper's `IN @AllowedWorkIds` expansion of an empty list is written out.
* Q4: `BuildProjectionSql` and Q5's `BuildSql` are private/internal. They are **not** copied: they are read from the compiled production assembly by reflection (`BindingFlags.NonPublic`), so the EF variant runs byte-for-byte the product's SQL and cannot drift. The short data `SELECT`, count statement, conditions and row mapping of `GetPageAsync` are copied for the two query shapes used.
* Q5: `NormalizeContributorList` and `DisplayArtworkUrlResolver` are copied. The metrics call (`PerformanceMetrics`) is skipped.

## Fairness rules

1. Same file, same SQLite build (the product's pinned `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3, which carries SQLite 3.50.4), same pragmas. EF takes its connection from `IDatabaseConnection.CreateConnection()` (WAL, `synchronous=NORMAL`, busy timeout 5 s, 16 MiB cache, 256 MiB mmap) and it is disposed per operation, as in production.
2. One EF context per operation from a pooled factory (`AddPooledDbContextFactory`). Q1 also has a labelled non-pooled `new DbContext` per call.
3. `verify` compares every variant to production field by field, in order (see below).
4. Parameter sets are fixed: ids come from deterministic strides, pages from fixed offsets.
5. BenchmarkDotNet: Release only, `MemoryDiagnoser`, columns Median, P95, Mean, StdDev, Allocated, Gen0/1/2 and the ratio to the Dapper baseline.
6. Synchronous where production is synchronous. Storage reads (Q1, Q2, Q4, and Q5's pseudonym lookup) call the synchronous SQLite API and return a completed task, so their EF variants use `ToList()`, not `ToListAsync()`. Where production awaits (`QueryAsync` in Q3 and in Q5's main query), the EF variants await too.
7. EF options set, and nothing else: SQLite provider and `QueryTrackingBehavior.NoTracking` as the default. Logging off, no sensitive-data logging, thread-safety checks left at their default. The EF write path in the concurrency run switches tracking on for its own context only.

Value converters match the product exactly: `Guid` as a 16-byte big-endian BLOB through `GuidSql`, `DateTimeOffset` as ISO-8601 `"o"` text parsed with `DateTimeOffset.Parse`. No enum columns are involved.

### Differences found while porting (worth knowing for the decision)

* EF silently reads a `Guid` BLOB with its **own** byte order unless a converter is attached to that exact property. The convention-level converter does not apply to ad-hoc `SqlQuery<T>` result types, so a first attempt returned plausible-looking but wrong ids. Every raw-SQL row type is therefore registered as a keyless entity, and `verify` catches any slip.
* Dapper leaves `0` when a NULL column maps to a non-nullable `int` (`DisplayWorkRow.CollectionManifestTotalCount`); EF throws. The EF raw variant uses a twin row type with `int?` and maps to the production type afterwards (`Ef/DisplayWorkEfRow.cs`, `Ef/RowMapper.cs`); a real port would make the property nullable.
* `new SqliteParameter("@x", 0)` binds to the `SqliteType` overload and carries no value. Raw SQL parameters must be boxed (affects Dapper-free code only).

## Packages (benchmark only, nothing ships)

| Package | Version | Licence | Why |
|---|---|---|---|
| `BenchmarkDotNet` | 0.15.8 (latest stable supporting net10.0) | MIT | timing, allocations, statistics |
| `Microsoft.EntityFrameworkCore.Sqlite.Core` | 10.0.3 (same line as the repo's `Microsoft.Data.Sqlite` 10.0.3) | MIT | EF Core with the SQLite provider **without** its own native SQLite, so EF runs on the product's pinned `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3 |

Their transitive packages are MIT (`Microsoft.EntityFrameworkCore*`, `Perfolizer`, `Iced`, `Gee.External.Capstone`, `CommandLineParser`, `Microsoft.CodeAnalysis*`, `Microsoft.Diagnostics.*`) and are AGPLv3-compatible. `THIRD-PARTY-NOTICES.md` is not touched because nothing ships. `benchmarks/Directory.Packages.props` imports the root file and adds only these two versions.

## Dataset

Created through the production path (`new DatabaseConnection(path)`, `InitializeSchema()`, `RunStartupChecks()`), then filled with set-based `INSERT ... SELECT` from recursive generators, one transaction per table group (the technique of `tests/MediaEngine.Performance.Tests/LargeLibraryFixture.cs`). Ids are deterministic and random-looking, so a seed is reproducible byte for byte. `ANALYZE` is run once at the end (the product gets statistics from `PRAGMA optimize` at startup) and the WAL is checkpointed.

| Table | `full` | `projection` | `small` |
|---|---:|---:|---:|
| leaf works (each with 1 edition and 1 media asset) | 100,000 | 4,000 | 2,000 |
| parent works (TV shows, music albums, no file) | 2,500 | 100 | 50 |
| `canonical_values` | 1,025,417 | 41,017 | 20,508 |
| `canonical_value_arrays` | 194,834 | 7,794 | 3,897 |
| `metadata_claims` (mix of current / superseded, 4 providers + Wikidata, 2 years) | 500,000 | 20,000 | 10,000 |
| `persons` | 20,000 | 800 | 400 |
| `artwork_assets` and `entity_artwork_links` | 50,000 each | 2,000 each | 1,000 each |
| `review_queue` and `identity_jobs` (about 2% of works each) | 2,000 each | 80 each | 40 each |
| collections | 500 | 20 | 10 |
| database file | 510 MiB | 23 MiB | 13 MiB |

Media types: 30% Books, 20% Movies, 25% TV episodes (20 per show), 15% Music tracks (12 per album), 6% Audiobooks, 4% Comics. Credits are not a table: the display projection derives them from `canonical_value_arrays` and claims, so persons are referenced by name. `search_index` is not populated; none of the five queries touches it.

Every work is marked Ready (`works.ready_at`), as in a fully processed library: Home, detail and the library list show only Ready works, so without it Home and detail return no rows and look fast.

Seed time on the reference desktop: about 16 s for `full`, under 1 s for `small` and `projection`. Audiobooks carry an explicit original publication year (an identified audiobook normally has one); without it Q4 evaluates a correlated sub-select over the whole credits view for each audiobook.

## Sizing: why Q4 and Q5 do not run at 100,000 works

Measured with `probe` (production Dapper code only, single runs, reference desktop: AMD Ryzen 7 9800X3D, 16 logical cores, other processes running; treat as orders of magnitude).

**Since 2026-10-19 (library list speed spec):** Q4 no longer grows faster than the library. At 4,000 works, all Ready, `probe --repeat 3` measured Q4 no filter 0.9-1.0 s, Q4 `Movies` 0.29-0.38 s and Q5 Home 6.9-7.6 s. The cause was seven credit look-ups that read every credit of a kind for each item (see `engineering/reports/slow-reads-2026-10.md`); guardrails in `tests/MediaEngine.Performance.Tests/LibraryListGuardrailTests.cs` keep it fixed. Q5 is the subject of the next speed spec. The table below is the earlier measurement, taken before the fix and before Home and detail showed only Ready works:

| Leaf works | Q1 | Q2 | Q3 (first / deep page) | Q4 no filter | Q4 `Movies` | Q5 Home |
|---:|---:|---:|---:|---:|---:|---:|
| 2,000 | 0.05-0.2 s | 0.02-0.07 s | 0.04 s / 0.03 s | 9-12 s | 0.65 s | 4.7-5 s |
| 4,000 | 0.02-0.4 s | 0.01-0.05 s | 0.03-0.1 s | 11-22 s | 1.3-1.8 s | 6-7 s |
| 16,000 | 0.07-0.2 s | 0.015-0.04 s | 0.13-0.57 s | 270-470 s | 19-22 s | 16-21 s |
| 100,000 | 0.08 s | 0.02 s | 2.6-6.5 s | stopped unfinished after 15+ min | not run | 352 s |

Before the fix, Q4 (library items page) and Q5 (Home projection) grew faster than the library: 8x the works (2k to 16k) cost roughly 25-45x for Q4 without a filter. The Q4 cause was found later (above); `explain --query q4` prints SQLite's plan (a materialised credits view and dozens of correlated sub-selects). It is the product's own SQL and both stacks run it identically, so the comparison stays valid, but the benchmark's fixed job (3 warm-up + 15 iterations per variant) would need hours at 100,000 works.

**Recommended run** (about 30 minutes of benchmarking plus a few minutes for `concurrency`, estimated from the table):

* `full` file: Q1, Q2 (`LightQueryBenchmarks`) and Q3 (`FeedQueryBenchmarks`).
* `projection` file (4,000 works): Q4, Q5 (`ProjectionQueryBenchmarks`) and the `concurrency` run, whose readers loop Q2 and Q4.

If Shy wants Q4/Q5 at 100,000 works anyway, the harness supports it (`--filter`, `--iterationCount`, `--warmupCount`), but expect each Q5 call to take about 6 minutes and each Q4 call far longer.

## Commands

Run from the repository root. Use a scratch directory outside the repository; every command takes the database path.

```bash
T=/c/Users/<you>/AppData/Local/Temp/tuvima-bench           # any scratch folder

dotnet build benchmarks/MediaEngine.Storage.Benchmarks -c Release
RUN="dotnet run -c Release --no-build --project benchmarks/MediaEngine.Storage.Benchmarks --"

$RUN seed --db $T/small.db --scale small                    # seconds
$RUN verify --db $T/small.db                                # must print "verify passed"

$RUN seed --db $T/full.db --scale full                      # about 16 s, 510 MiB
$RUN seed --db $T/projection.db --scale projection          # seconds, 23 MiB
$RUN verify --db $T/full.db --only Q1,Q2,Q3                 # a few seconds
$RUN verify --db $T/projection.db                           # about 1 minute (Q4/Q5 run twice each)

$RUN bench --db $T/full.db --filter "*LightQuery*" "*FeedQuery*"
$RUN bench --db $T/projection.db --filter "*ProjectionQuery*"
$RUN concurrency --db $T/projection.db --seconds 60 --writer-tps 50

$RUN clean --db $T/small.db                                 # deletes the db, -wal, -shm and the .bench.json description
```

Other commands: `probe --db <path> [--ef] [--repeat n] [--only Q1,Q4]` times each query once (sizing aid, not a measurement); `explain --db <path> --query q4|q5` prints SQLite's query plan for a heavy read.

* Stop a running Engine or Dashboard first if binaries are locked; do not kill other `dotnet` processes.
* `bench` takes any BenchmarkDotNet argument. A quick smoke test: `bench --db <small> --job dry --filter "*LightQuery*"`. The fixed jobs of the Feed and Projection classes still apply on top of `--job dry`; add `--iterationCount 1 --warmupCount 1` to shorten them.
* BenchmarkDotNet reports (`*-report-github.md`, `*-report.csv`) and the concurrency CSV are written to `BenchmarkDotNet.Artifacts/results` **next to the database file**, never into the repository. `benchmarks/.gitignore` also ignores `bin/`, `obj/`, `*.db*`, `BenchmarkDotNet.Artifacts/`, `*.csv`, `*.log`.

### Job settings

* `LightQueryBenchmarks` (Q1, Q2): BenchmarkDotNet's default job (automatic pilot, warm-up and iteration counts), Release, one launch.
* `FeedQueryBenchmarks` (Q3) and `ProjectionQueryBenchmarks` (Q4, Q5): `WarmupCount(3)`, `IterationCount(15)`, `InvocationCount(1, 1)` (unroll factor 1), one launch, so a run costs 18 calls per variant.
* Every class: `MemoryDiagnoser`, markdown and CSV exporters, JIT-optimisation validator (a Debug build fails fast).

### Concurrency run

Four runs, each in its own process against a fresh copy of the file (so growth from one writer never reaches the next, and peak working set and GC counts belong to that run alone): scenario A (one shared background writer on the production Dapper write path, `IDatabaseConnection.ExecuteWriteAsync`) and scenario B (the writer also uses the stack under test), each for Dapper and EF. The writer commits 50 `metadata_claims` inserts and 20 `canonical_values` updates per transaction at a fixed target rate, `--writer-tps` (default **50 transactions/s**, paced on a fixed schedule so a slow transaction is followed by a shorter pause), so the readers of both stacks face the same write load. The printed table and the CSV give the achieved rate, and the output says `WRITER BELOW TARGET` if it falls under 95% of the target. `--writer-tps 0` runs the writer unthrottled (as fast as it can). The EF writer takes the same global write lock and does one `SaveChanges` per batch inside a transaction on a pooled, change-tracking context. Four reader tasks loop Q2 and Q4 (media-type page) on the stack under test.

Two things about the EF side to keep in mind when reading the numbers:

* The EF writer runs a `SELECT` (load the 20 `canonical_values` rows with tracking, then modify and `SaveChanges`) **while holding the write lock**. The Dapper writer issues blind `UPDATE`s with no read. For a blind update of known keys, idiomatic EF would more likely use `ExecuteUpdate` or attached stub entities and skip the `SELECT`; load-modify-save is the classic change-tracking pattern and was kept, so the EF writer's transactions carry one extra read.
* The EF readers run Q2 through the **LINQ** variant, not the raw-SQL one (Q4 has only the raw-SQL variant).

Reported per run: reader operations with p50/p95/p99, writer transactions, `SQLITE_BUSY`/`SQLITE_LOCKED` counts (writer and reader, also when wrapped by EF), any other error (the run fails and prints it), peak working set, GC counts, allocated bytes and WAL size. Scenario A and B are the same run for Dapper; comparing them shows the noise level. Each child process is given the run length plus 5 minutes; if it exceeds that, only that child is killed, the run fails with a message, and the file copy is still deleted.

## Cost of a full run

* Disk: `full` 510 MiB, `projection` 23 MiB. `concurrency` copies the file for each of its four runs; the **WAL grows quickly** because the long Q4 readers block checkpointing (1-2 GB in 10 s at the small scale), so allow up to about 10 GB of free space for a 60-second run. Copies are deleted when each run ends.
* Time (estimate): seed under a minute; `Light` about 3 min; `Feed` at 100,000 works about 8 min; `Projection` at 4,000 works about 12 min; `concurrency --seconds 60` about 6 min including copies.
* Memory: a few hundred MiB per process.

## Known limits

Synthetic data (realistic shape, not a real library); one desktop; SQLite plans depend on the data and on `ANALYZE`; the EF variants include a few steps production does not (for example mapping the twin row type in Q5); Q4 and Q5 are measured at a smaller size than the other three; LINQ variants exist only where LINQ can express the query. Production rebuilds its SQL string on every call, while the EF variants build or cache it once (microseconds against calls that take milliseconds to minutes). The concurrency reader operations number only tens per run, so their p99 is effectively the maximum; read p50 and p95. The paced writer catches up after a stall, so short bursts above the target rate are possible.
