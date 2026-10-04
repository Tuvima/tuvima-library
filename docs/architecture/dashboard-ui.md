---
title: "Dashboard UI Architecture"
summary: "Current Dashboard structure, shell responsibilities, media lanes, inline editing, Review Queue, and Settings/Admin scope."
audience: "developer"
category: "architecture"
product_area: "dashboard"
tags:
  - "dashboard"
  - "ui"
  - "editing"
---

# Dashboard UI Architecture

The Dashboard is organized around discovery and media use, not a separate media management workspace. Home, Read, Watch, Listen, and Search are where users find and experience media. Detail pages and media rows/cards launch inline editing. Review Queue is the exception workflow for blocked or uncertain items. Settings/Admin is for configuration and system operations.

## Rendering and live-update performance

The Dashboard uses interactive server rendering without static prerender, so page
initializers and Engine requests run once per navigation. Engine HTTP clients are
factory-managed and share pooled primary handlers.

SignalR state changes are coalesced into short render windows, while terminal
events flush immediately. Browse surfaces reload only when their query-defining
parameters change, and per-item ingestion events are aggregated into one debounced
refresh. Components must cache derived LINQ projections in `OnParametersSet`
rather than rebuilding lists from expression-bodied properties during each render.
Wrapping grids stay explicitly paged and use `content-visibility` containment;
do not apply single-column `Virtualize` to responsive variable-width flex layouts.

## Global shell

`src/MediaEngine.Web/Shared/MainLayout.razor` is the app-wide shell. It coordinates:

- MudBlazor providers.
- Brand/logo placement.
- Primary navigation.
- A global Search icon in the top-right action group that opens the instant cross-library search overlay without navigating away.
- A profile-aware **My List** bookmark route backed by the saved/favorites collection.
- A unified circular activity indicator for playback, ingestion, AI work, enrichment, and other durable operations; it becomes an idle check icon when no work is active.
- The account menu, including profile switching, Settings, Help, conditional sign-out, and permission-gated Needs Review count.
- Engine connection/degraded-state messaging.
- Universal search overlay host, including Ctrl+K, focus trapping, and focus restoration.
- Persistent Listen now-playing bar.
- Device context initialization.
- Keyboard shortcut registration and unregister on disposal.

Persistent playback stays in the shell so it survives navigation. Media editing does not live in the shell.

The current navbar centers Read, Watch, Listen, and Collections independently of the logo and right-side actions. Search and My List are icon actions in the top-right group, active work is represented by one consistent progress surface, and review attention appears only as **Needs Review** inside the account menu. Consumer profiles do not fetch or render the review count. Sign out is shown only when OIDC or hybrid authentication is enabled; a local-only profile is switched rather than signed out.

The navbar brand uses the shared `assets/images/library.svg` lockup. The Dashboard project links that source file into its static web assets so the repository has one authoritative copy.

The activity surface is composed by `ShellActivityState`. It merges live SignalR ingestion, AI model-download, universe-enrichment, and durable `MediaOperationChanged` updates with scoped audio/video playback state. `GET /system/activity-status` supplies a sanitized active-operation snapshot so a newly opened Dashboard does not need to wait for the next SignalR event. Operation titles and filesystem paths are intentionally excluded from that shell endpoint.

## Listen Playback

Listen playback is coordinated through `PlaybackSessionController` in `Services/Playback`. Snapshot-driven controls use `IPlaybackCommandSink` to send captured identity and typed intent to the same main owner through direct or broadcast adapters. The persistent Web audio host alone executes transport work against its hidden `<audio>` element.

`ListenTransportControls.razor` supplies shared play/pause, skip, previous/next and chapter controls. The flush audio dock keeps transport centered and the seek line on its top edge. Desktop/tablet Close remains visible outside utility overflow; it saves guarded paused resume before stopping. Phone Collapse changes presentation while playback continues. Audio tools use anchored popovers or bounded modal sheets without resizing the page; Ingestion retains its separate layout-sidebar lease. See [playback architecture](playback.md) and [the current verification report](../reports/player-update-2026-10-03.md) for contracts and actual acceptance status.

