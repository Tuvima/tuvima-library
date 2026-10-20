# Spec: Home and item detail speed

> **Status:** Draft · **Version:** 1 · **Last updated:** 2026-10-19 · **Related docs:** [Product overview](../product/overview.md) · [Report: Slow library and Home reads at 4,000 works](../reports/slow-reads-2026-10.md) (causes 3 and 4, fix options D and E, recommendation step 2) · [Spec: Library list speed](archive/library-list-speed.md) (built; section 8 records the audiobook year look-up) · [Benchmarks README](../../benchmarks/README.md)

## 1. Goal

Home opens in under a second at 4,000 works instead of about 7 seconds, and an item's detail page gets its items in well under half a second instead of 2–5 seconds. Both stay proportional as the library grows: twice the works costs at most about twice the work. Guardrail tests keep them there, and nothing on screen changes except speed.

## 2. Scope

**In scope**
- **Home's item read gathers values in one pass (report option D).** Today each of the 1,000 items Home asks for fetches about 280 values one at a time (title, tagline, cover and its sizes, widths, heights, artwork states, colours, credits, year, and so on), each tried on the file, then the work, then the show or album. After this spec the read first picks the items, then reads all the stored values for just those items in one pass, then applies the same "file, then work, then show or album" choice to each field.
- **Credits are built only for the items being returned (option E).** Home and detail no longer build the whole library's credit list (about 8,700 rows) and the whole library's artist list (about 600 rows) on every call; they build them for the items they return.
- **The audiobook year look-up becomes proportional.** An audiobook with no original year of its own borrows the year of the book with the same title and the same author. Today that search reads the whole library's credit list once per such audiobook, so its cost grows with the square of the library (3.9× the work each time the library doubles). It is shared by the library list, Home and detail. After this spec it is worked out once per read for the audiobooks in that read.
- **One field-by-field comparison** of every value Home, detail, recent pages, the "all works" read and the library list return, before and after, on the benchmark library.
- **Guardrail tests for Home and detail**, the same kind as the library list's: a fixed work budget at 4,000 works and a check that 8,000 works costs at most 2.5 times as much.
- **Remove the library list guardrail workaround.** The library list's guardrail library currently gives every audiobook its own year so the slow look-up stays out of its budgets. Once the look-up is fixed, the guardrail library includes audiobooks without a year (some with a matching book), and the list budgets are measured again.
- **Depends on Q2:** a light "which works can this person see" read for the screens that only need that list, instead of building every work's full display row.

**Out of scope**
- A stored, precomputed display table (report option F). Held as the decision to revisit if this spec does not reach its targets (Q1).
- Showing fewer items on Home, longer caching, warming the cache after ingestion, or new indexes (options G, H, I).
- The library list's remaining linear cost of building every item before keeping 50 (report cause 5).
- The other screens that read the whole-library credit list on their own (people, search, series browse, artwork library and about 25 others). They keep using it unchanged.
- The "is this item still updating" check on Home cards (an unmeasured risk in the report). It stays per item, as today.
- Any change to what Home, detail, recent pages or the list show, their order, filters, profile visibility rules, or wire shapes.

## 3. Decisions made

