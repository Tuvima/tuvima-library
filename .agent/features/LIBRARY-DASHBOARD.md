# Feature: Library Dashboard

> **Mirrors:** `CLAUDE.md` Section 3.11 and Section 6. Keep both in sync per `.agent/SYNC-MAP.md`.

> Last audited: 2026-07-23 | Auditor: Codex

---

## Product Model

The Dashboard is the user-facing surface for a local-first story library. It is organized by user intent rather than by a separate media management workspace:

- **Home** (`/`) gives a discovery overview and recent library activity.
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

The five main landings reuse the detail-derived cinematic hero. `DetailHero` and `CinematicHeroCarousel` both compose `DetailHeroContent`, sharing logo/title scale, facts, primary action, progress, and full-paragraph synopsis. The carousel adds a top-right Featured Content or lane-specific Continue context plus rotation controls. Lane filters and detail tabs both use `SurfaceNavigationBar`. Group-opening actions do not use play icons. Watch landing TV slides always use root show artwork and fall back to the settled placeholder when it is absent; episode stills stay on episode-specific surfaces.

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
| Playback controller boundary | Listen playback UI reads the session controller and uses shared transport controls; browser mechanics stay behind the Web audio host and `listenPlayback` bridge. |
| Playback utilities and context | Shared bare 22px utility glyphs keep at least 44px targets; transport stays centered, tools align right and the seek rail grows with the available width. One layout-owned context sidebar replaces stacked workspaces. Speed and Sleep use central dropdowns with one scroll owner. The main playback owner verifies and captures sleep targets. One Add/Saved bookmark dialog preserves its captured position, anchors at the initiating desktop control and becomes a phone bottom sheet. Ownership and accepted checks are recorded in `docs/architecture/playback.md` and `docs/reports/audiobook-refinement-2026-10-02.md`. |
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
