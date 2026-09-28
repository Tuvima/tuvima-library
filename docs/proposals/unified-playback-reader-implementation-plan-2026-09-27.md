# Unified playback and reader implementation plan

Status: implementation in progress as of September 28, 2026. The attached requirements and eight mockups remain design input. The initial implementation covers a layout-owned audio dock, in-app Now Playing, capability-gated video controls and PiP fallback, managed subtitles and lyrics, EPUB appearance, and shared personal-video controls. The remaining acceptance work is tracked below.

## Plain-English product walkthrough

1. **Keep listening while moving around Tuvima (WP1–WP3).** Starting a song or audiobook opens a compact player at the bottom of the app. The page ends above it, including the last shelf row and any buttons. It stays available as the user moves among Home, For Me, Listen, details, and other library pages, including while Now Playing is open. Pausing keeps the current session and its controls visible; explicitly closing playback gives the space back. Music shows track, artist, and album; audiobooks show book and current chapter. The dock's expand icon and the navbar playback indicator open a richer Now Playing view without restarting the recording. Acceptance: scrolling, navigation, menus, and keyboard focus never put content behind the player; the dock remains usable in Now Playing; time continues advancing across compact/expanded transitions.

2. **Get the right controls for the item in front of you (WP1, WP3–WP5).** A music listener can open Queue or available Lyrics; an audiobook listener can change speed, select a real chapter, add a bookmark, or set a sleep timer. A movie offers captions, language, quality, and device choices only when those choices are usable. TV adds episode identity and Next Up only for owned, playable episodes. Acceptance: absent capabilities are omitted, selected tracks and active modes are apparent, and a control never promises a choice the asset or device cannot provide.

3. **Continue watching without a permanent video bar (WP4).** Movies and episodes play on a video-first surface. When a supported browser or native platform can enter picture in picture (PiP), leaving Watch keeps the same video session playing there; Restore returns to the primary player. Fullscreen uses the platform's video behavior. Acceptance: navigation does not create a second video or reset position; there is no full-width Watch transport dock over browsing content.

4. **Improve captions and lyrics in place (WP5).** The player lists real embedded and external subtitle versions, lets the user choose one, and offers Find better subtitles through the existing text-track operation. Music similarly shows timed lyrics when available and static lyrics otherwise, with repair and preferred-version actions. Acceptance: refresh reports its real outcome, preferred choices persist, and no subtitle request sends the item back through media ingestion or Review Queue.

5. **Read in a comfortable theme (WP6).** EPUB opens in the existing reader with Contents, Search, Bookmarks, and an `Aa` appearance control. Light, Dark, and Sepia are available, with System when reliable on the current client. Font, size, spacing, margins, and appropriate layout choices persist. Acceptance: reopening the book restores appearance and reading position; bookmarks, highlights, search, and progress still work.

6. **View personal videos without turning them into catalogue items (WP4, WP7).** A View video gets the same clear transport and applicable PiP/fullscreen behavior as catalogue video. Its Info panel still shows capture and file facts plus authorized Favorite, Gallery, Download, and archive/trash actions. Photos remain an immersive viewer. Acceptance: View playback never requires a Wikidata/TMDB work identity or shows TV actions; View access checks remain in force.

7. **Use the same experience on smaller screens and future native clients (WP1, WP8).** Phone bars keep the essentials, with other actions in accessible bottom sheets. Tablets can place lyrics or chapters beside the player. Existing iOS playback and future native integrations share logical commands and metadata while choosing their own physical transport. Acceptance: touch targets, safe areas, keyboard and screen-reader behavior work at each size; browser and native contracts are not tied to Blazor components.

What stays: one-click Play/Resume/Restart, profile progress and queue semantics, configured audiobook skip intervals, existing stream delivery and text-track operations, current reader content and annotations, the View authorization model, and the present Home/lane/detail navigation. This work changes the consumption surfaces, not the surrounding library information architecture.

Scope boundaries: no new Listen landing page, radio/charts/downloads navigation, music artwork-only fullscreen, synthetic chapters or subtitle choices, fake intro/credits markers, AI scene analysis, second-screen pairing, or full CarPlay/Android Auto UI. The mockups' application sidebars and sample content do not replace Tuvima's current navigation or catalogue rules.

