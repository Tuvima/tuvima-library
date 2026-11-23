# Tuvima for Roku

This is a native SceneGraph/BrightScript client. Sideload the `clients/roku`
directory as a development application, or package it through the Roku
Developer Dashboard.

The client implements server entry, device pairing, browse rows, search-ready
API plumbing, details metadata, HLS/direct playback, captions supplied by the
manifest, heartbeats, and resume. Roku does not provide a Keychain-equivalent;
the refresh token is stored in the device registry with the minimum consumer
scopes and remains protected by server-side rotation, replay revocation, and
device revocation.

Public publication remains blocked until Roku confirms that Tuvima's
browser-approved device flow satisfies the current on-device authentication
criterion. Store artwork and localization assets must also be supplied before
certification packaging.

## Home rows, photos, and profiles

Home shows Continue first, then the home shelves, then a Collections row and a
Photos row. Select a collection to browse its items (Back returns to Home).
A Collections entry that is a Universe (its `group_type` is `universe` and it
carries a Wikidata QID in `universe_qid`, or a QID-shaped `system_key`) opens
that Universe's Series list (`GET /api/v1/universe/{qid}`): one tile per Series
in the Engine's order, showing the item count, or "Also in this world" with its
count when the Series has no numbered run of its own (never "0 items"). Select a
Series to see its items; Back returns to the Series list, and Back from the list
returns to Home. Roku shows Series only: no cast, adaptations or totals. A
Universe the profile cannot see shows the usual error text.

Up and down move between rows and the top buttons; left and right move inside a
row. Photos open in a full-screen viewer: left and right step through the row,
Back closes it. The preview JPEG is fetched by the app with the bearer token
and saved to `tmp:/`, because a Roku `Poster` cannot send headers. A `403` on a
photo call shows that photo access was not allowed; re-pair and tick photos and
videos. Photo videos are not played, and Roku does not download for offline use.

The Profiles button lists the people this paired app may switch to
(`GET /api/v1/devices/current/profiles`) and switches with
`POST /api/v1/devices/current/switch-profile`. A `428` opens a PIN dialog, `403`,
`409` and `429` show a plain message. The app requests the
`view.personal.read` scope at pairing; the person must still tick the photos
and videos option.

## Player

The player follows the shared contract in
[`clients/contracts/v1/player-controls.md`](../contracts/v1/player-controls.md)
(transport order and names, skip defaults of 10 seconds back and 30 forward, tool
order, panel names, paused details, end card and the television key map) and the
approved layout
[`engineering/design/mockups/tv-player/tv-player-panels.html`](../../engineering/design/mockups/tv-player/tv-player-panels.html)
(spec `native-apps`, WP6). Video (an episode, a film or a folder video) gets its own
controls; music and audiobooks keep Roku's own playback screen and the Lyrics
panel (see below).

### Player controls

The controls are drawn by `PlayerOverlay` (with `TransportButton` and `ToolPill`)
from a model made in `components/PlayerLogic.brs`; `components/PlayerControls.brs`
keeps the state of the playing video and reads the remote. `PlayerLogic.brs` has no
screen, no `m` and no network, like `FindAnotherLogic.brs`.
`tests/build/roku-player.test.mjs` and `tests/native/Test-RokuClient.ps1` check the
words, the order, the keys and the package.

