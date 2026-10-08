# View Library remediation and visual acceptance plan

Status: approved to proceed; initial remediation implementation completed September 24, 2026. The complete release acceptance gate is **not** yet satisfied.

## Implementation checkpoint

Implemented: explicit Add/Enter tag commits with draft retention on failure and stale-item protection; shared measured Photos/artwork row layout; pane-height Photos rail with existing-period jumps and observer cleanup; unified Places day-position scale, live range drafts and interactive bar details; actual resolution queries; full-query photo/video totals and matching place-search predicates; centered map controls with a non-overlapping mobile layout; Without location moved into Filters; folder-file viewer opening; Gallery pagination, toggling selection and capability-driven shared removal toolbar. Scope changes clear old map results; obsolete atlas responses are ignored.

Verified: Engine build succeeds; 14 targeted Web tests, 4 discovery Storage tests, 9 discovery API tests and 3 JavaScript row-layout tests pass. The full Web run passes 1,114 of 1,115 tests; `UiShellRenderTests.SettingsPage_RendersSharedSidebarAndAdminOverviewContent` fails at line 196 with a stale bUnit event-handler ID, also reproduced separately. The initial run's three shared-control/typography guardrail failures were corrected. Settings code was not changed.

Rendered evidence: inspected desktop, tablet and mobile layouts through the browser. Zoom SVG center offsets measured zero; after correction, mobile controls end around y=295 while the chart begins around y=512. Histogram selection exposes date/photo/video counts; Month changes the returned buckets; the Photos rail ends 20px above the content-pane bottom. A portrait remains inside its canvas. Entering a complete draft tag leaves saved tags unchanged; the draft was closed without saving. Browser viewport overrides were reset. The current dataset has only ten sparse photo dates, so this is not dense-fixture visual signoff.

Remaining before full-plan completion: isolated representative harness expansion and screenshot baselines; full-date selection across server paging (current accessibility copy explicitly says loaded items); bounded long-history DOM; server-paged Without location results (still a 500-item client-side scan); adaptive bounded histogram aggregation, crowded-callout handling and calendar tick refinement; exact People drilldown; complete viewer/navigation/permissions and contributions matrix; cross-lane storage and canonical relationship-search audit; Gallery/source-specific video/compound navigation parity. Existing personal data and tags were not cleaned up or reseeded. The existing harness targets the default profile, so it was not rerun as an isolated test fixture.

This plan supersedes earlier visual-completion claims for the affected View experiences. Passing builds and source assertions do not establish screenshot fidelity or correct interactions. The current review is a source-level audit against the supplied references, not a claim that every runtime journey has been exercised. Runtime reproduction and measured baselines are the first implementation gate.

## Plain-English product walkthrough

### 1. Photos: a full-height date navigator, not a short second list — T1, T2

Photos keeps its search, scope, density, date headings, normal scrolling, and selection. Its right-edge year rail spans the usable height of the photo pane, with years distributed down that rail and a purple indicator tracking the date currently visible. It stays in place while photos scroll. Years never require an independent tiny scrollbar. A short history remains legible; a long history progressively reduces labels instead of squeezing them together.

Journey: scroll through photos, click an older year, choose a month, open a portrait, and close it. The page returns to that photo with its prior filters and position intact. Date selection uses the hover/focus checkmark, never “Select 5.” Selecting a date highlights the actual matching tiles.

Acceptance: the rail uses the available pane height; month popovers are not clipped; selected dates and displayed photos agree; the bottom action tray does not obscure date navigation.

### 2. Places: a correctly aligned, informative timeline — T3

Keep the flat world map. Rebuild the bottom timeline as the reference shows: compact count summary, a wide histogram and range track, date callouts above the handles, tick labels below, and a separate right-hand column for Year/Month/Day and Reset. All date positions come from one scale, so a handle, its pointer, its bar boundary, and its tick refer to the same instant.

Hovering or focusing a bar shows its period, total, photos, and videos. Dragging shows the proposed dates immediately; applying the range updates results without recreating the map. A single year shows meaningful months; a single month shows days. No duplicated endpoints or fabricated activity bars.

