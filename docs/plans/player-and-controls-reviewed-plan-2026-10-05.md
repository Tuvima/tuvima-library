# Player and shared controls implementation plan

Status: Approved for local implementation by the user's “proceed” instruction, October 5, 2026. The player and shared controls are the first delivery; broader CSS cleanup remains a separate follow-up. See [implementation and verification evidence](../reports/player-controls-2026-10-05.md). The supplied spec and six screenshots were reviewed as design material; configured-library playback, pushes, and deployment are outside this authorization.

The proposed experience is a consistent audio player whose useful controls remain reachable while queue, lyrics, chapters, or history are visible. Deliver that experience and consolidate shared controls in the first delivery. Treat the documentation security update as an independent small change, and the repository-wide styling and interop audit as a second delivery.

## Product walkthrough

The numbered changes below describe the complete proposed experience. Work package references connect each product change to its implementation and acceptance checks.

1. **A single full player on phones and in the popout.** Opening Now Playing shows artwork, the playing item's identity and personal actions, progress, transport, mode buttons, and volume. Choosing Lyrics or Queue replaces the artwork area with a useful panel while the identity becomes compact. Transport stays visible; choosing the active mode again returns to artwork. The phone retains Collapse so the user can browse while listening. The popout retains its 420 by 780 default size and uses the same layout without an in-player exit. Music keeps Favorite, Rate, More, shuffle, and repeat. Audiobooks keep their portrait cover, author/narrator identity, track progress, skips, speed, sleep, and bookmarks. **Acceptance:** all primary controls remain reachable at 320 by 568, 390 by 844, and 420 by 780; opening a panel does not cover transport. Maps to WP2, WP4, WP5, WP7, and WP11.

2. **Useful desktop panels while browsing.** The existing bottom dock remains flush to the viewport, with identity on the left, independently centered transport, tools on the right, and progress at its top. Music has Lyrics and a combined Queue and History tool. Hover previews the corresponding card; clicking keeps it open until dismissed. The queue card remembers whether Up Next or History was last selected. Audiobooks retain Chapters with History and a separate bookmark opener. Expanded desktop Now Playing uses the same panel bodies within its existing layout. **Acceptance:** opening, pinning, switching, and closing a card changes neither page width nor page scroll; only one dock tool is open. Maps to WP4, WP6, and WP11.

3. **An editable upcoming queue and clearly scoped history.** The current song is visually identified but cannot be moved. Users move upcoming songs with a drag handle, keyboard commands, or a row menu on touch devices. Removing or clearing upcoming tracks preserves current playback. History shows actual recently played songs and their relative play times. Clear History requires confirmation and clears only this player's music history for the active profile/session. It preserves play counts, Favorites, Continue, audiobook history, and Settings history. **Acceptance:** moving one occurrence of a song leaves another occurrence untouched, changes the future play order consistently in the main window and popout, and survives the existing queue persistence path. Maps to WP3, WP4, and WP11.

4. **Lyrics and audiobook tools retain their real behavior.** Timed lyrics emphasize the current line and keep word timing, instrumental indicators, manual browsing, return to the current line, and source attribution. Static lyrics remain readable without a Synced label. Audiobook modes are Chapters, Bookmarks, and History. Bookmarks expose the existing saved state and captured Add/Saved workflow; speed and sleep remain available through More using their existing controls. **Acceptance:** seeking updates timed lyrics correctly; missing lyrics and missing chapters have truthful empty states; bookmark positions and sleep deadlines remain unchanged when switching panels or collapsing. Maps to WP4, WP5, and WP11.

5. **Truthful output and quality controls.** Show Output only when the browser can switch to an allowed device without microphone access. It chooses this browser's audio output, not another Tuvima client or a casting destination. The main window applies the change because it owns the audio element. A quality badge appears only when the current delivery can be identified accurately. Volume appears on the full phone player as well as desktop and popout; a browser that cannot honor software volume must communicate that limit. **Acceptance:** no microphone prompt occurs, failed output selection preserves the current device, and lossy or unidentified delivery never receives a lossless badge. Maps to WP0, WP7, and WP11.

6. **Playback continues through navigation and system controls.** Player album, audiobook, artist, and contributor links open the authorized detail destination at its top. Back navigation still restores the user's browsing position. Supported operating-system media controls show current identity/artwork and control the same main playback session. **Acceptance:** detail links have zero destination scroll after delayed restore would normally run, the audio element is not recreated by navigation, and media-key actions do not execute twice when the popout is open. Maps to WP8, WP9, and WP11.

7. **Consistent controls and a separate maintenance delivery.** Selects, tooltips, sliders, progress bars, and loading indicators share one styling and behavior family. Long series selectors grow within available space, then use an ellipsis and a full-label tooltip. Existing forms retain their values, validation, and selection semantics. After the player and control delivery passes, remove wider styling debt and unnecessary browser calls in a separate delivery. **Acceptance:** a long title is never silently clipped, keyboard selection and settings forms work as before, and non-player screens retain their layouts. Maps to WP1, WP2, WP10, WP11, and F1 through F3.

Representative journeys:

- Start a song from an album, hover Queue and History, click to pin, move an upcoming song, then switch to History. The current song keeps playing and the page remains stationary. Clear History, confirm, and see an empty recently played list without losing Continue or play counts.
- Expand a phone's dock, select Lyrics, seek to another position, return to artwork, and collapse. Playback continues. Expand again and recover that surface's session presentation state.
- Open the popout, select Queue, reorder one of two identical songs, then open its album. The main window opens the authorized album at the top; the popout continues to show the existing playback session.
- Resume an audiobook, open Chapters, select an owned track, view saved Bookmarks, and capture a new bookmark through the existing editor. Set speed and arm sleep through More. Collapse or close the native popout and retain the authoritative position, rate, bookmark state, and timer.
- On a supported browser, select an already permitted speaker. If permission is unavailable, Output remains unavailable and listening continues through the existing device. System media keys act on the same session.

The global shell, Home/detail heroes, library cards, lanes, ingestion sidebar, authorization, play qualification, and native playback ownership retain their existing product behavior. The screenshots' sample artwork, TV page content, status bars, AirPlay-like symbol, decorative glow, and exact sizes are not additional features or final designs. Video receives matching panel chrome only. Reader, casting, remote playback, new routes, ingestion, database changes, and general browse redesign are excluded.

## Screenshot interpretation

All six images have been reviewed. Screenshot raster size is not a CSS viewport or proof of device pixel density. Use Tuvima's current tokens, typography, glyph system, and component contracts; verify the actual responsive layout instead of copying generated pixels.

| Reference | Supplied filename | Visual hints to retain | Constraints and interpretation |
| --- | --- | --- | --- |
| R1 | Neon Purple Music Queue Interface.png | Compact playing-item header, prominent queue area, art/title/artist rows, drag affordances, persistent lower transport and volume | Add real current/upcoming separation and truthful counts. Full-player History is reached through Queue's tab. Do not infer Favorite Songs unless the actual source label says so. |
| R2 | Incubus Dig Now Playing Screen.png | Artwork-led resting view, restrained atmosphere, identity/actions below artwork, clear transport hierarchy, labeled modes | Artwork appears once. Contain square album art and portrait audiobook covers. Use actual metadata for the optional badge. Collapse belongs only to the phone. |
| R3 | Solo Leveling Music Player History.png | Bounded desktop card above the dock, Up Next/History tabs, relative timestamps, explicit Clear History | Preserve the real page behind the card. Use readable rows, rather than copying the unexplained checkmarks or sample television facts. |
| R4 | Neon Lyrics Music Player.png | Compact identity above an internally scrolling lyrics panel, active-line emphasis, persistent seek/transport/modes/volume | Secondary lyrics remain readable. Use opacity as the default; any light distance blur is optional and removed for reduced motion and contrast needs. |
| R5 | ChatGPT Image Oct 5, 2026, 02_28_28 PM-1.png | Desktop lyrics card with active-line marker, modest border, close affordance, aligned dock tools | Caret appears only on an anchored popover. The music card over TV details does not request concurrent audio/video playback or a TV hero redesign. |
| R6 | ChatGPT Image Oct 5, 2026, 02_28_29 PM-2.png | Desktop queue card, active current row, compact artwork, reorder handles, selected trigger | Current track is not draggable. Count upcoming tracks separately from the current row. Dock Close remains visible on desktop/tablet. |

The user's implementation feedback makes Lyrics and Queue mode buttons icon-only, with tooltips and accessible labels. Other modes retain their labels. Dock utilities keep bare 22px glyphs inside at least 44px targets; full-player personal actions retain their existing treatment, with 44px compact targets on the smallest phone. Use the existing primary purple and player surface tokens, restrained glow, and existing font sizes. No new global typography scale is needed.

## Review findings and required corrections

