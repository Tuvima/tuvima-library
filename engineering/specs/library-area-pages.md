# Spec: Library area pages (Read, Listen, Watch), people tiles and details-page fixes

> **Status:** Locked · **Version:** 1 · **Last updated:** 2026-10-27 · **Related docs:** [Product overview](../product/overview.md) · [Presentation rules](../../docs/product/presentation-rules.md) · [Design system](../design/design-system.md) · bug log entries F16, F22, F29, F32, F58, F62, F67, F69 (also touches F20, F46, F70)

> Locked by Shy on 2026-10-27. Findings below come from reading the code at main (commit 44281287); nothing was run, so every "root cause" marked *likely* needs the first check in WP1 or WP2 before the fix is built.

## 1. Goal

Read, Listen and Watch look and behave like one family: the same top bar (tabs, search, one filter row), the same "Continue" row that still looks tidy with 30 items in progress, and the same people tile for authors, artists, directors and actors. The person page can be sorted and filtered, the movie details page stops wasting vertical space, and the book details page stops snapping back to the top.

Product walkthrough (what you will see):
1. Open **Read**: tabs (Discover, Books, Comics), a full-width search, one filter row (Browse as, Filters, quick toggles, Sort, Cards/List), then **Continue Reading** as a scrolling row of compact cards (small cover, title, author, progress bar with percent, Resume), then "Books, 21 items".
2. Open **Listen** and **Watch**: identical layout, only the words and tabs differ (Albums, Artists, Audiobooks; Movies, TV, Videos).
3. **Listen, Artists** (and Authors, Directors, Actors wherever they list people) show one people tile: 2x2 grid of covers, a round photo overlapping the bottom-left corner, the name in bold capitals, one small facts line.
4. Open a **person**: "Works in your library" has a Sort control and a quick filter next to the existing Read / Watch / Listen filter.
5. Open a **movie**: the series and related rows sit tight under the hero.
6. Open a **book** and scroll: the page stays where you scrolled.

## 2. Scope

**In scope**
- One shared layout for every library area page, built on the existing browse shell (F69, F58, sets the pattern for F20, F46).
- A compact Continue row: up to about 12 cards, scrolls sideways, progress and Resume, "View all".
- One shared people/creator tile (F62, F67), used for artists, authors, narrators, directors, actors.
- People counts on music people (F16), person page sort and quick filter (F22), movie overview spacing (F29), book scroll jump (F32).

**Out of scope**
- The icon-and-button rule for the whole app (F70) and the Settings look (F26, F35, F46); this spec only follows the F69 design for the Cards/List switch.
- Phone and native-app layouts beyond keeping the current phone behaviour (phones keep their own Continue row and filter sheet).
- New data on the Engine side, except the two small items in section 5.

## 3. Decisions made

- **D1.** (Shy, F69) One shared layout for all library areas, Read first, then Listen and Watch. The F69 screenshot is the target.
- **D2.** (Shy, F67) The people tile is one shared component, not an artist-only tile.
- **D3.** (Cloud-first rule) Everything is built in the cloud; look-and-feel checks go to the Local checks list.
- **D4.** (Shy, 2026-10-27, Q1) Music people show albums and songs, e.g. "12 albums · 148 songs".
- **D5.** (Shy, Q2) Person page works: newest first by default, Sort menu with Newest, Oldest, Title A to Z, Recently added.
- **D6.** (Shy, Q3) Continue row shows up to 12 cards, then "View all".
- **D7.** (Shy, Q4) List view is a compact table with the same columns on every area.

## 4. UI behaviour

**What exists today (so the work is small):** the browse shell already has the tab row, the full-width search, "Browse as", the Filters menus (Genres, creators, years), the quick toggles ("In progress", "Unmatched", "Finished"), Sort, and a Cards switch. What is missing against F69 is mostly the Continue row, a visible List option, and Listen/Watch using exactly the same bar.

**Continue row (F58).** Today the row is a single bordered bar showing only 3 items, with no progress, no Resume and no "View all", so it cannot show more than 3 and looks like a banner rather than a shelf. New behaviour: heading "Continue Reading / Listening / Watching" with a count, a horizontally scrolling row of up to 12 compact cards (about 5 visible on desktop), each with small cover, title, author or show, a thin progress bar with percent, and a Resume button. "View all" opens the same list with the "In progress" toggle on. Empty: the row is hidden (no empty header). Loading: three skeleton cards. Error: the row is hidden and the page still works. Phone: keeps its current one-row layout; tablet shows about 3 cards; TV web keeps focus order left to right.

