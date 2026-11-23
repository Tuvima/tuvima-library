# Tuvima Android clients

This Gradle workspace contains the shared API/authentication layer, the Android
TV pilot, the phone/tablet application, and the Android Auto media service.

Requirements: JDK 17, Android SDK 37, Gradle 9.3.1, and an Android device or
emulator. CI installs the pinned toolchain. Locally, point `local.properties`
(gitignored) at the SDK with `sdk.dir=...`; there is no Gradle wrapper, so use an
installed Gradle 9.3.1.

Build from this directory with `gradle :tv:assembleDebug :mobile:assembleDebug`.
Never place signing keys, service credentials, bearer tokens, or signed media
URLs in this repository.

Versions and releases (spec native-apps WP12): `version.properties` is the one place the version is set, for the phone
app and the TV app. `versionName` is `major.minor.patch`; `versionCode` is worked out as
`major * 1000000 + minor * 10000 + patch * 100 + versionBuild`, so a higher version always has a higher code (raise
`versionBuild`, 0 to 99, only to upload the same `versionName` again). `tools/Android-Version.mjs` prints the numbers and the
release tag. Release signing reads four environment variables, `TUVIMA_ANDROID_KEYSTORE_FILE`,
`TUVIMA_ANDROID_KEYSTORE_PASSWORD`, `TUVIMA_ANDROID_KEY_ALIAS` and `TUVIMA_ANDROID_KEY_PASSWORD`; without all four,
`assembleRelease` makes an unsigned APK, which is what pull requests build. Pushing a tag `android-v<versionName>`
(or `android-v<versionName>-build<n>`) runs `.github/workflows/android-release.yml`, which signs with the repository's
`ANDROID_*` secrets and attaches the phone and TV APKs to a GitHub Release; it never uploads to Google Play. The by-hand
steps (key, secrets, Play Console) are in `engineering/validation/native-android-play-submission-2026-11.md`.

Orientation: the phone app is locked to portrait (`screenOrientation="portrait"` on
`MainActivity`); TV is landscape by nature. Movies and episodes rotate to landscape, Photos clips follow
the phone, and everything else stays portrait. The rule lives in `core` `OrientationPolicy`.

Phone shell and My List (spec native-apps WP9a): the phone is laid out as the Dashboard is on a phone, not with a five-tab bar. The top bar has the menu button, the logo, then Downloads, My List and the avatar (Downloads moved up from the old tab; Settings and Switch profile moved into the menu). Under it are the section chips Read, Watch, Listen, View, Collections (Watch, Listen, Read for a Kids profile), and a floating dock of three tabs, Home, Search and My List, floats at the bottom (dark only, 44 dp targets, bare icon buttons, no toasts). The menu lists Home, My List, Recently added (not for Kids), the five lanes, then below a rule Settings (not for Kids), Switch profile (only when the household has more than one profile, Kids included, as on the Dashboard) and Sign out. A Kids profile never sees the full Settings page: its menu has no Settings row, and Switch profile and Sign out open a page holding only the profile and Sign out. The shell fails closed to the Kids shell until the profile list says the active profile is an adult (`PhoneShellLogic.treatAsKids`). The avatar opens Switch profile when there is more than one profile, otherwise Settings. The Dashboard's Engine-activity icon in the top bar is for administrators only and the apps have no `/api/v1` read for it, so it is not drawn. The lists, order, labels and Kids rules are pure functions in `core` `PhoneShellLogic`, with the words the Dashboard uses.

My List is the profile's saved titles (`GET /api/v1/profile-state/saved`; `PUT` and `DELETE /saved/{kind}/{id}`; `POST /saved/remove` and `/saved/restore` for Undo; `GET /saved/finished`), read in `core` `SavedItems.kt`: the kind travels as the Engine's `ProfileEntityKind` number. A plus (not saved) or tick (saved) sits in the top corner of every poster for a movie, show, book, comic, audiobook or album, and on Details; a press shows at once and is put back if the Engine refuses. An episode is saved as its show; a Series, curated collection or playlist card gets no plus (the phone cannot tell a Series from a saved collection), but a group already saved on the Dashboard shows a tick and can be removed. A heart is for songs only, on the Songs and Favorites lists in Listen (see below); a song card never gets a plus. The My List page reads the Engine's `personal/my-list` grid (`GET /api/v1/display/grids/personal/my-list`): Continue first, kind chips with counts (only when two or more kinds), a sort (Date added, Title, Release year), the count line, Remove finished in the "..." menu, and an Undo line for about ten seconds after any removal (Undo restores exactly what the Engine took, with the original dates). Home gets a My List row after Continue. Select (many at once) and the Home hero card are described below. Also left out for now: the profile links and Downloads card at the top of the Dashboard page, and remembering the chosen sort on the Engine. A press on a plus or tick is sent per title in order, so a quick second press never races the first, and the page reads again after any change. The Engine's own `my-list` Home shelf is dropped so Home draws only the app's My List row (20 titles). Details has a "..." menu with My List and Download (and, for a show, Download next episodes); there is no Edit and no Add to collection in version 1. Recently added (`GET /api/v1/display/recent`) is a page with Watch, Read and Listen filters; personal photos stay in View. The wire shapes are pinned by `tests/fixtures/native-client-v1` (`my-list-saved.json`, `my-list-remove-request.json`, `my-list-restore-request.json`, `display-recent.json` from WP8, and `my-list-grid.json`) and `NativeClientContractFixtureTests`. Details shows what a Download press did in a line of words under the top bar, not a toast.