- **D1. Same values, faster.** Every field of every row stays identical to today, including the quiet rules nobody wrote down. Example: where a field accepts several stored names (a small cover may be stored as a "cover", "poster" or "episode still" small image), today the data store picks the alphabetically first name that is present, not the first one written in the code. The rewrite keeps today's actual result. Reviewers reject any change in output that the comparison (D6) does not explain.
- **D2. Pick the items first, then read their values once.** The read decides which items it returns (the most recently added 1,000 for Home; the item, its series mates and its credited items for detail; the chosen shows, albums and works for recent pages) before it reads any display value. It then reads every stored value it needs for the files, works, shows, albums, editions and series of those items in one pass, and chooses each field from that. It stays one read in the data store, so the benchmark's comparison copy keeps reading the product's own text.
- **D3. Credits for the returned items only.** A new building block works out credits exactly as the shared credit list does today (show or album first, then the work, then the file; stored credits first, provider claims as the fallback) but for a given set of files only. Home, detail and recent pages use it for the items they return. The shared whole-library credit list stays for the other screens. A test proves the new block gives exactly the same rows as the shared list for every file, so the two cannot drift apart unnoticed.
- **D4. Person detail finds the person's items first.** A person's page shows the items that credit them. The read first finds which files credit that person (one pass over credits, proportional to the library), then builds full credits only for those files.
- **D5. The audiobook year is worked out once per read.** The read first lists its audiobooks that have no year of their own, then the books whose title matches one of them (ignoring case and surrounding spaces), then compares their authors using credits for just those files (D3). The matching rule does not change: same author, same title, a book and not an audiobook, the lowest work id and then file id when several match. The library list, Home and detail all use this.
- **D6. One before-and-after comparison.** The benchmark tool gains a command that writes every field of every row of Home, the "all works" read, sampled detail reads (a book, an audiobook with and without its own year, a TV episode, a music track, a movie, a person, an item with series mates), recent pages and the full library list to one text file. It runs on the benchmark library before the first change and after each wave; the files must match. The only allowed difference is D7.
- **D7. Items added at the same moment keep a fixed order.** Today, when two items have exactly the same "added" time, the data store returns them in whichever order its plan happens to produce, which can change when the read changes. The rewrite adds a fixed tie-break (by work, then library) so Home is stable from one visit to the next. The comparison treats rows with the same added time as a group: same rows, same values, order within the group free; at Home's 1,000-item cut-off it reports any tied rows that moved in or out.
- **D8. Count work, not seconds.** As in the library list spec: guardrails count the operations the data store performs, which is the same on every run. Each has a budget at 4,000 works (about 1.5 times the measured fixed cost, with the measured number written beside it) and a growth check that 8,000 works costs at most 2.5 times as much. They use the library list's guardrail library, extended with people, matching books and audiobooks, and audiobooks without a year.
- **D9. Targets.** Home at 4,000 works under 1 s of processor time (report: about 6.8–7.5 s today; the one-pass read of every value for every item took 0.15 s). Detail under 0.2 s for one item (about 0.9 s of processor time today, mostly the credit lists). These are estimates from the report's measurements, not yet built; the guardrails lock in whatever the fixed code measures. If Home cannot get under 1 s with D2 to D5, the builder stops and reports, and Q1's option (b) comes back to Shy.

## 4. UI behaviour

Nothing changes on screen except speed.

| Situation | What the person sees |
|---|---|
| Normal | Home's shelves appear in under a second at 4,000 works on a fresh visit (was about 7 s); an item's page loads its items in well under half a second (was 2–5 s). Revisits within 30 seconds are already fast and stay so. |
| Empty (nothing to show yet) | Unchanged. |
| Loading | Unchanged, just shorter. |
| Error (Engine unreachable, not allowed, not found) | Unchanged. Profile limits (which libraries, which media types, which content ratings) hide exactly the same items as today. |
| Partly ready (enrichment still running, provider down) | Unchanged. Items that are not Ready stay hidden; the "updating details" marker shows on the same items. |
| Items added at exactly the same time | Always the same order (D7); today their order can vary between visits. |

## 5. Data / model changes

None. No new stored information, no data store upgrade step, no reset, no re-ingest. The benchmark library (a test tool, not part of the product) gains audiobooks without their own year so the comparison covers the year look-up.

## 6. Work packets

Waves (at most two packets at once, each in its own worktree; WP3, WP4 and WP5 all change the Home read, so they go one after another):
- **Wave 1:** WP1 and WP2.
- **Wave 2:** WP3, and WP7 if Q2 is answered "include".
- **Wave 3:** WP4.
- **Wave 4:** WP5.
- **Wave 5:** WP6.
Then the reviewer and the full restore/build/test.

Baseline: after WP1 merges, run its `snapshot` command on `main` (seed `--scale projection`) and keep the file in `.tmp/` as the "before" for every later wave.

