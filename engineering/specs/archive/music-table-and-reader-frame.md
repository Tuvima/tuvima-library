# Spec: One song table and one reader frame

> **Status:** Built, awaiting your Local checks · **Version:** 8 · **Last updated:** 2026-11-11 (locked by Shy; built the same day) · **Related docs:** [Product overview](../../product/overview.md) · [Look pass v3](../look-pass-v3.md) · [Music player and queue](../music-player-and-queue.md) · [PDF and comic reader](pdf-and-comic-reader.md) · [Book reader images](../book-reader-images.md) · Mockups: [design/mockups/music-table-and-reader-frame.html](../../design/mockups/music-table-and-reader-frame.html)

Source: thread 7 of the Dashboard remediation run (`engineering/handoffs/2026-11-11-dashboard-remediation.md`, "Thread 7 result"). Shy approved planning both on 2026-11-11 ("Agree with all"). Leaving the photo editor and the media editor as two separate editors was settled in the same answer and is out of scope.

## 1. Goal

Every list of songs looks and behaves the same, and every reader looks and behaves the same. Someone who learns the Songs table already knows the album track list and a playlist; someone who learns the book reader already knows the PDF and comic reader. It also means one place to fix or polish each, instead of three song tables and two reader frames drifting apart.

## 2. Scope

**In scope**
- One shared song table used by the Listen Songs and Albums lists (today `ListenSongTable`), the album page track list (today the music side of `AudioItemTable`) and the playlist page (today an inline table in `Pages/ListenPage`), with one set of columns, sorting, resizing, row actions, now-playing mark, phone list and look.
- One shared reader frame used by the book reader (`BookReader`) and the page reader for PDFs and comics (`PageReader`): top line, tools, bottom line, auto-hide, side panel and phone sheet.
- Making the same behaviors match where the two copies differ today (section 4 lists each).
- Moving the book reader's typed English panel titles into the string table as part of the move.

**Out of scope**
- The photo and media editors (settled: stay separate).
- The audiobook chapter list inside `AudioItemTable` (it keeps its own table, which already shares the Look v3 tokens).
- Any change to what the Engine sends, the data store or the wire contracts.
- New features: no new tool, column or setting beyond the Columns button on Songs and Albums and the shared tool order.
- The reader's page drawing, PDF.js, foliate-js, bookmarks and highlights logic: these move into the frame's slots unchanged.

## 3. Decisions made

