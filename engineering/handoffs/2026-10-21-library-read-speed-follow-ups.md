# Handoff: library read speed, follow-ups (2026-10-21)

Run this handoff end to end per `.claude/rules/end-to-end-runs.md`.

> Written 2026-10-21 for Shy at the close of the library read speed work (started by [Slow library and Home reads at 4,000 works](../reports/slow-reads-2026-10.md)). Snapshot of `main` at `2bae63a0f` (2026-10-21 00:38 UTC). Re-check with `git fetch` and `gh pr list` before each thread. Locked specs win over this note wherever they disagree.

## 1. Where things stand

**Merged into main**

| Change | What it did |
|---|---|
| [#290](https://github.com/Tuvima/tuvima-library/pull/290) Library list speed (spec archived Built) | Credit look-ups resolve their target first; one statement for page and total; guardrails; duplicate `canonical_values` index removed |
| [#307](https://github.com/Tuvima/tuvima-library/pull/307) Home and item detail speed (spec archived Built) | One-pass Home read, credits only for returned rows, set-based audiobook year, light "visible works" read, guardrails. Home 7.8 s → 0.37 s, detail ~1 s → 0.06–0.09 s on the projection library |
| [#352](https://github.com/Tuvima/tuvima-library/pull/352) Audiobook year on the remaining seven screens (spec archived Built) | Seven reads stop searching the whole credit list for an audiobook's borrowed year |
| [#338](https://github.com/Tuvima/tuvima-library/pull/338) Series search via the search index | Fixed series search returning nothing when the index matched |
| [#349](https://github.com/Tuvima/tuvima-library/pull/349) Admin PIN unlocks | Revoking all sessions also deletes their admin unlocks |
| [#330](https://github.com/Tuvima/tuvima-library/pull/330), [#353](https://github.com/Tuvima/tuvima-library/pull/353) Stored-name order | Report, then fixed order for title colours, artist panel and Matching credits |

**Approved, labelled `ready-to-merge`, waiting only for the required Build & Test check** (it was running at 01:25 UTC on 2026-10-21; each merges itself when it passes):

| Change | What it is |
|---|---|
| [#355](https://github.com/Tuvima/tuvima-library/pull/355) | Documents that main requires the Build & Test check |
| [#356](https://github.com/Tuvima/tuvima-library/pull/356) | Albums and series read their artists and authors from the list table; records remediation T4 below in the stored-name report |
| [#357](https://github.com/Tuvima/tuvima-library/pull/357) | **Locked spec:** person credits and series search without the whole-library credit list (`engineering/specs/person-credits-and-series-search-speed.md`) |

If any of these three is still open when this handoff is picked up, find out why (red check, conflict) and fix it before thread T1.

## 2. Threads

| # | Thread | Kind | Depends on | Can run beside | Needs from Shy |
|---|---|---|---|---|---|
| T1 | Build the person credits and series search spec | Build (Locked spec) | #357 merged | T4 (once decided), T5 | Nothing |
| T2 | Remediation R1: library-sized id lists sent as separate values | Plan, then build | T1 built (reuses its one-list helper) | T4, T5 | Approval of the plan |
| T3 | Remediation R2: about 14 other reads using the whole-library credit list | Plan, then build | T1 built (reuses its candidate step) | T4, T5 | Approval of the plan |
| T4 | Combined artist view shows "Unknown Artist" | Decision, then small build | #356 merged | T1, T2, T3 | **Decision:** fix it or remove the unused action |
| T5 | Should a series "creator" hold several names? | Product question | — | everything | **Decision** |
| T6 | Measure the fixed reads at 100,000 works | Measure, report | T1 built | T4, T5 | Afterwards: whether the stored display table (option F) is needed |
| T7 | Two unmeasured risks: `IsUpdatingDetails` on a busy `media_operations`, and the person-detail step that scans claims per person | Measure, then plan if slow | T1 built | T6 | Approval of any plan |

T2 and T3 both touch the credit reads, so they run one after the other, not side by side.

### T1. Build the person credits and series search spec

* **Brief:** build `engineering/specs/person-credits-and-series-search-speed.md` (Locked; decisions D8–D10) end to end, WP1 to WP5. That includes its two validation checks, L1 and L2, in `engineering/validation/batch.md`. Finish with the merge, then report.
* **Collision check first:** the planning session asked for a **cloud** session to run this build. No branch or proposed change for it had appeared by 01:25 UTC on 2026-10-21, and cloud sessions do not report back. Before starting, look for a `claude/` branch or open change that edits `PersonCreditReadService`, `CollectionSearchReadService` or `PersonCreditSql`. If one exists, leave the build to it and track it. Otherwise build it here.
* **Facts:** expected result from the plan's prototype: a person's credits read and series search no longer build the whole-library credit list (series search: about 3.1 billion operations with 40 index matches, 67–103 s for a common word at 4,000 works). The comparison method (byte-identical snapshot with ids masked) and the benchmark project (`benchmarks/MediaEngine.Storage.Benchmarks`, outside the solution; build it with `-c Release` on its project file) are described in the spec.

### T2. Remediation R1: library-sized id lists sent as separate values

* **Source:** spec `person-credits-and-series-search-speed.md`, section 8, R1.
* **Problem:** universal search's people results (`UniversalSearchReadService`: `AllowedAssetIds`, `AllowedWorkIds`) and the library feed (`LibraryWorkFeedReadService`: `AllowedWorkIds`) bind every visible file or work as its own value. SQLite here accepts at most 32,766 values in one statement, so these reads fail outright above that. Below it, sending the values dominates their time.
* **Brief:** find every `IN @…` in `src/` and `tools/` fed from a library-sized set (for example `LoadVisibleWorkIdsAsync`). Move each to T1's one-list helper. Add a behaviour test per read with more than 32,766 ids, and a guardrail in the style of `DapperGuidListParameterGuardrailTests` that rejects library-sized lists bound as separate values. Plan first, as a remediation in that spec or a short spec; wait for "go ahead".

### T3. Remediation R2: other reads using the whole-library credit list

* **Source:** spec `person-credits-and-series-search-speed.md`, section 8, R2.
* **The reads (about 14):** universal search's people results (once per matching person), the series browse page (three places), the contributor shelf, people reads (four), a work's cast credits, artwork (two services), a series detail read, access checks, collection rules, the metadata editor and the person store.
* **Brief:** first measure each on the performance-test library at 4,000 and 8,000 works, and say which build the list once per visit and which once per row. Then plan moving the slow ones to `PersonCreditSql.ScopedCreditsCte` for the files they need, with T1's candidate step. Use the same same-values comparison and guardrails as T1. Plan first; wait for "go ahead".

### T4. Combined artist view shows "Unknown Artist"

* **Source:** `engineering/reports/stored-name-order-2026-10.md`, section "Remediation to address later: the combined artist view" (added by #356).
* **Problem:** the combined artist view (one artist's albums together) looks for the artist on the first song. Artists are stored on the album, so it never finds one: no name, photo, person link or genre. Year and track list are read from a song too and need checking.
* **Who uses it:** nothing found. No Dashboard screen, phone or TV app, or test calls this Engine action. The Dashboard's artist pages use a different one that already works.
* **Options:** (a) fix it the way #356 fixed the album page; (b) remove the unused action. **Recommendation: (b)**, unless a planned screen needs it.

### T5. Should a series "creator" hold several names?

* **Source:** the same report, "Related, also open".
* **Question:** `creator` is stored as a single value, although Wikidata can name several creators for a series. Options: (a) keep one name (today); (b) store and show several, like authors. Needs Shy's product call before anyone plans it. A data-structure change is allowed pre-release (reset, no migration).

### T6. Measure the fixed reads at 100,000 works

* **Why:** every fix so far was measured at 2,000 to 8,000 works. The report's held option F (a stored display table, one ready-made row per item) is to be revisited only if 100,000-work measurements miss the targets.
* **Known remaining cost:** the library list still builds every item's row before it keeps 50. It is now linear: about 1 s at 4,000 works in the report's prototype, 1.4 s in the Home spec's measurement, roughly 17 s at 100,000 by extrapolation. That figure is not measured.
* **Brief:** with the benchmark harness (`seed --scale full`, items marked Ready, which the seeder now does), measure the library list (no filter, one media type), Home, the "all works" read, item detail and person detail at 100,000 works. Write a short report with the numbers and whether each meets its spec target (Home under 1 s, detail under 0.2 s). If any misses, set out the options, option F included, for Shy. Measure only; no code changes.

### T7. Two unmeasured risks

* **`IsUpdatingDetails` check:** `src/MediaEngine.Storage/IngestionAvailability.cs` looks up `media_operations` with `entity_id = … OR source_path = …`. The seed leaves `media_operations` empty, so the cost of that `OR` with real operation history is unknown.
* **Person detail pre-step:** the step that finds a person's files scans `metadata_claims` for each matched person (recorded as a nit in the Home spec, section 8). It is linear, but may miss 0.2 s on a large claim history.
* **Brief:** fill `media_operations` and `metadata_claims` to realistic sizes in a scratch copy, measure both, and plan a fix only if either is slow.

## 3. Observation outside this work

* About 73 test files clean up with a silent `catch { }` (for example `tests/MediaEngine.Api.Tests/ArtworkLibraryReadServiceTests.cs`). CLAUDE.md "Never" rule 4 forbids silent catches. The two files from this work were fixed, but the pattern is repo-wide. Ask Shy whether the rule covers test clean-up; if it does, it is a Mechanical sweep (`catch (IOException)` with a comment, or a shared helper).

## 4. Close-out

When every thread is merged or explicitly left for Shy, mark this handoff Done in `engineering/README.md`. Update the row for the slow-reads report as well.
