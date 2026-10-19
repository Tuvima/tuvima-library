# Spec: The live ingestion page and when items appear

> **Status:** Locked · **Version:** 3 · **Last updated:** 2026-10-19 · **Related docs:** [Product overview](../product/overview.md) · [First run and prerequisites](archive/first-run-prerequisites.md) (Built; this spec builds on its "waiting, not failed" rule and does not redo it)

> Groups 2 and 3 of the 2026-10-18 bug list, combined at Shy's request because both depend on one question: when is an item "fully added"? Group 2: bugs 21, 5, 6, 20, 4, 15, 27, 28, 13 and planning items A, B. Group 3: bugs 11, 22 and planning item G. Shy answered the questions in two rounds (23:51Z and 00:01Z); his answers are recorded in section 3. His 00:01Z redesign of "View all" replaces the earlier decision to retire it. Everything below was read in the code on main. Where something is inferred or not yet reproduced it says so. Shy locked it on 2026-10-19. Nothing is built.

## Product walkthrough

**What goes wrong today.** The live page, the menu indicator and the Overview all describe the same work, but each counts it its own way. The Engine sends a progress figure, then the Dashboard re-works that figure into a second one, and both change what they are dividing by part-way through a run. So the bar jumps, the estimate is far out, the headline says "0 items" while hundreds of files are queued, and the icon columns shuffle depending on what each row happens to be.

**What it is like after.** One page, one story, always in the same order:
1. **A headline and one bar.** "Found 508 files", then a quick running tally by type ("212 movies, 140 episodes, 96 songs, 31 books, 29 still being sorted"). The bar only moves forward and covers all the work still in the queue.
2. **A time estimate only when it can be trusted.** Otherwise the page shows how long it has been running ("Running for 4 min"). It never shows a wild "about 2 min left" (D4).
3. **Two layers: what is happening now, and what happened (D3, D12, D13).**
   - **Now:** the top of the page shows the 5 to 10 items being worked on this moment, rotating as they finish and the next ones start, so the page feels alive. Each row stays on screen at least a few seconds so it never flickers. Photos and personal videos show only as a count ("2,300 photos in progress").
   - **Added:** under it, a short summary of what was fully added (for example "Today: 212 movies, 140 episodes, 31 books"), each count opening the Activity log filtered to those items.
   - **Activity log (View all, kept and widened):** one searchable, flat, time-stamped log of everything the Library did, not only ingestion. Filters: **Added** (the default when you arrive from the summary), Needs Review, Failed, **System** (for example "Couldn't find FFmpeg", a provider down, a tool downloaded), and All. No batches anywhere. Photos and personal videos appear as counts, never as file rows. It is paged and you can search titles and details.
4. **Two colours, no third.** The menu indicator is blue while the Library works and green when it is idle (D6). Because the Library is nearly always waiting on something, there is no amber state.
5. **Fixed columns** in both the Now panel and the log: Title, Type, Step, Status, Details. Nothing jumps.
6. **Things that need an administrator are one banner across the top of Settings**, with a link to their own page (D5): Needs Review, and a missing tool from the first-run spec. Only server administrators see it.
7. **Every screen shows the same activity.** The live page, the Activity log, the Overview's recent activity and the menu indicator read the same list and use the same words (D8).
8. **"Ready" means one thing, everywhere (D9).** An item is Ready when it is matched to a real title, placed in its show, album or series, and has its artwork. That should take moments, not minutes. Only Ready items appear on Home, in the hero, in the lanes, in Watch, Read, Listen and the other pages (D10). Until then an item appears only on this live page. After that, artwork, people and extras fill in by themselves while you already watch or read.
**What stays the same.** Recently added stays as it is. Scan, the run history and the item drawer stay. Each file is still matched in its own time; nothing waits for anything else (D11).

