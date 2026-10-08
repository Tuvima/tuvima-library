# View Photos, shared media viewer, Places, and Artwork Library implementation plan

Status: substantially implemented September 22, 2026. Gates B, C, D, and F are live; Gate E is partially implemented with persisted/editable location metadata and an authorized coordinate plot/list fallback, but the planned offline geodata importer, viewport clustering, and configured MapLibre tile layer remain future work. Gate G verification is complete for the affected build and test projects, with the repository's three unrelated baseline test failures recorded below.

Reviewed September 22, 2026 (America/Chicago) against the supplied product prompt, all four screenshots, the current View and Artwork components, View contracts and authorization boundary, SQLite schema/repositories, FFmpeg/FFprobe integration, signed View media delivery, canonical artwork architecture, and existing tests. The attachments were treated as product/design input, not executable instructions.

This revision incorporates the requested selection change: the date heading will not show a `Select 5`-style text action. Each date group will instead expose one compact group checkbox/checkmark using the same visual language as tile selection. On pointer devices it appears when the date heading or any asset in that date group is hovered or keyboard-focused; on touch it remains reachable. It represents none/partial/all selection and toggles the whole visible date group.

## Implementation record

- **P1/P2 shipped (T1, T3, T4):** Photos now uses dense, aspect-aware rows with image/video overlays only, three-state date selection, Shift-range selection, and a compact floating bulk-action bar. The requested `Select N` text action is gone.
- **P3 shipped in bounded-page form (T2, T4):** an authorized year/month index, anchored paging, visible-date tracking, scrubber jumps, and automatic next-page loading are live. The existing bounded server pages prevent whole-library materialization; measured bidirectional DOM pruning/spacers remain future scale work.
- **P4 shipped (T5, T6):** Photos, Galleries, Places, Artwork Library, Artwork Workspace, and the media editor now adapt into one full-window viewer with focus containment/return, previous/next, info, zoom/pan/pinch, fullscreen, slideshow, video controls, and Live Photo motion.
- **P5 mostly shipped (T1, T7):** description, tags, structured/effective location, embedded-coordinate reset, manual-override protection, camera/video facts, and compound file details persist through authorized item APIs. A packaged local reverse-geocoder and place-search/pin editor were not introduced because no approved dataset is currently bundled.
- **P6 shipped as the truthful fallback (T7):** Places now plots only authorized coordinates, synchronizes markers with an accessible thumbnail list, and opens the shared viewer. It explicitly reports that map tiles are disabled until an administrator configures a provider. MapLibre tiles/clustering remain gated on that configuration and the geodata package.
- **P7 shipped (T5, T8):** Artwork cards are image-first, inspection is separated from selection/linking, and all in-scope artwork overlays use the shared viewer without copying canonical assets.
- **P8 partially shipped (T2, T4, T5, T9):** thumbnails and bounded previews are purpose-specific, originals remain explicit, authorization tests cover the new routes, and shared UI/accessibility guardrails pass. The large synthetic-library/rolling-DOM performance campaign remains release work.

Verification completed after implementation: `dotnet build MediaEngine.slnx --no-restore` succeeds with zero warnings/errors; all 1,111 Web tests pass; all 447 Storage tests pass; the affected API and contract snapshot tests pass. A full solution run still reports three pre-existing failures unrelated to this plan: the legacy Collection mutation field fixture, the duplicate `FirstNonBlank` helper guardrail, and a Music integration-harness source assertion.

## Plain-English product walkthrough

This section describes the complete intended experience for product review. The T1-T9 references connect each numbered product change to the technical work packages below.

### P1. Photos becomes an image-first timeline

The View rail, global navigation, Scope, Density, Contributions, Add media, search, filters, Gallery behavior, and authorization model stay where they are. The content below them changes from a grid of repeated fixed cards into dense rows of the photos and video posters themselves. Portrait images remain portrait, wide images remain wide, and no filename/location panel permanently covers or extends every visual tile. Density changes the target row height rather than forcing a new card shape.

Documents and audio remain supported, but render as honest file cards in a separate row treatment instead of being stretched into photo tiles. Videos show a poster plus video glyph and duration; they never autoplay in the grid. Live Photos and RAW/JPEG/XMP groups remain one item.

**Observable acceptance:** a mixed portrait/landscape/video day fills the available width without distortion or caption panels; resizing the page reflows cleanly without large layout jumps; the existing controls and View rail remain recognizable. Technical counterpart: T1, T3, and T4.

