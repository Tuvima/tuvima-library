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

Mute uses the same active native transport owner as volume changes. The controller remembers the last positive volume within that controller session; a mute action applies native volume zero and muted state immediately, updates the visible slider and controller, then reports its heartbeat. Unmute restores the remembered level. Positive slider changes explicitly clear native mute, and zero-volume observations do not erase the remembered level. A queued toggle remains an action until its owner handles it, so two queued toggles resolve in order. The popup forwards the action to the main owner rather than maintaining a competing volume cache. Restoring an older positive-volume muted snapshot remembers that level and presents zero; a saved zero remains zero and falls back to the configured positive volume when there is no remembered level. This correction adds no persisted volume-history field or new transport JavaScript.

## Capability projection

`PlaybackControlCatalog` derives controls from experience, surface, genuine queue/chapter/track data, and runtime capability flags. Unsupported Cast, audio-track, quality, intro, and credits actions are absent. Audio-track selection is shown only when both the manifest and active browser renderer report multiple switchable tracks. Movie/TV captions include exact managed text-track variants through the authorized Dashboard proxy. Current quality may be reported as information; a quality selector requires real selectable renditions.

The selector's preferred action uses the existing text-track endpoint. Lyrics may be timed or static; a line seeks only if its timestamp exists. Text-track refresh returns its operation outcome within the player and does not feed media identity or Review Queue. Subtitle offset is omitted because the current browser renderer does not apply one consistent offset across managed tracks and HLS tracks.

## Reader and personal media boundaries

EPUB settings are reader state stored on the device: font, size, line spacing, margins, theme, and width. The reader preserves a text offset through repagination, as a page index is not stable after an appearance change. Light, Dark, Sepia, and System themes alter reading content and chrome. Reader position and annotations remain on their existing API path; the audio/video controller does not own them.

The View video viewer retains its View-protected resource URL and local asset identity. `PlaybackSessionController.ViewSession` holds that typed View subject, transport facts, and presentation surface independently of the catalogue queue. Opening a personal video or audio item cancels a pending catalogue start and pauses the prior catalogue transport; an active catalogue video is closed. Starting a catalogue item ends the View session. View never creates a catalogue work, resolves a provider identity, or reports catalogue playback progress. Its authorized URL stays in the viewer transport and is not copied into playback session state.

View video uses the shared video control primitives for timeline, seek, play/pause, volume, speed, PiP where supported, and fullscreen. It exposes subtitle and audio-track selectors only when the browser reports tracks it can switch. The physical View transport remains viewer-owned, so closing or unmounting the viewer stops that element and exits browser PiP. Cross-route continuation needs a layout-owned View transport and an explicit resource-grant renewal policy; the typed View state alone does not keep an element mounted. Do not substitute a fake `WorkId` in `ListenQueueItem` to bypass this boundary.

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

Persisted restore and popup state delivery use the same complete typed snapshot JSON through `IJSStreamReference`, bounded to 8 MiB by `PlaybackSnapshotStream`. The default 32 KiB hub message limit remains unchanged. Initial readers first use a boolean presence probe, then return a raw Blob for .NET's automatic stream-result conversion. JavaScript-to-.NET callbacks explicitly wrap their Blob as a stream reference. Known absence skips the typed read; disappearance after the presence probe, malformed JSON, oversized or interrupted reads remain failures, retain the existing state and expose Retry. Each read owns stream/reference disposal, and host generation checks prevent an older completion from replacing newer playback. The former whole-string Blazor restore/callback path is retired; the stored JSON format is unchanged.

### Appearance ownership

Playback chrome uses the shared token family: background `#090F17`, menu/sidebar `#101821`, and elevated dock `#171F2A`. Blurred background artwork is darkened independently of the sharp foreground cover and the video picture. Do not introduce a brighter host-local palette.

Control surface and appearance are separate choices. Utility and window actions use one thin outline glyph catalog and bare appearance across dock, full, phone, popup and video players. Their 22px glyphs retain 44px interactive areas. Resting utilities are transparent. Hover, an open chooser, an authoritative active selection, and keyboard focus use the same 44px square accent-soft highlight with an 8px radius; keyboard focus adds one 2px ring with a 2px offset. Flat selects suppress every inherited form fill and keep hidden selected text hidden in every interaction state. Full and phone labels sit outside that square. Each grid slot centers the visual and uses the same top baseline, including Speed and Sleep. Disabled tools remain neutral.

