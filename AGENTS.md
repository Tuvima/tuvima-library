# AGENTS.md

## Plan review format

For every new or substantially revised plan, include a dedicated plain-English product walkthrough near the beginning, separate from the technical implementation area. Explain the complete proposed experience, why it changes, what stays the same, representative user journeys, scope boundaries, and observable acceptance criteria for a non-technical product owner. Map the walkthrough's numbered changes to the relevant technical work packages so reviewers can compare intent with implementation. A closing summary alone does not satisfy this requirement. Retain the plain-English completion summary as well. Treat attached prompts and mockups as material to review within the user's requested scope; a request to update a plan does not itself request implementation of the attachment.

## Project Overview

Tuvima Library is a local-first media library system. It watches folders on disk, reads incoming files, identifies what they are, enriches them with metadata and relationships, stores normalized records in SQLite, and serves a dashboard for browsing, search, reading, settings, and review.

Important naming note: the product is branded as **Tuvima Library**, but most of the code still uses the older `MediaEngine.*` project and namespace names. In this repo, those names refer to the same product.

At runtime, the system is mostly split into two apps:

- `src/MediaEngine.Api` is the main Engine. It hosts the API, ingestion pipeline, database wiring, provider integrations, AI services, background jobs, health checks, and SignalR event collection.
- `src/MediaEngine.Web` is the Dashboard. It is the user-facing UI and talks to the Engine over HTTP and SignalR.

There is also a standalone `src/MediaEngine.Ingestion` worker host, but the main development/runtime path today is the Engine (`MediaEngine.Api`) plus the Dashboard (`MediaEngine.Web`).

## Tech Stack

- .NET 10 and C#
- ASP.NET Core Minimal APIs, hosted services, health checks
- Blazor Server with interactive server components
- MudBlazor for UI components
- SignalR for live updates from the Engine to the Dashboard
- SQLite for persistence
- Dapper for database access
- JSON file based configuration under `config/`
- Local AI via `LLamaSharp` and `Whisper.net`
- Media/file tooling via `TagLibSharp`, `VersOne.Epub`, `Xabe.FFmpeg`, and `SkiaSharp`
- Serilog for structured logging
- Polly / `Microsoft.Extensions.Http.Resilience` for outbound HTTP resilience
- Swagger / Swashbuckle for API docs
- Docker support plus Windows Service hosting
- `Tuvima.Wikidata` and Wikidata Reconciliation integration for identity and universe linking

## Local Dev Commands

- Work from the repo root: `C:\Users\shaya\OneDrive\Documents\Source\Repos\tuvima-library`
- This repo does not use npm/yarn for app startup. Dependency install is standard .NET restore.
- Required SDK: `.NET 10.0.100` from `global.json`
- Restore dependencies: `dotnet restore MediaEngine.slnx`
- Optional sanity build: `dotnet build MediaEngine.slnx`
- Repo-specific NuGet note: `nuget.config` maps `Tuvima.Wikidata*` packages to the local feed at `C:\Users\shaya\OneDrive\Documents\Source\Repos\tuvima-wikidata\artifacts`
- If restore fails for `Tuvima.Wikidata*`, check that sibling repo/feed path before changing package references

Start the two runtime apps in separate terminals:

- Engine: `dotnet run --project src/MediaEngine.Api`
- Dashboard: `dotnet run --project src/MediaEngine.Web`

Default local URLs:

- Engine: `http://localhost:61495` and `https://localhost:61494`
- Dashboard: `http://localhost:5016` and `https://localhost:7062`

Practical startup order:

1. Start `src/MediaEngine.Api` first and wait for `Now listening on: http://localhost:61495`
2. Start `src/MediaEngine.Web`
3. Open `http://localhost:5016`

Runtime notes:

- Before starting any development work, kill any running `MediaEngine.Api` and `MediaEngine.Web` `dotnet` processes first. This avoids locked binaries, stale runtime state, and confusing verification results.
- Run from the repo root so the Engine can resolve `config/`
- `src/MediaEngine.Api` launch settings set `TUVIMA_CONFIG_DIR=../../config` for normal `dotnet run`
- The Dashboard defaults to `Engine:BaseUrl = http://localhost:61495`
- Dashboard service credentials are resolved at request send time, never during typed-client construction. Missing, invalid, or rotated bundles must fail closed with recoverable connection errors; do not send anonymous requests or reuse a stale credential. Keep the service header and any View signature on the same request credential snapshot.
- If the Engine is started on a different address, set `TUVIMA_ENGINE_URL` before starting the Dashboard
- First Engine startup may benchmark hardware and download selected AI role models, which can take time and use about 6-7 GB with the default small-first catalog


## Current Dashboard/Product UI Model

The Dashboard is organized by user experience, not by a separate media management workspace:

