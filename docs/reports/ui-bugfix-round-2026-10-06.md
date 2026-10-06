# Dashboard UI bugfix round — October 6, 2026

Review branch: `codex/ui-bugfix-round`. Baseline: `main` at `011e0d69`.
Implementation and verification are complete for review. Nothing has been merged or pushed.
Native-window and hardware-input checks below remain for a human reviewer.

## Product walkthrough

1. **A clearer video exit and Info control (U1–U2).** View videos now use the same top toolbar as photos, with one Close action and an Info button that visibly toggles its panel. The phone panel starts below the whole toolbar. Closing View dismisses the viewer back to its hosting listing. Watch Close returns to the movie detail or the show detail with the current episode selected. Picture-in-picture hides the duplicate Restore bar while active. The product owner's October 6 decision is that either native PiP exit shows the compact Restore bar.
2. **Readable playback tools (U2–U3).** Captions sit above visible controls. Volume expands along the control rail without moving its neighbors, and speed opens a compact menu. Up Next, episode, and chapter cards share restrained sizing; episode names show their number once and reserve space for Playing/Paused status.
3. **Dock cards stay where users put them (U4).** Clicking Lyrics or Queue & History pins that card across clicks elsewhere and route navigation. The same trigger, Escape from inside the card, or closing the player dismisses it. Opening the other dock card replaces the first. Ordinary nested menus still dismiss normally. Phone full-player sheets retain their existing presentation.
4. **TV browsing starts at the beginning (U5).** A season's first owned episode and its highlight gutter are visible initially. Season and Jump to share a row; ownership and Show missing, when supported, sit below the sequence. The redundant watched-total sentence is removed.
5. **Consistent controls and truthful totals (U6–U10).** My List has a ringed planet icon. Switch state colors match the earlier control design while preserving geometry. Audiobook totals count owned books rather than their chapter files; physical folder totals say Files. Vertical scrolling remains available through media cards. Circular detail utilities share the same outline glyph family and target size.
6. **Continue tells users which episode they will resume (U11).** Home and Watch use the episode still, `S2 E5 · Episode title`, and the show name underneath. Visible completion percentages are removed from cards, while accessible progress labels remain. Focus/hover gently strengthens the artwork-edge progress strip without changing card size.

The work preserves playback ownership, library relationships, existing primary playback actions, and profile authorization. It adds no wire-contract or schema changes, replaces no vendor assets, and changes no design-token values. U12 documents these rules and packages reproducible QA helpers. Observable acceptance is the behavior above, with the remaining platform checks explicitly listed below.

## Defect → cause → fix → evidence