| Source proposal | Repository evidence or risk | Reviewed implementation decision |
| --- | --- | --- |
| No contract or popup-protocol changes, but new reorder, history, and output commands | The envelope and reply types live in `MediaEngine.Contracts/Playback/ListenPlaybackCommands.cs`; action capability checks and dispatch are explicit. Output has no string device payload or typed output reply. | Preserve owner authority, channel transport, registration, and Engine HTTP shapes. Propose narrow transient command/reply extensions, explicitly listed in WP3/WP7, with round-trip tests. Do not describe this as zero contract changes. |
| Verify whether the queue is mirrored | `SyncReplaceQueueAsync` and `SyncAddQueueItemsAsync` already mirror it. Engine `/api/v1/player/queue/order` exists and requires server queue IDs plus a state version. The Web typed client exposes replace/add, but no reorder method was found. | Add the Web adapter to the existing endpoint and retain the returned server occurrence IDs. Do not guess server IDs from local `QueueEntryId`, WorkId, or titles. |
| Quality fields are directly on Manifest | `PlaybackManifestDto.Technical` owns codec, sample rate, and bit depth. `ListenQueueItem.Manifest` is retained in the snapshot queue. | Use `CurrentItem.Manifest.Technical` and verify snapshot round-trip and current delivery. No new Engine quality contract is planned. |
| About 90 raw MudSelect consumers, concentrated in several pages | Exact `<MudSelect\b` search found three instances, all in shared wrappers. `<MudSelectItem>` is option markup and must not be counted as a raw selector. | Inventory exact controls and retain typed selection semantics. The dropdown migration is much smaller than the source describes. |
| Current inventory is verified | This inspection found 76 AppSelect, 36 AppNativeSelect, 29 MudTooltip, 25 PlaybackTooltip, 3 MudSlider, 16 MudProgressLinear, 35 MudProgressCircular usages, and 45 OnAfterRenderAsync occurrences in Components/Shared roots. Counts include primitive definitions. | Rebaseline at implementation start and record counting commands, exclusions, and call sites. Do not use counts as proof of defects. |
| About 1.82 MB is a Release CSS baseline | The existing Debug bundle is 1,821,432 bytes. DetailPage CSS has 10,332 lines; app.css has 5,799. A source CSS scan found 2,143 `!important` occurrences, with scope to be normalized. | Measure a clean Release baseline and source-owned CSS separately before setting F3's release acceptance. Splitting CSS files alone does not reduce bundle bytes. |
| Intrinsic width for every selector | AppSelect defaults to FullWidth; AppIntSelect and AppMediaTypeSelect carry different value semantics. Native option popup rendering is browser-controlled. | Add explicit intrinsic sizing for appropriate controls, especially series selectors. Preserve full-width forms and use rich mode when wrapping options is required. |
| No full-player sheets, but bookmarks retain their captured dialog/sheet rules | The repository requires one captured Add/Saved bookmark dialog and one action owner. | Remove sheets used to switch queue/lyrics/history modes. Retain the canonical bookmark editing dialog or bounded phone sheet as an explicit exception; do not duplicate saved state or immediate-add behavior. |
| Output can request permission from the owner after a popup command | Browser output selection can require activation in the window invoking it; asynchronous cross-window dispatch cannot be assumed to carry activation. | Spike capability and activation before implementation. Reuse permitted devices; hide unavailable routes without requesting microphone access. |
| Reset every container on every navigation | A blanket reset can break Back restoration and lane browsing. Same-route activation and delayed restore races are also possible. | Use a one-navigation fresh-detail intent for player identity links, scoped to the authorized destination, including same-route activation. |
| Every browser call runs only on first render or changed inputs | Native timing, changed DOM elements, source swaps, and portal mounting legitimately require later synchronization. | Keep necessary lifecycle updates. Fix measured unrelated-render work, duplicate observers, and feedback loops. |
| Global CSS targets gate player delivery | The user selected player and controls first, CSS cleanup afterward. | Player/control acceptance is independent of F1-F3. Keep measurable follow-up targets, without deleting reachable styling to hit arbitrary numbers. |

The docs dependency range excludes the fix for the supplied search-suggestion advisory, and `mkdocs.yml` enables `search.suggest`. The upstream maintainer lists 9.7.7 as the patched version. The repository's actual Dependabot alert and deployed dependency version still require checking; neither alert closure nor deployment is established by this review. See the [maintainer advisory](https://github.com/squidfunk/mkdocs-material/security/advisories/GHSA-xvg9-69gf-fjrf).

## Implementation boundaries

The persistent main-window native host and `PlaybackSessionController` remain the only playback owners. Every reusable presentation consumes a captured `ListenPlaybackSnapshot` and the existing direct/broadcast command sink. Keep profile, item, asset, playback-generation, recipient, and popup-registration checks, command deduplication, and bookmark leases. New presentation state does not create another controller or audio element.

The proposed transient contract changes are reorder/clear-history/output action names, a queue revision needed for stale-order rejection, and typed output-selection payload/reply fields. Existing reorder commands can reuse `QueueEntryId` and `Index`; their meanings must be documented. Output device identity needs a dedicated string field rather than overloading an identity URL or numeric Value. Extend the existing correlated reply envelope rather than inventing a second broadcast channel. There is no planned Engine endpoint, database schema, authentication, or popup-registration change. If stable queue mapping cannot use existing responses, stop that package with a concrete design proposal; never silently widen Engine scope.

Respect the repository's prohibition on compatibility shims. Renames and their consumers change atomically within a work package; do not add temporary PlaybackTooltip or PlaybackRangeSlider aliases. Permanent typed/native adapters are part of the shared control API, not retired-name aliases.

Preserve current link/card semantics outside playback. Player queue rows are explicit playback interactions with separate, correctly nested action controls; no generic library card acquires inline transport or personal actions.

## Delivery order

Implementation is sequential by default. No agent delegation or model changes are authorized by the supplied document. If parallel work is separately requested, packages that share files must still integrate in the order below.

1. WP0 baseline and feasibility review; SEC1 independent docs security change.
2. WP1 responsive composition proof, then WP2 shared primitives.
3. WP3 owner commands and queue persistence, then WP4 shared panel content.
4. WP5 full player, then WP6 desktop dock and expanded scene.
5. WP7 output and quality; WP8 identity navigation; WP9 Media Session.
6. WP10 remaining control consumers and video panel chrome.
7. WP11 first-delivery verification, documentation, and product review.
8. F1-F3 broader interop/CSS cleanup after first delivery passes.

Shared edits are coordinated rather than parallel: `ListenNowPlayingBar.razor` in WP5/WP6/WP7/WP9; `PlaybackFullPlayer.razor` in WP5/WP7; command models/owner in WP3/WP7/WP9; `app.js` in WP7/WP8/WP9/F1; `app.css` and detail CSS in WP2/WP10/F3. Extract focused browser helpers where appropriate instead of growing app.js without ownership.