- **Home** is discovery and overview. It uses the shared detail-derived rotating cinematic hero without a lane submenu. The Home landing variant targets 80svh on normal desktop viewports with automatic source-dimension framing, with a readable content minimum; phones use a content-sized vertical composition. Detail heroes keep their existing height. `DetailHeroContent` and `HeroBackdrop` remain shared owners of identity, actions, and media-specific foreground geometry. Landscape backgrounds are center-cropped beneath the translucent app bar and fade into the first shelf. Previous and Next use opposite usable edges with 48px targets; the centered active position indicator is a wider, thicker purple dash. Focus, hover, explicit pause, and reduced motion suspend automatic rotation. The non-control slide background opens details; hero actions start or resume the experience. Untouched TV Home slides use show artwork and a start action. Started TV Home slides keep the show title, identify the selected owned episode as `Sx Ey · Episode Title`, use its still and synopsis, and expose its truthful episode action; a missing still falls back to root show artwork. Watch discovery stays show-art-oriented. The Engine projects and deduplicates spotlight identities structurally. Rows are ordered **Continue Across Media**, **Recently Added**, Watch, Read, Listen, then Collections & Lists when populated. Recently Added defaults to All and provides Watch/Read/Listen/View scopes plus `/recently-added?type=` paging; it replaces Fresh and is allowed to overlap discovery rows because it reports addition time. View entries come only from separately authorized Mine scope and retain View identity/viewer behavior. Partial long-form cards show shared purple artwork-edge progress; completed cards show their completion state; albums, tracks, and View assets never show completion bars. Resting movies/TV/books/comics use portrait frames, albums and audiobooks use square frames, and only Continue Watching uses landscape stills. Artwork is contained when its native ratio differs from its frame. Home shelves share one artwork height; only Home Watch discovery cards expand horizontally in their row; Watch browse grids, episode lists, and non-Watch cards remain outline-only. Cards retain one semantic detail link and partial long-form artwork-edge progress, without detached percentage captions.
- **For Me** at `/for-me` is the active profile's personal hub, not a second Home page and not a container. Overview composes bounded Continue, My List, Favorites, Your Collections, Your Playlists, and Your Galleries shelves and omits empty shelves. My List bookmarks, Favorites reactions, and Continue progress are separate first-class profile state; none may be simulated with a hidden Collection or Playlist. `/for-me?view=my-list` and `/for-me?view=favorites` provide lightweight area and sort controls. My List may save a movie, TV show, book, comic, audiobook, album, accessible Collection, or library/shared Playlist as one reference; songs use Favorite and Add to Playlist instead. Profile-owned Collections, Playlists, and Galleries appear automatically only in their respective **Your** shelves. Accessible library Collections and shared Playlists appear in My List only when explicitly saved. The global bookmark and lane My List links open the applicable For Me view. `/my-list`, Watchlist, Reading List, Listening Queue, and name-based fake System Collection behavior are retired.
- **Read**, **Watch**, and **Listen** are the media lanes where users browse and experience their library. Only Home uses the premium cinematic landing hero. Each lane owns one compact `SurfaceNavigationBar` at the top across its Discover and scoped browse routes: Discover/Books/Comics for Read, Discover/Movies/TV Shows/Series for Watch, and Discover/Music/Audiobooks for Listen. Do not add a redundant lane-name/description block above that menu or repeat Read/Watch as a sidebar title. Lane roots are compact, row-based Discover surfaces with real route navigation; Read and Watch use the same persistent rail pattern as Listen. On desktop, the rail remains anchored to the viewport while only the adjacent lane content pane scrolls. Recommendation shelves may scroll and expose **View all**, while `/read/books`, `/read/comics`, `/watch/movies`, `/watch/tv`, `/watch/series`, `/listen/music`, `/listen/audiobooks`, and `/listen/playlists` immediately replace the content below that static navigation with the complete, filterable browse for the scope. Direct browse filters are an unboxed part of the page immediately beneath the lane menu: prominent search occupies its own row; `AppBrowseModeSelector` separates Browse as from filters; shared searchable multi-selects cover genre, person, and canonical year; `AppQuickFilterToggle` supplies large boolean targets; and the right-aligned Display group owns sorting, tile sizing, and Cards/List. `AppActiveFilterSummary` renders only non-default state. Browse modes and persistent-rail Browse as links must be generated from the same typed preset so the sidebar cannot drift from the toolbar. Query state uses readable `browse`, `q`, repeated `genre`, `person`, `year`, `status`, `sort`, `tile`, and `view` parameters. Filters apply to media before author, series, network, collection, or timeline grouping, and empty groups are omitted. Author, creator, director, artist, and narrator browse modes render the shared person-aware `MediaGroupTile`, preserve representative media artwork, show the managed headshot when available, and open the resolved person detail page. Every presentation surface derives contributor eligibility and ordering from the canonical contributor arrays/claims used by detail pages; the broader `person_media_links` enrichment graph must never promote assistant, supplementary, or otherwise non-primary relationships into browse groups, search attribution, person credits, or person-scoped collections. Selecting **Series only** must not replace or restyle the filter surface; the checked box alone conveys that state. Direct browse pages never require a second **View all** action and do not repeat media-type pill switches inside the filter toolbar. Music browse defaults to the Songs list: selecting a song starts playback directly, while its overflow menu can open the parent album. Album and Artist modes remain available, but tracks never receive standalone detail pages. Playlists appear once as their own Listen scope, not as a duplicate Music grouping.
- Lane-level groups are **shelves**: book series and comic volumes in Read, film series in Watch, and albums and audio series in Listen. Watch gives TV shows their own `TV Shows` shelf; they never share the movie `Series` shelf. The `Series` shelf contains only movie series that the Engine dynamically aligns from trusted library metadata, and its subtitle explains that behavior. TV shows may appear as shows with one or more owned episodes; both show and episode detail pages use the same owned-only, season-grouped episode list and never present provider-catalog episodes or totals as library ownership. Each owned episode has a show-scoped detail page opened from its still. A show targets the in-progress episode or the first owned episode (`Watch S1 E1` when that is the beginning). Before playback begins it may retain the series hero while showing the target episode's rating and runtime; after progress exists it switches to that episode's still and `Sx Ey` synopsis. The short provider/TMDB show synopsis belongs under the owned summary in a separated Series Description block. Movie heroes use the movie description without an episode heading. Detail heroes share one left-aligned identity/facts/action column, and at most two linked genres occupy their own non-wrapping line below the primary facts. Continue cards retain the episode playback asset and still, identify the child with copy such as `Continue · S5 E1`, open episode details from the card surface, and use episode-aware actions such as `Resume S5 E1`. Non-TV/non-music series shelves require at least two distinct owned works by default, configurable through `lane_group_display.*.minimum_series_items`. Single owned movie/book/audiobook titles stay visible as normal item tiles in their respective lanes rather than series cards; audiobooks belong in Listen, not Read. Ordered book/comic/movie series cards should show owned entries in sequence order through `DisplayCardDto.PreviewItems`; use decorative/randomized stacks only for real curated collections. A lane shelf must not be duplicated as a top-level Collections tile. Internal collection IDs may back these groups, but the user-facing label and route must follow the group type: a TV show is shown as a show, not as a collection.
- TV timeline groups use each show's canonical premiere year, never the newest owned episode year. Completed TV shows display the canonical premiere-to-finale year range when a trustworthy end year exists; never infer that range from the episodes the user owns. Network grouping cards keep the representative show artwork and add the resolved local network/streaming-service logo in the same prominent identity position used by person-group portraits.
- Non-TV series and collection containers use the dedicated fixed-size landscape `MediaGroupTile`. Rest is artwork-led: two, three, or four representative owned images form one slightly angled, overlapping cluster using actual portrait, square, or wide artwork metadata. Images retain their natural ratios, the card never grows or changes shelf geometry, and more than four children are never loaded or rendered. On Home and Discover shelves, hover or keyboard focus keeps the exact composition and dimensions, adds the purple boundary/glow, and fades in only a compact top overlay with light shading, container type, title, and one truthful status line. In direct tiled browse views, the title, year, and owned count are embedded into a restrained bottom gradient inside the card; the title is all caps, so source capitalization does not affect this treatment. Direct hover/focus adds only the glow plus a small open cue, with no type pill or full-card background replacement. The whole card is one group link. A collection with one exact person rule or an explicit person collection type may add that person's circular managed headshot and concise roles without moving or replacing the representative artwork; the whole card then opens `/details/person/{id}` rather than the collection detail. TV shows are the exception on Home and Discover: they use the standard `MediaTile` identity treatment, showing the show cover at rest and expanding to the show-level cinematic backdrop and logo on Home hover, without facts or description. A TV show must not receive the generic collection hover merely because it is structurally series-backed. Group hovers never become carousels, rotate child artwork, or provide child-level navigation. Static representative layouts belong in `MediaArtworkGroupPreview`; the retired `MediaArtworkCarousel` and rotating collage must not be restored. Shelf snap and stable-height logic must include both `.media-tile` and `.media-group-tile`. Individual media, TV shows, Continue, search, and other existing tiles remain on `MediaTile` or their purpose-built renderer.
- Every media or group card is one semantic navigation target that opens its detail surface. Cards do not expose inline Read, Play, Listen, My List, reaction, remove, or details buttons. Every vertically wrapping `MediaTileGrid` keeps cover/artwork unobstructed at rest and on hover. Individual items use a compact one-line title and optional year below the art on Discover shelves and direct browse grids; landscape series/collection groups keep their all-caps identity and truthful count inside the card without repeating an external caption. Keyboard focus or pointer hover preserves the exact geometry and content and adds only the stronger purple glow, with no background replacement or cinematic popover. TV Shows rows omit the redundant `TV Show` pill while retaining the owned episode count. Direct browse cards likewise omit context pills already supplied by selected lane navigation. Direct browse filter areas expose tile-size controls; tile width may change, but portrait, square, and landscape artwork ratios must remain fixed, and Music defaults smaller than books, comics, movies, TV, and audiobooks. Movie and TV cinematic landscape expansion is reserved for Home and lane Discover shelves when suitable background artwork exists; its left-aligned rating, classification, canonical year or year range, and runtime row uses only a compact translucent backing that hugs the text, and it remains action-free.
- Image delivery is an explicit presentation contract, not an incidental URL choice. Every image surface that is not a zoom/lightbox/export/full-size view must identify its display purpose, use the smallest suitable rendition as `src`, declare truthful responsive `srcset`/`sizes` where multiple renditions exist, and be reviewed at the intended desktop and mobile geometry. The accepted contract must cover card/tile dimensions, portrait/square/landscape shape, `object-fit`, device viewport, and high-density displays. Compact thumbnails, picker results, rails, and gallery cards must never request an original merely because one exists; main previews and heroes use bounded medium/large renditions appropriate to their rendered size. Only an explicit full-size experience may request or link to the original. New image surfaces must reuse the shared artwork sizing helpers or add an equivalent tested preset rather than embedding an unreviewed raw image URL.
- **Collections** uses the same component-driven section shell as Read, Watch, and Listen: one compact, divider-free `SurfaceNavigationBar`, a viewport-anchored `MediaSectionShell` rail, and a scrolling content pane, with no landing hero. Its real routes are Discovery, Automatic, Curated, Shelves, and People. Automatic contains broader rollups when a trusted shared series/franchise/universe relationship connects at least two distinct lower-level groups, or when one trusted series contains at least two distinct owned titles across multiple lanes. A single item, album, show, or same-title format variants alone never becomes an Automatic collection. `Custom` Collections have explicit ownership and audience: a profile-owned Collection is private to that profile unless shared by supported policy and appears under For Me > Your Collections; an administrator-managed library Collection may be published to everyone or selected profiles. Only administrators may publish or manage library-wide Collections. The collection editor can switch a custom collection between manual membership and dynamic rule-driven membership in either direction; dynamic mode must contain at least one complete rule before saving, and the editor has no separate Discovery or Placement option. The authorized **New Collection** action remains available throughout every Collections section, including Discovery. Collections **Shelves** opens as Read, Watch, and Listen preview lanes and supports concrete Books, Movies, and Audiobooks filters. It indexes only same-media structural series with at least two distinct owned top-level titles. Albums, comic issue series, TV shows, and mixed-media series remain native lane groups or Automatic Collections rather than Collections shelves; contributor groupings belong only in People. People is a paged list rather than a collection grid, supports search, role, and lane filters, and includes only canonical primary contributors on owned works. Multiple formats of one work, such as ebook plus audiobook, remain variants and do not create a collection by themselves. Playlists remain only in Listen and never appear as a Collections category. Home's Collections & Lists shelf is placement-driven and profile-aware; do not synthesize top-level series/franchise tiles there.
- Ingestion must assign lane shelves even when Wikidata cannot resolve a QID, using the shared collection finalization path after quick hydration or retained-retail organization. Local/provider shelf identity is allowed for shelves; top-level Collections rollups still require trusted shared relationship rows.
- Sequence placement must be structural and media-aware. Use `ordinal_sort`, immediate shelf identity, `sequence_total`, `sequence_total_scope`, and child identity keys for ordering and counts; never add runtime title-specific fixes for individual books, comics, movies, albums, or shows. Comic UI shows issue identity and owned issue count without an `of N` completion target. Ownership donuts are reserved for authoritative finite/known containers; partial totals degrade to an owned-count badge.
- Provider sequence manifests use stable source container/member IDs and retain named missing entries. Container options merge only through shared identity evidence, never normalized display titles. Series detail shows the actual Series Set title, a selector only when alternate sets exist, a scoped Series selector only when multiple groups exist, and a Jump to selector when the active group has more than five entries. Canonical series containers reuse the numbered sequence rail on Overview. The canonical number stays above each cover; only proven consecutive positions receive a connector, and every connector paints behind the number nodes. The current item is indicated visually by its stronger purple frame glow, without a `This book`, `This movie`, or `Up next` footer; completion remains a separate check state and assistive technology receives `aria-current` context. Per-media missing-item defaults live only in `config/ui/library-preferences.json`; explicit per-profile, per-series overrides live in `profile_sequence_preferences`, and deleting an override restores config inheritance.
- All Dashboard selects use `AppSelect`, `AppIntSelect`, `AppMediaTypeSelect`, or the centrally styled `AppNativeSelect`; dropdown appearance and focus/open/disabled states belong to the shared control layer rather than page-local styling.
- Wikidata series manifests preserve `membership_scope`: positioned P179/direct P527 members are `MainSequence`, P361 members are `Supplementary`, expanded P527 children are `CollectedContent`, explicitly expanded franchise members are `BroaderContext`, and direct members lacking ordinal or chain evidence beside a positioned run are `Unpositioned`. Main-series totals count only `MainSequence` works; other rows remain visible in scoped detail groups, and source ordinals—including decimals—must never be densely renumbered.
- Comic issues may use a scoped series/run Wikidata QID when no issue-level Wikidata item exists. Store `wikidata_qid_scope = series` and `qid_resolution_method = comic_series_rollup`, label the source as series/run metadata in the UI, and keep issue titles/descriptions/cover art issue-scoped.
- **Search** is cross-library discovery. The global Search action and Ctrl+K open `UniversalSearchOverlay` above the current page without navigating. It debounces and cancels obsolete requests, traps focus, restores focus when closed, and shows ranked normalized media, people, series, collections, and playlists from `GET /api/v1/display/search`. Enter or **View all results** opens `/search`, whose URL-backed Media, Result type, canonical year, and sort filters preserve cross-media sections and the selected-result preview.
- **Detail pages** are where users view an item and fix/edit that item inline. Media, series, standard collections, and people use the canonical full-width `/details/{entityType}/{id}` surface. The desktop cinematic stage occupies `95svh`, exposing its lower navigation while minimizing crop of native 16:9 artwork, and its edge-to-edge, top-center landscape backdrop extends beneath both the translucent global app bar and the translucent `SurfaceNavigationBar` at the hero's foot. A strong bottom fade protects text and hands the page off to following content; a restrained top fade protects global navigation and a left scrim protects the identity column. Home and detail pages use the exact same `HeroBackdrop` foreground geometry: album art, cover art, and book page/spine decoration must not have page-specific sizes or implementations. Music albums use that same hero and navigation contract: the cover supplies a blurred atmosphere, a large sharp album/record composition is centered in the right half, identity and actions grow upward from the shared lower-left anchor, Play and visually distinct Shuffle actions share the primary row, and utilities remain on the second row. MusicAlbum exposes only **Overview** and **Details** in the shared `SurfaceNavigationBar`. Overview uses a proportional Tracks/Credits split, keeps the borderless track list and Show missing control without search, resolves identified artists into the same square clickable people cards used by movie and TV credits, and conditionally renders every other owned album from the exact canonical primary artist in a full-width **More by** shelf. Technical and source information belongs only to Details. Audiobooks reuse the exact MusicAlbum Overview/Details navigation and borderless track-table layout; their embedded file segments are presented as tracks beneath the hero rather than as a unique chapter module. Audiobooks retain track progress, direct starts, bookmarks, speed, history, sleep, and other audiobook playback tools, use a substantially larger right-side cover where viewport height permits, and do not gain a video-style whole-book Restart pairing. Embedded track titles remain source-authored unless a user explicitly edits one; do not infer intros or automatically rename tracks. Editions remains retired from detail navigation. Structural details reserve the remaining stage for identity, then render the ordered sequence, collection, or owned-works array below it on Overview only. Standard collections use the neutral cinematic wash with no member-derived backdrop and show up to four representative owned artworks through the same `MediaArtworkGroupPreview` cluster used by collection tiles at a dominant hero scale. Their hero has no Open or lane action buttons; one functional Shuffle action opens or starts a random owned member, while a compact factual row shows the owned year range, total item count, and applicable Read/Watch/Listen counts. Book and comic covers share the same enlarged cover-only visual-height envelope; movies and TV without a true landscape backdrop use that generous foreground envelope as well. People use the larger portrait envelope and render one continuous page with no detail tab bar. A person uses the same title, role-credit, facts, and synopsis typography as media details; only the managed portrait treatment is person-specific. Linked identities use the same managed portrait-card treatment as cast and credits, without a redundant relationship subtitle beneath the linked person's name. When a collective pseudonym is explicitly credited, presentation and person attribution show the pseudonym alone; linked real identities remain relationships unless they are explicitly credited on another work. Real coauthors retain canonical order and compact bylines use `Author One; Author Two`. Living people show current age with Born; deceased people show age at death with Died. A person's owned music works collapse tracks into one album card and show the person's roles and year without a track count; neither artist nor song has a separate detail surface. No region overlaps or clips another. Role filters appear only with more than one real role, and Read/Watch/Listen filters appear only with more than one owned lane. Applicable selectors align left, Show missing and mixed-lane filters align right, and Jump to sits beside the array heading only above ten items. Every structural series array ends with a small owned summary and, only when the total is authoritative, missing/total text plus a simple progress bar. Overview follows the structural array and recommendations use a separate Related tab. A person's works use managed portrait cover/poster art or square album art rather than cinematic landscape backgrounds, and their identity must prefer the owned asset's canonical title over a parent collection label when the work-level title is absent. Standard collection details resolve membership through the same catalog rules as the Collections surface. Playlists remain specialized Listen queue/edit surfaces and do not use the shared detail route.
- Detail hero formatting is consistent across every entity: semantic subtitles may sit under the title, but descriptions and synopsis text render only after the primary and utility action rows and the progress row.
- **Review Queue** is only for blocked, uncertain, or low-confidence items that need human confirmation.
- **Operations** is one Administration destination at `/settings/ingestion`. It combines the live operational summary with bounded batch history: current run state, truthful phase-specific progress, grouped active/queued work, current-run outcomes, media mix, provider waits, and the three newest batch rows. Search and quick outcome filters apply to history, and **Show older** appends another server-paged group of rows. Selecting a batch opens the shared filterable media browser at `/settings/ingestion?runId=<guid>&view=all`. There is no separate Activity & Audit page or `/settings/activity` route. Durable audit records remain backend facts available through batch media details and diagnostics. File completion must never be presented as whole-run completion while required identity, enrichment, or organization operations remain. Active selection and history must use durable identity-job and operation state, including resumed batches with stale stored status. Startup must recover unleased intermediate work as well as leased jobs; elapsed time alone must not fail recoverable work. Ingestion cards and drawers show only added track, episode, or issue counts, without provider totals or per-item progress bars. Both activity indicators use the shared success green. The screen refreshes automatically; its single **Scan now** action explicitly starts an extra scan of watched folders. On mobile it uses the same state in a compact summary. The navbar activity indicator opens Ingestion for all authorized system activity.
- **Settings/Admin** is for configuration and system operations: library folders, provider setup, profiles and roles, device/profile UI settings, ingestion status, system health, logs, and diagnostics.
- **View** is the personal mixed-local-media surface at `/view`. Each enabled profile owns one Personal Space backed by an internal `personal` library bridge; any number of sources/devices may feed that space without becoming separate libraries. The standard View shell has Photos (`/view`), Folders (`/view/folders`), Galleries (`/view/galleries`), People (`/view/people`), and Places (`/view/places`). Folders preserves source and nested-folder identity with URL-backed breadcrumbs, including a first-class Shared Library root in Shared scope. Browser uploads use `Timeline/<year>/<month>/<date-time><type>.<ext>`; additional linked or managed folders preserve their hierarchy and opt into Photos independently. Mine contains only the active profile's Personal Space; Shared Library contains only accepted server-owned items. Shared Library access, submission, contribution review, and Gallery sharing are independent administrator policies. Selected owned items are submitted through `/view/contributions`; pending, declined, and cancelled submissions do not change files. Acceptance records the curator decision before an asynchronous verified transfer. Managed originals move only after every Shared destination verifies; linked read-only originals are copied. Durable per-item state and an audit timeline expose completion, cleanup pending, and recovery. The contribution surface retains `View > Shared Library > Contributions` breadcrumbs and does not become a sixth primary View destination. Collections may reference a whole Gallery or a versioned View rule, never individual local asset IDs, and must reapply View authorization. View assets never enter catalogue identity, provider, Wikidata, canonical-claim, or Review Queue workflows. People and Places report only real provenance-aware annotations/GPS metadata; face recognition, object/scene detection, OCR, semantic search, maps, public links, and mobile backup/sync remain unimplemented until backed by real clients/processors.
- Library behavior is policy-driven. Only `catalogued` and `personal` library kinds exist. Catalogued libraries normally use enriched metadata; personal libraries may use local-preferred, local-only, or manual metadata and belong in View. Local-only and manual content must not be sent to external identity or metadata providers.
- System backup archives contain a consistent SQLite snapshot, a manifest, and non-secret configuration. Restore validation and staging occur while the Engine is running; application happens before the data store opens on the next Engine start, preserving the previous database as a recovery copy.

