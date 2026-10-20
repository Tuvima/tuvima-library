# Spec: Audiobook year on the remaining screens

> **Status:** Draft · **Version:** 1 · **Last updated:** 2026-10-20 · **Related docs:** [Product overview](../product/overview.md) · [Spec: Home and item detail speed](archive/home-and-detail-speed.md) (built; its section 8 lists these seven reads) · [Spec: Library list speed](archive/library-list-speed.md) · [Benchmarks README](../../benchmarks/README.md)

## 1. Goal

An audiobook with no original year of its own borrows the year of the book with the same title and author. Seven screens still work that out the old way: as soon as one row on the screen needs a borrowed year, the screen builds the whole library's credit list to find the book. Measured at 4,000 works, that one step is 95–99% of the screen's work (a series page of 40 audiobooks: 47 million operations instead of 0.4 million, about 2–3 seconds), and it doubles every time the library doubles. After this spec those screens look only at their own audiobooks and the books that could match them, so the borrowed year adds a small share of the work. The slow form is then deleted, and nothing on screen changes except speed.

## 2. Scope

**In scope.** These seven reads switch to the "own audiobooks only" year that the library list, Home and item detail already use:

1. **The list of items on a series page** (also used for a show's or an album's list of episodes or tracks).
2. **Home's Continue shelf**, and the same read used for a profile's full play history.
3. **The collection editor's details for chosen items** (title, creator, year and cover of the items in a curated collection).
4. **The collection editor's "add media" search** (24 results per page).
5. **Series search** (the search box on the series screens, 20 results).
6. **A person's credits** (the list of a person's works on their page, newest year first).
7. **The taste profile** (the recent plays that feed recommendations; no screen of its own).

Also in scope:
- One before-and-after comparison of every value these seven reads return, on the benchmark library.
- Guardrails that fail if any of these reads goes back to building the whole library's credit list for the year (Q1).
- Deleting the old form once nothing uses it (Q2).
- Optionally, the person page's search for candidate files (Q3).

**Out of scope**
- Places that read the whole library's credit list for their own purposes rather than for the year: series search's author column and name search, and a person's credits list itself (which reads it twice for every visit, see section 7). They stay as they are; section 7 records them as follow-ups.
- The series search bug found while measuring (section 7). It is fixed separately.
- New indexes or any other data store change, unless Shy picks Q3 (b).
- Any change to what these screens show, their order, filters, profile visibility rules or wire shapes.

## 3. Decisions made

