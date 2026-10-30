# Dashboard (MediaEngine.Web) — Working Rules

> Loaded automatically when Claude works on files under `src/MediaEngine.Web/`. Root `CLAUDE.md` and `.claude/rules/` still apply (Dashboard product-surface rules: [`.claude/rules/dashboard-surfaces.md`](../../.claude/rules/dashboard-surfaces.md)). Per-surface product behaviour (cards, Home, details, TV, Collections, player panels) lives in [`docs/product/presentation-rules.md`](../../docs/product/presentation-rules.md) — read the relevant section before changing a surface.

## Dashboard quality gates

- Listen playback stays under `PlaybackSessionController` and the persistent native audio host. Snapshot controls use direct/broadcast command sinks against the same main owner. Shared bare 22px outline utilities keep 44px targets. The flush dock centers transport, places seek inside the dock above the controls and exposes desktop/tablet Close outside utility overflow. Close saves guarded paused resume before stopping; phone Collapse preserves playback and phone players have no session-stop Close. PlaybackFullPlayer supplies shared phone/popout UI from the captured snapshot; only the phone supplies Collapse. The popout defaults to 420 by 780, fills its window, has no in-player exit, and sends canonical identity navigation to the authorized main owner without reloading audio. Audio popovers/sheets do not resize the page; Ingestion retains its layout-sidebar lease and resize behavior. Speed uses the shared slider popover; Sleep uses the central flat select with one scroller. The main owner holds selected sleep minutes, the absolute deadline or verified captured chapter boundary separately from bookmark drafts. One captured Add/Saved bookmark dialog uses its local desktop opener or bounded phone sheet. Follow [playback architecture](../../docs/architecture/playback.md) and [current verification evidence](../../engineering/reports/player-update-2026-10-03.md), which supersedes the October 2 dock/sidebar layout; do not infer runtime acceptance from source-only checks.
- When product concepts, navigation, editing flows, data-store lifecycle, Docker startup, or CI checks change, update README/docs/AGENTS/CLAUDE in the same change (trigger table: `.claude/rules/docs.md`).
- Verification cost control: while iterating, check structure and computed styles with text tools (page text, accessibility tree, computed CSS). Take screenshots only for before/after proof at the required sizes, and keep them in ignored `.tmp/`.
- Visual checks target the four form factors (phone under 600, tablet 600–1023, desktop, TV) at the supported widths; new CSS breakpoints only 600 / 840 / 1180 (`docs/reference/ui-supported-devices.md`).

## Feature-sliced layout

All Dashboard code follows the **Feature-Sliced** pattern. Every new piece of UI code must go into the correct slice.

### Services (`src/MediaEngine.Web/Services/`)