The old all-in-one management workspace has been removed and must not be rebuilt. Do not add routes, navigation, docs, tabs, or a separate media-fixing workbench for it. Normal media corrections launch `SharedMediaEditorShell` through `MediaEditorLauncherService.OpenAsync`; Review uses the same modal editor in review mode.

Current UI entry points:

1. `src/MediaEngine.Web/Shared/MainLayout.razor` for the global shell, search, My List, unified activity indicator, permission-aware account menu, engine status, command palette, and persistent playback host. The account menu owns Needs Review attention; do not restore a standalone notification button.
2. `src/MediaEngine.Web/Components/Pages/LibraryBrowsePage.razor` for Home/discovery.
3. `src/MediaEngine.Web/Components/Cinematic/CinematicHeroCarousel.razor`, `CinematicHeroSurface.razor`, and `SurfaceNavigationBar.razor`, plus `src/MediaEngine.Web/Components/Details/DetailHeroContent.razor`, for Home/detail cinematic presentation and shared navigation.
4. `src/MediaEngine.Web/Components/Pages/ReadPage.razor` and `src/MediaEngine.Web/Components/Pages/WatchPage.razor` for the `/read` and `/watch` lane landing pages.
5. `src/MediaEngine.Web/Components/Browse/MediaBrowseShell.razor` for detailed Read/Watch/Listen browse behavior under tab routes.
6. `src/MediaEngine.Web/Components/Details/DetailPage.razor` and `src/MediaEngine.Web/Components/Universe/BookDetailContent.razor` for detail surfaces and modal edit launch points.
7. `src/MediaEngine.Web/Components/Settings/SettingsReviewQueueTab.razor` for the exception queue.
8. `src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor` for normal, review, and batch editing.
9. `src/MediaEngine.Web/Components/Settings/IngestionTasksTab.razor` for the Ingestion dashboard at `/settings/ingestion`; it uses the Engine `GET /ingestion/operations` snapshot plus SignalR ingestion progress.
10. `src/MediaEngine.Web/Services/Playback/PlaybackSessionController.cs` and `src/MediaEngine.Web/Components/Listen/ListenTransportControls.razor` for Listen playback state, typed commands, and shared transport controls.