| # / work package | Recorded cause | Final change | Evidence |
| --- | --- | --- | --- |
| 1 / U1 — View toolbar, Info, Close (V1) | Video actions were inside a transient More popover; Info had no pressed state and the panel had a second Close. On phones a fixed panel inset overlapped the taller, two-row toolbar. Older global button rules also overrode the selected color. | Shared photo/video header, one Close, pressed Info state, measured header offset, selected-state styling owned by the control. | [Info keyboard trace](ui-bugfix-round-2026-10-06/view-info-toggle.keyboard.json), [header/color measurements](ui-bugfix-round-2026-10-06/view-info-measurement.json), on/off captures below. |
| 2 / U2 — Watch Close destination | Close rebuilt a generic work URL after shutdown and discarded the episode's parent-show context. An early Close could precede metadata resolution. | Capture the owned show's destination before shutdown, awaiting that identity's existing metadata read when needed; preserve identity guards. | Baseline TV Close observed `/details/work/<episode>?context=watch`; final browser verification returned `/details/tvshow/<show>?context=watch&episode=<episode>`. Playback surface tests remain green. |
| 3 / U2 — PiP duplicate Restore | Restore depended only on collapsed video state; native PiP state was not tracked. | Identity-guarded native enter/leave callbacks suppress Restore while active; either exit retains compact Restore per the user decision. | JS native-event/stale-binding tests and bUnit `PipHidesRestoreUntilEitherNativeExitAndRejectsStaleCallbacks`; [native-browser attempt and limit](ui-bugfix-round-2026-10-06/native-pip-attempt.json). |
| 4 / U2 — Captions below the seek rail (V2) | Late managed cues could miss the initial binding; cue positioning used unsupported `lineAlign` behavior in this browser. Native cue percentages use the video viewport, including letterboxing. | Refresh loaded cues; use native end alignment where supported, otherwise reserve native line-box height above the measured rail. Restore authored placement when controls hide or detach. | Captions at all three sizes, plus late-load, multiline, header-resize and authored-restoration JS tests. |
| 5 / U2 — Volume geometry | Expanded volume occupied a separate position and could displace the control row. | Absolute 100px range beside the mute target; shared range-center alignment. | [1920 focus capture](ui-bugfix-round-2026-10-06/watch-volume-focus-1920x1080.jpg), [2560 focus capture](ui-bugfix-round-2026-10-06/watch-volume-focus-2560x1440.jpg). |
| 6 / U2 — Oversized speed panel | Speed inherited ordinary popover width, heading, and mobile full-sheet behavior. | A dedicated compact speed kind with intrinsic width and no repeated heading. | [Keyboard harness](ui-bugfix-round-2026-10-06/watch-speed.keyboard.json); speed captures at 1920, 2560 and 390. |
| 7 / U3 — Video context parity and episode text (V3) | Video cards used a wider layout; current markers had no reserved column. Number/show identity repeated in row text, and adjacent Razor text emitted literal interpolation fragments. | 400px desktop context card above chrome, bounded responsive imagery, four-column rows, clean episode title and one number, reserved activity marker. | [Up Next](ui-bugfix-round-2026-10-06/tv-up-next-1920x1080.jpg), [phone episodes](ui-bugfix-round-2026-10-06/tv-episodes-390x844.jpg), [chapters](ui-bugfix-round-2026-10-06/video-chapters-2560x1440.jpg), bUnit rendered-string assertions. |
| 8 / U4 — Pinned dock dismissal (V4) | Pointer/focus/Escape listeners applied transient-menu dismissal to pinned cards. Coordinator/item-generation changes could clear them. | Distinguish pinned dock cards from ordinary menus; retain the same profile/audio experience, while nested menus return to their pinned parent. | [Lyrics navigation/Escape trace](ui-bugfix-round-2026-10-06/pinned-lyrics-persistence.keyboard.json), [Lyrics after Read](ui-bugfix-round-2026-10-06/pinned-lyrics-after-navigation-1920x1080.jpg), [Queue after Watch](ui-bugfix-round-2026-10-06/pinned-queue-after-navigation-1920x1080.jpg), JS and coordinator tests. |
| 9 / U5 — TV rail clipping and control placement (V5) | First render scrolled toward the current episode; season arrows assumed multiple items implied scrollability. Missing controls and watched totals occupied the header. | Start at index zero; observe real scroll boundaries, preserve an 8px highlight gutter, put Season/Jump together and optional missing controls in the footer. | [Three-size rail measurements](ui-bugfix-round-2026-10-06/tv-rail-measurement.json) and first-rail captures. Focusing the first link nudges native scroll to 2px; the measured gutter remains 7.797px and Previous is absent/disabled. |
| 10 / U6 — My List identity | Top bar used the old bookmark glyph. | Original 24-unit ringed planet icon with existing name, tooltip, target, and navigation. | Visible in settings, Home, Watch, and detail captures at all three sizes. |
| 11 / U7 — Switch parity (V6) | Geometry already matched the old control; checked/disabled thumb colors and shadow treatment differed. | Restore accent checked thumb, neutral disabled treatment, and the earlier shadow; retain geometry. | [Nine-case before measurements](ui-bugfix-round-2026-10-06/switch-comparison-before.json), [after measurements](ui-bugfix-round-2026-10-06/switch-comparison-after.json), isolated comparison and actual Settings captures. |
| 12 / U8 — Inflated audiobook total (V8) | Owned-media counts counted distinct assets, making chapter files look like books. Some folder columns did not identify their physical-file meaning. | Audiobooks count distinct work IDs across chapters/editions; other media retain asset counts. Folder ingestion totals are labeled Files. | Real SQLite test: eleven audio files across two audiobook works → 2; ten music files → 10; movie → 1; an unowned catalogue work contributes zero. Consumer audit below. |
| 13 / U9 — Wheel/scroll interference (V9) | Shelf guards could restore vertical scroll and run without an active horizontal expansion. | Limit locks to actual horizontal shelf expansion; never restore vertical position or consume vertical wheel input. Wrapping grids have no shelf guard registration. | New JS wheel tests; [Recently Added keyboard-scroll measurement](ui-bugfix-round-2026-10-06/recently-added-keyboard-scroll.json) records content-pane movement 0 → 161px. Hardware wheel is a manual check. |
| 14 / U10 — Mixed hero circles | My List/Rate/More used different icon paths from other playback utilities. | Shared 24-unit, 1.5-stroke outline utility glyphs inside 56px circles, including Add/Check and album Shuffle. | Movie/book/album captures at all three sizes; browser measurements showed 56×56 targets and 24×24 glyph boxes for peer utilities. Primary buttons and rating-choice subcontrols retain their own roles. |
| 15 / U11 — Continue identity and percentage (V10) | Watch showed the show identity while Home used episode details; visible hover percentage was a separate renderer. | Canonical episode-title projection, shared episode/show captions, no visible percentage, 4px resting strip strengthened to 6px and `#A46FFF` on focus/hover. | [Rest/focus measurement](ui-bugfix-round-2026-10-06/watch-continue-measurement.json), Home/Watch captures at all sizes, bUnit single-link and accessible-progress assertions. |