Listen, View, Search, the Home hero and Select (spec native-apps WP9b): everything here reads Engine routes that already exist under `/api/v1`, so nothing was added on the Engine side. **Listen** has six tabs as chips that wrap rather than scroll: Songs, Albums, Artists, Audiobooks, Playlists and Favorites (`core` `ListenLogic`; Playlists came with WP19, see below, and the Songs page has a Play and Shuffle header that starts the queue, WP10c). Songs is `GET /api/v1/display/browse?lane=listen&mediaType=Music` a page of 48 at a time with a search box and an A-Z, Newest or Artist sort; a press on a row plays that song through the media service. Albums and Artists are `GET /api/v1/display/shelves/albums` and `/artists` (`lane=listen&mediaType=Music&grouping=home`) read by the cursor the Engine gives; an album opens its details page and an artist opens that artist's songs (the Songs request with `creator=`, then kept to that exact artist on the phone, because the Engine's creator filter also matches names that share a comma-separated part, such as "Earth" and "Earth, Wind & Fire"; paging goes by what the Engine handed over, so skipping near-misses never repeats or stops a page). A later page that fails keeps the songs already shown and offers Try again. Favorites is the browse route's `status=favorite` songs put in the person's own order by the saved list (`GET /profile-state/saved`, Song entries, the same join the Dashboard does). The heart saves or removes the song as the Song kind (`PUT`/`DELETE /profile-state/saved/song/{id}`), shows at once and is put back if the Engine refuses. **View** has four tabs, Timeline, Galleries, People and Places: People is `GET /api/v1/view/people` (round picture, name, count; a press opens that person's photos from `/view/people/{key}/items`) and Places is `GET /api/v1/view/places` (picture, name, count; a press opens that place's photos from `/view/places/{key}/items`, WP19). Both lists show the Engine's own words when it has nothing to show, and a refusal because photos were not allowed to this app says so instead of offering Try again. A Kids profile has no View chip, so it never reaches People or Places. **Search** is the dock's Search tab, a full-screen overlay with a Cancel button (`GET /api/v1/display/search`): typing is searched after a short pause once there are two characters, and the results are one column under a heading per kind (the top result first, a title shown once). **Home** opens with the hero card (the featured titles of `GET /display/home`, read from its `spotlights`): a fixed-height card you swipe or step with the dots, with the title, one line of facts, a progress line while started, one full-width button (Play, Resume, or Details for a book, comic or group, since the phone has no reader) and the My List plus. The Engine's "Recently added music" slide is a collage of the newest covers (up to four are drawn) with an "Open music" button; it opens the Listen section and has no My List plus, since it stands for all the new music, not one album. **My List Select** is in the page's "..." menu: a press on a poster ticks it instead of opening it, the top line shows "N selected" with Select all (or Clear), Remove and Cancel, and Remove takes every ticked title off in one request with the same Undo as a single removal; Back or Cancel leaves Select mode. Ticks on titles a reload removed are dropped, and Remove with only titles the phone cannot work a saved key out for says so in a short note and stays in Select mode. A search hit for one of the profile's own playlists opens the playlist page (WP19); the built-in lists under `/listen/music/playlists/system/` are not opened from search. The wire shapes are pinned by `tests/fixtures/native-client-v1` (`display-shelf-page.json`, `display-songs.json`, `display-home.json`, `view-people.json`, `view-places.json`, `display-search.json`) and `NativeClientContractFixtureTests`. The Dashboard's library-activity icon is still not drawn: the apps have no `/api/v1` read for it.
**Playlists and Place photos** (spec `native-apps`, WP19; the Engine routes came with WP18). The phone reads three routes that are read only. `GET /api/v1/collections/playlists` lists the profile's own playlists (a page of 50, pinned first and then the profile's saved order; each has a name, `item_count`, `total_duration_seconds`, `is_pinned`, `cover_mode` and `membership_mode`, and no artwork address); `GET /api/v1/collections/{id}/items` reads one playlist's songs in the saved order (a page of up to 200, `asset_id`, `album`, `duration_seconds` and a `sort_order` that keeps running across pages; `404` for a list that is not the profile's own); `GET /api/v1/view/places/{key}/items` reads one place's photos and videos like People's (the place's `key`, such as `paris@48.9,2.4`, goes in the path URL-encoded). In Listen the **Playlists** chip shows the playlists as tiles with a cover, the name, the song count and length, a Pinned mark and "Smart playlist" for a smart one. The tile's cover is made from the first songs' own covers because the list has no artwork: a mosaic of four different covers when the playlist's `cover_mode` is `mosaic` and it has four, otherwise the first cover; each tile reads its first songs only when it is drawn. A press opens the playlist page: its name, the count (and the length once every song is read), **Play** and **Shuffle**, **Download all** (the playlist's songs go through the same offline batch as an album, so storage and list limits stop it with a line of words), and the songs with cover, title, artist and album, and length. A press on a song plays the playlist from there through the music queue (`PlaylistsLogic.playFrom`: the songs in the saved order with their `asset_id`, up to the queue's 100 around the song, under the playlist's name); Shuffle takes a random 100 of a longer playlist, kept in playlist order so turning Shuffle off goes back to it. Every page of the songs is read in turn (a failed page keeps what is shown and offers Try again), an empty playlist and an empty list say that playlists are made and changed on the Dashboard (nothing is edited here), and a Kids profile gets what the Engine lets it see. Offline the list and page say so; the songs downloaded to the phone are marked Downloaded and are the only ones that play, because the queue is built from the files the phone holds. A search hit for a playlist opens the same page (`PlaylistsLogic.idFromRoute`), and the car's search still leaves playlists out. A smart playlist can list an album as one row, which has no file of its own: the row is shown dimmed with "No file to play here" and is left out of Play, Shuffle, Download all and the car. A playlist so long that the 50-page limit (10,000 songs) stops the reading is shown as partial ("Showing the first ... of ... songs"), with no length and no Download all. A press on a place in View opens its photos in the same grid as a person's (`PlacePhotosScreen`). The wire shapes are pinned by `tests/fixtures/native-client-v1` (`playlists.json`, `playlist-items.json`, `view-place-items.json`) and read in `PlaylistsLogicTest` and `ViewDiscoveryTest`.

Collections and Universes: a Collections entry that carries a `universe_qid` (or, failing that, a QID-shaped `system_key`) is a Universe. On the phone its "See the Universe shelves" button, and on TV selecting its card in the Collections row, open the Universe screen (`GET /api/v1/universe/{qid}`). The screen shows the Universe name, one totals line (for example "3 Series · 14 Works · 2 formats"; singular forms, and a part with no number is left out) and the description. Then each Series or show in the order the Engine gives, with its kind and item count: a row of cards for the numbered run (left out when the run is empty, and an empty run never reads "0 items") and, beneath it, an "Also in this world" row for Universe items that have no numbered place in that Series. Cards open exactly like cards anywhere else. After the shelves come a read-only Cast list (name, then "as Character, Character" when the Engine names characters; the phone also shows a small round headshot when there is one) and a read-only Adaptations list ("The Film — Based on The Novel"). Nothing on those lists is tappable, and there is no editing. A Universe the profile cannot see shows the usual error with Retry. All other Collections entries (shows, series, collections, lists) still open their item list. The wording and row rules are pure functions in `core` (`universeTotalsLine`, `universeShelfRows`, `castCharactersLine`, `adaptationLine`) and the wire reading is `parseUniverseDetail`; every field added with the Universe screen is optional, so an older Engine answer still reads.

Picture-in-Picture, audio focus and keys: pressing Home while a video plays shrinks it to a floating window (`PictureInPicturePolicy`; not while preparing, on an error, paused, or when the next-episode countdown is about to advance). The window shows only the picture with a play/pause button; closing it stops and clears the video exactly like Close, expanding it returns to the full screen, and it is the one visible exception to "no video plays in the background" (a phone without the feature, or the screen turning off, still pauses). The media service player uses media audio attributes with audio focus handling (a call or another app pauses playback and gives it back) and pauses when headphones are unplugged. On the video screen Space and Media Play/Pause toggle, the arrow keys seek by the skip seconds, and Escape or Back closes the controls before the screen. For a screen reader the seek bar says "12 minutes of 45 minutes" and offers back/forward actions, the controls stay while touch exploration is on, the next-episode card is announced once politely, and the system caption style wins over the profile's when the person turned system captions on. The video and clip screens handle resize, fold, font and layout-direction changes without restarting.

Home and outside address: the address typed at pairing is the server's home address and its stable identity (the
saved sign-in is keyed to it). The Dashboard's discovery answer (`GET /.well-known/tuvima`) also carries an optional
`outside_address` when the owner has set one up in Remote Access (a reverse-proxy address or a Tailscale address; nothing to type
twice). The app saves it next to the home address (`PreferencesServerAddressStore`, refreshed whenever discovery runs;
an answer that omits it never makes the app forget it, and only discovery at pairing or app start can set it, never the quick
address checks; so removing the outside address on the server takes effect on the phone after re-pairing). The app uses the home address
first: `TuvimaClient.refreshServerAddress()` asks the home address for its discovery answer with a short timeout
(about 1.5 seconds), then the outside address, and otherwise stays where it is, so the usual "Can't reach your Tuvima server"
with Retry appears. It rechecks on app start (`discover()`), when the phone's default network changes
(`NetworkChangeWatcher`, registered in the phone and TV `Application`; it needs only the network-state permission, no
Wi-Fi name and no location), and once after a request fails with a connection error (a GET is then repeated once on the address
that answers; other requests simply use the new address next time). Each request reads the address in use once when it starts,
so a switch never changes a request in flight, and sign-in, heartbeats, token refresh, downloads and artwork all keep
working after a switch. URLs are never trusted to carry a fixed address: the media service, the Photos clip player and the TV
player rebuild every URL at request time with `TuvimaClient.resolveUrl`, and artwork and download requests go through the client
the same way, so a queued item, a playlist segment or a saved artwork link built on the other address still reaches the server. The
rules live in `core` `ServerAddresses` and `ServerAddressSelector`. Paired from outside (the outside address typed)? Then that
is the only address known, which is fine.

Streaming and the short-lived token: the media service adds the bearer token to every request when the request is made (a fresh one, refreshed by `TuvimaClient` when it has expired), never once at start-up, so seeks, reconnects, the next item and sidecar subtitles keep working after the token's roughly ten minutes. If a streamed item still gets a 401 or 403 (for example the stream grant tied to the old token), the service asks for a fresh playback manifest, swaps the item in at the same position and play state, and tries once more; a second failure shows the normal error with Retry. Downloaded files read from the phone and never do this. Rules live in `core` `StreamRecovery`.

Next episode: when Play starts from a show (its detail page, or a download that knows its show), the video screen asks the Engine for the
next ten episodes once playback is 30 seconds in. At the start of a Credits segment (or the last 15 seconds, and always at the end) a card offers the
next one: with autoplay on an 8 second countdown with Play now and Cancel, otherwise Play next. Episodes opens a list to pick another. Both reuse the
same PLAY_VIDEO path, so resume, heartbeats and on-phone progress are not duplicated; the finished episode is reported as ended first. A movie or
the last episode closes back to the details page. The show id comes from the card's details route (`/details/tvshow/{showId}?...&episode={id}`, with the query string removed, so Home, Continue Watching, Libraries, Collections and Search all open the show correctly), from a download that knows its show, or from an episode detail answer that names its show (`showId` or `episodeContext.showWorkId`); without one (the `tuvima://play/{assetId}` link) these features stay hidden and nothing is guessed. A download keeps only
its first video and audio track (no subtitles), so its Subtitles list says "Not included in this download"; an expired download that cannot stream
says it expired. "Resume where I left off" off starts every video at 0, streamed or downloaded. Rules live in `core` `NextEpisodeLogic`.

Photos videos: tapping a video in the Photos gallery or timeline (or Play in the viewer) opens `ClipPlayerActivity`, which plays
`GET /api/v1/view/items/{id}/playback` (the original, by byte ranges) on its own player. It has no media service, History, progress or
lock-screen session, adds the bearer token to each request rather than the URL, and follows the phone's rotation.

Video: Play on a movie or episode (and on a downloaded one in Downloads) opens `VideoPlayerActivity`, a
full-screen landscape player. It shows the same ExoPlayer the media service owns through a Media3
`MediaController`, so the lock screen, headset buttons, offline progress and the audio notification stay in one
place, and starting a video pauses music. It sends custom session commands (`PLAY_VIDEO`, `CANCEL_VIDEO`) to the
service, which resolves the manifest, waits while the Engine prepares a stream (every 2 seconds, up to 2
minutes, with Cancel), and resumes at the Engine's saved position. The activity handles rotation itself, so
turning the phone never restarts playback; closing it returns to the portrait main screen. Leaving the screen
pauses (no background audio for video). A refused title (403 or 404, such as a Kids limit) reads "This title is
not available for this profile."; a lost connection reads "Can't reach your Tuvima server" with Retry. While a
streamed item (audio or video) plays, the service sends a heartbeat every 15 seconds and on pause, stop and end;
downloaded items keep their on-phone progress path. The choice between the video screen and the audio service is
`PlaybackRouting` in `core`.

Tracks, skips and gestures: the video screen has Audio and Subtitles bottom sheets (Subtitles has Off), a speed sheet
(0.5x to 2x) and, when the Engine sends chapters, a Chapters sheet. The starting audio and subtitles come from the
profile's watching preferences (`GET /api/v1/devices/current/playback-preferences`, read-only; defaults are used
if the route is missing or slow, and playback never waits more than about 2 seconds for it) through
`PlaybackTrackPicker`, applied with Media3 track overrides. Sidecar subtitles (WebVTT or SRT at `deliveryUrl`) ride on
the media item and are fetched with the same bearer token as the video. Skip Intro and Skip Credits appear only while the
position is inside that segment (`segmentToOffer`) and never skip on their own. Skip back and forward use the
preference seconds. Double-tap the left or right half to seek, slide up or down on the right half for volume and on the
left half for brightness, tap once to show or hide the controls. Video never plays in the background: Home pauses it,
closing the screen or reaching the end stops the player and clears the video from the media session, and a leftover
paused video cannot be started from the notification or a headset button while no video screen is open.