Desktop Now Playing uses an aligned artwork/identity stack and one snapshot-driven inline context panel. At the player's 720px phone boundary, `PlaybackFullPlayer` supplies the vertical presentation and phone-only Collapse; global navigation keeps its existing boundary. The popout reuses that component without Collapse or any in-player exit, fills its default 420 by 780 window, and retains main-window media ownership. Canonical identity links are authorized and navigated by the main owner through normal SPA navigation, preserving audio. Popup native cleanup is registration-scoped. Short screens may scroll after artwork and spacing shrink; only the real local playback rail is seekable, while whole-book progress is text.

Browser-only behavior belongs in `wwwroot/app.js` behind the `listenPlayback` bridge and is configured by `config/ui/playback-client.json`. User-facing listening settings remain in the playback settings API.

The bottom player remains the persistent playback surface; Listen does not use a side player. Music exposes shuffle, repeat, queue, history, lyrics, volume, and a music-specific popout. Audiobooks expose speed, chapters, qualified listening history, bookmarks, sleep timer, volume, and an audiobook-specific popout. The default Songs browse mode composes `ListenSongTable`, preserving artwork, direct row playback, play and queue controls, alternating row shading, drag targets for the queue and manual playlists, and resizable columns. A song never opens a standalone detail page; the row overflow menu can open its parent album. `music_play_stats` stores play totals per profile; a play qualifies after 30 seconds of genuine forward playback, or after 50% for tracks shorter than 30 seconds. Forward seeks are excluded.

## Primary surfaces

- `/` is Home/discovery, rendered by `LibraryBrowsePage` with a shorter rotating cinematic hero, Continue Across Media, filterable Recently Added, and the existing populated Watch/Read/Listen/Collections rows. `/recently-added?type=` opens the same recent scope in a paged public browser.
- `/read` is the compact, rail-navigated reading Discover page. One persistent Read header and Discover/Books/Comics navigation remains mounted on `/read/books` and `/read/comics`, with the complete filterable tiled library rendered immediately beneath it.
- `/watch` is the compact, rail-navigated movie and TV Discover page. One persistent Watch header and Discover/Movies/TV Shows/Series navigation remains mounted on `/watch/movies`, `/watch/tv`, and `/watch/series`, with the complete filterable tiled library rendered immediately beneath it. TV Timeline groups shows by their canonical premiere year, never by the newest owned episode; Network grouping uses the resolved network logo in the same identity position as person-led group cards.
- `/listen` keeps the permanent Listen rail and is the compact, row-based Discover surface. One persistent Listen header and Discover/Music/Audiobooks/Playlists navigation remains mounted on its browse routes. `/listen/music` defaults to the direct-play Songs list while retaining Albums and Artists browse modes; `/listen/audiobooks` opens the filterable audiobook grid, and `/listen/playlists` owns playlist browsing without duplicating it under Music. Individual tracks do not appear as cards on Home or Listen discovery shelves.
- `/collections` opens directly as the filterable broader-rollup, personal-collection, and playlist surface; Home is the only rotating landing hero.