| Subfolder | Key files | Purpose |
|---|---|---|
| `Branding/` | `StreamingServiceLogoResolver.cs` | Streaming-service logos for display chips |
| `Configuration/` | `DashboardConfigurationReader.cs`, `DashboardPaletteReloadService.cs` | Dashboard-side config read + palette hot-reload |
| `MediaTiles/` | `MediaTileComposerService.cs`, `MediaTileArtworkResolver.cs` | Shared browse tile shelves and sized artwork for Home, Read, Watch, Listen, Collections |
| `Companion/` | `CompanionState.cs`, `CompanionDeviceIdStore.cs` | The phone companion (U-WP13): `CompanionState` follows one of the active profile's own devices or holds a place set by hand, reads the companion context and saves the profile's Spoilers preference (the same setting as Settings); `CompanionPlace` turns a session into the place to read; the browser's own companion device id is a random value kept in local storage through `wwwroot/js/companion-device.js` |
| `Editing/` | `CollectionEditorLauncherService.cs`, `EditorDeviceRule.cs`, `EditingModeContext.cs`, `ItemFieldEditSession.cs`, `*Models.cs` | Editor open/close state and presentation models; `EditorDeviceRule` is the one desktop-only editing rule; `EditingModeContext` (on/off, the one open section, the Save-or-discard and Esc order) and `ItemFieldEditSession` hold a page's Editing Mode; `MediaEditorMatchRule` decides whether Match and Identity applies; wire DTOs remain in Contracts. The modal editor refuses multi-item launches: several items are edited together on the page (`DetailBatchPanel` selection bar, `DetailBatchSection` "Edit together" and `DetailBatchMoveFlow`, as `EditingSection.Batch` and `BatchMove` under the one-open-section rule), and moves go through the Match step (`Components/MediaEditor/Moves/`, `LibraryMove*`). After a move that removes the edited title, the page follows the surviving item through a one-shot resume note in `DetailsOverlayState`. |
| `Integration/` | `EngineApiClient*.cs` + `IEngineApiClient.cs`, `UIOrchestratorService.cs`, `UniverseStateContainer.cs`, `UniverseMapper.cs`, `ProviderCatalogueService.cs`, `IntercomEvents.cs` | All HTTP + SignalR communication with the Engine and explicit mapping into Dashboard presentation state |
| `Formatting/` | `DisplayFormat.cs` | Single home for UI duration/count/speed formatting |
| `Narration/` | `PhraseTemplateService.cs` + interface | Narrated-copy phrase templates |
| `Navigation/` | `MediaNavigation.cs`, `ListenNavigation.cs`, `DetailTabNavigation.cs`, `ShellNavigation.cs`, `ShellDock.cs`, `ProjectLinks.cs`, `DetailsOverlayRoute.cs`, `DetailsOverlayState.cs`, `RoutedPageQuery.cs`, `RoutedPageAddress.cs` | Route-building and shell navigation helpers; `ShellNavigation` is the one catalogue of primary destinations rendered by the desktop top bar, phone menu and chips; `ShellDock` holds the dock tabs and Downloads rules; `ProjectLinks` holds the public docs address used by Help. **Details overlay:** `DetailsOverlayRoute` is the pure, history-correct rule for which page sits under an open title (each detail history entry carries its underlay as `HistoryEntryState`; a reload opens over Home); `DetailsOverlayState` (scoped, injected by `Routes.razor` so it subscribes before the Router) tracks it, tags untagged in-app navigations, dismisses (`history.go(-depth)`, or a replace to the underlay when the depth is unknown or stepping back fails) and holds the `IDetailsOverlayDismissGuard` hook the editing work uses for "Save or discard?"; `DetailsOverlayNavigation` is the one place a replacing navigation to a detail address is made. **Routed pages read their query from `RoutedPageQuery`** (the address they were routed with, cascaded by the route view), never `[SupplyParameterFromQuery]` (guardrail); shells inside a routed page follow `RoutedPageAddress` instead of the address bar, and `LocationChanged` subscribers are allowlisted in `DetailsOverlayGuardrailTests` |
| `Playback/` | `PlaybackSessionController*.cs`, `PlaybackStateMachine.cs`, `PlaybackModels.cs`, `MediaKindClassifier.cs`, `MediaReactionService.cs`, `SavedItemService.cs`, `ListenPageState.cs`, `Audiobook*` | Playback session state, typed commands, queue, bookmarks, lyrics, reactions and saved items |
| `Ui/` | `AppDialogService.cs`, `AppToastService.cs`, `AppPopoverService.cs` | Scoped native dialog/result, toast/action and popup coordination |
| `Vault/` | `NsfwModeState.cs`, `NsfwModeSwitching.cs`, `VaultCall.cs`, `VaultPinPrompt.cs`, `VaultLibraryOnboarding.cs`, `VaultViews.cs`, `VaultViewPreferences.cs`, `IVaultPreferenceStore.cs`, `IVaultCatalogue.cs`, `IVaultFolderAttachment.cs` | Vault mode and the Vault (Videos spec WP10). `NsfwModeState` (scoped) is the one place that decides whether anything about them is drawn: it reads the Engine's `/api/v1/nsfw/status` for the active profile (a Kids profile is never asked; "not found", an unreachable Engine or an answer that is not a mode all mean nothing is drawn), tracks lock and unlock for this device session, locks on close and fails closed. Screens never draw a switch, check, PIN pad, nav entry or the word Vault unless `IsAvailable`/`Unlocked` says so. Calls go through `EngineApiClient.Vault.cs` (shared envelope helpers; the failure they record becomes a `VaultOutcome`; PINs only in bodies and never logged). Viewing options live on this device only (`wwwroot/js/vault-preferences.js`). `EngineVaultCatalogue` (WP14, the real `IVaultCatalogue`) reads the Engine's `/api/v1/vault/*` browsing routes through `EngineApiClient.Vault.cs`: scenes (matched and unmatched together, chips narrow them), folders, and separate performer, studio and tag lists whose tiles open to the owned scenes; a locked or unreachable answer reads "not available"; typed search text goes in a POST body, never an address or log. `VaultArtworkUrls` builds the sized picture addresses (scene 640/1280, performer 400/800, studio 300) through the allow-listed `/engine-image` proxy; `VaultFormat` writes lengths, sizes and dates. `IVaultFolderAttachment` (`EngineVaultFolderAttachment`) puts a folder into the Vault library through the Engine's `POST /api/v1/vault/libraries/{id}/folders` (the administrator's own PIN and the folder travel in the body only; one PIN entry in `VaultLibraryOnboarding` covers creating the library and adding the folder; a missing or unusable folder closes the pad and is told in a plain sentence) |
| `Theming/` | `ThemeService.cs`, `DeviceContextService.cs`, `PaletteProvider.cs`, `AppThemeStyles.cs` | Dark-mode theme, device cascade, palette |