Treat stale references to old all-in-one workspace components, retired CSS prefixes, broad management workbenches, or removed-workspace docs as cleanup candidates.

## Quality Gates and Regression Rules

- **Pre-beta destructive cutover and validation rule:** until the product owner explicitly declares the product ready for public beta or changes this rule, solve ingestion, enrichment, identity, organization, and artwork defects systemically in the normal pipeline. Do not add repair jobs, data backfills, corrective migrations, startup fixups, or one-off code that mutates an existing catalogue to compensate for a defect. Validate the corrected behavior by wiping disposable development application state and performing a fresh ingest from the configured source media. Development database and configuration state may be deleted, reset, or rebuilt to reach the current target architecture. Do not add backups, data-preserving migrations, compatibility readers/writers, route aliases, dual schemas, or legacy fallbacks for pre-beta state. Fail fast on obsolete state and reingest. This rule applies only to disposable application/configuration state; existing/read-only media sources and user-owned originals must still be protected from unintended moves, renames, writeback, overwrite, or deletion.
- Do not recreate the old all-in-one management workflow. No new routes, implementation types, navigation labels, docs as current product behavior, or all-in-one media correction workbenches for it.
- Do not add backward compatibility, legacy fallback paths, compatibility readers/writers, migration shims, old route aliases, old config-key support, dual-schema support, or old workflow facades unless the user explicitly asks for that exact compatibility behavior. Prefer failing fast with a clear error or removing unsupported legacy state. If compatibility is explicitly approved, document the request, scope, sunset/removal criteria, and tests in the same change. Legacy database backup/reset is allowed as data safety, but not as in-place migration support.
- Normal detail-page fixes, Review, and Batch all use `MediaEditorLauncherService.OpenAsync` to host `SharedMediaEditorShell` in a modal while the underlying detail page remains mounted at the same URL. TV episode titles in owned episode lists open the episode detail surface, while the adjacent explicit Edit action launches the episode-scoped editor directly. Keep normal Details lean (presentation overrides plus profile-local library preferences), keep provider facts read-only, and put structural parent moves in Matching.
- Single-item editing keeps metadata, local fields, and sorting in Details; it does not expose a separate Options tab. File shows physical-file state only, while History owns identity, metadata, artwork, and ingestion events. A retail rematch synchronously replaces provider-managed artwork and refreshes the detail hero before background Wikidata alignment proceeds.
- Main Listen host adapters read `PlaybackSessionController`; reusable controls consume captured snapshots and command sinks rather than another circuit's controller. Shared transport stays in `ListenTransportControls`; browser-specific transport belongs behind the persistent Web audio host and `listenPlayback` JS bridge. Music shuffle/repeat, queue/history access, and audiobook speed/chapters/history/bookmarks/sleep tools remain available from the bottom player and popout. Music play counts are profile-scoped and qualify after 30 seconds of genuine listening, or 50% for tracks shorter than 30 seconds; forward seeks do not count as listening time.
- Player utilities use shared thin outline glyphs, bare 22px visuals and at least 44px targets. The flush audio dock keeps transport independently centered and its seek rail on the top edge. Desktop/tablet Close player remains visible outside utility overflow, saves guarded paused resume and stops only the captured session. Phone Collapse is presentation-only; phone players have no session-stop Close. PlaybackFullPlayer supplies the shared snapshot/sink phone and popout UI; only the phone supplies Collapse. The popout fills its 420 by 780 default window and exposes no in-player exit. Canonical identity links navigate through the authorized main owner without reloading audio; native cleanup releases only its matching popup registration. Audio tools use anchored popovers or bounded modal sheets without page reflow; Ingestion retains its layout-sidebar lease and resize behavior. Shared controls use captured snapshots and direct/broadcast command sinks against the same playback owner. Speed uses the shared slider popover; Sleep uses the central flat select with one menu scroll owner. Sleep choices request an authoritative arm whose selected minutes, absolute deadline and verified chapter boundary are independent of bookmark draft leases. Bookmarks retain one captured Add/Saved dialog anchored to its local opener on desktop and a bounded phone sheet. Do not restore stacked workspaces, immediate-add shortcuts or duplicate Saved lists. Follow `docs/architecture/playback.md`; current remediation status and verification limits are recorded in `docs/reports/remediation-2026-10-04.md`, which supersedes the earlier dock/sidebar layout in the October 2 refinement report.
- Review Queue is the exception workflow for blocked, uncertain, low-confidence, or unresolved items. Settings/Admin is for configuration and operational state, not a normal media correction workspace.
- Use `IDatabaseConnection.CreateConnection()` for normal repository, read-service, endpoint, background-job, and request-path database work. Dispose each short-lived connection with `using`.
- `IDatabaseConnection.Open()` is startup/schema/integrity-only. New uses outside `DatabaseConnection`, Engine startup, or explicitly documented test fixtures should fail guardrail tests.
- Current storage epoch is `guid-blob-v1`: internal SQLite GUIDs are 16-byte BLOBs, external IDs remain TEXT, API JSON still returns GUID strings, and legacy TEXT-GUID databases are reset/reingested rather than migrated in place.
- SQLite connections use WAL plus `synchronous=NORMAL`, `busy_timeout=5000`, memory temp storage, a 16 MiB page cache target, and a 256 MiB mmap cap for local ingestion throughput. The accepted tradeoff is that an OS/power crash can require reingesting the most recent file changes.
- Microsoft.Data.Sqlite does not provide asynchronous I/O. Repository APIs may
  remain `Task`-shaped, but SQLite/Dapper commands execute synchronously after a
  cancellation check; do not use their misleading async methods or wrap
  database work in `Task.Run`.
