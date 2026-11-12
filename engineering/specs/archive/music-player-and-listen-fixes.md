# Spec: Music player and Listen fixes

> **Status:** Built, awaiting your Local checks (Locked by Shy 2026-11-11; built 2026-11-12 in PRs 914 and 940; Local checks L1 to L7 are in `engineering/validation/batch.md`) · **Version:** 1 · **Last updated:** 2026-11-12 · **Related docs:** [Unified player v2](unified-player-v2.md) (Built; this amends D5 and D24) · [One song table and one reader frame](music-table-and-reader-frame.md) (Built; this amends D4) · [Music player, queue, lyrics and Songs list](../music-player-and-queue.md) (Built; this amends the lyrics lookup) · [Collections and lists](collections-and-lists.md) (Built; this amends the Add to collection rule for songs) · Mockup [`../design/mockups/unified-player-v2.html`](../../design/mockups/unified-player-v2.html) · Issues #886, #887, #893, #894, #895

## 1. Goal

Fix the five Music and Listen problems found in the local check on 2026-11-11, so that lyrics appear, the player is always in view and the same size as the video player, Now playing matches the approved mock, song columns can be resized, songs go into playlists (by menu or by dragging), and Favorites behaves like a playlist.

### Plain-English walkthrough of the finished experience

1. **Lyrics show up.** Open Lyrics on a song. It says "Looking for lyrics…" for a moment, then shows the lyrics. If there really are none, it says "No lyrics found" with Find lyrics and Add a lyrics file. (WP1, WP2, WP3)
2. **Play on an album shows the player at once.** Press Play on an album page and the player bar is there straight away, in front, and every button on it works while the album page is still open. (WP4)
3. **One bar size.** The music and audiobook bar is exactly as tall as the video bar, with the same small cover size and no empty space. (WP5)
4. **Now playing looks like the mock.** The Now playing page matches the approved mock screen at 1024, 1280, 1440 and phone width. (WP6)
5. **Columns resize.** Drag the edge of a song column header to make it wider or narrower. Double-click the edge to put it back. The width is remembered for your profile on every device. Works the same in Songs, Albums, the album page and playlists. (WP7)
6. **Ticked songs go to a playlist.** Tick several songs and the bar offers Play, Add to queue, Add to playlist and Edit. Add to collection is gone for songs. (WP8)
7. **Drag songs onto a playlist.** Drag one song, or all the ticked songs, onto a playlist in the left rail or onto a playlist tile on the Playlists page. The playlist lights up while you hover and the songs are added when you drop. (WP9)
8. **Favorites is a playlist.** Clicking Favorites in the rail opens it inside Listen as a playlist with the same song table, sorting, reordering and play actions. It lists the songs you hearted. My List stays a separate place for everything else you saved. (WP10)
9. **The phone can do the same.** On a phone, Select in a song's "..." sheet ticks songs and shows Play, Add to queue and Add to playlist. (WP12)

### What stays the same

The queue, audiobook chapters, the video player, the heart and Like/Dislike rules, and where lyrics come from (your files first, then LRCLIB). Music bookmarks are being removed by the Global bookmarks work and are not touched here.

### What the code showed (audit, 2026-11-11)