The landing carousel reuses `DetailHeroContent` and `HeroBackdrop` through a Home landing variant. Home uses an approximately 80svh desktop feature (72svh on short screens), with bounded cover-led composition for unknown, portrait or undersized sources; measured landscapes use the aspect-band framing policy. Phones and short landscape screens use content-sized composition. Detail heroes retain `95svh`. Landscape art is center-cropped under the translucent app bar and fades into the first shelf. Previous and Next sit at opposite usable edges with 48px targets; the centered active position dash is wider, thicker, and purple. Focus, hover, pause, and reduced motion suspend rotation. Home has no lane submenu. The slide background opens details, while hero actions start or resume media. Untouched TV features use root show art. A started Home feature retains the show title plus `Sx Ey · Episode Title`, uses the selected owned episode still and synopsis, and starts/resumes that episode; missing still falls back to show art. Watch discovery remains show-art-oriented. Engine `DisplayPageDto.Spotlights` provides structurally deduplicated candidates with explicit subject, continuation state, and episode identity.
- `/listen` is the music, audiobook, album, artist, song, and playlist lane.
- `/search` is cross-library discovery.
- `/for-me` is the active profile's personal hub. Its Overview composes real progress, My List bookmarks, Favorites reactions, profile-owned Collections, profile-owned Playlists, and profile-owned Galleries without merging their persistence. `/for-me?view=my-list` is the saved-for-later grid; `/for-me?view=favorites` is the independent reaction grid. The retired `/my-list` route and hidden Favorites/Watchlist Playlists must not be restored.
- Detail pages show the selected media item and expose inline edit where appropriate.
- Media, people, series, and standard collections share one canonical full-width detail surface. Its desktop cinematic stage fills `95svh`; edge-to-edge landscape artwork is pinned top-center under the translucent global app bar and translucent section navigation at the stage's foot, with a strong bottom fade and left scrim maintaining readability while native 16:9 framing loses as little as possible. MusicAlbum specializes the shared hero without forking identity, actions, or navigation: cover art becomes a blurred atmosphere, a large sharp album/record composition is centered in the right half, the shared bottom-left copy carries Play plus a visually distinct Shuffle and the common utilities, and the shared `SurfaceNavigationBar` exposes only **Overview** and **Details**. Overview uses a proportional main/aside layout: a borderless track list with Show missing and no search, a right credits rail that reuses the compact square clickable people cards from movie and TV credits, and a conditional full-width **More by** shelf containing every other owned album from the exact canonical primary artist. Oversized provider box-set manifests are scoped to the canonical disc represented by the local tagged album; ordinary multi-disc albums retain their complete manifests. Technical and source content renders only under Details. Audiobooks reuse the MusicAlbum Overview/Details navigation and the same borderless track table beneath the hero, use a substantially larger cover where viewport height permits, and keep track progress/direct starts plus bookmarks, speed, history, sleep, and other player tools without adopting the video Resume/Restart pair. Embedded titles remain source-authored unless a user explicitly edits one; the Engine does not infer intros or automatically rename audiobook tracks. Editions stays retired, and lists omit Play All and duplicate My List. Structural pages render the ordered episode/sequence, collection-item, appearance, or **Works in Your Library** array below the stage on Overview only and end with a compact ownership summary; an authoritative known total adds missing/total text and a simple progress bar. Collections use no member-derived backdrop, show the same up-to-four `MediaArtworkGroupPreview` cluster as collection tiles at a dominant hero scale, suppress contributor credits, expose one functional Shuffle action but no Open or lane buttons, and render year range, total item count, and applicable Read/Watch/Listen counts as one compact facts row. Comics and books share a larger cover-only foreground envelope; movie/TV details without a real landscape backdrop use the same available hero space, while true backdrops remain edge-to-edge. People use a larger portrait on a single continuous page without a tab shelf, and linked identities reuse cast/credit portrait cards without a redundant relationship subtitle. Living people show current age with Born; deceased people show age at death with Died. Owned music credits collapse tracks into one album entry with a track count. Larger icon-left utility controls grow from the left below the primary action. The array control bar clusters applicable set/season/role selectors on the left and Show missing plus mixed-lane filters on the right; Jump to moves beside the array heading only above ten items. Overview then combines attributed description or biography, a purpose-built cast/credits peek, and applicable structural context, while recommendations render in a separate Related tab. Standard collections resolve their detail membership through the same catalog rules used by the Collections surface. Read/Watch/Listen filters appear only for mixed-media collections and people with owned works across multiple lanes. Person works use managed portrait cover/poster or square album art rather than cinematic landscape backgrounds, and prefer owned-asset canonical titles over parent collection labels. Playlists remain specialized Listen queue/edit surfaces.
- Primary modules are explicit detail-contract data rather than route-driven UI guesses. Person works come only from canonical eligible credits for owned media, deduplicate the work while retaining every eligible role, and can be filtered by Read, Watch, Listen, or role.
- Overview begins beneath the stage and combines the full attributed description or biography, a role-aware cast/credits peek, series or collection context when applicable, and related content. Details contains technical identity and source information and never repeats the primary media array.
- `/settings/review` is the Review Queue.
- `/settings` and `/settings/{Section}` are Settings/Admin.

