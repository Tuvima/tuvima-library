---
title: "Playback Architecture"
description: "Playback ownership, presentation surfaces, client timing, and audio and video transport boundaries."
audience: "developer"
category: "architecture"
product_area: "playback"
---
The current player/control delivery and its verification limits are recorded in [the October 5 report](../reports/player-controls-2026-10-05.md). The October 5 contract below supersedes earlier phone modal-tool, popup vertical-volume, and shared-control naming descriptions.

## October 5 player and shared-control contract

`PlaybackFullPlayer` replaces its artwork region with local Lyrics, Output, Queue/History, Chapters, Bookmarks, or History content. Progress, transport, modes, and horizontal volume remain below that region. Lyrics and Queue mode buttons are icon-only with tooltips and accessible names. The canonical bookmark Add/Saved component supports embedded presentation, preserving its captured lease, validation, save, replay, and deletion workflow. Mode changes release transient utility menus; changing speed or sleep does not replace the inline bookmark experience.

`PlaybackPanelCard` composes the same snapshot/sink panel body in the dock, expanded desktop scene, and full player. Dock Lyrics and Queue/History support a hover preview and pinned click state without a layout-sidebar lease. Nested row menus keep their parent panel open; dismissal returns to the parent. The queue shows the current occurrence separately and edits only upcoming occurrences. Drag handles, Alt+Arrow keys, and row menus address stable occurrence IDs. Modified queue keys cannot also trigger transport volume shortcuts.

Engine replace/add replies provide the persisted occurrence IDs. Reorder and remove adapters use the existing Engine routes, refresh the state version, preserve current/past entries, and verify the returned order/current identity. Changed saved state fails visibly; a missing mutation reply permits one read-back, never an automatic second mutation. Snapshot restore retains occurrence IDs. Clear History confirms before removing only the active player's local music history; durable history, progress, play counts, and audiobook history stay on their existing paths.

Output switching runs only on the main native audio element and only for already-permitted output devices. It never requests microphone access. Device changes update capability presentation; unavailable outputs and rejected changes provide recoverable guidance. Volume detects browsers that ignore software writes. Lossless badges require the current asset's direct, unconverted lossless stream plus known sample rate and bit depth. Unsupported or unknown delivery omits the badge.

Player identity anchors retain canonical hrefs and modified-click behavior. Normal activation uses the authorized owner and a short-lived fresh-detail intent that defeats delayed scroll restoration while preserving later normal Back restoration. Media Session handlers are installed only by the main audio owner, publish native position/state and bounded artwork, and clear on dismissal/disposal. Physical output, mobile OS media keys, and native popout acceptance remain distinct from automated and browser fixture checks.

`AppTooltip` and `AppRangeSlider` replace the former playback-only component names throughout the Dashboard. `AppSelect`, its typed core, and native/int/media-type adapters share one control family. Explicit intrinsic sizing measures a selected label within available space, keeps labels above fields, and offers full-label help only when truncated. `AppProgressBar` supplies determinate/indeterminate semantics; `AppSpinner` owns the remaining Mud spinner. The wider CSS reduction and interop audit remain a separate delivery.

Tuvima playback is split into session state, presentation surfaces, and host-specific transport.
The Dashboard uses persistent browser audio and video elements. Future iOS and Android clients should reuse logical commands, queue, device identity, and progress concepts without depending on DOM routes or browser-only APIs.

## Controller Boundary

The Web UI talks to `PlaybackSessionController` in `src/MediaEngine.Web/Services/Playback/`.

Its intended boundary is:

- `PlaybackSessionState State`
- `Task DispatchAsync(PlaybackCommand command, CancellationToken ct = default)`
- `event Action<PlaybackChangeKind>? Changed`

The controller owns session state and coordinates focused collaborators such as queue behavior, state transitions, transport commands, progress heartbeats, sleep timer state, and client identity. UI code should prefer typed `PlaybackCommand` dispatch for commands and read player state from the controller instead of reaching into storage, JavaScript, or Engine DTOs directly.