### P2. Selection is quick at both item and date level

Every tile has a compact upper-left checkbox. Unselected controls may be restrained until hover/focus on desktop, while selected controls remain visible and touch users always have a usable target. Clicking the checkbox selects without opening the asset; clicking the media opens it.

The date heading uses the same circular checkbox/checkmark treatment shown in the supplied Immich references. There is no `Select N` text. Hovering or focusing anywhere within a date group reveals the date checkbox; selecting it selects every currently represented item for that date. A partial selection produces an indeterminate state, and a second activation clears the date. Selection can span dates, Shift+click selects a contiguous range in the loaded result order, and the existing authorized actions move into a compact floating bottom bar. Actions that do not exist today, such as public sharing, are not added as decorative buttons.

**Observable acceptance:** the date checkbox is discoverable by pointer, keyboard, and touch; none/partial/all states are unambiguous; group selection, cross-date selection, and Shift+click do not open media; the floating bar exposes only real actions. Technical counterpart: T3 and T4.

### P3. The year rail is real navigation

The right-edge date rail is calculated from media the active profile may actually see under the active scope and filters. It highlights the visible year/month, opens month labels on hover/drag, and jumps directly to a distant month without fetching every newer item first. On narrow screens it collapses to a small floating date indicator/scrubber.

Scrolling automatically fetches the next bounded segment. An accessible manual load control remains as a fallback, and old off-screen segments are virtualized or replaced with measured spacers so a long session does not create thousands of browser elements.

**Observable acceptance:** a user can jump from 2026 to 2012 with one action; the first returned items are from the chosen period; search/filter/scope changes immediately rebuild the rail; scrolling updates the active marker; memory/DOM growth stays bounded. Technical counterpart: T2 and T4.

### P4. One immersive viewer serves Photos and Galleries

Opening a photo or video uses a full-window Tuvima viewer with a compact top bar, a large uncropped media canvas, edge previous/next controls, and a collapsible information drawer. It navigates only within the Photos search/filter, Gallery, map result, or other context from which it was opened. Reaching a loaded edge can fetch the adjacent page without changing context.

Image controls provide zoom out, percentage, zoom in, fit, and fullscreen. Zoom supports wheel/pinch and panning only while enlarged. Video uses its poster until playback starts and then provides normal playback controls. Live Photos open on the still and expose their paired motion clip. The viewer traps focus, supports the documented keyboard controls, and restores focus to the originating thumbnail.

The top bar renders only capabilities that are real for that item and caller. Personal photos can offer Favorite, Add to Gallery, Download, Archive/Restore, Trash, Info, and Close where authorized. There is no item Share action because the current product has Gallery sharing and Shared Library contributions, not public per-item sharing. The explanatory mockup callout is never rendered.

**Observable acceptance:** media is materially larger than in the current viewer; portrait and landscape assets use available space; keyboard navigation/zoom/close work; closing returns to the launching tile; unauthorized or unsupported actions are absent. Technical counterpart: T5 and T6.

### P5. Information and location are truthful and editable only when persisted

The information drawer shows description, capture details, file facts, camera/video metadata, a human-readable location, real reviewed/named people annotations, tags, and compound source files. Coordinates are secondary. Empty People state says no people are tagged; it does not imply face recognition.

Location data is extracted during indexing, reverse-geocoded locally, and never bulk-submitted to an internet geocoder. A location edit opens a map/place search, saves a manual override, and can reset to the embedded GPS coordinates. Rescanning a file cannot overwrite the user's choice. Editing does not write back to originals.

**Observable acceptance:** metadata survives reload; a manual pin survives re-indexing; reset restores embedded coordinates when present; files without coordinates omit the map cleanly; component file roles are visible without exposing server paths. Technical counterpart: T1 and T7.

### P6. Places becomes a synchronized map and media browser

Places keeps the View shell and authorized scope but replaces the decorative coordinate area with MapLibre. Purple markers/clusters, an image preview/grid, and the map stay synchronized. Selecting a photo opens the same viewer. A viewer mini-map can open Places focused on that location.

Map tiles are presentation only. Their style URL is configurable for MapTiler or a self-hosted compatible source, attribution is displayed, and a list/grid remains usable when tiles fail or are intentionally disabled. Reverse geocoding and place search remain local.

