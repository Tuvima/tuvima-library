# Spec: One tile design, everywhere

> **Status:** Locked (Shy approved 2026-11-06) · **Version:** 1 · **Last updated:** 2026-11-06 · **Related docs:** [Home, detail page and tile redesign](home-detail-and-tiles.md) (this spec replaces its tile hover, size and caption decisions; see Gaps and drift) · [Library area pages](library-area-pages.md) · [One shared look](one-shared-look.md) · [Look pass](look-pass.md) (section 4.4 menus) · Local check 2026-11-05 bugs 15, 16, 24 and questions 23, 24 · Mockups: [one-tile-design.html](../design/mockups/one-tile-design.html)
>
> **Follows [Look pass v2](look-pass-v2.md)** (a pointer only, no decision above changes): near-black surfaces, purple as an accent only (the hover outline is the accent), 44 px controls and tap targets, Material icons, phone menus opening as bottom sheets, and one tooltip style. Where this spec says the tile "..." menu is the same on a phone, it opens as the shared bottom sheet.

## 1. Goal

Every cover in Library looks and behaves the same, on Home and on every Watch, Read and Listen page. One size, no text under the cover, a "..." menu that is always visible, one quiet hover. It looks closer to a streaming app and removes the size slider, because List view is already the way to see more at once. Fewer controls, one look (Uniformity, Simplicity).

## 2. Scope

**In scope**
- One tile size app-wide; remove the Tile size slider from Watch, Read and Listen (albums and audiobooks too) and the per-tab size memory.
- Tile face: "..." top right always visible (Add to My List inside it, no "+"); hover outline; flush label slots; group count position; no text under covers.
- Continue watching tile text (the one exception) and its play mark, progress bar and menu.
- Phone and tablet behavior of the same tile.

**Out of scope**
- Episode stills on show, season and detail pages stay landscape with their episode number and title text; this spec covers browse and Home tiles, not the detail page's own episode lists.
- Tile shapes (portrait posters, square albums, landscape videos stay), the hero, the detail page, List view layout, Universe and Collections page layouts.
- Android, Apple, Roku and TV clients (own tiles; a short follow-up spec can align them later)
- People tiles and their name line (a face does not say who it is); noted in D9.
- Top 10 or other badge kinds beyond New and New episode (the slot is ready for them).

## 3. Decisions made

Marked **(Shy)** are from Shy's messages of 2026-11-05; the rest are proposed and need a yes.