### Findings that were not reproduced

View's baseline Back action invoked the host's local viewer-close callback and returned to `/view`; no browser-history jump was reproduced. That callback remains the owner of closure. The new header removes the ambiguous Back affordance. Final video closure was checked at listing scroll zero, and the shared photo/video shell was checked at nonzero scroll: [355px before and after](ui-bugfix-round-2026-10-06/view-listing-scroll-close.json). A video opened inside a nonzero-scrolled Gallery still belongs on the hand-check list.

The native PiP button did not activate a native window in the embedded browser. The implementation's enter/leave behavior is tested through identity-bound callbacks; native-window behavior is not claimed as visually verified.

### Audiobook count consumer audit

| Surface | Count source / result |
| --- | --- |
| System Overview | Library endpoint → `GetOwnedMediaTypeCountsAsync`; corrected to audiobook works. |
| Global browse / media counts | Library-item endpoint → existing `GetMediaTypeCountsAsync`, already work-oriented for visible canonical data. |
| Direct audiobook browse header | `MediaBrowseShell` uses the work catalogue total; no chapter-count substitution. |
| Listen discovery | Typed work projection; does not sum chapter assets. |
| Library folders | Ingestion/folder snapshot counts physical files; header now says Files. |
| Ingestion summaries | Durable physical-file, track, episode and issue facts remain physical operational counts. |

No DTO addition, chapter regrouping, migration, or playback-controller edit was necessary.

## Before and after

Baseline screenshots come from an isolated source copy of `011e0d69`, built against the same disposable fixture and run on port 5017. Final screenshots use port 5016. They show behavior/layout comparisons, not identical media time or pixel-diff baselines.