### WP1 — Before-and-after snapshot command, and audiobooks without a year in the benchmark library
- **What it does:**
  - Add a `snapshot --db <path> --out <file>` command to `benchmarks/MediaEngine.Storage.Benchmarks` (D6). It calls production code only: `DisplayWorkProjectionReader.LoadAsync` with `limit: 1000` (Home), with no limit (all works), with `detailId` for a fixed sample of ids (pick by deterministic stride: a book, an audiobook with its own year, an audiobook without one that matches a book, a TV episode, a music track, a movie, a person, a work that has series mates), with a `DisplayRecentGroupFilter` for a fixed sample of roots and works and `lean: true`; `LoadTvEpisodeSkeletonAsync` for a sample of shows (internal: read by reflection as `Q5` does for `BuildSql`); `LibraryItemRepository.GetPageAsync` with `IncludeAll`, `Limit 5000`; and the library item detail read for the same sampled works. Each section writes one line per row: every public property of the row type in declaration order, `name=value`, invariant culture, dates as `o`, nulls as `<null>`, Guids in `N` format. Rows with equal `CreatedAt` are sorted by `WorkId`, `LibraryId`, `AssetId` inside their group before writing (D7). End the file with a SHA-256 of everything above it.
  - Add a `compare --before <file> --after <file>` command that reports the first differing lines per section, and for Home lists any rows tied on `CreatedAt` at the 1,000-row cut-off that moved in or out.
  - In `Seeding/DataSeeder.cs`, stop giving every audiobook its own original year: half keep it; of the other half, some share a normalised title (`'  Title '` vs `'title'`) and an author with a seeded book, some match a book by title but not by author, and some match nothing. Keep it deterministic.
  - `benchmarks/README.md`: document both commands and the audiobook change.
- **Files likely touched:** `benchmarks/MediaEngine.Storage.Benchmarks/Program.cs`, `benchmarks/MediaEngine.Storage.Benchmarks/Verification/Snapshot.cs` (new), `benchmarks/MediaEngine.Storage.Benchmarks/Seeding/DataSeeder.cs`, `benchmarks/README.md`
- **Depends on:** none
- **Size:** Standard
- **Done when:** two `snapshot` runs on the same seeded file produce identical files; `compare` of a file against a copy with one value edited reports that line; after `seed --scale projection`, at least 100 audiobooks have no original year of their own and at least 20 of them borrow a book's year in the snapshot's library list section.

### WP2 — Credits for a given set of files
- **What it does:** add `PersonCreditSql` in `src/MediaEngine.Storage` with a method that returns the text of a CTE chain producing exactly the columns and rows of the `primary_person_media_credits` view (`schema.snapshot.sql`), but whose `asset_scope` starts from a caller-supplied file-id relation (a CTE name or `IN @assetIds`) instead of every media asset (D3). Keep every rule of the view: scope priority (root 0, work 1, file 2) with `MIN` per file and credit key, stored arrays before claims, the same credit keys, trimming and blank filtering, and whatever the view does after `selected_claims`. Do not change the view.
  - Tests in `tests/MediaEngine.Storage.Tests/PersonCreditSqlTests.cs` (new): on a fixture with credits on the show, season, episode and file, on the album and track, claims-only credits, a credit present at two levels, blank values and a QID, the scoped block given every file returns exactly the view's rows (same multiset, every column); given a subset, it returns exactly the view's rows for that subset.
- **Files likely touched:** `src/MediaEngine.Storage/PersonCreditSql.cs` (new), `tests/MediaEngine.Storage.Tests/PersonCreditSqlTests.cs` (new)
- **Depends on:** none
- **Size:** Standard
- **Done when:** the new tests pass, and `EXPLAIN QUERY PLAN` of the scoped block for 10 files shows no full scan of `canonical_value_arrays` or `metadata_claims`.

