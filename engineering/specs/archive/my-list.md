# Spec: My List

> **Status:** Built · **Version:** 2 · **Last updated:** 2026-11-12 · **Related docs:** [Product overview](../../product/overview.md) · [One tile design](../one-tile-design.md) · [Home, detail and tiles](../home-detail-and-tiles.md) · [Settings and top navigation](../settings-and-top-navigation.md) · [Unified player v2](unified-player-v2.md) · Mockups: [my-list.html](../../design/mockups/my-list.html)

## Plain-English walkthrough

**What My List is.** The place for things you want to get to later: a movie someone recommended, a show to start, a book to read next, an album to hear. One list per profile, across every kind of media. It is not a rating (Like and Dislike do that) and it is not Favorites (the songs you hearted, which Listen shows as a playlist).

**What is wrong today** (found by reading the code on main `ebc883cf`, not from a re-run):

1. **There is no My List page.** The top-bar bookmark is labeled "My List" but opens My Library, where My List is one row of up to 20 covers among Continue, Recent activity and Liked.
2. **The full list lives in two other places that do not match.** "See all" opens a plain grid with a sort box; the older For Me page has its own My List tab with different controls (area buttons and a different sort).
3. **Three different icons mean "My List".** A bookmark in the top bar, a plus (tick once added) on the hero and details page, and a heart in the players and on the For Me tab. The same movie shows a heart in the player and a plus on its page.
4. **Taking things off is slow.** One title at a time through each cover's "..." menu, with no undo and no way to clear what you have already finished.
5. **Home has no My List row**, so the list is out of sight unless you go looking for it.

**What you get.**

1. One personal page, called My List. It replaces My Library: the top-bar bookmark and the phone's third tab both open it. Continue sits at the top, then the full list; Activity and Liked are links beside the heading. (M1, M8)
2. The page is a wall of covers, newest first, with chips to narrow by kind (Movies, TV shows, Books and so on, only the kinds you have saved) and one Sort box. (M1, M2)
3. Removing is quick: "Remove from My List" stays first in each cover's "..." menu, and Select lets you tick several and remove them together. Either way a short line with Undo appears for about ten seconds. (M3, M4)
4. One icon for "add to My List" on every title, in every place. (M7; question 2)
5. A My List row on Home, under Continue, hidden while the list is empty. (M6; question 3)
6. Finished titles stay until you remove them, and the page menu has "Remove finished" to clear them in one go. (M4; question 4)
7. The duplicate My List views go away, so there is one list with one set of controls.

**What stays the same.** How things are saved and stored, the "Add to My List" row in every cover's "..." menu, Favorites for songs, Like and Dislike, the Activity and Liked lists themselves, and the "Is on My List" rule in smart collections.

**Representative journeys.**

- *Save and find.* On a movie's page you press the My List button; it changes to its "added" state. Later you press the bookmark in the top bar: the movie is the first cover.
- *Tonight's movie.* You open My List, press the Movies chip, and pick from the 12 movies you saved rather than the 800 you own.
- *Tidy up.* You open the page "..." menu and choose Remove finished. Seven covers go; a line reads "7 titles removed from My List. Undo".
- *On a phone.* The third tab, now called My List, opens the same page with chips you can swipe and Sort in a bottom sheet.

**Acceptance (what you can check).** A1 the bookmark and the phone tab open the one My List page, and there is no My Library page. A2 chips show only kinds present, each with a count, and narrow the grid. A3 removing one or many shows Undo and Undo puts them back in the same order. A4 the same icon adds to My List on the hero, details page, players and Listen. A5 Home shows the row only when the list has something in it. A6 Remove finished removes only titles this profile finished. A7 the old For Me My List tab, the old grid address and the My Library page are gone. A8 Sort applies inside every chip.

## 1. Goal

Give each profile one clear My List: one page, one icon, quick ways to add and remove, and a row on Home, so "save it for later" works the same for movies, shows, books, comics, audiobooks, albums, collections and playlists.

## 2. Scope

**In scope**
- One personal page, My List, at its own address, replacing My Library. Opened by the top-bar bookmark, the phone and TV dock tab, and every "See all" for My List. It carries the profile header (Switch profile, Settings), Downloads (Tuvima app only), Continue, and links to Activity and Liked.
- Kind chips, Sort, Select with bulk remove, Undo after removal, "Remove finished".
- One add/remove icon and one wording on every surface (hero, details page, both players, Listen album and playlist actions, cover menus).
- A My List row on Home.
- Removing the My Library page, the For Me "My List" tab and the old See-all grid for My List.
- Phone, tablet, desktop and TV layouts of the page.

