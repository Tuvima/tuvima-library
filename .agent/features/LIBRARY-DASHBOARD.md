# Feature: Library Dashboard

> **Mirrors:** `CLAUDE.md` Section 3.11 and Section 6. Keep both in sync per `.agent/SYNC-MAP.md`.

> Last audited: 2026-10-03 | Auditor: Codex

---

## Product Model

The Dashboard is the user-facing surface for a local-first story library. It is organized by user intent rather than by a separate media management workspace:

- **Home** (`/`) uses a shared cinematic feature, Continue Across Media, Recently Added, then the existing populated lane/collection shelves. Home uses an approximately 80svh desktop feature (72svh on short screens), with bounded cover-led composition for unknown, portrait or undersized sources; measured landscapes use the aspect-band framing policy. Phones and short landscape screens use content-sized composition. Detail heroes retain `95svh`. Opposite edge arrows and a centered purple active dash remain accessible above mobile navigation. Started TV Home keeps show title plus exact owned episode identity/still/synopsis/action; untouched shows and Watch discovery use show art. Resting movies/TV/books/comics use portrait frames, albums and audiobooks use square frames, and only Continue Watching uses landscape stills. Artwork is contained when its native ratio differs from its frame. Home shelves share one artwork height; only Home Watch discovery cards expand horizontally in their row; Watch browse grids, episode lists, and non-Watch cards remain outline-only. Cards retain one semantic detail link and partial long-form artwork-edge progress, without detached percentage captions. Recent All/Watch/Read/Listen/View filters use the same `/recently-added?type=` page; View contributes independently authorized Mine assets with fresh bounded grants and native viewer behavior. Fresh is retired, albums/tracks/View have no completion bars, and cards remain one detail link.
- **Read** (`/read`) is for books and comics.
- **Watch** (`/watch`) is for movies and TV.
- **Listen** uses `/listen` for Discover, `/listen/music` for tiled album browsing, and `/listen/audiobooks` for tiled audiobook browsing; its permanent rail provides Albums, Songs, and Artists shortcuts.
- **Collections** (`/collections`) is for broader rollups where multiple shelves share a series, franchise, or universe relationship.
- **Search** (`/search`) searches across the library.
- **My List** (`/my-list`) shows the active profile's saved shortlist.
- **Detail pages** use one canonical full-width surface for media, series, standard collections, and people. The desktop cinematic stage fills `95svh` so its lower navigation stays visible while retaining substantially more native 16:9 artwork; edge-to-edge landscape art is pinned top-center beneath translucent global and lower navigation, with its lower region darkened into the following content. Music albums use that shared stage with a cover-derived blurred atmosphere, a large sharp album/record composition centered in the right half, bottom-left identity, Play and Shuffle together, icon-left utilities below, plus separate **Tracks** and **Details** navigation items. The restrained Tracks surface gives most width to a borderless track list with Show missing and no search, resolves identified artists into clickable managed-person portraits with combined roles in the right rail, and conditionally renders every other owned album from the exact canonical primary artist in a full-width **More by** shelf; technical/source content belongs only to Details. Audiobooks retain their shared detail structure and chapter/player tools while using a substantially larger cover where viewport height permits. Audiobook Chapters remain embedded, Editions stays retired, and lists do not repeat Play All or My List. Structural details render their sequence, collection, appearance, or owned-person-work array below the stage and end with a compact ownership summary plus authoritative-total progress when available. Collections have no member-derived backdrop: they use the neutral hero, a dominant version of the shared up-to-four collection-tile artwork cluster, no collection action buttons, and one compact row for year range, total items, and applicable Read/Watch/Listen counts. Comic and book covers share a larger cover-only foreground envelope, and poster-only movie/TV details use the same empty hero space without changing true landscape backdrops. Person portraits are enlarged, people use one page without tabs, linked identities reuse cast/credit portrait cards, and larger icon-left utility controls grow from the left beneath the primary action. Long sequences retain named Jump to options and show alternate set/arc or missing controls only when real data supports them. Read/Watch/Listen filters belong only to mixed-media collections and multi-lane people. Person works are owned-only canonical credits rendered with portrait cover/poster or square album art, never landscape backgrounds, and prefer owned-asset canonical titles over parent collection labels. Overview follows the structural surface and combines description/biography, a purpose-built credits or cast peek, series/collection context, and related content; playlists remain specialized Listen surfaces.
- **Settings/Admin** owns configuration and operations, including Review Queue.