### WP3 — Audiobook year worked out once per read
- **What it does:** replace `MediaDateSql.RelatedBookOriginalYear`'s per-audiobook search with a set-based CTE (D5): `related_book_years(audiobook_asset_id, year)` built from the read's own candidate audiobooks that have no explicit year, the books whose normalised title (`LOWER(TRIM(COALESCE(asset title, work title)))`) equals one of theirs, and author credits for only those files from WP2. The per-row expression becomes a keyed look-up into that CTE. Same matching rule and same tie order (`ORDER BY book_work.id, book_asset.id`), same year rule (`OriginalYearValue` on the book work, then the book file). Update the three callers: `LibraryItemRepository.BuildProjectionSql` (list), the library item detail read (`@workId`/`@assetId`), and `DisplayWorkProjectionReader.BuildSql` (keep `LoadTvEpisodeSkeletonAsync` reading no credits). Remove `contributorRelation` parameters that no longer have a use.
  - In `tests/MediaEngine.Performance.Tests/LibraryListFixture.cs`, remove the "audiobooks carry their own original year" workaround and its comment: half the audiobooks lose their year, and some of those get a matching book (title and author). Re-measure the four library list budgets and update the constants and their comments in `LibraryListGuardrailTests.cs`.
  - Tests (behaviour sentences) in `tests/MediaEngine.Storage.Tests/`: an audiobook without a year borrows the matching book's year; matching ignores case and surrounding spaces; a book with the same title but a different author is not used; an audiobook with its own explicit year keeps it; two matching books → the lower work id wins; a matching audiobook is never used as the source.
- **Files likely touched:** `src/MediaEngine.Storage/MediaDateSql.cs`, `src/MediaEngine.Storage/LibraryItemRepository.cs`, `src/MediaEngine.Api/Services/Display/DisplayWorkProjectionReader.cs`, `tests/MediaEngine.Performance.Tests/LibraryListFixture.cs`, `tests/MediaEngine.Performance.Tests/LibraryListGuardrailTests.cs`, `tests/MediaEngine.Storage.Tests/MediaDateSqlTests.cs` (new; no direct tests exist today)
- **Depends on:** WP1 (baseline), WP2
- **Size:** Complex
- **Done when:** the new tests pass; the four library list guardrails pass on the fixture without the workaround, with the growth ratios at or below 2.5 (record them); with the old look-up put back locally, the unfiltered growth check fails (record the number, do not commit); `compare` against the baseline reports no differences.

### WP4 — Home and detail build credits only for the rows they return
- **What it does:** in `DisplayWorkProjectionReader.BuildSql`, move the row choice ahead of the credits (D2, D3): a `chosen` CTE (`ranked_assets` filtered to `AssetRank = 1`, ordered by `CreatedAt DESC, WorkId, LibraryId` (D7), `LIMIT @limit`), then `projection_credits` from WP2 scoped to `chosen`'s asset ids, then `canonical_artist_credits` from that. For person detail (D4), the detail predicate first finds the files crediting `@detailId` (by name, case-insensitive, or QID, as today) in one pass, and `ranked_assets` uses that set instead of `EXISTS` over the whole-library credits. The final `SELECT` reads from `chosen`. Search-only/lean handling, `@rootIds`/`@workIds`, and every column stay as they are.
- **Files likely touched:** `src/MediaEngine.Api/Services/Display/DisplayWorkProjectionReader.cs`, `tests/MediaEngine.Api.Tests/DisplayWorkProjectionReaderTests.cs`
- **Depends on:** WP3 (same file)
- **Size:** Standard
- **Done when:** existing `DisplayWorkProjectionReaderTests` pass, plus new tests: a person's detail returns exactly the items that credit them by name or by QID; a music item's `ArtistPersonId` is the same person as before when two persons share the artist's name; `compare` against the baseline reports no differences except D7 ties; the detail read for one audiobook on the benchmark library does at most a tenth of the operations it does on `main` (record both).