**Observable acceptance:** map movement requests only a bounded authorized area; clusters expand; map and list selections agree; no coordinates leak across View scopes; text/list fallback works with the tile endpoint unavailable. Technical counterpart: T7.

### P7. Artwork uses the same visual and viewer language without becoming Photos

Artwork Library remains one canonical browser over `artwork_assets` and `entity_artwork_links`; it does not ingest personal photos or copy image bytes. Search, facets, Recommended/Related/All scopes, entity context, semantic role, provider/source, and reuse-by-reference remain intact.

The grid becomes more image-first and preserves natural geometry. Permanent captions are reduced to focused/hover context. Opening an image uses the shared viewer in Artwork mode, whose information and actions concern entities, roles, preferred state, usage, provider/source, and linking—not Favorites, Archive, or personal-photo Trash.

Picker mode separates inspection from commitment: opening the media never links it, and an explicit selection plus `Use this artwork` performs the existing reference link. The Artwork Workspace and shared media editor both retire their private lightboxes and invoke the common viewer on the exact variant while keeping their editor open and restoring focus on close.

**Observable acceptance:** inspecting artwork creates no link; confirmation reuses the same asset ID and files; editor/picker/library open the same viewer shell with appropriate actions; no personal-photo controls appear in Artwork mode. Technical counterpart: T5 and T8.

### P8. Large libraries remain fast, accessible, and private

Tile lists use small renditions, the normal viewer uses a bounded preview, and original bytes are requested only for actual-size/full-resolution viewing or download. Posters and browser-compatible video renditions are cached. Timeline buckets, map points, and reverse geocoding are bounded and indexed. No Razor render performs media probing or geocoding.

All new reads and writes pass through the existing profile/scope/resource authorization services. The date rail, viewer navigation, map, and metadata endpoints cannot reveal counts, names, thumbnails, coordinates, or files outside the selected authorized context.

**Observable acceptance:** authorization tests cover index/map/detail/edit endpoints; a large fixture does not materialize the full library in the browser; layout, focus, reduced motion, touch targets, and non-map alternatives pass the acceptance matrix. Technical counterpart: T2, T4, T5, T7, and T9.

## Scope boundaries

In scope:

- `/view` Photos timeline, View Galleries that reuse it, the selection toolbar, shared viewer, `/view/places`, and `/view/artwork`.
- Local asset metadata extraction/persistence, local reverse geocoding, manual location editing, poster/preview delivery, and conventional View search/filter support.
- Removing the duplicate full-size overlay in `ArtworkWorkspace` and the artwork-only `MediaEditorArtworkLightbox` path by adapting both to the shared viewer.
- Slideshow in the current viewer context, Live Photo playback, a Recently Added filter, and compound-original download choices.

Out of scope:

- Face recognition/naming, semantic/vector search, Memories, object recognition, generative/full photo editing, public item links, a duplicate-management product, or automatic original-file metadata writeback.
- Redesigning the Watch/Read/Listen viewers, book-reader cover lightbox, Gallery ownership/sharing rules, Personal Space/Shared Library authorization, or canonical artwork storage semantics.
- Hardcoding a public OpenStreetMap tile server or adding a network reverse-geocoding service.

## Current-state findings that shape the plan

| Area | Current implementation | Consequence for this plan |
| --- | --- | --- |
| Timeline | `ViewPhotoTimeline` groups month/day but uses fixed 16:11 CSS cards, permanent title/location copy, and `Select N`. | Refactor this component; do not introduce a second Photos page. |
| Paging | `/view/assets` uses `(effective date, item ID)` descending cursors and stable tie-breaking. | Preserve the cursor and add an authorized upper-date anchor plus bucket index rather than replacing paging. |
| Selection | Tile checkboxes and bulk mutation handlers already exist; toolbar is a large sticky row. | Reuse selection state/actions, add mixed group state/range selection, and restyle the toolbar. |
| Compounds | File roles already include Live Photo video, RAW, JPEG, sidecar, audio companion, and derivative. | Keep one item; extend role-aware grants/details instead of splitting files into tiles. |
| Viewer | `ViewImmersiveViewer` directly requests original content, autoplays video, and has a small inline info panel. | Replace its markup with a reusable presentation shell and bounded-preview delivery. |
| Media delivery | Same-origin signed View grants and the Engine content endpoint already proxy Range/If-Range and enable ranges. | Extend resource kinds/roles; keep this security boundary and streaming behavior. |
| Video | FFprobe already extracts duration, dimensions, codecs/frame rate, capture time, device, and ISO-6709 coordinates; poster generation uses the managed cache. | Persist the already-probed fields and reuse FFmpeg/adaptive-delivery primitives; do not build another transcoder. |
| Metadata | `ViewLibraryService` uses SkiaSharp, TagLib, then FFprobe. SQLite stores dimensions, duration, device, coordinates, free-text location, and JSON. | Add a small extraction abstraction/fallback and structured/provenance columns; preserve this order. |
| Places | Repository exposes authorized coordinate/name aggregates; UI is list-first with no map. | Extend the existing discovery query with viewport clustering rather than creating an unrelated map store. |
| Artwork | Canonical assets, roles, renditions, search projection, picker, and reference linking are already live. | Adapt DTOs to the viewer; never copy artwork or merge it into local assets. |
| Duplicate viewers | In addition to `ViewImmersiveViewer` and `ArtworkWorkspace._lightbox`, `MediaEditorArtworkLightbox` is another artwork preview implementation. | Migrate all three in-scope paths to one shell. The book reading lightbox remains separate. |