Lane-level shelves stay in their lane. A single book series, film series, album, or audio series should not duplicate itself as a top-level Collections tile unless it connects to a broader cross-shelf relationship.

Non-TV series and collection containers render through the dedicated fixed-size landscape `MediaGroupTile`. Rest is artwork-led: two to four representative images use approved count-and-shape templates and retain their natural portrait, square, or wide ratios. Home and Discover hover preserve the artwork and dimensions, add the purple boundary/glow, and reveal only type, title, and one status line in a compact top overlay. Direct wrapping grids instead embed an all-caps title, year, and owned count inside a restrained bottom gradient and use glow-only hover with a small open cue. Their filters are unboxed page content with larger shared typography: search leads, genre/creator/year use one searchable multi-select checklist, boolean filters remain stable checkboxes, and tile sizing plus layout are right aligned; tile width changes retain the artwork ratio, with Music defaulting smaller. The whole surface opens the group; there are no buttons, rotating artwork, carousel, or child-level actions. TV shows render through `MediaTile`; the show-level cinematic backdrop and rich identity hover remain limited to Home and Discover. Every individual or group card is one details link. Individual and Continue cards keep their existing renderers.

Within Watch, TV shows occupy their own `TV Shows` shelf. The separate `Series` shelf contains only dynamically aligned movie series and explains that automatic grouping in its subtitle. Do not combine those shelves or place TV show cards in the Series row.

Home alone uses the landing carousel; detail retains its existing full-height shared hero. Engine spotlights own structural de-duplication and explicit episode/state context. Continue, Home, and episode sequence details read the same active-profile state without stale progress caches. Episode season summaries count distinct owned episodes only. Both heroes reuse `DetailHeroContent` and `HeroBackdrop`; no artwork positioning or generated assets are introduced. Recently Added keyset cursors bind source-qualified identity, added time, profile, and filter. Mobile acceptance includes small/landscape phones, enlarged text, safe areas, readable status without hover, and 48px controls.

---

## Core Entry Points

| Area | Files |
|---|---|
| Shell, search, My List, account menu, unified activity, engine state | `src/MediaEngine.Web/Shared/MainLayout.razor`, `src/MediaEngine.Web/Components/Navigation/` |
| Home/discovery | `src/MediaEngine.Web/Components/Pages/LibraryBrowsePage.razor` |
| Shared lane browsing | `src/MediaEngine.Web/Components/Browse/MediaBrowseShell.razor` |
| Read details | `src/MediaEngine.Web/Components/Universe/BookDetailContent.razor` |
| General details | `src/MediaEngine.Web/Components/Details/DetailPage.razor` |
| Collections | `src/MediaEngine.Web/Components/Collections/` |
| Series/collection tiles | `src/MediaEngine.Web/Components/MediaTiles/MediaGroupTile.razor` |
| Review Queue | `src/MediaEngine.Web/Components/Settings/SettingsReviewQueueTab.razor` |
| Shared editor | `src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor` |
| Ingestion dashboard | `src/MediaEngine.Web/Components/Settings/IngestionTasksTab.razor` |
| Listen playback controller | `src/MediaEngine.Web/Services/Playback/PlaybackSessionController.cs` |
| Shared Listen transport controls | `src/MediaEngine.Web/Components/Listen/ListenTransportControls.razor` |

---

## Business Rules