### WP5 — Home reads each chosen item's values in one pass
- **What it does:** replace the per-column correlated sub-selects in `BuildSql` with pivots over the chosen rows (D2):
  - `chosen_entities`: the distinct `AssetId`, `WorkId`, `RootWorkId`, `EditionId` and `CollectionId` of `chosen`.
  - `value_pivot`: one row per entity from a single pass over `canonical_values` for those ids and only the keys the projection reads (`GROUP BY entity_id` with `MAX(CASE WHEN key = '…' THEN value END)` per key).
  - `array_pivot`: one row per entity over `canonical_value_arrays` for those ids, with `group_concat` per key group in `ordinal` order, same separators (`'; '` or `';'`) as today.
  - The final `SELECT` joins `chosen` to the pivots for asset, work, root, edition and collection and rebuilds each column with the same `COALESCE` order. Where a column lists several keys (`key IN (…) LIMIT 1`), choose among them in alphabetical key order, which is today's actual behaviour (D1); where a column orders by `ordinal` across several keys, keep today's tie order. Year uses `MediaDateSql.OriginalYearFromGroupedRows` / `ExplicitOriginalYearFromGroupedRows` on the pivots plus WP3's look-up. `display_overrides_json`, collections, `series_manifest_hydrations` and `IsUpdatingDetails` may stay per row (they are single keyed reads), but read `works` once per row, not five times.
  - The search-only markers must still wrap exactly the search-only columns, so `lean` still blanks them.
- **Files likely touched:** `src/MediaEngine.Api/Services/Display/DisplayWorkProjectionReader.cs`, `tests/MediaEngine.Api.Tests/DisplayWorkProjectionReaderTests.cs`
- **Depends on:** WP4 (same file), WP1 (comparison)
- **Size:** Complex
- **Done when:** existing reader tests pass, plus new tests for: a TV episode taking its cover from the show when its file and work have none; a field with several stored names returning the same one as before; genres and authors joined in `ordinal` order; a lean read returning `NULL` search-only columns; `compare` against the baseline reports no differences except D7 ties; Home (`limit 1000`) on the benchmark library does at most a fifth of the operations it does on `main` and under 1 s of processor time (record both; if not reached, stop and report per D9).

### WP6 — Home and detail guardrails
- **What it does:** extend `LibraryListFixture` (or add `DisplayReadFixture` beside it if sizes must differ) with `persons` rows for a share of the credited names (some sharing a name, some with QIDs), and series membership for some works. Add `DisplayReadGuardrailTests` (`[Trait("Category", "Performance")]`) calling the production `DisplayWorkProjectionReader.LoadAsync` through `SqliteWorkCounter`:
  - `Home_ThousandItemsStayWithinBudgetAtFourThousandWorks`, `Home_GrowsInProportionToTheLibrary`
  - `Detail_OneAudiobookStaysWithinBudgetAtFourThousandWorks`, `Detail_OneAudiobookGrowsInProportionToTheLibrary`
  - `Detail_OnePersonStaysWithinBudgetAtFourThousandWorks`, `Detail_OnePersonGrowsInProportionToTheLibrary`
  - Budgets per D8, constants with measured values in comments, failure messages in the style of `LibraryListGuardrailTests`.
- **Files likely touched:** `tests/MediaEngine.Performance.Tests/LibraryListFixture.cs`, `tests/MediaEngine.Performance.Tests/DisplayReadGuardrailTests.cs` (new)
- **Depends on:** WP5 (budgets measured on the fixed code)
- **Size:** Standard
- **Done when:** the six tests pass; with WP4's or WP5's change reverted locally, at least the Home budget and the audiobook-detail budget fail (record the numbers, do not commit); the project's full test run stays under two minutes.