`ListenTransportControls.razor` renders shared play/pause, skip, previous/next, and chapter controls. Snapshot-driven controls use `IPlaybackCommandSink`: the direct and BroadcastChannel sinks send the same captured profile/work/asset/request envelope to the main owner. Upcoming queue actions identify the stable queue occurrence rather than a mutable row index. Correlation and owner-side deduplication prevent a retried command from applying twice. Presentation and session-exit actions stay outside this reusable transport capability set. The hidden browser `<audio>` element remains isolated in the persistent Listen host. A future native app should implement its own native transport host against the same command/state model.

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

Playback chrome uses the shared player palette: background `#0F131E`, surface `#121623`, accent `#8852FC`, hover `#A46FFF`, track `#30364A`, text `#F7F7FA`, and secondary text `#9EA4BC`. Blurred background artwork is darkened independently of the sharp foreground cover and the video picture. Do not introduce a brighter host-local palette.

Control surface and appearance are separate choices. Utility and window actions use one thin outline glyph catalog and bare appearance across dock, full, phone, popup and video players. Their 22px glyphs retain 44px interactive areas. Resting utilities are transparent. Hover, an open chooser, an authoritative active selection, and keyboard focus use the same 44px square accent-soft highlight with an 8px radius; keyboard focus adds one 2px ring with a 2px offset. Flat selects suppress every inherited form fill and keep hidden selected text hidden in every interaction state. Transport and utility triggers are icon-only; state remains in accessible names and delayed tooltips. Tabs, headings, choices, time information and list content retain readable text. Each grid slot centers the visual and uses the same top baseline, including Speed and Sleep. Disabled tools remain neutral.

Play/pause and relative skip remain distinct transport controls. Identity links have no resting underline and gain one on hover or keyboard focus; their text boxes own a padded pointer target without a slider or decorative overlay intercepting it. Chapter rows reserve separate number and inline activity columns so playback state cannot indent the title. State-aware tooltips use shared delayed hover/focus behavior, without replacing accessible names.

The audio dock is a full-width, square-edged bottom allocation, 84px on desktop/tablet and 72px plus the applicable safe area on phones. Its seek rail sits on the top edge, with elapsed and negative remaining time. Transport stays independently centered. At constrained desktop/tablet widths a separate utility More menu exposes secondary tools, while the terminal Close player remains directly available outside overflow. Phone mini-player composition stays bounded to identity, Play/Pause and Expand, with no session-stop Close. Visible glyph centers must match their target centers; icon-only buttons omit label spans instead of leaving clipped one-pixel text.

Desktop/tablet Close captures and saves the verified native position as paused, without marking completion, then rechecks profile/work/asset/request/source before releasing the session. It cannot close a successor that started during an awaited operation. Saved resume, backend history and bookmarks survive. Phone Collapse changes only presentation and must preserve playback. The popout has no in-player Close, Stop, Collapse, Back-to-dock or window-close command; native window closure is separate from audio ownership. Closing a tool dismisses that tool only.

`AudiobookPlaybackProjection` supplies the shared audiobook presentation. Current chapter identity uses the exact asset plus its local position, while the chapter count describes the actual catalog timeline. Complete, finite, source-associated durations supply cumulative Book progress; incomplete parts degrade to Current recording. The popup keeps its seek control tied to local native time and presents the whole-book value separately, so a completed short Intro is not presented as a completed book.

The October 2 refinement is tracked in [the approved plan](../plans/audiobook-dock-sidebar-refinement-2026-10-02.md) and [its execution evidence](../reports/audiobook-refinement-2026-10-02.md). Those documents distinguish accepted packages from pending implementation and rendered checks.

The October 4 implementation follows [the reviewed player plan](../plans/player-update-reviewed-plan-2026-10-03.md). Its [verification report](../reports/player-update-2026-10-03.md) supersedes the earlier dock layout and records actual package results, rendered failures and unavailable platform checks separately. An implementation contract here does not imply every platform has passed acceptance.

### Exact playback speed