- **Lyrics (#886).** The lyrics source works: a test request to LRCLIB returned lyrics. Library asks badly. (1) It measures each song's real length from the file but the lookup reads a different, older length field that is often empty or from an outside source; LRCLIB matches on length and Library rejects any answer more than 10 seconds off. (2) The lookup gathers title, artist and album from the file, the song and the album together and takes whichever comes first, so the album's name can be sent as the song title. (3) Find lyrics queues the lookup and the panel re-reads before it has run. (4) The fallback search only accepts a result whose title is identical, so "Yesterday (Remastered 2009)" never matches "Yesterday". A miss is logged only at the most detailed level, which is off by default. Which fault hit the local check is not proven; all four are fixed.
- **Player hidden (#887).** The album page opens as a window over the page behind it, and the browser switches off everything outside that window, including the player bar under the dimmed backdrop.
- **Bar height and Now playing (#887).** Both bars share one design with a 56 px cover slot, but the music bar carries extra padding. The issue does not say what is off on Now playing.
- **Columns (#893).** The handles exist and widths are stored on the profile, but each mouse movement goes to the server and back, so the drag often never takes hold. Double-click reset was never built.
- **Multi-select (#894).** Song lists use the shared selection bar, which offers Add to collection and no Add to playlist. Single-song drag to a rail playlist exists but carries no ticked songs and has no clear highlight.
- **Favorites (#895).** The rail row links to the For Me favorites view. A Favorites playlist view exists inside Listen but nothing links to it.

## 2. Scope

**In scope**
- The lyrics lookup and the Lyrics panel states.
- The player bar's layering over the details window, for every media type.
- Bar height for music and audiobooks.
- The desktop and phone Now playing views against the mock.
- Song table column resizing.
- The song selection bar on desktop and phone, and dragging songs to playlists.
- Favorites in Listen, including hand reordering.

**Out of scope**
- Music bookmarks (Global bookmarks work).
- New lyrics sources.
- The queue and chapters panels; TV, car and lock-screen players.
- Whether editing comes to phones (Editor and Match plan).

## 3. Decisions made

- **D1. Lyrics lookups use the song's own details and its measured length.** Title and artist come from the song, the album name from the album, and the length from the file measurement first. Reason: Reliability.
- **D2. Title matching is forgiving, by rules kept in the lyrics source's config file.** Ignore capitals, punctuation and bracketed extras such as "(Remastered 2009)" or "(feat. …)". Length still guards against a wrong song. Reason: Reliability; rules live in config, not code.
- **D3. The Lyrics panel waits.** It shows "Looking for lyrics…" until the lookup has finished, then lyrics or "No lyrics found". It never says none while a lookup is still running. Reason: ease of use.
- **D4. Every lyrics lookup writes one plain line to the Engine log** with the outcome and the reason (found, no match, length too different, source unreachable). Reason: Maintenance.
- **D5. The player bar is always in front and usable**, including while a details window is open. The details window ends above the bar instead of covering it. Applies to every media type's details window. Reason: ease of use.
- **D6. One bar height.** The music and audiobook bar uses the video bar's measurements: 28 px timeline plus a 60 px row, 56 px cover slot, no extra padding beyond the phone safe area. Amends unified player v2 D5. Reason: uniformity.
- **D7. Now playing matches the mock 1:1** (layout, sizes, order, labels, spacing) at 1024, 1280, 1440 and phone. Restates unified player v2 D24 and D26; no design change.
- **D8. Column resize is handled in the browser**, with a minimum width per column, double-click on the edge to reset that column, and one save to the profile when the drag ends. Widths stay per profile and per place (Songs, Albums, Album page, Playlist). Amends music table D4 by adding double-click reset.
- **D9. Songs go into playlists, not collections.** The selection bar for songs offers Play, Add to queue, Add to playlist (pick one or New playlist) and Edit (desktop). No Add to collection for songs anywhere, including the single song "..." menu. Amends Collections and lists 4a.
- **D10. Drag to a playlist.** Dragging a ticked song carries all ticked songs; dragging an unticked song carries that one. Drop targets: hand-made playlists in the rail, playlist tiles on the Playlists page, and "New playlist". The target shows a clear highlight; a rule-based playlist does not accept drops. Songs already in the playlist are skipped and the in-place notice says how many were added. Keyboard path is Add to playlist. Mouse only.
- **D11. Favorites is a music playlist in Listen.** The rail row opens it inside Listen with the playlist song table. It lists hearted songs only. Removing a song from it removes the heart. My List stays separate.
- **D12. Ticking several songs on a phone** (Shy, 2026-11-12: the phone matches the desktop wherever it can). The song "..." sheet gains Select. Ticked songs show a bottom bar with Play, Add to queue and Add to playlist. Dragging stays mouse-only; Add to playlist is the phone's way to do the same thing.
- **D13. Columns on a phone.** A phone shows a plain list with no columns, so there is nothing to resize. No change.
- **D14. Favorites can be reordered by hand** (Shy, Q1: B). Favorites opens newest hearted first. Any column can sort it, and in its own order songs can be dragged up and down on a desktop, or moved with Move up and Move down in the "..." menu and the phone "..." sheet, as in a hand-made playlist. A newly hearted song goes to the top. The order is saved on the profile.
- **D15. Albums and audiobooks when ticked** (Shy, Q2: A). Ticked albums offer Add to playlist (adds all their songs) and keep Add to collection. Ticked audiobooks keep Add to collection only.
- **D16. Now playing is built to the mock and judged in the Local check** (Shy, Q3: A).

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Lyrics loading | "Looking for lyrics…" |
| Lyrics found | The lyrics with the Synced or Unsynced chip |
| No lyrics | "No lyrics found" with Find lyrics and Add a lyrics file |
| No lyrics source on | The existing "no lyrics source is switched on" line |
| Lyrics source unreachable | "Lyrics could not be looked up right now" with Try again |
| Details window open while playing, desktop and tablet | The window stops above the bar; the bar and its panels work and open in front |
| Details sheet open while playing, phone | The 56 px bar stays visible under the sheet's lower edge and is tappable |
| Resizing a column | The column follows the pointer live; desktop and tablet with a mouse only |
| Songs ticked, desktop | Bar with Play, Add to queue, Add to playlist, Edit |
| Songs ticked, phone | Bottom bar with Play, Add to queue, Add to playlist |
| Dragging songs | The playlist under the pointer is highlighted; rule-based playlists are not |
| Add failed | The in-place notice; no toasts |
| Favorites empty | "Songs you heart appear here." |
| TV | No change |

## 5. Data / model changes

- Each hearted song gains a saved position in the profile's saved items (D14): one new numbered data store step and an update to the public data structure page. Pre-release: no conversion of old data; existing hearts order newest first.
- The Engine returns that position and gains an action to move a song within Favorites; the wire types live in Contracts with their fixtures.
- Lyrics title-matching rules are added to `config/providers/lrclib.json`.

## 6. Work packets

### WP1 — Lyrics lookup uses the right facts
- **What it does:** song title and artist from the song, album from the album, length from the file measurement first (D1).
- **Files likely touched:** `src/MediaEngine.Providers/Workers/TextTrackEnrichmentWorker.cs`, tests in `tests/MediaEngine.Providers.Tests`.
- **Depends on:** none. **Size:** Standard.
- **Done when:** a test with a song, its album and an outside length proves the lookup carries the song's title and the measured length.

### WP2 — Forgiving match and a log line
- **What it does:** title rules from config (D2), one log line per lookup (D4), a replay test with recorded LRCLIB answers.
- **Files likely touched:** `TextTrackProviderAdapter.Lyrics.cs`, `ProviderTextTrackConfiguration.cs`, `ProviderDeclarativeValidator.TextTrack.cs`, `config/providers/lrclib.json`, tests.
- **Depends on:** WP1. **Size:** Standard.
- **Done when:** the replay test finds lyrics for a remastered title and rejects a different song of the same name; the log shows the reason for a miss.

### WP3 — Lyrics panel waits for the answer
- **What it does:** D3.
- **Files likely touched:** `PlaybackLyricsPresenter.cs`, `PlaybackLyrics.razor`, strings; Engine status read to be confirmed.
- **Depends on:** none. **Size:** Standard.
- **Done when:** a test shows "Looking for lyrics…" until the queued lookup ends, then the lyrics or "No lyrics found".

### WP4 — Player bar in front of the details window
- **What it does:** D5. The bar is drawn in the same top layer as the window, with no second audio player.
- **Files likely touched:** `DetailsOverlayHost`, `AppViewer`, `app-dialog.js`, `ListenNowPlayingBar`, layout (to be confirmed).
- **Depends on:** none. **Size:** Complex.
- **Done when:** with an album window open, pressing Play shows the bar, Pause and Next work, Lyrics opens in front, and Esc still closes only the window.

### WP5 — One bar height
- **What it does:** D6.
- **Files likely touched:** `PlaybackShell.razor.css`, `PlaybackIdentity.razor.css`, `ListenNowPlayingBar.razor.css`.
- **Depends on:** WP4. **Size:** Standard.
- **Done when:** bar height and cover size are equal for music, audiobook and video, and a style test guards it.

### WP6 — Now playing matches the mock
- **What it does:** D7, D16.
- **Files likely touched:** `NowPlayingPage`, `PlaybackFullPlayer`, their styles.
- **Depends on:** WP5. **Size:** Standard.
- **Done when:** a written difference list against the mock's Now playing screens is empty at the four widths.

### WP7 — Column resize that works
- **What it does:** D8.
- **Files likely touched:** `SongTable.razor`, `SongTable.razor.cs`, `wwwroot/js/song-table.js`, `SongTablePreferences`, tests.
- **Depends on:** none. **Size:** Standard.
- **Done when:** dragging a header edge changes the width, the width survives a reload, respects the minimum, and double-click resets; same in Albums, the album page and a playlist.

### WP8 — Add to playlist in the selection bar
- **What it does:** D9, D15.
- **Files likely touched:** `BulkSelectionBar`, `BulkListEntry`, `SongList`, `ListenPage.Playlists.cs`, `MediaMembershipDialog`, the song "..." menu.
- **Depends on:** none. **Size:** Standard.
- **Done when:** ticking songs shows Add to playlist and no Add to collection; choosing a playlist adds them and the notice gives the count.

### WP9 — Drag songs onto a playlist
- **What it does:** D10.
- **Files likely touched:** `SongTable`, `song-table.js`, `ListenBrowsePage`, the `ListenPage.razor` rail, `MediaLanePage` navigation drop.
- **Depends on:** WP8. **Size:** Standard.
- **Done when:** three ticked songs dragged onto a rail playlist and onto a Playlists tile show the highlight and are added once.

### WP10 — Favorites as a playlist
- **What it does:** D11, D14, with the data change in section 5.
- **Files likely touched:** `ListenPage.Routing.cs`, `ListenPage.Playlists.cs`, `ListenPage.PlaylistTracks.cs`, the saved-items repository and endpoints, Contracts, a new upgrade step.
- **Depends on:** none. **Size:** Complex.
- **Done when:** the rail row opens Favorites inside Listen with the playlist table, reordering survives a reload, removing a song removes its heart.

### WP11 — Docs, batch rows and issue notes
- **What it does:** public docs pages for Listen and the player, presentation rules, `engineering/validation/batch.md` rows carrying the issue numbers, closure notes on the five issues.
- **Depends on:** all. **Size:** Mechanical.

### WP12 — Select several songs on a phone
- **What it does:** D12.
- **Files likely touched:** the `SongTable` phone list, `SongsPhoneHeader`, `SongList`, strings.
- **Depends on:** WP8. **Size:** Standard.
- **Done when:** at 390 px wide a test ticks three songs, adds them to a playlist and sees the count notice; every target is at least 44 px.

**Waves** (two packets at a time): 1) WP1 + WP7; 2) WP2 + WP4; 3) WP3 + WP5; 4) WP6 + WP8; 5) WP9 + WP10, then WP12; 6) WP11.