## How to read the attached mockups

The screenshots establish the playback visual language: dark surfaces, restrained elevation and borders, warm orange playback accent, clear large Play/Pause, compact secondary tools, thin progress, and attached-looking sheets. They are illustrative content, not a button inventory or a request to rebuild surrounding pages.

| Mockup | Apply to | Deliberate adaptation |
|---|---|---|
| Music Listen page with bottom bar; audiobook Listen page with bottom bar | Reserved compact dock and responsive control priority | Keep current Tuvima Listen routes, shelves, and navigation; no screenshot-only playlists or discovery sections. |
| Music Now Playing with Lyrics; audiobook Now Playing with Chapters | Large artwork/cover, central transport, contextual side sheet | Use real canonical links, text tracks, chapter data, and existing audiobook edit flow. Expand opens in-app Now Playing; popup remains an optional separate window. |
| Movie subtitles; TV Next Up | Video-first stage, lightweight control overlay, contextual sheet | Hide unavailable options; TV Next Up uses owned episodes; no invented markers. |
| EPUB reading settings | Minimal reading chrome, `Aa`, progress, themes | Keep actual EPUB pagination, bookmarks, highlights, and persisted position. |
| Personal video Info | Video with an integrated facts/actions panel | Keep View provenance, policies, and photo viewer behavior. |

## Current implementation and gaps to preserve or resolve

- `PlaybackSessionController` and `PlaybackModels` already provide a shared Music/Audiobook/Video session, commands, queue, progress, preferences, and a persistent browser audio host. Reuse these. `PlaybackExperience.Video` currently conflates movie and episode; `ListenQueueItem` assumes a non-null catalogue `WorkId`, so View assets need an explicit subject identity rather than a fake work.
- `MainLayout.razor` mounts `ListenNowPlayingBar` and `VideoPlaybackHost` outside the main layout. `ListenNowPlayingBar.razor.css` fixes a roughly 172px bar above page content; the current layout does not reserve its height. Some controls are hard-coded, including a disabled Cast button and text `View Details` action.
- `VideoPlaybackHost` owns one hidden/expanded `<video>` and a separate fixed, full-width minimized dock. It already calls browser PiP, but its current capability and recovery behavior need verification. Its Quality sheet presents current source quality even where no alternate choice exists.
- `PlaybackControlCatalog` has useful shared definitions, but its surface enum does not name Now Playing or PiP, and it returns disabled absent capabilities, including unconditional Skip Intro/Credits. Existing primitive tests assert parts of that behavior and must be revised with the design change.
- `PlaybackToolSheet`, `PlaybackPrimaryButton`, `PlaybackRelativeSkipButton`, `PlaybackRangeSlider`, `PlaybackControlStrip`, and `ListenTransportControls` exist. `ListenPlayerPopupPage` also renders many tools; align it to the shared catalogue instead of cloning a new transport.
- `EpubReader` already supports font, size, line height, margin, local storage settings, Contents, Search, Bookmarks, highlights, and progress. Its shell class is hard-coded `reader-theme-dark`; `ReaderSettingsDto` has no theme or layout field and the appearance action uses a gear.
- `MediaViewerShell` renders personal video with native `<video controls>` and owns an Info panel; the View viewer supplies provenance and authorized actions. This is a separate video UI today.
- The Engine already exposes text-track listing, refresh, import, and preferred-version endpoints; the Dashboard client calls them. `TextTrackDto` has language, timing mode, hearing-impaired, source, and preferred fields. Existing Lyrics loading is less rich than the requested selector/repair flow.
- `docs/architecture/playback.md` describes the controller and transport split, and `clients/apple/iOS/PlaybackCoordinator.swift` has an independent AVPlayer plus remote-command/heartbeat behavior. Keep the native boundary explicit rather than making a Blazor state record the permanent client contract.

