# Player surface visual and interaction correction plan

Status: Proposed for implementation. This document is the completed review and plan; it does not change player code.

The six player mockups supplied on September 29, 2026 are the visual references for this pass. The existing playback architecture remains the behavioral foundation. This plan supersedes the visual direction for player surfaces in the September 27 unified playback proposal where it differs from the new navy, blue, and purple references.

## Product walkthrough

### 1. The desktop player feels like one deliberate part of the app

The dock becomes a compact, rounded navy surface with artwork and clickable identity on the left, transport and progress genuinely centered, and comfortably spaced tools on the right. It remains available while navigating. The active track or chapter is easy to recognize. Its controls do not jump or bunch together at normal desktop widths.

### 2. Every adjacent panel feels connected to the player

Queue, lyrics, history, chapters, bookmarks, speed, sleep, and video settings use a consistent surface, header, row alignment, selection treatment, and close behavior. The queue includes the item playing now with a small animated activity mark while audio plays. Other rows start at the same horizontal position. Opening a panel shows the right tools for the current media type immediately, including during a rapid switch from music to audiobook or video.

### 3. Expanded and phone players put the artwork and media first

Music and audiobook players use distinct layouts that follow their supplied screenshots. Artwork supplies a restrained blue atmosphere behind legible text. Music emphasizes album art, song, artist, queue, lyrics, shuffle, and repeat. Audiobooks emphasize the book cover, book and chapter progress, chapter identity, skip intervals, speed, history, bookmarks, and sleep. Titles, artists, albums, books, and relevant contributors open their actual catalog destinations when those identities are known.

### 4. The popout is a first-class way to keep listening

A visible control in the desktop dock opens the popout directly. The same action remains available in the expanded player. Its main player is centered within its own window, with an attached context panel at wide sizes and a usable narrow layout. It follows the same live session; opening it never starts a second independent playback stream.

### 5. Video keeps its video-first presentation

The video surface moves closer to the desktop reference: image remains dominant, identity sits at the lower left, transport is balanced, and captions, audio, speed, quality, and Next Up open coherent settings surfaces when real choices exist. Personal View video keeps its existing viewer authorization and resource handling.

What stays the same: the single audio session, current queue and progress data, supported playback actions, video host and fullscreen behavior, truthful catalog metadata, surrounding library pages, and View access rules. This pass does not add fake media choices or new Cast support, rewrite ingestion or delivery APIs, or redesign reading and EPUB.

Representative journeys:

1. From a music playlist, a listener starts a song, sees it marked as playing in the dock and queue, opens its artist or album, and returns without losing playback.
2. From an audiobook, a listener opens History in the dock or popout, resumes a saved position, opens Chapters, then changes speed. Each panel keeps the same alignment and visible current state.
3. A listener opens the popout directly from the dock, changes to another media type, and sees the correct controls on the first update while audio continues in one session.
4. On a phone, a listener expands music or audiobook playback and can operate the full player and its sheets without clipped artwork, crowded controls, or hidden safe-area content.
5. During a movie or episode, a viewer opens an available audio or captions choice while the picture remains visible; unavailable settings do not appear as working options.

Observable acceptance for these changes: side-by-side reference review at agreed viewport sizes; centered transport independent of left/right dock content; aligned panel row columns; one visible current queue item with a playing or paused state; working catalog links; direct popout launch; correct tools on the first media-type update; keyboard and touch access; no clipped or overlapping controls.

| Walkthrough change | Implementation packages |
| --- | --- |
| 1. Desktop dock | WP0, WP1, WP2, WP3, WP7 |
| 2. Adjacent panels | WP0, WP1, WP2, WP3, WP6, WP7 |
| 3. Expanded and phone | WP0, WP1, WP2, WP4, WP7 |
| 4. Popout | WP0, WP1, WP2, WP5, WP7 |
| 5. Video | WP0, WP1, WP2, WP6, WP7 |

## Review evidence and decision

The current captures in tools/reports/playback-validation/README.md show a dense black desktop dock, small grouped actions, weak background color, a sparse queue/lyrics workspace, indented chapter and history rows, and expanded/mobile player layouts with smaller artwork and tighter controls than the supplied references. The audiobook popout capture also places its controls and History panel differently from the reference. These are visual observations. The exact cause of each failed catalog link still needs tracing through source metadata, queue snapshots, and destination routes.

The review covers every player-owned surface. Existing components are starting points only where their behavior and appearance pass the new visual contract. Shared primitives may be retained as logic while their markup, geometry, and styling change.

## Surface and component audit

