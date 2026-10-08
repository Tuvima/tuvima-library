# Player and shared controls: implementation evidence

October 5, 2026. Implemented locally on `codex/player-controls`. This is the first delivery from the [reviewed plan](../plans/player-and-controls-reviewed-plan-2026-10-05.md): player and shared controls first, wider CSS cleanup afterward. The attached specification and six generated screenshots supplied design hints; the user's staging choice, “proceed,” and subsequent icon-only feedback define the authorized work.

## Product walkthrough

1. **Keep listening controls within reach.** The full phone player places artwork, or the selected panel, above persistent progress, transport, mode controls, and volume. Selecting the same mode returns to artwork; Collapse returns to browsing. Lyrics and Queue use icons, tooltips, and accessible names. Audiobooks retain their cover, chapters, bookmarks, history, speed, and sleep tools. This implements WP1, WP4, and WP5. Acceptance is visible transport without outer-player scrolling at the tested small-phone sizes.
2. **Browse with a useful desktop player.** The bottom dock and expanded desktop player share bounded panel content. Dock tools preview on hover and stay open when pinned. Music combines Up Next and History; audiobook chapters/history and bookmarks retain their existing meanings. This implements WP4 and WP6. Acceptance is a stable page width and scroll position while opening or changing a panel.
3. **Choose what plays next reliably.** Upcoming occurrences have move controls, keyboard moves, and drag handles. Current playback cannot be moved. Saved order uses the Engine's existing occurrence IDs and version checks, so duplicated songs remain distinct and stale changes cannot overwrite a newer queue. Explicit order survives snapshot restoration while shuffle is enabled. Clear History confirms before clearing this player's music history. This implements WP3 and WP4. Acceptance is the same current audio and correctly ordered future occurrences after a move or reload.
4. **Retain audiobook and lyric behavior.** Timed lyrics follow real timestamps; static lyrics do not claim synchronization. The bookmark editor retains its captured Add/Saved workflow and can appear within the full player's panel area. Speed and sleep menus remain available through More. This implements WP4 and WP5. Acceptance is preserved position, native playback rate, saved bookmarks, and sleep state when switching presentation.
5. **Offer truthful browser capabilities.** Output selection appears only where permitted device switching is supported. It never requests microphone access. Quality labels require known lossless direct delivery. Operating-system media controls bind to the main audio owner, and player identity links open authorized details at the top while preserving playback. This implements WP7, WP8, and WP9. Automated checks cover capability failures, stale requests, delivery classification, native media-session state, and delayed navigation restoration; physical-device acceptance remains open below.
6. **Use consistent controls throughout the Dashboard.** Selectors, tooltips, sliders, progress, and spinners use the shared App family. Intrinsic series selectors size to the selected title within available width, then provide ellipsis and a full-label tooltip. Full-width forms retain their existing selection semantics. This implements WP2 and WP10. Wider styling removal remains F1–F3 and is not a gate for this first delivery.

Representative journeys were exercised with synthetic music and audiobook media: open a dock panel, change upcoming order without changing current audio, reload the queue, clear player history after confirmation, open timed lyrics, save an audiobook bookmark, change speed through More, and follow the book identity link while the same native audio element remains mounted. The library browsing model, media-card navigation, authorization, ingestion, database schema, and playback ownership stay as before. Casting, remote playback, new detail routes, and broader browse redesign are outside this delivery.

## Implementation

- Shared `AppTooltip`, `AppRangeSlider`, `AppSpinner`, and `AppProgressBar` own the migrated consumers. `AppTypedSelect<TValue>` is the single MudBlazor selector core behind permanent typed/native adapters. Retired tooltip/range component names have no compatibility aliases. Tooltips support hover, focus, Escape, touch cancellation, and viewport collision handling.
- `PlaybackPanelCard` wraps shared context content for music queue/history, audiobook chapters/history, and lyrics. Local full-player mode changes do not create another session controller or audio element. Nested menu registration retains its pinned parent and scopes dismissal correctly.
- The narrow transient command/reply additions provide queue reorder, music-history clear, output enumeration/selection, expected queue revision, and typed output state. Web adapters reuse the existing Engine queue endpoints. Contract fixtures were regenerated and reviewed; no Engine HTTP route, authentication, registration protocol, or database schema was added.
- Queue mutations capture profile, playback request, current occurrence, revision, saved occurrence IDs, and server version. A lost reorder reply causes one read to confirm the resulting order, without repeating the mutation. Failed/unconfirmed mutations preserve the local queue and surface an error. Heartbeats retain current saved occurrence identity.
- Main-window JavaScript integrates permitted audio output and Media Session with the existing guarded command owner. Output results are checked again after asynchronous enumeration/selection. Device changes refresh capability state. Quality derives from current direct delivery and technical metadata, rather than the filename or source album.
- Player detail navigation uses one fresh destination intent, cancels delayed restoration for that navigation, and preserves ordinary Back restoration. Queue keyboard modifiers no longer also trigger the player's volume shortcuts.
- The documentation dependency range now includes patched `mkdocs-material` 9.7.7. Deployment and closure of any remote Dependabot alert were not performed.

## Verification