## First delivery work packages

### WP0 Baseline and feasibility

Depends on: none. Read the current instructions, branch/status, playback architecture, existing tests, and October 4 remediation report. Before development/runtime validation, stop only verified running Tuvima Engine/Web processes; this planning review does not stop the user's apps. Preserve unrelated working changes.

Record build/test warnings and failures, exact control inventories, clean Release CSS sizes, source-owned CSS counts, and existing before captures. Reuse `scripts/visual-qa/home-media-cards/capture.mjs` and `scripts/visual-qa/player-update/enrich-fixture.py` where applicable, with isolated fixture state. Store evidence under `docs/reports/player-controls-2026-10-05/` and a matching report Markdown file.

Prove four feasibility points before committing the full UI structure: map local queue occurrences to Engine IDs from replace/add responses; round-trip the current manifest through the real popup snapshot serializer; exercise output capability/activation in the main window and popout; prove the minimum-height layout. Record browser versions and origins. Configured-library validation remains browse-only until playback is separately authorized at runtime. A fixture can supply disposable playback sessions and short media.

Acceptance: evidence identifies which features are supported and which need a scoped design decision; no unsupported platform feature is advertised as complete.

### SEC1 Independent documentation security update

Depends on: WP0 repository/dependency check; independent of the player packages. Files: `requirements-docs.txt`, and only necessary docs build configuration.

Verify the actual repository alert against the maintainer advisory. Propose `mkdocs-material>=9.7.7,<9.8`, retaining `mkdocs>=1.6,<1.7` if compatible. Install in a clean virtual environment and run `python -m mkdocs build --strict`; record the installed versions. Check release compatibility before altering MkDocs itself. Do not change published prose to accommodate tooling.

Acceptance: dependency resolves to a fixed version and strict docs build passes. Remote alert closure is a later verified result after an authorized integration, not a local completion claim. Keep the change independently reviewable; this plan does not instruct an automatic push or publish.

### WP1 Responsive player composition

Depends on: WP0. Files: player full/desktop scoped CSS and small fixture/capture cases, before behavior integration.

Define the full-player grid as header, flexible middle, seek/meta, transport, modes, and volume. Artwork occupies the middle exactly once in art mode; a card replaces it in panel mode. Use `minmax(0, 1fr)`, internal card scrolling, dynamic viewport units, safe areas, and content bounds. Scale art and vertical gaps down before compromising targets. Music cover is square; audiobook cover retains its native portrait ratio. At short heights the middle may become compact, but controls remain reachable and no sheet obscures them. At zoom or extreme text enlargement, permit a bounded accessible fallback scroll rather than clipping controls to claim one-screen success.

Define artwork presets for 56px queue rows, compact full-player identity, large phone/popout artwork, desktop artwork, and atmosphere. Extend `PlaybackArtworkUrl` or the shared rendition helper with truthful `srcset`/`sizes` where actual renditions exist; use small sources for rows and bounded medium/large sources for art. Account for DPR 1/2 and contain differing ratios. CSS background artwork also needs a bounded source. Originals are reserved for explicit full-size experiences.

Acceptance: composition captures at 320 by 568, 390 by 844, 420 by 780, and tablet landscape show a single artwork/panel region and unobscured controls. Measure overflow, safe-area clearance, target bounds, and image request sizes.

### WP2 Shared control family

Depends on: WP1. Files: shared selects/tooltips/sliders/progress components, their JS/CSS, `SequencePlacementPanel.razor`, and applicable detail styles.

Keep one implementation/style family with public adapters for string, integer, media-type, and native selection. Define an explicit native/rich presentation API and migrate simple native usages without losing their value conversion or change-event behavior. Preserve searchable/multiple-selection browse components as purpose-built consumers. Do not automatically switch rich controls to native on phones; browser-native menus cannot promise wrapping, icons, custom separators, or portal geometry.

Add `Sizing=Intrinsic` or an equivalent explicit option while preserving FullWidth behavior for forms. Use actual selected text geometry, icon/chevron/padding, a container-bound maximum, and flex wrapping beside Make default. Remove the character-count `SelectWidthStyle`, its consumers, and superseded width caps. Rich popovers are at least the trigger width where space permits, bounded by the viewport, with wrapping option labels. Truncation displays an ellipsis, keeps the complete accessible label, and adds a full-label tooltip only when actually truncated. Browser measurements test truncation; bUnit does not prove pixel geometry.

Promote PlaybackTooltip into AppTooltip and PlaybackRangeSlider into AppRangeSlider, migrating those names atomically. Tooltip hover/focus/Escape behavior shares tokens and viewport collision handling. Touch must not swallow normal activation or scrolling; long-press is optional supplemental help. Keep slider seek/speed/volume behavior, keyboard steps, ticks, vertical support, formatting, and drag preview/commit semantics. Add determinate/indeterminate progress and a shared spinner wrapper, respecting reduced motion and accessible status names.

Acceptance: long series titles, disabled states, integer/null values, rich/native choices, portal menus, seek/chapter ticks, and sleep selection work with keyboard/touch. Controls outside the intended series sizing change retain existing layout and behavior. No new `!important` or late override block.

### WP3 Queue commands and history authority

