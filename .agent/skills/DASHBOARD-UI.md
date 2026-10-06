# Skill: Dashboard UI Operations

> **Mirrors:** `CLAUDE.md` Section 6. Keep both in sync per `.agent/SYNC-MAP.md`.

> Last updated: 2026-10-04

---

## Purpose

Use this skill when changing Dashboard visual components, routed pages, navigation, state management, inline editing, or Settings/Admin surfaces.

---

## Current Dashboard Model

Home, Read, Watch, Listen, Collections, and Search are the user-facing discovery and media surfaces. Detail pages and media rows/cards launch inline editing through the shared media editor. Review Queue is only for blocked or uncertain items that need human confirmation. Settings/Admin is for configuration and operational/system concerns.

Canonical detail pages use a full-width desktop cinematic stage that fills `95svh`, leaving its lower navigation visible while retaining substantially more native 16:9 artwork. Edge-to-edge landscape artwork is pinned top-center beneath translucent global and lower navigation; protect it with restrained top/left shading and a deep bottom fade. Music albums specialize the shared hero with a cover-derived blurred atmosphere, a large sharp album/record composition centered in the right half, shared bottom-left identity, Play and Shuffle on the first action row, icon-left utilities on the second, and separate **Tracks** and **Details** navigation items. Keep Tracks borderless and transparent, retain Show missing without search, resolve identified artists into clickable managed-person portraits with combined roles in a right rail, and show every other owned album from the exact canonical primary artist in a conditional full-width **More by** shelf; technical/source content belongs only to Details. Audiobooks retain the shared detail structure and specialized chapter/player tools while using a substantially larger cover where viewport height permits and without adding a video-style whole-book Restart action. Never restore audiobook Chapters or Editions as shared tabs, and omit Play All and duplicate My List. Structural pages place their ordered sequence, collection, appearance, or owned-works array below the stage and end with a compact owned summary plus a simple progress bar only when the total is authoritative. Collections use a neutral no-backdrop hero with the same up-to-four shared artwork cluster used by collection tiles at a dominant scale; they have no collection action buttons and instead show one compact year-range, total-item, and applicable lane-count row. Comic and book covers share a larger cover-only foreground envelope, as do poster-only movie/TV heroes; real landscape backdrops remain edge-to-edge. Person portraits use the larger envelope, people use one continuous page without a tab shelf, and linked identities reuse cast/credit portrait cards. Utility actions use larger icon-left-of-label controls on a left-growing row below the primary action. No region may overlap or clip another. Long sequences keep named Jump to options and conditionally expose real alternate set/arc or missing-item controls. Read/Watch/Listen filters belong only to mixed-media collections and people with works in more than one lane. Person works show only owned canonical eligible credits, deduplicated per work with all eligible roles, use portrait cover/poster or square album art instead of landscape backgrounds, and prefer an owned asset's canonical title over a parent collection label. Overview follows the structural surface and combines attributed description/biography, a purpose-built cast or credits peek, series/collection context, and related content. Listen playlists remain lane-local specialized queue/edit surfaces.

Home is the only cinematic landing and reuses `CinematicHeroCarousel`/`CinematicHeroSurface`, `DetailHeroContent`, and `HeroBackdrop`. Home uses an approximately 80svh desktop feature (72svh on short screens), with bounded cover-led composition for unknown, portrait or undersized sources; measured landscapes use the aspect-band framing policy. Phones and short landscape screens use content-sized composition. Detail heroes retain `95svh`. Opposite edge controls use 48px targets and the centered active dash is thicker, wider, and purple. Focus, hover, pause, and reduced motion suspend rotation. Home has no lane submenu. Its rows are Continue Across Media, Recently Added, then populated Watch, Read, Listen, and Collections & Lists; Fresh is retired. All/Watch/Read/Listen/View recent filters preserve scope in `/recently-added?type=`. View remains a separately authorized Mine source with render-time thumbnail/preview grants and native viewer behavior. Started TV Home features retain show title plus exact owned `Sx Ey · Episode Title`, episode still/synopsis/action, and show-art fallback; untouched shows and Watch discovery use show art. Explicit Engine subject/state/episode contracts drive structurally deduplicated spotlights and the same episode identity in Continue/details. Shared artwork-edge progress appears only on partial long-form items; complete state is text/check, albums/tracks/View have no completion bar. Resting movies/TV/books/comics use portrait frames, albums and audiobooks use square frames, and only Continue Watching uses landscape stills. Artwork is contained when its native ratio differs from its frame. Home shelves share one artwork height; only Home Watch discovery cards expand horizontally in their row; Watch browse grids, episode lists, and non-Watch cards remain outline-only. Cards retain one semantic detail link and partial long-form artwork-edge progress, without detached percentage captions. Read, Watch, Listen, and Collections retain compact route navigation, anchored desktop rails, unboxed scoped filters, and their established browsing contracts.