The separate [playback/detail recovery plan](playback-detail-review-root-cause-plan-2026-09-27.md) covers in-flight queue safety, startup, and ingestion/read boundaries. Treat its state-cancellation and one-click-start corrections as prerequisites or integrate their landed changes; this plan does not replace those fixes. At implementation time, check the actual working tree before touching any shared file.

## Architecture decisions

### Session, surface, and transport

- **Session/state:** one active audiovisual session owns item identity, queue, position, play intent, profile/device context, and commands. A surface transition changes presentation state only. Switching to another audible item has one defined handoff (stop or pause the old transport, then start the new item) and cannot leave two streams playing. Use request generation/cancellation to reject stale async completions.
- **Presentation:** `Docked`, `NowPlaying`, optional `Popup`, `PrimaryVideo`, `PictureInPicture`, and `Fullscreen` are explicit surface states or host capabilities. `Fullscreen` is video-only. Do not serialize a browser-specific surface as canonical playback progress. The same audio/video element remains mounted through in-app transitions; popup is a control client of the same session, not a second player.
- **Physical transport:** keep browser audio/video calls behind the web bridge and manifest/delivery contract. Native clients implement their own transport and map the shared logical commands to AVPlayer or another host. A reader has reading position/settings, not an audio transport; it shares the visual and accessibility system without being forced into `PlaybackSessionController`.
- **Subject identity:** distinguish catalogue work/asset from an authorized View local asset. The latter resolves its own stream and policy, never passes through catalogue identity/progress endpoints. Prefer a typed subject reference and presenter metadata over extending `ListenQueueItem` with fake IDs. Keep a stable session ID, subject ID, position, and device available for future native/second-screen use, without a visible companion UI now.

### Capability projection and control placement

Make one presenter-facing projection from consumption kind (Music, Audiobook, Movie, TV Episode, Personal Video, Photo/Live Photo, EPUB), item facts, delivery manifest, text-track availability, client/device capability, viewport/input mode, and surface. `PlaybackControlCatalog` remains the authoritative control placement layer; extend its state rather than letting each Razor component guess. Each control definition should carry: key/command, placement and priority, label/ARIA text, availability, selected/active state, value/badge, and an absence reason for diagnostics. The renderer omits unsupported controls. A temporarily loading action can have a truthful busy state; a capability that can be fetched may expose its search action inside the relevant sheet.

| Capability | Source of truth | Visible rule |
|---|---|---|
| Previous/Next track or chapter | Current queue or genuine chapter index | Only if a target exists; audiobook skip interval comes from profile preferences. |
| Captions, lyrics | Embedded/managed `TextTrackDto` plus allowed refresh provider | Show selector for available tracks; offer Find better only when refresh is supported. |
| Audio track, quality | Playable manifest and active host switching support | Only with at least two selectable choices; a source-quality label may appear as information, not a selector. |
| Next Up / previous episode | Owned, playable episode sequence for the current show | TV only; do not use provider catalogue episodes or totals. |
| PiP, fullscreen, device | Runtime host/platform capability and eligible output targets | Show only when actually available; handle rejection without losing playback state. |
| Chapters, intro/credits, Companion | Real chapter/marker/context data | No placeholder chapter, marker, or AI controls. Reserve an optional contextual-sheet slot for later Companion work. |

Layout priority is shared: transport and timeline first; one or two most useful actions on the compact dock; remaining tools in a sheet on phone; richer adjacent panel on tablet/desktop. Control logic should be testable without rendering Razor. Selected value and `aria-pressed`/`aria-current` must agree.

## Technical work packages

### WP0 — Baseline, integration inventory, and visual contract

- Before any build/runtime implementation, follow `AGENTS.md` and stop running Engine/Dashboard `dotnet` processes. Record the current working-tree state and coordinate with the in-flight playback/detail remediation; preserve unrelated edits.
- Capture current flows for music, multi-file audiobook, movie, owned TV episode, EPUB, View video, and photo: start, navigate, expand, close, restore, error, and progress. Inventory entry points, existing tests, popup synchronization, HLS/direct delivery, text-track operations, and native coordinator assumptions.
- Define desktop, tablet, narrow phone, and safe-area visual references from the eight mockups. Specify dock heights and measured layout behavior, touch targets of at least 44px, icon sizes, text truncation, sheet anchoring, focus treatment, orange playback tokens, and high-density artwork renditions using the shared image sizing contract.
- Deliver a short control matrix and before/after screenshots. Resolve any conflict between current purple library accents and orange playback controls by scoping orange to consumption surfaces; do not recolor the rest of the product.

