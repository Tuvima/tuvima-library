# Spec: Listen playlists (v1)

> **Status:** Locked · **Version:** 1 · **Last updated:** 2026-11-19 (locked by Shy) · **Related docs:** [Product overview](../product/overview.md) · [Music player and queue](archive/music-player-and-listen-fixes.md) · mockups at `engineering/design/mockups/listen-playlists/mockups.html` · issues 1200 to 1205

## 1. Goal

Playlists work the same on every device and feel finished: one place to see them all, one way to add songs, reordering that works by touch, and a playlist page you can rename, describe and sort. Phones get everything the desktop has.

## 2. Scope

**In scope**
- A Playlists page of tiles (desktop, tablet, phone) with Sort and New playlist (Hand-picked or Smart); phones lose the menu rail, desktop and tablet keep the sidebar.
- Sidebar: keeps every hand-made playlist as a drop target for dragged songs and the Playlists heading as the "New playlist" drop zone; "..." menu per playlist; See all link. Tiles also accept dropped songs.
- One Add to playlist flow for every entry point (song menu, selection bar, player bar menu, drag, queue), with a picker that has search, covers, counts, a New playlist row and an "Already in" tick, and one sentence afterwards.
- Playlist page header (cover, owner, song count, length, description) and a "..." menu: Rename, Edit description and cover, Duplicate, Pin to top, Delete.
- Sort a playlist (Title, Artist, Album, Date added, Length) with "Keep this order" to make it the manual order.
- Reorder by touch (grip), Move to top and Move to bottom, and Move up/down for playlists themselves.
- Save queue as playlist (desktop and phone).
- Fix issues 1200 to 1205.

**Out of scope**
- Export and import of M3U (a later spec; the menu item is not shown until then).
- Shared, public or collaborative playlists, auto-made mixes, offline downloads (native clients).
- Playlist folders; the same song twice in one playlist.

## 3. Decisions made