**Out of scope**
- Favorites (songs). Opening Favorites as a music playlist is issue 895 in the Music player and Listen fixes plan.
- Like, Dislike, and the contents of the Liked and Activity lists; the other For Me tabs (the unplanned Profile, Taste and Activity redesign).
- Hand ordering of the list (drag to reorder). Newest first plus Sort covers the need; it can be added later without changing what is stored.
- Saving new kinds of things: a single episode, a Series, a Universe, a person, photos or documents. Today's kinds stay: movie, TV show, book, comic, audiobook, album, collection, playlist.
- Sharing a list between profiles, and importing a watchlist from another service (Collections and lists owns imports).
- Shared controls, type sizes and menu styling: they come from the shared look plan; this page uses them as they are.

## 3. Decisions made

Shy answered every question on 2026-11-11 ("agreed on all", after proposing the merge of My Library and My List).

- **D1 (Shy, 2026-11-11).** My Library and My List are one page, called My List, at `/my-list`. Shy: "these are very similar concepts". Top to bottom: profile header with the heading My List and the profile name, links to Activity, Liked, Switch profile and Settings; Downloads card (Tuvima app only); Continue (hidden when nothing is in progress); then the list. The Recent activity strip and the Liked shelf of My Library become the Activity and Liked links. Every entry point leads to it. Reason: three views with different controls read as three lists (the same finding as F66 in Settings and top navigation).
- **D2.** The page is the standard cover grid from One tile design: same cover sizes, "..." on every cover, no text under covers. Albums are square at cover height in the mixed "All" view. Reason: uniformity; no new tile.
- **D3.** Default order is newest saved first. Sort offers Date added, Title, Release year. The choice is remembered per profile, as it is today.
- **D4.** Kind chips: All, then only the kinds present, each with its count: Movies, TV shows, Books, Comics, Audiobooks, Albums, Collections, Playlists. One chip at a time. The chip is kept in the address so Back and a shared link behave. With one kind saved, the chip row is hidden.
- **D5.** Removing never asks "are you sure". The cover leaves the grid and a line under the heading reads "Removed from My List" (or "N titles removed from My List") with **Undo** for about ten seconds. Undo restores the same saved date, so the order does not change. Reason: the design system's undo rule; removal is cheap to reverse.
- **D6 (Shy, 2026-11-12 standing rule: most people use only their phones, so the phone matches the desktop in every way it can).** Everything on this page works on a phone: chips, Sort, Select, Remove finished and Undo. On phones Select and Remove finished sit in the page "..." sheet, and while selecting a bar at the bottom shows the count, Remove from My List and Cancel.
- **D7 (Shy).** The top-bar bookmark opens My List; pressing it again goes back to Home as today. The phone and TV dock tab is renamed My List with the bookmark icon and opens the same page. "Back to My Library" links on the profile and activity pages read "Back to My List".
- **D8 (Shy).** One icon for My List on titles: plus, turning into a tick once added, labeled "My List". The heart is used only for a song's Favorites. The top-bar button stays a bookmark, gold while the page is open.
- **D9 (Shy).** Home gets a "My List" row directly under Continue, newest first, up to 20 covers, with See all. Hidden when the list is empty.
- **D10 (Shy).** Finishing a title does not remove it. The page "..." menu has "Remove finished", which removes movies, books, comics and audiobooks this profile finished and shows that are fully watched. Albums, collections and playlists are never "finished".
- **D11 (Shy).** No hand ordering in this version. Sort (D3) applies inside every chip and stays as you change chips.
- **D12.** Titles that were deleted from the library, or that the profile may no longer see (Kids rules, Vault, Documents permission), are left out of the page and the counts, as today. They are not deleted from the stored list, so they return if access returns.
- **D13.** The old addresses (`/my-library`, `/for-me?view=my-list` and `/all/personal/my-list`) are removed, not redirected (standing rule: no URL redirects before release).
- **D15.** Build order with the video player overhaul: the plus/tick in the players (part of WP4) is built after that spec's first packet, which edits the same player identity files, and uses the player's shared sizes (28 px icon in a 48 px button).
- **D14.** Wording: "Add to My List", "Remove from My List", "My List". Listen's album and playlist buttons use the same words and icon.

## 4. UI behaviour

Mock screens M1 to M8 in `engineering/design/mockups/my-list.html`.