Journey: inspect August's count, narrow the selected period, choose Seattle, and open a photo from the side panel. Reset restores the complete filtered history without losing unrelated filters.

Acceptance: endpoints and pointers align within two CSS pixels at test widths; bars provide mouse, keyboard, and touch detail; a single-day library remains usable.

### 3. Places: truthful counts and stable controls — T4

Replace “mapped memories” with “items on map,” accompanied by photos/videos. These are media items, not separate memory objects or events. Move “Without location” into Filters as a secondary way to find items that need a location; do not put a text label or count outside the map-control stack. Omit the zero-count shortcut.

The plus and minus buttons use identical square targets with centered icons. Thumbnail markers remain anchored to their coordinates when hovered, selected, resized, or shown beside the details drawer. At world scale show continent-level labels; reveal countries, states, and cities progressively.

Acceptance: displayed totals reconcile under every filter; Seattle remains at Seattle during hover; both zoom icons are centered; provider attribution stays readable and compact, never removed.

### 4. Editing: typing is not saving — T5

Typing in Tags searches suggestions and edits a draft only. A suggestion click, Enter, or an explicit Add action commits one complete tag. Blur does not silently create a tag. “Summer vacation” becomes one tag, not a succession of letters or partial strings. Existing single-letter tags are not automatically deleted because they may be intentional.

The viewer stays visually simple: Description → Details → Location/map → People → Tags → Source files. Portraits fit inside the available screen. Information starts enabled and remembers the user's preference between images and sessions. Location and capture date have inline edit controls; people can only be selected from identified people.

Acceptance: typing alone makes zero tag-write requests; one deliberate commit creates exactly one tag; failed saves are recoverable; a delayed response cannot modify the next photo's visible state.

### 5. One consistent View experience across entry points — T6

Photos, folder media, Gallery contents, People results, Places results, and artwork browsing share the same visual language and viewer foundations. Artwork has its own relevant filter types and authorized actions, not an extra preview experience. Folder hierarchy, Gallery ownership/rules, and personal-versus-canonical media boundaries remain intact.

Journey: open a folder file, find a person's photos, add selected photos to a Gallery, inspect artwork from an editor, then cancel inspection. Navigation is functional, selection stays visually consistent, and inspecting artwork never links it until explicitly confirmed.

Acceptance: each visible control has a real action or an honest disabled explanation; every media entry point opens the correct viewer context; removing a Gallery member is distinct from deleting its source file.

### 6. Review the result with representative data — T7

Review all surfaces using repeatable harness fixtures with dense and sparse dates, varied locations, video and compound assets, and different image shapes. Compare rendered screens directly against the references before marking a phase complete.

Scope boundaries: no new face recognition, inferred identities, public sharing, or automatic location inference. No original files are rewritten or production tags deleted as part of visual cleanup. Wider catalogue relationship search and per-lane storage reporting are audited as existing requirements; unsupported backend work is explicitly estimated, not simulated with UI-only data.

## Evidence and gap ledger

Paths below are relative to the repository root. Confirmed findings refer to inspected source; other checks remain implementation-stage verification.