### WP1 — Shared session and capability contract

- Extend `PlaybackModels`/`PlaybackSessionController` with a presentation surface and typed consumption/subject context, while preserving queue/progress/heartbeat behavior. Integrate generation/cancellation guards from the recovery plan so route changes and delayed manifests cannot revive an older item.
- Extend `PlaybackControlCatalog` for surface-specific placement, runtime capabilities, selected values, and hidden unsupported actions. Remove unconditional Skip Intro/Credits and generic disabled device placeholders. Keep command dispatch typed and state changes observable by compact, Now Playing, popup, and video views.
- Define a transport-neutral native contract for identity, artwork, queue/chapter, position, duration, speed, shuffle/repeat, and commands. Map current web state and iOS coordinator into it incrementally; avoid leaking Blazor DTOs or DOM URLs. Maintain compatibility with existing authenticated manifests and progress APIs.
- Specify the single-active-media handoff and what happens on profile switch, authorization loss, device loss, browser PiP denial, and host disposal. Close or pause only by explicit policy, never as an accidental component unmount.

### WP2 — Layout-owned locked audio dock

- Move the Listen dock into one layout-owned bottom region. Use a grid/flex application shell or equivalent measured region so content/rail scrollports, mobile primary navigation, popovers, drawers, and focus scrolling all account for its actual height. Keep the dock visible for any active audio session, including paused sessions, Now Playing, and a separate popup; collapse it only when the session is explicitly closed. A `ResizeObserver`/CSS variable may publish height to the shell, not to each page.
- Refactor `ListenNowPlayingBar` into a compact Music/Audiobook presenter over the persistent audio transport. Keep canonical artist/album links and a book link where known. Use a single expand icon for Now Playing; retain popup only as an optional separate-window action in secondary tools. Remove `View Details` and textual full-player affordances.
- Verify final cards, action buttons, and page bottoms in Home, For Me, each lane, details, Search, Collections, Settings, and View at desktop/mobile sizes. Check open sheets, browser zoom, changing bar height, safe areas, and keyboard focus.

### WP3 — Music and audiobook Now Playing

- Build in-app `NowPlaying` presentation on the existing controller and mounted audio host. Music: artwork, title, canonical artist/album links, timeline, transport, shuffle/repeat, Favorite, Queue, Lyrics, History where useful, Device and Volume where supported. Audiobook: cover, book/author/narrator, chapter identity and timeline, overall book progress, chapter navigation, configured relative skip, Speed, Chapters, Bookmark/History, Sleep, Device, Volume.
- Consolidate compact, Now Playing, side sheet, and popup actions through `PlaybackControlCatalog` and shared primitives. Adapt `PlaybackToolSheet` for desktop side panels and mobile bottom sheets with consistent close, focus return, and active state.
- Retain the existing one-book audiobook queue semantics and chapter title cleanup/edit flow. Do not rename embedded chapter titles automatically. Make progress source explicit: track/chapter position versus whole-book progress.
- Keep Now Playing within the Tuvima application navigation. Expanding/collapsing must preserve element, stream, queue, exact position, and play/pause state.
- Make the navbar's playback indicator open the relevant Now Playing or video presentation directly. Keep the in-app Now Playing region keyboard reachable without trapping focus away from the persistent dock.

### WP4 — Catalogue video and PiP