| Experience | Before | After |
| --- | --- | --- |
| View video, desktop | [Old toolbar](ui-bugfix-round-2026-10-06/before-view-video-1920x1080.jpg) | [Info on](ui-bugfix-round-2026-10-06/view-video-info-on-1920x1080.jpg), [off](ui-bugfix-round-2026-10-06/view-video-info-off-1920x1080.jpg) |
| View video, phone | [Old layout](ui-bugfix-round-2026-10-06/before-view-video-390x844.jpg) | [Info below header](ui-bugfix-round-2026-10-06/view-video-info-on-390x844.jpg) |
| Watch captions, phone | [Original](ui-bugfix-round-2026-10-06/before-watch-captions-390x844.jpg) | [Above controls](ui-bugfix-round-2026-10-06/watch-captions-390x844.jpg) |
| Watch speed, desktop | [Original](ui-bugfix-round-2026-10-06/before-watch-speed-1920x1080.jpg) | [Compact](ui-bugfix-round-2026-10-06/watch-speed-1920x1080.jpg) |
| TV sequence, phone | [Original rail](ui-bugfix-round-2026-10-06/before-tv-detail-rail-390x844.jpg) | [First owned entry](ui-bugfix-round-2026-10-06/tv-detail-first-rail-390x844.jpg) |
| Home Continue, desktop | [Visible percentage](ui-bugfix-round-2026-10-06/before-home-continue-focus-1920x1080.jpg) | [Episode identity](ui-bugfix-round-2026-10-06/home-continue-focus-1920x1080.jpg) |
| Watch Continue, desktop | [Show-only caption](ui-bugfix-round-2026-10-06/before-watch-continue-focus-1920x1080.jpg) | [Episode plus show](ui-bugfix-round-2026-10-06/watch-continue-focus-1920x1080.jpg) |
| Album utility circles | [Original](ui-bugfix-round-2026-10-06/before-album-hero-1920x1080.jpg) | [Shared outline family](ui-bugfix-round-2026-10-06/album-hero-1920x1080.jpg) |
| Native switches | [Initial comparison](ui-bugfix-round-2026-10-06/switch-comparison-before-1920x1080.jpg) | [Final comparison](ui-bugfix-round-2026-10-06/switch-comparison-after-1920x1080.jpg), [Settings](ui-bugfix-round-2026-10-06/settings-switch-focus-1920x1080.jpg) |

The switch reference reconstructs the pinned MudBlazor 9.0.0 control CSS from `c765bc91` in an isolated comparison page. It is a control-level geometry/color comparison, not a historical full-app rendering. Nine cases cover off/on/disabled across small/medium/large. Track sizes remain 30×10, 34×14, 38×18; thumb sizes 14/20/26; checked travel 20px. The comparison-page layout changed during verification, so only relative control measurements are compared.

### Responsive coverage

Every final capture has adjacent JSON describing geometry, loaded artwork/fonts, overflow, DPR and actual JPEG dimensions. Captures refuse mismatched viewports, changed state, or incomplete images. All accepted captures use DPR 1; high-density-device verification remains manual.

1920×1080, 2560×1440 and 390×844 coverage includes View Info on/off, Watch captions and speed, TV first rail, Settings switches, movie/book/album heroes, and Home/Watch Continue rest and keyboard focus. Volume, chapters, video-context cards and pinned-dock navigation have representative captures as linked above. Pointer hover is represented by keyboard focus where the shared CSS uses the same selector. No hardware-hover, native-window, reduced-motion emulation, or exhaustive three-size panel matrix is claimed.