The removed all-in-one management workflow must not be recreated. Do not add routes, navigation labels, implementation types, or an all-in-one media correction workbench for it.

Non-TV series and collection containers use `Components/MediaTiles/MediaGroupTile.razor`, a dedicated fixed-size landscape card. Rest shows two to four representative artworks in one slightly angled, overlapping cluster built from real portrait, square, or wide metadata. Home and Discover shelf hover may add the purple boundary/glow plus the compact top type/title/status overlay without changing the composition. Vertically wrapping grids use `MediaTileHoverMode.GlowOnly`: individual cards retain a compact title/year below the art, while groups embed their all-caps title, year, and owned count into a restrained bottom gradient inside the card. Direct group hover adds only the glow and small open cue; no type pill, full background replacement, or cinematic popover appears. Direct browse filter areas include a tile-size slider; the width changes while portrait, square, and landscape ratios remain stable, and Music defaults smaller. TV shows on Home and Discover remain on `MediaTile`, with a show cover at rest and show-level cinematic background and logo on Home hover, without facts or description. Do not give TV shows the generic container summary merely because their storage identity is series-backed. Individual and Continue cards must retain their existing renderers and shelf geometry. Every card is one semantic link to details, and cinematic expansion remains action-free.

Media-specific browse results use wrapping tiled grids and must not become horizontally scrolling media rows. Series detail keeps proven-adjacency connectors behind the numbered nodes, uses a stronger purple frame glow as the only visible current-item state, and exposes current context through `aria-current`. Missing-item visibility inherits media defaults from `config/ui/library-preferences.json`; the database stores only explicit profile-and-series overrides, and reset deletes the override.

---

## Key Files

| File | Role |
|---|---|
| `src/MediaEngine.Web/Shared/MainLayout.razor` | Global shell, navigation, search, My List, unified activity indicator, account menu, engine status, command palette, and persistent playback host. |
| `src/MediaEngine.Web/Components/Navigation/TopNavAccountMenu.razor` | Profile switching, permission-gated Needs Review, Settings, Help, and conditional sign-out. |
| `src/MediaEngine.Web/Components/Navigation/SystemActivityIndicator.razor` | Busy/idle shell status for playback, ingestion, AI, enrichment, and durable Engine operations. |
| `src/MediaEngine.Web/Components/Pages/LibraryBrowsePage.razor` | Home/discovery page. |
| `src/MediaEngine.Web/Components/Cinematic/` | Shared hero stage/carousel and `SurfaceNavigationBar` used by lane filters and detail tabs. |
| `src/MediaEngine.Web/Components/Browse/MediaBrowseShell.razor` | Shared Read, Watch, and Listen browse behavior. |
| `src/MediaEngine.Web/Components/Details/DetailPage.razor` | Detail-page surface and inline edit launcher. |
| `src/MediaEngine.Web/Components/Universe/BookDetailContent.razor` | Read-detail surface for books. |
| `src/MediaEngine.Web/Components/Collections/` | Collections route, cards, sections, and editor shell. |
| `src/MediaEngine.Web/Components/Settings/SettingsReviewQueueTab.razor` | Review Queue exception workflow. |
| `src/MediaEngine.Web/Components/Settings/IngestionTasksTab.razor` | Ingestion operations dashboard. |
| `src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor` | Normal, Review, and Batch media editing shell. |
| `src/MediaEngine.Web/Components/MediaTiles/MediaGroupTile.razor` | Fixed-size series/collection artwork cluster with the Home/Discover top overlay and direct-grid embedded identity modes. |
| `src/MediaEngine.Web/Components/Listen/ListenTransportControls.razor` | Shared Listen transport controls for dock and full-player presentations. |
| `src/MediaEngine.Web/Services/Editing/MediaEditorLauncherService.cs` | Central editor launch and return path. |
| `src/MediaEngine.Web/Services/Playback/PlaybackSessionController.cs` | Listen playback session controller, command dispatch, queue/session state, and transport command boundary. |
| `src/MediaEngine.Web/Services/Integration/EngineApiClient.cs` | Engine HTTP and SignalR integration. |