| Area | Evidence / current gap | Required response |
|---|---|---|
| Photos rail | `Components/Pages/ViewTimelineScrubber.razor.css` uses `max-height`, content-sized children, and `overflow-y:auto`, without a definite full-height rail. | Define pane geometry and one bounded rail; avoid independent rail scrolling and clipped popovers. |
| Places chart | `Components/View/ViewPlacesTimeline.razor` separately derives range points, histogram buckets, and axis ticks. Histogram spans are `aria-hidden`, with no detail interaction. | One scale and accessible bucket model. |
| Range labels | Chart CSS clamps callout centers separately; calendar gutter and track origins differ. | Shared plot rectangle; independently clamp label box while preserving pointer anchor. |
| Tag persistence | `ViewImmersiveViewer.razor` sets `CoerceValue=true` and connects `ValueChanged` to `TagSelected`, which adds and saves. `AppAutocomplete` forwards value changes directly. | Separate draft/search from explicit commit; reproduce exact event sequence in browser. |
| Map totals | `MediaEngine.Storage/ViewDiscoveryRepository.cs` counts mapped/unmapped without the location search predicate used for hotspots. | Shared query semantics and complete totals independent of capped marker results. |
| Missing location results | `ViewPlacesPage.razor` fetches at most 500 items then filters missing coordinates client-side. | Server-side missing-location filter and cursor paging. |
| Grid geometry | Photos and artwork use different flex-based implementations and clamped aspect ratios. | Shared measured row layout and image-fit policy, not successive page-local CSS patches. |
| Folder/People journeys | `ViewFoldersPage.razor` file articles and `ViewPeoplePage.razor` cards have no opening action in inspected markup. | Functional media opening and person-scoped results with authorization. |
| Gallery parity | `ViewGalleryDetailPage.razor` reuses the photo timeline but exposes a separate Remove button instead of the shared selected-item treatment. | Capability-driven shared selection toolbar, preserving Gallery semantics. |
| Zoom icons | `AppIconButton` includes a tooltip wrapper; Places applies its own rectangular sizing. Exact rendered cause is not yet measured. | Inspect wrapper/button/SVG bounds; fix locally without changing every app icon. |
| Full View coverage | Prior source/build checks do not cover all runtime routes, responsive sizes, mutation failures, or data volumes. | Route-by-route acceptance matrix and evidence pack. |

Web component paths above are under `src/MediaEngine.Web/`. Storage paths are under `src/`.

## Count and location contract

- Current “mapped” SQL means both latitude and longitude are non-null. “Unmapped” means either is null. It does not mean the file is unavailable, the person is unidentified, or an item belongs to a special Memories collection. A text location such as Tokyo can exist without coordinates.
- Proposed map eligibility requires finite, valid latitude/longitude. Zero latitude or longitude is valid; do not discard coordinates merely because a value is zero. Out-of-range data needs an explicit invalid-location state and repair path.
- Count distinct logical photo/video items. Live Photo or RAW+JPEG components do not multiply the count. Define other media separately rather than silently including them in a photo/video total.
- Overall map summary uses authorized scope plus search, kind, favorites, and selected time range. It is not calculated by summing a capped marker response. A selected-place drawer reports that place's subset. If viewport counts are exposed, label them “in this area.”
- Histogram context uses the same non-time filters over the available history so users can expand their range again. Selected-range totals are separate and clearly named. Available-year navigation obeys the same relevant filters.
- “Without location” uses server-side filtering and full paging, with actionable location editing for authorized owners. Place-name search does not magically supply GPS; no silent geocoding of an entire library.

## Technical work packages

### T1. Measured shell and reusable visual primitives

Measure the actual content pane after app navigation, toolbar, drawer, and safe-area insets. Use a definite-height shell with `min-height:0` and one intended content scroll owner. Keep the date rail outside the scrolling photo content but inside its measured layout. Reserve a non-overlapping gutter for the browser scrollbar.

Use ResizeObserver for pane changes, including sidebar changes and browser zoom; disconnect observers/listeners on disposal. Define shared spacing, selected-state, timeline-node, icon-target, drawer, and toolbar tokens. Avoid fixed viewport offsets that assume one header height. Keep scope/filter controls centered within the available pane, not the entire window including the drawer.

### T2. Photos and shared media row layout

- Build explicit justified rows from source dimensions and measured width: desktop target 160px, typical operating range 150–170px, gaps 6–8px, final row allowed short. Sparse groups must not inflate to fill the width.
- Preserve image proportions. Use cover only for ordinary photographic/poster cases where small crop is acceptable; contain for logos, transparent images, title treatments, banners, and icons. Extreme-wide maximum boxes must contain the full image, never distort it. Share the layout engine across artwork browser/editor pickers and photo-oriented surfaces with surface-specific adapters.
- Spread year positions across the full rail; use adaptive label sampling for long histories and month detail on hover/focus. For one year, show that year and available months rather than repeated labels. No hidden rail scrollbar.
- Use stable item/date anchors for scroll tracking and jumps. Prefer scrolling to loaded anchors; fetch bounded windows when needed without losing selection or corrupting back navigation. Include bounded DOM/windowing work for long sessions.
- Define one capture-date grouping/timezone policy, handling unknown dates explicitly. Avoid duplicate headings/IDs from differing offsets on the same displayed date.
- Date select-all covers the entire matching date in the current authorized/filter scope, including paging boundaries. Resolve IDs server-side or use a selection token; render loaded tiles from that selection state. Never show “all” when only a partial page is selected.
- Center the compact, single-background bulk toolbar within the media pane; use taller, clearly labeled actions. Preserve selection states and accessible touch controls.

