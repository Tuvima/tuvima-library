# Spec: Group pages and tiles refresh

> **Status:** Built, awaiting your Local checks (built 2026-11-22; PRs 1289, 1290, 1292, 1293, 1294; locked by Shy 2026-11-21) · **Version:** 1 · **Last updated:** 2026-11-21 · **Related docs:** [One tile design](one-tile-design.md) · [Universal editor v8](archive/universal-editor-v8-badge-artwork-collections.md) · [Collections and lists](archive/collections-and-lists.md) · [Listen playlists](archive/listen-playlists.md) · Issues #1247, #1251, #1252, #1254, #1260 · Mockups: [group-pages-and-tiles.html](../design/mockups/group-pages-and-tiles.html) · Earlier work still awaiting a Local check: #1172, #1165, #1171, #1190, #1175, #1160, #1173, #1174

## 1. Goal

A group (a Series, a collection, a Universe, a playlist) should look like the same thing everywhere you meet it: the tile on a shelf, the page you open from it, and the Playlists list. The cover on a group's page is the tile's cover collage, larger; the Playlists page uses the shared group tile; and a Series' order in the editor shows what the order is, why, and what it gives. Result: fewer one-off looks (Uniformity, Simplicity), and a page that feels like opening the tile.

## 2. Scope

**In scope (recommended, see Q50)**
- **Playlists page** (#1247): playlists drawn with the shared group tile (landscape collage, name and count band, "..." menu, hover outline), in the same grid.
- **Details page of a Series and of a collection** (#1252): page cover is the tile's collage at page size; header, actions and member grid tidied to one layout; phone version.
- **Home row** (#1254): collections and Series leave Recently added and get their own row.
- **Phone group tiles** (#1260): see WP0.
- **Reading order in a Series** (#1251): a clear "how the order is worked out" then "result" layout in the Series editor.

**Out of scope (proposed; Q50 can widen it)**
- The Universe page and Chronicle explorer (own layout, already has its own design), playlist details page (songs table stays), the collection builder, Home shelves.
- Native clients (Android, Apple, Roku, TV).
- Any change to which covers a collage picks, the 3/4/6 cap or the "+N" chip: those are approved and built (#1175 and others) and only get their Local check.

- **Phone tile parity** (added 2026-11-21, from Shy's local review; a separate issue is being filed): on a phone, the tiles for video collections and similar groups do not line up with desktop. Cause not yet found. The tile code draws video collections and video-still tiles through their own paths (not the shared landscape collage), and the collage picks its covers by the tile's own width, so a narrow phone cell may take a different path than a desktop cell. I have not reproduced it. **Lead found 2026-11-21 (Shy: "no frame extracted on the tile"):** a video collection tile takes its picture only from a cover somebody chose for the group (`CardImageUrl` is the custom cover for any group); it never takes a frame from the videos inside. So with no chosen cover it shows the empty film icon, and the video-collection layout also bypasses the shared collage. **Confirmed in code:** a folder of videos is flagged as a group (`MediaTileComposerService`, `useGroupTile`), and `MediaTile.razor` then reads the picture from `CustomCoverUrl` only, so the picture the Engine sends for the folder (`TileImageUrl`, where a frame would be) is ignored. The fix is one line of intent: for a video collection, fall back to `TileImageUrl`, then to member frames. Why desktop looks different from phone is still unknown (same code path in what I read), so WP0 starts by reproducing both.

## 3. Decisions made

None are Shy's yet; all are proposals for the questions in section 8. Rules carried over: dark only, square pills, no toasts, Series read only, phone parity, cover art in lists, "..." menu as on every tile.

- **D1a (found in code, 2026-11-21).** Playlists are drawn by three different pieces today: the Playlists page's own `PlaylistGridTile` (its own markup and text under the cover), the shared `MediaSquareTile` (Listen, For me, browse), and nothing from the artist tile. The artist tile is the shared quadrant in `MediaTile` (2 by 2, 6 px gaps, 8 px padding, rounded cells, each cover in its own shape, name on a scrim, count in the bottom label), but it only turns on for a person. Decision proposed: make that quadrant available to a playlist (no photo, name and count in the same places) and retire both other playlist drawings, so one component draws every playlist and artist and text renders the same everywhere (this is also the likely fix for the text not rendering on the playlist tile).
- **D1 (Shy, 2026-11-21).** A playlist is always a square tile, filled with the albums of its songs in a 2 by 2 grid, the same as an artist's tile today (square covers, a missing quarter shown as a faint dashed square; the first four albums by play order or song order, to be confirmed in WP1). It is not the landscape collage. Series, collections and Universes keep the landscape collage. The line on the tile says what it is ("Playlist · 48 songs · 3 h 12 min", "Smart playlist · …", "Built in · 126 songs"). Reason: Uniformity, and the playlist tile code stops being a second copy.
- **D2 (revised 2026-11-22 after Shy's review).** The group page keeps the standard details-page hero: full-width backdrop, text, pills and actions on the left, artwork on the right. There is no tile in the hero. The artwork on the right is the group's covers in their native shape, one height, never cropped, with the same "+N" as the tile (the hero's existing group-artwork slot); a cover somebody chose is shown whole instead. The tile and the page therefore read as the same thing without the page copying the tile's frame. Phone: covers on top, text under, as other detail pages do on a phone.
- **D3 (proposed).** Left side of the hero: path, title, type pills in words (Series, Books, years, count), description, one primary action (Read next / Watch next / Play), My List plus, and "...". Series stays read only.
- **D4 (proposed).** Members as a cover grid in order, with the number on each cover; titles you do not own are dimmed with a dashed outline; one "You own 10 of 11 books." line under the grid (matches #1172). Tabs: In order and Details only; no People tab (Shy, 2026-11-22: people live on each item's own page).
- **D4a (Shy, 2026-11-22).** Shy likes the "In order" section under the hero; it stays.
- **D5 (proposed).** Reading order in a Series (editor, Series stays read only): step 1 the rules in the order they are tried, each as a square pill with how many titles it placed; step 2 the result as a list (number, small cover, title, detail line) with, on the right, the rule that placed each title; an amber chip marks one placed by a later rule. Heading words stay as today (Reading, Watch, Listening order, "In order" for mixed).
- **D7 (Shy, 2026-11-22; shape proposed).** Playlist details page gets a list view and follows what Apple Music and Spotify do: standard hero with the playlist's square album quadrant on the right, Play and Shuffle, Add songs, "..."; under it a song table (number, small cover, title and artist, album, date added, time) with a search-and-sort bar and a Cards/List switch, drag to reorder on desktop and touch reorder on phone, and a footer total ("48 songs, 3 h 12 min"). Pending Q60 for how much of that to take now.
- **D6 (proposed).** No new data. The editor already knows the members and their order; the "which rule placed it" chip needs the Engine to say it (Q54).

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Playlists, normal | Grid of group tiles; "New playlist" top right; Sort beside it (phone: its own line) |
| Playlists, drop a song on a hand-made one | Purple outline and a tinted fill on that tile; Smart and built-in tiles do not react |
| Playlists, empty | Heading, "New playlist" button, one line saying nothing is here yet (as today) |
| Playlist with no covers | Group tile with the playlist icon (the shared "no covers" tile) |
| Group page, normal | Mockup B at 1024, 1280, 1440, phone |
| Group page, nothing owned yet | Collage empty tile with kind icon; members all dimmed; the owned line says "You own none of the 11 books." |
| Group page, loading | Grey cover and grey grid at final size |
| Reading order, normal | Mockup C |
| Reading order, one rule only | One pill; every row's chip says that rule |
| Reading order, order unknown | Rules area says "No order known. Titles are listed A to Z." |
| Phone | Grids three across (covers) or one across (playlist tiles at 2 across if width allows); rule chip drops under the title; menus open as bottom sheets |

## 5. Data / model changes

- Playlists page gets the cover set (up to six song covers) and the count and length already known to the page. No data store change.
- Reading order: the editor needs, per member, the rule that placed it (Q54). If not available it ships without the chip and shows only the result list.
- Pre-release, no migration needed.

## 6. Work packets

### WP0 — Phone tile parity for groups
- **What it does:** finds why video collections and other groups differ on a phone, then makes video collections take a frame from their own videos when no cover is chosen (the Videos spec's order: own image, folder image, online thumbnail, extracted frame), and makes the same group tile (same shape, collage, band, "..." and count slot) draw at phone width as on desktop; adds a phone-width check to the tile tests.
- **Files likely touched:** `Components/MediaTiles/MediaTile.razor(.css)`, `Components/Shared/GroupCoverCollage.razor.css`, `Services/MediaTiles/GroupCollageLayout.cs` (to be confirmed).
- **Depends on:** a screenshot or the new issue's description of what differs (Q57) · **Size:** Standard
- **Done when:** a video collection, a Series and a playlist show the same tile at phone and desktop widths, differing only in size and column count.

### WP0b — Home "Collections and series" row
- **What it does:** Recently added shows only new titles (the existing roll-up per show, album, book Series stays, D14 of one-tile-design); collections and Series get their own Home row of group tiles.
- **Files likely touched:** Home page and its row builder in the Dashboard, the Engine's Home rows action (to be confirmed).
- **Depends on:** Q58, Q59 · **Size:** Standard
- **Done when:** a new collection never appears in Recently added; the new row shows group tiles at four widths; empty row is hidden.

### WP1 — Playlists use the group tile
- **What it does:** draws each playlist (and Favorites, Smart, built-in) as the shared square quadrant of `MediaTile` (extended to take a playlist, see D1a), retiring `PlaylistGridTile` and `MediaSquareTile` for playlists, with the type line, "..." menu and drop highlight.
- **Files likely touched:** `Components/Listen/PlaylistGridTile.razor` (retired or thin wrapper), `PlaylistTilesView.razor(.css)`, `Components/Shared/GroupCoverCollage.razor`, `Services/MediaTiles/` (a playlist-to-tile composer), tests (to be confirmed).
- **Depends on:** none · **Size:** Standard
- **Done when:** the Playlists page shows group tiles at 1024/1280/1440/phone; drag-a-song onto a hand-made tile still adds it; the old tile CSS is gone; tile tests pass.

### WP1b — Playlist details page (Apple Music / Spotify style)
- **What it does:** standard hero with the playlist's square album tile on the right, Play, Shuffle, Add, "..."; song list (number, cover, title and artist, album, date added, time) with search, sort, List / Cards switch, a visible grip on every row of a hand-made playlist (drag by mouse or finger, none on Smart or built-in), an "Add songs" button in the hero and a row under the list that opens a search where tapping + adds a song (to the end; Q61), now-playing highlight, row menu and total line; phone drops the album and date columns and shows the mini player. Mock screen D.
- **Files likely touched:** `Components/Pages/ListenPage.PlaylistHeader.cs`, `ListenPage.PlaylistTracks.cs`, `Components/Listen/SongTable.razor(.cs)`, `playlist-reorder.js`, `TouchReorderRules.cs` (to be confirmed).
- **Depends on:** WP1 · **Size:** Complex (the queue and reorder code has open bugs #1245 to #1249; fix or avoid touching them in the same change)
- **Done when:** the page matches mock D at four widths; reorder works by mouse and by touch; Smart lists show no handle.

### WP2 — Group page header and cover
- **What it does:** uses the collage component as the page cover for a Series and a collection; one header layout with pills, description, primary action, My List and "..."; phone layout.
- **Files likely touched:** `Components/Details/DetailHero*.razor`, `DetailPage.razor(.css)`, `GroupCoverCollage` (page size option), group page route (to be confirmed).
- **Depends on:** none · **Size:** Standard
- **Done when:** page cover equals its tile (same covers, same chosen cover) at the four widths.

### WP3 — Members grid and owned line
- **What it does:** ordered member grid with numbers, dimmed unowned, one owned line, tabs In order / People / Details.
- **Files likely touched:** `Components/Details/DetailOwnedLine.razor`, `OverviewTab.razor` (to be confirmed).
- **Depends on:** WP2 · **Size:** Standard
- **Done when:** a Series with a missing book shows it dimmed with the right number and the owned line says 10 of 11.

### WP4 — Reading order layout
- **What it does:** rebuilds the order block in the Series editor as rules then result; chip per row.
- **Files likely touched:** `Components/Editor/EditorDetailsPanel.razor`, `Services/Editing/EditorSeriesRules.cs`, a new small component, `SharedStrings.resx`, Engine editor session DTO if Q54 = yes (Contracts and fixtures).
- **Depends on:** Q54 · **Size:** Standard (Complex if the Engine change is included)
- **Done when:** the editor shows both steps at four widths and a novella shows the amber chip.

### WP5 — Docs and Local checks
- **What it does:** update presentation rules, the public docs page for groups, add the Local checks to the batch list.
- **Depends on:** WP1–WP4 · **Size:** Mechanical

## 7. Local checks

- **L1.** Playlists page with 6+ playlists, a Smart and Favorites at 1024 / 1280 / 1440 / phone: group tiles, hover outline, drop a song on a hand-made one.
- **L2.** A book Series, a TV show group, a mixed collection (book, album, film): page cover matches the tile; a chosen cover appears on both.
- **L3.** A Series with a missing book and a novella: order block shows rules, result and the amber chip.
- **L4.** Same pages on a phone: covers three across, menus as bottom sheets.

## 8. Risks and open questions

- **Risks:** a larger collage on the page repeats the cover art that the member grid shows right under it (mitigation: the page cover stays compact, 400 to 460 px, and the grid is the main content). The "which rule" chip may need an Engine change. The earlier group collage and page layout work (#1160 to #1190) has not had its Local check yet; this spec builds on it, so a failed check there would shift this work.
- **Questions, all answered "agree with all" (Shy, 2026-11-22), taking the recommended option:** Q50 C (Series, collections, Playlists page and playlist details page; Universe page left for its own spec after its Local checks), Q51 square album tile (Shy's own answer, D1), Q52 covers on the right of the hero (D2), Q53 numbered cover grid (D4), Q54 the Engine reports which rule placed each book (D6), Q55 the Series editor's In order list is the screen meant by #1251 (still an assumption: confirm on first look at the built screen), Q56 no hover on the page cover, Q57 the build finds every phone and desktop tile difference in the code and fixes it (WP0), Q58 Home row "Collections and series" right after Recently added, Q59 a Series that gains a book moves up, Q60 playlist details gets the streaming-style list now (WP1b), Q61 added songs go to the end.
- **No open questions remain.** Waiting only for Shy to say "lock it".

## Plain English summary

Today a Series, a collection and a playlist each look a bit different, and the page you open doesn't look like the tile you clicked. This plan makes them one family: playlists get the same tiles as collections and Series, the page's cover is that same picture made bigger, and the list of books in order is laid out so you can see the order, why it is that order, and what it gives. It reuses what's already built, adds no new data, and only possibly asks the Engine to say which rule placed each book. I need your answers on which group types to include (recommended: Series, collections and the Playlists page) and a few look choices.