`PlaybackRatePolicy` accepts finite normal rates from 0.5x through 3x without rounding. Explicit scan transport has its own 1x–32x policy. The central `AppSelect` supplies the flat speed menu through `PlaybackSpeedControl`; invariant round-trip values preserve an existing arbitrary valid rate alongside the usual choices. The compact recipe owns one menu scrollbar, a visible selected check and the semantics of the actual focusable trigger and options. Opening a menu does not change speed, and only the initiating player surface opens it.

The controller's rate-selection version prevents older settings, start, heartbeat or native observations from overwriting a newer user choice. Same-book chapter transitions retain the selected rate; another book applies its saved policy. An unsupported saved rate blocks automatic start and retains the last valid rate until an explicit valid choice recovers. Popup speed commands validate the expected profile, work, asset and playback request version before changing the owner.

### Playback tools and Ingestion sidebar

`ListenContextSidebarState` selects one supported body: Chapters or History for audiobooks, and Queue, Lyrics or History for music. Selecting another tool replaces the body; selecting the open tool closes it. `ContextSidebars` stores only the selected key, open state and width per profile/device/media context. Obsolete stacked preference JSON is rejected rather than translated or reset by the application.

Audio tools no longer acquire a layout sidebar lease or change page width. `PlaybackTransientToolCoordinator` selects one temporary tool; desktop dock tools use `PlaybackPopover`, and narrow presentations use modal sheets with bounded scrolling and focus restoration. Dock popovers clear the top seek rail. Outside pointer or focus departure dismisses a popover without stealing focus from the destination; Escape, trigger dismissal and explicit sheet Close restore trigger focus. The browser dismissal intent is forwarded through the .NET portal-release callback. The selected media tab remains profile-scoped; an old saved open flag does not automatically reopen a tool on startup. Ingestion retains the layout-owned `ContextSidebarCoordinator` lease, measured shell width and resize behavior. Playback changes must not disturb that ownership.

`PlaybackContextPanel` consumes its supplied snapshot and command sink. Its body never substitutes another circuit's controller as playback truth. The thin `ListenContextSidebar` wrapper supplies the main snapshot only when no explicit snapshot is provided. Music Queue preserves the current occurrence while Clear or removal affects upcoming occurrences; audiobook Chapters uses compact source-authored title/duration rows without repeated cover art, with an outlined current row and distinct paused/playing marks. History retains the recorded segment identity.

`PlaybackDesktopScene` places a shared left artwork/identity stack beside one inline panel. Dock tools select that panel's tab rather than opening a second panel. The title stays complete; artwork shrinks before text clips, and constrained content can scroll. `PlaybackArtworkUrl` selects bounded managed images and resolves legacy audiobook recording-cover snapshots through the authorized canonical book-cover route. It never falls back to the recording endpoint's original image. The player uses its own 720px phone boundary; global navigation retains 840px. The audio dock has an explicit app-frame grid row so expanded tablet playback cannot occupy a vacant header row.

`PlaybackFullPlayer` supplies the vertical phone and popup composition from a captured snapshot and command sink. It owns presentation only: one local seek rail, shared transport/tools and shared panel bodies, with text-only whole-book progress. Square music artwork and portrait book artwork yield space before controls clip; short screens may scroll intentionally. Only the phone host supplies Collapse. The popup omits that callback, its control and header slot, fills its content area, and defaults to 420 by 780 pixels subject to available screen bounds. Bookmark dialogs retain their existing host-local opener and command bridge. The full presentation contains Tab focus within its visible controls and owned selector portals; modal sheets and bookmark dialogs retain their own focus traps. Its boundary detaches on disposal, and phone Collapse restores the originating control through the host.

Full-player identity links submit allowlisted identity kinds and canonical IDs through the existing addressed owner channel. The main owner checks current profile/work/asset/request, verifies Engine access and rechecks freshness after awaiting, then uses normal SPA navigation without reloading the media host. Successful navigation clears the main player's temporary tools and returns its presentation to the dock so the requested details are visible; the session keeps playing and the popup stays full. The popup never follows an arbitrary supplied URL or navigates itself into `MainLayout`. Native popup registration binds a window token to its sender; passive cleanup releases only that matching registration and never stops main audio. A popup left open during video shows a truthful focus-main-window state and does not host another video element. Package and platform acceptance remain separately recorded in the verification report.

