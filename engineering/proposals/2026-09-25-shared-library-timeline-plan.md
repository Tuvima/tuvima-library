# Shared library timeline controls

Status: implemented and verified on 2026-09-25. Delivery notes and verification are below.

## Product walkthrough

1. **One familiar date navigator (T1, T3).** Read, Watch, and Listen timelines adopt the compact right-side date rail used by Photos: consistent typography, purple active marker, viewport alignment, keyboard focus, and small-screen treatment. The existing library sidebar stays unchanged. Covers, album art, item details, and playback behavior remain appropriate to each library; this is not a conversion of catalogue cards into photo tiles.
2. **Years by default, decades when useful (T2, T3).** In Timeline mode, one shared `Group by: Year / Decade` selector appears in the existing Display controls. Year mode shows populated years. Decade mode groups results under decade headings, retains year subheadings, and expands only the active decade to reveal its populated years. Photos retains its Year → Month behavior; catalogue timelines never show months. No repeated lists of empty years or months.
3. **Scrolling and clicking stay synchronized (T1, T4).** Entering another year updates the rail. In decade mode, the new decade expands and the previous one collapses. Selecting a period jumps to matching results, even if they have not loaded yet. Back navigation restores grouping, filters, and the opened item's year. Restoration is period-based, not an exact pixel position within a long year. Changing Year/Decade preserves the current period where possible rather than restarting at the top.
4. **Filters still mean the same thing (T2, T4).** Search, genre, person, year, ownership/status, and scope apply before timeline grouping. Selecting a rail entry navigates; it does not silently add a year filter. Counts describe all authorized matching results, not just the current page. Unknown dates appear once at the end as `Unknown year` and are never guessed.
5. **Logical grouping without extra clutter (T2).** Ship Year and Decade first. Define the shared model to support Century later for long historical libraries, but do not silently change grouping as results change. Genre, author, series, and networks remain existing Browse as modes—not extra timeline controls. Editorial eras require a separate definition and are outside this implementation.

### Representative journeys and acceptance

- **Read:** choose Timeline, filter an author, and switch to Decade. Only decades and years containing matching books appear. Open a book and return without losing position.
- **Listen:** browse albums by year, scroll into an older year, and see the same active-state treatment as Photos. Tracks do not inflate album counts. Audiobooks remain audiobook works, not file segments.
- **Watch:** jump to a TV show's canonical premiere year, not the year of its newest owned episode. Movies retain canonical release-year semantics.
- **Photos:** existing active-year months, date selection, and paging continue unchanged after moving onto the shared rail.
- **Acceptance:** exactly one date navigator and one grouping selector per catalogue timeline; populated periods only; scroll and click agree; unloaded-period jumps work; zero overlap with sidebars, filters, or mobile navigation; no scope leaks in period counts.

## Current implementation and confirmed gaps

- `Components/Pages/ViewTimelineScrubber.razor` owns Photos year/month navigation, active state, and styling. `wwwroot/js/view-timeline.js` measures the content pane and tracks active month sections, but uses Photos-specific selectors and document-wide jump lookups.
- `Components/Browse/AppTimelineResults.razor` owns a separate left navigator, decade disclosures, year grouping, item rendering, and hash navigation. All decades start expanded and include empty years. Active year changes on click rather than following scrolling. It groups `Items` by `SortYear` in ascending order independently of incoming browse sort.
- `Components/Browse/MediaBrowseShell.razor` passes `_cards` into that component, loads pages of 72, and already has album/TV timeline aggregation rules and a typed `YearSemantic` preset.
- `Components/Pages/ListenPage.razor` also uses `AppTimelineResults` for music and audiobooks. These entry points need the same state/control contract as the browse shell.
- Catalogue rail completeness currently depends on loaded items. A shared visual component alone cannot supply truthful full-result counts or jumps to unloaded periods.
- The catalogue renderer declares `sizes="96px"` despite variable artwork sizes; align that image-delivery contract while refactoring, without expanding the task into a card redesign.

## Technical work packages

### T1 — Extract shared navigation and scroll behavior

Introduce `AppTimelineNavigator` and a neutral period model with stable key, label, inclusive start/exclusive end, count, optional children, and unknown-date support. Keep View DTOs and catalogue DTOs behind adapters; do not unify their authorization or metadata models.

Extract an instance-scoped timeline controller: explicit scroll-root and results-root references, period anchors, active-period callback, jump callback, resize handling, and disposal. Avoid global selectors and shared anchor IDs. JS-triggered paging must explicitly render its completion, preserving the recent Photos fix. Ensure the expanded active group remains visible inside a bounded rail; avoid unbounded year arrays for historical date ranges.

Use one CSS/control contract for the narrow desktop rail and touch-sized horizontal small-screen control. Explicitly reserve navigation space; do not overlay covers or collide with the persistent library rail.

### T2 — Shared grouping and URL state

Add typed timeline grouping (`Year`, `Decade`) to `BrowseState`, `BrowseQueryBuilder`, and presets. Use a readable `period=decade` parameter with Year as the omitted default. Validate values, preserve existing query parameters, and use explicit URL state ahead of any stored profile preference. Separate navigational anchor from filters.

Use one grouping function for result headings, navigator periods, counts, and anchor resolution. Decades are calendar decades, e.g. 1990–1999. Keep canonical date semantics from each lane's preset. Choose newest-first as the unified default, allow oldest-first through the existing Sort control, and make the API, results, and rail agree. Remove or hide conflicting nonchronological sort choices only while Timeline is selected; restore normal browse choices on exit.

### T3 — Migrate rendering without duplicating controls

