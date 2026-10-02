# Player surface visual acceptance

This report compares the approved screenshot references with the actual local library. Covers, chapter names, runtimes, contributors, and available playback choices remain real library data; the supplied mockup artwork is not substituted into the product.

Sol owns the architecture and integration review. Luna workers implement the components. The primary agent records rendered evidence and checks the complete user journeys.

## Product experience

1. Desktop playback has an artwork-led Now Playing view, while the bottom dock keeps the timeline and transport in one place. Artist, album, book, author and narrator links open their resolved library destinations.
2. Queue, lyrics, chapters, bookmarks and history use consistent panel spacing and row columns. The currently playing queue item has an animated activity indicator; paused and reduced-motion states remain static.
3. Phone music and audiobook players have separate layouts. Music exposes Queue, Lyrics, Shuffle and Repeat; audiobooks expose Speed, Chapters, History, Bookmark and Sleep. Sheets close back to their opener.
4. The dock can launch the separate player directly. Movie, TV and personal View video retain their actual hosts and receive centered controls and surface-appropriate choices.

These changes preserve real library artwork, source chapter names, existing playback commands and authorization. Unavailable data produces an honest empty state. The source mockups guide composition and spacing rather than supplying fictional library content.

## Acceptance matrix

The recorded captures include the accepted visual build and the final targeted correction build. `geometry.json` records each image's actual viewport and loaded script bundle. States with remaining browser verification limits are explicit below.

| State | Evidence | Verification |
| --- | --- | --- |
| 1. Desktop music dock, no sidebar | [Capture](01-desktop-music-dock.jpg) | Accepted at 1280×720; centered transport, compact identity and direct popup/expand/close actions. |
| 2. Desktop music Queue and Lyrics | [Capture](02-desktop-music-queue-lyrics.jpg) | Accepted at 1920×1080; shared title columns and one current row with actual animated activity. Lyrics retains its real empty state. |
| 3. Desktop audiobook dock and context | [Capture](03-desktop-audiobook-context.jpg) | Accepted at 1920×1080; source Chapter 12 active, Chapters/Bookmarks workspace and compact tools without the former phantom gap. |
| 4. Desktop music Now Playing | [Capture](04-desktop-now-playing-music.jpg) | Accepted at 1920×1080; source artwork atmosphere, canonical identity links and the dock as the sole transport. |
| 5. Desktop audiobook Now Playing | [Capture](05-desktop-now-playing-audiobook.jpg) | Accepted at 1920×1080; source artwork ratio, author/narrator hierarchy, informational whole-book progress and unobscured dock. |
| 6. Music popup | Source/automated verification; native visual check unavailable | Direct dock launch was exercised. The browser provider cannot expose the native opened window; visual centering is not marked accepted. |
| 7. Audiobook popup and history/bookmarks | Source/automated verification; native visual check unavailable | Attached/narrow recipes, canonical history and single-owner bridge are covered by tests. An isolated popup-route tab lacks the owning window's session and is not visual proof. |
| 8. Movie controls | [Capture](08-desktop-movie-controls.jpg) | Accepted at 1440×900 with actual ready/advancing video and centered transport. Native browser fullscreen remains unverified. |
| 9. Movie audio/captions overlay | [Audio](09-desktop-movie-audio-panel.jpg), [Captions](09-desktop-movie-captions-panel.jpg) | Accepted at 1440×900 with actual advancing video. Both native ENG tracks appear; selected state matches browser modes. Off disables both, and either track is independently selectable. |
| 10. TV episode Next Up | [Capture](10-desktop-tv-next-up.jpg) | Accepted at 1440×900 with actual ready/advancing S1 E1. Owned S1 E2/E3/E4 successors show their real 24-minute runtime. |
| 11. Personal View video | [Capture](11-view-video-lightbox.jpg) | Accepted at 1440×900 with actual ready/advancing personal video, centered transport and open Info rail inside the existing authorized viewer. |
| 12. Ingestion context sidebar | [Capture](12-ingestion-context-with-dock.jpg) | Accepted at 1440×900 with the 410px nonmodal Ingestion rail and active dock. Settings→Ingestion→run→detail remains interactive under the actual Shy profile while the same audio advances. |
| 13. Phone music | [Full player](13-mobile-music-full.jpg), [Queue](13-mobile-music-queue.jpg) | Accepted at 390×844; 76px play, 48px transport, four primary tools and secondary History, modal queue, no horizontal overflow. |
| 14. Phone audiobook | [Full player](14-mobile-audiobook-full.jpg), [Speed](14-mobile-speed.jpg), [Sleep](14-mobile-sleep.jpg) | Accepted at 390×844; book/chapter progress, five tools, exact 1.25x and actual sleep choices. Cold desktop→phone resize works without reload. |
| 15. Phone audiobook bookmark/history sheets | [Bookmarks](15-mobile-audiobook-bookmarks.jpg), [History](15-mobile-audiobook-history.jpg) | Accepted at 390×844. Escape restores focus to Bookmark; create/replay/delete covered by API/session tests. Verified history now derives Chapter 1 at saved 0:34 rather than displaying the stale stored TV title. Elapsed session is accurately labeled. |
| 16. Phone mini player | [Capture](16-mobile-mini-player.jpg) | Accepted at 390×844; 84px compact continuation and direct expansion. |
| 17. Narrow video controls/settings | [Controls and native cue](17-narrow-video-controls.jpg), [Captions](17-narrow-video-captions.jpg) | Accepted at 390×844; all tools and choices fit. The final bundle shows an actual two-line ENG cue at 25:26 clearly above the identity, timeline and tools. Keyboard seeking changes the actual position. |