Video: the phone app declares MKV and M4V containers and SRT subtitles, and the playback manifest now
carries audio tracks, subtitle tracks, chapters and skip segments (`PlaybackTrackPicker` chooses the
starting tracks from the profile's language preferences).

Downloads and offline playback (spec native-apps WP10a, first-class on the phone): a title can be kept for a movie, an episode, a song or an audiobook, and
for a whole show, season, album, playlist or audiobook (every part) from the Details page's "Download all..." choice. The files come from the Details answer's own
`mediaGroups` for an album, playlist or audiobook and `sequencePlacement` (each owned episode's `episodeContext`) for a show or season, because the Engine sends no
`mediaGroups` for a show (`core` `DetailChildren`; an audiobook lists one row per chapter, so a file counts once), so no new Engine route is used; a book, comic or
Series is not kept (books open in the Dashboard's reader in the phone's browser: an audit decision of 2026-11-23, not a spec decision). One press queues at most 200 files and stops at the
storage limit or the Engine's 500-entry device list (`OfflineBatchLogic`). The Downloads page (top bar) opens from the phone's own list, so it works with no
connection and a paired phone that cannot reach the server opens on it. It shows what downloads use of the storage limit and the room the phone itself has left,
sections for Downloading, On this phone and Expired, per-title progress with Pause, Resume, Retry and Cancel, Pause all, Resume all and Remove all, and (not for a Kids
profile) "Download on Wi-Fi only" and the storage limit. Wi-Fi only changes the WorkManager network rule from connected to unmetered; progress, bookmark and list
reports always go over any connection. A download stops with "Not enough free space on this phone" when less than 500 MB would stay free, and keeps its partial file for
Retry; the check counts what the other moving downloads still have to write, so a batch cannot take the same room twice. A poster (one that names a file of its own: a
movie, episode, audiobook or song card), and Details, of a title that is on the phone shows a small Downloaded mark (a show, season, album or playlist card gets none).
Playing a download needs no connection; where it was left (position) and bookmarks added to a downloaded video or audiobook are kept on the phone and sent, oldest first,
when a connection returns (`OfflineSyncWorker`); the Engine's own bookmark list for the title is kept when its download finishes, so it reads offline
(`OfflineDownloads.bookmarksFor`, `addBookmarkOffline`). The phone video player's Bookmarks panel uses them (WP10b), and so does the audiobook player's (WP10c). Signing out removes every download from the phone and says so; a Kids
profile has only its own downloads and no download settings. What a press did is said in a line of text, never a toast. Rules are pure functions in `core`
(`DetailChildren`, `OfflineBatch`, `OfflineStorage`, `DownloadsPageLogic`, `OfflineBookmarks`, `DownloadBadge`).

Unit tests for the shared client (home and outside address selection, API v1 paths, typed profile-switch results, photo consent, photo grid layout,
and the offline download rules: server-won detection, replay order, the storage cap, expiry and report results,
plus kept photos: the cap with photos, the purge rule, kinds and the gallery plan)
run with `gradle :core:testDebugUnitTest`. They use plain JUnit and cover logic that needs no Android
framework; wire-shape parsing uses the Android JSON classes (the `android-json` test dependency) and the Find another
readers are covered by JVM unit tests against sample answers.

## Player

Android TV, the phone app and Android Auto follow the shared contract in
[`clients/contracts/v1/player-controls.md`](../contracts/v1/player-controls.md).
**Android TV** (spec `native-apps`, WP5) uses the stock player view only for the picture; the controls are the app's own
(`TvPlayerUi.kt`, drawn 1:1 from `engineering/design/mockups/tv-player/tv-player-panels.html`, sizes written in the mock's
1920 by 1080 pixels and scaled to the screen, so the right panel is 700 dp-equivalent at 1080p). Under the title and the
timeline sit the five transport buttons in the contract's order (Previous, Skip back, Play or Pause, Skip forward, Next;
for music Shuffle, Previous track, Play, Next track, Repeat; for an audiobook Previous and Next chapter), with Previous and
Next dimmed in place, and a tool row: **Episodes**, **Audio & subtitles**, **Bookmarks**, **Playback settings** (a film has
no Episodes; a song has Lyrics, an audiobook Chapters). Each tool opens a right panel with the contract's rows and words:
Episodes (Season picker, rows with the "Playing" or "Paused" state, the **Autoplay next episode** switch), Audio & subtitles
(below), Bookmarks (a **Mine | Global** choice that appears only when the Engine marks a bookmark `inEveryone`, "Bookmark at
{0}", Delete with Keep, "Show on timeline"; routes `/api/v1/player/audiobooks/{workId}/bookmarks` and `.../bookmarks/{id}`),
Playback settings (Playback speed, Skip back, Skip forward, Playback information, Reset playback preferences) and, for an
audiobook, Chapters with a "Timeline shows" choice. Paused details (title, line, description, rating card) show over the
picture while it is paused and no panel is open. The end card ("Up next", "Starts in {0} seconds", Cancel, Play now) uses
`NextEpisodeLogic`. The playback preferences are read-only for apps, so Autoplay, skip seconds, speed, subtitle appearance
and the timeline choice are kept on the television (`TvDeviceStore`) and never sent to the Engine. Keys (`TvKeyMap`): the
arrows move focus, Left and Right on the timeline skip by the profile's seconds, Rewind and Fast-forward skip back and
forward, Back closes the open panel (one level of a nested view) first, then hides the controls, then leaves; the controls
hide after 5 seconds and never while a panel, the Find window, the end card or a pause is showing. Music and audiobooks use
the same screen as a full-screen Now playing with Lyrics or Chapters as the side panel. The television app has no Cast
button yet; when it gets one it must be hidden on Fire TV, and the rule is ready (`TvDevice.isFireTv`, with `isFireTv(context)`
in `tv`: the maker is Amazon or the system has `amazon.hardware.fire_tv`).
The **Audio & subtitles** side panel (spec "Find another on TV", WP3) is the tool the remote's captions or menu key opens
directly. The panel lists Audio
rows when there are several tracks, then Subtitles: Off, then each track, and a footer row **Find subtitles…** that is
shown only when the sources check (`GET /api/v1/stream/{asset}/text-tracks/sources?kind=subtitles`) says `Ready`. Select
on the footer opens the **Find another** window: full screen over the paused, dimmed video; a header (the item's name,
"Find subtitles" and one Language button); the list on the left (release name, "Hearing impaired", "Made for your file"
or "A different release, may be out of step", once read "Fits the film" or "Ends N minutes short of the film" or "past the
film", and the "Best match" and "In use" chips); the focused row's first lines and their times on the right; Use this and
Cancel at the foot. Keys: Up and Down move through the list (the pane follows), Right goes list, pane, Use this, Left goes
back, Select on a row opens its words in full and focuses Use this, Select on Use this applies the result and resumes at
the same position with the new subtitles, Select on Language opens a short list of languages, and Back closes the
language list, then the full words, then the window. The best match is read as soon as the list opens; any other row is
read only after focus has rested on it for one second, a row is read once per window and a failed read is not retried.
Nothing can be typed. The player starts with the Engine's subtitle files attached (the profile's language preferences
pick the one shown); the entry is hidden when no source is on or the profile may not use it.
**Lyrics on Android TV** (spec WP5, D6). A song plays through the same player screen, and the Engine's playback manifest
names it as music, so for music the tool reads **Lyrics** (it replaces Audio & subtitles, which a song does not need) and
opens a **Lyrics** side panel: the heading with a **Synced** or **Unsynced** chip, the lyric lines (the current synced line
is bright and larger and the list follows the song's position every quarter second) and a footer row **Find lyrics…**,
hidden unless `GET /api/v1/stream/{asset}/text-tracks/sources?kind=lyrics` says `Ready`. The panel reads the song's
preferred lyrics track (`GET .../text-tracks`, then the file as text); a file with times is synced (word stamps and tag
lines such as `[ar:...]` are dropped, a line with several time tags appears at each), a file without is plain words. Select
on the footer opens the same Find another window with kind lyrics: title "Find lyrics", six lyric lines in the pane with no
times, the checks "Synced" or "Words only, not timed" and "Fits the song" or "N seconds longer or shorter", no Language
button and no language sent. Use this closes the window, the song carries on from the same place and the panel reads the
lyrics again ("Updating lyrics…"). Back or Left closes the panel. The reading, key and screen-free rules are in
`LyricsFollow` (`parse`, `currentIndex`, `pickTrack`, `isMusic`, `panelKey`) with `LyricsFollowTest`; the Lyrics body is
drawn with the other side panels in `TvPlayerUi.kt`.
The player's row builders, key effects, text lines and the Fire TV rule are plain Kotlin in `core` with JVM tests:
`TvPlayerLogic` (transport, tools, words, end card, key map), `TvPlayerPanels` (`TvPanels` builders and the panel key
reducer), `TvPlayerData` (episodes, bookmarks, chapters, subtitle appearance, playback information) and
`TvPlayerContractTest`, which reads the contract's tables and fails if a word or key drifts from them.
All of the logic is in `core` (pure Kotlin, no screen types, JVM unit-tested) so the phone's sheet reuses it:
`FindAnotherModel` (one window's state: search, reading, focus, apply), `FindAnotherKeys` (the key map), `FindAnotherDwell`
(the reading rule, with an injected scheduler), `FindAnotherWording` and `FindAnotherRows` (the Dashboard's sentences and
row data), `FindAnotherLanguages`, the wire models and parsers (`TextTrackAlternative`, `TextTrackAlternatives`,
`TextTrackAlternativePreview`, `TextTrackSources`, `ChooseTextTrackRequest`, `TextTrackChoiceResult`), the four routes in
`ClientRoutes` and the matching `TuvimaClient` calls. Gaps for **Android TV**: this thread's screen has not been compiled or
tried on a television or emulator (the build machine had no Android SDK); there is no Cast button in the television app yet
(V6b-A waits on the product owner's television test), so only the Fire TV rule is in place for when it arrives; bookmarks
cannot be renamed and no note can be typed; Lessons and Homework are not listed (no route returns them); the rating card
shows only when the Engine gives a content rating; the "Timeline shows" choice is kept per television, not per profile; the
mock lists Global before Mine while the contract (and the web player) put Mine first, and the app follows the contract; and
the window's "Updating" wait after Use this is shown inside the window, not on the panel. Android Auto is built in WP11 (below). The television side
panels are a first definition, to be settled in their own
threads (Unified player v2, D29 and section 9).

**Android phone: video player and Edit in browser** (spec `native-apps`, WP10b). The phone's video overlay follows the contract's
Phone section: a top line (close, the show or film title with its one line "S1 E1 · The First Wish · 22 min", and a "..." menu with
**Playback settings** and, where the phone can, **Picture in picture**), the five transport buttons (Previous, Skip back, Play or Pause,
Skip forward, Next; Previous and Next dimmed in place), and a tool row (**Audio & subtitles**, **Episodes**, **Bookmarks**; no Fullscreen,
because the phone's video screen is already full screen). Each tool opens one bottom sheet (`PlayerPanels.kt`) built from the same
`TvPanels` builders as the television, so every word is written once. Skip seconds, Autoplay, speed, subtitle appearance and "Show on
timeline" stay on the phone per profile (`PhoneDeviceStore`, `PhoneDeviceChoices`) because the playback preferences have no write route.
Subtitles start off and then follow what that person last chose (`SubtitleMemory`); a remembered language the next video does not carry
means no subtitles. Bookmarks (Mine | Global, "Bookmark at {0}", Add a note, Edit through `PUT .../bookmarks/{id}`, Delete asks once, search
above eight rows, ticks on the timeline) use the Engine's audiobook-bookmark routes and, for a download with no connection,
`OfflineDownloads.addBookmarkOffline` / `bookmarksFor` / `removeWaitingBookmark`. With no connection the next episode is offered from the
downloads (`DownloadedEpisodes`). The end card uses the contract's words. **Edit in browser:** the "..." menu on a title's page has **Edit**
(not for a Kids profile) that opens the Dashboard in the phone's browser on the item's own page (`EditInBrowser`); the link never opens the
editor, and signing in is the Dashboard's.

**Android phone: music and audiobook players** (spec `native-apps`, WP10c). Music and audiobooks play in the media service
(`TuvimaMediaLibraryService`), shown by a **mini player** above the phone dock (cover, title and artist, Play or Pause, and a thin
progress line; a failure shows one line under it, no toast) and a full **Now playing** screen (`NowPlayingScreen.kt`) opened from it.
Now playing follows the contract's phone rows: a top line ("Playing from {source}" and a "..." menu, with **Playback settings** for an
audiobook), the cover, the title line (with the Favorite heart for a song), a timeline with the elapsed and remaining time (bookmark
ticks on an audiobook), the five transport buttons, and a tool row. Songs get **Queue**, **Lyrics**, Shuffle and Repeat; an audiobook gets
**Chapters**, **Playback speed**, **Sleep timer** and **Bookmarks**. Each tool opens one bottom sheet (`AudioPanels.kt`, `PlayerPanels.kt`),
built from the same `TvPanels` builders as the television. The Queue sheet has Play next, Move up, Move down and Remove on each row;
Shuffle reorders only the songs after the one playing and turning it off puts them back in their original order (`QueueShuffle`).
Lyrics follow the song (line by line when they are synced) and a **Resume following** button returns after a scroll; **Find lyrics**
opens the same Find another sheet. The audiobook Sleep timer (presets, End of chapter, Custom, then +5 minutes and Cancel timer once
running) is counted in the service, so it keeps running with the screen off. Audiobook speed, the skip seconds and the repeat choice
stay on the phone per profile (`PhoneDeviceStore`). Audiobook Bookmarks (Mine | Global, a note field) use the Engine's audiobook-bookmark
routes and, for a download with no connection, the offline functions the video player uses. An audiobook resumes at its saved place.
The lock-screen and notification card are written from `LockScreen.card` (title, artist and album or author, chapter line, cover, and
the contract's actions), and a downloaded song or audiobook plays with no connection. A Play on an album's page, a song in Songs, an
artist's songs or Favorites play the list from that song (`ListenPlay`, `DetailPlay`); the Songs page has a **Play** and **Shuffle**
header.
Like and Dislike on a song's title line use the Engine's existing profile reactions (`PUT/DELETE /api/v1/profile-state/reactions/song/{id}`,
`SongReaction`); no new wire shape.

*Decisions for Shy (contract phone rows not built in WP10c).* (1) **Go to album** and **Go to artist** on a Queue row: the queue holds a
song's album and artist as text only, and the phone has no album or artist page it can open from a name, so they need the Engine to
return an album and artist id with a song. Recommendation: have the Engine return those ids in the song's display card in a later packet.
(2) **Music Bookmarks** in the "..." menu: the Engine's bookmark routes are the audiobook ones and the phone keeps music out of Bookmarks
(`OfflineBookmarkLogic.keepsBookmarks`). Recommendation: leave music without Bookmarks and change the contract line. (3) **Playback
settings** for music in the "..." menu: music has no speed or skip choices today, so the menu would hold nothing. Recommendation: leave the
music menu empty until a music setting exists, and change the contract line. The pure rules (`QueueOrder.kt`, `DetailPlayTarget.kt`, `NowPlayingLogic.kt`) have tests in `core`; the screens and the service
need a device check (see the spec's Local checks).

**Android Auto** (spec `native-apps`, WP11, issue 787). The car draws its own lists and one Now playing card, so the app supplies only the
media service's browse tree, its search and the card's buttons; nothing is added on the Engine's side and nothing is shown that is not audio
(no video, no pictures to browse). The pure rules are in `core` (`CarBrowse.kt`: `CarEntry`, `CarList`, `CarCards`, `CarPlayPlan`,
`CarSearch`, `CarNowPlaying`, `CarListings`) with JVM tests in `CarBrowseTest`; `TuvimaMediaLibraryService` turns them into Media3.
**The list** is the contract's (`car-list` table, compared by a test): **Continue** (`GET /display/continue?lane=listen`), **Listen** (the
Music home's shelves with the Engine's own titles, `GET /display/browse?lane=listen&mediaType=Music&grouping=home`, each shelf a list; Recently played
is the first shelf, the artists shelf is left out, and an album opens its songs from its Details page) and **Audiobooks**
(`browse?lane=listen&mediaType=Audiobook`). **Playlists** is in the contract's list
and is on since WP19 (`CarList.PLAYLISTS_AVAILABLE`): it lists the profile's playlists (`GET /collections/playlists`, audio only, pinned first, at
most 50, an empty one left out), and a playlist opens as a list of its songs (`GET /collections/{id}/items`, at most 50, a song with no file and a
file repeated left out) that plays from the pressed song under the playlist's name. The root keeps Auto's four tabs (Continue, Listen,
Audiobooks, Playlists): Recently played is the first shelf inside Listen (the contract says so) instead of a fifth tab, and the service slices the root by the page size
the car asks for. Each list is audio only (songs, albums, audiobooks), at most
50 rows, and is paged by the size the car asks for. A press on a song plays its list from that song under the list's name; a press on an
audiobook plays the book from where it was left. Lists are read when they are opened, for the signed-in profile only (the Engine decides what
a Kids profile may see, so the app never filters by profile), and a list kept for one profile is dropped when another signs in.
**Voice search** is Media3's `onSearch` and `onGetSearchResult` plus a spoken "play ..." request, all through the universal search route
(`GET /display/search`): audio results only, the best one starts (an album plays its songs, a song its file, a book from its place), and an
empty request ("play music") carries on with the first Continue item. **The Now playing card** carries the lock screen's fields
(`LockScreen.card`) and the contract's buttons: music has Previous, Play and Next with **Shuffle** and **Repeat** as the extra buttons; an
audiobook has back and forward by the profile's skip seconds and, with chapters, Previous and Next chapter, with **Speed** as the extra
button (one button that shows the speed and steps through the contract's presets, because the car's card holds only a few extra buttons).
The manifest already declares the Auto media app (`com.google.android.gms.car.application` and `automotive_app_desc.xml` with `media`) and the
`MediaBrowserService` action on the service; `CarBrowseTest` checks both.

*Decisions for Shy (WP11).* (1) **Playlists** in the car list is on since WP19 (the Engine's read-only playlist routes exist). The contract's
list has five rows and Auto's tab bar shows four, so Playlists stays a top-level tab and Recently played is the first shelf inside Listen
(`CarList.listenShelves`); `clients/contracts/v1/player-controls.md` now says so and its car-list table has four rows. (2) **Speed presets** are one stepping button, not five
buttons, for the reason above; the contract says "Speed presets as the template's extra buttons". (3) The old browse tree had a **Queue**
entry; the contract's list has none, so it is gone, and the source gate `tests/native/Test-NativeClientSources.ps1` now checks the contract's
list instead (the service builds its root from `CarList.rootEntries()`, `CarBrowse.kt` names every contract entry and reads only the Listen lane). (4) **The extra buttons are not limited to the car.** Media3 gives the session one custom layout, which Android Auto and the phone's own
notification and lock screen both read (a layout cannot be given to the car's controller alone), so Shuffle and Repeat for music, or the Speed
button for an audiobook, may also appear beside the lock-screen card's buttons on the phone. The card's own buttons are unchanged.
A fix would need the notification to be drawn by the app itself.

**Android phone: Find another** (spec "Find another on TV", WP8; Find another v1 D12). The video player's **Audio &
subtitles** view (the Subtitles sheet) ends with a **Find subtitles…** row, drawn only when the same sources check as on
Android TV says `Ready` and the phone is not playing a downloaded copy (a download has none of the Engine's subtitle
files to swap). The row opens a **bottom sheet** (`FindAnotherSheet.kt` in `mobile`) with the same content as the
television window in one column: a header (the item's name, "Find subtitles", one Language button that swaps the list for
a short list of languages), then one row per result (release name, "Hearing impaired", "Made for your file" or "A
different release, may be out of step", once read "Fits the film" or the minutes short or long, "Best match" and "In use"
chips, and the words of the row inside the row), with **Cancel** and **Use this** pinned at the bottom (spec L5). Touch:
the best match is read as soon as the sheet opens and is the selected row; a tap on any other row reads it at once (no
one-second wait on touch) and selects it, showing its words in full; Use this applies the selected row, Cancel or a swipe
down closes. Reading goes through the shared `FindAnotherModel` read-once cache (`tapRow`, `useSelected`, `cancel`,
`chooseLanguage`; the touch rules in `FindAnotherSheet` are in `core` and JVM-tested). The video pauses behind the sheet
and goes back as it was. After Use this the player asks the media service to reload the subtitle files at the same
position (`COMMAND_RELOAD_SUBTITLES`: a fresh playback manifest, the same item with its new sidecar subtitles) and then
switches the new file on, leaving the audio choice alone. Dark only, controls 44dp or more, no toasts ("Updating…" shows
in the sheet's footer). The phone app has **no Lyrics view** yet, so no "Find lyrics…" row is wired: the sheet and the
shared model already accept `TextTrackKind.Lyrics` (title "Find lyrics", the lyrics rows and checks), so a lyrics view only
needs to open it with that kind.