**Acceptance criteria**
- **A1.** With 500 mixed files added at once, the bar never moves backward and never jumps by more than the work completed. (WP1, WP3)
- **A2.** The headline shows files found first; the tally by type updates as types become known; audiobooks and albums count as one item in the table but their files in the headline. (WP1, WP3)
- **A3.** The page shows elapsed time until the estimate is reliable, then the estimate; in a replayed 500-file run no shown estimate is off by more than 25 percent. (WP2)
- **A4.** With FFmpeg hidden, the page says "Waiting for media tools", the administrator banner says so too, and the headline never says "0 items". (WP1, WP5, WP6)
- **A5.** Every row has the same columns in the same places for every media type; tooltips match the icon they sit on. (WP4)
- **A6.** The menu indicator is blue while working and green when idle, with no other state. (WP5)
- **A7.** "View all" opens the Activity log, which can be searched and filtered; arriving from the Added summary shows only added media, and the System filter shows "Couldn't find FFmpeg" style events from the same page. (WP4, WP12)
- **A8.** An administrator sees a Needs Review banner at the top of every Settings page when anything needs review, opening a Needs Review page; non-administrators see nothing. (WP6)
- **A9.** The Overview's recent activity and the live page's list are the same component with the same rows. (WP7)
- **A9b.** There are no batches anywhere a person can see: no batch history, no "Open batch", no run links. (WP4, WP7)
- **A9d.** The Now panel never shows more than 10 rows, rows rotate as items finish, and no row disappears in under a few seconds. (WP4)
- **A9c.** Adding 3,000 photos shows counts only, in the Now panel and in the log, and no file rows. (WP4, WP12)
- **A10.** The "Something went wrong" message on a half-added item no longer appears; one bad row cannot blank the page. (WP8)
- **A11.** Add 20 episode files of 3 shows: while they are being matched, none appears on Home or in Watch; as each show is matched, the show appears once with its episodes. (WP9, WP10)
- **A12.** An unidentified or in-review item never appears in a Home lane or the hero. (WP9)
- **A13.** Home, Watch and the Library list use the same definition of Ready; a test fails if a new screen defines its own. (WP9)
- **A14.** On the live page, an item that is not yet Ready says what it is waiting for. (WP10)
- **A15.** Measured on a 500-file run: the time from an item being matched to it appearing in the Library is reported, and the item's details fill in afterwards without a reload. (WP9)

## 1. Goal

Whoever adds media can glance at one place and trust what it says: how many files were found, what they are, how far along the whole job is, and what, if anything, needs them.

## 2. Scope

**In scope**
- One counting model on the Engine; the Dashboard shows it and never re-works it.
- The headline, the bar, the tally by type, the fixed-column table, the run states.
- The menu indicator's colours, and the Overview's link into this page.
- Needs Review items shown on the page.
- Finding and fixing the cause of the "Something went wrong" tile.
- One definition of "Ready", used by Home, the hero, lanes, Watch and the Library list (bugs 11, 22, item G).
- Removing batches everywhere (screens, Engine code and tables) and showing very large additions as counts.
- An Activity log that covers system events as well as added media, with search and filters.

**Out of scope**
- Missing-tool detection and the "waiting" state itself (Group 1, Locked).
- How files are identified, matched or enriched.
- The Recently added page's own design.

## 3. Decisions made

D3 to D6, D8 and D11 were answered by Shy on 2026-10-18 at 23:51Z. D9 follows from the code findings and Shy's answer on "Matched". D12 and D13 carry out Shy's direction on batches.