- **D1 (Shy).** One tile size for every poster across Home, Watch, Read, Listen, Universe and Collections. No size control anywhere. Size matched to the reference page Shy measured: **desktop 214 × 321** (2:3; the reference is 213.75 × 320.63), **phone 112 × 168**. Reason: Home shelves are 158 × 238 and Watch > Movies is 126 × 190 today because the slider rests at its minimum; three sizes make the app feel unfinished. A wider window shows more columns, never bigger tiles.
- **D2 (Shy).** Shapes stay: portrait posters 2:3, square albums, and landscape only for Continue watching (other videos now use the poster-size still-and-text tile, D7). Albums are square at the **same height** as the posters (321 × 321 in mixed rows, 214 × 214 on Listen-only pages), so rows line up. Square tiles are always the same height as posters, even though they are wider (Shy), because music and movies often sit together in the Recently added row. **Exception on the Listen pages (Shy, 57B):** where a page holds only squares (Listen albums, playlists, audiobooks), squares are 214 × 214, the same width as posters, so they do not render too large (about six across at 1440). In any row that mixes squares with posters (Home, search, Collections) they are 321 × 321. Landscape tiles are 320 × 180.
- **D3 (Shy).** "..." sits top right on every cover, always visible, a vertical three-dot mark with no box and a soft shadow so it reads on any art. It holds Add to My List, Restart (once started), Open Universe (when there is one), Edit (desktop, people who can edit), Delete (administrators on a desktop). It is not drawn when it would be empty.
- **D4 (Shy).** No "+" button. Add to My List is the first row of the "..." menu on every device, like the reference ("Remove from My List" once added). Hover is the outline alone.
- **D5 (Shy).** Hover is a thin (2 px) outline in the Library purple (Shy: the hover colour stays our purple) around the cover only. The tile does not grow, move or change shape and there is no purple ring.
- **D6 (Shy).** No text under any poster, album or video tile. The title is on the art. The exceptions are Continue watching (D8) and tiles with no cover art (D7).
- **D7 (Shy, 56).** A tile with no cover art (other videos, home videos, anything imported without a poster) keeps the poster size but uses a **two-part layout**: a landscape still frame from the video fills the top, and the title with one small detail line (count for a folder, "3 videos · Dec 2024"; for a single file, its length) sits as text at the bottom. This is how you tell what it is without cover art. Folder of videos: the frame of its first video, the folder name, the count. If no frame can be made, the top is a plain dark panel with a video icon. Anything else with no art uses the same bottom-text idea on a plain dark cover with the kind's icon: a book shows title and author, an album (square, poster height) shows album title and artist.
- **D8 (Shy, 50 and 55 answered).** Continue watching tiles are landscape with a play triangle at the bottom left of the picture and a progress bar along the bottom edge. Under the picture: the title (or "S3 E8 Episode title"), then the rating and year. The X (remove from Continue) moves into the "..." menu as **"Remove from Continue"** (Shy, question 55; plain wording, since the row holds watching, reading and listening). Because tapping a Continue tile plays it, its menu also starts with a **"Go to ..." row that opens the title's own page**, named by kind: "Go to show" on an episode, "Go to movie", "Go to book", "Go to album", "Go to series". Order on a Continue tile: Go to ..., Add to My List, Restart, Remove from Continue, then Open Universe, Edit, Delete. Other tiles already open their page on click, so they do not need it.
- **D9.** Collections, Series, Universe and album tiles carry their name on the art like any cover. People tiles keep a name line under the photo.
- **D10 (Shy).** Two label slots, both **flush to the artwork edges, not floating with an inset**: a status label touching the **top-left corner** (New, New episode) and a bottom label touching the **bottom-left corner** (later Top 10 and similar). One label per slot. Only the corner that touches no edge is rounded.
- **D11.** A group count (for example "6 books", "11 songs", "3 videos") uses the bottom label slot, so it can no longer collide with the New label (local check bug 15).
- **D13 (Shy).** Menu text size and row height are not set by this spec. They come from the Look pass spec section 4.4 (one menu part: 44 px rows, 16 px text, 22 px icons) and the app-wide menu fix from the 2026-11-05 local check (bug 26: items 14 to 16 px with taller rows, never below 14 px) applies to the tile "..." menu like every other menu. Shy (2026-11-05) also flagged **spacing**: the fonts are probably fine, but today's menu rows run almost edge to edge. Target, from the streaming reference: 8 px of space inside the menu edge on all sides, rows with 16 px side padding and about 48 px height, a 14 px gap between icon and text, the hover highlight an inset rounded block that does not touch the menu edge, and dividers inset with 8 px above and below. This is part of the shared menu part, so it is fixed once for every menu (handed to the bug 26 fix).
- **D14 (Shy, same rule as Activity History D12).** Home Recently Added (and the other Home rows) roll up per parent, so 1,000 new episodes are not 1,000 tiles. Shows, music, books, audiobooks and comics show **one tile per show, album, series or author, with its count** in the bottom label ("12 episodes", "6 books"). Other videos added in a folder show **one landscape tile per folder** with its count ("3 videos"); the files open from the folder's own page. **Movies and photos stay one tile each**, as do video files not in a folder. Opening a rolled-up tile goes to that parent's own page.
- **D15 (Shy).** The New and New episode labels only appear for items from a later import, never from a library's first import (after the first scan everything would say New). The Library activity spec's run recording defines what counts as "new" (later runs only); this spec only places the label and does not define its own rule. The rule now lives in `engineering/specs/library-activity.md` (D14: New for 14 days after a later import, never after a library's first import); the Engine sets `IsNew` on each card (`NewTagRule`) and the tile reads it.
- **D16 (Shy).** Imported videos with no cover art get their still frame from the Videos spec (its D11: own image, then folder image, then online thumbnail, then a good still frame, never a black tile; its WP7 picks the frame). On Home and in every mixed row they use the D7 two-part tile at poster size, so rows stay level and the frame is never cropped into a poster. **The same tile is used everywhere** (Shy): Home, Watch > Other videos, search, Collections. There is no separate landscape tile for other videos. Checking this on real files is a Local check.
- **D17 (Shy).** The "..." looks small but its tappable area is at least 44 × 44 px on every device, so it is easy to hit with a finger.
- **D12.** Touch screens show the same tile with no hover and no text (proposed; a phone tile with no caption relies on the cover art and List view). Screen readers still hear the title.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Normal (desktop) | Cover, "..." top right, optional status label top left, optional bottom label (count) bottom left |
| Hover (desktop) | Thin purple outline only |
| Menu open | Menu opens below "..."; outline stays |
| Keyboard focus | Same outline as hover |
| Phone and tablet | Three across at 112 × 168; same "..." menu without Edit or Delete |
| No actions at all | "..." not drawn (rare, since Add to My List is almost always there) |
| Empty row | Row hidden (unchanged) |
| Loading | Grey tiles at the final size |
| Error | The shared per-row error state (unchanged) |
| Partly ready (still being matched) | The tile shows a "Getting details" label in the top-left status slot instead of the old corner dot |
| No cover art | Still frame on top, title and detail line at the bottom (D7) |

Browse pages show only filters and the Grid / List switch; the Tile size control is gone. Desktop, tablet, phone and TV differ only as described above; TV and Roku use their own tile design in the native clients and are not changed here.

## 5. Data and model changes

- Remove the saved tile size (`TileSize` in the browse state and the `tile=` address parameter). Resets are acceptable (no compatibility shim for old saved sizes).
- Tiles need a few facts that may not all be on the tile view model yet (to be confirmed by the build, which updates Contracts and fixtures together if they are missing): the bottom label text (count), the no-art tile's still frame address and detail line, whether the menu holds "Go to ...", and for Home roll-ups the parent and its count. The "new" rule (D15) comes from the Library activity spec's run recording, so the label follows `DisplayCardDto.IsNew`, set by the Engine's `NewTagRule`.

## 6. Work packets

1. **One size, no slider (Standard).** Remove Tile size on Watch, Read and Listen (`MediaBrowseShell.razor(.css)`, `BrowseState`, `BrowseQueryBuilder`, `ListenPage.razor(.cs)`, `ShelfGridPage.razor.cs`, `AppTimelineResults`); set the single poster size (214 x 321, phone 112 x 168) and the square sizes (D2) in `MediaTileGrid`/`MediaTileShelf` CSS and `app.css`; remove unused strings. Depends on: none. Done when: Home, Watch, Read and Listen show the same poster and no page has a size control or `tile=` parameter; Listen-only pages show 214 x 214 squares and mixed rows 321 x 321.
2. **Tile face (Complex).** Always-visible "..." (44 px tap area, D17), no "+" (Add to My List is the first menu row), thin purple outline hover, flush top-left status and bottom-left labels (count in the bottom one), no caption under tiles. Files: `MediaTile.razor(.css)`, `MediaTileActions.razor`, `MediaTileHoverPolicy`, `MediaSquareTile`, `MediaGroupTile`, `app.css` (`.tl-card-hover--media`). Depends on: 1. Done when: at rest a tile shows only the cover, the "..." and a label or count; no text appears under a poster, album or video tile; the menu follows the shared menu fix (D13).
3. **No-art tile (Standard).** The two-part tile of D7 and D16 (still frame on top, title and detail line at the bottom) for videos and folders of videos, and the plain-cover-with-text version for albums and books; same tile on Home, Watch and everywhere else. Files: `MediaTile.razor(.css)`, tile view model and Engine tile composer for the still address and detail line. Depends on: 2. Done when: a video folder with no cover art shows a still and its name and count on Home and on Watch, never a blank tile.
4. **Continue tile (Standard).** Landscape text lines, play mark, progress bar, "Go to ..." and "Remove from Continue" in the menu (no X). Files: `ContinueLandscapeTile`, `ContinueWatchingOrListeningTile`, `PickUpCard`. Depends on: 2. Done when: a Continue tile matches D8 and its menu order, and tapping the tile still plays.
5. **Home roll-up (Complex).** Home Recently Added and the other Home rows roll up per parent (D14): one tile per show, album, series, author or video folder with its count; movies and photos stay single. Files: Engine Home composer (`DisplayComposerService` area) and Contracts if needed. Depends on: 2 and 3; the "new" rule (D15) follows the Library activity spec. Done when: adding 100 episodes to one show adds one Home tile.
6. **Docs and guardrails (Mechanical).** Update `presentation-rules.md`, the design system and the public docs; add a guardrail test that fails if a browse page offers a tile size control or a non-Continue, non-no-art tile draws a caption. Depends on: 1 to 5.

Waves: 1, then 2, then (3, 4 one after another, both edit tile files), then 5, then 6. Packets 3 and 5 may change Contracts: refresh fixtures one at a time.

## 7. Local checks

On SHY-DESKTOP with a small test library: Home, Watch > Movies, Read, Listen > Albums at 1440 and 1920 wide (same poster size everywhere, columns grow with width); hover outline; "..." and its menu on light covers; menu text size (bug 26 fix); a New item and a group count together; an other-videos folder with no cover art on Home (still on top, name at the bottom, not blank) (no overlap); Continue watching tile; phone width (112 px tiles, menu holds Add to My List); keyboard focus; an item with no picture; a Series, Universe and Collection tile (name on the art); tapping the "..." on a phone (44 px target).

## 8. Risks and open questions

- Titles live only in the cover art. Videos with no cover art use the still-and-text tile (D7); posters rely on their own title art (no hover text, question 50). On touch there is no text at all (D12).
- Albums at poster height are 321 px wide, so a Listen row shows fewer albums than today.
- Add to My List now takes two taps (menu, then row) instead of one; this is Shy's choice for a cleaner tile.

Answered by Shy: no hover text (50), X into the menu (55), albums at poster height (53), size (214 × 321, 112 × 168), no "+" (settles 51), flush labels. Shy confirmed the overrides in section 9 on 2026-11-06. No open questions. Hover title: none (Shy, 50A). Question 54 (count position) is replaced by D11.

## 9. Gaps and drift

All five decided by Shy on 2026-11-06 ("agree with all"): change the code to match this spec.
- Home, detail page and tile spec D1: "thick (3 px) white border plus purple ring" → this spec: thin purple outline, no white border or ring.
- Same spec D2: Watch and Read tiles "larger" → this spec: one fixed size for all.
- Same spec: tile captions under Movie and TV tiles on touch and desktop → removed.
- Same spec: the X at the top right of Continue tiles → moves into "...".
- Same spec: New label stays top-left but flush to the corner; the count and later labels take the bottom-left corner.

## Plain English summary

Every cover in the app becomes the same size and the same design: a clean picture, a three-dot menu always in the top right, a thin purple outline when you point at it, and small dark labels flush to the corners for things like New and counts. The size slider goes away, and there is no "+" button (Add to My List is in the menu); List view is for seeing more or reading details. No tile has text under it except Continue watching, which keeps its title and rating. Phones get the same tile without the hover bits.
