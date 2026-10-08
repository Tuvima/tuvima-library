# Ingestion restart recovery verification

## Root causes and changes

- The existing batch was marked abandoned while ten identity jobs remained unfinished. Current media selected batches from status and media operations, while history already excluded unfinished identity work. The batch therefore disappeared from both lists. All these selections now share the durable activity predicate, including progress heartbeats and startup emission.
- Completion reconciliation treated old unleased jobs as failures. It now waits for actual terminal outcomes. Startup immediately requeues intermediate jobs with or without a lease while preserving the original batch ID.
- Eight duplicate input files pointed to assets already identified in the batch and were omitted from settled progress. Distinct skipped input paths now count, with repeated logs for already-handled paths excluded.
- Navbar icon, circular progress, and active-batch icon use the same success green. Individual ingestion cards and drawers show added counts only for tracks, episodes, and comic issues. Removed catalogue-total queries and per-item progress bars, including the misleading Dune bar.

## Verification

- Restore passed; solution build passed with zero warnings and errors.
- Strict MkDocs build and `git diff --check` passed.
- The first serial full solution run had 3,285 passed, 37 skipped, and one unrelated naming guardrail failure on the pre-existing untracked access-architecture planning document. The final rerun had 3,284 passed, 37 skipped, and two failures: that same document and an intermittent ProviderTester navigation assertion. The navigation test then passed in isolation (50 settings cases), and the full Web suite passed again (914 tests). The unrelated planning document was left unchanged. A performance timing failure under parallel test load passed in isolation and in both serial solution runs.
- Added SQLite regression coverage for resumed abandoned, interrupted, completed, and failed batches; all seven outstanding identity states; leased and unleased startup recovery; newer completed batches; completion returning the recovered batch to history; long waits preserving job outcomes; and duplicate inputs sharing an asset.
- Used the existing development library without resets. Before the fix, Operations showed zero current items while the navbar reported ten active jobs. After startup recovery, the same batch `efc50f6f-3786-4e6c-b00b-6349e2b4c7f0` showed 88 grouped media items. A second Engine/Web restart preserved that batch and restored its two active and eight queued jobs. Startup explicitly logged recovery in both runs.
- Navbar and batch progress both reported 92%, with 119 of 129 input files settled after duplicate accounting. Active media appeared ahead of settled items; Drive and The Mandalorian exchanged ordering as their activity timestamps changed.
- Visually inspected Operations and album details at actual CSS viewports of 1920×1080, 1280×720, and 390×844, accounting for browser zoom. No horizontal page overflow was present. Navbar, ring, and batch icon all computed to `rgb(34, 197, 94)`.
- View all opened immediately; the drawer resolved the same original batch link. Album cards and details showed “0 tracks added” or “1 track added,” TV showed added episodes, and Batman showed “3 issues added.” The complete current-media page contained no ratio counts or per-item progress bars; Dune and Dune: Part Two were checked explicitly. Browser viewport overrides were reset after validation.

## Product-owner summary

Restarting no longer makes unfinished imports disappear. Operations recovers the same batch, keeps its active items visible, and reports actual completed work. Albums, shows, and comics display simple added counts, while the matching green indicators show that processing continues.