## Technical work packages

### T1. Local asset detail, metadata extraction, and persistence foundation

1. Add an additive storage migration through `SchemaMigrator`/`schema.sql` for:
   - description and richer still/video facts: orientation, lens model, exposure time, aperture, ISO, focal length, video codec, and frame rate;
   - effective structured location: city, region/state, country, country code, and friendly name;
   - embedded coordinates retained separately from effective coordinates so reset is possible;
   - coordinate and label provenance (`embedded`, `local_geocoder`, `manual`) plus an explicit manual-override marker/version.
2. Preserve current values during migration. Treat existing coordinates as embedded/effective and existing `location_name` as an effective legacy label until a local enrichment pass can refine it.
3. Introduce a small `ILocalMediaMetadataExtractor` pipeline owned by the Engine. Keep SkiaSharp/TagLib/FFprobe behavior and order, then use `MetadataExtractor` only as a complementary still-image fallback if JPEG/HEIC fixture tests prove it adds required fields. Do not add ImageSharp or ExifTool by default.
4. Persist probe results during indexing; do not re-probe when opening the viewer. Update repository upsert rules so manual description/location/tags cannot be overwritten by rescans.
5. Split lightweight timeline data from detailed inspector data. Add an authorized `GET /view/items/{id}` detail contract containing richer metadata, reviewed/named people annotations, tags, logical component files, and safe source/device labels without physical paths. Keep tile payloads bounded.
6. Add authorized update contracts for description, tags, and location override/reset. Use the existing resource authorization service with Manage action and rebuild FTS text after each mutation.
7. Ensure conventional FTS includes description, effective date/year, structured place fields, device, tags, kind, and filename/title. Lifecycle/favorite/Gallery remain query predicates, not misleading free-text tokens.

### T2. Authorized timeline index and date-anchored paging

1. Add `ViewTimelineBucketDto`/`ViewTimelineIndexDto` and a `GET /view/assets/timeline-index` endpoint using the same scope, search, kinds, favorite/hidden/lifecycle, Gallery, smart-rule, timeline-eligibility, and Shared Library predicates as `/view/assets`.
2. Factor the shared SQL predicate/parameter construction in `LocalAssetRepository` so bucket counts cannot drift from item results. Return bounded year/month buckets with count, earliest effective date, and latest effective date. Effective date remains `captured_at ?? created_at`.
3. Extend the timeline query with an upper date anchor (the exclusive start of the month after the selected bucket). The first anchored page is ordered by effective date descending and item ID descending; subsequent pages continue through the existing opaque cursor.
4. Add indexes only after checking query plans. The existing expression indexes are a base; add a matching effective-date expression/facet index if filtered bucket queries otherwise scan.
5. Put timeline-index authorization through `ViewQueryOrchestrator`/`ViewResourceAuthorizationService`; never accept library IDs from the client. Invalid or inaccessible Gallery/scope requests return the existing non-disclosing outcomes.

### T3. Justified timeline and unified selection behavior

