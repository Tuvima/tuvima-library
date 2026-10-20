---
title: "Slow library and Home reads at 4,000 works"
summary: "Why the unfiltered library list takes about 14 seconds and uncached Home about 6 seconds at 4,000 works, with measured causes, fix options and a recommendation."
audience: "product-owner, developer"
category: "report"
product_area: "engine"
---

# Slow library and Home reads at 4,000 works

October 19, 2026. Investigation only: no application code, configuration or data structure was changed. Measured on `main` at `6aa54565f` with the benchmark harness (`benchmarks/MediaEngine.Storage.Benchmarks`) and the performance probe (`tools/MediaEngine.PerformanceProbe`), both restored to CI in `481fce1`. Experiments ran on scratch copies of the seeded data outside the repository.

## Summary

| Screen | Today at 4,000 works | Main cause | Measured with the first fix |
|---|---:|---|---:|
| Library list, unfiltered (count + first page of 50) | 13–22 s | Seven per-item credit look-ups each scan **every** credit of that kind in the library, so the cost grows with the square of the library size; and the whole read runs twice (once to count, once for the page) | about 1–2 s (one statement, 14× fewer steps; identical rows) |
| Library list, one media type (Movies) | 1.4–2.7 s | Same two causes on a smaller set | about 0.2–0.5 s |
| Home, uncached (1,000 items) | 6.4–6.7 s | About 280 separate small look-ups per item × 1,000 items, plus two whole-library credit lists rebuilt on every call | not prototyped; the one-pass alternative reads every value for every item in 0.15 s |
| Item detail, uncached | 2–5 s for one item | The same two whole-library credit lists rebuilt for a single item | — |

1. **The library list has a query defect, not a size problem.** The credit look-ups for author, artist, narrator, genre, top cast and director (twice) find a TV episode's show or a track's album through joins written in an order that stops the data store from using its index. Each look-up then walks every credit of that kind for every item. Rewriting those seven look-ups to find the show or album first gave byte-identical results with 14× fewer steps. At 8,000 works the current count takes 36 s of processor time against 1.9 s after the rewrite.
2. **The library list does the whole job twice.** The count and the page are separate statements over the same 2,850-line read. Returning the total with the page in one statement halved the work again.
3. **Home is slow because of how many look-ups it does, not because any one is slow.** Every one of its 1,451 sub-reads uses an index, so there is no missing index to add. Each item costs 5–6 ms of processor time because each attribute (title, cover, sizes, colours and so on) is fetched separately. Home asks for 1,000 items.
4. **Home and detail pages rebuild two whole-library credit lists on every call** (about 0.9 s at 4,000 works). This fixed cost dominates a single-item detail read.