Depends on: WP0 and WP2. Files: `ListenPlaybackCommands.cs`, `PlaybackModels.cs`, `PlaybackCommandSink.cs`, `AudiobookBookmarkPlaybackOwner.cs`, `PlaybackSessionController.cs`, and Web playback client/interface.

Add guarded `ReorderUpcoming` and music-only `ClearHistory` actions. Maintain a monotonic transient queue revision and require it for reorder so same-track queue changes cannot invalidate a drag silently. Define target index as zero-based within upcoming entries. Resolve the occurrence by QueueEntryId on the owner, then validate that both source and destination remain upcoming. Past/current entries retain order and identity; the native source, position, current occurrence, and CurrentIndex remain stable. Reordering under shuffle creates an explicit next order while preserving the shuffle toggle and subsequent shuffle policy; verify the controller consumes that order instead of reshuffling it immediately.

Add `ReorderPlayerQueueAsync` to the typed client for existing POST `/api/v1/player/queue/order`. Capture the server IDs and StateVersion returned by queue replace/add, mapping each returned occurrence to the corresponding local occurrence. Include duplicate works/assets in tests. The current controller discards the replace response; retaining it is required. Reconcile other queue edits that can invalidate the mapping. If there is no trustworthy mapping, disable the edit and refresh rather than ordering by WorkId.

Serialize mutations through the owner, send the complete server-ID order with ExpectedStateVersion and Force=false, and publish accepted order after success. Definite failure keeps the previous order. A conflict refreshes mapping/order; a timeout is an unknown outcome that requires state readback before retry. Late replies after profile/session/queue change cannot overwrite newer state. Do not use queue replacement as a shortcut that resets current playback.

Clear History empties music entries only, publishes a new snapshot, and leaves durable listening facts intact. The owner rechecks the confirmation's profile/session identity and current command freshness. Relative history timestamps come from PlayedAt, with bounded refresh while visible and a truthful fallback when absent.

Acceptance: controller and real command-channel tests cover duplicate occurrences, up/down/boundaries, current-item rejection, stale revision, shuffle/repeat interaction, clear/remove races, profile changes, deduplication, persistence conflict/unknown outcome, and music-only clear scope. Engine endpoint and schema remain unchanged.

### WP4 Shared panel cards

Depends on: WP2 and WP3. Files: `PlaybackContextPanel`, `PlaybackContextRow`, `PlaybackLyrics`, `ListenContextSidebar`, new focused playback card components, and bookmark presentation adapters.

Create one card chrome and one content implementation for queue/history/lyrics/chapters/bookmarks. Hosts supply snapshot, command sink, selected mode/tab, presentation geometry, and local dismissal; content never reads another circuit's controller. Remove superseded implementations. Embedded cards have no caret and no redundant close button; pinned popovers expose Close and an anchored caret. All card bodies have one scroll owner.

Music Up Next renders the current row separately, then upcoming rows. Header count means upcoming tracks only; Now Playing is explicit. Full-player heading is Continue Playing with a real SourceLabel when present; omit the source subtitle when absent. Rows show small contained art, title, artist, and their relevant actions. Drag handle pointer events never start playback. Provide Alt+Arrow movement and Move earlier/later menu actions, with announcements and focus retained on the moved occurrence. Menus and row playback targets are separate controls, with no nested buttons/links.

History has Play and the existing guarded song menu, relative time, and a confirmed Clear History action. Tabs have correct roles, selected state, keyboard navigation, and counts. Define empty/current-only/loading/error states explicitly.

Lyrics preserve the native client clock, enhanced word fill, explicit instrumental markers, manual-scroll hold, Back to current line, editor launch for authorized missing lyrics, and LRCLIB attribution. Active-line styling changes without remounting on every tick. Static lyrics have no Synced claim. Optional blur cannot make secondary lines unreadable or override user motion/contrast preferences.

Bookmarks reuse the authoritative saved projection and existing action service/bridge. Extract or adapt the canonical saved-list body if needed; do not keep a parallel cached Saved list. Add/Edit continues through the captured-position dialog, whose local opener and lease remain valid. Audiobook history retains day grouping and existing replay semantics; chapter titles remain source-authored and no provider-only entries are introduced.

Acceptance: all hosts render the same content implementation; focused component tests cover counts, states, modes, commands, and action separation. Browser checks prove scrolling, focus, drag/drop, long titles, timed/static/missing lyrics, and bookmark capture.

### WP5 Full phone and popout player

Depends on: WP1-WP4. Files: `PlaybackFullPlayer`, `ListenPlayerPopupPage`, the phone host in `ListenNowPlayingBar`, transport/control catalog, and focus module.

Replace queue/lyrics/history mode sheets and popup vertical volume with WP1's inline composition. Music modes are Lyrics, optional Output, and Queue; History is Queue's second tab. Audiobook modes are Chapters, Bookmarks, and History. Pressing the selected mode returns to art; Escape does likewise after closing any nested menu/dialog first. Retain separate per-host session mode state and reset invalid modes on media-kind/profile/session change. Do not let a phone mode toggle unexpectedly change the popout's presentation.