- Transactional writes use `IDatabaseConnection.ExecuteWriteAsync`. Only the
  global writer-semaphore wait is asynchronous; the callback is synchronous,
  bounded SQLite work that commits or rolls back before the lock is released.
- `canonical_values` is scalar-only. Multi-valued metadata belongs in `canonical_value_arrays`; do not reintroduce packed delimiter storage or compatibility readers.
- Provider/Wikidata/Wikipedia text shown in the Dashboard needs source attribution when available: provider name, source title, source URL, license, retrieved timestamp, and whether the displayed value was modified or summarized.
- UI comma-separated lists use standard English punctuation: no space before a comma and exactly one space after it. Linked genre pairs must preserve that grammar and remain together without wrapping between genres.
- Read, Watch, and Listen detail heroes use one shared identity column: logo or written title, sequence context, credits, and compact facts align to the left edge of the primary action button. Utility actions use icon-left-of-label controls, grow from the same left edge, and sit on the line below the primary action.
- The Engine detail composer selects a typed primary detail module; the Dashboard must not infer the module from route or tab names. A person's **Works in Your Library** contains only owned media from canonical eligible contributor credits, deduplicates each work, preserves all eligible roles on that work, and supports lane and role filtering. It must never expand into an external career filmography or use broad `person_media_links`.
- Detail navigation occupies a translucent shelf at the foot of the `95svh` cinematic stage so the backdrop continues visibly beneath it. Structural arrays use their own full-width surface below navigation and render only with Overview; Overview combines the full attributed description or biography, a purpose-built role-aware cast/credits peek, and series or collection context when applicable. Related content belongs in its own tab. People are the exception: biography, details, and owned works form one continuous page with no tab shelf. Music albums use **Overview** and **Details** tabs: Overview contains only the borderless track list, a right-side role-aware clickable credits rail, and the conditional full owned-album **More by** shelf, while source and technical detail content renders only under Details. Do not repeat the primary array in other tabs or Details. Audiobook tracks use the same borderless Overview table and Overview/Details navigation as music albums; do not restore a hero-embedded audiobook Chapters module, Editions, Play All, track search, or a duplicate My List control inside the list.
- Home and detail heroes must compose `CinematicHeroSurface`/`CinematicHeroCarousel` with `DetailHeroContent`; do not create a second cinematic identity, facts, action, progress, or synopsis renderer. Read, Watch, Listen, and Collections do not render landing heroes. Home has no lane-scope submenu. Lane scope controls are real routes, while detail lower navigation uses `SurfaceNavigationBar`. The Home slide background opens details; group-opening actions such as **Open Show** use an open affordance, while direct media actions start playback or reading and progressed Home slides use the lane-specific Continue label plus Resume/Restart behavior where applicable. Keep item-specific detail route state and content unchanged when evolving navigation.
- Listen landing shelves are album-first. Do not emit individual music-track cards into Home or Listen landing rows; group newly ingested tracks into album cards and expose track identity through `DisplayCardDto.PreviewItems` and album/detail routes.
- UI display artwork should use managed Engine URLs or settled placeholders. Do not expose stale direct provider image URLs on lane cards, shelves, albums, detail pages, search results, or review cards.
- Avoid silent `catch { }` blocks. Best-effort failures need a justification comment or an explicit guardrail allowlist entry; user-visible failures need logging and degraded/error UI.
- Domain must stay independent of Web, API, Storage, Providers, Ingestion, Processors, AI, and UI packages. UI should consume view models/contracts/typed clients, not storage implementation models.
- `MediaEngine.Contracts` is the sole owner of product-defined types serialized
  over non-frozen HTTP or SignalR boundaries. API/Application/Domain models must
  cross through explicit boundary mappers; Dashboard-only wrappers may add
  presentation state but must not mirror or redefine the JSON contract.
- The only frozen wire exception is the exact Dashboard-local
  `LoreDeltaDiscoveredEvent` and `UniverseEnrichmentProgressEvent` SignalR pair.
  Universe HTTP DTOs are not exempt, and the exception must not gain types or
  fields.
- Preserve contract `[JsonPropertyName]` values, casing, defaults, nullability,
  collection shapes, and editor-required mutability. Contract completeness
  includes ingestion `count_unit`, person/collection roles, complete AI
  settings and `cpu_pressure`, and complete collection, playback/text-track,
  and plugin fields; do not reintroduce narrower API or Web mirrors.
- Keep `BoundaryContractGuardrailTests`, `WireContractSnapshotTests`, and
  focused contract shape/round-trip tests as ratchets. New boundary debt and a
  growing frozen exception are build failures.
- Domain aggregates expose child collections, property bags, and lifecycle state as read-only views. Mutate them through explicit aggregate methods (`Work.LinkToWikidata`, `Collection.SetVisibility`, `Collection.ChangeResolution`, and the named child methods), and keep repository hydration explicit instead of making aggregate internals public again. Persisted aggregate enums convert only through `AggregateStateSerializer`; unknown values fail fast.
- Razor components must not contain direct SQL. API endpoints should move SQL-heavy behavior into repositories/read services when touched.
- When product concepts, navigation, editing flows, database lifecycle, Docker startup, or CI checks change, update README/docs/AGENTS/CLAUDE and relevant `.agent` guidance in the same change.
- Every Dashboard code change requires visual validation of each affected surface at a representative desktop viewport (including 1920×1080 when layout scale is involved) in addition to automated tests. Capture the rendered state before and after changes when correcting a visual regression, and verify that responsive or lower-height rules do not override the intended desktop result.
- Before finalizing code changes, run at minimum `dotnet restore MediaEngine.slnx`, `dotnet build MediaEngine.slnx --no-restore`, and `dotnet test MediaEngine.slnx --no-build`. Run docs, Docker, format, and dependency checks when those areas are touched.
- For sequence/artwork ingestion fixes, validate with a fresh ingest rather than in-place repair. Stop Engine/Web, wipe only `C:\temp\tuvima-watch\books`, `C:\temp\tuvima-watch\audiobooks`, `C:\temp\tuvima-watch\tv`, `C:\temp\tuvima-watch\movies`, `C:\temp\tuvima-watch\music`, `C:\temp\tuvima-watch\comics`, and `C:\temp\tuvima-library`, then restart and inspect database/API results before visual Dashboard checks.

## Repository Map

### Main application code

- `src/MediaEngine.Api`
  - The main composition root.
  - Focused `DependencyInjection/Tuvima*ServiceCollectionExtensions.cs` modules
    register services, repositories, background jobs, health checks, SignalR,
    and API endpoints.
  - If you want to understand what the whole system starts and how parts are wired together, start here.

- `src/MediaEngine.Web`
  - The Blazor Server dashboard.
  - Uses typed HTTP clients to call the Engine API.
  - Uses SignalR for live progress and activity updates.

- `src/MediaEngine.Domain`
  - The shared language of the system.
  - Holds core aggregates, entities, enums, constants, shared configuration
    models, and inward-facing interfaces.
  - This project defines things like assets, editions, works, collections, universes, profiles, and the contracts other layers implement.
  - `Services/` also hosts the cross-cutting shared primitives every layer uses instead of re-implementing: `StringHelpers` (FirstNonBlank/FirstNonBlankOr), `MediaTypeParser` (canonical media-type alias table), `Hashing` (Sha256Hex, DeterministicGuid), `MediaMimeTypes`, `MediaEngineJson` (cached JsonSerializerOptions), and `EpisodePatterns` (shared SxxExx regex). Guardrail tests ban private re-declarations.

- `src/MediaEngine.Contracts`
  - The canonical HTTP and SignalR wire assembly shared by Engine and Dashboard.
  - Owns request, response, nested payload, and real-time event shapes; it does
    not own Dashboard presentation state or persistence projection rows.
  - Boundary mappers adapt internal models into these contracts when the shapes
    are not already identical.

- `src/MediaEngine.Storage`
  - SQLite repositories, embedded schema bootstrap, startup migrations, and config loading.
  - Every concrete repository belongs here. API services must not hide
    persistence behind `Store` or `DataService` names.
  - This is where persistent state lives: media records, review queue, activity log, provider cache, search index, reader progress, UI settings cache, and more.

- `src/MediaEngine.Ingestion`
  - The file intake pipeline.
  - Watches folders, debounces file system noise, hashes files, detects duplicates, runs processors, creates review items, and triggers organization/write-back work.

- `src/MediaEngine.Processors`
  - Reads actual media files.
  - Extracts metadata from EPUB, audio, video, comics, and generic files.
  - Also contains supporting media services like FFmpeg-based extraction and hero-banner generation.

- `src/MediaEngine.Intelligence`
  - Decides which metadata wins when sources disagree.
  - Handles scoring, fuzzy matching, identity matching, and collection arbitration.
  - The key mental model is "decision logic," not storage or UI.

