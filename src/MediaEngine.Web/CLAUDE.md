# Dashboard (MediaEngine.Web) — Working Rules

> Loaded automatically when Claude works on files under `src/MediaEngine.Web/`. Root `CLAUDE.md` still applies. Detailed product behaviour per surface (cards, Home, details, TV, Collections, player panels) lives in [`docs/product/presentation-rules.md`](../../docs/product/presentation-rules.md) — read the relevant section before changing a surface.

## Dashboard quality gates

- Listen playback stays under `PlaybackSessionController` and the persistent native audio host. Snapshot controls use direct/broadcast command sinks against the same main owner. Shared bare 22px outline utilities keep 44px targets. The flush dock centers transport, places seek inside the dock above the controls and exposes desktop/tablet Close outside utility overflow. Close saves guarded paused resume before stopping; phone Collapse preserves playback and phone players have no session-stop Close. PlaybackFullPlayer supplies shared phone/popout UI from the captured snapshot; only the phone supplies Collapse. The popout defaults to 420 by 780, fills its window, has no in-player exit, and sends canonical identity navigation to the authorized main owner without reloading audio. Audio popovers/sheets do not resize the page; Ingestion retains its layout-sidebar lease and resize behavior. Speed uses the shared slider popover; Sleep uses the central flat select with one scroller. The main owner holds selected sleep minutes, the absolute deadline or verified captured chapter boundary separately from bookmark drafts. One captured Add/Saved bookmark dialog uses its local desktop opener or bounded phone sheet. Follow [playback architecture](docs/architecture/playback.md) and [current verification evidence](docs/reports/player-update-2026-10-03.md), which supersedes the October 2 dock/sidebar layout; do not infer runtime acceptance from source-only checks.
- Avoid silent `catch { }` blocks. Best-effort failures need a justification comment or an explicit guardrail allowlist entry; user-visible failures need logging and degraded/error UI.
- Domain aggregates expose child collections, property bags, and lifecycle state as read-only views. Mutate them through explicit aggregate methods (`Work.LinkToWikidata`, `Collection.SetVisibility`, `Collection.ChangeResolution`, and the named child methods), and keep repository hydration explicit instead of making aggregate internals public again. Persisted aggregate enums convert only through `AggregateStateSerializer`; unknown values fail fast.
- When product concepts, navigation, editing flows, database lifecycle, Docker startup, or CI checks change, update README/docs/AGENTS/CLAUDE and relevant `.agent` guidance in the same change.
- Verification cost control: while iterating, check structure and computed styles with text tools (page text, accessibility tree, computed CSS). Take screenshots only for before/after proof at the required sizes, and keep them in ignored `.tmp/`.

## Feature-sliced layout

All Dashboard code follows the **Feature-Sliced** pattern. Every new piece of UI code must go into the correct slice.

### Services (`src/MediaEngine.Web/Services/`)

Non-UI logic the Dashboard needs, organised by concern.

| Subfolder | Key files | Purpose |
|---|---|---|
| `Branding/` | `StreamingServiceLogoResolver.cs` | Resolves streaming-service logos for display chips |
| `Configuration/` | `DashboardConfigurationReader.cs`, `DashboardPaletteReloadService.cs` | Dashboard-side config read + palette hot-reload |
| `MediaTiles/` | `MediaTileComposerService.cs`, `MediaTileArtworkResolver.cs` | Builds shared browse tile shelves and resolves sized artwork for Home, Read, Watch, Listen, and Collections |
| `Editing/` | `MediaEditorLauncherService.cs`, `CollectionEditorLauncherService.cs`, `*Models.cs` | Editor open/close state and presentation models; wire DTOs remain in Contracts |
| `Integration/` | `EngineApiClient.cs` + `IEngineApiClient.cs`, `UIOrchestratorService.cs`, `UniverseStateContainer.cs`, `UniverseMapper.cs`, `ProviderCatalogueService.cs`, `IntercomEvents.cs` | All HTTP + SignalR communication with the Engine and explicit mapping into Dashboard presentation state |
| `Formatting/` | `DisplayFormat.cs` | Single home for UI duration/count/speed formatting and word-splitting helpers (each divergent output format is a distinct method) |
| `Narration/` | `PhraseTemplateService.cs` + interface | Narrated-copy phrase templates |
| `Navigation/` | `MediaNavigation.cs`, `ListenNavigation.cs` | Route-building helpers |
| `Playback/` | `PlaybackSessionController.cs`, `PlaybackModels.cs`, `MediaKindClassifier.cs`, `PlaybackQueue.cs`, `PlaybackStateMachine.cs`, `ReadingProgressService.cs`, `ReaderSettingsService.cs`, `MediaReactionService.cs`, `WatchlistService.cs` | Playback session state, typed commands, queue/session primitives |
| `Ui/` | `AppDialogService.cs`, `AppToastService.cs`, `AppPopoverService.cs` | Scoped native dialog/result, toast/action and popup coordination |
| `Theming/` | `ThemeService.cs`, `DeviceContextService.cs`, `PaletteProvider.cs`, `SocialUriHelper.cs` | Dark-mode theme, device cascade, palette, Actionable-URI helpers |