## 7. Local checks

- **L1.** On real music: open Lyrics on ten songs across three albums, including one remastered title. Most show lyrics; a miss says "No lyrics found"; the Engine log has one line per lookup with the reason.
- **L2.** Open an album, press Play: the bar appears in front and its buttons work with the album window still open. Repeat from a movie page and an audiobook page.
- **L3.** Play a song, then a video: the two bars are the same height with the same cover size. Mock | Live at 1024, 1280, 1440 and phone.
- **L4.** Now playing against the mock at the four widths.
- **L5.** Resize a column in Songs, Albums, an album page and a playlist; reload; sign in on a second device and see the same widths; double-click resets.
- **L6.** Tick five songs: Add to playlist is offered and Add to collection is not. Drag them onto a rail playlist and onto a Playlists tile. Repeat the ticking and Add to playlist on a phone.
- **L7.** Favorites opens in Listen as a playlist; reorder it, reload and see the order kept; unheart from it; My List is unchanged.

## 8. Risks and open questions

- **The lyrics cause is likely, not proven.** Four real faults are fixed and tested, but the one that hit the local check is confirmed only by L1. If lyrics still fail, the new log line names the reason.
- **WP4 touches the shared details window.** It changes layering for every media type, so L2 checks movies and audiobooks too.
- **Other builds are running in the player and shell files** (Video player, My List, Look v3.1). This build rebases before touching shared player or shell files.
- No open questions.