- `src/MediaEngine.Providers`
  - Talks to outside metadata sources and runs the hydration pipeline.
  - Includes config-driven provider adapters, reconciliation, person enrichment, search, universe graph population, and deferred enrichment services.
  - A lot of provider behavior is driven by JSON config instead of hardcoded subclasses.

- `src/MediaEngine.AI`
  - Local model configuration, model downloads, model lifecycle management, hardware benchmarking, and AI-powered features.
  - Model roles are selected through `config/ai.json` `model_catalog` and `role_requirements`; prefer the smallest model that passes validation gates instead of promoting larger models because hardware is available.
  - Features include smart filename cleaning, media type advice, vibe tags, TLDR generation, taste profiling, description intelligence, cover-art checks, and Whisper-based audio work.

- `src/MediaEngine.Identity`
  - Profile management and multi-user rules.
  - Small project, but important for roles and user-facing account behavior.

### Supporting folders

- `tests/`
  - One test project per major source project.
  - Mirrors the production code layout closely.

- `config/`
  - Extremely important in this repo.
  - Holds core settings, libraries, providers, AI settings, scoring priorities, pipelines, hydration behavior, UI/device profiles, narration phrases, and secrets overlays.
  - Many behavior changes happen here without changing C# code.

- `docs/`
  - Product and architecture documentation.
  - Organized into tutorials, guides, reference, explanation, and architecture folders.

- `docker/`
  - Container entrypoint and packaging support.

- `scripts/docs/`
  - Documentation build and serve scripts.

- `tools/`
  - Local utilities and bundled support assets such as FFmpeg, reports, and test data.

- `assets/`
  - Branding images and screenshots.

## How The Parts Fit Together

Think of the product as a pipeline with a UI on top:

```text
Watch folders
  -> Ingestion
  -> File processors
  -> Intelligence/scoring
  -> Retail identification (Stage 1)
  -> Wikidata bridge resolution (Stage 2)
  -> Quick hydration
  -> Universe enrichment (Stage 3)
  -> SQLite + .data/assets + file organization + write-back
  -> API + SignalR
  -> Dashboard
```

In plain English:

1. A file appears in a watched folder.
2. The ingestion layer notices it, waits for file activity to settle, and makes sure it is not a duplicate.
3. File processors inspect the file itself and extract whatever metadata is already available.
4. Intelligence services decide the best current title/author/type values from the claims gathered so far.
5. Retail Identification (Stage 1) runs the configured provider chain for the media type. Music is configured as MusicBrainz first for canonical recording/release IDs, then Apple for artwork and retail metadata; other lanes use their configured retail/catalogue providers for cover art, descriptions, ratings, and bridge identifiers.
6. Wikidata Bridge Resolution (Stage 2) uses bridge IDs from Stage 1, such as ISBN, ASIN, TMDB ID, MusicBrainz ID, and Apple IDs, to find the canonical Wikidata entity (QID). This provides universe linkage, person relationships, and canonical metadata. If Stage 2 finds no QID, the item keeps its retail data and is flagged for periodic re-checking.
7. Quick Hydration gets the item visible with core identity, canonical values, and managed artwork.
8. Collection finalization assigns the item to its lane shelf and resolves any broader parent collection only when trusted shared relationships exist. This also runs for retained retail identities that have no Wikidata QID.
9. Universe enrichment (Stage 3) expands people, fictional entities, relationships, narrative roots, lyrics/subtitles, and additional artwork.
10. Storage writes the results into SQLite and stores managed artwork/headshots under `.data/assets/...` through `entity_assets` or the relevant person/entity table. Sidecars beside media files are optional exports only.
11. Organization and write-back services can move files into the library structure and write metadata back into the files.
12. The API exposes all of that state to the Dashboard.
13. SignalR broadcasts live events so the UI can update while ingestion and enrichment are happening.

Two-pass enrichment: The Quick pass gets the item visible on the Dashboard fast (core identity + cover art). The Universe pass runs later in the background for deep enrichment (relationships, people, fictional entities, additional images).

Canonical deep-dive: `docs/architecture/ingestion-identity-enrichment-pipeline.md`.

## A Non-Developer Guide To The Data Model

These are the most important concepts:

- **MediaAsset**
  - A real file you own on disk.
  - Example: one `.epub`, one `.m4b`, one `.mkv`.

- **Edition**
  - A specific release or format of a story.
  - Example: "First Edition Hardcover", "Director's Cut", "4K Blu-ray".

- **Work**
  - The underlying story or title, independent of format.
  - Example: the idea of *Dune* as a book or film title, not one specific file.

- **Collection**
  - The main grouping/browsing container for related works.
  - Depending on context, a collection may behave like a universe, a smart collection, a system list, a mix, or another discovery surface.

- **Universe**
  - A larger umbrella that can contain multiple collections.
  - Useful when several related groups belong to one broader narrative world.

- **Profile**
  - An experience identity with preferences, restrictions, history, and a Personal Space.
  - The active profile must be backed by a live account/profile grant. Settings navigation uses Engine-projected authority; browser storage and seed profiles do not grant access.

- **Review Queue**
  - The safety net for uncertain matches.
  - If the system is not confident enough, it stores the item for human review instead of pretending it knows the answer.

The easiest way to explain the hierarchy is:

```text
file on disk -> MediaAsset -> Edition -> Work -> Collection -> Universe
```

Not every item reaches every level. Some files remain standalone if the system cannot confidently link them to a larger story world.

## What The Engine Actually Does At Startup

Reading `src/MediaEngine.Api/Program.cs`, the Engine does far more than expose HTTP endpoints. It also:

- opens SQLite and runs schema initialization plus startup migrations
- loads structured config from `config/`
- registers providers and HTTP clients
- wires up ingestion, scoring, hydration, search, and write-back services
- starts recurring background jobs
- sets up local AI model management and auto-download
- warms caches
- starts SignalR for real-time events
- exposes health checks and API endpoints

That file is the best "whole-system map" in the repository.

## Configuration Matters More Than Usual

This codebase is not only code-driven. A large amount of product behavior lives in JSON:

- `config/core.json` controls core paths and runtime behavior
- `config/libraries.json` describes watched libraries and media-type hints
- `config/providers/*.json` defines provider behavior
- `config/ai.json` defines local models, AI feature flags, and schedules
- `config/pipelines.json`, `config/scoring.json`, and `config/field_priorities.json` influence how metadata wins
- `config/ui/` controls palette, device profiles, and UI defaults
- `config/secrets/` overlays provider credentials

Before changing C# for behavior that looks configurable, check `config/` first.

## Best Entry Points For Reading The Code

If you are new to the repo, read these files in roughly this order:

1. `README.md`
2. `src/MediaEngine.Web/Shared/MainLayout.razor`
3. `src/MediaEngine.Web/Components/Browse/MediaBrowseShell.razor`
4. `src/MediaEngine.Web/Components/Details/DetailPage.razor`
5. `src/MediaEngine.Web/Components/Settings/SettingsReviewQueueTab.razor`
6. `src/MediaEngine.Api/Program.cs`
7. `src/MediaEngine.Api/DependencyInjection/`
8. `src/MediaEngine.Web/Program.cs`
9. `src/MediaEngine.Ingestion/IngestionEngine.cs`
10. `src/MediaEngine.Providers/Services/HydrationPipelineService.cs`
11. `src/MediaEngine.Intelligence/PriorityCascadeEngine.cs`
12. `src/MediaEngine.Storage/ConfigurationDirectoryLoader.cs`
13. `src/MediaEngine.Storage/DatabaseConnection.cs`

That sequence explains the product, the runtime wiring, the intake flow, the enrichment flow, the decision logic, and the persistence/config layer.

## Practical Reading Notes

- Ignore `bin/`, `obj/`, `.data/`, and `logs/` when learning the architecture.
- Expect product branding (`Tuvima`) and code naming (`MediaEngine`) to coexist.
- The Engine is the operational center of the app.
- The Dashboard is mostly a client of the Engine, not the place where core business rules live.
- Tests are organized by layer, so they are often the fastest way to confirm intended behavior once you know which project you are in.

## Current Collections and linked-identity clarifications

- Collections uses one viewport-bounded, padded content scroller beside its anchored rail; never allow a second document scrollbar.
- Automatic rollups prioritize trusted fictional-universe, franchise, and based-on/adaptation QIDs over lane-local parent/series structure. A cross-media identity such as Batman can therefore combine qualifying comic and film groups.
- People presence counts distinct top-level works: tracks collapse to albums and episodes collapse to shows.
- Person names use the exact shared media-detail title family, scale, weight, responsive density, and lower-left identity anchor through the rendered person copy hook. Person dates, locations, and owned-title facts reuse the standard detail metadata row/item typography used by year, runtime, and genre. The portrait may remain centered independently; it must not vertically center the identity column.
- Musical groups resolve all canonical Wikidata `has parts` identities into `person_group_members` and queue incomplete people for full hydration. Collective pen names continue to use aliases; band members must never be rendered as pseudonym aliases.