Play/pause and relative skip remain distinct transport controls: the dock uses a 38px white play visual, while the popup uses a restrained 44–48px visual. Identity links have no resting underline and gain one on hover or keyboard focus; their text boxes own a padded pointer target without a slider or decorative overlay intercepting it. Chapter rows reserve separate number and inline activity columns so playback state cannot indent the title.

The audiobook dock keeps transport independently centered and justifies the complete utility group to the right edge, ending in Now Playing, Popout and Close. Window actions do not sit beside identity. A separate seek row uses the available width, with bounded timestamp columns and no fixed rail width or maximum-width cap. Constrained desktop/tablet layouts move the complete tool group into right-aligned wrapping rows; the layout reserves the resulting measured dock height. Phone mini-player composition stays bounded to identity, Play and Expand. Popup utilities are icon-only, with accessible names. The popup has no window-control toolbar or window-close handler; the native window owns closing it. A context-panel close only dismisses that panel. Visible glyph centers must match their target centers; icon-only buttons omit label spans instead of leaving clipped one-pixel text. Popup composition avoids redundant inner window framing and gives player/context panes equal-height outer bounds.

`AudiobookPlaybackProjection` supplies the shared audiobook presentation. Current chapter identity uses the exact asset plus its local position, while the chapter count describes the actual catalog timeline. Complete, finite, source-associated durations supply cumulative Book progress; incomplete parts degrade to Current recording. The popup keeps its seek control tied to local native time and presents the whole-book value separately, so a completed short Intro is not presented as a completed book.

The October 2 refinement is tracked in [the approved plan](../plans/audiobook-dock-sidebar-refinement-2026-10-02.md) and [its execution evidence](../reports/audiobook-refinement-2026-10-02.md). Those documents distinguish accepted packages from pending implementation and rendered checks.

### Exact playback speed

`PlaybackRatePolicy` accepts finite normal rates from 0.5x through 3x without rounding. Explicit scan transport has its own 1x–32x policy. The central `AppSelect` supplies the flat speed menu through `PlaybackSpeedControl`; invariant round-trip values preserve an existing arbitrary valid rate alongside the usual choices. The compact recipe owns one menu scrollbar, a visible selected check and the semantics of the actual focusable trigger and options. Opening a menu does not change speed, and only the initiating player surface opens it.

The controller's rate-selection version prevents older settings, start, heartbeat or native observations from overwriting a newer user choice. Same-book chapter transitions retain the selected rate; another book applies its saved policy. An unsupported saved rate blocks automatic start and retains the last valid rate until an explicit valid choice recovers. Popup speed commands validate the expected profile, work, asset and playback request version before changing the owner.

### Single context sidebar

`ListenContextSidebarState` selects one supported body: Chapters or History for audiobooks, and Queue, Lyrics or History for music. Selecting another tool replaces the body; selecting the open tool closes it. `ContextSidebars` stores only the selected key, open state and width per profile/device/media context. Obsolete stacked preference JSON is rejected rather than translated or reset by the application.

`ContextSidebarCoordinator` gives playback and Ingestion one layout-owned slot. An explicit user action may replace its owner; refreshes and delayed results cannot. Closing, resizing and disposal use the matching owner lease so an older component cannot disturb its replacement. The shared shell owns the single heading and independent body scroll area; content components do not introduce another full-height scrolling shell. Phone presentation uses the same selected body with modal focus ownership.

The page, app header and actual measured dock allocation must remain distinct layout regions. Fitting lane rails stay anchored; when a rail cannot fit, it joins the page's one scroll area. Cramped or zoomed dock layouts grow into readable rows instead of clipping controls or allocating impossible side tracks. Verification must measure visible glyph centers, window-action alignment and last-row reachability, not only native button dimensions.

### Audiobook sleep

