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
the phone, and everything else stays portrait. The rule lives in `core` `OrientationPolicy`. The phone app
has no on-device video surface yet, so nothing currently unlocks; the video screen must apply the rule
while shown and restore portrait on leaving.

Video: the phone app declares MKV and M4V containers and SRT subtitles, and the playback manifest now
carries audio tracks, subtitle tracks, chapters and skip segments (`PlaybackTrackPicker` chooses the
starting tracks from the profile's language preferences).

Unit tests for the shared client (API v1 paths, typed profile-switch results, photo consent, photo grid layout,
and the offline download rules: server-won detection, replay order, the storage cap, expiry and report results,
plus kept photos: the cap with photos, the purge rule, kinds and the gallery plan)
run with `gradle :core:testDebugUnitTest`. They use plain JUnit and cover logic that needs no Android
framework; wire-shape parsing needs the Android JSON classes and is not yet covered by JVM unit tests.