### Components (`src/MediaEngine.Web/Components/`)

Reusable visual components, organised by feature slice.

| Subfolder | What lives here |
|---|---|
| `Browse/` | `MediaBrowseShell`, `BrowseQueryBuilder`, `BrowseState`, `BrowseArtworkRules` — focused browse shell and extracted query/state/artwork helpers used by Read / Watch / Listen subroutes |
| `Cinematic/` | `CinematicHeroCarousel`, `CinematicHeroSurface`, `SurfaceNavigationBar` — shared rotating hero shell and lane/detail navigation |
| `Collections/` | `CollectionsPage`, `CollectionsSectionConfiguration`, `CollectionsSectionLayout`, `PeopleCatalogList`, `CollectionEditorShell` |
| `Details/` | Detail-page composition extracted from Pages. `DetailPage`, `DetailHero` (+ `DetailHeroPresentation`), shared `DetailHeroContent`, `DetailTabs`, `DetailPrimaryModule`, `OverviewTab`, `DetailsTab`, `FormatsTab`, `PeopleAndCharactersTab`, `ChildrenListTab`, `SyncTab`, `RelatedTab`, `UniverseTab`, `AudioItemTable`, `AudiobookChapterList`, `MusicAlbumOverviewContent`, `MusicAlbumSeriesRail`, `MusicTrackList`, `CharactersSection`, `ContributorsSection`, `CreditGroupSection`, `CastCharacterPairCard`, `CharacterCreditCard`, `OwnedFormatsPanel`, `OptionalSyncPanel`, `PersonAvatar`, `PersonCreditCard`, `RelatedEntityChip`, `SequencePlacementPanel`, `HeroBackdrop`, `HeroActionRow`, `HeroMetadataPills`, `HeroProgressBlock`, `ManageActionsMenu`, `OverflowActionMenu`, `GeneratedIdentity`, `DescriptionAttribution` |
| `Discovery/` | `AddToCollectionDialog` |
| `MediaHub/` | `MediaHubPage`, `LibrarySectionHeader`, `MediaSectionShell`, `MediaShelf`, `ShelfHeader`, `EmptyShelfState` — shared section scaffolding used by Read, Watch, Listen, and Collections |
| `MediaTiles/` | `MediaTile`, `MediaGroupTile`, `MediaTileGrid`, `MediaTileShelf` |
| `Layout/` | `MainLayout`, `NavMenu`, `ReconnectModal` — the routed app shell |
| `Library/` | Reusable legacy-named library helpers still used by current browse/list surfaces, such as configurable tables, column definitions, batch bars, and status pills. Do not add all-in-one management workflow components here. |
| `Listen/` | `ListenNowPlayingBar`, `ListenSongTable`, `ListenTransportControls`, `ListenNavigationItem`, `ListenNavigationSection` |
| `MediaEditor/` | `SharedMediaEditorShell`, `SharedMediaBatchConfirmDialog` |
| `Navigation/` | `SystemActivityIndicator`, `TopNavAccountMenu` |
| `Pages/` | All routed pages — see §6.4 |
| `LibraryItems/` | Internal building blocks used by Library + Universe: `LibraryItemInspector`, `LibraryItemCard`, `LibraryItemGrid`, `Inspector*Section` panels, `LibraryItemActionsBar`, `LibraryItemBulkBar`, `LibraryItemBatchList`, `LibraryItemFilterBar`, `LibraryItemHelpers`, `ProvisionalFormPanel`, `ActivityItemCard`, `ReportProblemDialog` |
| `Settings/` | Settings shell tabs — see §3.11 for the section list and component inventory. |
| `Shared/` | The first-party native `App*` design-system primitives (`AppPageState`, `AppErrorState`, `AppEmptyState`, `AppSkeleton`, `AppIcon`, `AppIconCatalog`, `AppMediaCard`, `AppTable`, `AppDialog`, form/field/button primitives, …) plus the `Playback*` control primitives (`PlaybackControlStrip`, `PlaybackPrimaryButton`, `PlaybackSpeedControl`, `PlaybackSleepTimerControl`, `PlaybackToolSheet`, …) and `TuvimaArtworkStack` |
| `Universe/` | Hero, swimlane, and card components: `CollectionHero`, `CompactHero`, `HeroCarousel`, `PosterSwimlane`, `SwimlaneSection`, `LandscapeCard`, `SquareCard`, `WideCard`, `WorkCard`, `LibraryCard`, `PersonCard`, `PersonSwimlaneHeader`, `TrackRow`, `MetadataChips`, `ProgressIndicator`, `AmbientBackground`, `GlobalBackground`, `GreetingBar`, `AdaptationTree` + node, `FamilyTreeView`, `AlphabeticalGrid`, `CastComparison`, `BookDetailContent`, `CollectionShell`, `CollectionToolbar`, `LaneFilterBar`, `ManualEntryForm`, `MediaSearchPanel`, `MissingUniverseChip`, `PathFinderPanel`, `PendingFilesAlert`, `UniverseGuide` |
| `Watch/` | `WatchPlaybackSpecs` |