### T3. Places histogram and both timeline layouts

Create a tested time-scale model with domain start/end, calendar-aware bins, and one date-to-position mapping. Histogram bins carry start, exclusive end, total, photos, and videos. All geometry uses the same measured inner plot width, including thumb-center offsets. Remove flex max-width behavior that prevents bins filling the chart.

The calendar icon sits outside the plot coordinate system. Tick labels use collision-aware sampling. A callout can shift to avoid clipping, but its pointer remains attached to the handle; overlapping callouts combine into a readable range. Year/Month/Day chooses calendar aggregation, never secretly changes mode without updating the selected control. Sparse/single-period domains receive safe surrounding context without fake counts. Remove arbitrary day-history truncation and bound aggregate responses appropriately.

Hover/focus/tap detail example: “August 2024 · 12 items · 9 photos · 3 videos.” Empty periods say zero; do not draw a minimum-height activity bar. Add keyboard range controls, accessible values, and a textual date-entry alternative. Local drag state updates immediately; commit an atomic start/end range on release or deliberate keyboard action. Cancel stale requests and retain the map instance/camera while results load.

Rebuild Place Story rows using shared explicit columns: date, rail/node, thumbnail, content. Draw connectors behind nodes, keep month headings outside rows, and align each node to the associated thumbnail. No absolute offsets tied to description length. Use concise titles, times, descriptions, and applicable media icons; unknown information stays absent rather than invented.

### T4. Map correctness, controls, and data contract

Centralize scope/filter predicates and distinct-item totals in discovery queries. Add paged missing-location results, valid-coordinate handling, and consistent range/count semantics. Test search and marker-limit discrepancies explicitly.

Keep MapLibre's coordinate-positioning transform on the outer marker; apply hover decoration only to an inner element. Never override the positioning transform with CSS scale/translate. Test markers at Seattle, Tokyo, equator, high latitudes, and the antimeridian through hover, zoom, drawer resize, and selection. Respect longitude/latitude order and cluster expansion bounds.

Inspect actual zoom-control computed layout. Give tooltip wrapper, button, and icon predictable centered geometry; use identical targets at least 44px square, consistent SVG sizing, and no hover padding shift. Assert icon centers within one CSS pixel of their target centers. Preserve flat projection, appropriate world fitting without squishing, progressive labels, reduced motion, attribution, tile failure fallback, and accessible result browsing.

### T5. Viewer and safe metadata editing

Introduce an explicit tag input contract: draft text, suggestion search, selected suggestion, and commit are separate. Debounce/cancel suggestion reads; disable coerced-value persistence. Trim and case-insensitively deduplicate on commit. Support multiword text, Unicode, paste, IME composition, Enter, Escape, and explicit Add. Do not commit on ordinary keystrokes or blur. Keep People list-only.

Serialize tag mutations or use backend revision checks to avoid lost updates. Snapshot target item IDs and ignore stale responses after navigation. Show saving/error states and restore confirmed data on failure. Inspect existing tag provenance for a user-reviewed cleanup option; never bulk-delete short tags based on length alone.

Audit the viewer from every entry point: fit-to-screen portrait/video behavior, default/persisted drawer preference, compact section order, working map tile, date/location edit/reset persistence, permissions, source-file grouping, previous/next paging, keyboard focus return, zoom/fullscreen, video and Live Photo playback. Similar-media discovery stays outside the core info drawer; Activity is not restored without meaningful supported history.

### T6. Full-surface functional review and parity

For every route test loading, empty, populated, error/retry, readonly/shared, unauthorized, narrow-screen, and keyboard states. Inventory every displayed action and trace its API/persistence behavior.