`PlaybackLyricsPresenter` reads raw authorized text-track content and parses valid leading LRC timestamps. Ordinary text has no simulated timing or seek action. Profile, asset, work and request generations prevent stale reads or choices from replacing current lyrics. Temporary version choice is projected through the existing owner channel as `lyrics_selection`; it is distinct from the explicit preferred-version API. Refresh retains an authorized user choice. The lyric scroller suspends following after manual input, offers Back to current line when that line leaves view, and resets following when the subject/version changes. Timed seeking uses the same captured command sink as transport.

The page, app header and actual measured dock allocation remain distinct layout regions. Fitting lane rails stay anchored; when a rail cannot fit, it joins the page's one scroll area. Constrained dock layouts use utility overflow rather than clipping controls or allocating impossible side tracks. Verification measures visible glyph centers, window-action alignment, seek clearance and last-row reachability, not only native button dimensions.

### Shared audio presentation and lyrics timing

The desktop dock is one flush 92px row with a full-width seek rail, 64px artwork and an independently centered 60px purple-ring transport. Music and audiobook slots share geometry; audiobook order is skip back, previous chapter, play/pause, next chapter, skip forward. At 1100px utilities move into overflow while Queue/Chapters, Expand and Close remain direct. Phone mini/full surfaces keep their bounded composition and omit desktop volume.

`PlaybackArtworkUrl` resolves real ingestion URL shapes for every audio surface. It accepts known bounded renditions, resolves unsized recording covers and inherited track art through canonical album/book work cover URLs, and uses a truthful raw source only when music has no canonical identity. It does not invent rendition siblings or width descriptors. Desktop Now Playing retains the dock and uses artwork/identity at left, Lyrics/Queue/History at right and a focus-restoring Back control. Phone and popout continue sharing the snapshot/sink full player; the popout's vertical volume slider has native keyboard semantics and no in-player exit.

LRC positive offsets advance every leading timestamp and negative offsets delay it. Browser highlighting follows validated native audio time in the owner window, or rate-aware extrapolation of the latest snapshot in a secondary presentation. Play, pause, seek, rate and time events resynchronize; detach cancels animation and event listeners. `lyrics_lead_milliseconds` defaults to 150 and clamps to 0–500. This local presentation clock does not change observer cadence or playback ownership. LRCLIB synced candidates within two seconds retain full duration confidence; two-to-ten-second alternates lose confidence and are not automatically preferred, and larger differences are rejected. Existing explicit preferences remain authoritative.

Anchored playback panels use 12px corners, 18px content padding, underlined tabs, internal scrolling and a measured trigger caret. Shared row content and captured commands remain the same in dock panels, desktop Now Playing and bounded sheets.

### Video presentation

`PlaybackVideoChrome` owns the shared header, centered transport and bottom timeline/tool slots for Watch and View; each host retains its mounted media element and existing commands. A whole-stage hover does not hold controls open. Playing idle chrome hides after three seconds; advancing native time clears stalled loading holds without restarting the timer each frame. Touch stage taps toggle chrome, while controls, focus, paused media, loading and open tools retain visibility. Chapter markers expose title/start labels through pointer-time presentation and keyboard-accessible tick buttons.

`VideoPlaybackHost` retains its persistent native element while `VideoPresentationResolver` supplies one truthful context: accessible owned TV episodes, otherwise usable movie chapters, otherwise the actual upcoming queue, otherwise none. Explicit episode-still DTO fields distinguish a genuine child still from root show artwork. Chapter ticks come from real source timing.

`playback-chrome.js` hides playing controls and the cursor after three idle seconds, makes hidden control regions inert, and keeps chrome visible for pause, loading, errors, keyboard focus and open tools. `PlaybackPopover` portals inside the fullscreen host and serializes release so dismissed sheets do not leave an obstructing modal behind. Back saves a guarded paused resume position before releasing video. Owned episode selection validates metadata under the captured old context and rechecks profile/work/asset/request after each await. Once validated, startup uses the host lifetime and the controller's new request lifetime, never the old metadata token that request replacement cancels. Component disposal still cancels the accepted start.