| Situation | What the person sees |
|---|---|
| Normal (M1) | Profile picture, heading "My List" and the profile name; links to Activity, Liked, Switch profile and Settings. Continue row when something is in progress. Then the count ("23 titles"), a row of kind chips with counts. On the right: Sort, Select, and a page "..." menu. Below: covers, newest first, each with "...". |
| A chip picked (M2) | Only that kind; the count line reads "12 movies". Albums alone show as squares in their usual size. |
| Cover menu (M3) | "Remove from My List" first, then the cover's usual rows (Restart, Open Universe, Edit, Delete as the cover already allows). |
| After removing (M3) | The cover is gone; under the heading, "Removed from My List. Undo" for about ten seconds. |
| Select (M4) | Select turns covers into tick targets. A bar replaces the chips row: "3 selected", Remove from My List, Cancel. Escape or Cancel leaves Select. |
| Page menu (M4) | "Remove finished" (grayed with "Nothing finished yet" when it would remove nothing). |
| Empty (M5) | The heading, links and Continue stay. Below them: "Your list is empty", "Press + on any title to save it for later.", and a Browse Home button. No chips, Sort or Select. |
| A chip that becomes empty | The page returns to All. |
| Loading | Heading, then skeleton covers in the grid's shape; chips appear with the first page of results. More covers load as you scroll, with Load more under the grid as on other grids. |
| Error | An alert at the top of the page: "My List could not load" with Try again. |
| Add or remove failed | The button returns to its old state and an alert in place says "My List could not be changed. Try again." |
| No active profile | The page asks to choose a profile, as other personal pages do. |
| Home (M6) | "My List" row under Continue; See all opens the page. Not shown when empty. |
| On titles (M7) | Plus with the label "My List"; a tick once added. Same on the hero, details page, video player, music bar for albums, Listen album and playlist headers. Songs keep the heart for Favorites. |

**Device differences**
- **Phone (M8):** three covers per row; chips scroll sideways; Sort opens as a bottom sheet from one button; the page "..." opens as a sheet with Select and Remove finished; Select shows a bottom bar (count, Remove from My List, Cancel). Nothing the desktop page does is missing. Continue is a row swiped sideways. Opened by the third dock tab (My List) and from Home's row.
- **Tablet:** as desktop, with touch-size targets.
- **TV:** the same grid with remote focus; chips and Sort are focusable; no Select.
- **Kids profile:** same page; only titles the profile may see.

## 5. Data / model changes

None to what is stored. The Engine gains: counts per kind for the chips, a "finished" answer per saved title, and one action that removes several saved items at once (used by Select, Remove finished and Undo). Wire names for these go through Contracts with fixtures.

## 6. Work packets

### WP1 — Engine: My List read and bulk actions
- **What it does:** the My List read returns kind counts, accepts a kind filter, and marks finished titles; one action removes many saved items and one restores them with their original saved dates.
- **Files likely touched:** `src/MediaEngine.Api/Services/Display/DisplayComposerService.Grids.cs`, `DisplayGridCatalog.cs`, `DisplayGridPersonalReader.cs`, `src/MediaEngine.Api/Endpoints/ProfileStateEndpoints.cs`, `src/MediaEngine.Storage/ProfileStateRepository.cs`, `src/MediaEngine.Contracts/Display/DisplayGridDtos.cs`, `src/MediaEngine.Contracts/ProfileState/`, Contracts fixtures, `tests/MediaEngine.Api.Tests`, `tests/MediaEngine.Storage.Tests`
- **Depends on:** none
- **Size:** Standard
- **Done when:** tests show counts per kind, a kind filter, finished flags per profile, remove-many, and restore keeping saved dates; another profile's list is never touched.

### WP2 — Home row
- **What it does:** adds the My List row under Continue on Home (D9), hidden when empty, See all to `/my-list`.
- **Files likely touched:** `src/MediaEngine.Api/Services/Display/` (Home composition), `DisplayGridCatalog.cs`, Home page parts in `src/MediaEngine.Web`, tests
- **Depends on:** none
- **Size:** Standard
- **Done when:** a test profile with saved titles gets the row second on Home; an empty list gives no row.

### WP3 — My List page
- **What it does:** the page at `/my-list` per M1 to M5 and M8: heading, chips, Sort, Select, page menu, Undo line, empty, loading and error states, phone sheets. The profile header, Downloads card and Continue row are carried over from My Library (D1). Built on the existing See-all grid parts and shared `App*` controls.
- **Files likely touched:** new `src/MediaEngine.Web/Components/Pages/MyListPage.razor(.cs/.css)`, `MyLibrary.razor` (parts moved), `ShelfGridPage` parts it reuses, `SavedItemService.cs`, localization strings, `tests/MediaEngine.Web.Tests`
- **Depends on:** WP1
- **Size:** Complex
- **Done when:** component tests cover each row of the section 4 table; the page matches M1 to M5 at 1024, 1280 and 1440 and M8 at phone width.

