# Playback surface implementation evidence

Implements the approved [visual correction plan](../plans/playback-surface-visual-correction-2026-09-29.md) under the [Sol implementation contract](../plans/playback-surface-contract-2026-10-01.md).

## Package status

| Package | Source and automated verification | Rendered acceptance |
| --- | --- | --- |
| WP0 architecture | Six references inspected; component, geometry, session and identity contracts recorded. | Available browser surfaces compared against the references; final captures and geometry are recorded in the matrix tracker below. |
| WP1 session and identity | Implemented; covered by the final 1,372 passing Dashboard tests. Covers first projection from a generic audiobook detail action before delayed settings, typed subject authority, stale startup/heartbeat/mode results, canonical destinations, snapshot identity and teardown-aware startup. | Accepted immediate audiobook/music tool changes, canonical artist destination and uninterrupted audio during navigation. Final author/navigation observations are recorded in the rendered appendix. |
| WP2 shared controls | Implemented; covered by the final passing Dashboard suite. Covers inline/modal semantics, bounded artwork/current activity, read-only progress, range recipes, exact fractional speed values and media-specific catalog order. | Accepted fixed row alignment/activity, circle targets, exact 1.25x, phone sheets, Escape/focus return and actual seek increments. OS reduced-motion and 200% zoom remain unverified. |
| WP3 dock and context | Implemented; active queue artwork/activity, fixed row columns, typed music history, canonical current-book history and stale lyrics checks pass. Functional clicks cover Queue→Lyrics and constrained dock More access. | Accepted centered music/book docks at wide and constrained widths, visible 44px actions, compact More access and canonical current-book history. |
| WP4 expanded and mobile | Implemented; component regressions cover transport, synopsis, sheet state and saved workspace preferences. Device settings publish immediately and observer startup/cleanup belongs to its layout instance; the functional JavaScript harness passes. | Accepted artwork-led desktop/phone layouts, unobscured sole-transport dock, cold desktop→phone transition and 320/390/430px fit. Settled paused resize retained the identical native source, exact time 986.724358 and ready-state 4. |
| WP5 popup | Implemented; attached bookmark/history context, shared source-derived rows, canonical identity and single-owner synchronization checks pass. | Direct dock launch exercised. Native opened-window composition/placement/synchronization visual evidence is unavailable from this browser provider; it is not marked visually accepted. |
| WP6 video and choices | Implemented; centered responsive transport, readable primary tools, actual native/HLS caption inventory/selection, automatic cue reservation, truthful Next Up runtime units, authorized View viewer composition and stale episode-title hydration checks pass. | Accepted actual ready/advancing movie, TV and View video, owned Next Up with 24-minute runtimes, real audio/caption choices and multiline native ENG cue clearance above phone identity. Native fullscreen/PiP remain unverified. |
| WP7 integration | Solution restore passed. Final solution build passed with zero warnings/errors. Final Dashboard: 1,372 passed. Focused person authorization: 39 passed. JavaScript: 21 passed. Full solution tests were executed and remain failed; details below. Final strict documentation rerun is recorded after the rendered appendix. | Available browser matrix accepted with explicit native/OS evidence limits; final connected navigation observations are recorded in the appendix. |

## Automated release gates

The final source build passed with zero warnings and errors. The final full solution run used `--no-build -m:1` and reported 4,548 passing tests, 21 failures and 34 skipped tests, out of 4,603 tests. One Dashboard source assertion still expected the superseded caption end alignment. After correcting only that fixture and rebuilding its test assembly with `BuildProjectReferences=false`, the final Dashboard rerun passed all 1,372 tests. The 20 failures in the other suites remain unresolved. No clean-baseline run was performed, so they are not labeled pre-existing. The full solution test gate remains failed; its original run log and final Dashboard rerun are both retained.

| Suite | Passed | Failed | Skipped | Representative unresolved cause |
| --- | ---: | ---: | ---: | --- |
| Dashboard | 1,372 | 0 | 0 | None. Final fixture-only rebuild used `BuildProjectReferences=false`, preserving the app outputs. |
| API | 1,230 | 9 | 0 | Endpoint/SQL allowlists, provider-settings and ingestion source assertions. |
| Contracts | 67 | 4 | 0 | Collection fields and public/wire inventory snapshots differ from approved fixtures. |
| Domain | 313 | 2 | 0 | Shared JSON and first-nonblank helper guardrails identify other source files. |
| Storage | 523 | 4 | 0 | GUID-column fixture, TV provider configuration expectations and SQLite execution guardrail. |
| Providers | 575 | 1 | 34 | Owned TV episode still-download assertion. |
| Ingestion | 218 | 0 | 0 | None. |
| Admin | 5 | 0 | 0 | None. |
| AI | 55 | 0 | 0 | None. |
| Identity | 26 | 0 | 0 | None. |
| Intelligence | 125 | 0 | 0 | None. |
| Performance | 3 | 0 | 0 | None. |
| Processors | 37 | 0 | 0 | None. |

The focused person projection/authorization run passed 39 tests, including the actually credited source-work authorization boundary. The Contracts boundary guardrail checks passed after fixing Razor source scanning and simplifying the artwork helper; its real-bar regression retains both existing playback deserializers without changing approved debt classifications or fixtures. The 21 JavaScript cases cover viewport events, failed and obsolete circuit calls, owner-specific responsive/dock/shortcut cleanup, early observer registration, fresh/cleared popup snapshot handshakes, actual caption inventory/selection, multiline cue reservation and native automatic-placement restoration. Earlier parallel-run settings, temporary-file cleanup, ingestion completion and performance failures did not recur in the final run; this is an observed result, not a baseline attribution.