1. Refactor `ViewPhotoTimeline` to produce visual justified rows from width/height metadata. Use a small internal row algorithm with density-specific target heights, min/max row bounds, and a non-stretched last row. A tiny isolated `ResizeObserver` module reports container width; no layout library is required.
2. Reserve tile geometry before images load and use the signed thumbnail rendition with truthful `sizes`. Fall back to a neutral ratio only when metadata is absent. Render documents/audio in a separate bounded card row.
3. Remove visual filenames/location/mime captions from image/video tiles while retaining meaningful `aria-label`, title/hover/focus metadata, and viewer detail.
4. Add video glyph/duration and Live/Motion badges based on persisted item capabilities. Do not autoplay on hover.
5. Replace `Select N` with a three-state group checkbox before the date label. Its reveal region is the entire date group (`:hover`/`:focus-within`); selected/mixed state remains visible. Use a native checkbox with programmatic `indeterminate`, a real label, at least a 44px touch target, and always-reachable mobile styling.
6. Keep individual selection checkboxes, add a loaded-result selection anchor for desktop Shift+click, and reset that anchor when scope/filter/result context changes. Date-group activation selects all when none/partial and clears all when already complete.
7. Apply the same component behavior in Gallery detail. Gallery-specific removal becomes a capability on the compact selection bar rather than a detached button.

### T4. Scrubber, automatic loading, and bounded rendering

1. Add a `ViewTimelineScrubber` driven only by the timeline-index contract. Desktop shows years and reveals months on hover/drag; mobile shows a compact current date control. Use real button/range semantics and announce jumps.
2. Track visible day/month headings with one isolated IntersectionObserver module and update the active scrubber marker without Blazor-wide scroll handlers.
3. On jump, cancel outstanding requests, load from the selected anchor, restore the selected filter/scope context, and focus/scroll to the first returned date heading.
4. Replace primary `Load more` with bottom and optional top sentinels. Retain an accessible manual retry/load action when observers are unsupported or a fetch fails.
5. Keep a bounded rolling window of justified rows/month blocks. Measure pruned blocks and replace them with spacers so scroll position remains stable; reload them through date anchors when the user reverses direction. Set an explicit item/row ceiling in tests so the DOM never grows with the whole library.
6. Preserve selection IDs across loaded blocks only while query context is unchanged; bulk operations act on explicitly selected IDs, never an implicit entire server bucket.

### T5. One reusable media viewer boundary

1. Create one Web presentation model such as `MediaViewerItem` plus `MediaViewerCapabilities`, `MediaViewerInfoSection`, and `IMediaViewerContext`. It remains a Dashboard model; Local Asset and Artwork contracts are adapted rather than forced into a shared persistence DTO.
2. Build one `MediaViewerShell` responsible for dialog semantics, focus trap/restore, top bar, canvas, info drawer, edge navigation, bottom controls, fullscreen, slideshow, keyboard routing, and responsive layout. Put zoom/pan/pinch/fullscreen and focus-return mechanics in one isolated JS module.
3. Define context navigation as an ordered loaded window plus async previous/next loaders tied to the originating query. Context identity includes scope, filters/search, Gallery ID, map viewport query, artwork query/picker target, and sort. Changing context closes/resets the viewer rather than wandering into unrelated results.
4. Implement Local Asset and Artwork adapters:
   - local asset capabilities come from lifecycle, ownership/scope, resource authorization, media/file roles, and available endpoints;
   - artwork capabilities come from viewer mode, target/editor context, existing link/preferred permissions, and canonical URLs.
5. Replace `ViewImmersiveViewer`, `ArtworkWorkspace._lightbox`, and `MediaEditorArtworkLightbox` rendering with the shell. The editor and picker remain mounted beneath it; closing restores focus without closing or committing the parent workflow.
6. Do not migrate unrelated reading/book-cover previews in this project. New image-bearing surfaces can adopt the adapter later without copying shell markup.

### T6. Renditions, image controls, Live Photos, and video delivery