### Other top-level folders under `src/MediaEngine.Web/`

| Folder | Purpose |
|---|---|
| `Models/ViewDTOs/` | Dashboard-only presentation shapes. They may wrap or compose Contracts types through explicit mappers, but they never own or mirror Engine↔Dashboard JSON. |
| `Resources/` | `SharedStrings.resx` (+ `.fr` / `.de` / `.es`) plus generated `SharedStrings.cs` |
| `Shared/` | Blazor app-host layout wrappers: `MainLayout`, `NavMenu`, `PopupLayout`, `ReaderLayout`, `_Imports` |
| `wwwroot/` | Static assets: images, CSS, JS (`cytoscape-interop.js`, `epub-reader.js`, `cover-popup.js`, `app.js`) |

### Routed Pages (`Components/Pages/`)

| Route | Page | Purpose |
|---|---|---|
| `/` | `LibraryBrowsePage.razor` | Home — discovery landing |
| `/read`, `/read/{Tab}` | `ReadPage.razor` | Books + comics browse |
| `/read/{AssetId:guid}` | `EpubReader.razor` | In-browser EPUB reader |
| `/watch`, `/watch/{Tab}` | `WatchPage.razor` | Movies + TV browse |
| `/watch/movie/{WorkId:guid}` | `WatchMoviePage.razor` | Movie detail |
| `/watch/tv/show/{CollectionId:guid}` | `WatchTvShowPage.razor` | TV show detail |
| `/watch/player/{AssetId:guid}` | `WatchPlayerPage.razor` | Video player |
| `/listen`, `/listen/music`, `/listen/audiobooks` | `ListenBrowsePage.razor` + `ListenBrowseConfiguration.cs` | Shared-shell Listen discovery and query-backed Music/Audiobook browse |
| `/details/musicalbum/{Id:guid}`, `/details/musictrack/{Id:guid}`, `/details/audiobook/{Id:guid}`, `/details/person/{Id:guid}` | `UnifiedDetailPage.razor` | Canonical full-width Listen and person details |
| `/listen/music/playlists/{CollectionId:guid}`, `/listen/music/playlists/system/{PlaylistKey}` | `ListenPage.razor` (+ `.razor.cs` code-behind) | Specialized playlist queue and editing surfaces |
| `/listen/player-popup` | `ListenPlayerPopupPage.razor` | Detached listen window |
| `/collections` | `Collections.razor` | Browse / create / manage collections |
| `/details/collection/{Id:guid}` | `UnifiedDetailPage.razor` | Standard collection detail |
| `/book/{Id:guid}` | `BookDetail.razor` | Book detail |
| `/detail/{Type}/{Id}` (and similar) | `UnifiedDetailPage.razor` | Unified detail surface (work / edition / collection / person) — uses `Components/Details/` slice |
| `/universe/{Qid}/explore` | `ChronicleExplorer.razor` | Universe graph explorer |
| `/search` | `SearchPage.razor` | Global search |
| `/settings`, `/settings/{Section}` | `Settings.razor` | Settings shell (review queue at `/settings/review`, ingestion at `/settings/ingestion`, temporary harness at `/settings/dev-harness`) |
| `/not-found`, `/Error` | `NotFound.razor`, `Error.razor` | Error pages |

