# Spec: Home page redesign (hero, pick up where you left off, Recently Added, tiles, details opening)

> **Status:** Built (archived 2026-10-28; awaiting Local checks L1 to L9) · **Version:** 1 · **Last updated:** 2026-10-27 (locked by Shy 2026-10-27) · **Related docs:** [Product overview](../../product/overview.md) · [Presentation rules](../../../docs/product/presentation-rules.md) · [Design system](../../design/design-system.md) · bug log entries F20, F31, F60, F68, F71, F72 (related: F21, F38, F58, F69)

> Written 2026-10-27 from Shy's bug log and Shy's layout example for F71. Root causes below were read from the code on main (44281287); nothing was run, so each cause says how sure we are and which packet confirms it. Nothing here is Locked.

## 1. Goal

Home should open on a hero that looks exactly like the top of a details page (title logo, cover, buttons), and that features what you are in the middle of first, then what is new. Below it, a compact "Pick up where you left off" row and a "Recently Added" row with smaller tiles show more of your library at a glance. Filters only appear when they have something behind them. Hovering a tile feels the same everywhere, and opening a details page appears at once instead of flashing empty boxes.

## 2. Scope

**In scope**
- Home hero: same look as a details page hero (F68), correct cover size for books (F31), and written selection rules.
- Home below the hero: new "Pick up where you left off" row, "Recently Added" with toggle pills, smaller tiles, then the Watch / Read / Listen shelves (F71, F20).
- One hover behaviour for movie and TV tiles with landscape banners on Home and Watch (F72).
- Details page opening without empty boxes (F60), including a profiling step to find out why it is slow.

**Out of scope**
- A hero picker that actually uses AI. This spec only makes room for it (D6).
- Community plugins (parked); only built-in plugins use hero slides.
- The Read, Listen and Watch area pages and their own continue rows (F58, F69). They get the same compact card later, from this spec's shared component, but their layout is a separate spec.
- Wrong-language title logos (F38) and TV artwork matching (F21). The hero uses whatever logo the details page uses, so fixing F38 fixes both.
- Changing what "continue" means for books, music and TV (progress rules stay as they are).

## 3. Decisions made

Shy's rules and example are recorded as requirements (R1 to R6). Shy answered the four open questions on 2026-10-27:

- **D1 (was Q1).** The hero follows R1 to R3 strictly, with no per-type cap. Why: simplest to explain and matches what Shy wrote.
- **D2 (was Q2).** A half-watched item shows in both the hero and the pick-up row. Why: the row stays a complete list.
- **D3 (was Q3).** Keep an "All" pill that resets the other pills.
- **D4 (was Q4).** Recently Added tiles are about 25% smaller than today.
- **D5 (Shy, 2026-10-27).** The hero gets per-profile settings (slide count, hero on or off, which types may appear, "continued only" or "continued plus new"). They only narrow what the rules choose from; they never change the rules' order.
- **D6 (Shy, 2026-10-27).** The hero is built as a swappable picker. A first step decides which items are allowed (finished matching, real artwork, permitted for that profile, within the profile's settings). A second step orders them. The default rules (R1 to R3) are one orderer; an AI orderer can be added later once AI features are on. AI can only reorder allowed items, and if AI is off or fails the default rules apply.
- **D7 (Shy, 2026-10-27).** Plugins can offer hero slides and show extra details on the hero, within the Library's fixed look. A plugin supplies data only (title, picture, a short line of text, a link or action); the Library draws it in its own hero style. Plugin slides go through the same allowed-items step, are marked with the plugin's name, and each plugin's slides are switched on or off in the hero settings. Only the built-in (Official) plugins use this for now; community plugins stay parked. To avoid a second extension path, this uses the display surface with slots in the plugin architecture v2 draft (not yet in the repo; locks separately) (C21: Home hero line or badge, hero slide suggestions). Plugins supply data only, can suggest but never override the hero order, and the hero never waits on a plugin. It also follows v2 on: hero content is a plugin output recorded like any other (what, source plugin, confidence, when: C1), carries a content rating so child and restricted profiles never see unsuitable plugin text (C6), can be cleared with "Remove what this plugin added" (C3), and any button on a plugin slide is a plugin action (C4).

- **R1 (Shy, F68).** Continued items come first in the hero.
- **R2 (Shy, F68).** An episode appears in the hero only if you have started watching that show. Otherwise the hero shows the series itself, so you can start it as new.
- **R3 (Shy, F68).** After continued items, newly added items.
- **R4 (Shy, F68).** The hero uses the same view as a details page hero, for an episode or a series.
- **R5 (Shy, F71).** Below the hero: a "Pick up where you left off" row of compact cards (small cover; a Read / Listen / Watch label with icon; title; subtitle; progress bar with "62% complete" or time remaining; round play button) covering books, music, movies and shows. Then "Recently Added" with All / Watch / Read / Listen / View pills and smaller tiles. Then a "Watch" shelf.
- **R6 (Shy, F20).** Hide any pill with nothing behind it. Pills are toggles that can be turned on and off, several at once.

## 4. UI behaviour

### 4.1 Hero (F68, F31)

| Situation | What the person sees |
|---|---|
| Show you have started | The hero looks like the episode details hero: the show's title logo (not plain text), the episode still as backdrop, "S1 E3 · Episode title", progress bar, **Resume Episode** button. |
| Show you have never started | The hero looks like the series details hero: logo, series backdrop, **Start watching** (first episode). No episode wording. |
| Movie you started | Movie details hero with the logo, progress bar and **Resume**. |
| Book / audiobook / album | Same hero as its details page: the cover large on the right at the same size as on the details page. Audiobook and album covers stay square. |
| No logo available | Rendered title in the details page's own style (never a small plain text line). |
| Item not finished identifying | Never shown in the hero (unchanged). |
| Nothing to feature (empty or still being prepared) | No hero; the page starts at the first row (unchanged). |

**Hero order (R1 to R3).** Up to five slides. First every item you are in the middle of, most recently touched first, across all types. Then newly added items, newest first. An unstarted show appears as a series (R2). Each item appears once; a started show is one slide, not one per episode. Slide order is fixed until the next refresh, so slides do not reshuffle while you look at them.

### 4.2 Pick up where you left off (R5)

One mixed row, most recently touched first, desktop and tablet scroll sideways; phone keeps its single-row behaviour. Card contents as in R5. Tapping the card or the round button resumes; tapping the title opens details. If nothing has been started the whole section is hidden (no empty heading). The old "Continue Across Media" three-group row (Watching / Reading / Listening) is replaced.

### 4.3 Recently Added (R5, R6)

| Situation | What the person sees |
|---|---|
| Normal | Heading, pills, smaller tiles so about a third more fit per screen than today, "View all". |
| Pills | Only types that have recent items get a pill. "All" is shown when more than one type has items. Pills are toggles: tap Watch to show Watch, tap Read as well to show both, tap again to remove. Nothing selected means everything. |
| One type only | No pills at all (nothing to filter). |
| Empty | Section hidden. |
| Loading | Row keeps its height with soft placeholders; pills do not jump in later. |
| Error | One line: "Recent additions are unavailable. Please try again." |

"View all" opens the full Recently Added page with the same toggles preselected.

### 4.4 Tile hover (F72)

One behaviour for every movie and TV tile on Home and Watch, whether the banner is landscape or the tile is a poster: after a short pause the tile lifts slightly and the preview fades in over the tile without moving its neighbours. Same speed in and out everywhere, no delay when moving from one tile directly to the next, nothing at all on touch devices and with "reduce motion" on.

### 4.5 Opening a details page (F60)

The page appears at once. From Home, the hero and tiles already know the title, cover, logo and backdrop, so the details page shows those immediately while the rest (cast, seasons, files) fills in below with soft placeholders under their own headings, not five empty boxes. The hero never flashes empty and then redraws.

## 5. Data / model changes

- Recently Added gains a per-type "has items" summary (watch / read / listen / view) so pills can hide without loading every list. Wire change: Contracts tests and fixtures updated.
- The Home page payload keeps one list of hero slides but each slide is marked "started" or "new", so the Dashboard can show the right wording and buttons. Wire change as above.
- The details route accepts a "preview" (title, cover, logo, backdrop) handed over from the tile the person clicked.
- No stored data changes. No reset needed.

## 6. Work packets

Built in the cloud, validated locally in one batch (see section 7). Order: WP1 first (it settles uncertain causes), then WP2 to WP5 can run two at a time on different files.

### WP1 — Measure before fixing
- **What it does:** Writes short measurements, not fixes: (a) the real size of a book cover in the Home hero versus on its details page; (b) how long the Engine takes to build a details page and which part dominates; (c) frame timing of the landscape-tile hover. Records the findings in the packet's pull request and corrects WP3 to WP5 if a cause below is wrong.
- **Files likely touched:** a throwaway Playwright script in `.tmp/`; no app code. Uses a small generated library only.
- **Depends on:** none
- **Size:** Standard
- **Done when:** a three-line finding for each of (a), (b) and (c) is posted, each saying "confirms" or "contradicts" the cause in section 8.

### WP2 — Hero looks like the details hero, with Shy's order
- **What it does:** Shows the title logo for episode heroes (today it is switched off whenever the hero is an episode), keeps the details-style episode wording, and rebuilds the hero selection to follow R1 to R3 (continued first by last touched, then newest new items, unstarted shows as series). Adds the started/new mark on each slide.
- **Files likely touched:** `src/MediaEngine.Api/Services/Display/DisplayComposerService.Spotlights.cs`, `src/MediaEngine.Web/Components/Cinematic/CinematicHeroCarousel.razor`, `src/MediaEngine.Contracts/Display/DisplayDtos.cs` (plus fixtures), `tests/MediaEngine.Api.Tests` (hero selection), `tests/MediaEngine.Web.Tests`.
- **Depends on:** WP1
- **Size:** Complex
- **Done when:** tests named like `HomeSpotlights_StartedShowComesBeforeNewItems`, `HomeSpotlights_UnstartedShowIsASeriesNotAnEpisode`, `HomeSpotlights_OneSlidePerShow` pass; the episode hero renders the logo in a component test; the wire fixtures are refreshed.

### WP3 — Book cover size in the hero
- **What it does:** Makes the Home hero use the same cover size rules as the details hero. Likely cause: those rules only apply inside the details page container, so Home gets none of them.
- **Files likely touched:** `src/MediaEngine.Web/Components/Details/HeroBackdrop.razor.css`, `src/MediaEngine.Web/Components/Cinematic/CinematicHeroSurface.razor.css`, `CinematicHeroCarousel.razor.css`.
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** a screenshot comparison (small generated library) shows the cover the same height within 5% on Home and on the details page, for a book, an audiobook and an album; the dashboard CSS size check passes.

### WP4 — Pick up row, Recently Added pills and smaller tiles
- **What it does:** Builds the compact "continue" card as a shared component and the single mixed row (R5); adds the per-type summary and the toggle pills (R6); shrinks Recently Added tiles; reorders Home sections.
- **Files likely touched:** `src/MediaEngine.Web/Components/MediaTiles/ContinueAcrossMediaSection.razor(.css)`, `RecentMediaFeed.razor`, `RecentMediaFilters.razor`, `src/MediaEngine.Web/Components/Pages/LibraryBrowsePage.razor`, `src/MediaEngine.Api/Services/Display/DisplayRecentComposerService*.cs`, `src/MediaEngine.Contracts/Display/DisplayRecentDtos.cs` (plus fixtures), bUnit tests.
- **Depends on:** WP1
- **Size:** Complex
- **Done when:** tests show a pill is absent when its type has no items, several pills can be on at once, and the section is hidden when nothing is started; layout checked at phone, tablet and desktop widths on a generated library.

### WP5 — One hover behaviour
- **What it does:** Gives landscape and poster movie/TV tiles one hover with one set of timings, and removes the second competing set. Likely cause: Home tiles expand by animating their width (every neighbour re-lays out each frame) while other tiles use a lift, and the pause, fade and hide timings are separate hard-coded numbers.
- **Files likely touched:** `src/MediaEngine.Web/Components/MediaTiles/MediaTile.razor.css`, `src/MediaEngine.Web/wwwroot/app.js` (the tile hover functions), `src/MediaEngine.Web/Services/MediaTiles/MediaTileHoverPolicy.cs`.
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** the frame timing from WP1 shows no dropped frames on a 10-tile row, and a test confirms Home and Watch tiles of the same kind resolve to the same hover mode.

### WP6 — Details page opens without empty boxes
- **What it does:** Hands the clicked item's title, cover, logo and backdrop to the details page so the hero draws immediately, and replaces the all-or-nothing skeleton with per-section placeholders. Moves whatever WP1 shows to be the slowest part of building the page out of the first response.
- **Files likely touched:** `src/MediaEngine.Web/Components/Details/DetailRouteHost.razor`, `DetailHeroSkeleton.razor(.css)`, `src/MediaEngine.Web/Components/Pages/UnifiedDetailPage.razor`, tile click handlers, and the Engine's details composer if WP1 points there.
- **Depends on:** WP1, WP2 (shares the hero)
- **Size:** Complex
- **Done when:** opening a details page from Home shows the hero within one frame of navigation in a recorded run; no standalone grey boxes appear before the hero; Engine time for the first response drops to the figure WP1 sets as the target.

### WP7 — Hero settings and swappable picker
- **What it does:** Splits hero selection into "allowed items" and "ordering" (D6), keeps the default rules as the first orderer, and adds the per-profile hero settings (D5) on the profile's preferences page, applied by the Engine so every device agrees.
- **Files likely touched:** `src/MediaEngine.Api/Services/Display/DisplayComposerService.Spotlights.cs` (split), a new picker interface beside it, the profile preferences store and page (to be confirmed), `src/MediaEngine.Contracts`, tests.
- **Depends on:** WP2
- **Size:** Complex
- **Done when:** tests show that a profile limited to Watch never gets a book slide, that turning the hero off hides it, and that a test orderer can be swapped in without touching the allowed-items step.

### WP8 — Plugin hero slides and hero details
- **What it does:** Adds "hero slide" and "hero detail line" as two things a built-in plugin can declare in its description, using the same fixed-page-types idea the plugin platform already uses for plugin pages. The Library validates the data (no scripts, pictures only from addresses the plugin declared), draws it in the standard hero, labels it with the plugin's name, and lets each profile switch a plugin's slides on or off. Built on plugin architecture v2 (C21 display slots, C1 output record, C3 undo, C4 plugin actions, C6 content rating), so it waits for those packets and must not add its own contract. If v2 is not yet locked when this is built, this packet stays parked and WP7's picker ships without plugin slides.
- **Files likely touched:** plugin description and loader (to be confirmed; plugin architecture v2 packets for C1, C4, C6), the picker from WP7, hero component, Plugins page, tests.
- **Depends on:** WP7; the plugin platform's page-contribution packets (to be confirmed)
- **Size:** Complex
- **Done when:** a test built-in plugin's slide appears in the hero with its name, disappears when switched off or when the profile is not allowed it, and an invalid slide (script, undeclared picture address) is refused with a logged reason.

### WP9 — Docs and close-out
- **What it does:** Updates the presentation rules and public docs for the hero order, the pick-up row, the pills and the hover rule; refreshes batch list.
- **Files likely touched:** `docs/product/presentation-rules.md`, `engineering/validation/batch.md`.
- **Depends on:** WP2 to WP8
- **Size:** Mechanical
- **Done when:** docs build passes and the batch list carries section 7.

## 7. Local checks

- **L1.** On the real library with something half-watched, half-read and half-listened: Home opens on the most recently touched of them, with its logo (not plain text). Failure: plain text title, or a never-started item ahead of a half-finished one.
- **L2.** A show you have never played: its hero shows the series with "Start watching". After you play one episode, the hero shows that episode with Resume. Failure: an episode hero for an untouched show.
- **L3.** A book hero cover looks the same size as on the book's details page. Failure: noticeably smaller.
- **L4.** "Pick up where you left off" row matches your example picture: cover, type label, progress text, round play button. With nothing started, the row is gone.
- **L5.** Recently Added: with no audio in the library the Listen pill is missing; with two pills on you see both types.
- **L6.** Hover a row of movie and TV tiles quickly side to side: smooth, same on Home and Watch. Failure: any stutter or a neighbour jumping.
- **L8.** Hero settings: limit a profile to Watch and Home shows no book or album slide; turn the hero off and Home starts at the first row. Failure: a type you excluded still appears.
- **L9.** A built-in plugin's slide shows its name and disappears when you switch that plugin's slides off.
- **L7.** Click ten items of mixed types from Home: the details hero appears immediately, no empty boxes.

## 8. Risks and open questions

**Risks**
- The hero order change can surprise people who liked the mix of types. Mitigation: rules are written down and tested.
- A new Home payload field and a Recently Added summary change the Engine/Dashboard wire format; the Android and TV clients must keep working (Contracts tests guard this).
- Causes below are from reading code, not from running it. WP1 exists so we do not build on a wrong guess.

**Causes (what the code says, how sure)**
- **F68 plain-text title: confident.** The Home hero only shows the logo when the slide is not an episode, so every started TV show falls back to plain text.
- **F68 wrong order: confident.** The selection takes one representative from each type in a fixed order (TV, movie, book, album, audiobook) before anything else, so a new item of an earlier type can beat something you are halfway through.
- **F31 small cover: likely, not proven.** Cover sizing for the hero is written only for the details page container; Home's hero is not inside it. WP1 measures.
- **F20 pills: confident.** The pill list is a fixed list of five and the Engine reports nothing about which types have items. Single-select by design today.
- **F60 empty boxes: confident about what they are, unsure why slow.** The boxes are the loading skeleton shown until the whole details payload arrives. The payload is composed in one go (cast, seasons, files and all), and the earlier speed work (B29) did not change that. WP1 profiles it.
- **F72 laggy hover: likely, not proven.** Home movie/TV tiles grow by animating width, so the row re-lays out every frame, and the pause (200 ms), fade (200 ms), hide (110 ms) and grow (280 ms) timings are separate numbers. WP1 measures.

**Open questions**
- None. Q1 to Q4 are answered as D1 to D4. Locked by Shy 2026-10-27.

## 9. Plain English summary

Home will open on a big featured item that looks exactly like the top of that item's details page, with its title logo and a Resume button. What you are halfway through comes first, then new arrivals, and a show you have never watched shows as the series so you can start it. Under that is a compact "Pick up where you left off" row, then Recently Added with smaller tiles and filter buttons that you can switch on and off, and that only appear when they have something to show. Hero settings per profile, room for an AI picker and plugin-supplied slides are included. Hovering over a movie or show will behave the same way everywhere, and opening a details page will show the picture and title straight away instead of a flash of empty boxes. A first step measures three things we are only fairly sure about (cover size, why details pages are slow, hover lag) before we fix them. All four questions are answered, so it is ready for you to lock.

## 9. Build notes (2026-10-28)

Built in #508 (hero order, logo, cover size), #510 (pick-up row, pills, tiles), #513 (hover, instant details opening) and #514 (hero settings, swappable picker, plugin slides, docs). Choices made while building unattended:

- **WP1 measure:** done by reading the code; the timings (details build time in the Engine, hover frame rate, cover screenshots) are Local checks L6 and L7.
- A show with every episode watched is not "in the middle of", so it ranks with new items but keeps its episode presentation.
- My Library's Continue shelf uses the same pick-up card; an album appears in the row as "Recently played".
- Watch shelves use the Home hover surface, so both pages hover the same.
- The instant details opening covers tile clicks, not the banner's own link. The Engine-side speed-up of the details build is not built; it waits for the L7 timing.
- Plugin slides carry no pictures (the display slot has text and an icon name only), so there are no declared picture addresses to allow; text with markup, scripts or addresses is refused and logged. A suggestion can only label and lift an item that is already allowed on the hero.
- Hero settings are for the signed-in profile only (no administrator editing of someone else's banner).
