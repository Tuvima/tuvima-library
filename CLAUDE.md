# CLAUDE.md — Tuvima Library Project Memory

> **Who reads this file?**
> Every Claude session working on this repository reads this file automatically before doing anything else.
> It is the single, authoritative source of truth for what Tuvima Library is, how it is built, and how to work on it.
> It bridges the Product Owner's business goals with the technical team's execution.

> **Companion documents.** [`AGENTS.md`](AGENTS.md) is a concise, developer-first code tour — read it for a fast repository map. `docs/architecture/*.md` holds the deep dives per subsystem. This file is a summary that points out to both.

---

## 1. Project Overview

### What is Tuvima Library?

**Tuvima Library** is the product name; **Tuvima** is the company. Code namespaces use `MediaEngine.*` intentionally — decoupled from branding for future resilience. In this repo the two names refer to the same product.

The core philosophy is **Presentation** — the act of bringing something forward and making it whole. Tuvima does not create a library. It presents one. The stories already exist on the hard drive, fragmented across formats and folders; the Library's job is to find them, understand them, unify them, and surface the result as something coherent and beautiful.

Every feature exists in service of that word:
- The **Intelligence Engine** works invisibly so the library is already whole when you look at it.
- The **Universe** is the act of presentation made structural — the book, film, and audiobook of the same story brought forward as one.
- The **Cinematic Dashboard** is the presentation layer made visible.

> **All future sessions must preserve this creative context.** When writing copy, naming features, or explaining the product, the Presentation philosophy is the frame.

### Quality Gates and Regression Rules

- Listen playback stays under `PlaybackSessionController` and the persistent native audio host. Snapshot controls use direct/broadcast command sinks against the same main owner. Shared bare 22px outline utilities keep 44px targets. The flush dock centers transport, places seek inside the dock above the controls and exposes desktop/tablet Close outside utility overflow. Close saves guarded paused resume before stopping; phone Collapse preserves playback and phone players have no session-stop Close. PlaybackFullPlayer supplies shared phone/popout UI from the captured snapshot; only the phone supplies Collapse. The popout defaults to 420 by 780, fills its window, has no in-player exit, and sends canonical identity navigation to the authorized main owner without reloading audio. Audio popovers/sheets do not resize the page; Ingestion retains its layout-sidebar lease and resize behavior. Speed uses the shared slider popover; Sleep uses the central flat select with one scroller. The main owner holds selected sleep minutes, the absolute deadline or verified captured chapter boundary separately from bookmark drafts. One captured Add/Saved bookmark dialog uses its local desktop opener or bounded phone sheet. Follow [playback architecture](docs/architecture/playback.md) and [current verification evidence](docs/reports/player-update-2026-10-03.md), which supersedes the October 2 dock/sidebar layout; do not infer runtime acceptance from source-only checks.
- Do not recreate the old all-in-one management workflow. No new routes, implementation types, navigation labels, docs as current product behavior, or all-in-one media correction workbenches for it.
- Normal detail-page fixes use `MediaEditorLauncherService.OpenAsync` to show `SharedMediaEditorShell` in a modal over the unchanged detail page and URL. Review and Batch reuse the same shell in dialogs. Keep normal Details lean (presentation overrides plus profile-local library preferences), keep provider facts read-only, and put structural parent moves in Matching.
- Single-item editing keeps metadata, local fields, and sorting in Details; it does not expose a separate Options tab. File shows physical-file state only, while History owns identity, metadata, artwork, and ingestion events. A retail rematch synchronously replaces provider-managed artwork and refreshes the detail hero before background Wikidata alignment proceeds.
- Review Queue is the exception workflow for blocked, uncertain, low-confidence, or unresolved items. Settings/Admin is for configuration and operational state, not a normal media correction workspace.
- Use `IDatabaseConnection.CreateConnection()` for normal repository, read-service, endpoint, background-job, and request-path database work. Dispose each short-lived connection with `using`.
- `IDatabaseConnection.Open()` is startup/schema/integrity-only. New uses outside `DatabaseConnection`, Engine startup, or explicitly documented test fixtures should fail guardrail tests.
- Current storage epoch is `guid-blob-v1`: internal SQLite GUIDs are 16-byte BLOBs, external IDs remain TEXT, API JSON still returns GUID strings, and legacy TEXT-GUID databases are reset/reingested rather than migrated in place.
- SQLite connections use WAL plus `synchronous=NORMAL`, `busy_timeout=5000`, memory temp storage, a 16 MiB page cache target, and a 256 MiB mmap cap for local ingestion throughput. The accepted tradeoff is that an OS/power crash can require reingesting the most recent file changes.
- `canonical_values` is scalar-only. Multi-valued metadata belongs in `canonical_value_arrays`; do not reintroduce packed delimiter storage or compatibility readers.
- Avoid silent `catch { }` blocks. Best-effort failures need a justification comment or an explicit guardrail allowlist entry; user-visible failures need logging and degraded/error UI.
- Dashboard service credentials are resolved at request send time, never during typed-client construction. Missing, invalid, or rotated bundles must fail closed with recoverable connection errors; do not send anonymous requests or reuse a stale credential. Keep the service header and any View signature on the same request credential snapshot.
- Domain must stay independent of Web, API, Storage, Providers, Ingestion, Processors, AI, and UI packages. UI should consume view models/contracts/typed clients, not storage implementation models.
- Domain aggregates expose child collections, property bags, and lifecycle state as read-only views. Mutate them through explicit aggregate methods (`Work.LinkToWikidata`, `Collection.SetVisibility`, `Collection.ChangeResolution`, and the named child methods), and keep repository hydration explicit instead of making aggregate internals public again. Persisted aggregate enums convert only through `AggregateStateSerializer`; unknown values fail fast.
- Razor components must not contain direct SQL. API endpoints should move SQL-heavy behavior into repositories/read services when touched.
- When product concepts, navigation, editing flows, database lifecycle, Docker startup, or CI checks change, update README/docs/AGENTS/CLAUDE and relevant `.agent` guidance in the same change.
- Dashboard changes require visual validation at representative desktop sizes, including 1920×1080 for scale-sensitive work. Compare the touched surface before and after, verify relevant responsive or lower-height overrides, and do not treat a successful build as visual proof.
- Before finalizing code changes, run at minimum `dotnet restore MediaEngine.slnx`, `dotnet build MediaEngine.slnx --no-restore`, and `dotnet test MediaEngine.slnx --no-build`. Run docs, Docker, format, and dependency checks when those areas are touched.

### What it does

Tuvima is a unified media intelligence platform that runs entirely on the user's machine — no cloud account, no subscription, no data leaving the home. Point it at a hard drive, and it automatically:

1. **Watches** folders for new files (books, audiobooks, comics, TV shows, movies, music).
2. **Fingerprints** each file so it can be tracked across moves and renames.
3. **Reads embedded metadata** (title, author, year, cover art, series) and applies a Priority Cascade to pick the most trustworthy version of each field.
4. **Groups files into Universes and Series** that link all versions of the same story across media types.
5. **Serves a Blazor dashboard** for browsing, search, playback, and management.
6. **Broadcasts instant updates** to the dashboard via SignalR.

### The Grouping Model — Universes and Series

A **Universe** is a creative world — the books, films, audiobooks, comics, and music that belong together because their metadata says so. A **Series** is a sub-grouping within a Universe — a specific sequence or collection of related works.

Current product rule: a Series is the lane-level shelf shown in Read, Watch, or Listen. A broader Universe/Collection is shown on `/collections` only when a shared series/franchise/universe relationship connects multiple shelves. Watch has separate `TV Shows` and `Series` shelves: TV show identities never share the movie Series row, while Series contains only movie groups dynamically aligned from trusted library metadata. TV shows may appear as show cards with one or more owned episodes, but non-TV/non-music series cards require at least two distinct owned works by default (`lane_group_display.*.minimum_series_items`). TV Timeline groups a show by its canonical premiere year, never the newest owned episode year; Network grouping renders the resolved network logo in the same identity position used for person-led group cards. A single owned movie/book/audiobook remains a normal item tile in its respective lane; audiobooks belong in Listen, not Read. A lane shelf such as owning multiple Matrix films stays in Watch without duplicating itself as a top-level Collection. Multiple formats of one work are variants, not collection triggers. Internal collection IDs may back a lane group, but user-facing surfaces must name and route by the group type: a TV show is a show, a book sequence is a series, and a curated rollup is a collection/list. Book, comic, and movie series cards preserve structural order while selecting at most four representative preview items; broader collections prefer cross-media breadth. Non-TV series and collection containers render through the fixed-size landscape `MediaGroupTile`: rest is artwork-led, with two to four slightly angled, overlapping images using actual portrait, square, or wide metadata. Home and Discover hover preserve the exact artwork and dimensions, add the purple boundary/glow, and reveal only a lightly shaded compact top overlay for type, title, and one status line. Direct grids instead embed an all-caps title, year, and owned count inside the bottom of the card; hover adds the glow and a small open cue without a type pill or cinematic replacement. There is no permanent copy column, child-title list, carousel, or child-level navigation. The retired artwork carousel and rotating collage must not be restored. The entire group card opens the group. TV shows remain on `MediaTile`; cinematic backdrop, logo, facts, and description hover is limited to Home and Discover. Existing item tiles remain on `MediaTile`.

Every card is one semantic link to its detail surface and contains no inline Read, Play, Listen, My List, reaction, remove, or details controls. Vertically wrapping `MediaTileGrid` results preserve the exact resting cover or group artwork on hover and keyboard focus, show a compact title and optional year below the art, and add only a stronger purple glow—no hover identity strip, scrim, background replacement, or popover. Direct browse cards omit redundant media/group pills such as `TV Show` inside the TV Shows route because the active navigation already supplies that context. Their filter areas provide a tile-size slider that changes width while retaining portrait, square, and landscape ratios; Music defaults smaller than the other formats. Movie and TV cinematic landscape expansion is reserved for Home and lane Discover shelves when background artwork supports it; its left-aligned rating, classification, year, and runtime row uses a compact translucent backing that hugs the text, with no inline actions.