> **Note.** Earlier drafts referenced `PersonDetail.razor`, `Home.razor`, and `ReviewRedirect.razor`. Those files no longer exist on disk; person detail is served by `UnifiedDetailPage` via `Components/Details/`, and the `/review` redirect has been replaced by direct `/settings/review` navigation.

### Rules for adding new code

| New code type | Where it goes |
|---|---|
| Engine HTTP call | `Services/Integration/EngineApiClient.cs` + `IEngineApiClient` |
| Engine↔Dashboard HTTP or SignalR type (crosses the boundary) | `src/MediaEngine.Contracts/<Concern>/`; preserve exact JSON names/defaults and add boundary/shape coverage |
| Internal model exposed through an endpoint | Keep it internal and add an explicit API boundary mapper into Contracts |
| Dashboard-only view model or presentation wrapper | `Models/ViewDTOs/`; map explicitly from Contracts and do not duplicate the wire shape |
| Reusable visual component | `Components/<FeatureSlice>/` |
| Detail-page tab or hero piece | `Components/Details/` |
| Full routable page | `Components/Pages/` |
| Settings section | `Components/Settings/<Name>Tab.razor` + register in `SettingsNav` |
| Review-queue surface component | `Components/Library/` |
| Inspector / cards reused by Library or Universe | `Components/LibraryItems/` |
| Reader-player component | inline in `Components/Pages/EpubReader.razor` (reader chrome) or `Components/Shared/Playback*` primitives |
| Listen/player transport controls | `Components/Listen/ListenTransportControls.razor` |
| Media-playback session controller or primitives | `Services/Playback/` |
| Route-building helper | `Services/Navigation/` |
| Editor launcher or state | `Services/Editing/` |
| Theme or device setting | `Services/Theming/` |
| Streaming-service logo / brand asset resolver | `Services/Branding/` |
| Dashboard-side configuration reader or palette plumbing | `Services/Configuration/` |
| Cross-cutting primitive | `Components/Shared/` |
| Blazor host layout wrapper | `Shared/` |
| Application-layer read-model DTO | `src/MediaEngine.Application/ReadModels/` |
| Application-layer query service contract | `src/MediaEngine.Application/Services/IReadServices.cs` |
| Shared configuration shape or inward-facing infrastructure port | `src/MediaEngine.Domain/Configuration/` or `src/MediaEngine.Domain/Contracts/` |
| Concrete SQLite/Dapper repository | `src/MediaEngine.Storage/`; use the `Repository` suffix |
| API implementation without persistence ownership | `src/MediaEngine.Api/Services/`; use a purpose-specific `Service` suffix and keep its interface separate |
| Engine service registration | A focused `src/MediaEngine.Api/DependencyInjection/Tuvima*ServiceCollectionExtensions.cs` module |
| New plugin | New project `src/MediaEngine.Plugin.<Name>/` implementing `ITuvimaPlugin` from `MediaEngine.Plugins` |

---


## Icon rows and player typography (October 5 2026)

Validate icons together whenever they share a row or area. Peer actions must use the same icon family, view box, stroke treatment, rendered glyph size, target size, shape, border, and alignment. Do not mix filled Material and outline playback icons within one peer row. Selected states may change color; intentionally dominant transport Play/Pause may have a larger target and must be documented as a separate role. Icon-only controls require a tooltip and accessible name, plus keyboard focus and selected/expanded semantics.