- **D1.** **One counting model, owned by the Engine.** The Engine works out the run's numbers once (files found, per-type tally, step reached by each file, state) and sends them; the Dashboard only displays them. Reason: Maintenance and Reliability. Today there are two formulas that disagree (see Findings).
- **D2.** **The bar is the sum of each file's step.** Every file passes through the same ordered steps (found, settled, read, scored, registered, identifying, hydrating, enriching, done). The bar is steps completed across all files still in the queue divided by steps possible, so it never changes units. Files that arrive mid-run join the queue; the displayed bar never moves backward, and the page says "120 more found" instead. Reason: Reliability.
- **D3.** **"View all" stays, as the Activity log** (Shy, 00:01Z; this replaces his earlier "retire"). The live page shows what is happening now; the log shows what happened overall, with search and filters, and covers system events too ("Couldn't find FFmpeg"). Arriving from the Added summary pre-filters to added media. Reason: one representative view of the whole system, in line with D8.
- **D4.** **Show a time estimate only when it is reliable; otherwise show elapsed time** (Shy). "Reliable" starts as: at least 15 percent of the work done and the last three estimates within 25 percent of each other; these numbers are tuned against a replayed run (WP2).
- **D5.** **Needs Review is an administrator banner across the top of Settings** with its own page (Shy). Only server administrators see it, matching the existing rule that only server administrators resolve review. Today Settings already tracks the review count but only the Overview shows it, so the banner described in the presentation rules is not present; the review list is a collapsed block inside Recently Added. It moves to its own page and the collapsed block goes.
- **D6.** **Menu indicator: blue while working, green when idle** (Shy). No amber state, because the Library is nearly always waiting for something; items that need a person go in the banner (D5). This changes the presentation rules, which today say green while active and hidden when idle (WP5 updates them).
- **D8.** **One activity list for every screen** (Shy: "no uniformity across data" is the problem). The live page, the Overview's recent activity and the indicator tooltip read the same Engine list and use the same wording.
- **D9.** **One definition of Ready, owned by the Engine.** Ready = matched to a real title (retail match settled, not in review, not no-match, not failed) + placed in its parent (show, album or series, when its type has one) + artwork present or settled. Wikidata and later enrichment are not required (Shy: "Matched", so items appear quickly and enrichment updates them as it goes). Reason: Reliability and Maintenance; today two different rules exist and neither means this (see Findings).
- **D10.** **Gate on Ready, everywhere** (Shy chose the gate; the Read, Listen and Watch wording is simplified, see Q12). An item that is not Ready appears only on the live page. This removes any need for a "finishing" badge.
- **D11.** **Gate per item** (Shy). Each item appears when it is Ready; nothing waits for the rest of a batch.
- **D12.** **No batches a person can see** (Shy). The Now panel and the Activity log are flat lists with a timestamp per row. The batch history screen, "Open batch", run links and batch wording are removed. Internally the Engine keeps one "active period" (idle to idle) only to size the bar.
- **D13.** **The Now panel is a small rotating window; counts only for photos and videos** (Shy, 00:01Z). It shows 5 to 10 items being worked on, starting with the most recently active; a row stays at least 3 seconds (starting values, tuned). Photos and personal videos show as counts everywhere and never as file rows. The Added summary under it shows counts by type. Because the Now panel is capped and the log is paged (50 rows a page, searchable), no separate "collapse when 50 arrive" rule is needed. Reason: Performance and clarity.
- **D14.** **Batches are removed from the Engine as well as the screens** (Shy: no reason to keep a bloated codebase). Reason: Maintenance. WP11.
- **D15.** **The missing-tool alert joins the one administrator banner** (Shy). The banner is built here as a shared piece; its look and place in Settings belong to the Settings revamp (Group 5), which should list it.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Normal, working | Headline "Found 508 files"; tally by type; one blue bar; flat time-stamped table with fixed columns |
| Empty / idle | "Library is up to date", green dot, last activity time; History below |
| Loading | Skeleton of the same layout, not a spinner page |
| Error (Engine unreachable) | "The Library's engine isn't responding" with Retry; last known numbers greyed |
| Waiting | Panel naming what is missing and how many files wait, with a button to Tools and requirements; bar paused, not red; the same note appears in the administrator banner |
| Partly ready | Rows finish individually; a row can be Done while the run is Working |
| Phone | Headline, bar, tally and filter chips; the table becomes a list with the same order of facts |

Fixed table columns: **Title**, **Type**, **Step**, **Status**, **Details** (artwork, text, people, links). The Details slot always shows all five symbols; one that does not apply to that type shows a dim dash, so nothing shifts. Hovering any symbol says the same thing every time.

## 5. Data / model changes

