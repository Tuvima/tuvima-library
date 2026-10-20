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

Media types: 30% Books, 20% Movies, 25% TV episodes (20 per show), 15% Music tracks (12 per album), 6% Audiobooks, 4% Comics. Credits are not a table: the display projection derives them from `canonical_value_arrays` and claims, so persons are referenced by name. `search_index` is not populated by the seeder; none of the five queries touches it (the snapshot's `series-search` sections rebuild it in the file they are run on).

Every work is marked Ready (`works.ready_at`), as in a fully processed library: Home, detail and the library list show only Ready works, so without it Home and detail return no rows and look fast.

Seed time on the reference desktop: about 16 s for `full`, under 1 s for `small` and `projection`.

**Audiobooks (since 2026-10-20).** Half of the audiobooks carry their own original publication year (an identified audiobook normally has one). The other half have none, so the display and library list reads borrow the year of the book with the same title and the same author (`MediaDateSql.RelatedBookOriginalYearCte`, worked out once per read for that read's own audiobooks; the old per-row form, which built the whole credits view, was removed on 2026-10-20). Of the audiobooks without a year, half match a book by title and author (the audiobook's asset and work title are the book's title in upper case with spaces around it, so only `LOWER(TRIM(...))` makes them equal; its `author` credit is the book's author), a quarter match a book's title but have a different author, and a quarter match nothing. At `projection` scale that is 240 audiobooks, 120 without a year, 60 of them matching a book by title and author. **Numbers measured before 2026-10-20 used audiobooks that all had their own year**, so they never exercised this look-up and are not comparable with new ones.

**Play history and a person group (since 2026-10-20).** Spec `engineering/specs/audiobook-year-other-reads.md` (WP1) added two things to the seed, for the snapshot sections that read them. They only add rows to tables no earlier section reads (`user_states`, `person_group_members`, `persons.is_group`), so **they do not change the earlier sections' numbers or values**: `home`, `all-works`, `recent`, `detail:*`, `tv-skeleton`, `library-list` and `library-detail:*` compare identical to a file seeded without them.

* *One profile's play history* in `user_states`, owned by `SeedIds.PlayHistoryProfile`, 60 plays chosen by a fixed stride: 20 audiobooks without their own original year (some borrow a book's year, some borrow nothing), 10 audiobooks with their own year, 10 books, 10 TV episodes and 10 tracks. Every sixth play is finished (100%), two are hidden from Continue (`{"hide_continue":"true"}`), the rest sit between 5% and 95%, two in three of those with a saved position. `last_accessed` is distinct for every play, so the order is fixed. The profile has no `profiles` row (nothing reads one).
* *One person group*: the person in the middle of the list (`Person <persons/2>`, flagged `is_group`, already credited on several works) with one member (`Person <persons/2 + 1>`), no dates. The member's credits page lists the group's works as "With <group>" rows.
* The seeded audiobooks are in no collection (collections only hold books and comics), so the `series-page:audiobook-series` section passes audiobooks as `resolvedWorkIds` of a collection id nothing owns; its header says so.

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

### Before-and-after snapshot (`snapshot` and `compare`)

For speed work that must not change what the product shows (spec `engineering/specs/home-and-detail-speed.md`, D6). `snapshot` calls **production code only** and writes every field of every row it returns to one text file; `compare` checks two such files. Run `snapshot` on the unchanged code to get the "before" file, and again after each change.

```bash
$RUN snapshot --db $T/projection.db --out $T/before.txt          # all sections
$RUN snapshot --db $T/projection.db --out $T/part.txt --only home,detail   # only sections whose name starts with one of these
$RUN compare --before $T/before.txt --after $T/after.txt         # prints "compare passed" and exits 0, or lists differences and exits 1
```

Keep the snapshot files outside the repository (for example `.tmp/`); they are not committed. `snapshot` prints the time of each section to the console (not to the file). The sections can be slow on the `projection` file before the audiobook year look-up is fixed (the library list and "all works" read run the quadratic look-up).