- Keep one `VideoPlaybackHost` transport for movie and TV and reshape `WatchPlayerPage` as its primary presentation. Replace the full-width minimized video dock with real browser/native PiP where available, plus a compact Restore affordance if needed outside PiP. Never remount the `<video>` just to switch surfaces. Use platform fullscreen and restore to the prior in-app surface.
- Add movie controls by capability: thin timeline, relative seek, Play/Pause, Volume, captions/language, device, PiP, fullscreen; put speed, audio, selectable quality, real chapters, and playback information in the contextual sheet. Put TV series/season/episode/title above the same primitives and add owned-only Next Episode/Next Up.
- Check PiP activation restrictions in the actual target browsers. Attempt automatic transition on navigation only when permitted; if the platform rejects it, keep a truthful resumable primary session and explain the available continuation action. Do not fake PiP with a large floating window or leave hidden video audibly playing without a discoverable restore path.
- Keep delivery selection from the manifest (direct/HLS) and progress/authorization protections. A quality control must switch among actual playable variants, not merely show the current resolution as if it were selectable.

### WP5 — Text-track and lyrics sheets

- Compose embedded, downloaded, and external captions into one selector with Off, language, SDH/forced flags when known, preferred state, and source/version distinction. Reuse `GetTextTracksAsync`, refresh/import/preferred endpoints and the existing durable text-track worker. Refresh is an in-player operation with progress and an accurate no-result state; it is not reingestion or Review Queue.
- Add Find better subtitles and Find better lyrics, preferred-version selection, and timed/static lyric mode when supported by stored track data. Timed lyrics highlight the active line and seek only when timestamps exist. Static lyrics remain readable without fake synchronization.
- Treat per-session subtitle offset as conditional: first verify the web/native renderer can apply it consistently to both embedded and managed tracks. If not, leave it out and document the reason rather than shipping a misleading slider.
- Make the selector accessible and keep selected track/offset stable through video surface changes and HLS refresh where applicable.

### WP6 — EPUB appearance and reading continuity

- Replace the gear with `Aa` in `EpubReader`, keep existing Contents/Search/Bookmarks, and add Theme and appropriate layout to `ReaderSettingsDto`. Implement Light, Dark, Sepia, and System only when client theme observation is reliable. Migrate existing local-storage settings without losing font, size, spacing, margins, or position.
- Apply theme tokens to content and chrome, including focus/selection/highlight contrast. Recalculate pagination after appearance changes while restoring a stable text location; avoid treating a page number as a durable position when pagination changes.
- Keep bottom location/chapter/percentage truthful for the available EPUB data and preserve current bookmark, highlight, search, and progress behavior. Share tokens and sheet conventions with playback; do not route reading through an audio/video transport.

### WP7 — View personal media

- Reuse the shared video control primitives and capability projection in `MediaViewerShell`/`ViewImmersiveViewer`, with a View subject and authorized stream source. Keep the existing Info panel and its provenance, favorite, gallery, download, and policy-gated archive/trash actions.
- Add applicable relative seek, speed, PiP, fullscreen, volume, and real embedded subtitle/audio options. Omit TV/Next Up/catalogue facts. Preserve photo and Live Photo immersive behavior in the viewer with shared visual tokens only.
- Verify that View URLs, thumbnails, metadata, and actions stay under View authorization and never create work IDs, provider lookups, catalogue progress, or Review Queue records.

### WP8 — Responsive, accessibility, native readiness, and documentation

- Implement phone compact-bar priorities and bottom sheets, tablet adjacent panels, desktop columns, safe-area handling, orientation/zoom behavior, and reduced-motion transitions from one control model. No hover-only control or separate mobile playback state.
- Standardize keyboard shortcuts without consuming keys inside text inputs: Space play/pause; M mute; contextual arrows seek/volume; F video fullscreen; Escape closes the active sheet/collapses as appropriate. Add logical tab order, visible focus, slider labels/value text, announced play/pause state, and dialog/sheet focus trap and return.
- Check iOS coordinator compatibility for queue/chapter/remote-command metadata and PiP readiness. Document later lock-screen, headset/Bluetooth, AirPlay, CarPlay, Android Auto, native PiP, and paired-device use as contract consumers; do not build those UIs here.
- Update `docs/architecture/playback.md` to separate session/state, surface/presentation, and physical transport, including dock ownership, video PiP, reader boundary, View subject authorization, and web/native capability projection.

## Delivery order and verification gates