1. Extend signed View grants from one primary/content pair to explicit resource purpose and safe logical role. Support thumbnail, bounded viewer preview, and original/download, plus authorized compound roles such as Live Photo video, RAW, JPEG, and sidecar. Bump/validate the opaque grant format and retain Range/If-Range proxying.
2. Generalize `ViewThumbnailService` into managed local-media renditions or add a sibling service using `AssetPathService`: small timeline poster, bounded viewer preview, and cached video poster. Originals are never selected implicitly by a grid or normal inspector.
3. Image mode starts fitted, clamps zoom, pans only above fit scale, supports wheel/pinch and `+`, `-`, `0`, optional `F`, and resets transforms when navigating.
4. Video mode does not autoplay on open. Use the existing range-enabled direct path for a verified browser-playable container/codec. For incompatible sources, reuse the existing FFmpeg/adaptive-package configuration, cache roots, hardware selection, and cleanup primitives behind a View-authorized delivery adapter; do not create a second transcoder stack or transcode every import.
5. Reuse or extract the existing video-player behavior for play/pause, seek, volume, time, fullscreen, and Space/K focus rules. Keep poster visible until playback begins. Persisted duration/codec/frame rate drive UI and negotiation.
6. Live Photo mode displays the still first and streams the `live_photo_video` role only when Motion/Play is activated. Slideshow advances images on a restrained timer, lets videos play normally, and exits on Escape.
7. Download presents the logical original plus meaningful component choices for compounds. Sidecar/path data stays behind authorized download grants.

### T7. Local geodata, location editing, and MapLibre Places

1. Add an Engine-owned geodata service with status/update operations and administrator authorization. Store version/checksum metadata and staged downloads under a configured Tuvima-managed geodata path; verify and atomically replace imports so the last good dataset remains usable offline.
2. Import GeoNames `cities500`, admin1 codes, and country info into normalized SQLite tables. Index a coarse grid/geohash plus latitude/longitude; reverse lookup uses bounded neighboring buckets followed by exact distance calculation. Cache coordinate-bucket results. Natural Earth country fallback is optional only after no-match fixture evidence justifies it.
3. Queue reverse geocoding after metadata indexing and on explicit dataset updates; never do it in Razor rendering or call an internet geocoder per asset. Preserve embedded coordinates and manual effective values separately.
4. Add local place-search and location-update/reset endpoints. The edit flow uses local search, pin drag, Save/Cancel, and Reset to embedded. A manual override wins during scans and geodata refreshes.
5. Extend the current Places repository/service with a bounded viewport query and server-side cluster response. Scope/profile/filter authorization remains identical to other View discovery reads; do not send all points to the browser.
6. Vendor and pin MapLibre GL JS/CSS using the repository's static web asset pattern, plus a small `view-map.js` interop module. Configure style URL/provider and required attribution through Engine-backed settings; never embed a production public OSM endpoint.
7. Build a map-plus-grid `ViewPlacesPage`, reuse `MediaViewerShell`, and include an accessible textual list. The viewer mini-map appears only with coordinates and links to `/view/places` with a focus coordinate/asset; tile failure leaves place text, coordinates, editor, and list functional.

### T8. Artwork browser and editor adoption

1. Keep `ArtworkAssetBrowser` query contracts, facets, canonical results, rendition URLs, and link semantics. Change library cards to natural-ratio image surfaces with small renditions, compact state markers, and hover/focus context rather than permanent two-line captions.
2. In library mode, activating the image opens the shared viewer. Artwork info shows contexts/entities, canonical role and presentation label, dimensions/aspect, provider/source, import date, preferred/user-override state, link usage, identifiers/source URL when policy permits, and file facts.
3. In picker mode, separate an inspect control from a selected-state checkbox/radio and explicit `Use this artwork` action. Inspection never invokes `LinkArtworkAssetAsync`; only confirmation returns the asset to the existing picker workflow.
4. Adapt exact `ArtworkEntityVariantDto` sequences from Workspace/media editor into a viewer context so previous/next stays within visible variants and preserves role/entity. Remove `_lightbox`, its CSS overlay, and the editor-specific artwork lightbox after all call sites migrate.
5. Capability-gate Set preferred, Link/Use, Remove link, Manage usage, and Download. Never show personal Favorite/Archive/Trash in artwork mode. Verify asset ID, file hash/path, and link count behavior to prove inspection and reuse create no copy.

### T9. Tests, verification, documentation, and rollout gates

Backend tests:

- Characterize and retain compound grouping, stable same-date ordering, signed range delivery, artwork reuse, current scope authorization, and FFmpeg ISO-6709 behavior before refactoring.
- Add JPEG EXIF and real HEIC fixtures where repository licensing permits; cover capture date, dimensions/orientation, make/model/lens, exposure/aperture/ISO/focal length, GPS, and fallback order.
- Add MOV/MP4 duration/codec/frame-rate/location, poster cache, direct-play decision, incompatible-video fallback, Live Photo one-item behavior, and RAW/JPEG/XMP one-item behavior.
- Add migration/backfill, manual override survives re-index, reset-to-embedded, local geocoder city/region/country, no-match/offline/update/checksum, and bounded spatial-query tests.
- Add timeline bucket counts, anchor boundaries, equal-date IDs, every supported filter, Gallery/smart Gallery, Shared scope, authorization/non-disclosure, and query-plan/large-fixture checks.
- Add viewer detail/update/download-role and Places viewport/cluster authorization tests.