`PlaybackSleepTimerControl` is the sole sleep chooser across the dock, full player, phone and popup. It uses the central select layer with one scrolling options list. The icon trigger displays a real remaining-time or chapter status when active and never reveals the hidden Off value on hover. Two non-selectable separators divide Off, configured timed choices and verified current/next chapter choices. Shared separator styling accounts for inset margins and paints only inside the select portal; MudSelect's shadow registration tree must not add lines or height beneath the trigger. Chapter choices use concise labels and an icon rather than appended chapter titles; unavailable reasons remain available through the disabled option's accessible name and description. The selected check follows the original choice, not a rounded countdown preset. All choices use the same Contracts-owned modes.

`AudiobookSleepTimerSelectionDto` carries only a choice and optional minutes. The main playback owner returns `AudiobookSleepTimerStateDto` after registration against the real native source. That state captures the profile, book, independent timer session, generation, originating asset/chapter, exact target asset/chapter/end, bound asset and playback request version. `AudiobookSleepTimerAvailabilityDto` reports verified targets or their unavailable reasons to every host; a popup cannot arm optimistically or infer a target from stale cached chapters. Signed source URLs are never persisted in timer state.

Minute deadlines are absolute UTC times and continue while paused. Chapter choices capture one verified current or immediate successor using `AudiobookSleepBoundaryResolver`, including a separate authorized audiobook file when its finite end is known. Seeking does not resolve a new target. A valid move to the captured successor can rebind the native arm; unrelated media, profile or permission changes cancel it. Timer lifetime is independent of a bookmark dialog draft.

The persistent native owner schedules the captured end and checks the asset, source, request and generation again before pausing. It is the single dispatcher for audio ended events: its capture-phase guard decides timer expiry before normal auto-next behavior, with one delivery per play cycle. A genuine native EOF can satisfy the captured target when its exact source, asset, request and timer ownership match, even when native duration is fractionally shorter than authored metadata. This does not permit an early-stop tolerance. The controller accepts only the matching expiry once; stale callbacks cannot pause a replacement source. Former Blazor audio-ended dispatch, slider/stepper UI, targetless restoration and dynamic chapter fallbacks must not be restored. Execution status and native-window verification limits are recorded in the refinement report.

### Audiobook bookmarks

Playback-position bookmarks are separate from For Me/My List references. `AudiobookBookmarkDialog` owns the Add/Saved presentation, and `AudiobookBookmarkActionService` owns its captured draft, saved-list state and confirmed deletion. Player hosts must not maintain a second Saved list or an immediate-add shortcut. Desktop placement uses the actual local initiating button reference, including the popup's own reference; phone uses a bounded sheet. Geometry stays local to Web and is independent of frozen playback capture and command authority. Closing restores the exact opener when it remains connected.

Opening Add captures native position and verified profile, book, session and asset facts once. `CapturedAudiobookBookmarkDraft` retains that position through later playback ticks; saving must not recapture it. The optional `Note` has a 200 UTF-16-unit limit, is separate from `Label`, and is normalized through the Domain policy. A verified natural transition within the same authorized book can retain the draft; a different profile, book, unrelated source or lost asset access invalidates it.

Native capture returns an asset/request/source-verification proof with position and optional duration, never a signed URL. The owner rechecks the exact request and live source after the asynchronous read. Captured Preview is a Web-local start intent: it returns the sole audio host to the frozen asset and position while preserving that book's draft lease. Saved and History replay establish their intended new playback request; cross-asset replay clears the previous stream and manifest before resolving the authorized source, while retaining the verified same-book catalog chapter timeline. Retained catalog chapters never substitute for resolving missing native transport. A native-confirmed Saved replay acknowledges success and releases only its exact old dialog binding, even if that binding has already closed; failed or stale commands retain their authority checks. Artist name and person link derive from the same canonical artist identity; the album label remains separate.

Both main and popout hosts use `ListenPlaybackCommandActionsClient` and `AudiobookBookmarkCommandDispatcher`. Main uses an in-process channel, while the popout uses transient BroadcastChannel request/reply messages. The Contracts-owned envelope supplies command and recipient correlation; bookmark commands, drafts and notes must never be persisted in localStorage. The main native owner validates authority before capture or replay. Pure snapshot reads do not emit change notifications, and an uncertain save result requires a Saved reload rather than an automatic duplicate retry.

Fresh bookmark DDL lives in `schema.sql`; the repository requires that schema and performs writes through the shared write boundary. Do not restore lazy table creation or runtime schema correction. Scoped disposable development-state changes are explicit setup operations recorded in the execution evidence.

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
