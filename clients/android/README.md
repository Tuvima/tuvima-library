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

Next episode: when Play starts from a show (its detail page, or a download that knows its show), the video screen asks the Engine for the
next ten episodes once playback is 30 seconds in. At the start of a Credits segment (or the last 15 seconds, and always at the end) a card offers the
next one: with autoplay on an 8 second countdown with Play now and Cancel, otherwise Play next. Episodes opens a list to pick another. Both reuse the
same PLAY_VIDEO path, so resume, heartbeats and on-phone progress are not duplicated; the finished episode is reported as ended first. A movie or
the last episode closes back to the details page. Without a show id (an episode's own detail page) these features stay hidden. A download keeps only
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