| Check | Result | Evidence |
| --- | --- | --- |
| Debug solution build | Passed, zero warnings/errors | *Generated QA log removed during documentation cleanup.* |
| Release solution build | Passed, zero warnings/errors | *Generated QA log removed during documentation cleanup.* |
| Complete serial Debug solution tests | 5,034 passed, 34 existing provider tests skipped | *Generated QA log removed during documentation cleanup.* |
| Final Dashboard tests after selector spacing and shell-tooltip migration | 1,700 passed | *Generated QA log removed during documentation cleanup.* |
| JavaScript suites | 84 passed, zero failures/skips | *Generated QA log removed during documentation cleanup.* |
| Release contract tests | 79 passed | *Generated QA log removed during documentation cleanup.* |
| Strict documentation build | Passed with Material 9.7.7 | *Generated QA log removed during documentation cleanup.* |
| Shared-control source inventory | Raw Mud selector/spinner occur only in their shared core; tooltip/slider/linear-progress consumers migrated | Exact tag-boundary search across `src/MediaEngine.Web/**/*.razor` |
| CSS/diff hygiene | No added `!important`; whitespace check clean | `git diff --check` and added-CSS-line scan |

The final Web verification log records the shared-selector layout and last shell-tooltip migration after the complete solution run. New behavior tests cover duplicate queue occurrences, persistence confirmation and lost replies, stale reorder rejection, history scope, audio quality, popup owner/capability contracts, panel layout, nested tools, identity navigation races, output races, and tooltip accessibility.

Baseline Release compilation was already broken by references to Debug-only development helpers. The production setup path is now conditionally separated from the development harness, and the Release Web project excludes the corresponding isolated harness CSS. Six API test files that depend exclusively on excluded Debug fixture helpers are conditionally excluded from Release compilation; they remain included in the full Debug test run. The existing asynchronous sidebar-delete test now waits for completion before asserting. An ingestion timing failure under the first concurrent run passed in isolation and in the complete serial run; its production code and test were not changed.

## Rendered checks

Only the marked disposable fixture at `tools/reports/home-media-cards-visual/fixture-player-controls-2026-10-05` was used for playback and state changes. The configured personal library was not played. The fixture uses generated short audio, timestamped lyrics, and source-authored test audiobook segments; its artificial metadata is not production library content.

Screenshots show actual rendered UI, not the supplied concept images. Viewport dimensions were measured from the page; the in-app browser's zoom required larger outer dimensions to obtain the stated CSS viewport.

| Surface | Evidence and observations |
| --- | --- |
| Desktop, 1920 × 1080 | *Screenshot removed during documentation cleanup; no capture is retained in this repository.*, *Screenshot removed during documentation cleanup; no capture is retained in this repository.*, *Screenshot removed during documentation cleanup; no capture is retained in this repository.*, *Screenshot removed during documentation cleanup; no capture is retained in this repository.*. Bounded dock cards and persistent centered transport. |
| Phone, 390 × 844 | *Screenshot removed during documentation cleanup; no capture is retained in this repository.*, *Screenshot removed during documentation cleanup; no capture is retained in this repository.*. Lyrics/Queue triggers are icons; seek, transport, and volume remain visible. |
| Small phone, 320 × 568 | *Screenshot removed during documentation cleanup; no capture is retained in this repository.*, *Screenshot removed during documentation cleanup; no capture is retained in this repository.*. Measured player height and scroll height were both 568px; volume ended within the viewport. The panel body scrolls internally. |
| Shared full-player component, 420 × 780 | *Screenshot removed during documentation cleanup; no capture is retained in this repository.*. This is the shared component in the phone host at the popout's target geometry; it is not evidence of an actual native popout window. |
| Tablet, 1024 × 768 | *Screenshot removed during documentation cleanup; no capture is retained in this repository.*. Compact dock exposes Queue and More; opening Queue retains the page geometry. |
| Shared controls | *Screenshot removed during documentation cleanup; no capture is retained in this repository.*, *Screenshot removed during documentation cleanup; no capture is retained in this repository.*. The final intrinsic selector has an 8px label-to-field gap. Long selection truncation is detected; choosing the short label shrinks width from about 588px to 205px. Keyboard activation opened the options. |

Live fixture checks retained the current audio source and paused position while moving a duplicate upcoming occurrence, restored saved queue order after reload, cleared two recent music history rows to zero after confirmation, followed the canonical audiobook link with destination scroll at zero, saved a bookmark at seven seconds, and confirmed native audiobook rate 1.3 after using the nested speed menu. Focus on the icon-only Lyrics control exposed the Lyrics tooltip. Final DOM inspection confirmed that both Lyrics and Queue buttons contain no text, retain their accessible names, and have corresponding tooltip text. The unsupported Output control stayed absent and no microphone prompt was requested.

## Remaining platform acceptance and follow-up

- A real separate player window was not exposed by the in-app browser's tab inventory after its opener was activated. The shared component geometry and owner/broadcast behavior have automated coverage; actual native popout interaction remains a manual browser check.
- Physical output switching/disconnection, mobile Safari software-volume behavior, and OS hardware media keys require supported physical devices. Unit checks cover the capability and failure contracts; they do not establish those hardware results.
- Native pointer drag and physical touch long-press require device/browser acceptance. Row-menu and keyboard reorder paths, stable occurrence identity, and JavaScript drag setup are implemented. Keyboard and touch tooltip logic has automated checks.
- F1–F3 remain a separate CSS/interop cleanup delivery. The current Release isolated-CSS bundle is approximately 1.826 MB. The earlier 1,821,432-byte baseline was Debug, and baseline Release did not compile; it is not a valid Release size comparison. No size-reduction target is claimed here.
- There has been no push or deployment. The documentation package fix is local; a remote advisory's closure needs verification in the repository hosting service.

## Plain-English completion summary

The first delivery is implemented locally: users can keep transport visible while viewing player tools, edit what plays next without disturbing the current song, and use consistent controls across the Dashboard. Lyrics and Queue are icon-only with tooltips. Automated checks pass and synthetic-library screenshots document the experience. The broad CSS cleanup and the physical-device/popout checks remain separate follow-up work.