- **D1. Same values, same order.** Every field of every row these reads return stays identical, and so does row order (a person's credits sort by year, so a wrong year would also move a row). The comparison (D6) is the proof; reviewers reject any difference it does not explain.
- **D2. The candidate list may be bigger than the rows, never smaller.** One audiobook's borrowed year depends only on that audiobook, the library's books and their authors, never on which other audiobooks are worked out alongside it. So a read may hand the year step any list of (work, file) pairs that contains every audiobook row it returns, and picks the cheapest one to write: usually "the audiobook works this read is about, with their files". Leaving out a returned audiobook would lose its year, so every read's fixture includes audiobooks without a year (D7).
- **D3. One statement per read; the year step is fed from tables or from one saved step.** SQLite copies a step's text at every place it is named, and the year step names its candidate list about six times. A read's candidate list is therefore a plain look-up over tables (with the read's own filter written once in code and used twice) or a single step the read marks as saved ("materialised"), never a chain of unsaved steps. All seven reads are single statements today and stay that way; none needs the person page's two-read split.
- **D4. Reads that return a page work out the year only for that page.** Series search (20 rows) and "add media" (24 rows) first choose their page, then work out the year for those rows. For "add media" the alternative (every audiobook the person may see) would be a library-sized list for a 24-row page.
- **D5. Each read's own rules stay untouched.** The row choice (which file a series row shows: the most recently played; Continue's progress rules; group credits on a person's page; the editor's representative file), the sort and every other field stay as they are. Only the year expression and the step that feeds it change.
- **D6. One before-and-after comparison.** The benchmark `snapshot` command gains a section per read, run on the benchmark library before the first change and after each packet; the files must match exactly. The benchmark library gains one profile's play history, which the Continue, play history and taste sections need; the README records it so older numbers are read with that in mind.
- **D7. Guardrails measure the year's own cost.** Today's slow form already grows in step with the library (the whole credit list is built once per visit, not once per audiobook), so the usual "twice the library costs at most 2.5 times the work" check would pass it. Instead, each read is measured twice on the guardrail library: with audiobooks that need a borrowed year and with the same number of audiobooks that have their own. The difference is the year's cost. It gets a fixed budget at 4,000 works (today about 42 million operations; after the change expected in the low millions), with the measured number written beside it. Operations are counted, not seconds, as in the earlier speed specs.

## 4. UI behaviour

Nothing changes on screen except speed.

| Situation | What the person sees |
|---|---|
| Normal | Same rows, same values, same order. A series page with audiobooks, the Continue shelf, the collection editor and a person's page open faster. Each now skips a step that took about 1–3 s at 4,000 works and grows with the library. |
| Empty (nothing to show yet) | Unchanged. |
| Loading | Unchanged, just shorter. |
| Error (Engine unreachable, not allowed, not found) | Unchanged. Profile limits hide exactly the same items as today. |
| Partly ready (enrichment still running, provider down) | Unchanged. An audiobook whose matching book is not yet identified shows no year, as today. |

## 5. Data / model changes

None in the product, unless Q3 (b) is chosen (one new index on provider claims, added by a numbered data store upgrade step; no reset, no re-ingest). The benchmark library and the performance-test library (test tools, not the product) gain one profile's play history and an audiobook series.

## 6. Work packets

**Today's cost, measured** on 2026-10-20 through the production code, on the performance-test library (10% audiobooks, half without their own year). Thousands of SQLite operations, the same on every run; "needs year" rows include audiobooks without their own year, "no year needed" rows are the same read with audiobooks that have one (or no audiobooks).

| Read | Rows | 4,000 works: needs year | 4,000: no year needed | 8,000 works: needs year | 8,000: no year needed |
|---|---|---:|---:|---:|---:|
| The year step alone, for one audiobook | 1 | 42,030 | — | 83,599 | — |
| Series page list | 40 audiobooks | 47,018 | 357 | 93,374 | 618 |
| Continue shelf | 40 audiobooks + 20 books | 47,031 | 370 | 93,226 | 470 |
| Collection editor details | 40 audiobooks | 42,865 | 151 | 85,168 | 259 |
| Collection editor "add media" | page of 24 | 43,099 (audiobooks) | 1,311 (books) | 85,505 | 2,666 |
| A person's credits | 12 and 25 credits | 126,636 (2 audiobooks) | 84,504 (books only) | 252,991 | 168,695 |
| Taste profile | 60 plays | 46,554 | 26 | 84,806 | 26 |
| Series search | 20 | not separable (see below) | | | |

What the numbers say:
- **Once per visit, not once per audiobook.** One audiobook costs 42 million operations; 40 cost 47 million. SQLite builds the whole library's credit list once per visit as soon as any row needs it, then looks each audiobook up in it. So the cost is linear, not "square of the library" as the earlier spec's section 8 assumed for these reads, but it is still 95–99% of each read's work, and it doubles with the library. On a desktop that is busy with other work, the step took 1–3 s at 4,000 works and 4–12 s at 8,000. If the cost stays linear, the 100,000-work benchmark library would need about 1 billion operations (tens of seconds); that figure is an estimate, not measured.
- **A person's credits** are expensive even without the year (84 million operations), because the read builds the whole credit list twice for its own purposes. This spec removes the year's extra 42 million; the rest is a follow-up (section 7).
- **Series search** could not be measured cleanly. On the path the Engine actually uses (through the search index), it returns no rows because of the bug in section 7, so it never reaches the year. On the fallback path (no search index), "Audiobooks" took 20.7 billion operations (570 s) at 4,000 works, mostly the fallback's own credit matching for every candidate file. WP1's comparison and WP3's guardrail measure it on the index path once the bug fix has landed (WP3 depends on it).

**How each read takes the year step** (D2–D4):

| Read | Rows it returns | Candidate list for the year step | Copy risk (D3) |
|---|---|---|---|
| Series page list | the series' works, each with its most recently played file | the audiobook works matched by the same filter (written once in code, used twice), with all their owned files | none: plain tables |
| Continue shelf / play history | the profile's played files | the profile's played audiobook files and their works (ignores the progress filters; a superset) | none: plain tables |
| Taste profile | the profile's latest 1,000 plays | the profile's played audiobook files and their works | none: plain tables |
| Collection editor details | the named works, each with a representative file | the representative-file step, marked saved, audiobooks only | low: one small saved step |
| Collection editor "add media" | 24 works per page | the chosen page (D4), as a saved step | low: the page step is saved |
| Series search | 20 matches | the 20 chosen matches (D4), as a saved step | low: the page step is saved |
| A person's credits | the person's credited works, grouped | the person's credited audiobook files and their works, from the read's own credit step, marked saved | low: one saved step |

### WP1 — Comparison sections, play history in the benchmark library, guardrail library
- **What it does:** in `benchmarks/MediaEngine.Storage.Benchmarks/Snapshot/SnapshotCommand.cs` add sections that call production code only: `series-page:<label>` (the internal `DetailCompositionOrchestrator.LoadCollectionWorksAsync`, read by reflection as `tv-skeleton` is, for a series with at least three audiobooks without their own year, a book series and a TV season), `continue` and `states` (`DisplayJourneyProjectionReader.LoadAsync` without and with `includeCompleted`), `collection-resolve` (`CollectionMediaLookupReadService.ResolveMetadataAsync` for 40 sampled works, half audiobooks), `collection-lookup` (`LookupAsync` for "Audiobooks", "Books" and no filter, pages 1 and 2), `series-search` (`CollectionSearchReadService.SearchAsync` with the product's `SearchIndexRepository` after `RebuildAsync`, for an audiobook title, a shared author name and a word matching more than 20 items), `person-credits:<label>` (`PersonCreditReadService.GetLibraryCreditsAsync` for a person credited on audiobooks with and without their own year, and a person credited through a group) and `taste` (`TasteProfileRepository.GetSignalsAsync(profile, 1000)`). In the benchmark seeder add one profile's play history (about 60 plays: audiobooks with and without their own year, books, episodes, tracks; some finished, some hidden from Continue) and one person-group membership; record both in the README.
  - In `tests/MediaEngine.Performance.Tests/LibraryListFixture.cs` add two profiles' play history (one with audiobooks that need a borrowed year, one with audiobooks that have their own, plus books, one play per 100 works), so the guardrails can take the difference (D7). Existing guardrail numbers must not move; if one does, re-measure and record it.
- **Files likely touched:** `benchmarks/MediaEngine.Storage.Benchmarks/Snapshot/SnapshotCommand.cs`, the benchmark seeder, `benchmarks/README.md`, `tests/MediaEngine.Performance.Tests/LibraryListFixture.cs`
- **Depends on:** none
- **Size:** Standard
- **Done when:** `snapshot` on unchanged code writes every new section with rows (none "no sample found", except `series-search` while the section 7 bug is open, which is recorded); a second run gives an identical file; the existing performance guardrails pass.

### WP2 — Series page list, Continue shelf and play history, taste profile
- **What it does:** the three reads whose candidates come straight from tables. In each, add (or extend) the `WITH` with `MediaDateSql.RelatedBookOriginalYearCte("related_book_year", <candidates>, "rby_")` and pass `MediaDateSql.RelatedBookYearLookup("related_book_year", <work>, <file>)` as `relatedBookYearExpression` to `DisplayOriginalYear`, with the same work and file expressions the year uses today (`w.id`/`ma.id`; `w.id`/`us.asset_id` in the taste profile).
  - Series page (`DetailCompositionOrchestrator.Readers.cs`, `LoadCollectionWorksAsync`): move the row filter (`w.collection_id = @collectionId OR ci.collection_id … OR w.id IN @resolvedWorkIds OR (@rootWorkId …)`) into one C# string used in the main `WHERE` and in the candidate select. Candidates are those works with `LOWER(media_type) LIKE '%audio%'` joined to all their `work_owned_assets` files that pass the same status, orphan and allowed-file rules. The chosen file (most recently played) is one of them.
  - Continue shelf and play history (`DisplayJourneyProjectionReader.cs`): candidates are `user_states` of `@ProfileId` joined to file, edition and work, audiobook works only.
  - Taste profile (`TasteProfileRepository.cs`): candidates are `user_states` of `@userId` joined the same way, audiobook works only. The 1,000-row limit is not applied to the candidates (a superset is allowed, D2).
  - Tests (behaviour sentences): per read, an audiobook without its own year shows the matching book's year (`SeriesPage_AudiobookWithoutAYear_ShowsTheMatchingBooksYear`, `ContinueShelf_…`, `TasteSignals_…`); the Q1 guardrails for these three reads.
- **Files likely touched:** `src/MediaEngine.Api/Services/Details/Internals/DetailCompositionOrchestrator.Readers.cs`, `src/MediaEngine.Api/Services/Display/DisplayJourneyProjectionReader.cs`, `src/MediaEngine.Storage/TasteProfileRepository.cs`, tests in `tests/MediaEngine.Api.Tests/`, `tests/MediaEngine.Storage.Tests/`, `tests/MediaEngine.Performance.Tests/`
- **Depends on:** WP1 (baseline snapshot taken first)
- **Size:** Standard
- **Done when:** the new tests pass; `compare` against the WP1 baseline reports `series-page`, `continue`, `states` and `taste` identical; each read's year cost (D7) is within its budget, and with the old form put back locally it is over budget (record both numbers, do not commit).

### WP3 — Collection editor (details and "add media"), series search, person's credits
- **What it does:** the four reads that need a saved step or a page chosen first.
  - Collection editor details (`CollectionMediaLookupReadService.ResolveMetadataAsync`): mark `representative_assets` `MATERIALIZED`; candidates are its rows joined to `works`, audiobook works only (`ra.WorkId`, `ra.AssetId`).
  - "Add media" (`LookupAsync`): put today's select (same joins, `WHERE`, `ORDER BY Title COLLATE NOCASE, w.id` and `LIMIT/OFFSET`, plus the source `w.id`, root id and `ra.AssetId` the year needs) into a saved `lookup_page` step; candidates are its audiobook rows; the outer select adds `Year` and keeps the same order. Rows and their order must not change, including the duplicates the multi-name joins can produce (the C# de-duplication stays).
  - Series search (`CollectionSearchReadService.SearchAsync`): take `Year` out of `matched`, add the file id and root id to it, add a saved `search_page` step (`WHERE RowNumber = 1 ORDER BY Title COLLATE NOCASE LIMIT 20`), take candidates from its audiobook rows; the outer select adds `Year` in the same order. If rows with the same title change order, add `WorkId` as the last sort key in both steps and record it.
  - A person's credits (`PersonCreditReadService.GetLibraryCreditsAsync`): mark `effective_credit` `MATERIALIZED`; candidates are its files joined to their works, audiobook works only. The year keeps reading the same `w.id` and grouped `ma.id` as today, so the newest-first order is unchanged.
  - Tests: per read, an audiobook without its own year shows the matching book's year; for "add media" and series search, also an audiobook on page 2; the Q1 guardrails for these four reads.
- **Files likely touched:** `src/MediaEngine.Api/Services/ReadServices/CollectionMediaLookupReadService.cs`, `src/MediaEngine.Api/Services/ReadServices/CollectionSearchReadService.cs`, `src/MediaEngine.Api/Services/ReadServices/PersonCreditReadService.cs`, tests in `tests/MediaEngine.Api.Tests/` and `tests/MediaEngine.Performance.Tests/`
- **Depends on:** WP1, and the series search bug fix (section 7) for the series search part. Runs beside WP2 (different files; the second packet to finish merges the shared performance-test file).
- **Size:** Standard
- **Done when:** the new tests pass; `compare` reports `collection-resolve`, `collection-lookup`, `series-search` and `person-credits` identical; each year cost is within budget and over budget with the old form put back locally (record, do not commit); preparing each statement takes under 50 ms on the benchmark library (no copy blow-up, D3).

### WP4 — Delete the old form (if Q2 is (a))
- **What it does:** delete `MediaDateSql.RelatedBookOriginalYear`; make `relatedBookYearExpression` a required parameter of `DisplayOriginalYear` (reads that never return audiobooks pass `"NULL"`, as `LoadTvEpisodeSkeletonAsync` does); update the doc comments that mention the old form, the benchmarks README paragraph that names it, and the `MediaDateSqlTests` line that passes `"NULL"`.
- **Files likely touched:** `src/MediaEngine.Storage/MediaDateSql.cs`, `benchmarks/README.md`, `tests/MediaEngine.Storage.Tests/MediaDateSqlTests.cs`
- **Depends on:** WP2, WP3
- **Size:** Mechanical
- **Done when:** a search for `RelatedBookOriginalYear(` finds nothing; the CI-strength build and all tests pass; a final full `snapshot` compares identical with the WP1 baseline (every section, including the earlier spec's).

### WP5 — Person page candidate files by index (only if Q3 is (b))
- **What it does:** a numbered data store upgrade step adds a partial index on `metadata_claims` over the trimmed claim value (case-insensitive) for the credit claim names only (`PersonCreditSql.CreditKeys`), and `DisplayWorkProjectionReader.PersonAssetsCtes` reads claims through it instead of scanning every claim. Same files found, same order.
- **Files likely touched:** `src/MediaEngine.Storage/Schema/Upgrades/NNNN_credit_claim_value_index.sql`, `src/MediaEngine.Storage/Schema/schema.snapshot.sql`, `src/MediaEngine.Api/Services/Display/DisplayWorkProjectionReader.cs`, `docs/reference/database-schema.md`, `tests/MediaEngine.Performance.Tests/DisplayReadGuardrailTests.cs`
- **Depends on:** none (separate files)
- **Size:** Standard
- **Done when:** `EXPLAIN QUERY PLAN` of the person look-up shows no scan of `metadata_claims`; the person page guardrails pass; `compare` reports `detail:person` identical.

## 7. Risks and open questions

- **Risks:**
  - *An audiobook loses its year.* If a read's candidate list misses one of its returned audiobooks (for example the most recently played file of a series row), that row shows no year. D2 forbids it; the behaviour tests and the comparison catch it.
  - *Row order changes.* A person's credits sort by year, and the two page-first reads (D4) sort again after choosing the page. The comparison checks order, not only values.
  - *Statement preparation blows up.* D3; WP3 times statement preparation.
  - *The guardrail library grows.* Adding play history to the shared performance library could move the earlier guardrails' numbers; WP1 checks them.
- **Found while planning, not in scope:**
  - **Series search through the search index returns nothing (bug).** The search index finds the matching works, but the read passes their ids to the data store in a form that never equals the stored ids (a list of ids is not converted the way a single id is). Measured: 40 known works matched 0 rows; the same ids converted explicitly matched 40. The Engine always has a search index, so series search shows no results whenever the index finds matches; the existing tests only cover the no-index fallback. Other reads that pass lists of ids unconverted may have the same problem. It needs its own fix and a test before WP3's series search part; I'm proposing it as a separate piece of work.
  - **A person's credits read builds the whole credit list twice per visit for its own purposes** (84 million operations at 4,000 works even without audiobooks, about 1–2 s). Series search's author column and fallback name search do the same for every candidate file. These are the next biggest costs on those screens; candidate follow-ups once this is built.
  - **"Add media" works out a representative file for every work in the library on every page.** Proportional to the library, not the main cost today, but the largest left once the year is fixed.
- **Open questions:**
  - **Q1. How should the guardrails look?** (a) One check per read (seven tests) that the borrowed year's own cost (D7: with year-needing audiobooks minus with own-year audiobooks) stays under a fixed budget at 4,000 works. A read going back to the whole-library build fails by more than ten times. (b) As (a), plus a fixed budget on each read's total work and a "twice the library" growth check (21 tests), like Home and detail have. That also catches a read getting slower for other reasons, but every budget needs re-measuring whenever the shared test library changes. (c) No per-read tests: rely on deleting the old form (Q2) so no code can call it, plus the behaviour tests. Cheapest, but nothing would notice a read that hands the year step a library-sized candidate list. **Recommendation: (a).** It targets exactly the failure this spec removes, and with the old form deleted there is no other way back.
  - **Q2. Delete the old form afterwards?** (a) Delete it and make every read that shows a year say how it gets the borrowed year, so a new screen cannot fall back to the slow form by leaving the parameter out (WP4). (b) Keep it as a convenience for one-row reads. **Recommendation: (a)**, per the "best long-term fix before beta" rule. Even for one row it costs the whole library's credit list (42 million operations at 4,000 works).
  - **Q3. The person page's search for candidate files (from the earlier spec's section 8).** To find which files credit a person, the person page reads every stored credit, every provider claim and every stored value once (claim names have no index), then checks credits for just those files. Measured at 4,000 works: 1.8 million operations with 1 claim per file, 2.8 million with 10, 4.9 million with 30, about 26 operations per claim. So 1 million claims add about 26 million operations (roughly 0.5–1 s). The 100,000-work benchmark library has 500,000 claims (about 13 million operations); a library with 30 claims per file at that size would have 3 million (about 80 million). (a) Leave it, and revisit if a real library's person page is slow. (b) Add an index for credit claim names (WP5: one numbered data store upgrade step, no reset). The person page then reads only matching claims, at the cost of slightly slower writes of credit claims during ingestion, the busiest write path. (c) Find candidates from stored credits and values only and read claims just for those files: no data store change, but an item credited only by a provider claim (no stored credit yet) would drop off the person's page, which changes behaviour. **Recommendation: (a).** At realistic claim counts it is a fraction of a second and linear, and (b) can be added later without a reset if a large real library shows otherwise.

## 8. Gaps and drift

Not built yet.