## Evidence limits

The available in-app browser does not expose native windows opened by `window.open`. Chrome/Edge and native app control were unavailable in this session. Native popup placement/composition, browser fullscreen and Picture in Picture are therefore unverified. The direct popup action and single-owner handshake are source/automated checks, not a substitute for the missing window capture.

Live OS reduced-motion switching and 200% browser zoom were not verified. The activity indicator's static paused/reduced-motion behavior is covered in CSS and component regressions. Actual high-density geometry was recorded at device pixel ratio 1.1; bounded artwork delivery remains source-derived. Keyboard Escape/focus return was exercised on the phone Bookmark sheet.

The browser provider captures are softened and can include a dark strip at the right or bottom at this device scale. The raw DOM measurements in `geometry.json` identify the actual CSS viewport; requested physical dimensions are not reported as the tested CSS dimensions. Screenshots are retained as returned by the provider. No fake playback snapshots or replacement mockup artwork are injected.

## Measured geometry and behavior

- At 1920×1080, desktop expanded content ends at 968px and the dock begins at 976px. The dock remains above the expanded stage and fully operable.
- The lane music dock follows the real 256px rail. Its 1920px capture starts at 272px and spans 1623px; transport is centered within the dock rather than the whole browser. The wide audiobook dock starts at 16px and spans 1879px.
- Wide audiobook Speed, Chapters, More, Expand, Popup and Close use 44px targets and approximately 6px gaps. Content sizing replaces the phantom five-column strip. Constrained music More retains Queue, Lyrics, History, Mute and Volume.
- Desktop music context uses three 72px slots with 16px gaps. Queue rows share 40px leading, flexible identity, 56px duration and 44px action columns. One row has `aria-current` and its three activity bars have an actual running animation while playing.
- At 390×844, full-player primary play is 76px and secondary transport is 48px. Book progress is informational; chapter position is the seek control. No horizontal overflow was measured.
- Additional audiobook captures at [320×800](18-mobile-audiobook-320.jpg) and [430×928](19-mobile-audiobook-430.jpg) retain all five tools without horizontal overflow. Vertical scrolling remains available on the smaller phone.
- Artist navigation was exercised through the actual player link: the resolved 10 Years person page presented four owned albums while the same audio remained ready and its playback time advanced. Book author/narrator links resolve to canonical person routes.
- The final audiobook author link opened Andy Weir's canonical person page with six real owned works. Its Listen filter responded while the same ready-state 4 audio source advanced from 986.724358 to 1022.061296 seconds. Playback was paused after verification; the [navigation record](canonical-author-navigation.json) preserves those observations.
- A settled paused movie resize from phone to desktop retained the identical source and exact 1526-second position. A settled audiobook resize from desktop to phone retained the identical source and exact 986.724358-second position, with all five phone tools appearing without reload. Both retained ready-state 4; [movie](settled-resize.json) and [audiobook](settled-audio-resize.json) records preserve the measurements.
- Movie and personal View evidence use actual ready-state 4, advancing video. TV Next Up uses only owned successor episodes. Empty lyrics/bookmarks stay empty rather than displaying mockup content.

## Release status

The application implementation and rendered matrix are tracked separately from the full solution's automated release gate. Exact final test results and unresolved failures are recorded in `docs/reports/playback-surface-correction-2026-10-01.md` and this folder's `test-results/` files. A passing build or Dashboard suite does not imply a passing full solution gate.

Fifteen available browser-rendered states are accepted. The two native popup states are implemented and automatically checked, but their window appearance remains unverified. The final solution build has zero warnings/errors, all 1,372 Dashboard tests and 21 JavaScript checks pass, and 20 failures remain unresolved in the other full-solution suites.

## Plain-English completion summary

The updated players have artwork-based backgrounds, centered controls, spaced tools, aligned panels and working library links. Current queue activity and media-specific choices update with playback. Real phone captions clear the title and controls, and resizing or following an author link preserves the playing item and position. The app is running with playback paused for review. Native popup appearance still needs a browser that exposes its window, and the wider automated test gate has 20 unresolved failures.