**Recommendation:** fix the library list now with a small, low-risk change (causes 1 and 2) plus guardrail tests. Then write a spec for Home that replaces the per-attribute look-ups with one pass per item and limits the credit lists to the items being shown. Keep a stored, precomputed display table as the fallback if that does not reach the target. Details under [Recommendation](#recommendation).

## How it was measured

* **Data:** `seed --scale projection` (4,000 leaf works, 100 parent works, 41,017 canonical values, 7,794 credit-array rows, 20,000 claims; layout in `benchmarks/README.md`). Also `--works 2000` and `--works 8000` to measure growth.
* **Harness gap (important):** the seeder never sets `works.ready_at`. Since `711cfae69` ("Show items only once they are Ready") Home and detail return **0 rows** on seeded data. Home then times at 0.1–0.4 s and looks healthy when it is not. All Home numbers here use a scratch copy with `UPDATE works SET ready_at = '2026-01-01T00:00:00Z' WHERE ready_at IS NULL; ANALYZE;` applied, which matches a fully processed real library. The library list counts 3,853 of 4,000 items as visible.
* **Machine:** AMD Ryzen 7 9800X3D, 31 GB, Windows 11. Other applications kept the processor at 67–96% busy throughout (video encoder, browsers, audio tools), so wall-clock times varied 2–5× between identical runs. Comparisons therefore use two measures that do not depend on load. **VM steps** is SQLite's own count of virtual-machine operations for a statement (`SQLITE_STMTSTATUS_VM_STEP`), and it is identical on every run. **CPU time** is the processor time the statement used.
* **SQL under test:** the exact production text. `LibraryItemRepository.BuildProjectionSql(false)` and `DisplayWorkProjectionReader.BuildSql(false, false, false)` were read by reflection, as the harness does, through the production `DatabaseConnection` and its pragmas. The list's count and page statements are the ones `GetPageAsync` builds for "no filter" and for `Movies`.
* **Stage timings:** each CTE in the chain was run on its own (`… SELECT * FROM <cte>`). Columns were then removed one at a time to find the cost.
* **Result checks:** every rewrite was compared with the original by a SHA-256 hash over all returned rows. All matched.

## Reproduction with the restored tools

Benchmark `probe` (production Dapper code, single runs):

| Data | Library list, no filter | Library list, Movies | Home (limit 1,000) |
|---|---:|---:|---:|
| Seed as generated (Home returns 0 rows) | 15.3 / 21.9 / 13.3 s | 1.9 / 2.0 / 3.6 s | 0.43 / 0.11 / 0.17 s |
| Seed with items Ready | 19.4 / 21.0 / 16.1 s | 1.5 / 1.4 / 1.6 s | 6.7 / 6.4 / 6.5 s |

Performance probe (`DisplayWorkProjectionReader`, 6 runs, Ready copy):

| Scope | Rows | First run | Slowest warm run |
|---|---:|---:|---:|
| One audiobook (detail) | 1 | 3.6 s | 4.9 s |
| One person | 5 | 2.1 s | 3.3 s |
| Whole catalogue (no limit) | 4,000 | 25.6 s | 14.1 s |

These match the reported "about 14 s" for the list and "about 6 s" for Home.

## Ranked causes

### 1. Library list: seven credit look-ups scan the whole credit table for every item (about 93% of the list's cost)

Stage timings for the list's count statement (wall clock, two runs each):

| Stage | Time | Rows |
|---|---:|---:|
| `ranked_primary_assets`, `primary_asset_data`, `root_work_data` | 1–12 ms | 4,000 |
| `ingest_date_data`, `review_data`, `identity_job_data`, `user_lock_data` | 7–31 ms | 80–4,000 |
| **`work_data`** | **6.5–7.5 s** | 4,000 |
| `raw_data`, `full_data`, count | 6.3–6.9 s (includes `work_data`) | 4,000 / 1 |

`work_data` accounts for 997.8 M of the count statement's 998.4 M VM steps.

Removing columns from `work_data`:

| `work_data` variant | Time |
|---|---:|
| As shipped | 6.5–7.5 s |
| Year logic removed (about 2,200 lines, including the audiobook "borrow the book's year" look-up) | 6.7–9.9 s, no gain |
| Any single one of artist, narrator, genre, top cast, director, retail match removed | 6.0–14.3 s, no gain |
| Author, artist, narrator, genre, top cast, director and retail match all removed | 0.54–0.72 s |
| Every per-item sub-read removed | 0.27–0.35 s |

The cost is spread over seven look-ups of the same shape, so removing any one of them makes no visible difference. The plan explains it. These look-ups resolve the root work (the show for an episode, the album for a track) either as `canonical_value_arrays cva LEFT JOIN works pw … LEFT JOIN works gpw … WHERE cva.entity_id = COALESCE(gpw.id, pw.id, w.id)` or by joining `root_work_data` on `rwd.root_work_id = cva.entity_id`. SQLite never moves the right side of a LEFT JOIN ahead of the tables before it, and it cannot use an index on a computed `COALESCE` across tables joined later. So it reads the credit table first, by `key` alone:

```
CORRELATED SCALAR SUBQUERY 168                    -- artist, as shipped
  SEARCH w USING INDEX sqlite_autoindex_works_1 (id=?)
  SEARCH cva_artist USING INDEX idx_canonical_value_arrays_key_qid_entity (key=?)   <- every 'artist' row
  SEARCH p USING INDEX sqlite_autoindex_works_1 (id=?) LEFT-JOIN
  SEARCH gp USING COVERING INDEX sqlite_autoindex_works_1 (id=?) LEFT-JOIN
```

The same `(key=?)` search appears for author (non-comics), narrator, genre, top cast and director. The director-by-file look-up uses a different join and has the same effect. Each item therefore reads every credit row of that kind in the library: work proportional to items × credits, that is, to the square of the library size.

**Growth (count statement only, Ready copies):**

| Leaf works | As shipped: VM steps | CPU | Rewritten: VM steps | CPU |
|---:|---:|---:|---:|---:|
| 2,000 | 266,933,019 | 2.2 s | 35,027,873 | 0.44 s |
| 4,000 | 998,379,290 | 8.1–9.3 s | 70,093,636 | 0.84–1.0 s |
| 8,000 | ≈3,854,476,079 ¹ | 36.0 s (76.7 s wall) | 140,338,693 | 1.9 s |

¹ The 32-bit step counter wrapped (it printed −440,491,217; 2³² − 440,491,217 = 3,854,476,079).

As shipped, the work grows about 3.7–3.9× each time the library doubles. Rewritten, it grows exactly 2×. This is the "grows faster than the library" effect recorded in `benchmarks/README.md` (8× the works cost 25–45×).

**The rewrite tested (scratch only).** Each of the seven look-ups resolves its target id first as a scalar, so the index on `(entity_id, key)` applies:

```sql
-- before (one of seven)
FROM canonical_value_arrays cva_artist
INNER JOIN root_work_data rwd_artist ON rwd_artist.root_work_id = cva_artist.entity_id
WHERE rwd_artist.work_id = w.id AND cva_artist.key IN ('album_artist', 'artist', 'performer')
-- after
FROM canonical_value_arrays cva_artist
WHERE cva_artist.entity_id = (SELECT r.root_work_id FROM root_work_data r WHERE r.work_id = w.id)
  AND cva_artist.key IN ('album_artist', 'artist', 'performer')
```

The plan becomes `SEARCH cva_artist USING INDEX sqlite_autoindex_canonical_value_arrays_1 (entity_id=? AND key=?)`. The director-by-file look-up uses `cva_director_asset.entity_id = (SELECT pad.asset_id FROM primary_asset_data pad WHERE pad.work_id = w.id)`. Nine substitutions in total. `work_data`, all 4,000 `full_data` rows and the first page all hash identically before and after.

### 2. Library list: the whole read runs twice (2×)

`GetPageAsync` sends `<projection> SELECT COUNT(*) …` and then `<projection> SELECT … LIMIT 50` as two statements. The second cannot reuse the first, so each pays the full chain:

| Statement (4,000 works, no filter) | VM steps | CPU |
|---|---:|---:|
| Count, as shipped | 998,379,290 | 8.1–9.3 s |
| Page, as shipped | 998,536,381 | 9.1–9.3 s |
| Count + page, rewrite from cause 1 | 70,093,636 + 70,250,727 | 0.84–1.0 s + 0.94–0.98 s |
| Page with `COUNT(*) OVER ()` as one statement, rewrite from cause 1 | 71,692,274 | 0.95–0.97 s |

The one-statement form returned the same total (3,853) on each row of the page.

For `Movies`: as shipped 174.7 M + 174.8 M steps (0.95 s + 0.9 s CPU); rewritten 11.2 M + 11.3 M (0.25 s + 0.21 s CPU).

### 3. Home: about 280 separate look-ups per item, times 1,000 items (about 80% of Home's cost)

Home's statement is 10,173 lines: a `ranked_assets` chain and one result column per display field. Its plan contains **1,451 correlated scalar sub-queries**. **All of them use an index** (`SEARCH … (entity_id=? AND key=?)`, no scans), so this is not a bad plan. Outside the year logic, each row performs 276 sub-selects, 212 of them single-key reads of `canonical_values` (title, tagline, cover URL and its four sizes, widths, heights, states, accent colour, each tried on the file, then the work, then the show or album). The year logic adds the branch for that media type.

| Home variant (4,000 works) | Rows returned | VM steps | CPU |
|---|---:|---:|---:|
| `LIMIT 50` | 50 | 68,599,899 | 1.5–1.8 s |
| `LIMIT 1000` (production) | 1,000 | 83,185,874 | 6.8–7.5 s |
| No limit (the uncapped "all works" read) | 4,000 | 94,658,846 | 11.6–12.3 s |
| `LIMIT 1000`, every column except Year removed | 1,000 | 2,890,562 | 0.33 s |
| `LIMIT 1000`, Year only removed | 1,000 | 82,050,569 | 5.6–5.9 s |
| One-pass read of all 69 keys Home uses, for all 6,500 entities (`GROUP BY entity_id` with `MAX(CASE …)`) | 6,500 | 15,031,866 | 0.13–0.17 s |

SQLite already computes the expensive columns only for the rows it returns, which is why `LIMIT 50` is cheap. Each returned row then costs about 5–6 ms of CPU. The step count rises only modestly because almost every step is an index seek or a small sort (about 25 temporary sorts per row), not a cheap operation. Removing column groups shows the cost spread across the columns, with no single culprit. The one-pass row is not a drop-in rewrite: it shows that gathering the same values for every item at once costs about 1/40th of fetching them one by one.

Home asks for 1,000 items (`HomeProjectionLimit`, cached 30 s). It needs a wide set: `BuildHomeAsync` groups TV episodes into show cards and tracks into album cards before each shelf takes 18 items.

### 4. Home and detail: two whole-library credit lists are rebuilt on every call (fixed ~0.9 s at 4,000 works)

| Part | VM steps | CPU | Rows |
|---|---:|---:|---:|
| `projection_credits AS MATERIALIZED (SELECT * FROM primary_person_media_credits)` | 59,793,968 | 0.59–0.64 s | 8,694 |
| `canonical_artist_credits` (ranks every artist credit against `persons` with an `OR` join on name or QID; includes the line above) | 65,472,315 | 0.63–0.66 s | 600 |
| Detail read for one audiobook (`BuildSql(true, …)`) | 66,750,247 | 0.86–0.98 s | 1 |

Both are built for the whole library before any row is chosen, including for a detail page that shows one item. That is why the probe saw 2–5 s for one audiobook. The cost grows with the library, not with the page.

### 5. Library list, after fixes 1 and 2: the whole library is still built to show 50 items (linear, about 1 s at 4,000)

The page sorts by first-claim date (`ingest_date_data`) and filters on visibility computed at the end of the chain. Every item's full row is therefore built before 50 are kept. After fixes 1 and 2 this is linear, about 70 M steps and 1 s CPU at 4,000 works. It would reach about 17 s at 100,000 works by extrapolation (not measured).

### Related observations (not ranked; not measured in depth)

* **No guardrail covers these reads.** `tests/MediaEngine.Performance.Tests/LargeLibraryQueryGuardrailTests.cs` covers the collection page and historical ingestion only. Neither the library list nor Home has a step or time budget, so cause 1 went unnoticed.
* **The harness's Home benchmark measures nothing today** (0 rows, see [How it was measured](#how-it-was-measured)). The sizing table in `benchmarks/README.md` predates the Ready gate.
* **The uncapped "all works" read is widely used, often only as a visibility check.** About 45 call sites (22 in `CollectionEndpoints.cs`, others in people, detail, search, universe and display composition) call `LoadWorksAsync`. It builds all 4,000 display rows (11.6–12.3 s CPU uncached, 14–26 s wall in the probe), and many callers keep only the set of visible work ids. It is cached for 30 s. Any series or collection screen that misses the cache pays this cost.
* **Duplicate index:** `idx_canonical_values_entity_key ON canonical_values(entity_id, key)` repeats the table's primary key `(entity_id, key)`. It costs write time and disk space without helping any read.
* **Unmeasured risk:** Home's `IsUpdatingDetails` checks `media_operations` with `entity_id = … OR source_path = …`. The seed leaves `media_operations` empty, so the cost of that `OR` on a busy real library is unknown.

## Fix options

Product goals: **Performance** (speed), **Reliability** (correct and current results, fewer failure modes), **Maintenance** (how hard the code is to change safely).

| # | Option | Fixes | Performance | Reliability | Maintenance |
|---|---|---|---|---|---|
| A | **Rewrite the seven credit look-ups** to resolve the show/album or file id first (as tested above) | 1 | Measured: list 14× fewer steps; linear growth; 4,000 works ≈ 1 s CPU per statement | Same rows (hash-checked). Low risk: a local change in one SQL builder | Small. Needs a guardrail so the pattern does not return |
| B | **Count and page in one statement** (`COUNT(*) OVER ()`) | 2 | Measured: halves the list again | Count and page come from the same snapshot, so they can no longer disagree | Small. Touches `GetPageAsync` and its row mapping |
| C | **Add guardrails** at 4,000 and 8,000 works with VM-step budgets for the list, Home and detail, and set `ready_at` in the benchmark seeder | prevents regressions | none directly | Catches the next quadratic read before release | Low. Step budgets are deterministic, so they do not flake on a busy machine |
| D | **Home: gather each item's values in one pass** (pivot `canonical_values` / `canonical_value_arrays` once for the chosen items, then pick file → work → show/album in C# or a single `COALESCE`) instead of about 280 sub-selects per row | 3 | Indicated: the full one-pass read is 0.15 s against about 5.5 s; a realistic target for Home is under 1 s at 4,000 works | Same values if the fallback order is kept exactly; needs a field-by-field comparison test (the harness `verify` pattern) | Medium. A large rewrite of a 10,000-line generated query, but the result is much shorter and easier to read |
| E | **Home/detail: build credits only for the items being returned** (filter `primary_person_media_credits` by the chosen asset ids, or keep a stored credits table updated when credits change) | 4 | Removes about 0.9 s fixed cost; detail pages become near-instant | Filtering is low risk. A stored table adds a "kept in step" duty on every credit write | Filtering: small to medium. Stored table: medium, plus rebuild tooling |
| F | **Stored display table** (one precomputed row per item, refreshed when its values, artwork or credits change; list, Home and detail read it directly) | 1, 3, 4, 5 | Best: reads become simple indexed scans at any size (100,000 works included) | Weakest: rows can go stale if any write path forgets to refresh; needs a rebuild command and a consistency check. The data-store reset rule makes rollout easy pre-release | Largest: a new table, refresh triggers or jobs in the identity pipeline, and an architecture decision record |
| G | **Ask Home for fewer items** (lower `HomeProjectionLimit`) | 3 (partly) | Roughly proportional: 250 items would be about 2.5 s today | Product risk: shows and albums whose episodes or tracks fall outside the window disappear from Home shelves | Trivial code, but a product decision |
| H | **Longer cache or warm the cache after ingestion** | hides 3 and 4 | First visit after expiry is still slow | Stale shelves for longer; does not help detail pages or the list | Small, but masks the problem |
| I | **Add indexes** | — | None expected: Home's plan is fully indexed, and the list's problem is join order, which an index cannot fix | — | Not recommended. Instead drop the duplicate `idx_canonical_values_entity_key` (write-cost cleanup) |

## Recommendation

1. **Now: A + B + C as one small spec.** These are bug-level fixes with measured, hash-checked results. The unfiltered list should go from 13–22 s to about 1–2 s at 4,000 works, and its growth becomes linear. Guardrails with step budgets keep it there. Risk is low and the change stays inside `LibraryItemRepository` and the benchmark seeder. Removing the duplicate index can ride along if Shy agrees, since it changes the data structure.
2. **Next: a spec for Home and detail built on D + E (filtered credits).** This attacks the per-item look-up volume and the fixed credit cost inside the existing reader, with no new stored state. Target: Home under 1 s and detail under 0.2 s at 4,000 works, linear at 100,000. Option D is the bigger of the two and needs a careful field-by-field comparison. Include the "all works" callers that only need visible work ids: they want a list of ids, not 4,000 display rows.
3. **Hold F (stored display table) as the decision to revisit** if D + E cannot reach the target at 100,000 works. It is the fastest design but adds a staleness failure mode on every write path. It deserves its own architecture decision rather than being slipped into a speed fix.
4. **Do not** lower the Home limit, lengthen the cache or add indexes as the main fix. Each one hides the cost or moves it rather than removing it.

**Decision for Shy:** whether the Home work (step 2) should aim straight for the stored display table (F) because of the "best long-term fix before beta" principle, or start with D + E. My recommendation is D + E first: it removes the causes this report measured without adding stored state that can go out of date. Option F stays available if 100,000-work measurements show it is needed.

## Limits of this investigation

* Synthetic data with the harness's shape and ratios, made Ready by hand. Real libraries have more claims, more `media_operations` rows and different credit shapes.
* Wall-clock times were taken on a heavily loaded desktop. Use the VM-step and CPU columns for comparisons.
* Only the Engine's data-store reads were measured, not Engine-to-Dashboard transfer, rendering, or authorization filtering.
* Options A and B were prototyped as SQL text in scratch files. D, E and F are estimated from the measurements above, not built.
* The 100,000-work figures are extrapolations, except where `benchmarks/README.md` measured them.