---

## Component Placement

| What you are adding | Preferred location |
|---|---|
| Routed page | `Components/Pages/` or the existing feature folder if the route already lives there |
| Shared lane browsing | `Components/Browse/` |
| Detail composition | `Components/Details/` |
| Collection UI | `Components/Collections/` |
| Read/watch/listen-specific UI | `Components/Universe/`, `Components/Watch/`, or `Components/Listen/` according to existing usage |
| Media correction UI | `Components/MediaEditor/` |
| Settings/Admin tab | `Components/Settings/` |
| Cross-cutting primitive | `Components/Shared/` |
| Navigation/search shell | `Components/Navigation/` or `Shared/MainLayout.razor` |
| Listen transport controls | `Components/Listen/ListenTransportControls.razor` |

Use existing feature folders before introducing new abstractions.

---

## Editing Rules

1. Normal corrections launch from the current media surface or detail page.
2. Review corrections launch from Review Queue.
3. Batch corrections only launch from a real selected set.
4. All three paths use `MediaEditorLauncherService` and `SharedMediaEditorShell`.
5. After a successful save, refresh the current surface and keep the user in context.
6. Canceled edits must not mutate UI state.

---

## State and Integration

- `EngineApiClient` is the typed Dashboard client for Engine endpoints.
- SignalR events keep ingestion, activity, enrichment, and review indicators current.
- Keep review attention inside the permission-aware account menu; do not reintroduce a standalone navbar notification button.
- Use `ShellActivityState` for cross-cutting busy/idle state instead of adding one-off navbar progress controls.
- Dashboard view models live under `Models/ViewDTOs/`; do not pass storage implementation models into UI.
- Razor components must not contain direct SQL.
- Settings/Admin pages should call Engine APIs or typed services, not storage repositories.
- Main Listen host adapters read `PlaybackSessionController`; reusable controls consume captured snapshots and command sinks rather than another circuit's controller. Browser transport work stays behind the persistent Web audio host and the `listenPlayback` JS bridge.
- Do not duplicate Listen play/pause, skip, previous/next, or chapter controls outside `ListenTransportControls.razor`.
- Playback utilities use shared bare 22px glyphs inside at least 44px targets. The flush audio dock centers transport, places seek inside the dock above the controls and exposes desktop/tablet Close outside utility overflow. Close saves guarded paused resume before stopping; phone Collapse preserves playback and phone players have no session-stop Close. PlaybackFullPlayer supplies shared phone/popout UI from the captured snapshot; only the phone supplies Collapse. The popout defaults to 420 by 780, fills its window, has no in-player exit, and sends canonical identity navigation to the authorized main owner without reloading audio. Audio popovers/sheets leave page width and scroll unchanged; Ingestion keeps its layout-sidebar lease and resize behavior. Snapshot-driven controls use direct/broadcast sinks against one main owner. Speed uses the shared slider popover and Sleep the central select; sleep deadlines and verified chapter targets stay owner-captured. Bookmarks retain one captured Add/Saved dialog with its local desktop opener or bounded phone sheet. Follow `docs/architecture/playback.md` and `docs/reports/player-update-2026-10-03.md`; do not restore stacked workspaces or the earlier dock/sidebar layout.