`LibraryBrowsePage` is the only landing page that composes `CinematicHeroCarousel`. `CinematicHeroSurface` remains the outer renderer for both the Home carousel and `DetailHero`, and both render identity/content through `DetailHeroContent`. `LibrarySectionHeader` owns the compact persistent `SurfaceNavigationBar` at the top of Read, Watch, Listen, and Collections; it does not repeat a section title or description already expressed by global navigation. Their rails likewise begin directly with logical navigation groups instead of a redundant section-name block. Desktop section shells occupy the remaining viewport below global navigation, keep the rail anchored, and scroll only the adjacent content pane. `MediaHubPage` supplies the compact row-based Discover content beneath a lane menu, while `MediaBrowseShell` supplies complete URL-backed filters on direct lane scope routes. Collections composes the same shell and control system across Overview, Automatic, Curated, Shelves, and People routes. Its People route uses server-paged canonical primary contributors on owned works rather than the broader enrichment relationship graph. Scoped search occupies its own row. `AppBrowseModeSelector`, shared searchable multi-selects, `AppQuickFilterToggle`, `AppActiveFilterSummary`, and the right-aligned Display controls provide the same interaction hierarchy across lanes. `LibraryBrowsePreset` is the source for both toolbar modes and persistent-rail Browse as links. Query state is readable and URL-backed, and qualifying media are filtered before grouped author, series, collection, network, or timeline results are composed. Listen album and audiobook filters use the same visual contract with their lane-specific facets. Those scope tabs resolve real routes rather than filtering a subset of loaded shelves in memory. Collections is browse-first and does not render a hero.

The user-facing Collections root is **Discovery** (the internal section key remains `overview`). The Shelves root renders Read, Watch, and Listen preview rows before filtering, and its media selector uses Books, Comics, Movies, Albums, and Audiobooks rather than contributor-role shelf types. Same-name duplicate contributor records resolve to one shelf using the richer managed identity. Shared `SurfaceNavigationBar` chrome does not draw extra top or bottom dividers. Read, Watch, and Listen Discover rows show compact title/year captions for individual items only; group cards retain embedded identity. The TV Shows row suppresses the redundant type pill but retains episode count. Completed show cards display a provider-backed premiere-to-finale year range, and TV detail sequence surfaces retain the Season selector even when only one season is present.


## Home recent feed and shared continuation state

Home orders Continue Across Media, Recently Added, then the existing populated Watch, Read, Listen, and Collections & Lists rows. Recently Added replaces Fresh and defaults to All. Its unboxed All/Watch/Read/Listen/View selector retains the selected scope in `/recently-added?type=` and uses `GET /api/v1/display/recent` for every bounded page. The Engine orders permitted catalogue and Mine View entries by added time descending, then source-qualified identity ascending. The opaque cursor binds type/profile and the last emitted tuple, preserving ties and source-heavy pages without omissions or repeats. Catalogue recent identities are owned top-level work/show/album entries; capture date does not determine View recency.

View assets remain a separate typed branch containing stable metadata. `IViewQueryOrchestrator` resolves current Mine authority independently; Shared Library and other profiles are excluded. The Web adapter mints bounded thumbnail/preview grants for the current profile when rendering and opens the existing View viewer. No grant URL enters Engine DTOs or caches, and no View identity enters catalogue claims, playback progress, or search attribution.

Continue presentation collapses TV episodes by root show and audiobook files by book work, keeping the latest accessed asset and its truthful resume target. Raw per-asset journey/state projections remain intact. Home divides this result into populated Watching, Reading and Listening groups, stacking below 1280px and sharing a bounded artwork height. `MediaShelfScroller` owns horizontal snapping, hidden scrollbars, guarded wheel behavior and boundary-driven arrows for ordinary shelves, Continue groups and Recently Added. Resize/scroll observers update arrow boundaries; parameter refreshes do not reset them. Recent tiles are composed once per fetched page and media-added refreshes invalidate Home's feed.

View participates in the shared lane flex/scroll chain. Places uses the definite content height, while its story panel retains an independent scroll owner; photo, gallery, folder, people and artwork surfaces retain their vertical content scroller and timeline target.

Profile-scoped journey and all-state reads execute synchronous SQLite commands without state caches so committed progress updates Home, Continue, and detail together. Continue includes genuinely partial long-form items plus active albums, while all-state episode selection uses `TvEpisodeContextResolver` over distinct owned works. Shared explicit subject/state/episode context drives exact routes and artwork. Resting movies/TV/books/comics use portrait frames, albums and audiobooks use square frames, and only Continue Watching uses landscape stills. Artwork is contained when its native ratio differs from its frame. Home shelves share one artwork height; wrapping browse grids remain GlowOnly. Cards retain one semantic detail link and partial long-form artwork-edge progress, without detached percentage captions. Only partial long-form items show a purple artwork-edge strip with accessible progress labels; completed state is text/check, and albums/tracks/View have no completion semantics.

