# Spec: Library activity

> **Status:** Built, awaiting your Local checks · **Version:** 1 · **Last updated:** 2026-11-06 (locked by Shy) · **Related docs:** [Libraries and folder picker](libraries-and-folder-picker.md) · [Libraries redesign](libraries-redesign.md) · [Library pages](library-pages.md) · [Library access](library-access.md) · [Look pass](look-pass.md) · Mockups: `../design/mockups/library-activity.html`

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
- **Open questions:** none. This spec depends on the Libraries scan card that the Libraries fix build and the Libraries spec revision are delivering; if that build changes the card, only WP4 is affected.

## Plain English summary

Activity becomes one page for administrators. The top shows what Library is doing right now, with one progress bar and tiles for the titles being worked on. Below it is a timeline of past imports with month and year headings you can collapse. Each import opens in place to show what was added, stacked so a hundred files read as a few dozen tiles. Search shows each title once with its own dates. To make this work, the Engine starts recording one entry per import, which also lets the "New" tag appear only after the first load. Photos and documents stay out, and the top-bar spinner opens this page.
