# Spec: Library activity

> **Status:** Built (Version 3 built 2026-11-14 in the cloud, awaiting Local checks L10 to L16 in `engineering/validation/batch.md`; versions 1 and 2 are built and await Local checks) · **Version:** 3 · **Last updated:** 2026-11-11 (v1 locked by Shy 2026-11-06; v2 changes come from Shy's own local-check decisions of 2026-11-07 and are recorded here for confirmation) · **Related docs:** [Libraries and folder picker](libraries-and-folder-picker.md) · [Libraries redesign](libraries-redesign.md) · [Library pages](library-pages.md) · [Library access](library-access.md) · [Look pass](look-pass.md) · Mockups: `../design/mockups/library-activity.html`

> Shy's reference pictures (5 and 6 Nov) stay on SHY-DESKTOP and are not committed. The tile size and overlays follow the One tile design spec as it finally stands; the mockups draw its draft values (214 × 321 desktop tile).

## 1. Goal

When Library is scanning, identifying or fetching details, an administrator can see what is happening from one clear page, and the spinner in the top bar takes them straight to it. Under it, a timeline of past imports answers "what was added, and when". Today the spinner opens the Libraries page, which shows one line of text while a scan runs, and nothing groups imports.

## 2. Scope

**In scope**
- One Activity page (Settings › Library › Activity): **Working now** at the top while something runs, and **History** as a vertical timeline underneath. With nothing running it opens on History.
- Where the top-bar spinner goes, and how the Libraries scan card links to this page.
- A library's own Activity tab as this same page filtered to that library.
- Recording one "run" per scan, refresh, fix and removal in the Engine, so History can group imports.
- The rule for the "New" tag on tiles (D14).

**Out of scope**
- Photos, personal videos and documents in Activity, and a Recently added row for View and the Home View pill (D17; moved to the thread "Review Photos UI and recently added").
- Building the Libraries scan card, header and left-rail removal (the Libraries fix build and the Libraries spec revision own them; this spec only says where View activity goes).
- Logs and Events under Settings › System › Logs (developer-facing, unchanged; the old `/operations/activity` address still redirects there).
- Needs Review and Unidentified files screens (this page links to them).

## 3. Decisions made

All answered by Shy (5 to 6 Nov 2026).

**Where it lives**
- **D1.** One Activity page, not a tab inside Libraries; the Libraries scan card is a summary that links to it. Reason: Maintenance (one place to keep right) and Ease of use.
- **D2.** The top-bar spinner opens the Activity page.
- **D3.** Administrators only, like the library settings. For everyone else there is no Activity entry in the navigation, no spinner, and no History; the page is not reachable.
- **D4.** The Libraries scan card stays as in the Locked Libraries mockup (what is scanning, files discovered, time elapsed, bar with percent, View activity). Scanning rows show a Scanning status only; no per-row bars. Nothing else on the Libraries page is changed here.

**Working now**
- **D5.** One progress card for everything Library is doing: "Updating your library", percent, and counts (found by kind, ready, need review, processing, queued) with Review N items. Its subtext uses the same wording as a finished batch line ("412 movies · 85 episodes · 51 tracks found"). One overall bar; no per-library bars and no per-library Engine numbers. The library chips at the top also filter the card (with Watch picked it counts only Watch). When nothing is running the card is replaced by "Everything is up to date · Last completed scan …".
- **D6.** Below the card, **Working now** is a row of standard tiles (D13). Each carries the flush top-left status label for what Library is doing to that title ("Finding people", "Getting artwork") and a flush bottom-left count or step. Clicking a tile opens that item's page. The section is hidden when nothing runs.
- **D7.** Background work (AI, maintenance) has no bar of its own. It counts in the card's total, and while any is running a plain Background line lists it. Hidden when none.
- **D8.** "Needs you" is not a separate card: failures and unsure files are reached through Review N items and the Libraries attention strip. A folder that goes offline shows as a line with Check again.

**History**
- **D9.** History is one entry per batch (a scan, refresh, fix from Needs Review, or removal), on a vertical timeline: a round bubble holding the date ("Nov 6") for each entry on a line, under month and year headings. Each entry shows what was added and where, up to five small covers, a plain count and Completed. A batch that is still running is the newest entry and says **Active** (with how far along), then turns Completed.
- **D10.** Month and year headings collapse and expand. The current month starts open; older months and years start collapsed with their batch count ("September 2026 · 9 batches", "2025 · 63 batches"). Searching expands every heading that holds a match.
- **D11.** Clicking an entry opens it in place (no separate page): its items as standard tiles in a scrolling area that shows two rows at a time, as many across as fit the window, and loads more as you scroll, with a filter by kind. No file or enrichment detail (that lives in the item's editor). Clicking a tile opens that item's page.
- **D12.** An opened batch stacks its items: episodes into one tile per show (or season), tracks into one per album, other videos into one per root folder, books and audiobooks per series or author. Movies are one tile each. A hundred added files read as a few dozen tiles, never thousands.
- **D13.** Tiles follow the One tile design spec exactly (size, "..." menu, flush labels, no caption). Square art (albums) and portrait art (posters) have the same height in the small covers and in the opened tiles.
- **D14.** **"New" is decided by run**, and this is the one definition the tile spec points to. A library's first run (its initial load, including files that trickle in before that run closes) never marks anything New. An item is New when a later run added it and that run finished within the last 14 days. The same rule serves Home and every tile.
- **D15.** Speed: the collapsed entry uses small cached versions of the covers, so History loads fast; an opened batch loads its pictures only as they scroll into view.
- **D16.** The History controls are one row: search, type (Added, Updated, Fixed, Removed), date range (Today, 7 days, 30 days, year, custom) and sort by time (Newest first, Oldest first). There is no A–Z and no sort by name; search covers finding things. The library chips at the top apply to the whole page. There is no subtitle under History.
- **D17.** Photos, personal videos and documents never appear in Activity (View is personal per profile; Activity is administrators only). View's Recently added is handled in a separate thread.
- **D18.** Search is an instant filter as you type. A title found in several batches appears **once** as a standard tile with the total in its bottom-left label ("12 new episodes"), and beside it that title's own vertical timeline (date bubbles on a line, with how much was added each time, under month and year headings when there are many). So searching "The Expanse" shows today's 4 episodes and the 5 from another date as one card, not as many batches. Matching follows the stacking rule (D12).
- **D19.** History follows Vault rules: a Vault library's runs show no titles or covers outside the Vault activity list (Library pages D16, Vault D40).
- **D20.** History is kept forever (it is mostly text), with an administrator action to purge or trim runs older than a chosen date.

**Version 2 changes (from Shy's local check, built 2026-11-10)**
- **D22. The progress card is very simple (changes D5).** One line with the top bar's ring (it fills with the percent), "Updating your library" and the percent; under it one counts line and Review N items. There is no separate bar and no "Started … ago" line.
- **D23. A truthful percent.** The percent is rounded down and is never 100 while files are still being worked on, so 100% always means finished. The card and the top bar's ring show the same number, worked out once.
- **D24. Working now is one live row (changes D6).** The tiles never wrap onto several rows; a long run scrolls sideways.
- **D25. One History entry per scan across libraries.** A scan that covers Movies, Books and Audiobooks is one entry that names all its libraries and adds their counts. Each library still keeps its own run underneath (its first-run flag and the library chips depend on that); runs that open in the same scan share a scan id.
- **D26. A library's first import can arrive in several waves (changes D14).** While the Engine has been busy without a break since the library's first run began, every run of that library counts as first and nothing is tagged New. "New" only starts once the Engine has gone quiet and a later import arrives.
- **D27. Files nobody picks up cannot hold the page "working" (#708).** A file that is queued with no asset and has not changed for 10 minutes counts as waiting ("Some files are waiting · 3 files are waiting"), the run can finish without it, and the Libraries status line says so instead of "Adding 3 files" for hours.

**Recording runs**
- **D21.** The Engine records a run as each scan, refresh, fix or removal happens, with true duration, counts and the list of titles touched. A run starts where the Engine already lists files in one pass (the library scan) with the full count taken then. Files that arrive one by one (a watched folder, the Import folder) join the open run for that library until 5 quiet minutes pass. History starts from the first run after the build; there is no back-filling of older imports (pre-release).

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Busy | Libraries: scan card with bar and View activity; scanning rows say Scanning. Activity: progress card, Working now tiles, Background line, then History with the newest entry Active. |
| Idle | Libraries: scan card reads "Idle · Last completed scan 12 minutes ago", no bar. Activity: "Everything is up to date", then History. |
| Empty (no history yet) | "Nothing has been added yet. New imports will appear here." with the Add library button when there are no libraries. |
| Loading | The progress bar is indeterminate; History shows the standard loading state; covers fill in as they load. |
| Error | "Activity could not be loaded" with Try again (existing wording). |
| Partly ready | Total not known yet: indeterminate bar and "412 files found" with no percent. |
| Not an administrator | No spinner, no Activity entry in the navigation. |
| Searching | Matches only, each title once with its own timeline; headings with matches expand. |
| Phone | Everything stacks to one column; the tiles use the phone tile size from the One tile design spec. |

The spinner's tooltip is unchanged ("Working · …" or "Idle"); clicking it opens Activity. Under Settings, Activity keeps the History icon it has today.

## 5. Data / model changes

- **Runs.** The Engine had batch tables, but migration 0018 removed them, so today each file is tracked alone (an intake operation plus an identity job) and the Engine cannot say "these 14 files were one run". Add a small "activity run" record: library, kind (added, updated, fixed, removed), start, end, status (Active, Completed, Failed), counts by kind, whether it is the library's first run, and the titles touched (with a title search index). A library scan already lists every file in one pass, so the run only has to keep that count and list. Pre-release: no migration and no back-fill.
- **Wire types.** New run and run-item types in `src/MediaEngine.Contracts/`, with fixtures and tests (Never 3).
- **Progress.** The existing overall ingestion progress is reused; no per-library numbers (D5). Add counts by kind to its subtext if they are not already there.
- **Retention.** Keep all runs; add the administrator purge/trim action (D20).
- Background items already exist (AI, enrichment, maintenance); no change.

## 6. Work packets

### WP1 — Activity runs in the Engine
- **What it does:** record one run per scan (flagging each library's first run), refresh, fix and removal with counts, titles touched and status; join late-arriving single files to the open run for 5 quiet minutes; read runs by month with filters and title search; purge/trim action; Active state while running.
- **Files likely touched:** Storage schema and a new repository, Engine intake/refresh/fix/removal paths, `src/MediaEngine.Contracts/`, fixtures and tests (to be confirmed).
- **Depends on:** none · **Size:** Standard
- **Done when:** a test that ingests 14 files in one pass returns one run with 14 items, a duration and Completed; a removal of 3 items returns one "Removed 3 items" run; a library's first run is flagged; a title search returns the matching runs.

### WP2 — Progress card, Working now and Background
- **What it does:** the overall progress card with counts by kind and Review N items; Working now tiles with status labels; the Background line; the idle sentence; library chips filtering the card.
- **Files likely touched:** `Components/Pages/RecentlyAddedPageContent.razor(.css)` and new components, `Services/Integration/ShellActivityState.cs`, strings (to be confirmed).
- **Depends on:** none · **Size:** Complex
- **Done when:** bUnit tests show the card with counts, a tile with its status label per working title, the Background line only while background work runs, and the idle sentence when nothing runs.

### WP3 — History timeline
- **What it does:** the vertical timeline of D9, D10, D11, D12, D15, D16: date bubbles, collapsible months and years, entries with small covers, opening in place into tiles that load as you scroll, stacking rule, filters, instant search with one card per title and its own timeline (D18).
- **Files likely touched:** new History components and styles, shared tile parts, strings (to be confirmed).
- **Depends on:** WP1 · **Size:** Complex
- **Done when:** bUnit tests show headings that collapse, an entry opening into stacked tiles (episodes into one show tile), a search for a title showing it once with two dated markers, and no A–Z control anywhere.

### WP4 — Spinner, navigation and the scan card link
- **What it does:** the spinner opens Activity; Activity appears in navigation only for administrators; View activity on the Libraries scan card opens Activity; a library's Activity tab renders this page filtered to that library.
- **Files likely touched:** `Components/Navigation/SystemActivityIndicator.razor`, `Components/Settings/Libraries/LibraryActivityTab.razor`, navigation model, the scan card (from the Libraries fix build).
- **Depends on:** WP2 · **Size:** Standard
- **Done when:** a test shows the click navigates to `/settings/library-activity`; a member session has no Activity entry and no spinner; both entry points show the same rows for the same library.

### WP5 — "New" tag rule
- **What it does:** expose "is New" from the run data (D14) so tiles show New only for items added by a later run within 14 days.
- **Files likely touched:** the read model the tiles use, with the One tile design spec's tile code (to be confirmed).
- **Depends on:** WP1 · **Size:** Standard
- **Done when:** a test shows nothing is New after a library's first import and an item from a later import is New for 14 days, then not.

### WP6 — Docs
- **What it does:** public docs page on the Activity page, History and what the spinner means, with the why; update the presentation rules and engineering index.
- **Depends on:** WP2 to WP5 · **Size:** Mechanical
- **Done when:** the docs build passes and the page is listed in its manifest.

## 7. Local checks

- **L1.** On a small test library, start a scan. The spinner turns blue; click it and Activity opens with the progress card moving, Working now tiles with status labels, and a History entry saying Active. Failure: it lands on Libraries, or there is one bar per library.
- **L2.** Let the scan finish. The spinner turns green; the card is replaced by "Everything is up to date"; the entry turns Completed and shows its covers. Failure: the entry stays Active.
- **L3.** Open a finished entry. Two rows of standard tiles appear with episodes stacked into one tile per show; scrolling loads more. Square and portrait art are the same height.
- **L4.** Type a show's title in search. It appears once with its own vertical timeline of dates; months with matches are open.
- **L5.** Collapse and expand months and years.
- **L6.** Sign in as a non-administrator. There is no spinner and no Activity entry.
- **L7.** Check the Libraries page after the fix build: scan card with View activity opens Activity.
- **L8.** Drop three new files into a watched folder, then more after 5 minutes. They form two entries. Photos and documents added never create entries.
- **L9.** Check the first import of a brand-new library shows no New tags, and a later import does.

## 8. Risks and open questions

- **Risks:** run recording touches the intake paths (Reliability): a missed run only loses a History line and never blocks ingestion; test it. The title search over runs must stay fast with a lot of history (Performance); keep an index. The Libraries fix build must keep View activity on the scan card.
- **Open questions:** (v2) A PDF found inside a video library is still not given a final "Unmatched / ignored" state by the Engine; D27 only stops it keeping the page busy. Confirm whether to follow up. This spec depends on the Libraries scan card that the Libraries fix build and the Libraries spec revision are delivering; if that build changes the card, only WP4 is affected.

## Plain English summary

Activity becomes one page for administrators. The top shows what Library is doing right now, with one progress bar and tiles for the titles being worked on. Below it is a timeline of past imports with month and year headings you can collapse. Each import opens in place to show what was added, stacked so a hundred files read as a few dozen tiles. Search shows each title once with its own dates. To make this work, the Engine starts recording one entry per import, which also lets the "New" tag appear only after the first load. Photos and documents stay out, and the top-bar spinner opens this page.

---

## Version 3: Activity page cleanup (Locked by Shy 2026-11-11)

Covers issues #866, #880, #868, #863, #864 and #862 from the 11 Nov local check, and unblocks #867 (the count next to Libraries in the Settings menu, owned by the Libraries work). Nothing in versions 1 and 2 above is rewritten; where a decision below changes an earlier one, it says which. Out of scope: the Needs Review screen itself, the search results view, and the month and year headings.

### Goal

During a busy import the Activity page should be calm, and every number on it should be one you can trust. Reason: Reliability (numbers that agree) and Ease of use (less on screen).

### What was found (causes)

These come from reading the code, not from re-running the import. Each build packet starts by reproducing the problem in a test, so a wrong guess shows up early.

| Issue | What you saw | Likely cause |
|---|---|---|
| #866 | Card said "Review 13 items", the Needs Review tab said 8. | Two different counts. The tab counts the entries actually waiting in the Needs Review list. The card counts titles in the current import that look unsure, which includes titles whose match is still undecided and not yet in the list. |
| #880 | Working now still showed titles after the card said everything was done. | Working now treats two extra things as "in progress": a title that is waiting for a person in Needs Review (it stays until someone resolves it), and a title with any follow-up job still open, such as making the preview pictures for video scrubbing. The card ignores both. |
| #868 | One import showed as two batches. | A batch closes after 5 quiet minutes, measured from the last title that finished. On a long import (large videos) more than 5 minutes can pass between finished titles while the Engine is still busy, so the batch closes early and the next title opens a second one. |
| #863 | Chips changed the card but not the batches. | The chips pick which batches are listed, but a batch that covers several libraries still shows all its counts, covers and items. |

### Decisions made (Version 3)

Already decided by Shy on 11 Nov: the top filters narrow the timeline and the filter inside a batch is removed; the word "batches" stays; the card shows one "need review" pill and no "unmatched" pill.

- **D28. One "need review" number (#866).** The Engine works the number out once, the same way the Needs Review list does (what is waiting for a person now). The Activity card, the Needs Review tab, the administrator banner and, later, the Settings menu (#867) all show that number. Effect to know about: the pill counts everything waiting in Needs Review, not only what this import added, because that is what the link opens. With a chip picked the pill still shows the whole number, so it always equals the tab (Shy, 11 Nov).
- **D29. One rule for "working" (#880, changes D6 and D7).** A title is in Working now only while its files are being added, identified or having their details fetched. A title that only waits for a person is not "working"; it is counted in the "need review" pill. Follow-up jobs that run after a title is usable (preview pictures for scrubbing, and similar) appear on the quiet Background line, for example "Background · Making preview pictures for 12 videos". The card, the top-bar ring and Working now use this one rule, so when the card says "Everything is up to date", Working now is gone. The top-bar spinner still shows background work in its tooltip, as today.
- **D30. One batch per import (#868, changes D21).** A batch cannot close while the Engine is still busy with that import. The 5 quiet minutes only start counting once the Engine has gone idle. If the Engine is stopped part way and picks the same work up again on restart, the titles join the batch that was cut off instead of starting a new one. Files dropped in later, after the Engine has been idle for 5 minutes, still make a new batch (L8 is unchanged).
- **D31. One set of filters for the whole page (#863, changes D11 and D16).** The chips at the top (All libraries, Watch, Listen, Read) narrow the card, Working now and the timeline together. With Watch picked, a batch with nothing for Watch is hidden, and a mixed batch shows only its Watch counts, covers and items. The filter by kind inside an opened batch is removed. The History row (search, type, date range, sort) stays as it is. A library's own Activity tab narrows the same way to that library.
- **D32. A simpler progress card (#864, changes D5 and D22).** One slim card, following Shy's picture:
  - Left: the same animated sync icon as the top bar's indicator, the title, and a purple "In progress" dot-label.
  - Under the title, one line: "427 of 538 files processed · 16 min elapsed".
  - Right: one amber pill, "2 need review", which opens Needs Review. Hidden at zero.
  - Bottom: a thin purple bar across the full width with the percent at its right end.
  - Gone from the card: the counts by kind, and the ready, processing and queued numbers.
  - Kept: the truthful percent (D23: rounded down, never 100 while working, same number as the top bar), files as the only denominator (no provider totals for TV or comics), "412 files found" with a moving bar when the total is not known yet, the idle sentence, and the "Some files are waiting" line (D27).
  - Note: on 10 Nov (D22) the bar and the "Started … ago" line were removed. Shy's 11 Nov picture brings back a thin bar and an elapsed time, so D22 is replaced on those two points.
  - The title stays "Updating your library" (Shy, 11 Nov: the card also covers identifying titles and fetching details, not only scanning).
  - The picture's "View activity" button is not shown on the Activity page itself (you are already there); it appears on the Libraries page (D36).
- **D33. Slimmer batch rows (#864, changes D9).** Following Shy's picture:
  - Month heading: "November 2026 · 2 batches" with the collapse arrow; a vertical line with a dot and a date label per batch ("Now", "Nov 11").
  - A running batch: purple left edge, a title such as "Adding audiobooks" (when one kind is being added) or "Adding items" (several kinds), one line "2 added so far", up to two small covers, a purple "Active" dot-label and a chevron.
  - A finished batch: a title such as "250 items added" ("12 items updated", "3 items fixed", "3 items removed" for the other types), one line of counts by kind ("91 videos · 82 tracks · 37 episodes · 25 books · 9 audiobooks · 6 movies"), three small covers and "+247", a green check "Completed" and a chevron.
  - Changes from today: three covers instead of five, one status at the right, and library names no longer on the row.
  - Opening a batch is unchanged (stacked tiles, two rows, loads as you scroll), without the kind filter (D31).
- **D34. Working now fits the width (#862, changes D24).** Tiles are about half today's size. The row shows only as many as fit across, with no scroll bar at any width. At most 10 show at a time and each stays at least 3 seconds; when more titles are in progress than fit, the row rotates through them. The heading says how many there are in all ("Working now · 34 titles") so the smaller row does not hide the scale of the work. Clicking a tile still opens that item's page.
- **D35. The phone matches the desktop (Shy, 12 Nov standing rule).** Most people use only their phones, so every part of this page works on a phone as it does on a desktop: the card, the pill, the chips, Working now, the Background line, the timeline, opening a batch, search and the History filters. Only the layout changes to fit. The one exception that stays is Add library in the empty state, which is setup.
- **D36. The Libraries scan card takes the same look (Shy, 11 Nov; changes D4).** The Libraries page shows this same slim card, built once and shared, so the two cannot drift apart. On the Libraries page it also carries the "View activity" button from Shy's picture; on the Activity page it does not.

### UI behaviour (what changes)

| Situation | What the person sees |
|---|---|
| Busy | Slim card with icon, one line, bar and percent, and the "need review" pill. Working now: one row of small tiles that fits the width. Background line when follow-up work runs. Timeline with the newest batch Active. |
| Import finished, follow-up jobs still running | "Everything is up to date". No Working now. Background line names the follow-up work. The batch says Completed. |
| Idle | "Everything is up to date · Last completed scan …", then the timeline. |
| A chip is picked | Card, Working now and the timeline all show that area only. A batch with nothing in that area is hidden. |
| Total not known yet | Moving bar, "412 files found", no percent. |
| Nothing needs review | No pill. |
| Error, Empty, Loading, Not an administrator | Unchanged. |
| Phone | Everything the desktop has, nothing left out (D35). Card stacks: icon and title, the line, the pill, then the bar. The chips scroll in one line. Working now shows as many small tiles as fit (about 3 or 4) and rotates. Batch rows keep title, the counts line (it may wrap to two lines), covers (two, with the "+N") and status. A batch opens in place into tiles. Search, type, date range and sort are all there, in a bottom sheet behind one Filters button if they do not fit on one line. |
| Tablet, desktop | As described; checked at 1024, 1280 and 1440. |
| TV | Not applicable (administrators' settings page). |

### Data / model changes

- No new tables. The Engine adds the single "need review" number to what it already sends for the Activity card, and sends each batch's counts and covers by kind so the Dashboard can narrow a batch to the chosen area. Both are wire changes, so the Contracts tests and fixtures are updated with them.
- A batch remembers whether it was closed by the Engine stopping (so a restart can continue it). If that needs a new field, it is a numbered data-store step; pre-release, no back-fill.
- Follow-up jobs (preview pictures) are reported as Background work with a plain name and a count.

### Work packets (Version 3)

Numbering continues from the spec's WP6. One build thread, four waves, at most two packets at once.

#### WP7 — One "need review" number (#866)
- **What it does:** the Engine exposes one count, worked out exactly as the Needs Review list counts; the Activity card, the tab and the banner read it; the card's own separate count is removed.
- **Files likely touched:** `Api/Services/ReadServices/IngestionPresentationReadService.cs`, `NeedsReviewListReadService.cs`, `Contracts/Ingestion` and `Contracts/Review`, `Web/.../LibraryActivityProgressCard.razor`, `SettingsAttentionState.cs`, fixtures (to be confirmed).
- **Depends on:** none · **Size:** Standard
- **Done when:** a test with 8 entries in Needs Review and 5 more titles still being matched shows 8 on the card, the tab and the banner; resolving one changes all three to 7.

#### WP8 — One rule for "working" (#880)
- **What it does:** Working now leaves out titles that only wait for review and titles whose only open work is a follow-up job; follow-up jobs are named on the Background line; card, ring and row share the rule.
- **Files likely touched:** `IngestionPresentationReadService.cs` (the in-progress rule), `Web/Services/Integration/ShellActivityState.cs`, `LibraryActivityWorkingNow.razor`, `LibraryActivityPage.razor`, strings (to be confirmed).
- **Depends on:** WP7 (same Engine file) · **Size:** Standard
- **Done when:** a test with a finished import, one title in Needs Review and preview-picture jobs still open shows "Everything is up to date", no Working now section, and a Background line naming the preview pictures.

#### WP9 — One batch per import (#868)
- **What it does:** a batch stays open while the Engine is busy with the import; the quiet 5 minutes count from when the Engine goes idle; a batch cut off by an Engine stop is continued on restart.
- **Files likely touched:** `Api/Services/ActivityRunRecorder.cs`, `ActivityRunSweeper.cs`, `Storage/ActivityRunRepository.cs`, possibly one data-store step (to be confirmed).
- **Depends on:** none · **Size:** Standard
- **Done when:** a test where two titles finish 12 minutes apart during one busy period gives one batch; a test with a stop and restart in the middle gives one batch; a file added after 5 idle minutes gives a second batch.

#### WP10 — The chips narrow the whole page (#863)
- **What it does:** each batch carries its counts and covers by kind; with a chip picked, batches with nothing in that area are hidden and mixed batches show only that area; Working now follows the chip; the kind filter inside an opened batch is removed.
- **Files likely touched:** `Storage/ActivityRunRepository.cs`, `Api/Services/ReadServices/ActivityRunReadService.cs`, `Contracts/Library` run types and fixtures, `LibraryActivityHistory.razor`, `LibraryActivityRunEntry.razor` (to be confirmed).
- **Depends on:** WP9 (same storage file) · **Size:** Standard
- **Done when:** a test with one batch holding movies and books shows, with Watch picked, only the movie count, covers and tiles, and with Read picked only the books; a batch of music only is hidden under Watch; no kind chips exist inside an opened batch.

#### WP11 — Simpler progress card (#864, first picture)
- **What it does:** the slim card of D32, sharing the animated icon with the top bar's indicator; the Libraries page scan card uses the same card with the View activity button (D36).
- **Files likely touched:** `LibraryActivityProgressCard.razor(.css)`, `Components/Navigation/SystemActivityIndicator.razor` (shared icon), `LibraryActivityText.cs`, the Libraries page scan card, strings (to be confirmed).
- **Depends on:** WP7, WP8 · **Size:** Standard
- **Done when:** tests show the one line "N of M files processed · X min elapsed", the bar with the same percent as the top bar, the pill only above zero, and none of the old counts; the Libraries page shows the same card with View activity; screenshots at 1024, 1280, 1440 and phone match Shy's picture.

#### WP12 — Slimmer batch rows (#864, second picture)
- **What it does:** the rows of D33 for running and finished batches, all four types.
- **Files likely touched:** `LibraryActivityRunEntry.razor(.css)`, `LibraryActivityHistory.razor(.css)`, `LibraryActivityText.cs`, strings (to be confirmed).
- **Depends on:** WP10 · **Size:** Standard
- **Done when:** tests show a running row ("Adding audiobooks", "2 added so far", two covers, Active) and a finished row ("250 items added", the counts line, three covers and "+247", Completed); screenshots at the four widths match Shy's picture.

#### WP13 — Working now fits the width (#862)
- **What it does:** half-size tiles; as many as fit in one row; no scroll bar; at most 10, each held at least 3 seconds, rotating; the total in the heading.
- **Files likely touched:** `LibraryActivityWorkingNow.razor(.css)`, `LibraryActivityTile.razor(.css)`, the Engine's list of current titles if the cap and hold live there (to be confirmed).
- **Depends on:** WP8 · **Size:** Standard
- **Done when:** with 30 titles in progress, a browser check at 1024, 1280, 1440 and phone width finds no horizontal scroll bar and no cut-off tile, and the tiles change over time; the heading reads "Working now · 30 titles".

#### WP14 — Docs and close-out
- **What it does:** update the public Activity page and presentation rules (with the why), the engineering index, the batch list rows (each with its issue number) and the closure note on each issue.
- **Depends on:** WP7 to WP13 · **Size:** Mechanical
- **Done when:** the docs build passes and each of the six issues has its closure note.

**Waves:** 1) WP7 and WP9. 2) WP8 and WP10. 3) WP11 and WP12. 4) WP13, then WP14.

### Local checks (Version 3, added to the batch list when built)

- **L10 (#866).** During an import of the test media, compare the card's pill, the Needs Review tab and the banner. They show the same number while it runs and after it settles. Failure: any two differ.
- **L11 (#880).** Let the import finish. The card says "Everything is up to date" and Working now disappears at the same moment. If preview pictures are still being made, a Background line says so. Failure: tiles remain under an "up to date" card.
- **L12 (#868).** A fresh first import of the real test media gives one batch for the whole import, including after stopping and starting the Engine part way. Failure: two batches for one import.
- **L13 (#863).** Pick Watch, Listen and Read in turn. The card, Working now and each batch show only that area; there is no filter inside an opened batch.
- **L14 (#864).** Compare the card and the batch rows with your two pictures at 1024, 1280, 1440 and phone width. The card's icon animates and matches the top bar's. The Libraries page shows the same card with View activity, which opens Activity.
- **L15 (#862).** During a busy import, Working now has no sideways scroll bar at the four widths, shows smaller covers, and rotates.
- **L16 (phone).** On a phone, do everything in L10 to L15: pick chips, read the card and pill, open a batch, search, change type, date range and sort. Failure: anything the desktop can do here that the phone cannot.

Why the cloud cannot finish these: a long import of large real videos (the timing behind #868 and #880) and the side-by-side look against Shy's pictures need the PC and the real test media.

### Risks and open questions

- The causes for #866, #880 and #868 come from reading the code. Each packet reproduces the problem in a test first; if a cause turns out different, the build says so in its thread before changing course.
- Keeping a batch open while the Engine is busy means a very long import is one long Active batch. That is the intent, but a stuck Engine could leave it Active; the existing rule that files nobody picks up cannot hold the page "working" (D27) still applies.
- Narrowing batches by area adds a little work when History loads (Performance); the counts are stored per batch, not worked out on every view.

**Open questions:** none. Shy answered all three on 11 Nov (D28, D32, D36).

### Plain English summary (Version 3)

The Activity page worked on the local check, but it showed too much and some of its numbers disagreed. The "need review" number now comes from one place, so the card, the Needs Review tab and the banner always agree. "Working now" only shows titles that are really being imported, and it empties when the card says the work is done. One import shows as one batch. The chips at the top narrow the whole page, and the extra filter inside each batch goes away. The progress card and the batch rows become the slimmer versions from Shy's two pictures, and the Libraries page uses the same card. The "Working now" row uses smaller covers and shows only as many as fit. The phone gets everything the desktop has.