Machine-readable results and the full run log are under `tools/reports/player-surfaces-2026-10-01/test-results/`. The final rendered matrix tracker is `tools/reports/player-surfaces-2026-10-01/README.md`; it is outside the documentation tree and is recorded as a repository path so strict documentation builds do not interpret it as a missing page.

## Scope and capability decisions

Queue/snapshot identity uses explicit album, artist and playlist IDs. A song opens its resolved parent album rather than gaining a song detail page. Existing audiobook detail credit groups supply the canonical person IDs that earlier queue factories dropped. The detail and track-table paths now carry ordered credited authors/narrators and the canonical audiobook parent identity into the player. Genuinely unresolved credits remain plain text; no ID is guessed from a name or from the broader enrichment graph.

The live artist-link check exposed a music-person projection bug: album-normalized credits were filtered against visible child work IDs. The fix retains the actually credited source work IDs and authorizes those sources before presenting album credit cards. The final focused API run passed 39 tests, including an authorized sibling/denied credited source case; it never promotes an unrelated visible album sibling into permission for a denied credit.

Desktop Now Playing provides artwork and context while its persistent dock owns transport and seeking. Phone and popup players retain their own transport and timelines. Desktop audiobook progress is informational. View video uses the same centered controls inside its existing authorized viewer stage, including with its information rail open.

Audiobook bookmarks retain the actual existing create, replay and delete capabilities and label/note display. Persisted note editing requires a backend operation that does not exist in the current API and is outside this visual correction; no pretend editing control is rendered.

The popup controls the existing audio host. A transition to video clears audio tools and presents a return-to-video action, preserving the main window's video host and View authorization.

Music history filters known music media types without relabeling audiobook or unknown legacy entries. Audiobook history requires verified current-book work and asset membership. For accepted rows, a source chapter must contain the actual saved position before its title is used; stale historical chapter strings are cleared and unresolved positions show the canonical book. Saved session intervals are labeled “Elapsed session,” because they can include pauses; asset runtime is never presented as time listened.

Caption choices reflect real browser/HLS track modes as well as manifest/managed choices. Automatically positioned cues reserve their wrapped text height above visible controls while authored numeric placement remains intact. The actual two-line native ENG cue was accepted above phone identity on the final bundle `app.y3lvaic69b.js`. Bare video detail runtimes represent minutes and become explicit clock values in the queue; unknown/nonpositive runtimes are omitted. The observed rapid-navigation circuit closure had no exception stack in the available logs, so its cause is not claimed. Startup/disposal now uses cancellation and owned registrations; final interactive navigation observations are recorded in the rendered appendix.

## Final rendered acceptance

The recorded matrix accepts 15 available browser-rendered states. The two native popup states remain implemented/source-checked and are explicitly not marked visually accepted. Captures use real library content and the provider's unchanged image output; `geometry.json` records the actual CSS viewport and script bundle for each capture. The final source bundle is `app.y3lvaic69b.js`; earlier accepted layout captures remain applicable where the final targeted caption correction did not change their layout.

Desktop music and audiobook docks, expanded context, fixed-column Queue/Chapters/History panels, phone full/mini players and tool sheets were checked at their intended geometry. Audiobook tools remain visible without horizontal overflow at 320, 390 and 430px. The desktop expanded stage leaves the persistent dock unobscured and as the sole transport. Actual current queue activity was animated while playing, with its paused state static.

Real movie, TV and personal View video reached ready-state 4 and advanced. Actual audio choices and both native ENG caption tracks were selectable; Off disabled both. The final phone capture at 25:26 shows a real two-line native ENG cue clearing the title, timeline and controls. The actual timeline responded to keyboard seek increments. The TV Next Up panel shows only owned successors with their actual 24-minute runtime.

Settled paused phone-to-desktop movie resize preserved the same native source and exact 1526-second position. Settled desktop-to-phone audiobook resize preserved the same native source and exact 986.724358-second position; the phone transport and all five tools rendered without reload. Both retained ready-state 4. The final Now Playing author link opened Andy Weir's canonical person page, showing six real owned works. Audio retained its source and advanced from 986.724358 to 1022.061296 seconds while the page's Listen filter responded. The Shy profile remained active; playback was paused after verification. The repository evidence folder contains `settled-resize.json`, `settled-audio-resize.json` and `canonical-author-navigation.json`.

Connected Settings→Ingestion→run→detail navigation was also exercised with the actual profile and advancing audio. A startup live-updates notice recovered through its visible Retry action. The earlier circuit closure had no diagnostic exception stack establishing its cause; these observations establish final interactive behavior, not a definitive crash-cause diagnosis.

Native popup appearance/placement, browser fullscreen/Picture in Picture, live OS reduced-motion switching and 200% browser zoom remain unverified. No clean-baseline run establishes an attribution for the 20 unresolved broader-suite test failures.

## Final documentation verification

The strict MkDocs build passed after the final rendered appendix and package-status updates. `git diff --check` also passed. The strict-build output is retained in `tools/reports/player-surfaces-2026-10-01/test-results/final-mkdocs-strict.log`. The evidence geometry retains the final capture for each of 23 named images, including the final native-caption bundle and actual paused position.

## Plain-English completion summary

The player layouts and behavior changes are implemented. Listeners have centered, usable docks, artwork-led phone players and consistent panels; desktop Now Playing keeps the dock as its only transport. Real movie, episode and personal video controls and caption choices were checked in the browser. All Dashboard checks pass and the complete solution builds successfully. The full test gate still has unresolved failures, and native popup, fullscreen/PiP and OS-specific visual checks remain unverified.