Favorite, Rate, and More on the dock, phone full player, desktop full player and popout use `PlaybackSongActions`: 44px circular targets, 1px borders and 22px `PlaybackUtilityGlyph` icons in a 24-unit view box with a 1.5-unit stroke. Detail hero actions keep their own size contract. Regression checks cover the shared hosts, glyph types, callbacks and accessible labels. Run the read-only `tools/validate-player-icon-rows.js` in the rendered browser at desktop, phone, short-phone and popout sizes; require equal dimensions and centered glyphs within 1px. Also inspect resting, focus, rated/favorited and open-menu states. Do not infer size parity from source alone.

Lyrics use the shared UI sans-serif family, bold container-sized text and a larger bright active line. Adjacent lines are dimmed without readability-damaging blur. Lyrics has no visible heading or Synced badge; timed highlighting conveys synchronization. Queue/history has no Continue Playing or source header: only Up Next and History tabs. Preserve accessible panel names, meaningful error/empty states, LRCLIB attribution, timing, scrolling and seek behavior. Lyrics and Queue mode triggers remain icon-only with tooltips.

The shared seek rail shows elapsed time on the left and total/remaining time on the right on every player surface, including phone, popout and video. The right label retains its total/remaining toggle.


## Dashboard CSS ownership maintenance

Component styles follow emitted HTML ownership, with documented contextual boundaries for shared controls, C# renderers, render fragments and portals. DetailPage retains page/stage/tab containers; its presentation owners and SequenceEntryContent own their markup styling. The editor's Details, Artwork, Match, History and Header sections take explicit values and callbacks; the shell retains mutable state, permissions, data access and save/cancel/navigation guards. State-changing EventCallbacks keep the shell as receiver. Settings owns canvas descendant rules; AppSwitchRow owns row layout; ListenNavigationSection owns native rail links while inline playlist and dormant audiobook styles stay with ListenPage.

All isolated CSS has a 2,000-line cap. The CSS audit and StyleOwnershipGuardrailTests enforce explicit ownership, line limits, and transfer-aware per-file/aggregate override budgets. Compare actual generated selectors, DOM scopes, computed styles and paired desktop/phone images. Global popup/vendor bridges remain when ancestry requires them. See `docs/reports/css-ownership-2026-10-06.md` for the acceptance state and measured limits; do not infer bundle reduction from extraction alone. Native controls and Release minification now have first-party ownership; verify their current evidence separately from that historical report. Broader per-render interop work remains a separate follow-up.

## Native Dashboard controls and release styling

The Dashboard uses first-party `Components/Shared/App*` primitives and scoped `Services/Ui/` services. `AppPopoverHost`, `AppDialogHost`, `AppToastHost` and `AppThemeProvider` are mounted by Main, Popup, Reader and SetupWizard layouts. Dialog and toast callers use `IAppDialogService` and `IAppToastService`; popup coordination belongs to `AppPopoverService`. Native dialogs preserve the editor's unchanged URL, typed results and unsaved-change interception. Toast actions retain asynchronous Undo behavior. Popup ownership follows the active modal or fullscreen container so a select remains usable inside an editor or player.

`tuvima.tokens.css` owns canonical tokens; first-party theme aliases, `native-utilities.css`, `native-structure.css`, `native-fields.css`, global `app.css` and component-isolated styles own presentation. `AppMaterialIcon` renders the pinned `AppMaterialIcons` SVG catalog; regenerate it with `python scripts/icons/generate-material-icons.py`, retaining `THIRD-PARTY-NOTICES.md`. Material and playback icon families keep their established row contracts.

Release builds use build-only NUglify through `src/MediaEngine.Web/Build/DashboardCss.targets`. Global CSS is copied/minified into `obj/`; scoped bundles are minified after `BundleScopedCssFiles`, before static-asset fingerprinting and gzip/brotli compression. Source CSS stays editable and Debug stays unminified. Minifier errors fail the build. Verify actual Release assets and compressed content; a successful build or smaller source file alone does not establish visual parity or download savings.

Dashboard CI publishes Release and runs scripts/build/verify-dashboard-css.mjs to verify minification, gzip/brotli content, fingerprints and the finalized scoped bundle ceiling. Docker copies src/MediaEngine.Web/Build/ before restore; DockerfileGuardrailTests protects explicit project imports. ComponentParameterGuardrailTests checks direct Razor component parameters, including captured HTML attributes, without accepting arbitrary retired parameter names.

