# Handoff: finish the outstanding work, end to end

> Written 2026-10-19 by the planning side for Shy (product owner, not a developer). Snapshot of `main` at 2e21dd3; re-check with `git fetch` and `git log` before each thread. Locked specs in `engineering/specs/` win over this note wherever they disagree. Companion review: the 2026-10-19 outstanding-work review (summarised in section 1 below, so this file stands alone).

## 1. Where things stand (verified against main on 2026-10-19)

| Area | State |
|---|---|
| First run and prerequisites | Built, archived |
| Settings revamp (WP0 to WP18) | Built, archived |
| Ingestion and readiness (`ingestion-and-readiness.md`, v4) | About 60%. Built: WP1, WP2, WP3, WP5, WP9, WP12 (WP6 banner built under Settings revamp, WP7 mostly). Open: WP4 (Now panel, no batch look), WP10 (waiting-for reasons), WP8 (Cibola Burn error; needs Shy's Engine log), WP11 (remove batches, last). The spec's status line still says "Nothing is built": fix it. |
| Universe (`universe.md`, v4) | On main: U-WP0, U-WP1. Built but NOT merged: U-WP2 and U-WP3 (the Expanse fix, bug 23) on branch `claude/universe-wave2-int`. Rest not started. |
| Developer tools (`developer-tools.md`, v3) | Wave 1 on main (WP0, WP13, WP3, WP32). WP0 still has a repeatability gap. Waves 2 to 8 not started. |
| Delete from library (Locked v5), Deleting personal files in View (Locked v3) | Not started. Not in any wave plan. |
| Bug list group 6 | Bug 14 built. Open: bug 8 (the app is called Library, but "Tuvima" or "Tuvima Library" appears in page titles, Settings text and Setup, for example `SetupPage.razor` "Tuvima could not get ready"), bug 12 (My List shows a bookmark; Shy wants a heart). |
| Bug list group 4 (details overlay, Edit in the "..." menu, menu layout, squished Editing drawer) | No spec. Planning comes first (thread 5 below). |
| Slow data reads (library list about 14 s, uncached Home about 6 s at 4,000 works) | Not investigated. |
| Open pull requests | #282 (flaky tool-installer test fix), #270 (website dependency bump) |
| Leftover branches | `docs/first-run-prerequisites` (delete), `claude/universe-wave2-int` (land it) |

## 2. Rules every thread follows (all already in CLAUDE.md)
- Start from the spec. Set its status to In progress and update `engineering/README.md` at the start. Re-check related specs on `main` for collisions first (Universe section 6B has a collision check).
- At most two packets or threads at a time, each in its own worktree. Stop only your own .NET processes (`tools/Stop-WorktreeDotnet.ps1`).
- Finish each wave with: CI-strength build, tests, the independent `reviewer` pass, then merge by the `ready-to-merge` label. Shy's go-ahead on these prompts is the approval to merge the work they name. New scope or a security finding along the way goes to Shy first.
- Dashboard changes need the visual checks in `src/MediaEngine.Web/CLAUDE.md`.
- Finish with a Plain English summary. Keep only `main`: delete finished branches and worktrees.

## 3. Order and why

| Step | Thread | Runs with | Why here |
|---|---|---|---|
| 0 | Land and tidy | alone (short) | The Expanse branch is already built and goes stale while `main` moves. Closes bug 23 and clears housekeeping. |
| 1a | Finish ingestion | 1b | Most visible bug-list area left. |
| 1b | Developer tools foundation | 1a | Fixes the baseline tool first, then packets that unlock the most others. Touches different files from 1a. |
| 2a | Small fixes: Library wording and heart | 2b | Touches many files, so run it when ingestion is merged. |
| 2b | Universe page and Engine actions | 2a | Needs the Expanse fix merged (step 0). |
| 3 | Developer tools waves 3 to 8 | one other thread | Continue wave by wave, one thread per wave. |
| 3 | Lane View all grid (3d) |  any gap | Built 2026-10-20, merged in PR #297 (rules: `docs/product/presentation-rules.md` › Shelves and View all grids). |
| 3 | Deletion specs, then slow-read investigation | any gap | Locked specs, unscheduled. The investigation is read-only. |
| 4 | Details overlay and Editing Mode planning | needs Shy | Planning first (Claude Code cannot start without a spec). |

Spec dependency notes: Universe U-WP4 waits on Developer tools WP9. Developer tools WP2, WP4, WP6, WP10 and WP30 wait on WP1; WP10, WP28 and WP30 also wait on WP27; WP17 waits on WP9, WP10 and WP13.

## 4. Prompts to paste into Claude Code (one per thread)
Open Claude Code at claude.ai/code, pick Tuvima/tuvima-library, paste one prompt per new thread.

### Thread 0: Land and tidy
```
Land finished work and tidy the repository. Read CLAUDE.md first.
1. Branch claude/universe-wave2-int holds U-WP2 and U-WP3 of engineering/specs/universe.md (the Series fix for bug 23, The Expanse). Merge main into it, resolve conflicts, check the data-store upgrade step number is still free, run the CI-strength build and the tests, run the independent reviewer against universe.md sections for U-WP2 and U-WP3, then merge it with the ready-to-merge label. Mark U-WP2 and U-WP3 built in universe.md and update engineering/README.md.
2. Merge pull request 282 (flaky tool installer test) once CI is green. Leave pull request 270 (website dependency bump) for Shy unless CI is green and it only touches /website.
3. Delete the remote branch docs/first-run-prerequisites. Archive engineering/specs/profiles-setup-redesign.md and engineering/specs/editing-mode.md (Built) into engineering/specs/archive and update the README index.
4. Correct the status text in engineering/specs/ingestion-and-readiness.md: the header says nothing is built, but WP1, WP2, WP3, WP5, WP9 and WP12 are built. Change only status wording, not decisions, and update the README line.
Finish with a Plain English summary. Tell Shy plainly that the Expanse fix needs a data-store reset or upgrade on the desktop test app, and that bug 22 (each TV file listed as its own show) is still unproven.
```

### Thread 1a: Finish ingestion
```
Implement the remaining packets of engineering/specs/ingestion-and-readiness.md. Read CLAUDE.md and the spec first; run its collision check against the other In progress specs (developer-tools.md, universe.md).
Built already: WP1, WP2, WP3, WP5, WP9, WP12. Do, in this order:
- WP4: the rotating Now panel (5 to 10 rows, minimum dwell), Added summary, fixed columns, photos and personal videos as counts, remove IngestionBatchHistory and every batch word from what people see.
- WP6 check: the old collapsed review block in RecentlyAddedPageContent.razor must be gone and Needs Review must live on its own page, with the spec's done-when test.
- WP7 check: the same event shows with identical text in the live page, the Overview and the menu tooltip (add the one test the spec asks for).
- WP10: waiting-for reasons on each not-Ready row.
- WP8: reproduce the Cibola Burn "something went wrong" error. If you cannot reproduce it, wrap each row so one bad row cannot take down the page, add the row-missing-data test, and tell Shy exactly which Engine log lines (temp-folder engine.log from the moment it happened) you still need.
- WP11 last: remove batches from the Engine, Contracts and Dashboard, with a data store upgrade step. Compare an ingestion run before and after with the baseline tool (tools/Run-IngestionBaseline.ps1) and show no change.
Also confirm bug 22 (each TV file listed as its own show in Watch) with a test; if it still happens, fix it under WP9. Run waves of at most two packets, reviewer after each wave, merge by label. Set the spec Built and archive it at the end. Finish with a Plain English summary.
```

### Thread 1b: Developer tools foundation
```
Continue engineering/specs/developer-tools.md (v3, In progress). Read CLAUDE.md and the spec first; run its collision check against ingestion-and-readiness.md and universe.md.
Wave 1 is on main (WP0, WP3, WP13, WP32). Do, in this order:
1. Finish WP0 repeatability: two baseline runs of the same build differed in 36 values. Add a record-and-replay mode to the baseline tool (src/MediaEngine.Api/DevSupport/Baseline*.cs, tools/Run-IngestionBaseline.ps1): the first run saves every provider answer under the run folder and later runs replay them. Development builds only, and no saved answer may contain a key. Done when two replayed runs of the same build compare with zero differences; list any remaining differences as our own nondeterminism.
2. Ask Shy whether C:\Temp\real_import (books with PDFs, audiobooks, movies, tv, music, comics, personal_photos, personal_videos) is ready, and record a real-media baseline with -SampleRoot before WP9.
3. WP1 (shared state card and health words; the Settings frame it needed is built) and WP27 (Local AI engine room), two at a time.
4. Then WP14 (plugin secrets store), WP5 (Developer Tools made honest), WP2 (approval step and record), then WP9 (make all shipped providers declarative; compare before and after with the replay baseline).
Run the checkpoint after each wave: build, tests, ingestion comparison, independent review, merge. Stop and ask Shy if the WP21 licence check or any security decision comes up. Finish with a Plain English summary and the list of next packets that are now unblocked.
```

### Thread 2a: Small fixes (after 1a has merged)
```
Fix bug list group 6 items 8 and 12 (2026-10-18 bug list). Read CLAUDE.md first. The app is called Library; Tuvima is the company and brand.
- Bug 8: review every user-visible string (page titles, Settings descriptions, Setup, error messages, snackbars, the PWA manifest and meta description) and replace "Tuvima" or "Tuvima Library" with "Library" where it names the app (for example the profile editor and "Tuvima could not get ready" in SetupPage.razor). Keep Tuvima where it is the company or brand (about text, licences, brand assets, "Tuvima Atlas" only if Shy confirms its name). List every place you kept it. Add a guardrail test that fails when new user-facing strings say "Tuvima" outside an allowlist.
- Also check (H4c leftover): the profile menu and glossary say "My Library" consistently; fix any stragglers.
- Bug 12: "My List" uses a heart icon everywhere (For Me, Watch, Read, Listen browse menus, the hero and phone hero menu, My Library). Filled when saved, outlined when not. Do not change the Listen song Favorites heart semantics.
Dashboard visual checks per src/MediaEngine.Web/CLAUDE.md. Update docs/product/presentation-rules.md if it names the icon. Finish with a Plain English summary.
```

### Thread 2b: Universe page and actions (after Thread 0)
```
Continue engineering/specs/universe.md (v4). Read CLAUDE.md and the spec first and run its section 6B collision check. U-WP0 to U-WP3 are built once Thread 0 has merged; verify on main.
Do, two at a time: U-WP5 (Universe page, entry points, empty and partly-ready states, "Part of" line), U-WP7 (Engine actions for helper apps), U-WP12 (companion session service), then U-WP10 (Also appears in). Leave U-WP4 until Developer tools WP9 is merged, and U-WP6, U-WP8, U-WP9, U-WP11, U-WP13 to U-WP15 for later threads. Stop and ask Shy if any packet goes beyond the spec. Visual checks at phone, tablet, desktop and TV widths. Finish with a Plain English summary and the Shy-owned device checks (companion on iPhone and Android, Universe page on TV).
```

### Thread 3e: Videos, all waves (spec Locked v25, 2026-10-20)
Depends on: the ingestion finish and Developer tools foundation threads being merged. One session runs all three waves. Wave 2 needs the Details overlay and Editing Mode "..." menu and banner, and the Developer tools secrets store. Wave 3 checks the StashDB terms with Shy before the Stash-box plugin. The Vault access, PIN, history-isolation and leak-audit packets (WP9, WP12, WP13, WP17) need an independent security review.
```
Run engineering/specs/videos.md end to end, all waves, per .claude/rules/end-to-end-runs.md. Read CLAUDE.md first.
```

### Thread 3d: Lane "View all" grid (H4e)
```
Build the Home lane "View all" grid (Shy approved 2026-10-16). Read CLAUDE.md and docs/product/presentation-rules.md first. There is no spec file: plan briefly, show Shy the plan, and build on "go ahead".
Shelves show about 20 items plus a View all tile; View all opens a tiled grid that loads more on scroll. Default sort Recently added; other sorts Title A-Z, Year, Recently played, remembered per lane. Filters: media type, genre, format, Not started / In progress / Finished (only the meaningful ones per lane); phones reuse the Filters bottom sheet. My Library sections (My List, Liked, Recent activity) get their own View all. Shelf links currently use each shelf's old SeeAllRoute (MediaTileShelf.razor, MediaTileComposerService.cs). History needs paging; the id lookup (POST /api/v1/display/items) loads the whole catalogue per call, so consider paging it. Guardrails: 48px targets, no ::deep or inline styles, wire types in Contracts with fixtures regenerated, independent review for data and permissions, update presentation-rules.md. Finish with a Plain English summary.
```

### Thread 3a: Developer tools waves 3 to 8 (one thread per wave)
```
Continue engineering/specs/developer-tools.md at the next wave (waves are listed in section 6 of the spec; do wave N where N is the first wave not fully built on main). Run the collision check, do at most two packets at a time, run the ingestion comparison against the baseline and the independent reviewer at the end of the wave, merge by label, and update the spec and README. Finish with a Plain English summary.
```

### Thread 3b: Delete from library, then Deleting personal files in View
```
Implement engineering/specs/delete-from-library.md (Locked v5) in full, then engineering/specs/view-personal-file-deletion.md (Locked v3; its blocker, the paired-app fix 248, has merged). Read CLAUDE.md and each spec first, run collision checks, two packets at a time, reviewer after each wave, merge by label. Deletion and data-loss risk: use the independent reviewer on every packet that removes files, and ask Shy before anything the specs do not decide. Finish each spec Built and archived, with a Plain English summary.
```

### Thread 3c: Slow reads investigation (read-only, report only)
```
Investigate why the library list takes about 14 seconds unfiltered and uncached Home about 6 seconds at 4,000 works. Read CLAUDE.md and use the existing benchmark harness and performance probe (restored in CI in commit 481fce1). Do not change application code. Find the slow queries, show timings and query plans, and write a report in engineering/reports/slow-reads-2026-10.md with ranked causes, fix options with product trade-offs (Performance, Reliability, Maintenance) and a recommendation. Commit only the report to main, then give Shy a Plain English summary.
```

## 5. Planning that must happen before Claude Code can build
- **Videos:** planned and Locked on 2026-10-20 (`engineering/specs/videos.md`, v25). Its build prompt is Thread 3e above.
- **Group 4: details overlay, Edit in the "..." menu, menu alignment, squished Editing drawer.** No spec exists. Shy shares the Editing drawer screenshots in the planning project; a spec is written and locked; then a Claude Code prompt is added here. It also settles the older "per-type edit pages" step (U2) versus the Item Detail rule that per-type pages stay gone.
- **Lane "View all" grid (H4e) and My Library on desktop/tablet (H4c).** Not confirmed from the repo; decide whether to keep them in scope.

## 6. Owed by Shy
Provide the Engine log for bug 13 (Cibola Burn). Confirm `C:\Temp\real_import` is ready. Device checks: iPhone book reader and Home; Android playback past 10 minutes then seek, pause 15 minutes, picture-in-picture; TV cast; phone and TV pairing and QR code from the Settings revamp.

## 7. Plain English summary
Most testing bugs are fixed. Seven Claude Code threads finish the rest in a sensible order: tidy and land the Expanse fix first; then finish ingestion beside the plugin foundation; then the Library wording and heart icon beside the Universe page; then the remaining plugin waves, the deletion features and the slow-load investigation. The details pop-over and editing drawer need a plan from the planning project before they can be built.