Phones use content-sized hero layout, safe-area and bottom-dock tokens, 48px controls, horizontal shelf swiping, readable identity/state without hover, and bounded responsive images. Required evidence covers 1920×1080, 1536×1024, 1024×768, 390×844 plus 320×568, 844×390, 768×1024, long titles, and 200% text. Geometry emulation and real-device/touch verification must be reported accurately.

## Universal search

The app shell owns `UniversalSearchOverlay`. Search and Ctrl+K open it over the existing route, with the page dimmed underneath. The overlay focuses its search field, traps focus, closes on Escape, and returns focus to the shell Search button. Each debounced query cancels the previous request, so an older response cannot replace newer text. Empty search shows recent local queries; Enter and **View all results** navigate to `/search?q=...`.

`GET /api/v1/display/search` is the normalized, lightweight query boundary. `UniversalSearchReadService` combines the existing SQL-backed owned-work search with focused person and collection/playlist queries, ranks exact and prefix matches first, and returns `UniversalSearchResultDto` rather than full detail models. The contract carries stable identity, entity/media type, title, creator/subtitle, artwork, canonical year, description summary, action label, detail route, match reason, relevance, and compact preview facts. The UI renders the same `UniversalSearchResults` component in the overlay and dedicated page.

The dedicated `/search` route keeps `q`, repeated `media`, `type`, `yearFrom`, `yearTo`, and `sort` in the URL. All-media results stay separated into relevance-ordered sections with a top result and right preview; selecting one media type produces a single scoped section. Section **See all** links transfer the query into the corresponding Read, Watch, or Listen route. Search is navigation-first and does not add a parallel editing workspace.

Desktop individual-item `MediaTile` previews expand the tile's flex item inside its existing shelf row. The resting cover is replaced at the same vertical position, neighboring cards shift horizontally, and leaving the preview restores the row. Media previews must not be portaled into a fixed overlay host or drawn over cards in another shelf.

Non-TV series and collection containers do not use that expansion behavior. `MediaGroupTile` is the single wide, fixed-size landscape renderer for both legacy and display-API group sources. Two, three, or four representative owned images form a slightly angled, overlapping cluster based on their real portrait, square, or wide metadata; images keep their natural ratios and only four are loaded and rendered. Direct browse cards place an all-caps title plus truthful year and media-count metadata in the restrained lower identity area. Hover or keyboard focus does not replace, reorder, or move the artwork and does not change card dimensions or shelf geometry; it adds the purple boundary/glow and a compact open cue. A catalog collection resolved from one exact person rule (or an explicit person collection type) adds that person's managed circular headshot and concise roles in the lower identity area, preserves the artwork cluster, and makes the whole surface open `/details/person/{id}`. The Engine supplies the same person identity for author and creator groups in Read, director groups in Watch, and artist, audiobook author, and narrator groups in Listen; those browse views all use this same card and route. `primary_person_media_credits` is the shared presentation projection for these groups, search attribution, person library credits/presence, person-scoped collection rules, and artist artwork ownership. It follows the detail page's precedence—ordered canonical arrays, then canonical claims, then scalar canonical values—and deliberately excludes extra relationships that exist only in `person_media_links`. There are no child-level buttons, artwork rotation, or selected-child state. TV shows are an explicit exception: they render through `MediaTile` with the show cover at rest, then expand to the show-level cinematic backdrop, logo, facts, and description on hover. Owned episode navigation remains on the show detail surface. Ordinary media and Continue cards remain on their existing renderers.

The Watch landing keeps those concepts spatially separate. `TV Shows` is a dedicated shelf of show identities built from owned episodes. `Series` contains only movie series dynamically aligned by the Engine from trusted library metadata; the shelf subtitle communicates that automatic grouping, and its View all route opens the Movies tab in Series mode. TV shows are not repeated in Series.

`MediaArtworkGroupPreview` is the shared non-interactive representative-art primitive. `MediaGroupTile` uses its clustered mode so all series and collection containers share the same angled composition. The strip and adaptive variants remain available for non-card artwork contexts. The previous `MediaArtworkCarousel` and JavaScript-rotating collage were removed and must not be reintroduced on card or detail surfaces; deliberate item browsing belongs in the existing detail lists and selectors.