| Surface | Required review/remediation |
|---|---|
| Photos | Search/facets, density, date rail, paging, selected-date behavior, favorites/archive/trash, upload, viewer context. |
| Folders | Source hierarchy, breadcrumbs/back navigation, offline/linked sources, include-in-Photos policy, file opening, recursion/search and pagination. |
| Galleries | Create/edit/manual/smart rules, readable rule summaries rather than raw JSON, membership pagination, viewer, shared selection, reorder/share permissions; remove membership vs delete asset. |
| People | Identified-person eligibility, functional person-to-media drilldown, matching counts, search cancellation, paging, no implication of unimplemented recognition. |
| Places | Tiles, coordinates, clustering, filters, histogram, missing-location workflow, Story drawer, viewer and count reconciliation. |
| Artwork and editor selectors | Shared layout/viewer, functional movie/person/role/source filters, entity relationship search, truthful dates, explicit link confirmation, no personal-media mutations. |
| Contributions / Shared Library | Policy visibility, submit/cancel/review/status/retry, accepted transfer states, pending/declined ownership unchanged, read-only behavior. |
| Shared shell and storage | Consistent scope/profile switching, stale-state clearing, responsive rail, truthful free-space reporting per backing volume. Do not sum the same drive twice; show unavailable data honestly. Cross-lane storage work gets a separately bounded shared-shell package. |
| Search and tags | Real authorized suggestions and persistence; literal personal metadata search. Audit canonical artwork entity/character aliases independently—do not inject catalogue identities into personal View ingestion. |

Keep a disposition ledger for each gap: confirmed defect, supported-but-inconsistent feature, missing backend capability, or future scope. Trace actor/character/work search to canonical relationships, not fabricated tags. Additional backend capability requires a reviewed package before implementation.

### T7. Harness, visual evidence, and release gates

Use a clearly identified, isolated test profile/library. Seed through supported ingestion/fixture paths so Photos and Places show the same authorized assets. No production-library reseeding or destructive cleanup.

Fixtures cover: dense multi-row days; 20+ years; one year/month/day/item; no dates; empty periods; varied offsets/leap days; nearby and worldwide GPS; missing/invalid GPS; text-only locations; photos/videos/Live Photos/RAW+JPEG/XMP; portrait/square/wide/logo/transparent assets; >500 missing-location items; page-split dates; permissions, offline files, and slow/error responses. Use synthetic or licensed imagery with provenance. Add a large metadata fixture for bounded queries/DOM behavior without huge original downloads.

Verification layers:

1. Unit tests: calendar domains/bins, position mapping, row geometry, date grouping, tag commit/dedup, selection state.
2. API/storage tests: shared predicates, complete counts, compound deduplication, missing-location paging, authorized metadata mutations and manual override persistence.
3. Browser interaction tests: typing without writes, explicit tag commit, fast navigation during saves, rail jump/scroll, range drag and tooltips, marker hover stability, Gallery/folder/person opening, profile switching.
4. Rendered comparisons at 1440×900, 1600×900, 1920×1080 and a narrow/mobile viewport; repeat with drawer open/closed, browser zoom, keyboard focus and touch-size controls. Compare matching data/layout regions to the supplied references, documenting deliberate differences.
5. Quantitative checks: full available rail height; aligned timeline anchors within two pixels; icon centers within one pixel; marker anchor stable within one pixel on hover at unchanged camera; no clipped callouts, horizontal page overflow, or overlapping controls. Contrast/focus and reduced motion remain usable.
6. Network review: thumbnails use bounded renditions; no originals in compact surfaces; no mutation per tag keystroke; no unbounded asset requests; range changes do not recreate the map.

## Delivery sequence and review checkpoints

1. **Baseline and urgent tagging fix (T5, T7):** reproduce event sequence, capture screenshots/current geometry, fix explicit commit and race handling, add behavioral regression tests. Review tag cleanup separately.
2. **Photos foundations (T1, T2):** measured shell, full-height rail, row engine and selection. Product review of Photos at reference sizes before spreading primitives.
3. **Places correctness and fidelity (T3, T4):** unify scale/query contract, implement bar detail, align Story rows and map controls, verify stable map. Product review of multi-year and single-period cases.
4. **Cross-surface completion (T5, T6):** apply shared primitives, close confirmed navigation/action gaps, verify viewer and metadata from every entry point. Review any newly discovered backend work explicitly.
5. **Release acceptance (T7):** complete route/state matrix, fixture-driven browser evidence and affected builds/tests. No phase is “complete” on source assertions alone. Record unrelated failures separately rather than silently waiving them.