| Section | Production call |
|---|---|
| `home` | `DisplayWorkProjectionReader.LoadAsync(ct, 1000)` (Home's limit) |
| `all-works` | `LoadAsync(ct)` with no limit; sorted by `WorkId`, `LibraryId`, `AssetId` (its order is not part of the contract) |
| `recent` | `LoadAsync(ct, groups: new DisplayRecentGroupFilter(rootIds, workIds), lean: true)` for 5 TV show roots, 5 album roots and 10 standalone works |
| `detail:<label>` | `LoadAsync(ct, detailId: id)` for a `book`, an `audiobook-own-year`, an `audiobook-borrowed-year` (no year of its own, matches a book by title and author), a `tv-episode`, a `music-track`, a `movie`, `series-mates` (a work whose series has other works) and a `person` (first by name with at least three credited items). The chosen id is in the section header. |
| `tv-skeleton` | the internal `LoadTvEpisodeSkeletonAsync` (read by reflection, as Q5 reads `BuildSql`) for the 5 sampled shows |
| `library-list` | `LibraryItemRepository.GetPageAsync(new LibraryItemQuery(Limit: 5000, IncludeAll: true))`; the first line is `total=<n>` |
| `library-detail:<label>` | `LibraryItemRepository.GetDetailAsync(id)` for the same sampled works as `book`, `audiobook-own-year`, `audiobook-borrowed-year`, `tv-episode`, `music-track`, `movie` |
| `series-page:<label>` | the internal `DetailCompositionOrchestrator.LoadCollectionWorksAsync` (a series page's, show's or album's list of items), read by reflection: the instance is created without its constructor and only `_db` is set, because the read uses nothing else. Labels `audiobook-series` (40 audiobooks, 30 without their own year, as resolved work ids of a fresh collection id; see above), `book-series` (the middle collection with at least three books) and `tv-season` (the middle TV show as `rootWorkId` plus its 20 episodes as resolved ids: the seeded file has no season level, and its episode numbers are on the file, so the root alone matches nothing). The rows are a private record, formatted like any other row. Keeps the reader's order. |
| `continue` | `DisplayJourneyProjectionReader.LoadAsync(profile, null)` for the seeded play-history profile (Home's Continue shelf) |
| `states` | the same with `includeCompleted: true` (the profile's full play history) |
| `collection-resolve` | `CollectionMediaLookupReadService.ResolveMetadataAsync` for 40 works: 20 audiobooks without their own year (spread over all four classes), then 5 books, 5 movies, 5 TV episodes and 5 tracks |
| `collection-lookup:<label>` | `CollectionMediaLookupReadService.LookupAsync(null, mediaTypes, {}, every work id, offset, 24)`: `audiobooks-p1` and `audiobooks-p2` ("Audiobooks", offsets 0 and 24), `books-p1` ("Books"), `all-p1` (no filter). Passing every work id as a parameter list needs the file to have under about 30,000 works (SQLite's limit on parameters), so it works at `projection` and `small` but not at `full` |
| `series-search:<label>` | `CollectionSearchReadService.SearchAsync` with the product's `SearchIndexRepository`, after `RebuildAsync()` (the seeder leaves `search_index` empty; the snapshot rebuilds it in the file it is run on). Queries: `audiobook-title` (an audiobook that borrows a book's year), `person-name` (a credited author of audiobooks) and `common-word` ("Title", which most titles contain). **While the known bug in spec section 7 is open (ids handed to `w.id IN @workIds` as plain Guids) these three sections are empty.** That is expected until the fix lands; once it has, their rows appear and must be compared like any other |
| `person-credits:<label>` | `PersonCreditReadService.GetLibraryCreditsAsync` (built with the real `CanonicalValueArrayRepository` and `PersonRepository`) for `audiobooks` (a person credited as author on at least two audiobooks without their own year), `audiobook-borrowing` (a person credited on an audiobook that borrows a book's year) and `group` (the seeded group's member) |
| `taste` | `TasteProfileRepository.GetSignalsAsync(profile, 1000)` for the seeded play-history profile |

The ids are chosen by SQL over the seeded file (ordered by id, then picked by a fixed stride), so the same seeded file always gives the same samples. Each section starts with `## <name>`, has one line per row, and the file ends with `sha256=<hex>` of everything above it. A row is every public readable property of the row type in declaration order as `name=value`, separated by ` | `: invariant culture, dates as `o`, Guids as `N`, `null` as `<null>`, collections as `[a, b]`, nested objects as `{name=value; ...}`, newlines as `
`.

**Order and ties (spec D7).** Rows keep the order the reader returned, except that consecutive rows with the same `CreatedAt` are sorted by `WorkId`, `LibraryId`, `AssetId` inside their group before writing, because the data store returns tied rows in whatever order its plan produces. `compare` reports, per section, either "identical" or the first 20 differing lines (as the fields that differ) and the counts of rows only in the before file and only in the after file. For `home` it also recognises the one allowed difference: when the rows that differ are all rows whose `CreatedAt` equals the last row's `CreatedAt` (they tie at the 1,000-row cut-off, so which of them made the cut may change), they are listed as "tie at cut-off" and the section still passes. A row present in both files under the same identity with a changed value is always a difference.

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