**People tile (F62, F67).** Cover grid on top, round photo overlapping the bottom-left corner, name in bold capitals, one facts line, sized to its content in a wrapping grid under "Artists, 9 items". The facts line is the only part that changes by role:
- Artist: active years and "N albums · M songs" (see Q1).
- Author or narrator: active years and "N books" or "N audiobooks".
- Director or actor: active years and "N movies · N shows".
Missing photo: initials in the circle. Fewer than 4 covers: 1 cover fills the grid, 2 or 3 leave the empty cells blank.

**Person page (F22).** Next to the Read / Watch / Listen filter and the role filter: a Sort menu (Title A to Z, Newest first, Oldest first, Recently added; Q2) and a text quick filter ("Find in works"). Default order: newest first within each lane (Q2 recommends this over A to Z).

**Movie details (F29).** A one-item series or a "Also in this world" row with few items must not reserve a full carousel. Single item or 4 or fewer: show a plain row with no arrows; collapsed row is one slim bar (as designed in #406).

**Book details (F32).** After opening a book, scrolling down never jumps to the top.

## 5. Data / model changes

- Music people need two numbers (albums and songs). The Engine already returns `work_count` and `distinct_title_count` for content groups; WP4 confirms whether `distinct_title_count` is the album count for music before adding anything new. No data-store change.
- Continue row asks the Engine for 12 instead of 3 (`GetDisplayContinueAsync` limit); no wire shape change.

## 6. Root causes found

| Bug | Likely root cause | Confidence |
|---|---|---|
| F58 | `CompactContinueStrip` hard-codes `Items.Take(3)`, and the shell loads only 3 items (`limit: 3`). It is a single bar with no progress, Resume or "View all". The older large-tile shelf (`ContinueAcrossMediaSection`) is still used on Home and the My Library page. Also a suspected markup slip in the strip: an `else` after the progress component sits outside the `if`, so the fallback icon may print as text. | High for the cap and missing parts; the `else` needs a render check |
| F69 | Not a defect: most of the F69 layout is already in `MediaBrowseShell` (2,400 lines). The gap is the Continue row, List display, and parity between Read, Listen and Watch. Listen has its own 1,200-line page with its own artist and album filters (`ListenPage.razor`), so it drifts from the shared bar. | High |
| F62 / F67 | A shared composer exists (`MediaPersonGroupTileComposer`) but produces a landscape "banner" tile with cover-only text and a single count line; the round photo, bold name and facts line are not in it. Listen Artists passes through it, so changing the shared composer fixes every role at once. | High |
| F16 | The shared people tile takes a single `ownedCount` and labels it by media type (music shows "tracks"), so albums and songs cannot both appear. The People list in Collections shows lane totals (Read / Watch / Listen) instead. The exact "Album" pill needs a screenshot to pin down. | Medium |
| F22 | The person "Works in Your Library" module sorts every item by title (case-insensitive A to Z) with no control; lane and role filters exist, sort and text filter do not. Reported "not sorted" is likely articles and subtitles ("The ...") scattering the order. | Medium |
| F29 | Each details row now uses the shared collapsible heading, which has a 2.75rem minimum tap height plus gaps (#406, 2026-10-22), on top of the series panel's own carousel with large arrows even for one item. Two stacked headings plus a full-height carousel for one movie explains the extra height. | Medium; confirm with a rendered measurement in WP3 |
| F32 | `detailOrigin.fresh()` in `wwwroot/app.js` watches the whole page for changes for one second after a details page opens and resets the scroll to the top on every change. Book pages load extra sections late (progress, series, credits), so a person who starts scrolling within that second is pulled back to the top. Other media types likely do it too but load faster. | Medium; reproduce with a slowed load in WP2 |

## 7. Work packets

### WP1 — Shared Continue row (F58)
- **What it does:** Replace the 3-item bar with the scrolling compact-card row (progress, Resume, "View all"), loading 12 items; fix the stray `else`.
- **Files likely touched:** `Components/Browse/CompactContinueStrip.razor(.css)`, `Components/Browse/MediaBrowseShell.razor`, `Services/MediaTiles/MediaTileComposerService.cs`, bUnit tests in `tests/MediaEngine.Web.Tests`.
- **Depends on:** none
- **Size:** Standard
- **Done when:** a test shows 12 items render, a card with progress shows percent and Resume, empty list renders nothing, and a visual check at desktop width shows one scrolling row.

### WP2 — Book details scroll jump (F32)
- **What it does:** Reproduce (slow page load, scroll within the first second), then stop the reset after the first paint or as soon as the person scrolls. Check movie, TV, album and audiobook pages the same way.
- **Files likely touched:** `wwwroot/app.js` (`detailOrigin`), a JavaScript test in `tests/MediaEngine.Web.Tests/JavaScript`.
- **Depends on:** none
- **Size:** Standard
- **Done when:** a JavaScript test shows late page changes after the person has scrolled do not reset the scroll, and the Local check passes on a real book.

### WP3 — Movie overview spacing (F29)
- **What it does:** Tighten collapsible rows and skip the carousel chrome for 4 or fewer items; measure before and after.
- **Files likely touched:** `Components/Shared/AppCollapsibleRow.razor.css`, `Components/Details/SequencePlacementPanel.razor(.css)`, `Components/Details/DetailPrimaryModule.razor.css`.
- **Depends on:** none
- **Size:** Standard
- **Done when:** before and after screenshots of a movie in a series show the section clearly shorter, and the collapse arrow still works.

### WP4 — Shared people tile (F62, F67, F16)
- **What it does:** Rebuild the people tile (cover grid, round photo, bold name, facts line) in the shared composer; add the album and song counts for music (Q1); wire artists, authors, narrators, directors and actors to it.
- **Files likely touched:** `Services/MediaTiles/MediaPersonGroupTileComposer.cs`, `Components/MediaTiles/` (tile and CSS), `Components/Pages/ListenPage.razor(.cs)`, `Components/Collections/PeopleCatalogList.razor`.
- **Depends on:** none
- **Size:** Complex
- **Done when:** one test per role shows the right facts line, and Listen Artists, Read Authors and a Watch cast list render the same tile.

### WP5 — Person page sort and quick filter (F22)
- **What it does:** Add Sort and "Find in works" to the person's works module, with sensible default order.
- **Files likely touched:** `Components/Details/DetailPrimaryModule.razor`, `Services/Details/Internals/DetailCompositionOrchestrator.ViewModelBuilder.cs`.
- **Depends on:** none
- **Size:** Standard
- **Done when:** tests show each sort order and the text filter narrowing the list, and the choice survives a refresh in the same browser.

### WP6 — One layout for Read, Listen, Watch (F69)
- **What it does:** Make Listen and Watch use exactly the shared bar and Continue row; add the Cards/List switch to all three; move Listen's own filters into the shared Filters menu.
- **Files likely touched:** `Components/Pages/ListenPage.razor(.cs)`, `Components/Pages/WatchPage.razor`, `Components/Pages/ReadPage.razor`, `Components/Browse/MediaBrowseShell.razor`.
- **Depends on:** WP1 and WP4
- **Size:** Complex
- **Done when:** screenshots of Read, Listen and Watch show the same bar in the same positions, and List view lists the same items as Cards.

Order: WP1, WP2, WP3 and WP5 can start now (two at a time); WP6 last.

## 8. Local checks

- **L1.** On a real book from `Z:\Development\testing\media\real_media`, open details, scroll straight away: no jump. Repeat on a movie, a show and an album.
- **L2.** Read, Listen, Watch side by side with 15 or more items in progress: Continue row scrolls, Resume starts the right item.
- **L3.** Listen, Artists with real artists: tile photos, covers and counts look right; one with no photo.
- **L4.** A movie in a series: the Overview area is clearly shorter than before.
- **L5.** A person with many works: sort and quick filter feel right.

## 9. Risks and open questions

- **Risks:** Listen's page is large and has its own behaviour (playlists, album detail); WP6 could break Listen filters, so it goes last with guardrail tests. The scroll fix must keep Back returning to the right place (existing restore logic).
- **Open questions:** none. Q1 to Q4 were answered A (the recommended options) and are recorded as D4 to D7.

## Plain English summary

Read, Listen and Watch already share most of one page frame; what is missing is a proper "Continue" shelf (today it is a thin bar that shows only three items), the nicer people tile (artists, authors, actors all use one), and Listen drifting from the others. This spec makes the three pages the same, makes the Continue shelf scroll and hold many items, and fixes three smaller annoyances: the person page cannot be sorted, the movie page wastes space, and the book page jumps to the top. All four open questions are answered, so it is ready for you to lock.
