# Spec: Library list speed

> **Status:** In progress (locked by Shy 2026-10-19, with Q1 answered yes) · **Version:** 2 · **Last updated:** 2026-10-19 · **Related docs:** [Product overview](../product/overview.md) · [Report: Slow library and Home reads at 4,000 works](../reports/slow-reads-2026-10.md) (causes 1–2, fix options A, B, C) · [Benchmarks README](../../benchmarks/README.md)

## 1. Goal

The library list opens in about 1–2 seconds at 4,000 works instead of 13–22 seconds, and stays proportional as the library grows: twice the works, about twice the time, not four times. Guardrail tests stop the slow pattern from coming back.

## 2. Scope

**In scope**
- The library list (every filter, sort and page): the seven credit look-ups (author, artist, narrator, genre, top cast, director from the show or album, director from the file) find their target first, so each one reads only that item's credits (report option A).
- The list returns its total and its page from one read instead of two full reads (option B).
- Guardrail tests that measure the list's work at 4,000 and 8,000 works, unfiltered and for one media type (option C).
- The benchmark seeder marks its items Ready, so Home and detail benchmarks measure real rows again.
- Remove a duplicate index on the stored-values table (D8).

**Out of scope**
- Home and the item detail page (report causes 3 and 4, options D and E). These get their own spec next, with their own guardrails.
- The uncapped "all works" read used by series, people and search screens.
- Building only the shown page instead of the whole library before keeping 50 (report cause 5). After this spec that cost grows in proportion to the library, about 1 s at 4,000 works.
- A stored, precomputed display table (option F), shorter Home windows, longer caching, or new indexes.
- Any change to what the list shows, its filters, sorts, paging or wire shapes.

## 3. Decisions made