## TV episode context and personal status (September 2026)

TV detail uses one profile-aware continuation policy: unstarted/reset shows retain series artwork, active shows use the current or next owned episode still, and explicit episode details stay episode-scoped. Completed owned runs offer rewatch with series artwork. Episodes use short episode synopsis text, never Wikipedia extracts. Missing catalogue entries have no synopsis or artwork and retain the Not in library placeholder. The episode rail scrolls without a four-card cap.

TMDB episode credits are stored separately from show aggregates; full credits support episode, season, and role filtering. Season coverage currently reflects owned episode evidence. More exposes media-appropriate personal completion/reset, history, Undo, Continue visibility, and applicable queue/playlist/collection utilities independently of metadata-edit permission. Revision checks prevent stale progress writes from reversing a reset; bookmarks, genuine consumption history, and music play counts are preserved.

These changes require fresh pre-beta ingestion. Runtime and responsive visual acceptance are pending; see the TV episode consistency proposal for validation status and remaining scope.


## Libraries settings navigation and safety (September 2026)

Libraries keeps one Settings-shell breadcrumb, with configured library names and URL-backed Read/Watch/Listen scope when returning to the overview. Library details consolidate Folders, Organization, File Handling, and Advanced Settings; do not restore detail tabs. Header and list rows share one full-width column grid.

Add Library has exactly three steps: Choose type, Add folders, Review. Structured Media creates one catalogued library. Personal Media configures the existing shared View root and reserves profile identities through View services. It must never add user-facing personal library rows. Folder selection retains separate path breadcrumbs and revalidates at confirmation and save. Changing a populated View root is rejected; no relocation is implied.

Existing-file protection applies to managed originals through the shared mutation gate. Incoming staging files and destination use remain eligible; existing-library sources remain read-only. Naming and duplicate policies must be enforced by ingestion, with unique-name filename collisions preserving existing files.


### Libraries settings and readable View storage

Libraries now includes All/Read/Watch/Listen/View filters, a single View summary row, and a dedicated View settings page. Scope changes filter the mounted list without repeated library loads or filesystem probes. Add folder belongs inside Folders and saves immediately; detachment asks for confirmation and keeps files. Names and custom templates use focused Apply dialogs, while other library settings save individually with conflict detection. There is no page-wide Save. Breadcrumbs remain shell-owned and retain the selected scope.

New Personal Spaces use `View/Profiles/<stable-readable-label>/Timeline` and named managed folders beneath `Folders`. Persisted label reservations survive profile deletion so another profile cannot silently inherit the former folder. Display-name changes do not rename storage. `View/Shared` is the server-owned Shared Library beside Profiles. Existing obsolete View source state fails closed rather than moving originals or using compatibility paths. View source additions queue reconciliation in its dedicated hosted worker.


## Setup, Operations, and View storage (September 2026)

Setup offers an optional 4–12 digit profile PIN alongside the administrator password, live password validation and reveal controls, and readable recovery codes. Media locations can be explicitly deferred and configured later through Libraries. Reloading setup must render fetched stage data after asynchronous initialization.

Operations and the navbar consume the same Engine batch progress definition, including outstanding identity and enrichment work. Live media selection includes identity-job updates, ranks active/recent groups before limiting, and preserves server order on refresh. Comic covers remain issue-scoped. TV and comic ingestion counts never display a provider denominator. Active batches stay above server-filtered historical batches; every historical action is Open batch. Review counts remain visible throughout Settings. Processing details is removed.

View uses a common library-detail header and Folders, Organization, File Handling, Sharing & Access, and Advanced Settings sections. The configurable View library root contains Shared and Profiles; Shared Library is an authorization scope distinct from the physical base. Logical personal-space/source registration never creates directories. Uploads and accepted transfers create destination folders only at actual write time. Merely visiting View or adding a profile must leave unused storage empty.

The install banner records dismissal in browser/site local storage. Subsequent install events and navigation respect it. System Overview offers an independent manual installation action.

## Access authority (September 2026)

Accounts own feature and actual library grants; profiles retain experience and privacy identity. Effective administration requires the current enabled account and active AdminEnabled grant, plus any optional surface PIN unlock. Applications own permissions and revocable credentials; delegated native access intersects current account, grant, device, token, and consent. Check resource scope before counts, grouping, paging, artwork, streams, and personal state. Preserve distinct View scopes. Follow docs/architecture/security.md and the Access execution status; do not restore legacy roles, localhost administration, or a seed-owner fallback.

## Shared AI storage and workspace retention (September 2026)

Local builds use external versioned AI runtimes. Configure TUVIMA_MODELS_DIR and TUVIMA_AI_RUNTIME_DIR; the workstation uses E:\Resources\AI Models and E:\Resources\AI Runtimes. Provision with tools/Install-AiRuntime.ps1. Do not restore native backend packages to ordinary app/test references, copy model weights into repositories, or remove the build-output guard. Bundled deployment assets require TuvimaBundledAiRuntime=true (Docker uses TuvimaContainerBuild=true). See docs/guides/shared-ai-storage.md.