Home alone uses `CinematicHeroCarousel`/`CinematicHeroSurface` as a landing treatment; detail heroes continue sharing `DetailHeroContent`, and both use `HeroBackdrop` for foreground cover/album geometry and book decoration. Home uses an approximately 80svh desktop feature (72svh on short screens), with bounded cover-led composition for unknown, portrait or undersized sources; measured landscapes use the aspect-band framing policy. Phones and short landscape screens use content-sized composition. Detail heroes retain `95svh`. Opposite edge arrows have 48px targets; the centered active dash is thicker, wider, and purple. Focus, hover, pause, and reduced motion suspend rotation. Home has no lane submenu. Its rows are Continue Across Media, Recently Added, then populated Watch, Read, Listen, and Collections & Lists; Fresh is retired. Recent filters All/Watch/Read/Listen/View retain the selected scope in `/recently-added?type=` and use added-at keyset paging. View contributes only independently authorized Mine assets, with fresh render-time media grants and native viewer navigation. Untouched TV features use show art; started Home TV keeps the show title, shows `Sx Ey · Episode Title`, and uses the exact owned episode still/synopsis/action with show-art fallback. Watch discovery remains rooted in show art. Shared explicit subject/state/episode contracts drive Engine spotlight selection, Continue, and sequence detail. Only partial long-form media has a purple artwork-edge progress strip; albums, tracks, and View omit completion bars. Resting movies/TV/books/comics use portrait frames, albums and audiobooks use square frames, and only Continue Watching uses landscape stills. Artwork is contained when its native ratio differs from its frame. Home shelves share one artwork height; only Home Watch discovery cards expand horizontally in their row; Watch browse grids, episode lists, and non-Watch cards remain outline-only. Cards retain one semantic detail link and partial long-form artwork-edge progress, without detached percentage captions. Read, Watch, Listen, and Collections retain their compact shared route navigation, viewport-anchored desktop rails, unboxed scoped filters, and existing downstream browsing behavior.

Canonical media, person, series, and standard-collection details use a full-width `95svh` desktop cinematic stage so its lower navigation remains visible while native 16:9 artwork loses as little of the frame as possible. Edge-to-edge landscape art is pinned top-center beneath the translucent global app bar and translucent lower navigation; the bottom is deliberately darkened more heavily than the top so the source's upper composition and hero copy remain legible. Music albums use the shared hero and lower navigation: the cover supplies a blurred atmosphere, a large sharp album/record composition is centered in the right half, identity and actions grow upward from the shared lower-left anchor, a compact Play button and circular Shuffle/My List/Rate/More controls share the action row, and MusicAlbum exposes **Overview** plus **Details**. Overview uses a proportional Tracks/Credits layout, keeps the borderless list with Show missing and no search, resolves identified artists into the same compact square clickable people cards used by movie and TV credits, and conditionally shows every other owned album from the exact canonical primary artist in a full-width **More by** shelf. Oversized provider box-set manifests are scoped to the canonical disc that represents the tagged local album; ordinary multi-disc albums retain their full sequence. Technical and source content belongs only to Details. Audiobooks retain the shared detail structure, chapter-level playback, and specialized player tools, use a substantially larger cover where viewport height permits, and keep Restart in More alongside the other shared actions. Audiobook Chapters remain embedded; Editions does not return, and lists contain neither Play All nor a second My List action. Structural details place their ordered sequence, collection, appearance, or owned-works array below the stage on Overview only, ending with an owned summary and an authoritative-total progress bar when available. Collections use a neutral no-backdrop hero and the same up-to-four representative artwork cluster as collection tiles at a dominant scale; they suppress contributor credits, expose only a functional Shuffle action, and use a compact row for the year range, total items, and applicable Read/Watch/Listen counts. Books and comics share a larger cover-only foreground envelope, as do poster-only movie/TV heroes; true landscape backdrops remain edge-to-edge. People use the same title, role-credit, facts, and synopsis typography as other detail pages, keep only the portrait treatment person-specific, and render one continuous page without tabs. Linked identities reuse cast/credit portrait cards. Explicit collective pseudonyms are the displayed and attributed author identity; linked real identities are not promoted unless explicitly credited on another work. Real multiple-author bylines retain order and use semicolons followed by one space. Person music credits collapse tracks into one album card and show roles/year without track counts. Role and lane filters render only when more than one real option exists. Utility actions use larger icon-left-of-label controls on a left-growing row beneath the primary action. Jump to appears beside the array heading only above ten entries; alternate series/arc selectors align left and Show missing aligns right. Person works use managed portrait covers/posters or square album art, never cinematic landscape backgrounds, and prefer owned-asset canonical titles over parent collection labels. Overview combines attributed description or biography, a purpose-built cast/credits peek, and applicable series or collection context; recommendations use a separate Related tab. Standard collection details use the same resolved membership as the Collections catalog. Listen playlists remain specialized lane-local surfaces.

Matching is automatic: when the Engine discovers that a novel, its film adaptation, and an audiobook share the same author, franchise identifiers, or Wikidata Q-identifier, it groups them into the same Universe. Users browse by creative world, not by file type.

> *Example: The "Dune" Universe might contain:*
> - *The "Dune Novels" Series — Frank Herbert's novels (EPUB)*
> - *The "Dune Films" Series — Denis Villeneuve adaptations (MP4)*
> - *The audiobook narrations (M4B)*
> - *The graphic novels (CBZ)*
>
> *Linked by shared author, franchise QID, and series identifiers — not shared filename.*

A Series is not limited to a numbered sequence. While it often represents a book series or film franchise, it is a flexible virtual container for *any* creative grouping — spin-off works, thematic collections, or cross-media narrative links.

### Terminology — User-Facing vs Internal

| Level | User-facing | Internal code | Example |
|---|---|---|---|
| Entire library | **Library** | Library | Everything you own |
| Franchise grouping | **Universe** | ParentCollection | Dune, Marvel, Tolkien |
| Series / collection | **Series** | Collection | Dune Novels, Dune Films |
| Single title | **Work** | Work | Dune Part One |
| Specific version | **Edition** | Edition | 4K HDR Blu-ray Remux |
| File on disk | **Media Asset** | MediaAsset | the .mkv file |

> **Rule:** Anything the user sees uses the **user-facing name**. Internal code names stay in the domain/engine layer only.

The hierarchy:

```
Library (your entire collection)
  └── Universe (franchise/creative world — e.g. "Dune")
        └── Series (sub-grouping — e.g. "Dune Novels")
              └── Work (one title — e.g. "Dune Part One")
                    └── Edition (one physical version — e.g. "4K HDR Blu-ray Remux")
                          └── Media Asset (one file on disk)
```

Universes are optional. A Series that belongs to no larger franchise sits directly under the Library. Both Universes and Series are resolved at metadata-scoring time by the Intelligence Engine — they have no presence on the filesystem.

### Who is it for?

A single power user who wants complete, private control over a large media collection — without depending on services like Plex, Jellyfin, or any subscription platform.

---

Current presentation rules: the Collections landing route is labeled **Discovery**, while Automatic, Curated, Shelves, and People retain their names. Shelves opens with Read, Watch, and Listen preview lanes and filters by concrete media (Books, Comics, Movies, Albums, and Audiobooks). Same-name duplicate contributor records collapse into one enriched shelf presentation. Discover rows show a small title and optional year for individual items, but group tiles do not repeat text they already embed. TV Shows rows keep episode counts but omit the redundant `TV Show` pill. Completed TV shows show a provider-backed premiere-to-finale year range; TV detail pages always keep the season selector visible, including when only one season is owned.

## 2. Technical Stack

> When speaking to the Product Owner, always use the plain-English column.

### Core Tools

| Plain-English name | Technical name | Purpose |
|---|---|---|
| Programming language | C# / .NET 10 | All Engine and Dashboard code |
| Local database | SQLite | Single file storing the library catalogue |
| Data access layer | Dapper | Maps database rows to C# objects |
| Visual interface components | First-party `App*` primitives | Native HTML, SVG, CSS and JavaScript controls |
| Real-time intercom | SignalR | Pushes live updates to the dashboard |
| Book file reader | VersOne.Epub | Reads EPUB contents |
| Audio/video tagging | TagLibSharp | Reads/writes ID3, MP4, FLAC, Vorbis tags |
| Transcoding / FFmpeg wrapper | Xabe.FFmpeg | Video/audio stream inspection and extraction |
| Image rendering | SkiaSharp | Cover art thumbnails, hero banners |
| Engine API documentation | Swashbuckle | Interactive API menu at `/swagger` |
| Structured logging | Serilog | Rolling log files |
| Resilient HTTP calls | Polly (Microsoft.Extensions.Http.Resilience) | Retries with backoff |
| Wikidata client | Tuvima.Wikidata | Reconciliation, entity fetching, Wikipedia, in-memory graph |
| Cron scheduling | Cronos | Background task timing |
| AI text inference | LLamaSharp | Local LLM with GBNF grammar constraints |
| AI audio inference | Whisper.net | Local speech-to-text and language detection |
| Automated quality checks | xUnit + coverlet | Tests |
| Version control | Git + GitHub | Code history |

See `Directory.Packages.props` for the authoritative dependency list.

### Headless Design — Engine and Dashboard are Separate

The Engine and Dashboard are two independent apps that communicate over HTTP + SignalR.

| Part | Technical project | Role |
|---|---|---|
| The Engine | `MediaEngine.Api` | Intelligence, data, file operations, API surface. Main runtime composition root. |
| The Dashboard | `MediaEngine.Web` | Blazor Server browser interface, client of the Engine |
| Standalone worker | `MediaEngine.Ingestion` | A worker host that can run the ingestion pipeline on its own; not the main runtime path today |

### Source Code Layout