| Surface or component | Current finding | Required update |
| --- | --- | --- |
| Desktop audio dock: ListenNowPlayingBar | Three unequal grid regions and a dense tool strip pull transport away from the true center; popup launch exists only in the expanded view. | Separate identity, centered transport/progress, and utility layout; add direct popup action; rebalance artwork, spacing, background, and breakpoints. |
| MainLayout and ContextSidebarShell | Dock and context pane share space inconsistently at some widths. | Define one predictable reservation and edge alignment; preserve page content and profile panel preferences. |
| ListenContextWorkspace | Queue renders only upcoming items; row structures differ across panels and produce visible indents. | Include the current item; use a fixed-column context row recipe for artwork/state, title/subtitle, duration, and action; use the same horizontal inset and dividers. |
| Queue | No clearly marked active row or animated playing icon. | Show exactly one current row, highlight it, animate equalizer only while playing, and provide a static paused/reduced-motion form with aria-current. |
| Lyrics | Current panel is sparse and visually detached from the player. | Apply the shared context shell, stable loading/empty states, readable timed or untimed lyrics, and active-line treatment only when timing exists. |
| Music History | Current content lacks the reference panel hierarchy. | Use the shared date and row grammar, accurate listening events, and direct replay controls. |
| Audiobook History | Current popout capture shows a single oversized card instead of the compact grouped reference list. | Group saved listening events by local day with aligned time, chapter, listened duration, and resume action. |
| Chapters | Current rows visibly indent and selection is understated. | Align title and duration columns, preserve authored order and names, mark current chapter, and support direct starts. |
| Bookmarks | Existing controls need full visual review in both dock and sheets. | Align position, chapter, note, replay, and edit/remove actions; keep empty and long-note layouts legible. |
| Speed | Existing control behavior can remain if accurate, but its popup style differs. | Give presets/current rate a clear selected state; align sheet and desktop panel geometry; update displayed rate immediately. |
| Sleep | Existing control behavior can remain if accurate, but options and states need inspection. | Show Off, configured timer, and end-of-chapter only when supported; expose remaining time and cancellation clearly. |
| Captions and audio tracks | Choice surfaces need a video-reference style and truthful selection. | Use coherent settings rows, current selection, source language/format, and real switchable choices only. |
| Quality and Next Up | Existing generic sheets do not establish the reference hierarchy. | Review options, current state, and loading/empty conditions; Next Up lists owned playable episodes only. |
| Volume | Native range styling differs from the progress control. | Give volume the same visual language, keyboard behavior, and spacing as other ranges. |
| PlaybackToolSheet, PlaybackSheetRow/List | One sheet treatment serves modal, inline, and popup contexts; semantics and padding need review. | Introduce explicit surface variants with appropriate dialog semantics, focus handling, inset, and row geometry. |
| PlaybackControlStrip and PlaybackIconButton | Reuse has made action clusters dense and nearly indistinguishable. | Specify icon size, touch target, label, active state, and spacing by dock, expanded, phone, popup, and video surface. |
| ListenTransportControls and PlaybackPositionRow/List/RangeSlider | Transport and progress scale inconsistently among surfaces. | Establish measured sizing and alignment per surface; preserve one playback action mapping. |
| PlaybackSpeedControl and PlaybackSleepTimerControl | Functional controls lack a common visual treatment. | Rework appearance and state feedback within the new context/sheet recipes. |
| Desktop expanded Now Playing | Artwork, metadata, and tools are arranged as a dark utility view rather than the balanced reference. | Compose music and audiobook layouts separately over bounded artwork-derived navy atmosphere. |
| Mobile expanded Now Playing and sheets | Artwork and controls are too small or compressed for supplied phone references. | Recompose for portrait viewports and safe areas; use large artwork and separated primary/secondary actions. |
| PlaybackMiniPlayer | Needs inspection as an entry and transition surface. | Ensure identity, current state, expansion, and media-type capabilities remain in sync. |
| Popout page and PlaybackPopoutShell | Main content is narrow and visually off center with context; background and attached panel are weak. | Center the player in the usable window, attach a properly sized side panel at wide widths, and use a single-column narrow recipe. |
| Video player and settings tray | Current video is closer than audio but identity/tools do not yet match the lower-stage reference. | Refine overlay placement, transport balance, and settings tray without obscuring video or changing host ownership. |

The PlaybackControlCatalog remains the source of which actions are actually available. Its visual presentation is reviewed above; capability truth must not be replaced with static mockup buttons.

## Technical work packages

### WP0 — Sol orchestration: freeze the visual contract

Measure the six references against current captures 01–19 in tools/reports/playback-validation. Record the intended geometry, art ratio, typography, navy/blue/purple tokens, elevation, focus states, and panel behavior for dock, desktop expanded, phone, popup, video, and attached panels. Review at 1920, 1440, and 1024 desktop widths; 430, 390, and 320 phone widths; wide and narrow popup windows; and fullscreen/narrow video. Resolve choices where a mockup is illustrative rather than a literal dimension. Use bounded artwork renditions and truthful responsive sizes, including high-density displays.

Deliverable: a reference-to-component checklist and screenshot acceptance baseline approved within the implementation team before styling begins.

### WP1 — Sol with Terra: session projection, capability timing, and catalog identity