`VideoEndCardState` binds the next owned episode to the captured current session. Its ten-second clock advances only with genuine playing time; pause, stall, hidden-document state and relevant seeking suspend or reset it. Dismissal suppresses the card for that play cycle. The countdown and native ended path share one guarded continuation, so races cannot start the successor twice. Native fullscreen and PiP remain browser capabilities, with recoverable activation failures rather than fabricated support.

### Audiobook sleep

`PlaybackSleepTimerControl` is the sole sleep chooser across the dock, full player, phone and popup. It uses the central select layer with one scrolling options list. The icon-only trigger exposes real remaining-time or chapter status through its accessible name and tooltip, highlights the authoritative active state, and never reveals the hidden Off value on hover. This October 4 presentation supersedes the earlier visible sleep-status treatment; earlier report captures remain historical evidence. Two non-selectable separators divide Off, configured timed choices and verified current/next chapter choices. Shared separator styling accounts for inset margins and paints only inside the select portal; MudSelect's shadow registration tree must not add lines or height beneath the trigger. Chapter choices use concise labels and an icon rather than appended chapter titles; unavailable reasons remain available through the disabled option's accessible name and description. The selected check follows the original choice, not a rounded countdown preset. All choices use the same Contracts-owned modes.

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

### October 2026 remediation follow-up

Home previews reserve resting shelf geometry before expansion and use a bounded cinematic layout for movies/TV, with episode identity/description and truthful progress. Watch cards outside Continue preview a landscape background and brief detail copy; non-Watch cards remain outline-only. Shelf arrows have dedicated rails and minimum 44px targets. Continue groups have natural whole-card widths, 32px dividers/gaps and stack below 1280px. Shared book foreground height, perspective, page/spine geometry and proportional shadow live in `HeroBackdrop`; detail stages retain 95svh. Known small Home sources use backdrop framing; unknown dimensions use the conservative fallback.

The shared primary action uses the larger default requested by the product owner for Play, Read, Listen, Resume and Continue: 22rem wide and 4.6rem high, capped to the available phone width. Plain actions use larger text/icons; continued actions show a second percentage line and embedded progress. Adjacent My List, Rate and More are 56px circles; Shuffle remains a circle beside album Play. Restart belongs in More and preserves existing query/fragment state while starting at position zero. Progress includes a visible watched/read/listened percentage and a slim strip inside the primary action. Rate choices are Likes/Dislikes; only a song's Love/heart adds it to Favorites. My List is saved-item state, not a rating. Song menus operate on the selected song and use guarded active-profile/snapshot identity.

Home refreshes are coalesced and profile-bounded: progress is limited to once per minute, library-state changes to once per 15 seconds, new media to a two-second debounce, pause to five seconds, and dismissal/stop to immediate refresh. Transport ticks do not rebuild shelves or Recently Added. Background Recently Added refresh keeps the mounted cards visible and deduplicates paging.

Audio, phone/popout and Watch/View video use `PlaybackSeekRail`: total duration sits at the right end and toggles to remaining time. The dock also shows elapsed time at the left end, aligned with the rail and right label. Other surfaces retain elapsed time in the focused/hovered/dragged seek bubble. Chapter name/time shares that bubble. Speed uses the bounded 0.5–3.0 slider in 0.05 steps with reset to 1×; stored rate precision is retained until changed. Sleep retains its existing select and authoritative deadline.

Watch/View video use shared bottom chrome, a Back affordance, control hover/focus holds, and one subtitle-positioning owner in `playback-chrome.js`. Active cues sit 12px above the seek rail while controls show and restore authored settings when hidden or detached. Up Next uses owned eligibility and separates Up Next/Episodes/Chapters; no provider-only episode is promoted. Native cue animation is browser-dependent and is not promised.

Lyrics content omits the former version/provider toolbar. Enhanced LRC strips inline timestamps into independently timed words, uses native audio time, and keeps client highlighting separate from server render updates. Only explicit instrumental markers, explicit word-end gaps, known intro/outro boundaries produce three-dot sections; ordinary long line spacing never invents a musical break. Three configurable durations live in `config/ui/playback-client.json`. Unknown duration prefers timed lyrics. Authorized empty state opens Manage lyrics in the existing editor Details tab. LRCLIB attribution appears for LRCLIB tracks.