A single stored "ready" state per work replaces the two read-time rules (the Home visibility rule and the Library list's "visible" rule), set by the Engine when the last Ready condition is met. Pre-release, a reset is acceptable.

The Engine's batch progress message gains: files found, a per-type tally, a single progress value, and a run state (Working / Waiting / Done). The estimate field is removed from the page, not from the wire until a later clean-up (Contracts tests and fixtures updated with it). No stored-data change; pre-release, a reset is acceptable.

## 6. Work packets

### WP1 — One counting model (Engine)
- **What it does:** Builds the run's numbers once from the file operation rows: files found, per-type tally, per-file step, run state including Waiting. Replaces the two competing percentage formulas.
- **Files likely touched (to be confirmed):** `src/MediaEngine.Providers/Services/BatchProgressService.cs`, `src/MediaEngine.Contracts/Realtime/IngestionEvents.cs`, `src/MediaEngine.Api/Services/ReadServices/IngestionPresentationReadService.cs`, contract fixtures and tests.
- **Depends on:** Group 1's "waiting" state (WP3 there)
- **Size:** Complex
- **Done when:** a test feeds 500 mixed files through every step and shows progress never decreasing and never changing units at the end of intake; a test with held files reports Waiting, not Done and not Failed.

### WP2 — Reliable estimate or elapsed time
- **What it does:** Shows "Running for N min" until the estimate passes the reliability test (D4), then shows it. The estimate uses the whole-queue step rate, not the intake speed.
- **Files likely touched:** `src/MediaEngine.Web/Components/Settings/IngestionTasksTab.razor`, `IngestionBatchProgressSummary.cs`
- **Depends on:** none
- **Size:** Mechanical
- **Done when:** a replay of a recorded 500-file run never shows an estimate off by more than 25 percent, and shows elapsed time before the test is met.

### WP3 — Headline, tally and bar
- **What it does:** Shows files found first, the running tally by type, and the single bar from WP1. The Dashboard stops deriving its own percentage.
- **Files likely touched:** `IngestionTasksTab.razor`, `IngestionLiveDashboardState*.cs`, `IngestionBatchProgressSummary.cs`
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** the Dashboard has no percentage calculation of its own (guardrail test); headline and tally match the Engine's numbers in a test.

### WP4 — Live "Now" panel, Added summary, no batches
- **What it does:** Replaces the "Being added now" cards, the batch history and the old "View all" link with a rotating Now panel (5 to 10 rows, minimum dwell per row), a short Added summary by type, fixed columns and consistent symbols and tooltips. Photos and personal videos show as counts. Removes "Open batch" and run links.
- **Files likely touched:** `IngestionTasksTab.razor`, `IngestionMediaCard.razor`, `IngestionFacetIndicators.razor`, `IngestionMediaPagedView.razor`, `IngestionRecentlyAddedPreview.razor`, `IngestionBatchHistory.razor`, `IngestionBatchDisplay.cs`, their CSS
- **Depends on:** WP3
- **Size:** Standard
- **Done when:** a screenshot test at desktop and phone width shows identical column positions for a movie, a show, an album and a book; with 500 items in flight the panel never shows more than 10 rows and rows rotate; 3,000 photos produce a count and no file rows; the page contains no batch wording.

### WP5 — Menu indicator colours
- **What it does:** Blue while working, green when idle, nothing else; the tooltip uses the same words as the page. Updates the presentation rules, which currently say green while active and hidden when idle.
- **Files likely touched:** `Components/Navigation/SystemActivityIndicator.razor` and CSS, `Services/Integration/ShellActivityState.cs`, `docs/product/presentation-rules.md`
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** both states shown in a screenshot; the colours are theme tokens (secondary accent for blue), not hard-coded.

### WP6 — Administrator banner and Needs Review page
- **What it does:** A banner across the top of every Settings page for server administrators when anything needs them (Needs Review items; a missing tool from the first-run spec), linking to a Needs Review page that holds the review list moved out of Recently Added.
- **Files likely touched:** `Components/Pages/Settings.razor`, a new Needs Review page, `RecentlyAddedPageContent.razor`, `Operations.razor` redirects
- **Depends on:** WP4
- **Size:** Standard
- **Done when:** a non-administrator sees no banner and no review request; an administrator sees the banner on every Settings page and can open and resolve an item from the new page; the old collapsed block is gone from Recently Added.

### WP7 — One activity list on every screen
- **What it does:** The Overview's Recent Activity and the live page's History use one shared component fed by the same Engine list, with the same wording; the Overview's "View all activity" opens the Activity log.
- **Files likely touched:** `Components/Settings/OverviewTab.razor`, `IngestionTasksTab.razor`, the indicator tooltip
- **Depends on:** WP4
- **Size:** Standard
- **Done when:** the same event shows with identical text and time in the live page, the Overview and the tooltip (one test feeds one event to all three).

### WP8 — "Something went wrong" investigation
- **What it does:** Reproduces the Cibola Burn error with a half-added item, finds the source in the Engine log, fixes the cause, and wraps each table row so one bad row cannot take down the page.
- **Files likely touched:** to be confirmed by Claude Code (the exact words are not in the Dashboard; the nearest is a generic screen-error message in `Components/Routes.razor`)
- **Depends on:** WP4
- **Size:** Standard
- **Done when:** a test with a row missing its artwork, title or type still shows the row; the log line that caused the original is explained in the spec.

### WP9 — One Ready definition
- **What it does:** Adds the single Ready state (D9) and makes Home, hero, lanes, Watch and the Library list use only it. Removes the two old rules. Adds a guardrail test: display queries must use the shared Ready rule.
- **Files likely touched (to be confirmed):** `src/MediaEngine.Storage/HomeVisibilitySql.cs`, `LibraryItemRepository.cs`, `src/MediaEngine.Api/Services/Display/DisplayWorkProjectionReader.cs`, `DisplayJourneyProjectionReader.cs`, `RecentCatalogueReadService.cs`, schema step, `docs/reference/database-schema.md`
- **Depends on:** none
- **Size:** Complex
- **Done when:** tests show a work that is queued, matching, in review, no-match or failed never appears in Home, hero, lanes or Watch; a matched, placed work with artwork does; the guardrail test fails if a display query omits the rule.

### WP12 — Activity log page
- **What it does:** Turns "View all" into a paged, searchable log of everything the Library did: added media, Needs Review, failures and system events. Filters: Added (default from the summary), Needs Review, Failed, System, All. Makes sure system events such as a missing tool, a refused download or a provider outage are written to the log in plain words, so the System filter is useful.
- **Files likely touched (to be confirmed):** the page reached from `/settings/ingestion`, `src/MediaEngine.Api/Endpoints/ActivityEndpoints.cs`, `src/MediaEngine.Api/Services/ReadServices/ActivityBatchReadService.cs` (to be rebuilt without batches), `src/MediaEngine.Contracts/Activity/`, the log writers for tool and provider events
- **Depends on:** WP4, and Group 1's tool-missing entries
- **Size:** Complex
- **Done when:** with FFmpeg hidden, the System filter shows a plain "Couldn't find FFmpeg" entry; a search for a title finds its Added entry; arriving from the Added summary shows only added media; 3,000 photos appear as one count row; the page is paged and fast with 50,000 entries in a test data store.

### WP10 — Waiting-for reasons
- **What it does:** A row on the live page that is not yet Ready says what it is waiting for (matching, placing in its show, artwork).
- **Files likely touched:** `IngestionMediaCard.razor` and the Engine's row data
- **Depends on:** WP9, WP4
- **Size:** Standard
- **Done when:** a screenshot shows rows each naming their missing step; the text is the same everywhere it appears.

### WP11 — Remove batches from the Engine
- **What it does:** Deletes the batch tables, batch counters, batch progress events and batch wording from the Engine, Contracts and Dashboard, once nothing reads them. The bar and list run from the per-file work rows. Data store reset is acceptable pre-release (epoch bump).
- **Files likely touched (to be confirmed, about 40 files):** `src/MediaEngine.Ingestion/*`, `src/MediaEngine.Providers/Services/BatchProgressService.cs`, `src/MediaEngine.Api/Services/*Batch*`, `src/MediaEngine.Contracts/Realtime/IngestionEvents.cs`, `src/MediaEngine.Storage/Schema/Upgrades/`, contract fixtures, `docs/reference/database-schema.md`
- **Depends on:** WP1, WP3, WP4, WP7, WP12
- **Size:** Complex
- **Done when:** no code, table or wire name mentions a batch; full build and tests pass; contract snapshots refreshed; ingesting 500 files still shows the bar, tally and list.

## 7. Risks and open questions

**Findings (confirmed in the code unless marked inferred)**
- **Bar bounces (5).** Two formulas feed it. The Engine switches the bar from "files" to "identity jobs" the moment intake finishes (`BatchProgressService.GetProgressAsync`), then the Dashboard averages a third measure on top (`IngestionBatchProgressSummary`). The total also grows when more files land in a running batch (`IngestionEngine.Watching.cs`).
- **Estimate (20).** It divides by how fast files were *registered*, which is quick, then applies that speed to the slow identity work. It is not the item count; it is mixed units. Shy decided it is shown only when reliable (D4).
- **Counts (6).** The page mixes files, identity jobs and "media groups" under the word "items".
- **"0 items processing" (21).** The "Being added now" count only includes groups that already have an identity and cover art (the empty-state text says so). Queued, waiting or failed files count as zero. Group 1 already fixes why video files failed.
- **Jumping icons (4).** The status text appears only when a row is not Ready, and the five detail icons are filtered per row by "not applicable".
- **Menu indicator (15).** Today it is neutral when active, orange for playback and amber for warnings; there is no idle colour. The presentation rules say green while active, hidden when idle, so the code and the rules already disagree.
- **Needs Review (28).** A "Needs attention" link goes to Settings, Recently Added, review section (administrators only), which exists. Why it felt like "no page" is not reproduced; the banner and page in D5 answer it.
- **Bug 27.** Inferred: the Overview's "Recent Activity" has a table the live page lacks. Shy confirmed the Overview activity is not in step with other pages (D8).
- **Bug 13.** Not reproduced. Needs the Engine log from that moment (Shy's PC: the temp-folder `engine.log`; this thread cannot read it).

- **Home and the hero show unidentified media (11).** Home's "visible work" rule (`HomeVisibilitySql.VisibleWorkPredicate`) only asks that a work is not rejected or provisional, has a file on disk, and is not catalogue-only. It never asks whether matching finished. The Library list uses a different rule (`library_visibility`): a title (a name taken from the file counts), a media type, and artwork present or "settled" (settled can mean "gave up looking"). Neither means "fully added". Helpers that would exclude items in review or no-match (`PendingReviewExclusion`, `LatestNeedsReviewStateExclusion`, `LatestTerminalIdentityStateExclusion`) exist, but a search finds no display query that uses them.
- **Every episode is its own show in Watch (22).** Inferred: shows are grouped through a parent work, which is only created once the show is identified, so before that each episode file is a standalone work and is listed as one. The same will happen to tracks and albums. Not reproduced by a test yet; WP9 includes one.
- **Item G.** Matching order is already per file: retail match, then the Wikidata bridge, then Quick Hydration. A whole-batch barrier would delay everything to the slowest file and would not fix grouping by itself. Shy chose the per-item gate (D11).
- **Batches.** The batch idea reaches about 40 files across the Engine, Contracts and Dashboard (ingestion, progress, notifications, activity reads, the batch tables). Removing what people see is contained (WP4, WP7); removing the Engine's tables and code is bigger and is WP11, last, so nothing reads them when they go.
- **Needs Review banner (5, 28).** The presentation rules say review counts stay visible throughout Settings. The code tracks the count but only the Overview shows it, and I could not tell from history when a banner was removed.

**Risks:** removing the estimate loses a reassurance some people like; the per-file step model needs a stable list of steps, so a new pipeline step later must be added in one place. A rewritten bar changes numbers other screens quote; WP3 checks every user of the old figure.

**Open questions:** none. All were answered by Shy on 2026-10-18 and 2026-10-19 and are recorded as decisions. (The earlier "50 items" limit is no longer needed; see D13.)

**Still needing information:** bug 13 ("something wrong" on Cibola Burn): the Engine log from that moment. Bug 28: with the banner and page above, is "where is the review page" answered? 

## 8. Plain English summary

The app disagrees with itself in two ways. It counts progress three different ways, so the bar jumps, the clock is wrong and the headline says "0". And it has two different ideas of when something is "added", so unfinished items and lone episodes show up on Home and in Watch. The plan fixes both at the source: the Engine counts once and every screen just shows it, and an item is "Ready" only when it is matched, placed in its show or album, and has its artwork. Only Ready items appear anywhere in the Library; everything else is visible only on the live page. The live page shows what is happening right now: a handful of rotating items, with a short summary of what was fully added underneath. "View all" stays and becomes an Activity log of everything the Library does, searchable and filterable, including system events like a missing tool. There are no batches, photos and videos show as counts, and a time estimate appears only when it can be trusted. The menu dot is blue while working and green when idle. Anything needing an administrator, such as Needs Review, becomes one banner across the top of Settings with its own page.