- **Overlay.** The title and the one line ("S1 E1 · The First Wish · 22 min",
  "2019 · 1h 52m") at the bottom left on a local gradient, the timeline with the time
  played and the time left, the five transport buttons (Previous, Skip back, Play,
  Skip forward, Next; Play is the largest and focused first; Previous and Next are
  dimmed in place, and always dimmed for a film because a Roku film has no queue),
  then the tool row: Episodes (only for a show that lists its episodes), Audio &
  subtitles, Bookmarks (only when the Engine gave the item's work), Playback
  settings. The overlay appears on any key and hides after 5 seconds, never while a
  panel, the Find window or the End card is open, nor while paused. While paused, an
  episode also shows its description.
- **Keys.** Left and Right on the timeline, and Rewind and Fast-forward anywhere,
  skip by the profile's seconds (10 and 30 by default). Up and Down move between the
  timeline, the transport row and the tool row; Select presses; Play is play/pause.
  Back closes the open panel first (a nested view goes to its root first), then
  hides the overlay, then leaves the video. Options (`*`) still opens Audio &
  subtitles.
- **Panels** (`SidePanel`, 700 of the 1920 pixels at the right edge, full height,
  fixed header with Close, a scrolling middle, a fixed footer; the controls stay to
  its left and the tool that opened it stays highlighted; Right moves from the tool
  row into the panel and Left returns):
  - **Episodes** (`GET /api/v1/details/tvShow/{showId}`): Season row (Left and Right
    change the Season), the count, a row per episode with "Playing · 2:23 of 22:41",
    "Paused · 4:10 of 22:00" or its length and synopsis, the "Up next" chip, and the
    Autoplay next episode switch. Previous and Next use the same list.
  - **Audio & subtitles**: the existing rows, now in the shared panel with a check
    on the row in use, and the footer row Find subtitles… (see below).
  - **Bookmarks** (`/api/v1/player/audiobooks/{workId}/bookmarks`, the Engine's route
    for videos too): **Mine | Global** from the Engine's `isYours` and `inEveryone`
    (the choice shows only when a Global set exists; Mine first), "Bookmark at
    {time}" (saves at once, at the time it was pressed, with no note), a row per
    bookmark (Select jumps to it), Show on timeline (ticks on the timeline), and the
    two empty states. The Options key on a row of Mine opens **Edit** (rename with
    Roku's keyboard dialog) and **Delete** (asks once: Delete or Keep). There is no
    note field and no search box: a remote cannot type.
  - **Playback settings**: Skip back (5, 10, 15, 30), Skip forward (10, 30, 60, 90),
    Playback information, and the footer Reset playback preferences.
- **End card** (an episode with a next episode, in its last 10 seconds): "Up next",
  "Starts in {0} seconds" (autoplay on; with autoplay off the card stays and has no
  countdown), the next episode's title with "Next episode", Cancel and Play now
  (focused). Back cancels. When the episode ends with autoplay on, the next episode
  starts.
- **What stays on this Roku.** The profile's playback preferences are read-only for
  a paired app, so the profile's skip seconds and Autoplay next episode are read from
  `GET /api/v1/devices/current/playback-preferences`, and a choice made in Playback
  settings or on the Autoplay switch is kept in the Roku's registry
  (`skip_back`, `skip_forward`, `autoplay`) and wins on this device. Reset playback
  preferences deletes those three keys.
- **Images.** The round buttons and icons are plain white shapes in `images/`, made
  by `node tools/Make-RokuPlayerIcons.mjs` (no packages) and tinted by the player.

### Player gaps (recorded, not built)

- **Playback speed** is not a row of Playback settings: Roku's player cannot change
  speed (the app pairs with `supports_playback_speed: false`), and a tool the app
  does not support is absent. Picture in picture, Volume, Fullscreen and Close are
  absent by the contract's television rules.
- **Subtitle appearance** (the footer row of Audio & subtitles) is not built: Roku
  draws captions with the system's own Settings > Accessibility > Captions.
- **Audio rows** show the language only; Roku does not give the track's facts
  ("Dolby Atmos · 7.1"), and commentary and audio-description tracks are not moved to
  the end.
- **Lessons and Homework**: a folder video gets a film's controls and the line "title
  · length". There is no Lessons panel, no "Next lesson" end card and no Homework
  block, because the Engine has no route for them yet.
- **Stills and frames**: episode stills, bookmark frames and the End card's still are
  not drawn. A Roku `Poster` cannot send the bearer header, so each picture would
  have to be downloaded to `tmp:/` first, as photos are. The rating card at the top
  left is not shown either (the item answers carry no rating).
- **Bookmarks**: the label "Bookmark at {time}" shows the time when the panel last
  drew (opening it, or after a save); the bookmark itself is saved at the time of the
  press. Edit has no note field.
- **Music and audiobooks** have no full-screen Now playing (no cover, title line,
  timeline or Queue panel, no Chapters or Sleep timer, no Bookmarks): a song plays in
  Roku's own playback screen and only gets the Lyrics panel on Options (see below).
  That is a gap for a later packet.
- **Not checked on a device** (no Roku is available to the build; Local checks):
  focus movement between the overlay and the panel, the white focus rings and the
  tinted icons, text sizes from the sofa, the `KeyboardDialog` rename, the `Dialog`
  menus, `roUrlTransfer` PUT and DELETE for renaming and deleting a bookmark, and the
  Rewind and Fast-forward keys reaching the scene while the video node has no focus.

### Audio & subtitles and Find another

While a video plays, the Audio & subtitles tool (or the remote's Options (`*`) key)
opens the Audio & subtitles panel on the right (`AudioSubtitlesPanel`, which fills the
shared `SidePanel`): Audio rows when the video has more
than one track, then Subtitles (Off, then each subtitle track the Engine has),
then the footer row **Find subtitles…**. The footer row is shown only when
`GET /api/v1/stream/{asset}/text-tracks/sources?kind=subtitles` answers `Ready`.
A subtitle track is fetched with the bearer token, saved to `tmp:/`, and the same
stream is started again at the same position with it attached.

**Find subtitles…** opens the full-screen Find another window
(`FindAnotherWindow`, spec `find-another-on-tv`): the list on the left, the first
lines of the focused result on the right, **Use this** and **Cancel** at the
foot and one **Language** button (the profile's `subtitleLanguage`, with a short
list of languages to switch to; no typing). The best match is read at once; any
other row is read only after focus has rested on it for one second (a `Timer`
restarted on every focus change), once per window. Select on a row opens its
words in full and moves to **Use this**; Back closes the language list, then the
full words, then the window. **Use this** posts `alternatives/choose`, reloads the
text tracks and switches the new one on.

All wording, the dwell rule, the read-once cache and the key map are in
`components/FindAnotherLogic.brs`, which has no screen, `m` or network calls.
`tests/build/roku-find-another.test.mjs` checks the component structure in CI.

Not yet checked on a device (no Roku is available to the build): the Options key
reaching the scene while the video node has focus, a `tmp:/` WebVTT as an external
subtitle track, and the screen layout. The Engine's preview returns three lines per
result, so "full words" shows three lines today (the window takes up to 20 when the
Engine sends them).

### Lyrics and Find lyrics

Roku has no Now playing screen: a song plays in the same full-screen player as a
video (the manifest's `mediaType` says it is music), so the minimal panel is
reachable from there. For a song, Options opens the **Lyrics** side panel
(`LyricsPanel`, with `LyricsLine` rows) instead of Audio & subtitles: the heading
"Lyrics" with a **Synced** or **Unsynced** chip, the lyric lines, and the footer row
**Find lyrics…**. The lyrics are the asset's preferred lyrics text track
(`GET /api/v1/stream/{asset}/text-tracks`, then the track as text, fetched by
`ApiTask` operation `lyricsText`). A file with time tags (`[mm:ss.xx]`, parsed in
`faParseLyrics` the way the Dashboard's `LrcParser` does) is Synced: the current
line has a purple edge and bright words and the list follows the song, driven by
the video node's `position`. A file without them is Unsynced plain words. The
song keeps playing under the panel. The footer row is shown only when
`GET /api/v1/stream/{asset}/text-tracks/sources?kind=lyrics` answers `Ready`.

**Find lyrics…** opens the same Find another window with `kind` set to lyrics:
the title "Find lyrics", six lyric lines in the pane (no times), the checks
"Synced" or "Words only, not timed" and "Fits the song" or "N seconds longer or
shorter", and no Language button (the language is the song's, so no language
parameter is sent). The keys, the one-second dwell rule and the Back order are
the subtitles ones. **Use this** posts `alternatives/choose` with kind lyrics,
returns to the panel (which says "Updating lyrics…") and reads the lyrics again.
The song is paused behind the window and resumes when it closes.

Not yet checked on a device: the `MarkupList` keeping the current line in the
middle (`vertFocusAnimationStyle="fixedFocus"` with `focusRow`) while the list is
not focused, and the panel layout. Not built: a cover, track title or timeline on
a Now playing screen, and any other panel for music (Queue, Bookmarks).