| Rule | Meaning |
|---|---|
| Surface-per-concern | Browsing lives on Home, Read, Watch, Listen, Collections, Search, and details. |
| Inline correction | Normal media fixes launch from the surface where the user found the issue. |
| Shared editor | Use `MediaEditorLauncherService` and `SharedMediaEditorShell` for Normal, Review, and Batch modes. |
| Review exception | Review Queue is only for blocked, uncertain, low-confidence, or unresolved items. |
| Review attention | Needs Review is permission-gated inside the account menu; there is no standalone navbar notification button. |
| Global activity | Playback, ingestion, AI, enrichment, and durable Engine work share one circular busy/idle indicator. |
| Settings/Admin scope | Settings/Admin is for folders, providers, profiles, roles, ingestion, health, logs, diagnostics, plugins, AI, and review. |
| No removed management workflow | Do not recreate all-in-one management routes, implementation types, navigation labels, or media correction workbenches. |
| Playback controller boundary | Main host adapters read the session controller; reusable controls consume captured snapshots and send commands through the owner sink. Shared transport controls preserve one native owner. Browser mechanics stay behind the Web audio host and `listenPlayback` bridge. |
| Playback utilities and context | Shared bare 22px glyphs keep at least 44px targets. The flush audio dock centers transport and places seek on its top edge. Desktop/tablet Close stays outside utility overflow and saves guarded paused resume before stopping; phone Collapse preserves playback and phone players have no session-stop Close. PlaybackFullPlayer supplies shared phone/popout UI from the captured snapshot; only the phone supplies Collapse. The popout defaults to 420 by 780, fills its window, has no in-player exit, and sends canonical identity navigation to the authorized main owner without reloading audio. Audio popovers/sheets do not resize the page; Ingestion keeps its layout-sidebar lease. Snapshot controls use direct/broadcast sinks against one main playback owner. Speed/Sleep retain central selects with one scroller and owner-captured sleep deadlines/boundaries. One captured Add/Saved bookmark dialog uses the local desktop opener or bounded phone sheet. See `docs/architecture/playback.md` and `docs/reports/player-update-2026-10-03.md`; the current dock/sidebar contract supersedes the October 2 layout. |
| Group tile isolation | Artwork-only series/collection rest and compact top overlay behavior belong in `MediaGroupTile`; do not add group navigation controls to `MediaTile` or change individual-card geometry. |
| Tiled media browse | Media-specific browse result sets wrap vertically and never use horizontally scrolling media rows. |
| Series preference inheritance | `config/ui/library-preferences.json` owns media defaults; SQLite stores only explicit profile-and-series missing-item overrides, and reset deletes the override. |

---

## Platform Health

| Area | Status | Notes |
|---|---|---|
| Home | Live | Discovery and overview surface. |
| Read/Watch/Listen | Live | Shared browse behavior with lane-specific content. |
| Collections | Live/partial | Broader rollups are present; deeper collection creation/editing remains Early Access. |
| Search | Live | Cross-library discovery. |
| Detail pages | Live | Inline correction entry points use the shared editor path. |
| Review Queue | Live | Exception workflow under Settings/Admin. |
| Ingestion dashboard | Live | Uses `GET /ingestion/operations` plus SignalR progress. |
| Local AI/admin areas | Partial | Model management and feature surfaces exist; some workflows remain in progress. |

---

## Product Owner Summary

The Dashboard is no longer a general management workspace. It is a story-first library experience: people browse through Home, Read, Watch, Listen, Collections, Search, and detail pages, and they only go to Review Queue when Tuvima needs human confirmation. Normal corrections happen where the user finds the problem, using one shared editor.

TV detail is show-centered: seasons contain owned episode rows, and each episode
has a show-scoped detail page opened from its still. An unstarted show may keep the
series hero while its facts and action target the first owned episode; after progress,
the hero switches to that episode's still and separated `Sx Ey` synopsis. Short
provider show copy appears under the owned summary in Series Description. Detail
heroes use one left-aligned column for identity, logos, compact facts, actions, and
description. The facts show at most two linked genres on their own non-wrapping line.
Movie synopsis blocks use the movie description.
Continue cards retain the episode target with `Sx Ey` action context. Comic sequence presentation shows issue numbers and owned
counts without treating the current provider run count as a completion target.