Editor artwork previews are padded and contained. Open full size opens the authenticated original image in a new tab and removes rendition-size query parameters. Canonical entity artwork lookup avoids an expensive full-detail composition when a cover claim exists. No ten-minute cache was added because invalidation ownership is not yet established.


### Product owner visual corrections October 4 2026

The latest product-owner reference supersedes earlier hover and smaller-button guidance. All Continue Watching movies and episodes keep their still and exact resting dimensions; hover/focus reveals a compact in-place identity overlay without JavaScript expansion. Books, comics, music, audiobooks, and other non-Watch media use only a subtle outline and register no preview mouse events. Movie/show cards outside Continue retain their cover at rest and reveal a landscape backdrop with concise details on hover/focus, including Watch browse grids. Media/group cards still have one detail link; group artwork composition remains fixed.

All hero Play/Read/Listen/Continue actions use the same larger 22rem by 4.6rem button, bounded to the available width on phones. A continued action includes the applicable percent watched/read/listened; a plain action centers larger text and icon. Song menus use intrinsic content width, the same shared item-height/typography/padding tokens as detail menus, and 56px More triggers in full Now Playing. Obsolete overflow-menu geometry overrides are removed. The audio seek rail and end time sit inside the dock, centered together above the controls; desktop reserves 104px and phone 88px plus safe area. Playback ownership and guarded personal-action identities remain unchanged.

### October 5 dock and palette refinement

The app primary purple is #8852FC, with #A46FFF for hover and rgba(136, 82, 252, 0.16) for soft highlights. Dock elapsed time sits at the left and remaining/total time at the right, sharing the progress line center. Desktop song actions include Rate alongside Favorite and More, using the existing guarded song identity. Lyrics uses the shared quotation speech-bubble glyph. Open rating choices lift the whole Rate control above adjacent actions; detail action rows also lift while the chooser is open. The 104px desktop and 88px phone dock allocation remains current; earlier geometry above is historical.

Dock rating icons use the same bare 22px glyph sizing and 44px target as Favorite. Song overflow omits Like/Dislike when the direct Rate control is rendered; surfaces without a direct Rate control retain those menu actions. Detail rating circles keep their existing appearance.

Rate hover, focus, and selected-choice highlights are circular and contained within their 44px targets and padded choice pill. Dock Rate remains borderless at rest; its open/hover highlight is circular, matching the choice buttons.

## October 6 video and dock corrections

Watch Close returns to movie details or show details scoped to the episode. The owned presentation context supplies the show identity and Close waits for its existing metadata read before capturing the destination. View Close releases its managed video and invokes the hosting callback; its shared header exposes one Close and the selected Info toggle. Neither player exposes Back or duplicates viewer actions in a More menu.

Native PiP enter/leave events are bound to the current profile/work/asset/request identity. While PiP is active the in-page Restore bar is absent. The product-owner decision is to show the compact Restore bar after either native Close or Back to tab, because the browser leave event does not distinguish them. Stale callbacks do not change the replacement player.

Shared caption positioning uses the native video viewport, including the element's letterboxed area, updates late-loaded tracks and newly active cues while chrome is visible, and restores authored cue settings when controls hide or detach. Cue type uses the existing xs/lg tokens and 1.15vw. Browsers without native lineAlign reserve native cue line-box space before the rail. Video volume overlays the adjacent controls without moving them and speed uses one compact heading-free anchored control. Video context rows reserve a separate activity-marker column and show episode numbers once, separate from titles.

Pinned desktop dock cards retain their tool identity through page navigation and same-profile audio queue changes. Ordinary menus can open above a pinned card and dismiss independently. Hover previews do not replace a pinned card; a second pinned dock card replaces it. Escape is scoped to focus inside the card. Player shutdown, a profile change or a switch out of the audio experience clears the pinned card. Dock height reservations and playback session ownership remain unchanged.