- **D1.** (Q140A) Phones get a Playlists page of tiles and no rail. Desktop and tablet keep the sidebar. Reason: phone parity, a third of the phone screen was taken by the rail.
- **D2.** (Q141A) Build first: Save queue as playlist, the new add picker, sorting, cover and description. M3U later.
- **D3.** (Q142A) The order of playlists in the sidebar and on the Playlists page stays on the profile, so it follows the person across devices. Songs inside a playlist stay in the playlist.
- **D4.** Dragging songs onto the sidebar stays as today (desktop only); tiles on the Playlists page accept drops too. Touch and phones use the picker. Smart and system lists refuse drops.
- **D5.** The same song cannot be in a playlist twice; adding one that is there says "already there" (kept as today).
- **D6.** One sentence after any add, in place, no toast: "3 songs added to Road trip", "2 added, 1 already there".
- **D7.** The "..." menu is the same everywhere (sidebar, page, tile): Rename, Edit description and cover, Duplicate, Pin to top, Delete (asks once). Smart lists add Edit rules; Favorites has no menu. A bottom sheet on touch.
- **D8.** Type is shown in words ("Smart", "Songs"), heart only for songs, 44 px controls, dark only, tooltips on icon buttons, new words in the translated strings.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Normal (desktop) | Sidebar with playlists and "..."; Playlists page of tiles with Sort and + New playlist; playlist page with header, Play, Shuffle, Sort, Reorder, "..." |
| Normal (phone) | Playlists page reached from the Listen menu; same tiles two across; playlist page with grip handles and "..." per song with Move to top / Move to bottom |
| Adding songs | Picker (small window on desktop, bottom sheet on phone): search, covers, counts, New playlist row, "Already in" tick, Add button; then the one sentence |
| Save queue | Button in the queue panel asks for a name, saves the current and upcoming songs, never changes the queue |
| Empty | No playlists: a short line and a New playlist button. Empty playlist: "Add songs from any song's menu" |
| Loading | Skeleton tiles / rows (the app's normal Updating state) |
| Error | In-place notice with what failed and what was kept; a playlist over 1,000 songs still reorders |
| Partly ready | Songs the library cannot find are listed as "N unavailable songs" in the header and skipped when playing |

## 5. Data / model changes

- Playlist gets an optional description and a cover choice (own picture or automatic mosaic) and a "pinned" flag per profile. Pre-release: a reset is acceptable.
- Reorder takes a position move (item and new place) so it works at any playlist size; the old "send every item" action is replaced.
- The "already there" check no longer stops at 250 songs; the Engine tells how many were added and how many were already there.
- Profile keeps the playlist order as today.

## 6. Work packets

### WP1 — Engine: playlist actions
- **What it does:** description and cover on a playlist; Duplicate; pin per profile; move-by-position reorder with no size cap; add returns added and already-there counts; save-queue-as-playlist input.
- **Files likely touched:** `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs`, Contracts collection types, `src/MediaEngine.Storage/` collection repository, tests (to be confirmed).
- **Depends on:** none · **Size:** Standard
- **Done when:** tests prove a 1,200-song playlist moves an item, duplicates add nothing and report counts, and a Duplicate copies songs in order.

### WP2 — One add flow and picker
- **What it does:** one service and one picker used by the song menu, selection bar, player bar menu, drag drop and queue; removes the unused helpers; same sentence everywhere (fixes 1202).
- **Files likely touched:** `Services/Editing/PlaylistMembership*.cs`, `Components/Details/MediaMembershipDialog.razor`, `Components/Player/PlaybackItemMenu.razor`, `Components/Pages/ListenPage.Playlists.cs`, `ListenPage.TrackActions.cs`
- **Depends on:** WP1 · **Size:** Standard
- **Done when:** every entry point gives the same sentence, the picker shows search, covers, counts and the "Already in" tick, and tests cover duplicates and partial failures.

### WP3 — Playlists page and sidebar
- **What it does:** the tiles page with Sort and New playlist; phones lose the rail; sidebar keeps drop targets, the "..." menu and See all; tiles accept drops; New playlist box gets a Create button and closes on blur (fixes 1200).
- **Files likely touched:** `Components/Pages/ListenPage.razor`, `.razor.css`, `ListenPage.Playlists.cs`, `Components/Listen/ListenNavigationSection.razor`
- **Depends on:** WP2 · **Size:** Complex
- **Done when:** at 1024, 1280, 1440 and phone width the page and sidebar match the mockup, smart playlists can be created on a phone, and dropping songs on a tile adds them.

### WP4 — Playlist page header and menu
- **What it does:** header with cover, owner, count, length, description; "..." menu (D7); Sort with Keep this order; unavailable-songs line (fixes 1204, part of 1203).
- **Files likely touched:** `ListenPage.razor`, `ListenPage.PlaylistTracks.cs`, `Components/Listen/SongTable*`
- **Depends on:** WP1 · **Size:** Standard
- **Done when:** a hand-made playlist shows count and length, Rename and Delete work from its own page, and sorting then Keep this order saves.

### WP5 — Touch reorder and playlist order
- **What it does:** grip drag by touch for song rows, queue and sidebar playlists; Move to top / bottom; a Move up/down way to reorder playlists on the page (fixes 1201).
- **Files likely touched:** `Components/Listen/SongTable.razor(.cs)`, `wwwroot/js/song-table.js`, `Components/Player/QueuePanel.razor`, `wwwroot/js/playback-queue-drag.js`, `ListenPage.Playlists.cs`
- **Depends on:** WP3 · **Size:** Complex
- **Done when:** a touch test reorders a song, a queue row and a playlist, and the keyboard paths still work.

### WP6 — Save queue as playlist
- **What it does:** button and name step in the queue panel, desktop and phone.
- **Files likely touched:** `Components/Player/QueuePanel.razor`, `Services/Playback/`
- **Depends on:** WP2 · **Size:** Standard
- **Done when:** saving a 12-song queue makes a playlist in play order and leaves the queue unchanged.

### WP7 — Words and strings
- **What it does:** move hard-coded wording to the resource files and apply the words-not-icons type line (fixes 1205).
- **Files likely touched:** `Resources/SharedStrings*.resx`, the files above
- **Depends on:** WP2 to WP6 · **Size:** Mechanical
- **Done when:** a guardrail finds no literal playlist words in the Listen files.

## 7. Local checks

- **L1.** Desktop: drag three songs onto a sidebar playlist, onto the Playlists heading (name it), and onto a tile; each says the right sentence; a smart list refuses.
- **L2.** Phone: Playlists page, New (Hand-picked and Smart), add songs from the picker, reorder with the grip, Move to top.
- **L3.** Create playlists of 300 and 1,100 songs and reorder one; counts match what is shown.
- **L4.** Save queue as playlist on desktop and phone; queue keeps playing.
- **L5.** Check at 1024, 1280, 1440 and phone width against the mockups, including the "..." menus.

## 8. Risks and open questions

- **Risks:** touch drag can fight page scrolling (use a grip, not the whole row); moving the sidebar's order logic could lose a profile's saved order (keep reading the old setting); changing the reorder action touches the native clients if they use it.
- **Open questions:** none; all answered 2026-11-19 (Q140 to Q143, all recommended).