### WP4 — One icon and wording
- **What it does:** plus/tick and the D14 wording on the hero, details page, video player, music bar (albums), Listen album and playlist headers and the For Me tab icon; heart only for songs.
- **Files likely touched:** `HeroActionRow.razor`, `PlaybackIdentityActions.razor`, `PlaybackUtilityGlyph.razor`, `ListenPage.razor`, `ListenPage.Albums.cs`, `CinematicHeroCarousel.razor`, `PhoneHeroCard.razor`, tests (`PlaybackIdentityTests`, `LookV3IconButtonTests`)
- **Depends on:** none for the hero, details page and Listen; the player part waits for the video player overhaul's first packet (D15)
- **Size:** Standard
- **Done when:** a guardrail test finds no heart used for a non-song save and no My List label other than the three in D14.

### WP5 — Entry points and clean-up
- **What it does:** top-bar bookmark and the dock tab open `/my-list` (gold while open; tab renamed My List); "Back to My Library" links renamed; For Me "See all" points to `/my-list`; the My Library page, the For Me My List tab and `/all/personal/my-list` are removed.
- **Files likely touched:** `MainLayout.razor`, `ShellNavigation.cs`, `ShellDock.cs`, `MyLibrary.razor`, `ForMe.razor`, `DisplayGridCatalog.cs`, tests (`UiShellRenderTests`, `ShelfGridPageTests`, `MyLibrarySectionTests`)
- **Depends on:** WP3
- **Size:** Standard
- **Done when:** tests show the bookmark and dock targets and that the old addresses no longer resolve.

### WP6 — Docs and batch list
- **What it does:** public docs page for My List (what it is for, how it differs from Favorites and Like), presentation rules, Local checks appended to `engineering/validation/batch.md`, a "Review:" issue.
- **Files likely touched:** `docs/product/presentation-rules.md`, `tuvima-docs` `content/products/library/`, `engineering/validation/batch.md`, `engineering/README.md`
- **Depends on:** WP1 to WP5
- **Size:** Mechanical
- **Done when:** docs build passes and the batch rows exist.

Waves: 1 = WP1 + WP2; 2 = WP3 + WP4; 3 = WP5; 4 = WP6.

## 7. Local checks

- **L1.** Side by side with mocks M1 to M5 at 1024, 1280 and 1440 wide, and M8 at phone width: layout, order, labels, sizes.
- **L2.** On the real library: save one of each kind; the page shows them newest first, chips show the right counts, and each chip narrows correctly.
- **L3.** Remove one title from its "..." menu and three with Select; Undo each time; the order is unchanged after Undo.
- **L4.** Finish a movie and a book that are on the list; Remove finished takes exactly those.
- **L5.** The same plus/tick shows on the hero, details page, video player and Listen album header for a saved title; a song still shows the heart.
- **L6.** Home shows the My List row under Continue; empty the list and the row goes.
- **L7.** Phone or phone-width window: the third tab reads My List and opens the page; Continue swipes; chips swipe; Sort sheet; Select two covers from the "..." sheet and remove them, then Undo. Sort by Title inside the Movies chip gives movies A to Z.
- **L8.** Switch profile: the other profile's list is separate. Kids profile sees only allowed titles.

## 8. Risks and open questions

**Risks**
- The top-bar bookmark and phone tab keep their place but the page changes: Recent activity and Liked are one press further away (links).
- The video player's heart becomes a plus for movies and shows under D8, a visible change to Player v2; it must land after the video player overhaul's first packet (D15).
- Large lists: counts and "finished" must come from the Engine in one read so the page stays fast.

**Open questions:** none. Answered by Shy on 2026-11-11: merge My Library and My List into one page (D1); call it My List (D1, D7); plus and tick, heart only for songs (D8); Home row (D9); finished titles stay, with Remove finished (D10); no hand ordering (D11).

## Plain English summary

My Library and My List become one page, called My List, that the top-bar bookmark and the phone tab open: Continue at the top, then a wall of the covers you saved, newest first, with chips to narrow by kind, one Sort box, and quick removal with Undo. The same plus button adds to it everywhere, Home gets a My List row, and the two older, mismatched views of the list are removed. Nothing about what is stored changes, so nothing saved today is lost.