(Proposed; each gets Shy's yes when locked. Q1-Q6 from the first draft are answered in D20.)

- **D1. One table, three hosts.** A single `SongTable` component owns the columns, header (sort, resize, Columns button), row (number or equalizer, art, title, heart, time, hover queue, "..." menu), now-playing mark, phone list and CSS. Each page stays the host of its own data and behavior (what to play, how to delete, the bulk Edit panel, playlist reorder) and passes them in through a few parameters and row actions. Reason: Maintenance and Uniformity.
- **D2. One row model.** The table takes a small `SongRow` (id, title, artist, album, cover, duration, date added, plays, owned or missing, number, and the ids needed for play, queue and open actions). Mapping from the three current view models (`WorkViewModel`, `MediaGroupingItemViewModel`, playlist item) lives in one mapper with tests. Reason: Maintenance.
- **D3. One column set, one order.** Tick · # · Song (art, title) · Artist · Album · Date added · Plays · Genre · Favorite · Time · Columns/"..." (queue appears on hover). Each place chooses which columns exist and which start on: Songs: all but Genre; Album page: #, Song, Time, Status, Favorite, plus Artist only when the album has more than one artist; Playlist: #, Song, Artist, Album, Favorite, Time. Favorite is always right before Time. Today the order differs per table. Reason: Uniformity.
- **D4. Columns button everywhere.** The header's last cell holds a Columns button that opens the same list the playlist page already has. Visible columns and widths are remembered per profile for each place (Songs, Albums, Playlist, Album page). Reset columns puts back the defaults. Reason: ease of use; today only the playlist can hide columns.
- **D5. Sorting.** Every sortable header sorts the same way (first click ascending, except Date added, Plays, Time and Favorite, which start descending, as today's Songs table). The album page keeps its fixed "numbered tracks first, Unnumbered group last" rule from the details overlay spec D13. The playlist is in its own saved order and its headers do not sort (sorting would fight drag order); the sort arrow is absent there.
- **D6. Now playing.** The playing song shows the equalizer mark in place of its number and its title in the accent color, in all three places and for the song that matches the current queue item. Reason: Uniformity.
- **D7. Heart.** Songs only (on a phone it moves into the "..." sheet, D10), outline until saved and filled once saved, saves to Favorites (not My List), same size and place everywhere. Unowned (missing) tracks have no heart action.
- **D8. Row actions.** Add to queue shows on hover or keyboard focus before the "..." menu. The "..." menu is one list in one order: Play next, Add to queue, Go to album, Go to artist, personal status (listened and so on), Edit (administrators on a desktop), Remove from playlist (playlist only, red), Delete from library (administrators on a desktop, red, last). Items that do not apply are left out, not disabled, except Go to album and Go to artist which show disabled when the target is unknown. Reason: fewer controls, predictable menus.
- **D9. Playlist reorder.** Drag from the handle that replaces the number on hover; a purple line shows the drop spot; keyboard users move the focused row with Alt+Up and Alt+Down; on a phone the "..." sheet gets Move up and Move down. Reorder only exists in a playlist the person may edit. The existing "reorder-upcoming"-style guarded calls are untouched.
- **D10. Phone: a plain list** (Shy, 2026-11-11: mirror Apple Music and Spotify). Under 600 px every song list is a simple, flat list by default, with no card boxes: cover on the left (none on an album's own page, whose art is at the top), title with the artist beneath (no album name, so a row never wraps; the album is in the "..." sheet's Go to album), "..." on the right, 64 px rows. Tap plays; press and hold or "..." opens the bottom sheet. The playing song has its title in the accent color and a small equalizer mark. The heart is not on the row: it is the first item of the "..." sheet (Add to Favorites / Remove from Favorites) and sits on the full player. A playlist's Move up and Move down are in the same sheet. No columns, resizing or drag on a phone. Reason: a plain list is faster to scan on a small screen and matches what people already know.
- **D21. Phone sort, filter and search stay** (Shy asked, 2026-11-11). Without column headers, the phone Songs page header carries them, as Apple Music does (mockup A6): a search field ("Search Songs"), Play and Shuffle buttons, and a sort and filter button that opens a bottom sheet with Sort by (Title, Artist, Album, Date added, Plays, Time; ascending or descending) and the filters the page already has (Owned, Favorites, genre). The choice is remembered per profile. Albums and playlists keep their own existing header controls. Nothing new is stored or sent: the same sort and filter rules as desktop. An A to Z jump strip with letter headings appears while sorted by Title or Artist (Shy, 2026-11-11: include it).
- **D22. Phone album page** (Shy's Apple Music screenshot, 2026-11-11). The album page keeps its existing header (large cover, title, artist, facts, Play, Shuffle, description) and the track list sits below it as the same plain list, without covers: track number, then the title with an Explicit badge when it has one, and "..." on the right. The artist shows beneath a title only when it differs from the album's artist (compilations). Missing tracks (Show missing tracks on) say "Missing" beneath. No change to the header itself.
- **D23. Phone playlist page** (Shy, 2026-11-11). The playlist page keeps its existing header, including the cover that is a collage of covers from the albums in the playlist (the page already draws this collage; this spec does not change it). Below it the songs are the same plain list as Songs (cover, title, artist, "..."), with Move up and Move down in the "..." sheet.
- **D11. Look.** Look v3 tokens only: 48 px rows, 36 px art, 44 px targets, Material icons, the shared row-menu and overflow-menu components, no toasts (a failed or undoable action shows in place above the table, as the Songs table does now).
- **D12. One frame.** A single `ReaderFrame` component (in `Components/Reader/`) owns: the viewer shell, the stage slot the reader draws into, the top line (title, subtitle, tools, then Close at the far right), the bottom line slot, the auto-hide rule, the side panel with its dim, header and Close, the phone "..." sheet, the live region and the saved-page announcement. `BookReader` and `PageReader` keep their own logic (position saving, search, highlights, pages) and fill the frame's slots. Reason: Maintenance; D1 of the PDF and comic reader asked for one look and the CSS is still two files (`book-reader.css`, `page-reader.css`).
- **D13. One tool order.** Search · Contents · Pages · Bookmark this page · Bookmarks · Reading settings, then Close as the last control at the top right. A reader leaves out a tool it does not have: a book has no Pages grid; a comic has no Search or Contents; a PDF has all six.
- **D14. One bookmark button.** The bookmark icon is outline when this page is not bookmarked and filled when you have bookmarked it; pressing again removes yours (the page reader already works this way). The book reader gets the same.
- **D15. One panel.** Same width (400 px on desktop), same dim, same title style and Close position, same Mine | Everyone switch and "Bookmark this page" button in the Bookmarks panel. The book's Bookmarks panel keeps listing highlights beneath your bookmarks, as the Bookmarks and highlights panel does now, under the shared title "Bookmarks".
- **D16. One phone layout.** On a phone only Bookmark this page and Close stay on the top line; every other tool goes in one "..." bottom sheet (the page reader does this today; the book reader gets it too).
- **D17. One look for the background.** The frame reads three colors (page background, bar text, accent) from variables. The book's themes (Light, Sepia, Dark, System) and the page reader's backgrounds (Black, Dark, Light) both set those variables; the frame itself has one stylesheet, `reader-frame.css`, and the old duplicated bar, title, tools, panel and scrim rules are removed from both existing files.
- **D19. Close is always top right** (Shy, 2026-11-11). Both readers replace the Back arrow at the top left with a Close (X) at the top right, in the same place as the panel's own Close, so a person can always find it. It does what Back does today (saves the page, then returns to the Library page the reader was opened from). The top-left corner holds the title. Reason: Uniformity; one place to look for Close across the app.
- **D20.** Q1-Q6 answered A by Shy (2026-11-11): single click plays everywhere on desktop (D3 row rule); columns remembered per profile and place (D4); book bookmark is the same toggle (D14); audiobook chapter list stays its own table (out of scope); phone playlist reorder uses Move up and Move down; both streams run at the same time.
- **D18. No behavior change on the page.** Page drawing, zoom, spreads, right-to-left, saved position, progress and finished rules are unchanged.

## 4. UI behaviour

Screens are in the mockups (A1-A5, B1-B3); the numbers below match.

| Situation | What the person sees |
|---|---|
| Songs, desktop (A1) | Table with tick, #, Song, Artist, Album, Date added, Plays, Favorite, Time and the Columns/"..." cell; sort arrow on the active header; resize handle between headers. |
| Album page (A2) | Same table; Show missing tracks beside the track count; missing rows dimmed with a Missing chip and no actions; Unnumbered group at the end. |
| Playlist (A3) | Same table; drag handle in place of the number on hover; Remove in "..."; saved order, no sort arrows. |
| Columns (A4) | Ticked list plus Reset columns. |
| Phone Songs page (A6) | Header with search, Play, Shuffle, sort and filter button, then the plain list (D21). |
| Phone (A5) | Plain flat list (cover, title, artist, "..."), same on all three. |
| Empty | Songs: the existing empty line for that page. Album: "No tracks match the current view." Playlist: the existing "Nothing in this playlist yet" with the add hint. Unchanged wording. |
| Loading | The table shows the existing skeleton rows; the header is already visible. |
| Error | A failed favorite, delete or status change shows an in-place notice above the table with Close (existing `AppAlert`); never a toast. |
| Partly ready | A song still being identified shows the existing "Updating details" state on its art and disables its Edit. |
| Book or PDF/comic (B1) | Title at the left, tools in the same order, Close (X) at the top right, bottom line chosen by the reader. |
| Panel (B2) | Same panel for Bookmarks, Search, Contents, Pages and Reading settings. |
| Phone reader (B3) | Bookmark this page plus "..." sheet in both. |
| Reader loading or can't open | The frame shows the page-reader's current states: spinner while opening; "can't open" with Close; unchanged for books. |
| Touch devices | No resize handles and no hover-only controls: the queue and Edit actions are in the "..." menu; the table's drag is off on touch (as today). |

Differences from today that the person will notice:
- Songs and Albums gain the Columns button.
- The album page loses double-click-to-play and gains single click.
- The album page gets a heart in the same place as Songs, and its rows look like Songs rows (art, hover queue, same menu).
- The playlist offers Artist, Album, Favorite and Time only (no Genre, Plays or Date added).
- Both readers lose the Back arrow at the top left and gain Close at the top right (D19).
- The book reader gains the shared tool order, the filled bookmark and the phone "..." sheet; its panel titles are translated like the rest.

## 5. Data / model changes

None to the Engine, wire contracts or data store. The Dashboard remembers, per profile and place, which song columns are shown and their widths; this uses the profile preference store the Dashboard already has (`IUserPlaybackPreferencesAccessor` family), as the page reader's background and layout choices do. Pre-release: no migration; unset means defaults.

## 6. Work packets

Two independent streams (music and reader) that can run in parallel; each stream's packets run in order. Everything is Dashboard code under `src/MediaEngine.Web/` with tests under `tests/MediaEngine.Web.Tests/`.

### Music

#### WP1 — Song row model, columns and sort rules
- **What it does:** `SongRow`, a mapper from the three current view models, `SongTableColumns` (the column set, per-place defaults, widths, remembered choice) and the sort rules (including the album page's Unnumbered group), all pure code.
- **Files likely touched:** new `Components/Listen/SongRow.cs`, `SongTableColumns.cs`, `SongTableSort.cs`; `Services/Playback/` for the remembered column choice; tests.
- **Depends on:** none
- **Size:** Standard
- **Done when:** unit tests cover mapping from each source, the per-place defaults, Favorite-before-Time order, each sort including descending-first columns and Unnumbered placement, and the remembered choice round trip.

#### WP2 — `SongTable` component
- **What it does:** the shared table: header with sort, resize and Columns button, virtualized rows, now-playing mark, heart, hover queue, shared "..." menu with the D8 order, selection ticks and the bulk Edit host as an optional slot, drag handle and drop line as an optional playlist mode, phone list, in-place notice and Undo slots, scoped CSS.
- **Files likely touched:** new `Components/Listen/SongTable.razor` (+ `.razor.cs`, `.razor.css`); `wwwroot/app.css` only if the shared phone-card rules need a hook; tests (bUnit).
- **Depends on:** WP1
- **Size:** Complex
- **Done when:** bUnit tests render all three modes, the phone list, a missing row (no actions), sort, column hide and reset, keyboard row move and the no-hover touch path; `StyleOwnershipGuardrailTests` and the 2,000-line CSS cap pass.

#### WP3 — Songs and Albums lists on `SongTable`
- **What it does:** replaces `ListenSongTable` in `ListenPage` (albums, songs) and `MediaBrowseShell` with `SongTable`; keeps the tick boxes, Edit, delete flow and the browse header's requested sort.
- **Files likely touched:** `Components/Pages/ListenPage.razor` and partials, `Components/Browse/MediaBrowseShell.razor` and partials, `Components/Listen/ListenSongTable.razor*` (removed).
- **Depends on:** WP2
- **Size:** Standard
- **Done when:** the existing ListenSongTable tests are ported and pass against `SongTable`; the Songs and Albums pages show the Columns button; source-text guardrails (typed wording, no storage code, query-attribute ban) still pass.

#### WP4 — Album page track list on `SongTable`
- **What it does:** the music side of `MusicTrackList` / `AudioItemTable` uses `SongTable` with the album defaults, Show missing tracks, the row tick box, the single-click rule (D20) and the heart. The audiobook side is untouched.
- **Files likely touched:** `Components/Details/MusicTrackList.razor`, `Components/Details/AudioItemTable.razor*` (music branch removed), tests.
- **Depends on:** WP2
- **Size:** Standard
- **Done when:** album pages show the same table with missing-track rows, Unnumbered group and bulk tick; audiobook pages are visually unchanged; the details tests pass.

#### WP5 — Playlist page on `SongTable`
- **What it does:** the playlist page's inline table and its column chooser menu are replaced by `SongTable` in playlist mode: drag handle, drop line, Remove, Move up and Move down on phones, keyboard reorder.
- **Files likely touched:** `Components/Pages/ListenPage.razor`, `ListenPage.PlaylistTracks.cs`, `ListenPage.razor.css` (playlist rules removed), tests.
- **Depends on:** WP2 (and after WP3 because both edit `ListenPage`)
- **Size:** Standard
- **Done when:** reorder by drag and by keyboard produces the same saved order as today; the playlist column menu is the shared Columns menu; the `ListenPage.razor.css` playlist selectors are gone.

#### WP6 — Retire the old tables and write it down
- **What it does:** deletes dead selectors and code, updates the guardrail allowlists, `docs/product/presentation-rules.md` and the Dashboard map in `src/MediaEngine.Web/CLAUDE.md`, and the tuvima-docs page that describes Listen.
- **Files likely touched:** the CSS and code left over from WP3-WP5, `docs/product/presentation-rules.md`, `src/MediaEngine.Web/CLAUDE.md`, `tuvima-docs` Listen page, `engineering/README.md`.
- **Depends on:** WP3, WP4, WP5
- **Size:** Mechanical
- **Done when:** a repo search finds no `listen-song-table`, `listen-playlist-track` or `tl-audio-item-table` selector for music; docs build passes.

### Reader

#### WP7 — `ReaderFrame` component and stylesheet
- **What it does:** the frame (shell, stage slot, top line with tool slots, bottom slot, auto-hide, panel, phone "..." sheet, live region) and `reader-frame.css` with the three color variables. No reader uses it yet.
- **Files likely touched:** new `Components/Reader/ReaderFrame.razor` (+ code and scoped parts), `wwwroot/css/reader-frame.css`, tests.
- **Depends on:** none
- **Size:** Complex
- **Done when:** bUnit tests render the frame with each tool combination, the phone sheet, the panel and the hidden-chrome state; accessible names, focus order and 44 px targets verified by test.

#### WP8 — Page reader on the frame
- **What it does:** `PageReader` fills the frame's slots and drops its own bar, panel and scrim markup and rules; page drawing and the JS stay as they are.
- **Files likely touched:** `Components/Pages/PageReader.razor*`, `wwwroot/css/page-reader.css`, tests.
- **Depends on:** WP7
- **Size:** Standard
- **Done when:** existing page-reader tests pass unchanged in behavior; `page-reader.css` holds only page-stage rules.

#### WP9 — Book reader on the frame
- **What it does:** `BookReader` fills the frame; tool order, the bookmark toggle, phone "..." sheet and translated panel titles (moved into the string table in all four languages); theme colors set the frame variables.
- **Files likely touched:** `Components/Pages/BookReader.razor*`, `wwwroot/css/book-reader.css`, `Resources/SharedStrings*.resx`, tests.
- **Depends on:** WP7 (after WP8 so the shared pieces are proven)
- **Size:** Standard
- **Done when:** the book reader tests pass; no typed English remains in the panel titles (typed-wording baseline goes down); `book-reader.css` holds only the book stage, highlight and theme rules.

#### WP10 — Retire duplicates and write it down
- **What it does:** removes any leftover `br-*` or `pr-*` bar, title, tools and panel rules and dead helpers; updates the presentation rules, the Dashboard map and the PDF and comic reader docs page.
- **Files likely touched:** both reader CSS files, `docs/product/presentation-rules.md`, `src/MediaEngine.Web/CLAUDE.md`, tuvima-docs reader page.
- **Depends on:** WP8, WP9
- **Size:** Mechanical
- **Done when:** a repo search finds one definition of each shared bar, title, tools and panel rule; the CI-strength build and tests pass.

## 7. Local checks

- **L1.** Songs, Albums and Playlist at 1024, 1280 and 1440 px on a small generated music library (never the real one): same header, rows, hover queue, heart and menu in all three; resize a column, hide one, reset, reload the page and see widths and columns remembered.
- **L2.** Album page: Show missing tracks dims a missing row and gives it no actions; the Unnumbered group stays last whatever is sorted; ticking tracks opens the bulk Edit bar as before.
- **L3.** Playlist: drag a song, drop it between two others, reload and see the order kept; do the same with the keyboard; on a phone use Move up and Move down.
- **L4.** Phone width (360 and 390): the three lists are the same plain list; the playing song has an accent title and the equalizer mark; the heart is in the "..." sheet; every target is at least 44 px.
- **L5.** Book (EPUB), PDF and comic at 1024, 1280, 1440 and phone: same top line, same tool order, same panel; open Bookmarks, Search, Contents, Pages and Reading settings in each and compare against mockups B1-B3.
- **L6.** Bookmark a page in the book and in a PDF: the icon fills, pressing again removes it, the Bookmarks panel lists it in both.
- **L7.** On `Z:\Development\testing\media\real_media`: a large music library scrolls smoothly in Songs (virtualized), and a long EPUB and a long PDF still open to the saved page. Reason: the cloud has no real media or screenshots.

## 8. Risks and open questions

- **Risks:** the shared table touches four pages at once, so a mistake shows in many places (mitigation: WP3-WP5 land one at a time behind tests, and Local checks L1-L4). Songs is virtualized and the album and playlist lists are not: the shared table virtualizes all three, which needs the playlist drag line to work with it (WP5 done-when covers it). The reader frame must keep the auto-hide and the page reader's saved page and announcement exactly (D18; WP8 tests). Both specs the work touches (`pdf-and-comic-reader`, `look-pass-v3`) are Built; this spec changes only the structure under their rules, not their meaning.

**Open questions:** none. All answers are recorded in D20 and D21; Shy still has to say "lock it".

## Plain English summary

Today Library has three different song tables and two different reader frames that were each built for one place and then drifted apart. This plan makes one song table that the Songs list, the album page and playlists all use, so hearts, menus, sorting, hiding columns and phone list work the same everywhere, and one reader frame that books, PDFs and comics all sit in, so the buttons, bookmarks and panels are in the same place whatever you are reading. Nothing about what is stored or sent changes, and the photo and media editors stay separate as already agreed. The work is split into small steps that can each be tested, and every screen is in the mockups so you can see exactly what you will get. 