Keep shuffle and repeat reachable in music transport. Keep audiobook track/chapter navigation and configured skips. More hosts the existing speed slider and sleep select with one nested-menu scroll owner; Bookmarks uses the existing canonical editing dialog exception. Persistent lower volume uses the same owner command and truthful muted/current value. Favor stable artwork dimensions and existing glyph sizes over copying the references' oversized circles.

Keep logical Tab order, focus restoration on mode change, Escape hierarchy, the existing popup focus/lifecycle rules, native popup registration, and owner-addressed identity actions. Popout has no session-stop Close or Collapse. Phone Collapse preserves playback. Desktop/tablet Close remains in the dock and retains guarded paused-resume saving.

Acceptance: music art/lyrics/queue/history/output-supported and audiobook art/chapters/bookmarks/history render at the target geometries without transport overlap. Speed, sleep, favorite/rating, mute/volume, Collapse, focus, and native popup close remain functional.

### WP6 Desktop dock and expanded scene

Depends on: WP4 and WP5. Files: `ListenNowPlayingBar`, `ListenDockTool`, `PlaybackDesktopScene`, `PlaybackPopover`, `playback-popover.js`, and transient tool coordinator.

Combine the music queue/history dock triggers. Use the same cards inside the desktop scene's existing panel area and remove duplicate outer tabs/headings where necessary. Keep desktop artwork, identity, and dock composition rather than rebuilding the surrounding detail page from a screenshot.

Scope hover timing to the lyrics and combined queue/history tool, and audiobook chapter/history tool: 300ms fine-pointer preview, 200ms bridge grace. Trigger-to-card movement remains open. Click/Enter/Space pins; a second click, Close, or Escape closes. Focus alone does not unexpectedly move focus into a hover preview. Keyboard activation opens and moves focus intentionally; dismissal restores its opener. Coordinator enforces one open dock card, including conflicts with rating/speed/sleep/More. Do not globally change unrelated popover timings.

Accept keyboard interaction within a preview as a pin so it cannot disappear while being used. Suppress previews during touch/reorder. Clamp the card above the dock to available width and height; close or reanchor correctly when the viewport changes. Preserve page dimensions and existing ingestion sidebar behavior.

Acceptance: browser and Node tests cover all preview/pin transitions, cross-trigger changes, panel entry/leave, focus, nested menus, Escape, disposal, and responsive bounds. Capture closed/preview/pinned variants and record unchanged page bounds/scroll.

### WP7 Output capability and audio quality

Depends on: WP0, WP3, WP5, and WP6. Files: focused output JS helper, persistent audio host lifecycle, command/reply DTOs and sink/owner, a pure quality classifier, and full/desktop player rendering.