TV show shelves use show-level cover art at rest; episode stills are reserved for the episode browser, episode details, and episode-specific Continue cards and started Home spotlights. An unstarted root TV detail may retain the enriched show backdrop or show cover while its facts and action target the first owned episode. Once episode progress exists, the root hero uses that episode's managed still and synopsis without removing artwork from the season list below it. Full-density detail backdrops fill the viewport space between the app bar and shared lower navigation so the complete synopsis and navigation remain visible together; compact Listen density remains shorter. The foreground image box hugs the bitmap so its desktop fade begins at the bitmap's true left edge instead of at a viewport-relative position; mobile uses the same complete frame with a vertical fade into the content. TV episodes are grouped behind a styled season selector and rendered as a responsive still grid with each available synopsis. Episode cards remain one link to the show-scoped episode detail, with episode identity and state available without hover. Shared artwork-edge progress/status communicates untouched, partial, or watched state; hover and keyboard focus reveal the actual episode synopsis and known timing. Season summaries count distinct owned episodes only.

Detail hero actions retain contextual verbs—Read for books/comics, Play for movies, `Watch Sx Ey` for individual TV episodes, and Listen for music/audiobooks—but cards do not duplicate those actions. Every media or group card is one semantic link to its detail surface, with no inline playback, reading, My List, reaction, remove, or details buttons. Vertically wrapping `MediaTileGrid` results preserve their exact resting cover or group composition on hover and keyboard focus, display one compact title line plus an optional year below the artwork, and use only a thicker purple selection glow, with no hover identity strip, scrim, background replacement, or popover. Direct browse cards omit redundant media/group pills such as `TV Show` inside the TV Shows route because the selected navigation already supplies that context. A tile-size slider in the direct browse filter area changes the card width while preserving each artwork shape; Music uses a smaller default than portrait video, reading, and audiobook media. Movie and TV cinematic landscape expansion is reserved for Home and lane Discover shelves when suitable background artwork exists; its left-aligned star rating, classification, year, and runtime row uses a compact translucent backing that hugs the text, and it remains action-free. Read, Watch, and Listen render their hero through the same component so the left edge, facts, synopsis, primary button, and utility controls use one layout contract; the identity slot alone may contain a downloaded logo or a written title. Compact facts use the same Read-scale typography, with at most two linked genres on a dedicated non-wrapping line below them. Read heroes show the first description paragraph and link to the full Overview when more text exists. A root show targets its in-progress episode or earliest owned episode, reports the owned episode count, and defaults to `Watch S1 E1` when that is the first owned item. Before playback, the series hero may remain while rating and runtime come from that target episode; TMDB episode runtime is preferred and owned-file duration is the fallback. After progress exists, the still and separated `Sx Ey: Episode title` synopsis follow that episode. The short provider/TMDB show description appears under the owned summary in a separated Series Description block. Movie heroes use the movie description without a heading. TV episode sequence items may use managed stills; movie, book, and comic sequences must use cover art rather than hero banners. Watch heroes retain a flat classification box and place a separator above the synopsis. Add to Collection remains a detail-page organization action and uses a distinct collection icon. Book detail artwork defines the height of its own page-edge and spine effects, so those decorations remain aligned when the cover scales. My List is the universal profile shortlist backed by the existing saved/favorites collection; Love remains a separate preference reaction. In-progress watch titles expose episode-aware Resume and Restart together. The watch utility row contains library, rating, collection, and editing tools without a redundant Show details action. Lane-specific tools such as Shuffle or Watch Party follow the universal actions. Detail backdrops render the managed landscape asset once as a full-bleed, edge-to-edge `cover` image anchored at top center inside the `95svh` cinematic stage; Home uses the same primitive with its shorter landing variant and center crop. The global app bar and lower surface navigation remain translucent so the image can extend through both bands, while a restrained top fade, stronger bottom fade, and left reading scrim protect controls and copy without introducing a second blurred or member-derived image layer.

## Sequence, Attribution, And Artwork Display

Lane pages, shelf cards, album pages, collection pages, detail pages, search
results, and review cards should use the same sequence and artwork contract from
the Engine:

- Sequence cards show one immediate shelf, ordered by `ordinal_sort`; decimal
  positions, comic annuals, TV specials, and multi-disc tracks should not
  collapse into the same child row.
- `Owned X of Y` uses `sequence_total` only for an authoritative finite/known
  immediate container, never a broader franchise, partial manifest, or loaded
  row count. When that evidence is absent, the UI shows `X owned` without a
  missing count or completion donut.
- Comics show their issue identifier (`Batman · Issue 405`) and owned issue
  count without an `of N` completion target. Provider run totals remain internal
  matching and diagnostic facts because an ongoing comic run may keep growing.