---

## Quality Gates

- Keep removed all-in-one management workflows out of active routes, navigation, docs, and CSS.
- Keep media correction inline through the shared editor.
- Keep Review Queue focused on blocked, uncertain, low-confidence, or unresolved items.
- Keep Settings/Admin focused on configuration, operations, diagnostics, users, providers, plugins, AI, ingestion, and review.
- Run restore, build, tests, and relevant docs checks before closing UI or documentation work.

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
- **Shared controls.** Dashboard controls use first-party `Components/Shared/App*` primitives, including `AppSelect`/`AppTypedSelect`, `AppTooltip`, `AppRangeSlider`, `AppProgressBar`, and `AppSpinner`. Their native HTML, SVG, CSS and JavaScript own sizing, appearance, focus and expanded/selected semantics. Pages use the shared components rather than reimplementing their controls.
- **Intrinsic selectors.** Selectors such as the series selector size to the selected label within the available width, then ellipsize with a full-label tooltip.
- **Follow-up.** CSS ownership, native shared controls and Release minification are implemented. The earlier CSS ownership report retains its historical bundle acceptance state; current acceptance requires measured generated assets and paired visual evidence. Broader per-render interop optimization remains separate.
- **Docs toolchain.** The documentation toolchain requires Material for MkDocs 9.7.7 or later.


### Dashboard CSS ownership maintenance

Component styles follow emitted HTML ownership, with documented contextual boundaries for shared controls, C# renderers, render fragments and portals. DetailPage retains page/stage/tab containers; its presentation owners and SequenceEntryContent own their markup styling. The editor's Details, Artwork, Match, History and Header sections take explicit values and callbacks; the shell retains mutable state, permissions, data access and save/cancel/navigation guards. State-changing EventCallbacks keep the shell as receiver. Settings owns canvas descendant rules; AppSwitchRow owns row layout; ListenNavigationSection owns native rail links while inline playlist and dormant audiobook styles stay with ListenPage.

All isolated CSS has a 2,000-line cap. The CSS audit and StyleOwnershipGuardrailTests enforce explicit ownership, line limits, and transfer-aware per-file/aggregate override budgets. Compare actual generated selectors, DOM scopes, computed styles and paired desktop/phone images. Global popup/vendor bridges remain when ancestry requires them. See `docs/reports/css-ownership-2026-10-06.md` for the acceptance state and measured limits; do not infer bundle reduction from extraction alone. Native controls and Release minification now have first-party ownership; verify their current evidence separately from that historical report. Broader per-render interop work remains a separate follow-up.

### Native Dashboard controls and release styling

The Dashboard uses first-party `Components/Shared/App*` primitives and scoped `Services/Ui/` services. `AppPopoverHost`, `AppDialogHost`, `AppToastHost` and `AppThemeProvider` are mounted by Main, Popup, Reader and SetupWizard layouts. Dialog and toast callers use `IAppDialogService` and `IAppToastService`; popup coordination belongs to `AppPopoverService`. Native dialogs preserve the editor's unchanged URL, typed results and unsaved-change interception. Toast actions retain asynchronous Undo behavior. Popup ownership follows the active modal or fullscreen container so a select remains usable inside an editor or player.

`tuvima.tokens.css` owns canonical tokens; first-party theme aliases, `native-utilities.css`, `native-structure.css`, `native-fields.css`, global `app.css` and component-isolated styles own presentation. `AppMaterialIcon` renders the pinned `AppMaterialIcons` SVG catalog; regenerate it with `python scripts/icons/generate-material-icons.py`, retaining `THIRD-PARTY-NOTICES.md`. Material and playback icon families keep their established row contracts.

Release builds use build-only NUglify through `scripts/build/dashboard-css.targets`. Global CSS is copied/minified into `obj/`; scoped bundles are minified after `BundleScopedCssFiles`, before static-asset fingerprinting and gzip/brotli compression. Source CSS stays editable and Debug stays unminified. Minifier errors fail the build. Verify actual Release assets and compressed content; a successful build or smaller source file alone does not establish visual parity or download savings.