## Product-owner review decisions

Recommended defaults for approval: use “items on map”; put “Without location” in Filters; commit tags only explicitly; use adaptive calendar detail for short histories; preserve the screenshots' two distinct timeline designs rather than forcing Photos and Places into one visual control. Share data/geometry primitives where useful, not their entire presentation.

Completion means the layouts visibly match the intended hierarchy, the controls actually perform their stated actions, and edits/counts remain correct across navigation and filters. Initial implementation is underway; the full acceptance matrix is not yet complete.

### Follow-up: quick filters and Folders

- Reproduced Videos crashing when an empty result removed the timeline anchor. Fixed observer teardown, null/disconnected-anchor handling, and late responses from previous filter/paging requests.
- Browser-verified All → Videos → Favorites → Archive → All: empty states remain usable and All restores the ten current photos. This live profile has no videos/favorites/archive items, so nonempty filter fixtures remain part of the release matrix.
- Folders now uses the shared Photos timeline/gallery, selection treatment, viewer, and discovery search controls. Fixed CSS isolation on folder/source buttons and breadcrumbs; preserved source identity, nested navigation, recursive browsing, pinning, and timeline policy.
- Checked folder source contents at desktop, tablet, and mobile widths; mobile document has no horizontal overflow. The follow-up count audit is complete: sources and child folders count distinct logical items, not companion/duplicate file paths, and exclude hidden/trashed items consistently. Browser verification shows 10 in both places.
- Superseded verification: the final full Web suite now passes, including the previously failing Settings test; see the completion checkpoint below.

### Completion checkpoint — September 25, 2026

**Product-owner summary:** Favorites/Videos no longer break the Photos surface when results are empty. Folders shares the photo browsing presentation and reports items consistently. People opens the matching photos; People, Folders, Galleries, and Places can continue through paged viewer results. Places explains missing coordinates as **Without location**, uses **items on map** for combined photo/video totals, and exposes period details on the histogram. Long date ranges retain all counts without rendering an unbounded chart. Date selection includes matching photos beyond the loaded page; long Photos sessions use bounded windows while retaining selection.

Implemented and verified in this follow-up:

- T2: date-wide selection across API pages, stale search-result protection, 600-item Photos window with explicit continuation, retained selection on date navigation, and truthful bulk-action failure handling.
- T3/T4: calendar-aligned ticks, edge-safe range labels, visible narrow histogram bars, real exclusive bucket ends, bounded aggregation (at most 180 bars), valid-coordinate classification, and authorized cursor paging for Without location. Stale story requests cannot overwrite a newer selection.
- T5/T6: exact person-evidence drilldown, paged viewer navigation and Live Photo URLs on additional entry points, distinct folder counts, scope-aware folder reloads, and capacity authorization/stale-response protection.
- T7: an isolated 720-item storage fixture spans roughly 25 years, includes 520 items without coordinates, and verifies complete paging and count-preserving aggregation. It does not seed or modify the user's originals.

Final automated results: **1,115 Web tests, 167 View API tests, 14 targeted storage tests, and 4 JavaScript regressions passed**. `git diff --check` passed. Both runtime applications were rebuilt through the test projects and restarted successfully.

Browser evidence: source/folder totals reconcile; People opens the exact matching image; Places bar details and the selected-place drawer work; desktop 1600×900, tablet 900×900, and mobile 430×900 layouts were inspected. The final rebuilt timeline was rechecked at desktop and mobile widths. Temporary viewport overrides were reset. No personal tags, favorites, originals, or sharing permissions were changed for testing.

**Not a full release sign-off:** the exhaustive route/permission/compound-video matrix and fixture-driven screenshot baselines in T7 remain unexecuted. The existing live library has only ten photos, so nonempty live video/favorite states are not browser-certified. Canonical artwork work→actor relationship expansion remains separate backend scope, not an inferred personal-media tagging feature. The data-scale fixture is complete; a rich isolated browser-media fixture is not. These are explicit acceptance gaps rather than claims of completed functionality.