1. **Gate A — contracts and safety:** WP0 then WP1. Integrate the recovery plan's in-flight-request safeguards before surface work. Unit tests prove stale manifest responses cannot replace current media; control matrix tests cover every consumption kind/surface/device combination and hide unsupported controls.
2. **Gate B — listening:** WP2 then WP3. Browser tests verify one-click start, unbroken playback across navigation/expand/collapse/popup, correct canonical links, queue and audiobook tools, and actual shell space reservation. Visual checks cover desktop, tablet, phone, zoom, safe areas, last scroll item, and focus visibility.
3. **Gate C — watching and tracks:** WP4 then WP5. Browser tests verify direct/HLS video, owned TV identity and Next Up, real PiP and restore on supported browsers, honest unsupported behavior, captions/audio/quality conditionality, text-track refresh and preference persistence, and no duplicate media elements.
4. **Gate D — reader and View:** WP6 then WP7. Tests verify appearance migration/persistence and stable EPUB location after repagination; personal-video controls and Info permissions; no catalogue/identity calls for View.
5. **Gate E — integration:** WP8 and final architecture update. Run targeted .NET suites (`MediaEngine.Web.Tests`, API/text-track/contract tests), browser accessibility and responsive flows, then a solution build. Replace brittle source-string assertions in `PlaybackPrimitiveTests` with behavior/component assertions where possible. Keep a concise manual matrix for platforms whose PiP/device behavior cannot be automated in CI.

Release in slices behind the existing UI where necessary, but do not ship a temporary overlay as the dock. Each gate needs screenshots/video and test results against its walkthrough acceptance criteria. Do not claim a device, subtitle offset, quality variant, or PiP mode supported until verified on that host.

### September 28 implementation checkpoint

- Completed in code: the dock occupies a measured layout row; Now Playing reuses the existing audio element and adds Music Favorite, book identity, and author/narrator where known; the persistent video host uses browser PiP when accepted and pauses with a Restore control when navigation cannot enter PiP; TV Next Up is drawn from later owned episodes with playable assets; text-track variants and preferred/refresh actions are in the player; EPUB themes and width persist; View video has common transport primitives and pauses the preceding catalogue session.
- Verified: full `MediaEngine.Web.Tests` suite (1,150 tests), 29 focused API playback/text-track tests, solution build, JavaScript syntax, and `git diff --check`. In the authenticated local browser, the navbar playback action opened Now Playing, the bottom dock remained visible, and its Play/Pause button paused the live audio without closing either surface.
- Remaining for the full walkthrough: broader authenticated desktop/phone playback and reader visual checks; direct/HLS browser checks on supported hosts; runtime TV Next Up validation; popup and native coordinator parity; persistent View video continuation across routes; and a transport-neutral View subject contract. Browser PiP and View track switching must be checked in the actual supported browsers before calling Gates C and D complete.

## Main risks and decisions to resolve during implementation

- **Existing concurrent changes:** the September 27 working tree is not clean, including `PlaybackSessionController`, `MainLayout`, and broad UI/API edits. Re-read and merge against those changes; do not reset them.
- **Browser activation and PiP:** automatic PiP and audible start may be denied after asynchronous navigation. Record real capability/rejection, preserve the session, and expose a clear recovery action. Validate on the browsers the Dashboard actually supports.
- **View identity and authorization:** catalogue `WorkId` cannot stand in for a local View asset. Model the distinction before routing View video into shared playback controls.
- **Text-track truth:** availability, fetchability, and selectability differ. The current API can refresh and prefer tracks, but timed lyric versions, forced flags, host switching, and offset behavior require an asset-by-asset capability audit.
- **Layout interaction:** a shell-owned dock must account for the existing mobile nav and nested scrolling rails. Verify measured geometry and focus scrolling rather than relying on a fixed CSS height.

## Plain-English completion summary

The first implementation slice now gives users a reserved audio dock that remains available in Now Playing, a working navbar shortcut to that screen, honest video PiP and text-track choices, comfortable EPUB themes, and clearer personal-video controls. The music flow was checked in the live app; broader browser, reader, View, and native integration checks are needed before the complete walkthrough can be accepted.