Trace the subject-change flow in PlaybackSessionController. PlayQueueItemCoreAsync and ReplaceQueueItemsAsync currently assign Experience and PresentationSurface before awaiting settings/position/rate work, then notify listeners afterward. Publish the new subject and capability projection immediately, clear invalid old panels and lyrics, and apply asynchronous position/rate data with a version guard so older completions cannot restore obsolete options. Keep the single audio host and existing playback commands.

Trace artist, album, audiobook, playlist, and other supported player links from source records through ListenQueueItemFactory, session snapshots, dock, expanded player, popup, and destination routes. The presence of conditional anchors does not prove valid IDs. Use canonical destinations where resolvable; show plain text for genuinely unresolved identity rather than a dead link. Verify navigation keeps playback alive.

### WP2 — Terra: shared visual primitives and context row

Redesign PlaybackControlStrip, PlaybackIconButton, ListenTransportControls, PlaybackPositionRow/List, PlaybackRangeSlider, PlaybackToolSheet, PlaybackSheetRow/List, PlaybackSpeedControl, PlaybackSleepTimerControl, PlaybackMiniPlayer, and PlaybackPopoutShell against explicit surface recipes. Build a fixed-column ContextRow for queue, chapters, history, bookmarks, lyrics where applicable, and video lists. Shared logic is welcome; visual parity is the acceptance condition. Separate inline panel semantics from modal sheet semantics. Build the equalizer with a reduced-motion/static state.

### WP3 — Terra: desktop dock and context workspace

Update ListenNowPlayingBar, its CSS, ContextSidebarShell, ListenContextWorkspace, and MainLayout integration after WP2 primitives stabilize. Keep transport centered relative to the dock itself even when metadata or utilities have unequal widths. Add popup launch to the dock. Implement the current queue row and fixed alignment across panels. Preserve valid per-profile panel selections and sizing across navigation and refresh where currently supported.

### WP4 — Terra: expanded and mobile music/audiobook

Give music and audiobook explicit expanded-player compositions rather than stretching the popup layout. Music uses album art, linked song context, artist/album destinations, timeline, transport, and queue/lyrics/shuffle/repeat. Audiobook uses a larger cover, linked book/contributors, whole-book and chapter progress where data exists, skip controls, and speed/chapters/history/bookmark/sleep. Match the supplied phone references through responsive rules, safe-area insets, accessible targets, and artwork-derived color. Do not invent total chapter counts or progress.

### WP5 — Terra: popup

Update ListenPlayerPopupPage, its CSS, the launch bridge, and PlaybackPopoutShell. Request a centered named browser window and center the main player within its usable content, including with a side panel open. Add the dock opener and blocked-popup fallback. Keep live synchronization through the existing same-origin channel/session path and ensure only one audio element owns playback. Test a real desktop browser window as well as the in-app browser; the latter cannot establish the popup's visual acceptance alone.

### WP6 — Terra: video and every context choice

Adjust the video overlay and settings tray toward the video reference, including lower-left identity, centered transport, lower tools, and panel placement. Audit each choice named in the component table in its actual host: docked pane, expanded sheet, popup panel, and video overlay where applicable. Check real capabilities, focus return, scrolling, closing, selection, and narrow width. Preserve View's authorized, viewer-owned video path and supported fullscreen/PiP behavior.

### WP7 — Sol integration and release gate

Sol assigns nonoverlapping file ownership to Terra agents, merges WP2 before dependent surface packages, resolves integration conflicts, and checks the full interaction matrix. Add focused tests for subject-change ordering and stale results, route resolution, current queue state, and popup synchronization. Use visual captures at WP0 sizes and compare them side by side with the supplied references; a functional component that still looks wrong fails this gate. Verify keyboard navigation, focus, 200% zoom, reduced motion, high-density artwork requests, long titles, empty panels, paused state, and rapid music/audiobook/video transitions. Build the relevant .NET projects and run only the tests needed to cover the changed behavior. Stop development hosts before build/runtime validation as AGENTS.md requires.

## Sequencing and ownership

Sol is the architectural orchestrator and final integrator. Terra agents implement bounded packages with exclusive file ownership. WP0 and WP1 establish contracts first. WP2 primitives land before WP3–WP6 surface work; independent surface packages can then proceed in parallel without simultaneous edits to shared primitives. Sol owns the final cross-surface pass and resolves any remaining mismatch against the reference checklist.

The main implementation risk is that one shared visual component will look correct on one surface and distort another. Explicit recipes and screenshots for every host are the control. A second risk is stale asynchronous session data after changing media type; WP1's immediate projection and version guard address it. Popup size and placement can be constrained by browser window managers, so acceptance checks the requested placement and usable centered layout rather than assuming pixel-exact OS window placement.

## Plain-English completion summary

The player review and implementation plan are complete. It names the mismatched desktop, phone, popout, video, and panel components; explains what listeners should see and do; and assigns the update and verification work to Sol and Terra. Player code has not been changed in this planning pass.