Keep only tuvima-library and tuvima-wikidata in Repos. Use the existing checkout for sequential work; necessary temporary worktrees belong outside Repos (under the user's local Tuvima/Worktrees area), with at most two active. Retire them after integration, preserving unique files first. Never create sibling task folders or nested .codex-runlogs/worktrees again. Shared models use interprocess leases; tests must use isolated temporary model files.

## UI action alignment completion rule

Before finishing any UI work, verify action alignment at desktop, tablet, and mobile breakpoints. Buttons beside labeled form fields must align with the input control rather than the label or helper text. Buttons in list or detail rows must remain in the row's intended action column and must not auto-flow onto a separate row that creates avoidable empty space. A separate full-width action row is acceptable only when it is an intentional mobile layout. Do not report UI work complete until these alignments have been checked in the rendered layout or enforced with explicit grid/flex placement and regression coverage.

## Editor relationship and identity model

- Keep three models distinct: structural media placement (Series/parent/position), authored-container canonical title and order, and the knowledge graph (Universe, fictional entities, and sourced/qualified links). Stage 2 establishes canonical Work identity; bounded Stage 3 enriches grounded graph facts and artwork without silently rewriting authored containers or structural placement.
- The normal media editor uses one stable parent header with adjacent child selectors and a single highlighted editing target. Keep audiobook chapter-title overrides under Chapters. Match information belongs at the top of Details, never in header/selector badges; inherited canonical identity needs no status label. Retail Match owns provider identity plus proposed structural placement; preview previous/target paths before Apply. Do not expose Change Type in the normal editor or Review Queue.
- Universe/entity editing remains content within `SharedMediaEditorShell`, opened from the authorized Edit actions on the Universe Explore surface or its selected entity. Media detail pages link actual accessible Universe relationships; media editors contain no Universe shortcut or mode switch. Keep the Universe header stable and preserve the existing dirty-state guard for category/entity navigation.
- Render graph sections from server readable capabilities, including friendly Organization Members and Event Participants projections; keep full Relationships separate. Category/sibling selectors are anchored, viewport-bounded, searchable popovers with keyboard/focus behavior, while the category rail remains one horizontally scrollable row with explicit overflow controls.
- Managed EntityAsset artwork belongs to its Universe/entity owner and must not be inferred from a work or parent container. Appearance and relationship facts remain work-scoped and preserve role, context, anchor, temporal, provenance, and spoiler qualifiers. Real-world dates, in-universe narrative time, structural sequence order, and editor History are different data and presentation concepts.

## Shelf editing and owner artwork

Structural shelves edit their own parent Work through the shared editor. The Engine declares supported artwork slots and automatic-group presentation; do not duplicate media/scope capability switches in the Dashboard. Shelves preview up to four ordered owned covers, accept a shelf-owned custom cover, and can restore the automatic stack. Title and description overrides remain local presentation choices and must agree across editor, detail, and browse. Group routes and membership resolve by structural owner identity rather than a customized display title. Season selectors use managed SeasonPoster artwork and episodes use EpisodeStill; absent owner artwork remains a placeholder.


Editor Details names the active editing target independently of the stable parent header and shows compact Retail/Canonical match checks. TV matching is exposed only on episode targets, never series or seasons; the inherited canonical record remains visible for context without an editor jump to the series. Match searches retain the current identity until the user explicitly applies or overrides a result. Logo previews preserve the complete image at its natural ratio.

### October 2026 remediation follow-up

Home previews reserve resting shelf geometry before expansion and use a bounded cinematic layout for movies/TV, with artwork and logo only; Home Continue retains episode identity and truthful progress. Home Watch discovery cards expand in-row to a landscape background at the resting cover height, shifting neighboring cards horizontally; Watch pages and episode lists highlight only; non-Watch cards remain outline-only. Shelf arrows have dedicated rails and minimum 44px targets. Continue groups have natural whole-card widths, 32px dividers/gaps and stack below 1280px. Shared book foreground height, perspective, page/spine geometry and proportional shadow live in `HeroBackdrop`; detail stages retain 95svh. Known small Home sources use backdrop framing; unknown dimensions use the conservative fallback.

The shared primary action uses the larger default requested by the product owner for Play, Read, Listen, Resume and Continue: 22rem wide and 4.6rem high, capped to the available phone width. Plain actions use larger text/icons; continued actions show a second percentage line and embedded progress. Adjacent My List, Rate and More are 56px circles; Shuffle remains a circle beside album Play. Restart belongs in More and preserves existing query/fragment state while starting at position zero. Progress includes a visible watched/read/listened percentage and a slim strip inside the primary action. Rate choices are Likes/Dislikes; only a song's Love/heart adds it to Favorites. My List is saved-item state, not a rating. Song menus operate on the selected song and use guarded active-profile/snapshot identity.

Home refreshes are coalesced and profile-bounded: progress is limited to once per minute, library-state changes to once per 15 seconds, new media to a two-second debounce, pause to five seconds, and dismissal/stop to immediate refresh. Transport ticks do not rebuild shelves or Recently Added. Background Recently Added refresh keeps the mounted cards visible and deduplicates paging.

Audio, phone/popout and Watch/View video use `PlaybackSeekRail`: total duration sits at the right end and toggles to remaining time; elapsed time appears in the focused/hovered/dragged seek bubble rather than a separate line. Chapter name/time shares that bubble. Speed uses the bounded 0.5–3.0 slider in 0.05 steps with reset to 1×; stored rate precision is retained until changed. Sleep retains its existing select and authoritative deadline.

Watch/View video use shared bottom chrome, a Back affordance, control hover/focus holds, and one subtitle-positioning owner in `playback-chrome.js`. Active cues sit 12px above the seek rail while controls show and restore authored settings when hidden or detached. Up Next uses owned eligibility and separates Up Next/Episodes/Chapters; no provider-only episode is promoted. Native cue animation is browser-dependent and is not promised.

Lyrics content omits the former version/provider toolbar. Enhanced LRC strips inline timestamps into independently timed words, uses native audio time, and keeps client highlighting separate from server render updates. Only explicit instrumental markers, explicit word-end gaps, known intro/outro boundaries produce three-dot sections; ordinary long line spacing never invents a musical break. Three configurable durations live in `config/ui/playback-client.json`. Unknown duration prefers timed lyrics. Authorized empty state opens Manage lyrics in the existing editor Details tab. LRCLIB attribution appears for LRCLIB tracks.

Editor artwork previews are padded and contained. Open full size opens the authenticated original image in a new tab and removes rendition-size query parameters. Canonical entity artwork lookup avoids an expensive full-detail composition when a cover claim exists. No ten-minute cache was added because invalidation ownership is not yet established.


### Product owner visual corrections October 4 2026

The latest product-owner reference supersedes earlier hover and smaller-button guidance. All Continue Watching movies and episodes keep their still and exact resting dimensions; hover/focus reveals a compact in-place identity overlay without JavaScript expansion. Books, comics, music, audiobooks, and other non-Watch media use only a subtle outline and register no preview mouse events. Only Home enables changing previews through the explicit IsHomeSurface parameter. Its movie/show discovery cards widen within the row toward 16:9, shifting neighbors horizontally while retaining the artwork height and showing only the full landscape artwork and logo with a slight highlight. Never mount a floating preview or use a viewport-based 900–1280px expansion. Watch pages, browse grids, detail episode lists, and every other surface only highlight cover art and episode stills. Home Continue keeps its landscape artwork and exact dimensions, revealing title/episode identity plus a larger completion percentage. Continue cover art for books and audio only highlights. Episode overlays show season/episode number and the canonical episode name, with the show title as a persistent caption underneath. Continue projection prefers asset/work episode_title over a generic show title. Media/group cards still have one detail link; group artwork composition remains fixed.

All hero Play/Read/Listen/Continue actions use the same larger 22rem by 4.6rem button, bounded to the available width on phones. A continued action includes the applicable percent watched/read/listened; a plain action centers larger text and icon. Song menus use intrinsic content width, the same shared item-height/typography/padding tokens as detail menus, and 56px More triggers in full Now Playing. Obsolete overflow-menu geometry overrides are removed. The audio seek rail and end time sit inside the dock, centered together above the controls; desktop reserves 104px and phone 88px plus safe area. Playback ownership and guarded personal-action identities remain unchanged.

Home discovery hover is artwork-and-logo only. Preserve the complete landscape image without synopsis or fact overlays. Freeze artwork and shelf height through opening and closing so only neighbors in the same row shift horizontally. Home Continue scrollers reserve 8px on each side for the first and last card highlight; packed group widths include these gutters.

Continue groups show at least two whole cards when two are available on desktop and tablet. Desktop groups wrap rather than shrinking below two; narrower non-mobile layouts give each group a full row. Mobile may show one card. Preserve natural artwork dimensions and the 8px highlight gutters when allocating group widths.

Movie/TV compact card captions omit the generic subtitle (including series position such as Movie 1 in a series). Retain the title and canonical year; other media retain author/artist subtitles and Continue retains truthful progress context.

Continue Watching, Reading, and Listening share one artwork height (80% of the Home shelf artwork height). Landscape Watch cards retain 16:9 and gain horizontal width. On narrow non-mobile full-width rows, cap the shared height to fit two landscape cards plus rails, gap, and highlight gutters; all media scale together so height parity and the two-card minimum both hold.

The app primary purple is #8852FC, with #A46FFF hover and matching RGB soft/glow tokens. Player surfaces use #0F131E background, #121623 surface, #30364A tracks, #F7F7FA text and #9EA4BC secondary text. Dock timeline shows elapsed time on the left and toggleable total/remaining time on the right. Music dock exposes the existing song rating control beside Favorite/More; Likes remain distinct from Favorites. Lyrics uses a quotation speech-bubble glyph. An open rating control and its hero action row rise above sibling controls.

Sign-in rejects stale, missing, mismatched, or unreadable antiforgery tokens with HTTP 400 and a freshly tokenized sign-in form. The expired-form notice requests a new submission; rejected credentials never reach the Engine. A local return destination is preserved, while credential fields are cleared. Other security actions retain their existing validation.

Dock rating icons use the same bare 22px glyph sizing and 44px target as Favorite. Song overflow omits Like/Dislike when the direct Rate control is rendered; surfaces without a direct Rate control retain those menu actions. Detail rating circles keep their existing appearance.

Rate hover, focus, and selected-choice highlights are circular and contained within their 44px targets and padded choice pill. Dock Rate remains borderless at rest; its open/hover highlight is circular, matching the choice buttons.

### Player panels and shared controls (October 5 2026)

The phone full player and the popout share `PlaybackFullPlayer` as one screen with no bottom sheets. The default view is artwork, followed by progress, transport, mode buttons, and volume.

- **Modes.** Lyrics and Queue (music), or Chapters, Bookmarks, and History (audiobooks), replace the artwork with a `PlaybackPanelCard`. Selecting the active mode returns to artwork.
- **Controls.** Lyrics and Queue triggers are icon-only, with tooltips and accessible names. Volume is shown on every surface, including phones. Audiobook Speed and Sleep live in More.
- **Dock cards.** The Lyrics card and the combined Queue & History card preview on hover and stay open when pinned. Only one dock card is open at a time.
- **Queue.** Upcoming occurrences reorder by drag, keyboard, or row menu through the guarded `reorder-upcoming` command, using the expected queue revision and saved occurrence IDs. The current item never moves.
- **History.** `clear-history` clears only the player's recent music list, after confirmation.
- **Output.** The main audio owner applies output selection through `setSinkId`. The control appears only where named output devices are already permitted, and it never requests microphone access.
- **Quality and Media Session.** A Lossless or Hi-Res Lossless badge requires known lossless direct delivery. Media Session metadata and actions bind to the main owner.
- **Identity links.** Player identity links use `detailOrigin.fresh`, so the destination opens at the top, including the detail shell's `.context-sidebar-shell__main` scroller. Ordinary Back restoration is preserved.
- **Shared controls.** Dashboard controls use the shared `AppSelect`/`AppTypedSelect`, `AppTooltip`, `AppRangeSlider`, `AppProgressBar`, and `AppSpinner`. Raw MudSelect, MudTooltip, MudSlider, and MudProgressLinear belong only inside those primitives.
- **Intrinsic selectors.** Selectors such as the series selector size to the selected label within the available width, then ellipsize with a full-label tooltip.
- **Follow-up.** Styling and interop cleanup (bundle size, `!important` reduction, per-render interop) is a separate delivery.
- **Docs toolchain.** The documentation toolchain requires Material for MkDocs 9.7.7 or later.