## TV episode context and personal status (September 2026)

TV detail uses one profile-aware continuation policy: unstarted/reset shows retain series artwork, active shows use the current or next owned episode still, and explicit episode details stay episode-scoped. Completed owned runs offer rewatch with series artwork. Episodes use short episode synopsis text, never Wikipedia extracts. Missing catalogue entries have no synopsis or artwork and retain the Not in library placeholder. The episode rail scrolls without a four-card cap.

TMDB episode credits are stored separately from show aggregates; full credits support episode, season, and role filtering. Season coverage currently reflects owned episode evidence. More exposes media-appropriate personal completion/reset, history, Undo, Continue visibility, and applicable queue/playlist/collection utilities independently of metadata-edit permission. Revision checks prevent stale progress writes from reversing a reset; bookmarks, genuine consumption history, and music play counts are preserved.

These changes require fresh pre-beta ingestion. Runtime and responsive visual acceptance are pending; see the TV episode consistency proposal for validation status and remaining scope.

## Editor ownership and navigation (September 2026)

The shared modal has one stable parent header and adjacent child selectors. Active borders identify the editing target; dropdowns use the selected owner's art, match the full control width, and omit repeated type/status badges. Details owns the compact source summary. Use Engine `ArtworkSlots` and `ArtworkPresentation` capabilities for shelf artwork, including automatic ordered stacks, custom covers, and restoration. Shelf title/description overrides must refresh the same owner in detail and browse without changing membership or route identity.

Media detail → named Universe → Explore → authorized Universe/entity Edit is the navigation path. Do not reintroduce a Universe shortcut or media/Universe mode switch in a media editor. Explore launches the existing shared entity workspace, retaining its dirty-state guard and permission checks.

Playback presentation and local lyrics timing follow `docs/architecture/playback.md`. The shared scroller, split Continue section, View lane height chain, canonical audio artwork and shared Watch/View video chrome are documented in `docs/reports/remediation-2026-10-04.md`; verification limits must remain explicit.

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
- **Shared controls.** Dashboard controls use the shared `AppSelect`/`AppTypedSelect`, `AppTooltip`, `AppRangeSlider`, `AppProgressBar`, and `AppSpinner`. Raw MudSelect, MudTooltip, MudSlider, and MudProgressLinear belong only inside those primitives.
- **Intrinsic selectors.** Selectors such as the series selector size to the selected label within the available width, then ellipsize with a full-label tooltip.
- **Follow-up.** CSS ownership and line limits are implemented; bundle acceptance remains pending in `docs/reports/css-ownership-2026-10-06.md`. Substantial bundle/priority reduction and broader per-render interop cleanup remain follow-up deliveries.
- **Docs toolchain.** The documentation toolchain requires Material for MkDocs 9.7.7 or later.


### Dashboard CSS ownership maintenance

Component styles follow emitted HTML ownership, with documented contextual boundaries for shared controls, C# renderers, render fragments and portals. DetailPage retains page/stage/tab containers; its presentation owners and SequenceEntryContent own their markup styling. The editor's Details, Artwork, Match, History and Header sections take explicit values and callbacks; the shell retains mutable state, permissions, data access and save/cancel/navigation guards. State-changing EventCallbacks keep the shell as receiver. Settings owns canvas descendant rules; AppSwitchRow owns row layout; ListenNavigationSection owns native rail links while inline playlist and dormant audiobook styles stay with ListenPage.

All isolated CSS has a 2,000-line cap. The CSS audit and StyleOwnershipGuardrailTests enforce explicit ownership, line limits, and transfer-aware per-file/aggregate override budgets. Compare actual generated selectors, DOM scopes, computed styles and paired desktop/phone images. Global popup/vendor bridges remain when ancestry requires them. See `docs/reports/css-ownership-2026-10-06.md` for the acceptance state and measured limits; do not infer bundle reduction from extraction alone. Broader per-render interop work remains a separate follow-up.