For native cue positioning, [WebVTT](https://www.w3.org/TR/webvtt1/) defines percentage-based placement within the video viewport. The unsupported-alignment fallback was reviewed against [Chromium's text-track rendering implementation](https://chromium.googlesource.com/chromium/src/+/0aee4434a4dba42a42abaea9bfbc0cd196a63bc1/third_party/blink/renderer/core/html/track/text_track_container.cc) and verified visually in this browser. Multiline estimates reserve conservative space; complex authored cue layouts still merit a native-browser check.

## Verification

| Check | Result |
| --- | --- |
| .NET restore | Passed with existing sibling Wikidata feed. |
| Debug and Release solution builds, warnings as errors | Passed, zero warnings/errors. |
| Full solution tests, serial, 13 projects | **5,124 passed; 34 skipped; 0 failed.** |
| JavaScript test collection, including video support regressions | **159 passed; 0 failed.** |
| Python CSS audit tests | **14 passed.** Expected negative-fixture diagnostics are test inputs. |
| Final CSS/component/dependency guardrails after last CSS change | **15 passed.** |
| Browser route smoke | **34/34 passed**; [route results](ui-bugfix-round-2026-10-06/route-smoke.json). |
| Keyboard harnesses | View Info toggle, Watch speed/Escape/focus restoration, pinned Lyrics/outside Escape/navigation/inside Escape passed. |
| Final Release publish and CSS verification | All six CSS assets passed minification, gzip/Brotli round-trip, fingerprints, dependency exclusions, and local HTTP delivery. |
| Scoped CSS size ratchet | **1,277,846 bytes ≤ unchanged 1,278,063-byte ceiling.** [Published asset report](ui-bugfix-round-2026-10-06/published-css.json). |
| Documentation strict build / whitespace check | **Passed**; strict build and `git diff --check` are green. |

Assertions reflecting the old visible-percentage or Back affordance were updated to assert the reviewed replacement. Accessible labels, single navigation target, stale-identity rejection, one-open dock behavior, and style budgets remain asserted. No assertion/budget was removed to obtain a pass.

QA used the marked disposable fixture only. Temporary runtime data, credentials, auth bundles, binaries, `.tmp`, and Python caches are excluded from the review commits. QA helper source is included. Task-owned app hosts, comparison server, browser tabs and viewport overrides were cleaned up after verification.

## Save points

One specific-file save point was created for each U1–U12. Shared playback JS/component changes and regression fixtures cross unit boundaries; the combined final tree is the verified release. The final report/evidence package follows those save points. The following log is captured after U12 and before packaging this report:

```text
71773f41 U12: document the reviewed UI bugfix behavior and QA helpers
b721a066 U11: unify Continue episode identity and artwork progress
cc029ded U10: unify circular hero utility glyphs
4ab063aa U9: preserve vertical scrolling through media surfaces
9c5e2012 U8: count audiobook works and label physical folder files
13fccd98 U7: restore native switch state colors and baseline geometry
8f743e64 U6: use a ringed planet for the My List action
a304de17 U5: start TV episode rails at the first owned entry
ea6f68b8 U4: retain pinned dock tools through navigation and focus loss
dfd5c01e U3: align video context cards and clean episode identity
bf88df6a U2: fix Watch close destination, PiP and video controls
75aa0f2c U1: fix View viewer header, Info toggle and single Close
```

## Product-owner completion summary and hand checks

Video controls are clearer, Continue cards identify the actual episode, pinned dock cards stay open when browsing, and audiobook totals represent books. TV rails and shared controls now behave consistently. The branch is ready for code review and product acceptance, with no merge or push performed.

Check by hand before release:

- In a supported native browser, enter PiP, then try both Close and Back to tab: no duplicate Restore while active; the compact Restore bar after either exit; the same episode/position retained.
- Open a video from a scrolled Gallery, toggle Info twice, then Close: same Gallery and scroll position.
- Use a real wheel/trackpad over Recently Added, Home, Read Books and Collections; vertical scrolling should remain free, including over cards. Confirm horizontal Home expansion stays stable.
- Hover the first/last TV cards and Continue cards; check their edge glow without clipping or geometry changes. Repeat with reduced motion and a high-density display.
- Try native subtitles with long/multiline authored cues, controls shown/hidden, fullscreen and a View video containing subtitle tracks. The fixture's View video has no native caption track.