### WP7 — Light "visible works" read (only if Q2 is answered "include")
- **What it does:** add `LoadVisibleWorkIdsAsync(CancellationToken)` to `IDisplayProjectionReadService`, implemented in `DisplayProjectionReadService` (cached 30 s like the others) by a new light reader (`DisplayVisibleWorkReader`, its own file) that returns `WorkId`, `LibraryId`, `MediaType` and `ContentRating` for exactly the rows `LoadAsync()` with no limit returns (same Ready rule, visible path rule, `AssetRank = 1` choice and `ContentRating` fallback order). `AuthorizedDisplayProjectionReadService` filters it with the same `Allows(LibraryId, MediaType, ContentRating, scope)` rule and returns the id set. Switch every caller whose only use of `LoadWorksAsync` is `.Select(work => work.WorkId).ToHashSet()` (about 20 in `CollectionEndpoints.cs`, plus `CollectionClientEndpoints.cs` and the Home collections filter in `AuthorizedDisplayProjectionReadService`); leave callers that read other fields alone and list them in the packet report.
  - Tests: for profiles limited by library, by media type and by content rating, and for the owner, the new id set equals the set derived from `LoadWorksAsync` on the same fixture. A guardrail `VisibleWorks_StaysWithinBudgetAtFourThousandWorks` plus its growth check.
- **Files likely touched:** `src/MediaEngine.Api/Services/Display/DisplayVisibleWorkReader.cs` (new), `src/MediaEngine.Api/Services/Display/IDisplayProjectionReadService.cs`, `src/MediaEngine.Api/Services/Display/DisplayProjectionReadService.cs`, `src/MediaEngine.Api/Security/AuthorizedDisplayProjectionReadService.cs`, `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs`, `src/MediaEngine.Api/Endpoints/CollectionClientEndpoints.cs`, tests in `tests/MediaEngine.Api.Tests/` and `tests/MediaEngine.Performance.Tests/`
- **Depends on:** none (separate files; scheduled in wave 2)
- **Size:** Standard
- **Done when:** the equality tests pass for every profile shape; the guardrails pass; no switched caller reads any field but `WorkId`; the light read takes under a tenth of the operations of the full read on the benchmark library (record both).

## 7. Risks and open questions

- **Risks:**
  - *A field changes value.* The one-pass rewrite rebuilds every display field (about 70 stored value names) with its fallbacks; one wrong order would show the wrong cover, title or year. The full snapshot comparison (D6) over the benchmark library, after every wave, is the main protection; the reviewer checks each column's fallback order against today's.
  - *The two credit definitions drift.* The scoped credits (D3) repeat the shared list's rules. WP2's equality test fails the build if they ever differ; a future change to the shared list must change both, and that test says so.
  - *Profile visibility changes (WP7).* Showing an item a profile should not see would be a privacy defect. WP7's equality tests across profile shapes cover it, and the reviewer treats it as high-risk.
  - *The target is not reached.* The 1-second figure is estimated from the report, not built. D9 makes the builder stop and report rather than add anything new.
  - *Benchmark numbers move.* Making some benchmark audiobooks lose their year changes the benchmark library; the README records it so old and new numbers are not compared directly.
- **Observation (not part of this spec):** where a field accepts several stored names, today's choice is alphabetical rather than the order written in the code (for example, a small episode still beats a small poster). This spec keeps today's results (D1). Whether the written order was the intent is a separate product question; I'll propose it as a follow-up once this is built.
- **Open questions:**
  - **Q1. Start with this spec (D + E), or go straight to a stored display table (option F)?** (a) This spec: removes the measured causes inside today's read, with no new stored information that can go out of date. (b) A stored table with one ready-made row per item, refreshed whenever an item's values, artwork or credits change: the fastest at any size, but every write path in the Engine must remember to refresh it, it needs a rebuild command and a consistency check, and it deserves its own architecture decision. **Recommendation: (a)**, as the report recommends. Option F stays available if 100,000-work measurements later show it is needed.
  - **Q2. Include the light "visible works" read (WP7) now, or defer it?** About 45 places read every visible work's full display row; at least 20 of them only need the list of work ids the person may see. After this spec the full read gets cheaper too, but still builds every row for 4,000 works whenever a series or collection screen misses the 30-second cache. (a) Include WP7: those screens read only ids, kinds and ratings, with the visibility rule tested to match exactly. (b) Defer to its own spec. **Recommendation: (a)**, since the "best long-term fix before beta" rule applies and it runs beside wave 2 without touching the same files.