| Folder | What it is | Role |
|---|---|---|
| `src/MediaEngine.Domain` | The Rulebook | Aggregates (`Collection`, `Edition`, `MediaAsset`, `Profile`, `Work`), entities, enums, constants, shared configuration shapes, and inward-facing ports. Pure business language with no external package references. |
| `src/MediaEngine.Contracts` | The Order Form | Serializable DTOs that cross the Engine↔Dashboard boundary — `Details/`, `Display/`, `Paging/`, `Playback/`, `Settings/`. Depends only on Domain. |
| `src/MediaEngine.Application` | The Office Assistant | Read-model DTOs and service-interface contracts for cross-layer queries (`IJourneyReadService`, `IIngestionBatchReadService`, person/asset read services). Depends on Domain + Contracts only. |
| `src/MediaEngine.Storage` | The Filing Clerk | Every concrete repository, SQLite/Dapper execution, embedded schema bootstrap, idempotent startup migrations (M-001…current), `DatabaseConnection` lifecycle facade, and `ConfigurationDirectoryLoader` (multi-file JSON config with `.bak` fallback and hot reload). |
| `src/MediaEngine.Intelligence` | The Analyst | Priority Cascade engine, scoring, fuzzy matching, identity strategies, `IdentityDecisionService`, `CollectionArbiter`, `ParentCollectionResolver`, `IdentityMatcher`. |
| `src/MediaEngine.Processors` | The Scanner | Reads EPUB, audio, video, comic, PDF, and generic files for embedded metadata. Six processors plus extractors. |
| `src/MediaEngine.Providers` | The Research Team | Config-driven and reconciliation adapters, hydration pipeline workers, ~24 enrichment/reconciliation services. The bulk of the runtime payload lives here. |
| `src/MediaEngine.Ingestion` | The Mail Room | Folder watchers, debounce, hashing, dedup, file organization, write-back. `IngestionEngine` is the orchestration entry point. |
| `src/MediaEngine.AI` | The Brain | Local model lifecycle, hardware benchmarking, 15 AI feature services (SmartLabeler, VibeTagger, TldrGenerator, QidDisambiguator, etc.), Llama + Whisper inference. |
| `src/MediaEngine.Identity` | The Front Desk | Accounts, profile grants, authentication, and multi-user rules. Small but important. Builds with `TreatWarningsAsErrors`. |
| `src/MediaEngine.Plugins` | The Plug Socket | Plugin contracts (`ITuvimaPlugin`, `IPluginCapability`, `IPlaybackSegmentDetector`), manifest, models. In-process plugin model. |
| `src/MediaEngine.Plugin.CommercialSkip` | A Plugin | Detects commercial breaks via Comskip (primary) or FFmpeg (fallback). Produces `playback-segment-detector` segments. |
| `src/MediaEngine.Plugin.MediaSegments` | A Plugin | Detects opening credits / closing credits / recap segments. |
| `src/MediaEngine.Api` | The Reception Desk | Composition root: thin `Program.cs`, focused `AddTuvima*` registration modules, endpoint files, awaited startup services, SignalR `Intercom` hub, and health checks. |
| `src/MediaEngine.Web` | The Showroom | Blazor Server dashboard. Uses typed HTTP clients + SignalR; no direct database access. |
| `tests/` | The Quality Inspector | One test project per source project. Strong guardrail suite (architecture boundary, DB connection, accessibility, smoke). Real SQLite temp DBs for Storage tests; hand-written spies, no Moq/NSubstitute. |

---

## 3. Architecture Summary

> **Detail docs** live in `docs/architecture/*.md`. Each subsection below is a short summary — read the linked detail doc when working on a subsystem.

> **Full architecture doc index** (21 files in `docs/architecture/`):
> Subsystem deep-dives — [`ingestion-pipeline.md`](docs/architecture/ingestion-pipeline.md), [`scoring-and-cascade.md`](docs/architecture/scoring-and-cascade.md), [`hydration-and-providers.md`](docs/architecture/hydration-and-providers.md), [`universe-graph.md`](docs/architecture/universe-graph.md), [`ai-integration.md`](docs/architecture/ai-integration.md), [`collections.md`](docs/architecture/collections.md), [`dashboard-ui.md`](docs/architecture/dashboard-ui.md), [`security.md`](docs/architecture/security.md), [`localization.md`](docs/architecture/localization.md), [`target-state.md`](docs/architecture/target-state.md).
> Boundaries & policy — [`api-boundaries.md`](docs/architecture/api-boundaries.md), [`api-boundary-debt.md`](docs/architecture/api-boundary-debt.md), [`project-boundaries.md`](docs/architecture/project-boundaries.md), [`storage-policy-adr.md`](docs/architecture/storage-policy-adr.md), [`storage-policy-review.md`](docs/architecture/storage-policy-review.md), [`configuration.md`](docs/architecture/configuration.md).
> Cross-cutting — [`display-api.md`](docs/architecture/display-api.md), [`js-interop.md`](docs/architecture/js-interop.md), [`openapi-migration.md`](docs/architecture/openapi-migration.md), [`performance-and-large-libraries.md`](docs/architecture/performance-and-large-libraries.md), [`inline-media-editing.md`](docs/architecture/inline-media-editing.md).

### 3.1 — Ingestion Pipeline
**Detail:** [`docs/architecture/ingestion-pipeline.md`](docs/architecture/ingestion-pipeline.md)

Configured Library Folders from `config/libraries.json` tell the Engine where to look and what media to expect through stable `sources` entries. Files go through Settle → Lock Check → Fingerprint → Scan → Identify → Stage 1 retail match → Stage 2 Wikidata bridge resolution → Quick Hydration → Stage 3 universe enrichment → organisation/write-back when confidence allows. No normal runtime path falls back to the old single `WatchDirectory` or `source_path` config shape. Items surface when browse readiness is satisfied (non-placeholder title, resolved media type, settled artwork), and a QID is not required if Stage 1 succeeded but Stage 2 found no QID. The database tracks every status transition; rejected files land under `.data/staging/rejected/`. Work-level deduplication ensures duplicate files create new Editions under an existing Work instead of creating new Works. Config: `config/libraries.json`, `config/disambiguation.json`. Full flow: [`docs/architecture/ingestion-identity-enrichment-pipeline.md`](docs/architecture/ingestion-identity-enrichment-pipeline.md).

Schema is maintained by idempotent startup migrations (`M-001` through current) orchestrated by `DatabaseConnection.RunStartupChecks()` and owned by `SchemaMigrator`. `DatabaseConnection` remains the `IDatabaseConnection` facade: `SqliteConnectionFactory` owns connection PRAGMAs, `SchemaInitializer` owns embedded `Schema/schema.sql` loading and base DDL execution, `SchemaMigrator` owns incremental migrations plus startup seed rows, and `DatabaseIntegrityChecker` owns `PRAGMA integrity_check` / `PRAGMA optimize`. Each migration is guarded so re-running on an already-migrated DB is a no-op.

### 3.2 — Priority Cascade Engine
**Detail:** [`docs/architecture/scoring-and-cascade.md`](docs/architecture/scoring-and-cascade.md)

When sources disagree, a four-tier cascade resolves the dispute: **Tier A** (user locks) → **Tier B** (per-field provider priority) → **Tier C** (Wikidata authority) → **Tier D** (highest confidence). AI improves matching quality (SmartLabeler, QidDisambiguator) but Wikidata remains the canonical authority. All claims are append-only; history is never lost.

The Intelligence project's main public surface:

- `PriorityCascadeEngine` — the four-tier waterfall implementation.
- `IdentityDecisionService` (in `Intelligence/Services`) — accept/review/retry verdicts using a `ConfidenceBand` (Exact / Strong / Provisional / Ambiguous / Insufficient) and a `ReviewRootCause`.
- `IdentityMatcher` — routes a candidate to the right media-type identity strategy.
- `CollectionArbiter` — decides whether a Work belongs in a Collection.
- `ParentCollectionResolver` — resolves parent/child collection hierarchy (Series→Universe).
- `FuzzyMatchingService` — Levenshtein + phonetic name matching.
- `Intelligence/Strategies/` — seven `IMediaTypeIdentityStrategy` implementations: `ExactMatchStrategy`, `BookIdentityStrategy`, `MovieIdentityStrategy`, `AudiobookIdentityStrategy`, `ComicIdentityStrategy`, `MusicIdentityStrategy`, `TvIdentityStrategy`.

Config: `config/scoring.json`, `config/field_priorities.json`.

### 3.3 — Security
**Detail:** [Security architecture](docs/architecture/security.md). Access remains under final integration; see its execution status before claiming delivery.

Accounts own feature/library grants and administrator eligibility; profiles own experience, restrictions, history, and Personal Space identity. Effective administration requires the enabled account and exact active grant with AdminEnabled. Optional grant-specific PIN protection gates administrator surfaces and editor entry. Dashboard navigation/actions consume live Engine authority; no seed-owner or locally stored profile role fallback is permitted. Personal settings remain available when administrator settings are locked.

Applications own registered service permissions. Credentials are hashed, independently revocable, and displayed once; native clients additionally intersect account, profile, consent, device, and exact token authority. TuvimaAuthentication, IRequestAuthorityResolver, and IAuthorizationEvaluator apply live identity and operation policy. Resource scope is checked before catalogue counts/grouping/paging and on artwork, streaming, personal state, and View paths. View private, explicit administrator inspection, Shared Library, and Gallery-share policies remain separate.

Host-bound plugin capabilities differ from external service permissions; unavailable functions remain visibly unavailable. Application events use a durable outbox and a separate scoped hub; Dashboard Intercom retains its wire and requires recipient filtering. Webhooks use current Application permissions, safe DNS-pinned destinations, exact-body HMAC, protected one-time secrets, and bounded retries. Exact event/native and combined acceptance remains tracked in the plan.

Obsolete pre-beta identity state fails fast. Do not restore role-based guest keys, automatic compatibility conversion, localhost administration, or private-space reassignment. Original source media remain protected.

### 3.4 — Dashboard UI
**Detail:** [`docs/architecture/dashboard-ui.md`](docs/architecture/dashboard-ui.md)

Dark-mode-only cinematic design with an ambient gradient background. The Dashboard is a set of dedicated surfaces rather than one command page:

- `/` — **LibraryBrowsePage** (home / discovery landing)
- `/read`, `/read/{Tab}` — ReadPage (books + comics)
- `/watch`, `/watch/{Tab}`, `/watch/movie/{WorkId}`, `/watch/tv/show/{CollectionId}/...`, `/watch/player/{AssetId}` — Watch surfaces
- `/listen`, `/listen/music/...`, `/listen/audiobooks`, `/listen/audiobook/{WorkId}` — Listen surfaces
- `/collections`, `/details/collection/{Id}` — Collections browse + canonical full-width detail
- `/book/{Id}`, `/person/{Id}` — detail pages
- `/universe/{Qid}/explore` — Chronicle Explorer (Cytoscape graph)
- `/search` — global search
- `/settings`, `/settings/{Section}` — Settings shell (review queue lives at `/settings/review`)

Real-time SignalR updates push pipeline progress into every surface. Theming is fixed dark with a purple chrome accent (`#8852FC`, hover `#A46FFF`); EPUB reader highlight colors remain reader-specific.

Canonical book, comic, and movie series containers show their sequence rail directly on Overview. Source numbering stays above each cover, connectors appear behind number nodes only between proven consecutive positions, and the current item uses a stronger purple frame glow without `This book`, `This movie`, or `Up next` labels. Completion remains a separate check state, and `aria-current` preserves accessible current-item context. Missing-item visibility inherits its media default from `config/ui/library-preferences.json`; the database stores only explicit profile-and-series overrides, which can be removed to restore config inheritance.

`MainLayout` exposes My List as the active profile's saved shortlist and delegates account actions to `TopNavAccountMenu`. Needs Review lives inside that permission-aware menu rather than in a standalone bell. `SystemActivityIndicator` uses `ShellActivityState` to combine playback, ingestion, AI download/parsing, enrichment, and durable-operation activity into one circular progress surface, using success green for its icon and ring while work is active and hiding when idle. Sign out is present only for OIDC/hybrid authentication.

### 3.5 — Brand Assets