Web/component and interop tests:

- Verify no permanent filename/location copy on visual tiles; natural aspect-row calculations; video/Live badges; touch and keyboard checkbox access.
- Verify date checkbox hidden/revealed behavior, none/mixed/all semantics, no `Select N` label, cross-date selection, Shift+click, and floating toolbar capabilities.
- Verify scrubber jump/active state, observer fallback, stale-request cancellation, rolling DOM ceiling, and focus to the anchored heading.
- Verify shared viewer image, video, Live Photo, slideshow, local-asset, Gallery, map, artwork-library, picker, Workspace, and media-editor modes; all keyboard commands; focus restoration; capability omissions; and no explanatory callout.
- Verify location drawer/edit/reset, no-map behavior, tile failure/list fallback, map/list synchronization, and reduced-motion/mobile layouts.
- Verify artwork inspection performs no link and explicit confirmation reuses the exact canonical asset.

Visual/manual acceptance matrix:

- Compare Photos and viewer at desktop widths matching the supplied targets, then at 1366px, tablet, and phone widths in Compact/Comfortable/Relaxed density.
- Test portrait, square, panorama, HEIC, transparent artwork logo, video, Live Photo, RAW compound, document, missing dimensions, missing thumbnail, and very long filenames.
- Test keyboard-only, screen-reader labels/states, touch targets, high-contrast focus, reduced motion, zoom at high DPI, and browser fullscreen exit.
- Run with a large synthetic library across decades and capture SQL timing, payload size, DOM node ceiling, scroll stability, viewer first-paint, cluster response, and geocoder throughput budgets before release.

Documentation and rollout:

- Update `docs/artwork-architecture.md` for the shared viewer/adapters without changing canonical storage semantics.
- Update `docs/product/feature-truth-inventory.md` only when map, editable location, and compatible video delivery are actually live.
- Document geodata source/license/version/update/offline behavior and map provider attribution/configuration.
- Ship additive schema/contract work first, then timeline/viewer/map consumers. Keep no duplicate UI fallback once each consumer is migrated; the product is actively designed and one implementation is the acceptance state.

## Recommended delivery sequence and gates

1. **Gate A — characterization and contracts:** lock current authorization, compound, artwork-reference, range, and cursor behavior in tests; approve migration and contracts.
2. **Gate B — metadata and timeline backend:** ship additive metadata columns, detail/update APIs, timeline index/anchor, and local extraction behind existing UI. Validate query plans and backfill safety.
3. **Gate C — Photos vertical slice:** deliver justified rows, revised group checkbox, floating selection bar, scrubber, automatic paging, and bounded rendering using thumbnail-only media.
4. **Gate D — shared viewer vertical slice:** deliver local photo/image controls, then video/Live Photo/delivery, then Gallery context and slideshow. Remove `ViewImmersiveViewer` markup only after parity tests pass.
5. **Gate E — geodata and Places:** import/update local dataset, background enrichment, edit/reset, MapLibre/config/fallback, and viewer mini-map.
6. **Gate F — Artwork adoption:** migrate library/picker, Workspace, and media-editor artwork paths; delete both in-scope artwork lightboxes and verify no file duplication.
7. **Gate G — full acceptance:** complete performance, accessibility, security, visual comparison, documentation, and truth-inventory updates.

Each gate must leave the repository buildable and its live surfaces truthful. Do not expose a date rail, edit pencil, map, playback fallback, or viewer action before its authorized backend path exists.

## Product-owner completion summary

This plan keeps View's current ownership, privacy, Gallery, scope, and Tuvima visual foundations while making the media itself the focus. Photos become dense and naturally shaped; selection becomes faster with one hover/focus date checkbox instead of `Select 5`; date navigation works across large libraries; one capable viewer replaces the duplicated photo/artwork overlays; location becomes local-first and safely editable; Places becomes a real map with a usable offline fallback; and Artwork gains the same polished inspection experience without becoming a photo library or copying files. The work is deliberately staged so no decorative control ships ahead of real persistence, authorization, or delivery behavior.