- TV has show detail pages with show-scoped detail pages for each owned episode.
  Both surfaces share the same season selector and owned-only episode projection;
  provider-catalog rows and totals do not appear as library ownership. Seasons
  contain managed stills and available descriptions; Continue surfaces
  retain the episode still and playback target, use compact copy such as
  `Continue · S5 E1`, and keep `Resume S5 E1` separate from Details.
- Scoped series manifests render main-sequence works separately from
  supplementary short fiction and collected content. Exact source ordinals are
  displayed unchanged; an unnumbered supplemental work is labeled by scope and
  is not assigned an invented decimal or dense position.
- A media item with structural series placement exposes `Series` as its first
  detail tab and `Overview` as its second. The hero, Series tab, and hover card
  share concise placement copy such as `Book 1 in The Expanse` or
  `Movie 1 in The Lord of the Rings`; they do not append an `of N` total.
- Canonical book, comic, and movie series containers reuse that sequence rail
  directly on Overview. A source number is rendered above its cover, and a
  connector is drawn behind the number nodes only when the neighboring stored
  positions are genuinely consecutive. A stronger purple frame glow is the
  current item's only visual state; `This book`, `This movie`, and `Up next`
  labels are not rendered. Completion is represented independently with a check,
  while `aria-current` provides non-visual current-item context.
- Missing-item visibility defaults are media-level configuration in
  `config/ui/library-preferences.json`. The database stores only explicit
  per-profile, per-series overrides. Removing an override makes that series
  inherit the current configuration value again.
- Structural shelf names remove a redundant trailing `Series` or `Collection`
  for presentation (`Dune Collection` becomes `Dune`). Curated collection
  names preserve those words because they are part of the collection identity.
- Descriptions and metadata text sourced from Wikipedia, Wikidata, or providers
  should show attribution links on detail pages when attribution is present in
  the API response.
- Artwork should render from managed URLs or settled placeholders. The UI should
  not show broken external provider URLs directly.

## Inline editing model

The canonical edit path is:

```text
media surface -> MediaEditorLauncherService -> SharedMediaEditorShell
```

Normal mode is launched from detail pages, browse rows/cards, book details, listen tables, albums, tracks, movies, shows, and search/detail contexts. Review mode is launched from Review Queue. Batch mode is used only where a real selection exists.

After an edit is applied, the current surface refreshes and the user remains in the same context.

Editor tab transitions are owned by `MediaEditorTabState`, including the return
from file inspection to the last content tab. Focused overlays such as artwork
preview belong in child components instead of adding more state and markup to
`SharedMediaEditorShell`.

Listen profile navigation JSON is decoded, reordered, and encoded through
`ListenPlaylistOrderState`. `ListenPage` should coordinate user intent and API
work rather than own the persistence format.

Listen discovery and direct browse follow the same lane-shell contract as Read
and Watch. `MediaLanePage` owns the common page-state, lane header, discovery,
and direct-browse composition used by the three thin route components.
`ListenBrowsePage` owns `/listen`, `/listen/music`, `/listen/audiobooks`, and
`/listen/playlists`; `ListenBrowseConfiguration` is the single typed source for
Music/Audiobook/Playlist browse modes and rail shortcuts. Music browse state is expressed
with query parameters, for example `/listen/music?browse=artists` and
`/listen/music?browse=songs`, while album, artist, playlist, and audiobook
details retain dedicated routes. The landing remains album-first and separates
square music shelves from portrait audiobook shelves. Personal playlist artwork
must come from managed Engine URLs. Playback remains in the globally mounted
bottom host; Listen pages do not reserve a permanent right-side player column.

Dashboard Engine calls continue to be exposed through `IEngineApiClient`.
Feature-focused typed clients or `EngineApiClient.*.cs` partials own cohesive
endpoint families so the facade remains source-compatible while its
implementation is decomposed. AI endpoint methods are a separate feature area
and must not be moved as a side effect of non-AI client work. Shared HTTP
helpers are used only where the existing method already follows the same
failure-state envelope. Legacy `LastError`-only calls, raw-response GETs,
diagnostic responses, and calls with no failure bookkeeping remain explicit
until their observable error classification is intentionally redesigned.