Three official SVG logo files. **Never replace logo placements with hand-written text.**

| File | Location | Use when… |
|---|---|---|
| `tuvima-logo.svg` | `wwwroot/images/`, `assets/images/` | Full horizontal logo — mark + "TUVIMA" wordmark |
| `tuvima-icon.svg` | `wwwroot/images/`, `wwwroot/favicon.svg`, `assets/images/` | Square icon mark only — favicon, app icon |
| `tuvima-hero.svg` | `assets/images/` | Mark + wordmark + subtitle — README hero and marketing |

All SVGs are designed for dark backgrounds. Source files live outside the repo at `C:\Users\shaya\OneDrive\Documents\Projects\Tuvima\Graphics\`.

### 3.6 — Centralized Data Directory (`.data/`)

All Engine-managed artefacts live under a single `.data/` directory at the library root:

```
{LibraryRoot}/.data/
  database/library.db
  assets/
    artwork/{EntityType}/{EntityId}/{AssetType}/
    people/{personId}/headshot.*
    text-tracks/
    transcripts/
  staging/
    {assetId12}/           ← in-flight assets
    rejected/              ← explicitly rejected files
```

`AssetPathService` (Domain layer, singleton) is the single source of truth for managed asset paths. Artwork variants are indexed through `entity_assets`; person headshots resolve through `Person.LocalHeadshotPath` and `.data/assets/people/{personId}/headshot.*`.

### 3.7 — Hydration Pipeline & Providers
**Detail:** [`docs/architecture/hydration-and-providers.md`](docs/architecture/hydration-and-providers.md)

Operations uses `IngestionBatchActivitySql` to keep a batch active while durable jobs or operations remain, even across restart and stale terminal batch status. Startup requeues leased and unleased intermediate jobs before workers start. Long waits are not terminal failures. Ingestion cards and drawers show added tracks, episodes, and issues without provider totals or per-item progress bars; whole-batch progress includes distinct skipped duplicate inputs.

A durable staged enrichment pipeline runs after ingestion. `identity_jobs` rows (SQLite) replace any in-memory queue. Pipeline workers poll for jobs:

- **`RetailMatchWorker`** - Stage 1. Active configured providers score candidates through `RetailMatchScoringService`; music runs MusicBrainz first for recording/release identity and Apple second for artwork/retail enrichment. Strong candidates auto-accept, ambiguous candidates route to review, and failed provider matches do not trigger broad Wikidata fallback.
- **`WikidataBridgeWorker`** - Stage 2. Uses retail/catalogue bridge IDs (ISBN, ASIN, TMDB ID, MusicBrainz ID, Apple ID, Comic Vine ID, etc.) to resolve a QID. A batch gate holds Stage 2 until Stage 1 finishes for the run.
- **`QuickHydrationWorker`** — Populates canonical values, hero images, collection assignment.
- **Stage 3 enrichment workers** — Fanart.tv, people enrichment, universe graph population, lyrics/subtitles, fictional entities, relationships, and additional images.

`SynchronousIdentityPipelineService` provides an inline implementation for synchronous callers.

Enrichment is modular. `EnrichmentService` dispatches to dedicated workers: `CoverArtWorker`, `PersonEnrichmentWorker`, `PersonImageEnrichmentWorker`, `ChildEntityWorker`, `FictionalEntityWorker`, `DescriptionEnrichmentWorker`, `TextTrackEnrichmentWorker`, plus `ImageEnrichmentService` for Fanart.tv imagery. `PostPipelineService` auto-resolves stale review items when confidence improves. Provider adapters under `Providers/Adapters/` are config-driven (`ConfigDrivenAdapter`, `ReconciliationAdapter`), with only two hand-written REST providers (`LrclibTextTrackProvider`, `OpenSubtitlesTextTrackProvider`); all others are JSON-config-driven in `config/providers/`.

Every retail candidate and Wikidata candidate is persisted (`retail_match_candidates`, `wikidata_bridge_candidates`) so the Review drawer can show full score breakdowns. Provider behaviour is driven by JSON config — adding a REST+JSON provider is a zero-code operation. See [`docs/reference/providers.md`](docs/reference/providers.md).

### 3.8 — Configuration Architecture
**Detail:** [`docs/architecture/configuration.md`](docs/architecture/configuration.md)

All settings live in `config/` as individual JSON files grouped by concern. Provider secrets (API keys) go in `config/secrets/` (gitignored). `ConfigurationDirectoryLoader` performs typed validation, throws `ConfigValidationException` on failure, keeps a `.bak` fallback for each file, and supports bounded hot reload.

| File / folder | What it controls |
|---|---|
| `core.json` | Core paths, rate limits, language preferences, batch gate, maintenance schedules |
| `libraries.json` | Watch folders, media-type hints, organization templates |
| `providers/*.json` | One file per provider; includes language strategy |
| `providers/wikidata_reconciliation.json` | All Wikidata config — reconciliation, edition pivot, data extension, child entity discovery |
| `ai.json` | Local models, feature toggles, cron schedules |
| `pipelines.json` | Ranked Stage 1 provider pipelines per media type (Waterfall / Cascade / Sequential) |
| `scoring.json`, `field_priorities.json` | Metadata priority |
| `ui/palette.json`, `ui/global.json`, `ui/devices/*`, `ui/profiles/*` | UI theming and device profiles |
| `writeback.json`, `writeback-fields.json` | Tag write-back rules |
| `transcoding.json`, `media_types.json`, `narration/phrases.json` | Additional runtime settings |

Before changing C# for behaviour that looks configurable, check `config/` first.

### 3.9 — Universe Graph & Chronicle Engine
**Detail:** [`docs/architecture/universe-graph.md`](docs/architecture/universe-graph.md)

Builds a relationship graph connecting characters, locations, factions, and works. Entities and relationships live in SQLite; `Tuvima.Wikidata.Graph` provides in-memory graph queries. Person infrastructure includes biographical data, social links, pseudonym resolution, and character-performer links. Roles: Actor, Voice Actor, Performer, Artist, Composer, Author, Director, Narrator. The Chronicle Engine adds temporal qualifiers, Lore Delta detection, and era-correct actor detection. The Chronicle Explorer at `/universe/{Qid}/explore` visualises the graph with Cytoscape.js.

### 3.10 — Local AI Intelligence Layer
**Detail:** [`docs/architecture/ai-integration.md`](docs/architecture/ai-integration.md)

AI is a core function, not an add-on. Model roles are small-first: **text_fast** (Qwen3 0.6B-class on-demand), **text_quality** (Qwen3 1.7B-class batch work), **text_scholar** (4B-class hard enrichment), **text_cjk** (CJK/multilingual), and **audio** (Whisper-compatible timestamped transcription + language detection). `config/ai.json` includes `model_catalog` and `role_requirements`; do not promote Gemma 4 12B or any larger model by hardware availability alone. Features span Ingestion (Smart Labeling, Media Type Classification), Alignment (QID Disambiguation, Series Alignment), Enrichment (Vibe Tags, TL;DR, Audio Similarity), Syncing (Immersive Bake, Subtitle Sync), Personalization (Taste Profiling, "Why" Factor), and Discovery (Intent Search). GBNF grammar constraints force valid JSON output. AI improves matching; the Priority Cascade determines canonical values.

### 3.11 — Settings

Settings at `/settings/{Section}` is the Dashboard's operational hub. `SettingsNav` (`src/MediaEngine.Web/Models/ViewDTOs/SettingsNav.cs`) is the canonical route map: two sidebar groups with role-filtered visibility. Sections (each rendered by a `src/MediaEngine.Web/Components/Settings/*Tab.razor`):

| Group | Sections (slug → tab) |
|---|---|
| **User Settings** | Overview → `UserOverviewTab`, `playback` → `PlaybackTab`, `privacy` → `PrivacyHistoryTab` |
| **Admin Settings** | `admin` → `OverviewTab`, `libraries` → `LibrariesTab`, `ingestion` → the Operations `IngestionTasksTab` (automatic live summary on desktop/mobile), `review` → `SettingsReviewQueueTab`, `dev-harness` → `DevHarnessTab`, `providers` → `ProviderPriorityTab`, `ai` → `LocalAiSettingsTab`, `plugins` → `PluginSettingsTab`, `delivery` → `PlaybackDeliverySettingsTab`, `access` → `UsersAccessSettingsTab`, `provider-tester` → `ProviderTesterToolTab`, `enrichment-tester` → `EnrichmentTesterToolTab` |

Additional tab components composed inside those sections: `EncodeSettingsTab`, `OfflineDownloadsTab`, `ModelsTab`, `AiFeaturesTab`, `VibeVocabularyTab`, `AiScheduleTab`, `WikidataConfigTab`, `UniverseSettingsTab`, `SecurityTab`, `UsersTab`, `ApiKeysTab`.

Supporting components used inside tabs: `CuratorsDrawer`, `FolderBrowserDialog`, `SettingsSectionPanel`, `SettingsStatusBadge`, `MediaRail`, `MediaRailCard`, `IngestionLiveDashboard`, `ProviderStageSelector`.

Navigation is URL-driven: `/settings/review` deep-links straight into the review queue, `/settings/ingestion` opens the ingestion admin view, and `/settings/dev-harness` opens the temporary development wipe/reingest harness.

### 3.12 — Review Queue (inside Settings)

The Review Queue is the Engine's safety net for uncertain matches. It lives at `/settings/review` and is rendered by `SettingsReviewQueueTab`. It opens the shared media editor in review mode for blocked or uncertain items.

The queue surfaces items that need human attention: failed retail matches, ambiguous Wikidata candidates, low-confidence matches, missing titles, and items that fell through during enrichment. Review rows can launch the shared editor in review mode, dismiss an item, skip universe matching where supported, or retry/resolve according to existing Engine rules. `PostPipelineService` auto-resolves queue items when a later enrichment pass pushes confidence above threshold.

Browsing lives on Home, Read, Watch, Listen, Collections, Search, and detail pages; review lives inside Settings/Admin. Do not add all-in-one management routes, components, docs, or navigation. Normal media correction opens `SharedMediaEditorShell` from media pages and details through `MediaEditorLauncherService.OpenAsync`; Review uses the same modal editor in review mode.

### 3.13 — Universal Parameterized Collection System
**Detail:** [`docs/architecture/collections.md`](docs/architecture/collections.md)

Every collection is a parameterised query container. Normalised filter predicates are stored as JSON arrays of `{field, op, value}` objects in the `rule_json` column; `CollectionRuleEvaluator` (Storage) translates predicates to SQL. Six collection types as presentation hints:

- **ContentGroup** — engine-owned lane shelves (albums, TV shows, book series) that route through their media-specific surfaces
- **Smart** — auto-generated from library data (by genre, author, director, decade, etc.)
- **System** — per-user, pre-created (Reading List, Watchlist, Favorites, etc.)
- **Mix** — AI-generated per-user (Continue, Heavy Rotation, Discovery Queue, etc.)
- **Playlist** — profile-owned, materialised, and exposed only in Listen
- **Custom** — administrator-curated, library-published, and query-resolved or hand-picked via the collection builder

Resolution is hybrid: query-resolved collections evaluate predicates at display time; materialised collections track membership in `collection_works`. `CollectionAssignmentService` (called by `QuickHydrationWorker`) reads Wikidata series / franchise / universe QIDs and assigns works to a ContentGroup collection via the `collection_id` FK, but lane shelves route by media concept, such as `/watch/tv/show/{CollectionId}` for TV. `collection_placements` maps broader collections/lists to UI locations. The Collections section browses automatic rollups, administrator-curated collections, cross-lane shelves, and canonical people; administrator controls create and manage curated collections.

### 3.14 — Localization & Multi-Language Support

Six language concerns addressed across six phases (all implemented): UI language, metadata display language, content language, provider query language, AI working language, search language. `CoreConfiguration.Language` is a structured `LanguagePreferences` object (Display / Metadata / Additional / AcceptAny). UI localisation uses `IStringLocalizer<SharedStrings>` with .resx files for English, French, German, Spanish. Wikidata searches run in both the file's detected language and the metadata language, deduplicating by QID. FTS5 search uses a `trigram` tokenizer for CJK support. Provider adapters support per-provider `language_strategy` (`source` / `localized` / `both`). See [`docs/guides/language-setup.md`](docs/guides/language-setup.md).

### 3.15 — Target State Features
**Detail:** [`docs/architecture/target-state.md`](docs/architecture/target-state.md)

Not yet implemented: full Authentication & Multi-User (PIN/password, parental controls), a full Transcoding Pipeline (Shadow Transcoder), a deeper Music Domain Model (MusicBrainz, richer `MusicProcessor`), full Interoperability (OPDS 1.2, Audiobookshelf API, webhooks, import wizard, PWA), and advanced Browse & Discovery pages (UniverseDetail, Statistics). Local profiles exist, and the Dashboard persists an active browser profile selection for role-aware navigation.

### 3.16 — Supported Library Types and Policies

| Library Type | Includes |
|---|---|
| **Books** | Ebooks (EPUB, PDF) + Audiobooks (M4B, MP3) |
| **TV** | Episodic television, web series |
| **Movies** | Feature films, short films |
| **Music** | Albums, singles, tracks |
| **Comics** | CBZ, CBR, PDF comics, manga |
| **Personal / Custom** | Home videos, lectures, and unmatched content; local-only or manual metadata bypasses provider and Wikidata ingestion. |
| **Photos** | A separate local photo asset index with timeline, search, thumbnails, favorites, hidden items, albums, duplicate-source tracking, and EXIF camera/GPS details. |

Every library has a stable ID, explicit kind, and metadata policy. Photo assets never enter the catalogue work/edition graph. Face/object/OCR search, maps, memories, sharing, and mobile sync are post-beta work; see `docs/product/beta-roadmap.md`.

---

## 4. Product Owner Communication Rules

> Claude must apply these rules in every single message to the Product Owner. There are no exceptions.

### 4.1 — Mandatory vocabulary

Always use the plain-English term. Never use the technical term in conversation.

| Never say | Always say |
|---|---|
| Backend / Frontend | Engine / Dashboard |
| API / Endpoint | Engine connection point / Action |
| Database / Schema | Data store / Data structure |
| Deploy / Ship | Publish / Release |
| Refactor | Reorganise / Clean up |
| Repository / Commit | Code history / Save point |
| Dependency / Package | Tool / Library |
| DI container / Service registration | App's ingredient list |
| Null reference / Exception | Missing value / Unexpected error |
| Compile / Build | Assemble / Verify |
| Pull request / Branch | Proposed change / Parallel work stream |
| csproj / props file | Project configuration file |
| namespace / class | Code module / Blueprint |
| Collection (internal) | Series |
| ParentCollection (internal) | Universe |

### 4.2 — Always explain the "Why" in business terms

Every technical choice must be justified using one or more of these business goals:

| Goal | Meaning |
|---|---|
| **Maintenance** | Makes the product easier and cheaper to change in the future |
| **Extensibility** | Allows new features or external tools to be added without breaking existing ones |
| **Privacy** | Keeps user data on the user's machine; nothing leaves without explicit action |
| **Reliability** | Reduces the chance of errors or data loss |
| **Performance** | Makes the product faster or more responsive for the user |

### 4.3 — Plan before coding

Before writing any code, Claude must present a plain-English plan using this exact format:

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
 PLAN: [Feature name]
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
 What I'm going to build:
   [1–3 plain-English sentences]

 Why this serves your goals:
   [Cite Maintenance / Extensibility / Privacy / Reliability / Performance]

 What I'll create:
   [plain list of new items]

 What I'll change:
   [plain list of modified items]

 New tools needed:
   [name + license] or "None"

 Trade-offs to know about:
   [any limitations or risks] or "None"

 Plain English Summary:
   [2–4 sentences a non-technical person can read to understand
    what will change and why, written as if explaining to someone
    who has never seen the code]
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

**Do not proceed until the Product Owner says "go ahead" or equivalent.**

**Always ask detailed questions before finalising the plan.** Do not assume intent — probe for specifics: which media types are affected, what the user expects to see on-screen, edge cases, whether existing behaviour should change, and how the feature interacts with other parts of the system. A plan built on assumptions will be rejected; a plan built on answers will be approved.

**Plain English Summary is mandatory** — every plan and every completed task must end with a plain English summary that a non-technical person can understand. This is not optional.

### 4.4 — Error reporting

When the build fails or a quality check breaks, Claude must:
1. Say **what went wrong** in plain English (no error codes as the primary explanation).
2. Say **what will be done** to fix it.
3. Fix it immediately — errors during an approved task do not need a second sign-off.

### 4.5 — Honest about uncertainty

Never guess silently. If Claude is unsure about an approach, it must say so:
> *"I'm not certain which approach is best here — I see two options. Here's the trade-off: [explain]. Which matters more to you?"*

### 4.6 — Plan Output Target: Codex by Default

**Default rule.** When the Product Owner asks Opus for a plan, the final deliverable is a **Codex Handoff Spec**: one self-contained Markdown document the Product Owner pastes directly into ChatGPT/Codex, which then implements it. Opus does not implement the plan itself. Build a Claude-executed plan (§4.7) **only** when the Product Owner explicitly says so (e.g. "plan this for Claude", "you implement it").

**Optimisation target:** accuracy first, then token efficiency. Speed is not a concern — prefer higher reasoning effort, sequential safety, and fewer, larger, fully specified work units over many small agents (every agent re-reads context and burns tokens).

**Flow.**
1. Ask clarifying questions and present the §4.3 plan in chat (plain English, Product Owner vocabulary). Wait for approval.
2. Before writing the spec, Opus reads the actual code and verifies every path, symbol, signature, config key, and current behaviour the spec cites. Nothing is guessed; anything unverifiable is marked `VERIFY FIRST` with the exact check Codex must run.
3. Emit the spec as a single fenced `markdown` block in chat (and, if it is long, also save it to the session scratchpad and send it with SendUserFile). No prose after the block except the §4.3 Plain English Summary.

**Spec writing rules.**
- Address Codex directly in the imperative ("Modify…", "Add…", "Do not…"). Technical vocabulary is required inside the spec; §4.1 applies only to chat with the Product Owner.
- Self-contained: no references to "this conversation", Claude, Opus, Sonnet, or Claude-only tools (Agent, Skill, TodoWrite, subagents by Claude name, MCP tools).
- Codex reads `AGENTS.md` automatically. Do not restate repo-wide rules; quote only the specific `CLAUDE.md` quality gates and architecture rules this change can violate.
- Prefer exact identifiers, file paths with line anchors, signatures, JSON property names, and SQL/config snippets over descriptive prose. No filler, no background essays.
- Every work unit is independently verifiable and states what must not change.

**Model and reasoning routing (put this table at the top of every spec).** Work runs on the GPT-6 family: **Sol** (everyday reasoning) and **Luna** (high-volume, low-cost). **Astra** (top tier) is used only for a final review when necessary — never for orchestration or implementation. Effort levels: none, low, medium, high, xhigh, max.

| Role | Model | Reasoning effort |
|---|---|---|
| Orchestrator — main Codex session: reads the spec, sequences units, assigns agents, integrates, runs verification | Sol | xhigh |
| Architecture / ambiguity escalation — only when a unit hits a design question the spec does not answer (otherwise stop and ask the Product Owner) | Sol | xhigh |
| Implementation agents — complex units (concurrency, playback state, schema/migrations, security/authorization, Engine↔Dashboard contracts, identity pipeline, large cross-file reorganisations) | Sol | high (max only for complex multi-file reorganisations) |
| Implementation agents — standard scoped units | Sol | medium |
| Mechanical units — docs, renames, config/JSON, test fixtures, boilerplate | Luna | max |
| Verification — restore/build/test, format/docs checks, log triage | Luna | max |
| Review — final diff against spec and guardrails, fresh context, never the author agent | Sol | xhigh |
| Escalated final review — **only when necessary**: security/authorization, data-loss or migration risk, storage-epoch or wire-contract changes, or a Sol review that leaves unresolved high-severity doubt | Astra | high |

Each spec states explicitly whether the Astra review applies and why; default is no.

**Required spec structure.**
```
# <Feature> — Codex Handoff Spec
0. Model & reasoning routing   (table above, trimmed to roles this task uses)
1. Goal / Non-goals            (≤3 sentences each)
2. Constraints                 (only task-relevant CLAUDE.md gates + do-not-touch list)
3. Verified current state      (paths, symbols, line anchors, behaviour; VERIFY FIRST items)
4. Work units, in order — each with:
     ID · model/effort · depends-on · files (create/modify) · exact changes
     (signatures, contracts, JSON names, SQL) · tests to add/update ·
     acceptance criteria · must-not-change
5. Parallelism                 (which units may run concurrently; default sequential)
6. Verification                (dotnet restore MediaEngine.slnx; dotnet build MediaEngine.slnx --no-restore;
                                dotnet test MediaEngine.slnx --no-build; dashboard visual checks incl. 1920×1080;
                                docs/Docker/format checks when touched)
7. Documentation updates       (per §5.2 Step 4 and §5.4)
8. Review checklist            (spec conformance, guardrails, regressions, tests meaningful, docs)
9. Stop-and-ask conditions     (anything requiring an architectural or product decision not in the spec)
10. Commit & final report      (save points with specific files, no Claude co-author trailer;
                                end with a plain-English summary for the Product Owner)
```

### 4.7 — Claude-Executed Plans (only on explicit request)

When the Product Owner explicitly asks Claude to implement, use the two-tier model strategy.

**Opus** handles planning, architectural decisions, task decomposition, code review, resolving ambiguity escalated by Sonnet agents, and project documentation (`CLAUDE.md`, `AGENTS.md`, `MEMORY.md`, `.agent/`).

**Sonnet** handles implementation and coding tasks delegated by Opus, file modifications with clear scoped instructions, and build verification (`dotnet build`) after each unit of work.

**Delegation rules:**
1. When a task involves both planning and coding, Opus must break down the work first, then dispatch coding subtasks to Sonnet agents with clear, scoped instructions — including all necessary context (file paths, exact changes, integration points, verification steps).
2. Sonnet agents must not make architectural decisions autonomously. If scope is ambiguous or requirements unclear, escalate to Opus.
3. Independent units should be dispatched in parallel. Dependent units must wait.
4. Each Sonnet agent receives a complete handoff spec. The agent should not need to explore the codebase.
5. If a Sonnet agent encounters a build failure it cannot resolve, it escalates to Opus with full error context.

---

## 5. Compliance & Workflow

### 5.1 — License: AGPLv3

> **This project is licensed under AGPLv3. This cannot be changed without the Product Owner's explicit decision.**

**Every new tool added must have a compatible license.** Claude must check before adding anything.

| License | Compatible? | Notes |
|---|---|---|
| MIT | Safe | Most common |
| Apache 2.0 | Safe | Used by many Microsoft packages |
| BSD 2/3-clause | Safe | Permissive |
| LGPL v2.1 / v3 | Safe | Common for libraries |
| GPL v3 / AGPL v3 | Safe | Same family |
| GPL v2 (no "or later") | Check | May be incompatible — ask first |
| SSPL | Block | Not OSI-approved |
| Commons Clause | Block | Restricts commercial use |
| Proprietary | Block | Incompatible |

**Current approved tools (selected):** Microsoft.Data.Sqlite / SQLitePCLRaw (MIT), NUglify 1.23.3 (BSD-2-Clause, build-time CSS minification), VersOne.Epub (MIT), TagLibSharp (LGPL-2.1), Xabe.FFmpeg (MIT), SkiaSharp (MIT), Swashbuckle (MIT), xUnit 2 (Apache 2.0), coverlet (MIT), Serilog (Apache 2.0), Polly / Microsoft.Extensions.Http.Resilience (MIT), Dapper (Apache 2.0), LLamaSharp (MIT), Whisper.net (MIT), Cronos (MIT), FuzzySharp (MIT), Cytoscape.js (MIT, vendored), Tuvima.Wikidata (MIT), MkDocs (BSD-2-Clause), Material for MkDocs (MIT), tinycss2 (BSD-3-Clause, development-only CSS audit script).

See `Directory.Packages.props` for the authoritative dependency list.

### 5.2 — Mandatory Workflow

**Step 1 — Read before touching anything**
Read `CLAUDE.md`, `AGENTS.md`, `README.md`, and every file relevant to the task. Never assume current state.

**Step 2 — Present the plan and wait for sign-off**
Use the plan format in §4.3 (including Plain English Summary). Do not code until approved.

**Step 3 — Assemble and verify**
```bash
dotnet build
```
Result must be **0 errors, 0 warnings**. After all parallel agents complete:
```bash
taskkill //F //IM dotnet.exe
```

**Step 4 — Update documentation**

| Document | Update when… |
|---|---|
| `README.md` | Feature changes install/config/usage |
| `CLAUDE.md` §3 or `docs/architecture/*.md` | Architecture changes |
| `AGENTS.md` | Repository map / startup / high-level code tour changes |
| `CLAUDE.md` §5.1 | New dependency approved |
| `MEMORY.md` | New architectural decision |
| `docs/**/*.md` | Feature, config, API, schema, or UI changes |

**Step 5 — Commit and push**
```bash
git add <specific files>
git commit -m "Short summary

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"
git push
```

**Never commit:** `tuvima_master.json`, `*.db`, `bin/`, `obj/`, `.vs/`, `.idea/`, `appsettings.*.json` with real keys, `.codex/`, `site/`.

### 5.3 — Cross-Agent Synchronization

Two AI assistants work on this repository:
- **Claude Code** — reads `CLAUDE.md` (this file) as its canonical source of truth.
- **Antigravity (Gemini)** — reads files under `.agent/`.
- Both should be able to start from `AGENTS.md` for a quick code tour.

`CLAUDE.md` is the canonical source. The `.agent/` directory contains supplementary files that must stay in sync. `.agent/SYNC-MAP.md` contains the reverse mapping from `.agent/` files to `CLAUDE.md` sections.

Current work uses the surface-per-concern layout: Home, Read, Watch, Listen, Collections, Search, detail pages, and Settings (with the review queue at `/settings/review`). Do not add all-in-one management routes, components, docs, or navigation.

### 5.4 — Documentation Upkeep (Diátaxis)

> **All project documentation follows the [Diátaxis framework](https://diataxis.fr/).** The `docs/` directory is organised into Tutorials, How-to Guides, Reference, and Explanation. The landing page is `docs/index.md`.

Documentation must be updated when:

| Trigger | What to update |
|---|---|
| New feature added | Relevant Explanation page + Reference entries + How-to Guide if user-facing |
| New config field added | `docs/reference/configuration.md` |
| New API endpoint added | `docs/reference/api-endpoints.md` |
| New database table or column | `docs/reference/database-schema.md` |
| New file format supported | `docs/reference/media-types.md` |
| New provider added | `docs/reference/configuration.md` + `docs/reference/providers.md` + `docs/guides/configuring-providers.md` |
| New processor added | `docs/reference/media-types.md` + `docs/guides/writing-a-processor.md` |
| Terminology change | `docs/reference/glossary.md` |
| Architecture change | `docs/architecture/*.md` + corresponding Explanation page |
| New UI surface or Settings section | relevant Explanation page |

Rules:
1. Documentation updates are part of Step 4 in the Mandatory Workflow (§5.2). They are not optional.
2. User-facing docs use plain English per §4.1 vocabulary rules. Developer docs may use technical terms.
3. Cross-link between related docs: Explanation pages link to Architecture deep-dives; How-to Guides link to Reference pages.
4. Every new Explanation page must be linked from `docs/index.md`.
5. Every published docs page must include front matter with `title`, `summary`, `audience`, `category`, and `product_area`. Use `status: target-state` for future-facing pages.
6. `.codex/` is a gitignored, derived local context layer. Regenerate it with `scripts/docs/refresh-codex-context.ps1`; never edit it by hand.

---

## 6. Structure Reference — Feature-Sliced Dashboard Layout

### 6.0 — Engine/Dashboard wire ownership

`src/MediaEngine.Contracts/` is the sole owner of product-defined types
serialized over non-frozen HTTP and SignalR boundaries. Engine endpoints use
explicit mappers when Domain, Application, provider, or persistence models do
not already match the contract. The Dashboard deserializes Contracts types
directly and may map them into presentation wrappers for formatting, selection,
or component state; it must not maintain a second JSON DTO definition.

The only frozen exception is the exact Dashboard-local SignalR pair
`LoreDeltaDiscoveredEvent` and `UniverseEnrichmentProgressEvent` in
`Services/Integration/IntercomEvents.cs`. Universe HTTP payloads are normal
Contracts types. Do not add types or fields to the exception.

Preserve `[JsonPropertyName]`, casing, defaults, nullability, collection shape,
and editor-required mutability. The canonical contracts intentionally retain
the complete field union from the former API and Dashboard mirrors, including
ingestion `count_unit`, person and collection roles, full AI settings and
`cpu_pressure`, and complete collection, playback/text-track, and plugin
fields. `BoundaryContractGuardrailTests`, `WireContractSnapshotTests`, and
focused shape/round-trip tests enforce this boundary.

All Dashboard code in `src/MediaEngine.Web/` follows the **Feature-Sliced** pattern. Every new piece of UI code must go into the correct slice.

Playback utilities use shared bare 22px glyphs inside at least 44px targets. The flush audio dock centers transport, places seek inside the dock above the controls and exposes desktop/tablet Close outside utility overflow. Close saves guarded paused resume before stopping; phone Collapse preserves playback and phone players have no session-stop Close. PlaybackFullPlayer supplies shared phone/popout UI from the captured snapshot; only the phone supplies Collapse. The popout defaults to 420 by 780, fills its window, has no in-player exit, and sends canonical identity navigation to the authorized main owner without reloading audio. Audio popovers/sheets leave page width and scroll unchanged; Ingestion keeps its layout-sidebar lease and resize behavior. Snapshot-driven controls use direct/broadcast sinks against one main owner. Speed uses the shared slider popover and Sleep the central select; sleep deadlines and verified chapter targets stay owner-captured. Bookmarks retain one captured Add/Saved dialog with its local desktop opener or bounded phone sheet. Follow `docs/architecture/playback.md` and `docs/reports/player-update-2026-10-03.md`; do not restore stacked workspaces or the earlier dock/sidebar layout.

### 6.1 — Services (`src/MediaEngine.Web/Services/`)

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

### 6.2 — Components (`src/MediaEngine.Web/Components/`)

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

### 6.3 — Other top-level folders under `src/MediaEngine.Web/`

| Folder | Purpose |
|---|---|
| `Models/ViewDTOs/` | Dashboard-only presentation shapes. They may wrap or compose Contracts types through explicit mappers, but they never own or mirror Engine↔Dashboard JSON. |
| `Resources/` | `SharedStrings.resx` (+ `.fr` / `.de` / `.es`) plus generated `SharedStrings.cs` |
| `Shared/` | Blazor app-host layout wrappers: `MainLayout`, `NavMenu`, `PopupLayout`, `ReaderLayout`, `_Imports` |
| `wwwroot/` | Static assets: images, CSS, JS (`cytoscape-interop.js`, `epub-reader.js`, `cover-popup.js`, `app.js`) |

### 6.4 — Routed Pages (`Components/Pages/`)

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

TV episodes are children of the show detail page, organized by season. Each owned
episode has a show-scoped detail route opened from its still. Continue surfaces
retain the episode still and playback target, identify it with compact copy such
as `Continue · S5 E1`, and use actions such as `Resume S5 E1`. Episode detail
heroes retain that episode's still, synopsis, and genre. Root show heroes report
owned episodes and target either the in-progress episode or the first owned episode.
An unstarted show may retain the series hero while showing that episode's facts;
after progress, the hero switches to the episode still and `Sx Ey` description.
Short provider show copy appears under the owned summary in a separated Series
Description block. Detail heroes use one left-aligned column for written identity,
logos, compact facts, actions, and description, and show at most two linked genres
on their own non-wrapping line. Movie heroes
use the movie description without a synopsis heading. The watch utility row does
not repeat a Show details action.
| `/settings`, `/settings/{Section}` | `Settings.razor` | Settings shell (review queue at `/settings/review`, ingestion at `/settings/ingestion`, temporary harness at `/settings/dev-harness`) |
| `/not-found`, `/Error` | `NotFound.razor`, `Error.razor` | Error pages |

> **Note.** Earlier drafts referenced `PersonDetail.razor`, `Home.razor`, and `ReviewRedirect.razor`. Those files no longer exist on disk; person detail is served by `UnifiedDetailPage` via `Components/Details/`, and the `/review` redirect has been replaced by direct `/settings/review` navigation.

### 6.5 — Rules for adding new code

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

## 7. Project Contacts

| Role | Detail |
|---|---|
| Product Owner | Shaya |
| Repository | [github.com/shyfaruqi/tuvima-library](https://github.com/shyfaruqi/tuvima-library) |
| License | AGPLv3 |
| Engine base URL (local dev) | `http://localhost:61495` (HTTPS on `61494`) |
| Dashboard URL (local dev) | `http://localhost:5016` (HTTPS on `7062`) |

## Current Collections and linked-identity clarifications

- Collections has one padded, viewport-bounded content scroller beside its anchored rail, with no second document scrollbar.
- Automatic rollups prioritize trusted fictional-universe, franchise, and based-on/adaptation QIDs over lane-local structure, allowing qualifying comics and films such as Batman to share one rollup.
- People counts collapse tracks to albums and episodes to shows.
- Person names use the shared media-detail title family, scale, weight, responsive density, and lower-left identity anchor through the rendered person copy hook. Person dates, locations, and owned-title facts reuse the standard detail metadata row/item typography used by year, runtime, and genre. The portrait may remain centered independently; it must not vertically center the identity column.
- Musical groups populate every canonical Wikidata `has parts` member through `person_group_members` and fully hydrate thin member identities; collective pen names remain alias relationships.

## TV episode context and personal status (September 2026)

TV detail uses one profile-aware continuation policy: unstarted/reset shows retain series artwork, active shows use the current or next owned episode still, and explicit episode details stay episode-scoped. Completed owned runs offer rewatch with series artwork. Episodes use short episode synopsis text, never Wikipedia extracts. Missing catalogue entries have no synopsis or artwork and retain the Not in library placeholder. The episode rail scrolls without a four-card cap.

TMDB episode credits are stored separately from show aggregates; full credits support episode, season, and role filtering. Season coverage currently reflects owned episode evidence. More exposes media-appropriate personal completion/reset, history, Undo, Continue visibility, and applicable queue/playlist/collection utilities independently of metadata-edit permission. Revision checks prevent stale progress writes from reversing a reset; bookmarks, genuine consumption history, and music play counts are preserved.

These changes require fresh pre-beta ingestion. Runtime and responsive visual acceptance are pending; see the TV episode consistency proposal for validation status and remaining scope.


### Libraries settings refresh

The Libraries overview uses aligned full-width rows and URL-backed lane scope. Shell-owned breadcrumbs retain the configured library name on details. One library page contains Folders, Organization, File Handling, and Advanced Settings. Add Library has Choose type, Add folders, Review, with Structured Media and Personal Media branches. The latter saves the shared View root and never creates a catalogued row. Folder validation is repeated at confirmation and commit. Existing-file protection and per-library naming/duplicate policy are enforced in ingestion; populated View roots cannot be relocated through settings.


### Libraries settings and readable View storage

Libraries now includes All/Read/Watch/Listen/View filters, a single View summary row, and a dedicated View settings page. Scope changes filter the mounted list without repeated library loads or filesystem probes. Add folder belongs inside Folders and saves immediately; detachment asks for confirmation and keeps files. Names and custom templates use focused Apply dialogs, while other library settings save individually with conflict detection. There is no page-wide Save. Breadcrumbs remain shell-owned and retain the selected scope.

New Personal Spaces use `View/Profiles/<stable-readable-label>/Timeline` and named managed folders beneath `Folders`. Persisted label reservations survive profile deletion so another profile cannot silently inherit the former folder. Display-name changes do not rename storage. `View/Shared` is the server-owned Shared Library beside Profiles; accepted contributions use verified transfer before managed-original cleanup. Existing obsolete View source state fails closed rather than moving originals or using compatibility paths. View source additions queue reconciliation in its dedicated hosted worker.


## Setup, Operations, and View storage (September 2026)

Setup offers an optional 4–12 digit profile PIN alongside the administrator password, live password validation and reveal controls, and readable recovery codes. Media locations can be explicitly deferred and configured later through Libraries. Reloading setup must render fetched stage data after asynchronous initialization.

Operations and the navbar consume the same Engine batch progress definition, including outstanding identity and enrichment work. Live media selection includes identity-job updates, ranks active/recent groups before limiting, and preserves server order on refresh. Comic covers remain issue-scoped. TV and comic ingestion counts never display a provider denominator. Active batches stay above server-filtered historical batches; every historical action is Open batch. Review counts remain visible throughout Settings. Processing details is removed.

View uses a common library-detail header and Folders, Organization, File Handling, Sharing & Access, and Advanced Settings sections. The configurable View library root contains Shared and Profiles; Shared Library is an authorization scope distinct from the physical base. Logical personal-space/source registration never creates directories. Uploads and accepted transfers create destination folders only at actual write time. Merely visiting View or adding a profile must leave unused storage empty.

The install banner records dismissal in browser/site local storage. Subsequent install events and navigation respect it. System Overview offers an independent manual installation action.

## Shared AI storage (September 2026)

Local app/test builds load versioned native AI dependencies from TUVIMA_AI_RUNTIME_DIR and model weights from TUVIMA_MODELS_DIR. Provision through tools/Install-AiRuntime.ps1; do not add native package copies back to ordinary build outputs. The workstation uses E:\Resources\AI Models (llama/whisper directly beneath it) and sibling AI Runtimes. Windows installer and Docker builds explicitly bundle deployment assets. See docs/guides/shared-ai-storage.md and AGENTS.md for setup, verification and workspace retention. Repos holds only Library and Wikidata; keep at most two temporary worktrees outside Repos and retire them after integration.

## Shared editor and Universe graph rules

- Treat structural media placement, authored-container canonical title/order, and the knowledge graph as separate models. Stage 2 establishes canonical Work identity; bounded Stage 3 enriches grounded entities, qualified graph links, and graph-owned artwork without silently changing authored containers or structural placement.
- The normal media editor uses one stable parent header with adjacent child selectors and a single highlighted editing target. Keep audiobook chapter-title overrides under Chapters. Match information belongs at the top of Details, never in header/selector badges; inherited canonical identity needs no status label. Retail Match owns provider identity plus proposed structural placement; preview previous/target paths before Apply. Do not expose Change Type in the normal editor or Review Queue.
- Universe/entity editing remains content within `SharedMediaEditorShell`, opened from the authorized Edit actions on the Universe Explore surface or its selected entity. Media detail pages link actual accessible Universe relationships; media editors contain no Universe shortcut or mode switch. Keep the Universe header stable and preserve the existing dirty-state guard for category/entity navigation.
- Follow server-readable section capabilities; label/filter Organization Members and Event Participants as their own projections without replacing the full Relationships section. Keep the Universe category rail in one row and use keyboard-accessible, anchored, viewport-bounded category/sibling selector popovers.
- Managed EntityAsset artwork is owned by its Universe/entity target. Preserve work-scoped role/context/anchor/time/spoiler qualifiers and provenance. Do not conflate real-world dates, structural order, in-universe narrative time, and editor History.

## Shelf editing and owner artwork

Structural shelves edit their own parent Work through the shared editor. The Engine declares supported artwork slots and automatic-group presentation; do not duplicate media/scope capability switches in the Dashboard. Shelves preview up to four ordered owned covers, accept a shelf-owned custom cover, and can restore the automatic stack. Title and description overrides remain local presentation choices and must agree across editor, detail, and browse. Group routes and membership resolve by structural owner identity rather than a customized display title. Season selectors use managed SeasonPoster artwork and episodes use EpisodeStill; absent owner artwork remains a placeholder.


Editor Details names the active editing target independently of the stable parent header and shows compact Retail/Canonical match checks. TV matching is exposed only on episode targets, never series or seasons; the inherited canonical record remains visible for context without an editor jump to the series. Match searches retain the current identity until the user explicitly applies or overrides a result. Logo previews preserve the complete image at its natural ratio.

### October 2026 remediation follow-up

Home previews reserve resting shelf geometry before expansion and use a bounded cinematic layout for movies/TV, with artwork and logo only; Home Continue retains episode identity and truthful progress. Home Watch discovery cards expand in-row to a landscape background at the resting cover height, shifting neighboring cards horizontally; Watch pages and episode lists highlight only; non-Watch cards remain outline-only. Shelf arrows have dedicated rails and minimum 44px targets. Continue groups have natural whole-card widths, 32px dividers/gaps and stack below 1280px. Shared book foreground height, perspective, page/spine geometry and proportional shadow live in `HeroBackdrop`; detail stages retain 95svh. Known small Home sources use backdrop framing; unknown dimensions use the conservative fallback.

The shared primary action uses the larger default requested by the product owner for Play, Read, Listen, Resume and Continue: 22rem wide and 4.6rem high, capped to the available phone width. Plain actions use larger text/icons; continued actions show a second percentage line and embedded progress. Adjacent My List, Rate and More are 56px circles; Shuffle remains a circle beside album Play. Restart belongs in More and preserves existing query/fragment state while starting at position zero. Progress includes a visible watched/read/listened percentage and a slim strip inside the primary action. Rate choices are Likes/Dislikes; only a song's Love/heart adds it to Favorites. My List is saved-item state, not a rating. Song menus operate on the selected song and use guarded active-profile/snapshot identity.

Home refreshes are coalesced and profile-bounded: progress is limited to once per minute, library-state changes to once per 15 seconds, new media to a two-second debounce, pause to five seconds, and dismissal/stop to immediate refresh. Transport ticks do not rebuild shelves or Recently Added. Background Recently Added refresh keeps the mounted cards visible and deduplicates paging.

Audio, phone/popout and Watch/View video use `PlaybackSeekRail`: total duration sits at the right end and toggles to remaining time; elapsed time stays visible at the left end and also appears in the focused/hovered/dragged seek bubble. Chapter name/time shares that bubble. Speed uses the bounded 0.5–3.0 slider in 0.05 steps with reset to 1×; stored rate precision is retained until changed. Sleep retains its existing select and authoritative deadline.

Watch/View video use shared bottom chrome, a Back affordance, control hover/focus holds, and one subtitle-positioning owner in `playback-chrome.js`. Active cues sit 12px above the seek rail while controls show and restore authored settings when hidden or detached. Up Next uses owned eligibility and separates Up Next/Episodes/Chapters; no provider-only episode is promoted. Native cue animation is browser-dependent and is not promised.

Lyrics content omits the former version/provider toolbar. Enhanced LRC strips inline timestamps into independently timed words, uses native audio time, and keeps client highlighting separate from server render updates. Only explicit instrumental markers, explicit word-end gaps, known intro/outro boundaries produce three-dot sections; ordinary long line spacing never invents a musical break. Three configurable durations live in `config/ui/playback-client.json`. Unknown duration prefers timed lyrics. Authorized empty state opens Manage lyrics in the existing editor Details tab. LRCLIB attribution appears for LRCLIB tracks.

Editor artwork previews are padded and contained. Open full size opens the authenticated original image in a new tab and removes rendition-size query parameters. Canonical entity artwork lookup avoids an expensive full-detail composition when a cover claim exists. No ten-minute cache was added because invalidation ownership is not yet established.

Retail identity confidence now uses the eight typed per-media matrices in `config/pipelines.json`, synchronized defaults, required/optional missing policies and terminal eligibility caps. Genre is excluded. Nullable `field_scores` is additive in Contracts and complete evidence persists with the candidate. See `docs/architecture/scoring-and-cascade.md`; canonical claim trust and Wikidata scores retain their own owners.


### Product owner visual corrections October 4 2026

The latest product-owner reference supersedes earlier hover and smaller-button guidance. All Continue Watching movies and episodes keep their still and exact resting dimensions; hover/focus reveals a compact in-place identity overlay without JavaScript expansion. Books, comics, music, audiobooks, and other non-Watch media use only a subtle outline and register no preview mouse events. Only Home enables changing previews through the explicit IsHomeSurface parameter. Its movie/show discovery cards widen within the row toward 16:9, shifting neighbors horizontally while retaining the artwork height and showing only the full landscape artwork and logo with a slight highlight. Never mount a floating preview or use a viewport-based 900–1280px expansion. Watch pages, browse grids, detail episode lists, and every other surface only highlight cover art and episode stills. Home Continue keeps its landscape artwork and exact dimensions, revealing title/episode identity plus a larger completion percentage. Continue cover art for books and audio only highlights. Episode overlays show season/episode number and the canonical episode name, with the show title as a persistent caption underneath. Continue projection prefers asset/work episode_title over a generic show title. Media/group cards still have one detail link; group artwork composition remains fixed.

All hero Play/Read/Listen/Continue actions use the same larger 22rem by 4.6rem button, bounded to the available width on phones. A continued action includes the applicable percent watched/read/listened; a plain action centers larger text and icon. Song menus use intrinsic content width, the same shared item-height/typography/padding tokens as detail menus, and the shared circular song action row in full Now Playing. Obsolete overflow-menu geometry overrides are removed. The audio seek rail and end time sit inside the dock, centered together above the controls; desktop reserves 104px and phone 88px plus safe area. Playback ownership and guarded personal-action identities remain unchanged.

Home discovery hover is artwork-and-logo only. Preserve the complete landscape image without synopsis or fact overlays. Freeze artwork and shelf height through opening and closing so only neighbors in the same row shift horizontally. Home Continue scrollers reserve 8px on each side for the first and last card highlight; packed group widths include these gutters.

Continue groups show at least two whole cards when two are available on desktop and tablet. Desktop groups wrap rather than shrinking below two; narrower non-mobile layouts give each group a full row. Mobile may show one card. Preserve natural artwork dimensions and the 8px highlight gutters when allocating group widths.

Movie/TV compact card captions omit the generic subtitle (including series position such as Movie 1 in a series). Retain the title and canonical year; other media retain author/artist subtitles and Continue retains truthful progress context.

Continue Watching, Reading, and Listening share one artwork height (80% of the Home shelf artwork height). Landscape Watch cards retain 16:9 and gain horizontal width. On narrow non-mobile full-width rows, cap the shared height to fit two landscape cards plus rails, gap, and highlight gutters; all media scale together so height parity and the two-card minimum both hold.

The app primary purple is #8852FC, with #A46FFF hover and matching RGB soft/glow tokens. Player surfaces use #0F131E background, #121623 surface, #30364A tracks, #F7F7FA text and #9EA4BC secondary text. Dock timeline shows elapsed time on the left and toggleable total/remaining time on the right. Music dock exposes the existing song rating control beside Favorite/More; Likes remain distinct from Favorites. Lyrics uses a quotation speech-bubble glyph. An open rating control and its hero action row rise above sibling controls.

Sign-in rejects stale, missing, mismatched, or unreadable antiforgery tokens with HTTP 400 and a freshly tokenized sign-in form. The expired-form notice requests a new submission; rejected credentials never reach the Engine. A local return destination is preserved, while credential fields are cleared. Other security actions retain their existing validation.

Playback song rating, Favorite and More use the same outline 22px glyph sizing and circular 44px target. Song overflow omits Like/Dislike when the direct Rate control is rendered; surfaces without a direct Rate control retain those menu actions. Detail rating circles keep their existing appearance.

Rate hover, focus, and selected-choice highlights are circular and contained within their 44px targets and padded choice pill. Playback Rate keeps the shared circular border at rest; its open/hover highlight is circular, matching the choice buttons.

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
- **Follow-up.** Shared controls and Release CSS minification now use first-party ownership. Bundle acceptance still requires measured Release assets and paired visual evidence; broader per-render interop optimization remains separate.
- **Docs toolchain.** The documentation toolchain requires Material for MkDocs 9.7.7 or later.

### Icon rows and player typography (October 5 2026)

Validate icons together whenever they share a row or area. Peer actions must use the same icon family, view box, stroke treatment, rendered glyph size, target size, shape, border, and alignment. Do not mix filled Material and outline playback icons within one peer row. Selected states may change color; intentionally dominant transport Play/Pause may have a larger target and must be documented as a separate role. Icon-only controls require a tooltip and accessible name, plus keyboard focus and selected/expanded semantics.

Favorite, Rate, and More on the dock, phone full player, desktop full player and popout use `PlaybackSongActions`: 44px circular targets, 1px borders and 22px `PlaybackUtilityGlyph` icons in a 24-unit view box with a 1.5-unit stroke. Detail hero actions keep their own size contract. Regression checks cover the shared hosts, glyph types, callbacks and accessible labels. Run the read-only `tools/validate-player-icon-rows.js` in the rendered browser at desktop, phone, short-phone and popout sizes; require equal dimensions and centered glyphs within 1px. Also inspect resting, focus, rated/favorited and open-menu states. Do not infer size parity from source alone.

Lyrics use the shared UI sans-serif family, bold container-sized text and a larger bright active line. Adjacent lines are dimmed without readability-damaging blur. Lyrics has no visible heading or Synced badge; timed highlighting conveys synchronization. Queue/history has no Continue Playing or source header: only Up Next and History tabs. Preserve accessible panel names, meaningful error/empty states, LRCLIB attribution, timing, scrolling and seek behavior. Lyrics and Queue mode triggers remain icon-only with tooltips.

The shared seek rail shows elapsed time on the left and total/remaining time on the right on every player surface, including phone, popout and video. The right label retains its total/remaining toggle.


### Dashboard CSS ownership maintenance

Component styles follow emitted HTML ownership, with documented contextual boundaries for shared controls, C# renderers, render fragments and portals. DetailPage retains page/stage/tab containers; its presentation owners and SequenceEntryContent own their markup styling. The editor's Details, Artwork, Match, History and Header sections take explicit values and callbacks; the shell retains mutable state, permissions, data access and save/cancel/navigation guards. State-changing EventCallbacks keep the shell as receiver. Settings owns canvas descendant rules; AppSwitchRow owns row layout; ListenNavigationSection owns native rail links while inline playlist and dormant audiobook styles stay with ListenPage.

All isolated CSS has a 2,000-line cap. The CSS audit and StyleOwnershipGuardrailTests enforce explicit ownership, line limits, and transfer-aware per-file/aggregate override budgets. Compare actual generated selectors, DOM scopes, computed styles and paired desktop/phone images. Global popup/vendor bridges remain when ancestry requires them. See `docs/reports/css-ownership-2026-10-06.md` for the acceptance state and measured limits; do not infer bundle reduction from extraction alone. Native controls and Release minification now have first-party ownership; verify their current evidence separately from that historical report. Broader per-render interop work remains a separate follow-up.

### Native Dashboard controls and release styling

The Dashboard uses first-party `Components/Shared/App*` primitives and scoped `Services/Ui/` services. `AppPopoverHost`, `AppDialogHost`, `AppToastHost` and `AppThemeProvider` are mounted by Main, Popup, Reader and SetupWizard layouts. Dialog and toast callers use `IAppDialogService` and `IAppToastService`; popup coordination belongs to `AppPopoverService`. Native dialogs preserve the editor's unchanged URL, typed results and unsaved-change interception. Toast actions retain asynchronous Undo behavior. Popup ownership follows the active modal or fullscreen container so a select remains usable inside an editor or player.

`tuvima.tokens.css` owns canonical tokens; first-party theme aliases, `native-utilities.css`, `native-structure.css`, `native-fields.css`, global `app.css` and component-isolated styles own presentation. `AppMaterialIcon` renders the pinned `AppMaterialIcons` SVG catalog; regenerate it with `python scripts/icons/generate-material-icons.py`, retaining `THIRD-PARTY-NOTICES.md`. Material and playback icon families keep their established row contracts.

Release builds use build-only NUglify through `src/MediaEngine.Web/Build/DashboardCss.targets`. Global CSS is copied/minified into `obj/`; scoped bundles are minified after `BundleScopedCssFiles`, before static-asset fingerprinting and gzip/brotli compression. Source CSS stays editable and Debug stays unminified. Minifier errors fail the build. Verify actual Release assets and compressed content; a successful build or smaller source file alone does not establish visual parity or download savings.

Dashboard CI publishes Release and runs scripts/build/verify-dashboard-css.mjs to verify minification, gzip/brotli content, fingerprints and the finalized scoped bundle ceiling. Docker copies src/MediaEngine.Web/Build/ before restore; DockerfileGuardrailTests protects explicit project imports. ComponentParameterGuardrailTests checks direct Razor component parameters, including captured HTML attributes, without accepting arbitrary retired parameter names.