## Plain English summary

Lyrics fail because of how Library asks the lyrics source, not because of the source; that is fixed and logged. The player bar was hidden because the album window switches off everything behind it; the bar will sit in front and work. The music bar gets the video bar's exact size, Now playing is brought back to the mock, columns resize properly, ticked songs go to playlists by menu or by dragging on a desktop and by menu on a phone, and Favorites opens as a playlist inside Listen that can be put in any order.

## Build notes (2026-11-12)

What was built differs from the text above in these places. None changes a decision Shy made; each is listed so the Local check knows what to expect.

- **Bar height (D6).** The video player follow-ups that merged during this build made the bar's row 72 px and said the same sizes apply to music and audiobooks. The music and audiobook bar follows that: 28 px timeline plus a 72 px row (100 px), a 64 px cover slot, on desktop and tablet. D6's goal, one bar size for music, audiobooks and video, is met; the 88 px and 56 px numbers in D6 are out of date.
- **Phones (D6).** The phone keeps its compact mini-player (the video phone bar is a different, taller layout). Equal heights apply to desktop and tablet.
- **Now playing (D7).** Type sizes and weights come from the Look v3.1 type jobs; spacing and line heights follow the mock. The phone transport uses the video follow-ups sizes (48 px buttons, 56 px Play). The mock's "Following playback" line under lyrics and its 10 and 30 second skips for music are not built (the first is not an existing feature, the second contradicts unified player v2 D3).
- **Favorites (section 5).** No new data store step was needed: `profile_saved_items.position` already existed and now holds the hand-made order. Favorites reorders at once (no Reorder mode with Save order, which main added for hand-made playlists during this build).
- **Albums (D15).** When the spec was built no list could tick albums. A follow-up (Shy, 2026-11-12) added ticking to the Albums cover grid in Listen: **Select** above the grid (on a phone, **Select** in an album's "..." sheet), then a bar with Add to playlist (all the songs you own on those albums, in album order) and Add to collection. Up to 20 albums at a time; an album whose songs cannot be read is counted in the notice. No Play, Add to queue or Edit for ticked albums, and the Albums table layout stays as it was.
- **Columns (D8).** The Song column can grow to 1200 px. A resized column keeps its width and a filler takes the spare room.
- **Lyrics (D2 to D4).** The match rules gained an "ignore accents" switch. An unreachable lyrics source is retried three times in total (15 minutes, then 2 hours), then left until Find lyrics is pressed or a rescan runs. Three Engine actions were added: two reads of a lookup's status and the Favorites move.
- **Details window (D5).** The window is drawn through an outlet in the layout so it never sits inside the page's clipped box. The video restore pill stays dimmed behind an open window, as before.
- **Found, not fixed (outside this spec).** Two older style faults in shared player parts: the Play button's own style rules in the bar never reach the button, and the slider rail height rule is dropped by Chrome and Safari. Both are filed as a GitHub issue for the players thread.
