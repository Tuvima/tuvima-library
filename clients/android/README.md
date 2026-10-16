# Tuvima Android clients

This Gradle workspace contains the shared API/authentication layer, the Android
TV pilot, the phone/tablet application, and the Android Auto media service.

Requirements: JDK 17, Android SDK 37, Gradle 9.3.1, and an Android device or
emulator. CI installs the pinned toolchain. This Windows checkout does not
currently have a local Android toolchain.

Build from this directory with `gradle :tv:assembleDebug :mobile:assembleDebug`.
Never place signing keys, service credentials, bearer tokens, or signed media
URLs in this repository.

Orientation: the phone app is locked to portrait (`screenOrientation="portrait"` on
`MainActivity`); TV is landscape by nature. Movies and episodes rotate to landscape, Photos clips follow
the phone, and everything else stays portrait. The rule lives in `core` `OrientationPolicy`.

Picture-in-Picture, audio focus and keys: pressing Home while a video plays shrinks it to a floating window (`PictureInPicturePolicy`; not while preparing, on an error, paused, or when the next-episode countdown is about to advance). The window shows only the picture with a play/pause button; closing it stops and clears the video exactly like Close, expanding it returns to the full screen, and it is the one visible exception to "no video plays in the background" (a phone without the feature, or the screen turning off, still pauses). The media service player uses media audio attributes with audio focus handling (a call or another app pauses playback and gives it back) and pauses when headphones are unplugged. On the video screen Space and Media Play/Pause toggle, the arrow keys seek by the skip seconds, and Escape or Back closes the controls before the screen. For a screen reader the seek bar says "12 minutes of 45 minutes" and offers back/forward actions, the controls stay while touch exploration is on, the next-episode card is announced once politely, and the system caption style wins over the profile's when the person turned system captions on. The video and clip screens handle resize, fold, font and layout-direction changes without restarting.

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

Unit tests for the shared client (API v1 paths, typed profile-switch results, photo consent, photo grid layout,
and the offline download rules: server-won detection, replay order, the storage cap, expiry and report results,
plus kept photos: the cap with photos, the purge rule, kinds and the gallery plan)
run with `gradle :core:testDebugUnitTest`. They use plain JUnit and cover logic that needs no Android
framework; wire-shape parsing needs the Android JSON classes and is not yet covered by JVM unit tests.