- **D1. Same rows, faster.** The rewrite changes how the look-ups find their rows, not what they return. Every field on every row stays identical, which the report verified with a hash over all 4,000 rows. Reviewers reject any change in output.
- **D2. Find the target first.** Each credit look-up first works out the one id it needs (the show or album for an episode or track, otherwise the work itself; or the item's main file for the "director from the file" fallback), then reads only that id's credits. This lets the data store use its existing index on (item, kind of credit). Nine places change: the non-comic author look-up has three parts (count, first, second), plus artist, narrator, genre, top cast and the two director look-ups. The comic author look-up already reads by the work's own files and stays as it is.
- **D3. One read for total and page.** The list asks for the page and the total in the same read, so the two can never disagree. When a page past the end comes back empty, the list asks for the total separately, so "page 9 of 8" still reports the right total. This extra count is only for that rare case.
- **D4. Count work, not seconds.** Guardrails measure the data store's own count of the operations it performed (the same work counter the existing large-library guardrails use). That count is the same on every run, so the tests do not fail just because the machine is busy. Each guardrail has two checks: a fixed budget at 4,000 works, and a growth check that 8,000 works costs at most 2.5 times 4,000 works. The growth check is what catches a cost that grows with the square of the library.
- **D5. Guardrails use their own small fixture.** The test project seeds its own library shape (books, audiobooks, movies, TV shows with seasons and episodes, albums with tracks, credits on shows, albums and files, all marked Ready) rather than depending on the benchmark tool. Budgets are set from the fixed code's measured cost on that fixture, with about 50% headroom, and the measured number is written beside the budget.
- **D6. The benchmark seeder marks items Ready**, matching a fully processed real library. Without it, Home and detail return nothing on seeded data and look healthy when they are not.
- **D7. Other readers that share the list's projection benefit automatically.** The same building block also feeds the library item detail read and the library overview counts. They get the faster look-ups with no other change.
- **D8. Remove the duplicate index (Q1, Shy 2026-10-19).** The index that repeats the stored-values table's main key is removed in this spec (WP4).

## 4. UI behaviour

Nothing changes on screen except speed.

| Situation | What the person sees |
|---|---|
| Normal | The library list and its total appear in about 1–2 s at 4,000 works (was 13–22 s); one media type in about 0.2–0.5 s (was 1.4–2.7 s). |
| Empty (nothing to show yet) | Unchanged: the empty list and a total of 0. |
| Loading | Unchanged, just shorter. |
| Error (Engine unreachable, not allowed, not found) | Unchanged. |
| Partly ready (enrichment still running, provider down) | Unchanged. Items that are not Ready stay hidden exactly as today. |
| Page past the end | Unchanged: no items, with the correct total. |

## 5. Data / model changes

None for the list fixes: no new stored information, no reset, no re-ingest.

One data store upgrade step (D8) removes an index that exactly repeats the main key of the stored-values table. Existing libraries get it automatically at the next start. Nothing is lost; writes get a little cheaper and the data store file a little smaller.

## 6. Work packets

Wave 1: WP1 and WP3 together. Wave 2: WP2 and WP4 together. Then the reviewer and the full restore/build/test.

### WP1 — Credit look-ups and one-read paging
- **What it does:**
  - In `LibraryItemRepository.BuildProjectionSql` (`work_data` CTE), rewrite the nine credit sub-reads to resolve the target id as a scalar first (D2):
    - non-comic `author` (`cva0`, `cva1`, `cva2`): drop the `LEFT JOIN works pw*/gpw*` and use `cvaN.entity_id = (SELECT r.root_work_id FROM root_work_data r WHERE r.work_id = w.id)`;
    - `artist`, `narrator`, `genre`, `director` (root): drop `INNER JOIN root_work_data rwd_*` and use the same scalar;
    - `top_cast`: same as author;
    - `director` (asset fallback): drop `INNER JOIN primary_asset_data pad_director` and use `cva_director_asset.entity_id = (SELECT pad.asset_id FROM primary_asset_data pad WHERE pad.work_id = w.id)`.
    - Keep every `key` filter, `ORDER BY`, `LIMIT`/`OFFSET`, `GROUP_CONCAT` separator and the `COALESCE` order exactly as they are. The comic author branches (`e0/e1/e2`, `mc_author`) do not change.
  - In `GetPageAsync`, remove the separate count statement. Add `COUNT(*) OVER () AS total_count` to the page `SELECT` (after the existing 48 columns, so ordinals 0–47 stay put) and read it from the first row. If the page is empty and `query.Offset > 0`, run the existing `SELECT COUNT(*) FROM full_data fd {whereClause}` once for the total (D3). If the page is empty at offset 0, the total is 0. `HasMore` keeps its current rule.
  - Tests in `tests/MediaEngine.Storage.Tests/LibraryItemProjectionTests.cs` (new, behaviour-sentence names), with fixtures where the credit sits on the root and decoys sit on other works:
    - a TV episode (show → season → episode) shows the show's director and first three cast members; a decoy show's credits do not appear;
    - a music track shows its album's `album_artist` in preference to `artist`/`performer`;
    - an audiobook shows its narrator and its genres joined with `; `;
    - a movie with a director only on its main file falls back to that director;
    - a book with three authors shows `A & B + 1 more`;
    - the page total equals the number of matching rows on every page, including a filtered page, and a page past the end returns no items with the correct total.
- **Files likely touched:** `src/MediaEngine.Storage/LibraryItemRepository.cs`, `tests/MediaEngine.Storage.Tests/LibraryItemProjectionTests.cs`
- **Depends on:** none
- **Size:** Standard
- **Done when:** the new tests and all existing `LibraryItemProjectionTests` pass; `EXPLAIN QUERY PLAN` of the unfiltered page shows each rewritten look-up as `SEARCH … USING INDEX sqlite_autoindex_canonical_value_arrays_1 (entity_id=? AND key=?)` and none of them as `(key=?)` alone; on a benchmark dataset (`seed --scale projection`, Ready copy) `GetPageAsync` with `IncludeAll`, `Limit 5000` returns rows identical field-for-field to `main` before the change (hash compare, as the report did), with the same total.

### WP2 — Library list guardrails
- **What it does:**
  - Add `SeedLibraryListShape(int leafWorkCount)` to `tests/MediaEngine.Performance.Tests/LargeLibraryFixture.cs` (set-based SQL like the existing seed methods). Per D5: a mix of books, audiobooks, movies, TV (show → season → episode) and music (album → track); one edition and one media asset per leaf work; `canonical_values` for title and cover on assets and works; `canonical_value_arrays` credits (author, narrator, genre, cast_member, director, album_artist) on root works, with some directors only on assets; one metadata claim per asset so the list's first-claim date exists; `works.ready_at` set on every work. Each guardrail size uses its own fixture database, so the 4,000 and 8,000 runs do not share rows.
  - Add `LibraryListGuardrailTests` (`[Trait("Category", "Performance")]`), calling the production `LibraryItemRepository.GetPageAsync` through `SqliteWorkCounter`:
    - `LibraryList_UnfilteredFirstPageStaysWithinBudgetAtFourThousandWorks`
    - `LibraryList_UnfilteredFirstPageGrowsInProportionToTheLibrary` (8,000 ÷ 4,000 ≤ 2.5)
    - `LibraryList_OneMediaTypeFirstPageStaysWithinBudgetAtFourThousandWorks`
    - `LibraryList_OneMediaTypeFirstPageGrowsInProportionToTheLibrary`
  - Budgets are constants beside the measured value (D5), in the style of `DeepPageWorkBudgetKiloInstructions`; failure messages state measured and budget numbers.
- **Files likely touched:** `tests/MediaEngine.Performance.Tests/LargeLibraryFixture.cs`, `tests/MediaEngine.Performance.Tests/LibraryListGuardrailTests.cs` (new)
- **Depends on:** WP1 (budgets are measured on the fixed code)
- **Size:** Standard
- **Done when:** the four tests pass on the fixed code; with WP1's credit rewrite temporarily reverted locally, both growth tests fail (record the reverted numbers in the packet report, do not commit the revert); the project's full test run stays under two minutes.

### WP3 — Benchmark seeder marks items Ready
- **What it does:** `benchmarks/MediaEngine.Storage.Benchmarks/Seeding/DataSeeder.cs` sets `works.ready_at` (a fixed timestamp, e.g. `2026-01-01T00:00:00Z`) on every work it creates, then the seeder's existing `ANALYZE` (add one if it has none) runs after. Update `benchmarks/README.md`: note that seeded items are Ready, and mark the sizing table as measured before the Ready gate and before this spec.
- **Files likely touched:** `benchmarks/MediaEngine.Storage.Benchmarks/Seeding/DataSeeder.cs`, `benchmarks/README.md`
- **Depends on:** none
- **Size:** Mechanical
- **Done when:** after `seed --scale projection`, `SELECT COUNT(*) FROM works WHERE ready_at IS NULL` returns 0, and the harness's Home probe returns rows (not 0).

### WP4 — Drop the duplicate index (D8)
- **What it does:** add upgrade step `0013_drop_duplicate_canonical_values_index.sql` containing `DROP INDEX IF EXISTS idx_canonical_values_entity_key;`; remove the index from `schema.snapshot.sql`; register the step wherever the upgrade guardrails require (`released-steps.txt` and `DataStoreUpgradeCatalog` per the numbered-upgrade rules). No epoch bump: removing an index needs no reset.
- **Files likely touched:** `src/MediaEngine.Storage/Schema/Upgrades/0013_drop_duplicate_canonical_values_index.sql` (new), `src/MediaEngine.Storage/Schema/schema.snapshot.sql`, `src/MediaEngine.Storage/Schema/Upgrades/released-steps.txt`, `docs/reference/database-schema.md` if it lists the index
- **Depends on:** none (scheduled in wave 2 to keep wave 1 to two packets)
- **Size:** Mechanical
- **Done when:** the data store upgrade and schema guardrail tests pass, and a fresh data store has no `idx_canonical_values_entity_key` while `canonical_values` reads still use the primary key index.

## 7. Risks and open questions

- **Risks:**
  - *A field changes value.* A look-up rewritten slightly wrong would show the wrong author or director, or none. WP1's hash compare over all 4,000 benchmark rows and the new per-media-type tests catch this; the reviewer checks that only the `FROM`/`WHERE` of each look-up changed.
  - *Totals drift on edge pages.* D3's fallback count covers the empty-page case; WP1 tests it.
  - *Budgets too tight or too loose.* Too tight fails on harmless changes; too loose misses a slow-down. The growth check (D4) is the main protection and does not depend on the exact budget.
  - *Expected speed is measured on a loaded desktop.* The 1–2 s figure is from the report's processor-time numbers; the guardrails track operation counts, which do not vary.
- **Open questions:** none open.
  - **Q1 (answered: yes, see D8). Remove the duplicate index now?** One index on the stored-values table repeats that table's main key, so it costs write time and disk space and helps no read. Options: (a) remove it in this spec as WP4, a small data store upgrade that existing libraries apply at next start with no reset; (b) leave it for a later tidy-up. **Recommendation: (a)**: it is low risk, measured as unused, and fits the "best long-term fix before beta" rule.

## 8. Gaps and drift

- **Built and matches the spec:** WP1 to WP4. On the benchmark library (4,000 works, all Ready) every row, total, sampled detail page and overview count is byte-identical before and after (one SHA-256 over 4,451 lines of output). Measured through the production code on a busy desktop: unfiltered first page 22.5 s → 1.1 s, all 4,000 rows 27.4 s → 1.3 s, Movies 3.2 s → 0.5 s. On the guardrail library the unfiltered first page does 12.8 million operations (was 2,865 million) and grows exactly 2.00× when the library doubles (was 3.98×). With the old look-ups put back, all four guardrails fail.
- **Built differently (what changed):** the guardrail library lives in its own fixture (`LibraryListFixture`) rather than as a seed method on the existing large-library fixture, because each size needs its own data store. The benchmark's Entity Framework comparison copy of the list read was updated to the same one-read paging, so the comparison stays like for like.
- **Found while building, not in scope:** an audiobook with no original year of its own borrows the matching book's year through a look-up that reads the whole-library credits list for each such audiobook. That cost still grows with the square of the library (3.9× per doubling on the guardrail library). It is shared with Home and detail, so it belongs with the Home speed work. The guardrail library gives its audiobooks their own year, as the benchmark library does, and says so beside the budgets.