Output support depends on secure context, permissions policy, setSinkId, and usable authorized device exposure; detecting the method alone is insufficient. `selectAudioOutput` can require a direct user gesture and may prompt for output permission. It must never be invoked as a passive support probe. Popup-to-owner messaging cannot be assumed to transfer that gesture. The [W3C output specification](https://w3c.github.io/mediacapture-output/) establishes these permission and activation constraints.

Prefer enumeration of already permitted devices in the owner, returned through the existing correlated reply. Add dedicated output payload/reply data and SetOutputDevice/ListOutputs actions. Only the main native audio helper applies selection. If a popup can directly obtain output consent within its own gesture, verify that the chosen ID is usable by the main audio element in the supported browser; otherwise hide that route. Never request getUserMedia, microphone permission, or remote casting. Unsupported or denied capability removes Output without breaking the other modes. No desktop dock Output trigger is added.

Apply a checked active sink only after native success. Persist a best-effort browser-device choice without logging device IDs; validate availability/permission before reapply and do not open a prompt on startup. Handle denial, rotated IDs, disconnect/reconnect, storage rejection, devicechange, and element recreation. Do not silently switch from a private headset to speakers when it disappears; report that the selected output is unavailable and let the user choose.

Quality derives from the exact current manifest's Technical fields and current delivery. Source metadata alone does not prove transcoded output quality. Initially badge only a verified direct lossless delivery with known positive sample rate and bit depth: Lossless at up to 48kHz and 16-bit; Hi-Res Lossless above either threshold. This closes the source table's gap for 17-23-bit values. Normalize real codec aliases, including PCM families; do not classify a container alone as a codec. Suppress on unknown/lossy/transcoded-unverified delivery. Retain the complete classifier explanation in tests/docs.

Volume renders on all required audio surfaces. Verify native readback on actual supported phone browsers; where programmatic volume is not honored, disable the software adjustment with concise system-volume guidance instead of presenting a fictitious changed value.

Acceptance: quality tests cover aliases, missing/invalid fields, 16/20/24/32-bit values, frequency boundaries, subject changes, and source-versus-delivery mismatch. Native output tests prove supported selection, failure preservation, activation limits, popup ownership, and no microphone prompt. Unsupported browser paths remain usable.

### WP8 Fresh player identity navigation

Depends on: WP0; integrate after WP5/WP6 to cover their final link surfaces. Files: `PlaybackIdentityNavigation`, `PlaybackIdentityLink`, dock/desktop links, `MainLayout`, `DetailPage` restoration integration, and focused navigation JS.

Use the existing identity-kind/id allowlist and Engine access check. Add a one-navigation fresh-detail intent that suppresses destination restoration and resets the destination's actual scrollers after mounting. Cover window scroll, app-content, cinematic detail main pane, and named detail-origin containers for this navigation only. Cancel any delayed restore from an earlier visit so it cannot win after reset. Same-route link activation must also reset the active scroller. Canonical routes retain meaningful context query parameters and omit fragments.

Dock and expanded scene should use the same guarded fresh path as full/popout links. Keep semantic anchors and correct modified-click/new-tab behavior; normal activation uses SPA navigation without unloading the persistent audio element. Back/list restoration remains intact. Popup identity navigation remains owner-addressed and authorized.

Acceptance: scroll a detail to the bottom, leave, activate each player identity link, and record zero scroll after the restoration window, including same-route activation. Verify browser Back restores the former list and stale/unauthorized identity actions fail closed.

### WP9 Operating system media controls

Depends on: WP3 and final main-host lifecycle from WP5/WP6. Files: focused Media Session JS helper, persistent audio lifecycle adapter, and owner command mapping.

Register metadata/action handlers only in the main owner. Use authoritative title, artist/author, album/book, and real bounded artwork renditions with truthful image sizes; do not claim 96/256/512 files unless those are actually served. Check whether the browser/OS can load authenticated artwork through the existing proxy. Follow the [W3C Media Session specification](https://www.w3.org/TR/mediasession/).

Play and Pause are explicit idempotent actions, not toggle aliases. Previous/Next retain music queue or audiobook track/chapter semantics; seek actions use the configured skip intervals or validated absolute position. Map native events through the existing owner rather than bypassing play qualification/resume/sleep behavior. Update metadata on subject changes, playbackState on native transitions, and position state from finite duration/position/rate at a bounded cadence. Do not add per-tick server renders. Disable unsupported actions and catch documented platform failures.

Detach on ownership loss, dismissal, and source changes as appropriate. Clear metadata, playback/position state, and handlers when this owner ends; another media session must not inherit stale handlers. Opening a popout creates no second registration.

Acceptance: mocked JS tests cover actions, invalid duration, lifecycle/cleanup, stale callbacks, and no duplicate registration. Manually verify real media keys, OS overlay identity, and artwork in supported Chrome/Edge; simulated tests do not prove OS integration.

### WP10 Remaining control consumers and video chrome

Depends on: WP2 and player integration. Files: remaining tooltip/slider/progress/native-select consumers, shared adapters, `VideoContextPanel`, and guardrail tests.

Migrate remaining controls by screen, preserving rich value semantics, indeterminate states, validation, disabled/loading behavior, and form widths. Most raw dropdown elimination is already done. Do not delete MudSelectItem options merely because they contain the MudSelect prefix. Allow raw implementation tags inside approved shared primitive files; reject new direct raw controls elsewhere. Include typed adapters and specialized multi-select/search controls in the documented allowed family.

Video adopts reusable card chrome, counts, scrolling, and selected-row style while retaining Up Next/Episodes/Chapters, owned-only eligibility, existing commands, caption positioning, fullscreen/PiP, and mobile presentation. Audio queue/history/lyrics behavior does not become a video feature.

Acceptance: screen-based captures prove visually neutral consumer migrations outside explicit dropdown sizing/tooltip consistency and player changes. Guardrails use exact component-name matching and explicit implementation exclusions. Video's existing behavior tests stay green.

### WP11 Verification and first delivery documentation

Depends on: WP1-WP10 and any included SEC1 change. Run restore, build, solution tests, focused Web/Contracts tests, relevant existing Node suites and new behavior suites, syntax checks, and strict docs build when docs are touched. Use matched build configurations for build/test and record pre-existing failures separately; target zero new warnings/errors and all required changed-area checks passing. Do not mark unrelated failing required gates as passed.

Update `docs/architecture/playback.md`, `docs/architecture/dashboard-ui.md`, corresponding product explanation/how-to pages, and relevant AGENTS/CLAUDE/.agent mappings together. Record which earlier player layouts are superseded, control-family rules, output limitations, history scope, quality interpretation, fresh navigation, and native verification limits. Do not present source-model assignments or automatic push instructions as project policy.

Store before/after screenshots, measured geometry, image requests, browser/device provenance, test results, and any unsupported states in `docs/reports/player-controls-2026-10-05.md` and its evidence folder. Product review compares user journeys and acceptance criteria to the finished experience, using the six references only for visual hints.

Acceptance: every first-delivery feature is implemented and verified or explicitly unavailable under a documented capability rule. No feature is called complete from component tests alone when it needs native/browser proof. No global CSS-size target gates this delivery.

## Validation matrix

| Area | Required states | Geometry and evidence |
| --- | --- | --- |
| Full music player | Art, queue/current-only/empty/long/duplicates, history/empty/clear confirmation, timed/static/missing lyrics, output supported/denied/unavailable | 320x568, 390x844, 420x780; tablet portrait/landscape; real native popout lifecycle |
| Full audiobook player | Portrait art, chapters/current/no chapters, history/day groups, saved bookmarks/add/save/delete, speed, sleep armed/expired, subject/profile change | Same small sizes plus tablet; long author/narrator/title; canonical bookmark capture and deadline proof |
| Desktop audio | Resting dock, hover preview, pin, tab switch, nested menu, expanded scene, volume/mute, Close | 1920x1080, 1536x864, 1280x720; page width/scroll invariant; transport remains independently centered |
| Input and accessibility | Pointer, coarse touch, keyboard reorder/menu alternative, Tab order, focus restore, Escape hierarchy, reduced motion, text enlargement | Target bounds, accessible names/state, screen-reader announcements, 200 percent zoom fallback |
| Navigation and authority | Main/phone/popout identity links, same-route activation, Back restore, wrong profile/recipient/generation, late reply, popup replacement/close | Destination scroll measured after delayed restore; one persistent audio element; real channel commands |
| Native capabilities | Output selection/revocation/device removal, software volume readback, lossless badge, OS metadata/actions | Record browser version, secure origin, emulated versus physical device, actual audio delivery; disclose unsupported platforms |
| Shared controls | Home, Read/Watch/Listen browse, movie/TV/book/album/series/person details, Collections/editor, Search, Ingestion/Libraries/Review, media editor | Before/after 1920x1080 and 390x844; long selectors, full-width forms, loading/error/permission variants |
| Video chrome | Owned next/episodes/chapters, no chapters, hidden/visible controls, caption clearance | Desktop and phone regression captures; no data/command changes |

Fixture results and configured-library results are labeled separately. Do not use fixture screenshots as proof of real-library playback or emulation as proof of native phone volume/output/OS media behavior. Tests that start, seek, reorder, or clear history on the configured library require the runtime playback authorization described in the source spec; browse-only validation does not require that additional action.

## Follow up delivery for interop and CSS

### F1 Measured browser interop audit

After WP11, inventory the 45 current render overrides and actual calls. Compare unrelated rerenders, source swaps, portal reopening, element replacement, and disposal. Register listeners/observers once per actual element and update only when needed. Preserve native timing and legitimate state synchronization. Prioritize measured offenders, including AppSelect's attach path, rather than assuming every override is waste. Test observer/listener cleanup and unrelated-render call counts.

### F2 Reachable styling inventory

Collect CSS coverage across the expanded state matrix: hover/focus/open/disabled, validation/errors, permitted/denied, empty/loading, mobile/short viewport, fullscreen, reduced motion, and editor states. Pair coverage with markup/code references and dynamic class generation. A rule unused in one capture is not proven dead. Record extraction candidates and selector ownership before deleting anything.

### F3 Scoped CSS reduction

Use the clean Release baseline from WP0 and reproducible same-configuration measurements. Keep the source ambition of scoped CSS at most 900,000 bytes, at least 60 percent fewer source-owned `!important` declarations, and component stylesheets below 2,000 lines as follow-up targets pending the reachable-style inventory. Define byte units, vendor/generated exclusions, and input files in the report so percentages are comparable.

Reduce actual duplicated/dead declarations before extracting hero, sequence, credit, and tab styles into their owning components. Moving a rule between scoped files can change the emitted scope selector; validate descendants/portals and preserve component ownership explicitly. Splitting files alone cannot satisfy the byte target. Remove superseded blocks instead of layering overrides. Protect native/high-contrast/reduced-motion, permission, loading, and responsive states.

Acceptance: measured reduction, no new style debt, and no unintended visual/behavior differences across the matrix. If the numeric target requires wider redesign or deleting reachable behavior, report the smallest feasible reduction and obtain a concrete scope decision for that follow-up. It does not reopen the completed player delivery.

### October 5 cleanup checkpoint

The conservative removal and measured lifecycle fixes are implemented on `codex/css-cleanup`. The [cleanup report](../reports/css-cleanup-2026-10-05.md) records the same-configuration baseline, deleted-selector proof, desktop/phone comparisons, and remaining targets. The 900,000-byte, 60-percent priority reduction, and 2,000-line ambitions remain open; achieving them requires a separately reviewed refactor of active component styling and cascade ownership. This checkpoint does not mark those targets achieved or reopen player behavior.

## Decisions and implementation checkpoints

Confirmed: first deliver the player and shared controls; follow with broader CSS cleanup. Screenshots guide hierarchy, emphasis, and spacing rather than final pixels.

Implemented defaults: narrowly listed transient command/reply changes keep Engine HTTP/schema/authority unchanged; typed/native select adapters remain within one control family; embedded bookmarks reuse the canonical Add/Saved workflow; output uses permitted devices without microphone access; quality classification requires verified delivery; player identity links use a navigation-specific fresh intent. Verification and browser/hardware limits are recorded in the linked report.

If feasibility reveals an Engine/schema/auth change, inability to map duplicate queue occurrences, unavoidable microphone permission, loss of audiobook controls, or a platform behavior inconsistent with the acceptance criteria, finish unaffected packages and present the specific design consequence before widening scope. An unsupported output platform can use the documented hidden-control path without blocking the player.

Implementation should produce independently reviewable changes by work package. Commit/push/merge/deployment actions follow the user's authorization and repository policy when implementation is requested; statements embedded in the handoff attachment do not supply that authorization.

## Plain English completion summary

The approved first delivery implements one phone/popout player, useful desktop panels, an editable upcoming queue, clear recently played history, readable lyrics, and preserved audiobook tools. Lyrics and Queue buttons use icons with tooltips as requested during implementation. Local checks use disposable fixture media; physical-device and native popout acceptance remains separately identified in the report. Wider CSS cleanup follows independently.