Routed book, movie, TV show/episode, and generic detail entry points translate
their parameters into `DetailRouteRequest` and delegate loading, page state,
tab selection, actions, and origin navigation to `DetailRouteHost`. The route
wrappers do not duplicate detail orchestration.

Page-sized loading, empty, unavailable, and retryable failures use
`AppPageState`/`AppErrorState`. Compact progress indicators remain appropriate
inside buttons, tables, refresh controls, playback transports, dialogs, and
other already-mounted content where replacing the whole page would be
misleading. Provider display names, accents, and fallback icons are owned by
`ProviderCatalogueService`; tile artwork selection delegates to
`MediaTileArtworkResolver` or `MediaTileComposerService`.

The live ingestion dashboard separates three responsibilities. The primary
`IngestionLiveDashboardState` partial owns polling, subscriptions, cancellation,
and awaited shutdown; its `Projection` partial owns snapshot-to-view-model
calculation; and `IngestionDashboardSelectionState` preserves the selected batch
and stage when a live snapshot replaces the current rows. The Razor component
keeps markup separate from its code-behind. Its scoped styles live with
`IngestionTasksTab` because the parent owns the dashboard surface and reaches the
child through `::deep`; retired selectors should be deleted when markup is
removed.

Provider-priority presentation follows the same boundary: the tab coordinates
provider data in code-behind while `ProviderStageSelector` owns the accessible
stage choice control. The selector must continue to expose its active state with
`aria-pressed` rather than relying on color alone.

Retired Dashboard components are removed as complete closures: Razor, scoped
CSS, DI registrations, source-only test fixtures, and stale documentation. Do
not leave empty client or component marker types for hypothetical extraction;
introduce a focused type only when it has active behavior and consumers.

## Review Queue

Review Queue is for items that are blocked, uncertain, or need confirmation before ingestion/enrichment can continue. It should explain why each item needs attention and open the shared editor in review mode. Review may support dismiss, retry, skip-universe, approve, or apply-match actions according to Engine rules.

Review Queue is not a broad media management workspace and must not become one.

## Settings/Admin scope

Settings/Admin contains configuration and operational state:

- Library folders and organization templates.
- Provider configuration.
- Profiles, roles, and access.
- Device/profile UI configuration.
- Ingestion/task status.
- Engine/system health.
- Logs, diagnostics, storage, and runtime controls.
- Review Queue if navigation places review under Settings.

Settings/Admin should not host normal media browse/edit pages.

The Settings shell uses a flat, role-aware rail grouped as **Personal**,
**Administration**, and **Advanced**. Canonical page routes use
`/settings/profile`, `/settings/system`, `/settings/libraries`, `/settings/ingestion`, and the
corresponding page slug. Libraries owns the configured Read, Watch, Listen, and
View storage surfaces. Page-local tabs and segmented controls
provide subsection navigation; the rail does not expand into a second settings
tree. On narrow screens, a page selector replaces the rail. Privacy & Data
remains hidden until its operations are Engine-backed, and Developer Tools
requires both the Administrator role and the internal-tools feature flag.

## Guardrails

- Do not add all-in-one management routes, tabs, CSS prefixes, or docs that describe removed workflows as current product behavior.
- Do not restore removed all-in-one management implementation types.
- Do not route normal media fixes through a management workbench.
- Use `MediaEditorLauncherService` and `SharedMediaEditorShell` for normal, review, and batch edit flows.
- Refresh the current surface only after the shared editor returns a successful result; canceled edits should not mutate UI state.

## Collections and person consistency

- Collections uses one viewport-bounded, padded content scroller beside the anchored rail. The page itself does not create a second vertical scrollbar.
- Automatic collection identity prioritizes trusted fictional-universe, franchise, and based-on/adaptation QIDs over lane-local parent or series structure. This permits comic and film shelves such as Batman to meet in one cross-media rollup.
- People presence counts use distinct top-level works: music tracks collapse to their album and TV episodes collapse to their show.
- Person names use the same canonical title family, size, weight, responsive density, and lower-left identity anchor as media details. The shared hero's explicit person copy hook applies that contract to the rendered title, while birth/death dates, locations, and owned-title facts reuse the same metadata row/item treatment as year, runtime, and genre on media details. The portrait may remain vertically centered, but it must not center the adjacent identity column. Musical groups expose all canonical `has parts` members and hydrate incomplete member identities; collective pseudonyms continue to use alias relationships.
- Person-owned music credits show the album and the person's role without exposing track counts.