### Components (`src/MediaEngine.Web/Components/`)

| Subfolder | What lives here |
|---|---|
| `Artwork/` | Artwork browser, picker and workspace dialogs |
| `Browse/` | `MediaBrowseShell`, `BrowseQueryBuilder`, `BrowseState`, `BrowseArtworkRules` — browse shell for Read / Watch / Listen subroutes |
| `Cinematic/` | `CinematicHeroCarousel`, `CinematicHeroSurface`, `SurfaceNavigationBar` |
| `Companion/` | `CompanionScreen` (title and place, the Up to here / Everything switch, Who's here, Where you've seen them, Previously), `CompanionPositionPicker` (episode or chapter by hand, or a chapter-number stepper), `CompanionDeviceList`, `CompanionNowPlayingCard` (Home's Now playing). Read-only: no editing, no drawer |
| `Collections/` | `CollectionsPage`, section configuration/layout, `PeopleCatalogList`, `CollectionEditorShell` (only for creating collections, playlists and galleries and managing their items and rules; renaming and covers of an existing Series or curated collection happen in the shared bar) |
| `Details/` | The details overlay: `DetailsOverlayRouteView` (replaces `RouteView` in `Routes.razor`: renders a routed page in its own layout and, for a `/details/` address, keeps the page you came from alive under a `DetailsOverlayHost` modal window, a bottom sheet on phones) and `DetailsOverlayHost`; detail-page composition: `DetailPage`, `DetailHero*`, `DetailTabs`, tab panels (`OverviewTab`, `DetailsTab`, `PeopleTab` (cast and credits), …), `AudioItemTable`, music album pieces, credit cards, `HeroActionRow`, `OverflowActionMenu`; Editing Mode (details overlay spec; no drawer, guardrail): `EditingBar` (the sticky bar), `EditorPanelHost` (the Editor window: tool rail for Details, Artwork, Match and History, fixed Cancel and Save, Review changes as a page of the window), `EditorHistoryTool`, `EditSavedRibbon` (the "Saved N changes" pill after Save), `EditingSaveOrDiscardDialog`, `EditableFieldPencil`, `EditingHeroArtworkButtons`, `EditingLibraryFields`. People, Series, curated collections and Universes edit through the same bar, panel and floating artwork picker (`ItemFieldEditSession.LoadSubjectAsync` / `LoadUniverseSubjectAsync` read the Engine's editor subject; Details and Artwork only, never Match; a Universe's name and description are local overrides that "Reset to canonical value" removes; `UniversePage` lists its Series read-only in the Details panel). There is no person pop-up and no Edit Universe banner. `Shared/AppConfirmDialog` is the one confirmation dialog style. Identity status and artwork source sit in the editor's Match section, for server administrators only. |
| `Layout/` | `MainLayout`, `ReconnectModal` |
| `Library/` | Legacy-named shared helpers still used by browse/list surfaces (configurable tables, status pills). Do not add all-in-one management workflow components here. |
| `LibraryItems/` | Small helpers used by Library + Universe surfaces |
| `Listen/` | Now-playing bar, song table, transport controls, navigation items |
| `MediaEditor/` | Shared pieces for the Collections editors (`MediaEditorPanel`, `MediaEditorSurface`) and the Details tag editor and move picker; the old all-in-one editor shell is gone |
| `MediaHub/` | Shared section scaffolding for Read, Watch, Listen, Collections |
| `MediaTiles/` | `MediaTile`, `MediaGroupTile`, `MediaTileGrid`, `MediaTileShelf` |
| `Navigation/` | `SystemActivityIndicator`, `TopNavAccountMenu` (incl. Needs Review link), `LibrarySwitcherSheet`, and the phone shell (`PhoneNavigationMenu`, `PhoneSectionChips`, `PhoneDownloadsAction`) — see *Phone navigation* in `docs/product/presentation-rules.md` |
| `Pages/` | All routed pages — see the table below |
| `Profiles/` | Profile editor/avatar/sign-in sheets with their `ProfileEditingWorkflow` / `ProfileSignInWorkflow`, `AdminUnlockDialog`, `ProfileManageRules` |
| `Rules/` | Shared rule builder for Collections and View |
| `Search/` | `UniversalSearchOverlay`, results |
| `Settings/` | Settings pages inside the shared `Frame/SettingsPageFrame` (breadcrumb, title, attention notices, page notice, tabs, content) and the one flat menu (`SettingsNav.MenuGroups`, built by `Services/Navigation/SettingsMenuBuilder`): every row is a page, never a folder, and a page's own tabs sit in a row under its title (`Frame/SettingsPageTabs`, the shared `AppCountTabs` style; a page may span several sections, and a tab may point at another section's address). Personal (Profile with Overview, Activity and Taste tabs and the off-menu Apps; Manage Profiles, where Preferences, Playback, Listening, Reading and Home are tabs of a person's own page; Security & Devices), Server (Library & Ingestion, Network & Remote Access, Streaming, Users & Households), Platform (Local AI, Plugins, Integrations), System (Overview, Scheduled Tasks, Logs, Backups, Configuration) and Developer (Developer Tools). A Kids profile sees Profile only. Register sections in `SettingsNav` |
| `Setup/` | First-run setup stages. `SetupMediaStage` draws the shared Libraries table (Watch, Listen, Read, View; the Vault area only once the Vault is on); View rows take folders through `AddViewFolderDialog`. After Continue the first administrator (desktop or tablet, no Vault access yet) is offered `SetupVaultOffer` once: Not now, or an optional PIN typed twice (never required), then Your Vault is on |
| `Shared/` | First-party `App*` design-system primitives (`AppPageState`, `AppErrorState`, `AppEmptyState`, `AppSkeleton`, `AppIcon`, `AppTable`, `AppDialog`, `AppSelect`, `AppTooltip`, `AppCollapsibleRow` (the one collapse arrow for detail page rows), …), `Playback*` control primitives, `TuvimaArtworkStack` |
| `Universe/` | Universe surface components: the `/universe/{qid}` page pieces (`UniverseHero`, `UniverseSeriesShelf`, `UniverseConnections`, `UniverseCastList`, `UniverseAdaptationsList`, `UniversePageText`) and the explorer panels |
| `Vault/` | Vault mode and the Vault area (Videos spec WP10). Every component here renders nothing unless `NsfwModeState` says it applies: `NsfwLockWatcher` (in `MainLayout`; asks the Engine for the active profile, loads `wwwroot/js/vault-lock.js` only while Vault mode is on: Escape twice locks and returns Home, real use restarts the idle timer), `NsfwQuickCheck` (the optional Vault mode check in the profile menu), `VaultHiddenReminder` (Home and Settings: "set up but hidden" line with Turn on and close, only for someone with Vault access who has not turned on Show Vault in the menu), `VaultProfileSettings` (on the profile page: the person's own Vault mode switch, top-menu check and "Turn Vault mode off after being idle for" choice, and the administrator's two switches per adult profile, "Vault mode for X" and "Ask for their PIN to open Vault mode"; there is no separate Vault PIN, the profile PIN is the only PIN; never for a Kids or limited profile), `VaultPinDialog` (number pad; the Engine does the checking), `VaultArea` (+ `VaultViewOptions`, `VaultBlurToggle`, `VaultCover`, `VaultLibrarySettings`, administrators only, inside the area), `VaultSearchGroup` (the separate "Vault" group in Search and the search overlay), `VaultActivityList` (WP12: the quiet one-line-per-file Vault activity list, the only place Vault ingestion shows; inside `VaultLibrarySettings`, drawn nothing when the Engine answers not found), `VaultIdentifyPanel` (WP12: search, candidate list, tick-per-field preview and Apply; desktop-only through `EditorDeviceRule`; a person's own edit cannot be ticked; "None of these" sends nothing; mounted by the Unmatched list's Identify button and the Vault scene page, WP14). WP14 adds `VaultItemList` (every layout: grid, shelf rows, details list, timeline; Performer/Studio/Tag card shapes), `VaultUnmatchedList` (sortable rows; Identify only for an administrator on a computer), `VaultSceneView` (a scene's own page body: Play, stored facts, linked names) and a rewritten `VaultCover` (16:9 scenes, 2:3 performers, 3:2 studio logos, initials or name fallbacks, Blur on every picture, optional hover stills cycled in CSS and off under reduced motion). The third Add folder choice is `AddFolderPlaceDialog` with `ShowVault` |
| `View/` | Places map and timeline pieces |
| `Watch/` | `VideoPlaybackHost`, `VideoContextPanel`, `OwnedEpisodeQueuePlanner` |

### Other top-level folders

| Folder | Purpose |
|---|---|
| `Models/ViewDTOs/` | Dashboard-only presentation shapes; may wrap Contracts through explicit mappers, never own or mirror Engine↔Dashboard JSON |
| `Resources/` | `SharedStrings.resx` (+ `.fr` / `.de` / `.es`) and generated `SharedStrings.cs` |
| `Shared/` | Blazor host layouts: `MainLayout`, `PopupLayout`, `ReaderLayout`, `_Imports` |
| `wwwroot/` | Static assets: CSS (`tuvima.tokens.css`, `app.css`, `native-*.css`), JS, vendored libraries |
| `Build/` | `DashboardCss.targets` (Release CSS minification) |

### Routed pages (`Components/Pages/`)

| Route | Page | Purpose |
|---|---|---|
| `/` | `LibraryBrowsePage` | Home |
| `/read`, `/read/{Tab}` | `ReadPage` | Books + comics browse |
| `/read/{AssetId:guid}` | `EpubReader` | EPUB reader (hosts the foliate-js `BookReader` for every book) |
| `/watch`, `/watch/{Tab}` | `WatchPage` | Movies + TV browse |
| `/watch/player/{WorkId:guid}` | `WatchPlayerPage` | Video player |
| `/listen`, `/listen/{Tab}` | `ListenBrowsePage` | Listen discovery, Music / Audiobook browse |
| `/listen/music/playlists/{CollectionId:guid}`, `/listen/music/playlists/system/{PlaylistKey}` | `ListenPage` | Playlist queue and editing |
| `/listen/player-popup` | `ListenPlayerPopupPage` | Detached listen window |
| `/details/{EntityType}/{Id:guid}` | `UnifiedDetailPage` | The one detail page for every entity type (`Components/Details/`), shown as an overlay over the page you came from (`DetailsOverlayRouteView`); TV episodes open via their show with `?episode=` |
| `/collections`, `/collections/{Section}` | `Collections` | Collections and universes |
| `/universe/{Qid}` | `UniversePage` | A Universe: hero, each Series in order (with missing entries when the profile shows them), standalone works, Connections (Cast, Adaptations) and, for desktop administrators, Edit, which shows the editing banner and the existing Universe editor in-line in the section slot. Pieces in `Components/Universe/`, state in `UniversePageState`, one Engine read (`/universe/{qid}/overview`) |
| `/companion`, `/companion/{WorkId:guid}` | `CompanionPage` | The phone companion: follows the chosen device (`/companion`) or holds the title opened from its "Companion" menu item and lists its episodes or chapters (`/companion/{workId}`); polls every 5 seconds while following. Reading only; the Engine's `GET /api/v1/companion/context` supplies everything shown |
| `/universe/{Qid}/explore` | `ChronicleExplorer` | Universe graph explorer (desktop; paths and family tree) |
| `/my-library` | `MyLibrary` | The personal page (Continue, My List, Recent activity preview, Liked; Downloads in the app); the phone dock's My Library tab |
| `/my-activity` | `MyActivity` | The signed-in person's own Activity page outside Settings (Kids profiles and quick access); same component as Settings > Overview > Activity, own scope only |
| `/for-me` | `ForMe` | Personal shelves and See All grids |
| `/all/{Lane}/{ShelfKey}` | `ShelfGridPage` | View all: the full list behind a shelf as a grid, with a remembered sort and the filters the Engine returns; sort and filters live in the query string (replaced, never pushed); loads 48 at a time through `wwwroot/js/load-more-observer.js` with a Load more button always under the grid |
| `/who` | `ProfilePickerPage` | Profile picker |
| `/setup` | `SetupPage` | First-run setup |
| `/recently-added` | `RecentlyAddedPage` | Recently added |
| `/search` | `SearchPage` | Global search |
| `/vault`, `/vault/{View}`, `/vault/{View}/{Id}`, `/vault/scene/{AssetId}` | `VaultPage`, `VaultScenePage` | The fourth top-level area (Videos spec D45). In the menu and routable only while Vault mode is on for this device session; for anyone else the page draws nothing and sends them to `/not-found`. The page title is plain ("Library"). Views: Recently added, Scenes, Folders, Unmatched, Images, Performers, Studios, Tags, and Library settings for administrators. Lists come from `IVaultCatalogue` (`EngineVaultCatalogue`). Recently added, Scenes, Folders and Unmatched list scenes (an unmatched video appears only in those); Performers, Studios and Tags are lists of their own, never on the main page, and a tile opens to its owned scenes at `/vault/{View}/{Id}`; `/vault/scene/{AssetId}` is one scene's page (Play goes to `/watch/player/{WorkId}`). Images stay empty until WP16 |
| `/view`, `/view/*` (artwork, contributions, documents, folders, galleries, people, places) | `View*Page` | Personal and shared View library |
| `/downloads` | `DownloadsPage` | Explains downloads live in the Library app |
| `/plugin/{pluginId}/{pageKey}` | `PluginPage` | A page a plugin adds (WP16): list, grid, guide timeline (`AppTimelineSlot`), form or detail, drawn from the plugin's records; menu entries come from `GET /plugin-pages/menu` (account menu, phone menu for phone-suitable types only) |
| `/settings`, `/settings/{Section}[/{Subsection}[/{Detail}]]` | `Settings` | Settings shell; lands on Profile (`/settings/overview`). Needs Review is its own page at `/settings/review` (`NeedsReviewPageContent`, server administrators only, off the menu, opened from the administrator banner); Recently Added is `/settings/recently-added` (opened from a Needs Review row's "Find in recent history" and the old `/operations` links; Library & Ingestion's "View all" opens the Activity log) and holds no review list; the old `?review=expanded` link redirects to `/settings/review` |
| `/engine-activity-artwork/{key}`, `/engine-activity-export` | `Program.cs` minimal endpoints | Stream the signed-in person's private activity pictures and CSV export from the Engine with that person's own sign-in (never the service credential) |
| `/operations/*` | `Operations` | Redirects only (to `/settings/ingestion`, `/settings/recently-added`, `/settings/review` for `needs-review`, or for `activity`/`timeline` the Activity log at `/settings/system/logs?tab=events`) |
| `/design-system/components` | `DesignSystemPreview` | Component preview |
| `/not-found`, `/Error` | `NotFound`, `Error` | Error pages |

### Rules for adding new code

| New code type | Where it goes |
|---|---|
| Engine HTTP call | `Services/Integration/EngineApiClient*.cs` + `IEngineApiClient` |
| Engine↔Dashboard HTTP or SignalR type | `src/MediaEngine.Contracts/<Concern>/`; preserve exact JSON names/defaults and add boundary/shape coverage |
| Internal model exposed through an endpoint | Keep it internal; add an explicit API boundary mapper into Contracts |
| Dashboard-only view model | `Models/ViewDTOs/`; map explicitly from Contracts |
| Reusable visual component | `Components/<FeatureSlice>/` |
| Detail-page tab or hero piece | `Components/Details/` |
| Full routable page | `Components/Pages/` |
| Settings section | `Components/Settings/<Name>Tab.razor` + register in `SettingsNav` |
| Reader-player component | `Components/Pages/BookReader.razor` (book access stays in `wwwroot/js/book-reader.js`), `EpubReader.razor` (the `/read` route page only) or `Components/Shared/Playback*` |
| Listen transport controls | `Components/Listen/ListenTransportControls.razor` |
| Playback session controller or primitives | `Services/Playback/` |
| Route-building helper | `Services/Navigation/` |
| Editor launcher or state | `Services/Editing/` |
| Theme or device setting | `Services/Theming/` |
| Cross-cutting primitive | `Components/Shared/` |
| Blazor host layout wrapper | `Shared/` |

Non-Dashboard placement (Application, Domain, Storage, Api, plugins) is in root `CLAUDE.md`.

---

## Icon rows and player typography

Validate icons together whenever they share a row or area. Peer actions must use the same icon family, view box, stroke treatment, rendered glyph size, target size, shape, border, and alignment. Do not mix filled Material and outline playback icons within one peer row. Selected states may change color; intentionally dominant transport Play/Pause may have a larger target and must be documented as a separate role. Icon-only controls require a tooltip and accessible name, plus keyboard focus and selected/expanded semantics.

Favorite, Rate, and More on the dock, phone full player, desktop full player and popout use `PlaybackSongActions`: 44px circular targets, 1px borders and 22px `PlaybackUtilityGlyph` icons in a 24-unit view box with a 1.5-unit stroke. Detail hero actions keep their own size contract. Regression checks cover the shared hosts, glyph types, callbacks and accessible labels. Run the read-only `tools/validate-player-icon-rows.js` in the rendered browser at desktop, phone, short-phone and popout sizes; require equal dimensions and centered glyphs within 1px. Also inspect resting, focus, rated/favorited and open-menu states. Do not infer size parity from source alone. Run the read-only `tools/validate-touch-targets.js` at 360, 768 and 1440 px on every page you touch: it lists controls under 44 × 44 px (48 on the bottom dock) and elements wider than the viewport; touch/narrow tap-target rules live in the shared `Shared 44px tap-target contract` block in `wwwroot/app.css` (token `--tl-tap-target-min`), and `100vh` is allowed only as a fallback line directly before its `100dvh` twin.

Lyrics use the shared UI sans-serif family, bold container-sized text and a larger bright active line. Adjacent lines are dimmed without readability-damaging blur. Lyrics has no visible heading or Synced badge; timed highlighting conveys synchronization. Queue/history has no Continue Playing or source header: only Up Next and History tabs. Preserve accessible panel names, meaningful error/empty states, LRCLIB attribution, timing, scrolling and seek behavior. Lyrics and Queue mode triggers remain icon-only with tooltips.

The shared seek rail shows elapsed time on the left and total/remaining time on the right on every player surface, including phone, popout and video. The right label retains its total/remaining toggle.

## Dashboard CSS ownership

Component styles follow emitted HTML ownership, with documented contextual boundaries for shared controls, C# renderers, render fragments and portals. DetailPage retains page/stage/tab containers; its presentation owners and SequenceEntryContent own their markup styling. The editor's Details, Artwork, Match, History and Header sections take explicit values and callbacks; the shell retains mutable state, permissions, data access and save/cancel/navigation guards. State-changing EventCallbacks keep the shell as receiver. Settings owns canvas descendant rules; AppSwitchRow owns row layout; ListenNavigationSection owns native rail links while inline playlist and dormant audiobook styles stay with ListenPage.

All isolated CSS has a 2,000-line cap. The CSS audit and StyleOwnershipGuardrailTests enforce explicit ownership, line limits, and transfer-aware per-file/aggregate override budgets. Compare actual generated selectors, DOM scopes, computed styles and paired desktop/phone images. Global popup/vendor bridges remain when ancestry requires them. See `engineering/reports/css-ownership-2026-10-06.md` for the acceptance state and measured limits; do not infer bundle reduction from extraction alone.

## Native Dashboard controls and release styling

The Dashboard uses first-party `Components/Shared/App*` primitives and scoped `Services/Ui/` services. `AppPopoverHost`, `AppDialogHost`, `AppToastHost` and `AppThemeProvider` are mounted by Main, Popup, Reader and SetupWizard layouts. Dialog and toast callers use `IAppDialogService` and `IAppToastService`; popup coordination belongs to `AppPopoverService`. Native dialogs preserve the editor's unchanged URL, typed results and unsaved-change interception. Toast actions retain asynchronous Undo behavior. Popup ownership follows the active modal or fullscreen container so a select remains usable inside an editor or player. Pages use the shared components rather than reimplementing their controls; selectors size to the selected label, then ellipsize with a full-label tooltip.

Phone primitives (shared, opt-in, desktop unchanged): `AppDialogOptions.Sheet = AppDialogSheetMode.Phone|PhoneAndTablet` presents a dialog as a bottom sheet (grip, swipe down, backdrop tap, Escape); `<AppTable CardsOn="AppTableCardsMode.Phone|PhoneAndTablet">` stacks rows as cards and needs `data-card-role="title|subtitle|value|actions|hidden"` on the header cell and every body cell of the column (CSS in `wwwroot/app.css`); `<AppRowMenu Items=... />` is the 44 px row "more" button (sheet on phones, `AppOverflowMenu` popover elsewhere). Use these instead of hover-only or drag-only row actions.

`tuvima.tokens.css` owns canonical tokens; first-party theme aliases, `native-utilities.css`, `native-structure.css`, `native-fields.css`, global `app.css` and component-isolated styles own presentation. `AppMaterialIcon` renders the pinned `AppMaterialIcons` SVG catalog; regenerate it with `python scripts/icons/generate-material-icons.py`, retaining `THIRD-PARTY-NOTICES.md`. Material and playback icon families keep their established row contracts.

Release builds use build-only NUglify through `src/MediaEngine.Web/Build/DashboardCss.targets`. Global CSS is copied/minified into `obj/`; scoped bundles are minified after `BundleScopedCssFiles`, before static-asset fingerprinting and gzip/brotli compression. Source CSS stays editable and Debug stays unminified. Minifier errors fail the build. Verify actual Release assets and compressed content; a successful build or smaller source file alone does not establish visual parity or download savings.

Dashboard CI publishes Release and runs `scripts/build/verify-dashboard-css.mjs` to verify minification, gzip/brotli content, fingerprints and the finalized scoped bundle ceiling. Docker copies `src/MediaEngine.Web/Build/` before restore; DockerfileGuardrailTests protects explicit project imports. ComponentParameterGuardrailTests checks direct Razor component parameters, including captured HTML attributes, without accepting arbitrary retired parameter names.
