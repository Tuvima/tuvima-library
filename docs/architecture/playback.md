# Playback Architecture

Tuvima playback is split into session state, presentation surfaces, and host-specific transport.
The Dashboard uses persistent browser audio and video elements. Future iOS and Android clients should reuse logical commands, queue, device identity, and progress concepts without depending on DOM routes or browser-only APIs.

## Controller Boundary

The Web UI talks to `PlaybackSessionController` in `src/MediaEngine.Web/Services/Playback/`.

Its intended boundary is:

- `PlaybackSessionState State`
- `Task DispatchAsync(PlaybackCommand command, CancellationToken ct = default)`
- `event Action<PlaybackChangeKind>? Changed`

The controller owns session state and coordinates focused collaborators such as queue behavior, state transitions, transport commands, progress heartbeats, sleep timer state, and client identity. UI code should prefer typed `PlaybackCommand` dispatch for commands and read player state from the controller instead of reaching into storage, JavaScript, or Engine DTOs directly.

`ListenTransportControls.razor` renders the shared play/pause, skip, previous/next, and chapter controls for the bottom bar, side panel, and popup. The hidden browser `<audio>` element remains isolated in the persistent Listen host. A future native app should implement its own native transport host against the same command/state model.

## Presentation and dock ownership

`PlaybackPresentationSurface` identifies Docked, NowPlaying, PrimaryVideo, PictureInPicture, RestorableVideo, and Fullscreen presentation. Changing surface does not replace the queue item or increase the playback start version. The browser audio element remains mounted in `ListenNowPlayingBar`, and the video element remains mounted in `VideoPlaybackHost`. Presentation surface is UI state; persisted progress is position and item identity.

`MainLayout` reserves a grid row for the compact audio player and puts page scrolling in the adjacent content row. A `ResizeObserver` publishes the actual row height as `--tl-audio-dock-height` for anchored lane rails and sheets. On phone the primary navigation occupies its own row. An active audio session keeps that dock visible through pause, navigation, Now Playing, and an optional separate popup; closing the session releases the row. The navbar playback indicator opens Now Playing directly. Now Playing expands within the app as a region that leaves the dock controls keyboard reachable and uses the same transport session.

Video has no full-width minimized transport. Leaving `/watch/player/{workId}` attempts native browser PiP when it is available. Browser activation rules can reject this attempt; in that case the host pauses playback and leaves a small, visible Restore control. An explicit PiP request changes the presentation state only after the browser accepts it. A new audio or video item cancels the previous pending start and pauses the other persistent host.

## Capability projection

`PlaybackControlCatalog` derives controls from experience, surface, genuine queue/chapter/track data, and runtime capability flags. Unsupported Cast, audio-track, quality, intro, and credits actions are absent. Audio-track selection is shown only when both the manifest and active browser renderer report multiple switchable tracks. Movie/TV captions include exact managed text-track variants through the authorized Dashboard proxy. Current quality may be reported as information; a quality selector requires real selectable renditions.

The selector's preferred action uses the existing text-track endpoint. Lyrics may be timed or static; a line seeks only if its timestamp exists. Text-track refresh returns its operation outcome within the player and does not feed media identity or Review Queue. Subtitle offset is omitted because the current browser renderer does not apply one consistent offset across managed tracks and HLS tracks.

## Reader and personal media boundaries

EPUB settings are reader state stored on the device: font, size, line spacing, margins, theme, and width. The reader preserves a text offset through repagination, as a page index is not stable after an appearance change. Light, Dark, Sepia, and System themes alter reading content and chrome. Reader position and annotations remain on their existing API path; the audio/video controller does not own them.

The View video viewer retains its View-protected preview URL and local item identity. It does not create a catalogue work, resolve a provider identity, or report catalogue playback progress. Opening a personal video or audio item pauses the prior catalogue session. View video uses the shared video transport primitives for timeline, seek, play/pause, volume, speed, PiP where supported, and fullscreen. It exposes subtitle and audio-track selectors only when the browser reports tracks it can switch. Its transport is still viewer-local: closing the View viewer ends browser PiP, and continuing that View stream across routes needs a separate persistent View subject/session contract. Do not substitute a fake `WorkId` in `ListenQueueItem` to bypass that boundary.