Make `AppTimelineResults` a results renderer with shared year/decade headings and existing media-aware artwork, using the common navigator instead of its private decade list. Add the grouping selector through the shared Display-control slot, not a second toolbar inside results. Wire both `MediaBrowseShell` and Listen's music/audiobook call sites.

Adapt Photos to the same navigator with populated-month children and no new catalogue grouping selector. Remove retired navigator CSS/JS only once all call sites have migrated. Places' horizontal histogram is deliberately not replaced: share generic period utilities only when semantics actually match.

### T4 — Full-result date index and bounded jumps

Trace existing display query/aggregation services before choosing the endpoint extension. Add a catalogue period-index response over the exact authorized, filtered result set; reuse existing canonical-year and album/show/work aggregation logic. Counts must not include invisible libraries, extra editions, episode files, or track segments incorrectly.

Support stable period-anchored pages with canonical-year order and deterministic identity tie-breakers. Jumping loads a bounded window near the chosen year/decade; do not fetch every earlier page. Preserve an item anchor across append, regrouping, and history navigation. Cancel/discard outdated index and page responses when filters, profile, or scope change. Expose loading/retry rather than presenting incomplete counts as complete.

### T5 — Regression and visual acceptance

- Unit: year/decade boundaries, sparse years, unknown dates, ascending/descending order, grouping-state parsing, and per-lane aggregation semantics.
- API/storage: complete period counts across multiple pages, authorization before aggregation, filtered counts, and stable anchored pagination.
- Component/JS: active period follows scroll, active decade alone expands, month children remain Photos-only, explicit render after JS paging, keyboard navigation, multiple timeline instances, and disposal.
- Browser fixtures: books, movies, shows, albums, and audiobooks across decades; >72 results; one year; unknown-only; empty filter; slow/error requests. Use isolated fixtures, not production reseeding.
- Compare Photos and catalogue rails at desktop, tablet, and mobile widths. Verify viewport-height alignment, input/action alignment, touch targets, focus, no horizontal document overflow, preserved cover ratios, and bounded thumbnail requests.

## Delivery order and completion gates

1. Build neutral period model, shared navigator, controller, and tests; preserve Photos behavior through its adapter.
2. Implement catalogue date-index/anchor support and filtered-count tests.
3. Pilot Read with Year/Decade, including back navigation and unloaded-period jumps; review visual parity with Photos.
4. Migrate Watch and Listen, explicitly verifying show/album/audiobook semantics and all direct call sites.
5. Remove duplicate navigator code, complete responsive/browser regression checks, and document any intentionally deferred scope.

Completion means one shared navigation implementation, Year/Decade working across catalogue timelines, unchanged Photos month behavior, truthful full-result navigation, and verified responsive alignment. No new provider enrichment, inferred dates, catalogue month grouping, arbitrary eras, or Places redesign is included.

## Plain-English summary

Use the Photos rail as the common interaction pattern, not as a photo-specific component copied into other pages. Catalogue timelines gain a simple Year/Decade choice, consistent scrolling and jumping, and less duplicated navigation. The underlying date meaning and media presentation stay correct for each library.

## Delivery notes and verification

- **Shared presentation:** `AppTimelineNavigator`, `TimelinePeriod`, and `AppTimelineGroupingControl` are the common controls. Photos is now a thin adapter with populated month children; catalogue views provide year or decade/year children. Retired catalogue navigator styles were removed. Mobile uses a touch-sized period picker plus the active group's children.
- **Catalogue integration:** Read, Watch, and Listen share the right-side navigator, newest/oldest chronological sort, and URL-backed `period=decade`. Timeline mode has one grouping selector and no redundant Cards/List switch. Explicit desktop/tablet/mobile grid positions keep Group by aligned with Display and Sort.
- **Complete navigation:** the existing authorized browse response includes year counts and deterministic offsets over the full filtered result set. Normal page limits remain in place. Albums and TV shows are aggregated before indexing and paging; their tracks/episodes do not inflate counts. Outdated responses are discarded, and failed jumps show a retry message.
- **Scroll and return behavior:** catalogue anchors are instance-scoped; scroll tracking, resize measurements, disposal, and tab-session year restoration are implemented. The generic detail return handler yields pixel restoration to the catalogue timeline. A one-pixel tolerance prevents fractional browser zoom from activating the preceding year. Photos retains its separate paging/scroll adapter; sharing that source-specific controller is not required for the common visual/interaction contract and was not forced into this change.
- **Scope decisions:** the shared period model uses stable numeric keys and children, not date intervals: catalogue canonical years are not capture timestamps. Century/editorial eras remain future extensions. No Places redesign, data reseeding, enrichment, or authorization changes were made.
- **Automated verification:** 1,123 Dashboard tests passed in the final run; 48 targeted display/projection API tests passed; two Node timeline-controller tests passed. Both application builds succeeded with zero warnings/errors. One unrelated profile-edit test failed during an intermediate run, then passed individually and in subsequent full runs.
- **Browser verification:** Read Year/Decade, decade jumps, active expansion, returning from book details to the correct year, Photos' active-year months, Listen album counts/routes, and TV show counts/premiere-year routes were checked against the running development library. Desktop, tablet, and mobile layouts were checked (approximately 1309, 818, and 391 CSS pixels wide under the existing browser zoom); no horizontal document overflow was observed. Desktop rail height grew to the available content viewport after scrolling. Alignment also has explicit responsive CSS and regression coverage. Large/unloaded-year paging was exercised with a 181-item filtered API fixture rather than reseeding the user's library.

**Product-owner completion summary:** browsing by date now feels familiar across the libraries: one compact navigator, a simple Year/Decade choice for catalogue media, and months only in Photos. Dates and counts reflect the media being browsed, and returning from an item brings the reader back to its year.