## Web and native contract

The transport-neutral part is subject identity, active item, queue/chapter index, position, duration, play intent, rate, volume/mute, repeat/shuffle, profile/device context, and typed commands. Browser stream URLs, element references, PiP/fullscreen activation, DOM text tracks, and CSS surface placement stay in the Web host. The iOS `PlaybackCoordinator` remains a native transport implementation and should map those logical fields to AVPlayer and remote commands; this Web change does not add CarPlay, Android Auto, pairing, or a device picker.

## Shared Primitives

Shared playback primitives use `Playback*` names when they apply beyond Listen:

- `PlaybackExperience`: `Music`, `Audiobook`, `Video`
- `PlaybackPhase`: `Idle`, `Loading`, `Ready`, `Playing`, `Paused`, `Ended`, `Error`, `NeedsGesture`
- `PlaybackCommand`: user/session intent
- `PlaybackTransportCommand`: host transport work, such as start, pause, seek, speed, or volume
- `PlaybackClientContext`: `DeviceId`, `DeviceName`, `Client`, `AppVersion`, and `DeviceClass`

Listen-specific behavior stays under `Listen*` or `Audiobook*` names. Audiobooks intentionally use a single-item queue: choosing another audiobook replaces the current queue rather than creating a multi-book playlist.

## Client Identity

Web stores a stable device id in localStorage under `tuvima.playback.v2.device-id`. Future native clients should store the same concept in Keychain on iOS and Keystore on Android. Engine heartbeats and queue sync should use the supplied device id and client string, not a hardcoded Dashboard id.

## Configuration

Browser transport mechanics live in `config/ui/playback-client.json`: popup dimensions, immediate-action debounce windows, audio observer cadence, seek tolerance, volume step, heartbeat interval, transport UI interval, pending command limits, and default volume.

User-facing listening preferences remain in `UserPlaybackSettingsDto.Listening`. Do not mix profile preferences such as skip seconds, audiobook speed, resume rewind, or sleep timer options with low-level browser transport config.

## Mobile Preparation

The Engine has explicit Android and iOS playback capability profiles. Native clients should plan for:

- background audio
- OS media controls and lock-screen metadata
- audio interruptions and route changes
- Bluetooth and headset events
- secure stream URLs
- byte-range and HLS support
- offline downloads
- resume/progress conflict handling across devices

## Adaptive Video Delivery

The playback manifest keeps direct play when the negotiated client profile supports the source. Otherwise, the Engine creates a source-aware HLS VOD package under the configured variant cache. A package contains independent H.264 video renditions, AAC alternate-audio playlists, managed and embedded WebVTT caption playlists, and a master manifest. Rendition heights never upscale the source.

Packages become visible only after every playlist reference has been validated and the staging directory has been moved into place. The package row records its source hash, profile, size, completion state, and last access. A background cleanup service removes abandoned staging directories, expired/failed packages, and least-recently-used packages above the storage limit while leaving active readers alone.

The manifest returns a short-lived signed path scoped to one asset and one package. Native players request the master, variant playlists, and segments through that same path. The Dashboard exposes the path through `/engine-hls/`; the Engine rejects expired, modified, cross-package, and traversal attempts. HLS playlists use VOD timelines and aligned independent segments, so a native HLS client can seek immediately after preparation. Resume remains the shared persisted playback position applied by `PlaybackSessionController`.

The persistent video host owns video transport, captions, audio-track selection, seeking, and fullscreen. It follows `recommendedDelivery` from the manifest and must not fall back to an incompatible direct source when HLS preparation is pending or failed.

## FFmpeg and Hardware Profiles

Windows installers bundle the checksum-pinned FFmpeg build described in `tools/ffmpeg/README.md`. Container builds verify the HLS muxer plus H.264, AAC, and WebVTT encoders. Engine readiness reports these capabilities separately.

`hardware_acceleration` accepts `auto`, `nvenc`, `quicksync`, `vaapi`, `cpu`, or `none`. Auto mode probes hardware encoders and falls back to `libx264`; explicit software modes still report and use normal HLS capabilities. A failed hardware encode is retried once with `libx264`.
