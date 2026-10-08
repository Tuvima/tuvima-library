---
title: "Architecture Summary"
summary: "One-page summary of every Engine and Dashboard subsystem, with links to the deep-dive architecture pages."
audience: "developer"
category: "explanation"
product_area: "architecture"
tags:
  - "architecture"
  - "overview"
---

# Architecture Summary

This page holds the per-subsystem summaries that previously lived in the root `CLAUDE.md`. Each section links to its deep-dive page.

> **Detail docs** live in `docs/architecture/*.md`. Each subsection below is a short summary — read the linked detail doc when working on a subsystem.

> **Full architecture doc index** (21 files in `docs/architecture/`):
> Subsystem deep-dives — [`ingestion-pipeline.md`](ingestion-pipeline.md), [`scoring-and-cascade.md`](scoring-and-cascade.md), [`hydration-and-providers.md`](hydration-and-providers.md), [`universe-graph.md`](universe-graph.md), [`ai-integration.md`](ai-integration.md), [`collections.md`](collections.md), [`dashboard-ui.md`](dashboard-ui.md), [`security.md`](security.md), [`localization.md`](localization.md), [`target-state.md`](target-state.md).
> Boundaries & policy — [`api-boundaries.md`](api-boundaries.md), [`api-boundary-debt.md`](api-boundary-debt.md), [`project-boundaries.md`](project-boundaries.md), [`storage-policy-adr.md`](storage-policy-adr.md), [`storage-policy-review.md`](storage-policy-review.md), [`configuration.md`](configuration.md).
> Cross-cutting — [`display-api.md`](display-api.md), [`js-interop.md`](js-interop.md), [`openapi-migration.md`](openapi-migration.md), [`performance-and-large-libraries.md`](performance-and-large-libraries.md), [`inline-media-editing.md`](inline-media-editing.md).

## Ingestion Pipeline
**Detail:** [`docs/architecture/ingestion-pipeline.md`](ingestion-pipeline.md)

Configured Library Folders from `config/libraries.json` tell the Engine where to look and what media to expect through stable `sources` entries. Files go through Settle → Lock Check → Fingerprint → Scan → Identify → Stage 1 retail match → Stage 2 Wikidata bridge resolution → Quick Hydration → Stage 3 universe enrichment → organisation/write-back when confidence allows. No normal runtime path falls back to the old single `WatchDirectory` or `source_path` config shape. Items surface when browse readiness is satisfied (non-placeholder title, resolved media type, settled artwork), and a QID is not required if Stage 1 succeeded but Stage 2 found no QID. The database tracks every status transition; rejected files land under `.data/staging/rejected/`. Work-level deduplication ensures duplicate files create new Editions under an existing Work instead of creating new Works. Config: `config/libraries.json`, `config/disambiguation.json`. Full flow: [`docs/architecture/ingestion-identity-enrichment-pipeline.md`](ingestion-identity-enrichment-pipeline.md).

Schema is maintained by idempotent startup migrations (`M-001` through current) orchestrated by `DatabaseConnection.RunStartupChecks()` and owned by `SchemaMigrator`. `DatabaseConnection` remains the `IDatabaseConnection` facade: `SqliteConnectionFactory` owns connection PRAGMAs, `SchemaInitializer` owns embedded `Schema/schema.sql` loading and base DDL execution, `SchemaMigrator` owns incremental migrations plus startup seed rows, and `DatabaseIntegrityChecker` owns `PRAGMA integrity_check` / `PRAGMA optimize`. Each migration is guarded so re-running on an already-migrated DB is a no-op.

## Priority Cascade Engine
**Detail:** [`docs/architecture/scoring-and-cascade.md`](scoring-and-cascade.md)

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

## Security
**Detail:** [Security architecture](security.md). Access remains under final integration; see its execution status before claiming delivery.

Accounts own feature/library grants and administrator eligibility; profiles own experience, restrictions, history, and Personal Space identity. Effective administration requires the enabled account and exact active grant with AdminEnabled. Optional grant-specific PIN protection gates administrator surfaces and editor entry. Dashboard navigation/actions consume live Engine authority; no seed-owner or locally stored profile role fallback is permitted. Personal settings remain available when administrator settings are locked.

Applications own registered service permissions. Credentials are hashed, independently revocable, and displayed once; native clients additionally intersect account, profile, consent, device, and exact token authority. TuvimaAuthentication, IRequestAuthorityResolver, and IAuthorizationEvaluator apply live identity and operation policy. Resource scope is checked before catalogue counts/grouping/paging and on artwork, streaming, personal state, and View paths. View private, explicit administrator inspection, Shared Library, and Gallery-share policies remain separate.

Host-bound plugin capabilities differ from external service permissions; unavailable functions remain visibly unavailable. Application events use a durable outbox and a separate scoped hub; Dashboard Intercom retains its wire and requires recipient filtering. Webhooks use current Application permissions, safe DNS-pinned destinations, exact-body HMAC, protected one-time secrets, and bounded retries. Exact event/native and combined acceptance remains tracked in the plan.

Obsolete pre-beta identity state fails fast. Do not restore role-based guest keys, automatic compatibility conversion, localhost administration, or private-space reassignment. Original source media remain protected.

## Dashboard UI
**Detail:** [`docs/architecture/dashboard-ui.md`](dashboard-ui.md)

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

## Brand Assets

Three official SVG logo files. **Never replace logo placements with hand-written text.**

| File | Location | Use when… |
|---|---|---|
| `tuvima-logo.svg` | `wwwroot/images/`, `assets/images/` | Full horizontal logo — mark + "TUVIMA" wordmark |
| `tuvima-icon.svg` | `wwwroot/images/`, `wwwroot/favicon.svg`, `assets/images/` | Square icon mark only — favicon, app icon |
| `tuvima-hero.svg` | `assets/images/` | Mark + wordmark + subtitle — README hero and marketing |

All SVGs are designed for dark backgrounds. Source files live outside the repo at `C:\Users\shaya\OneDrive\Documents\Projects\Tuvima\Graphics\`.

## Centralized Data Directory (`.data/`)

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

## Hydration Pipeline & Providers
**Detail:** [`docs/architecture/hydration-and-providers.md`](hydration-and-providers.md)

Operations uses `IngestionBatchActivitySql` to keep a batch active while durable jobs or operations remain, even across restart and stale terminal batch status. Startup requeues leased and unleased intermediate jobs before workers start. Long waits are not terminal failures. Ingestion cards and drawers show added tracks, episodes, and issues without provider totals or per-item progress bars; whole-batch progress includes distinct skipped duplicate inputs.

A durable staged enrichment pipeline runs after ingestion. `identity_jobs` rows (SQLite) replace any in-memory queue. Pipeline workers poll for jobs:

- **`RetailMatchWorker`** - Stage 1. Active configured providers score candidates through `RetailMatchScoringService`; music runs MusicBrainz first for recording/release identity and Apple second for artwork/retail enrichment. Strong candidates auto-accept, ambiguous candidates route to review, and failed provider matches do not trigger broad Wikidata fallback.
- **`WikidataBridgeWorker`** - Stage 2. Uses retail/catalogue bridge IDs (ISBN, ASIN, TMDB ID, MusicBrainz ID, Apple ID, Comic Vine ID, etc.) to resolve a QID. A batch gate holds Stage 2 until Stage 1 finishes for the run.
- **`QuickHydrationWorker`** — Populates canonical values, hero images, collection assignment.
- **Stage 3 enrichment workers** — Fanart.tv, people enrichment, universe graph population, lyrics/subtitles, fictional entities, relationships, and additional images.

`SynchronousIdentityPipelineService` provides an inline implementation for synchronous callers.

Enrichment is modular. `EnrichmentService` dispatches to dedicated workers: `CoverArtWorker`, `PersonEnrichmentWorker`, `PersonImageEnrichmentWorker`, `ChildEntityWorker`, `FictionalEntityWorker`, `DescriptionEnrichmentWorker`, `TextTrackEnrichmentWorker`, plus `ImageEnrichmentService` for Fanart.tv imagery. `PostPipelineService` auto-resolves stale review items when confidence improves. Provider adapters under `Providers/Adapters/` are config-driven (`ConfigDrivenAdapter`, `ReconciliationAdapter`), with only two hand-written REST providers (`LrclibTextTrackProvider`, `OpenSubtitlesTextTrackProvider`); all others are JSON-config-driven in `config/providers/`.

Every retail candidate and Wikidata candidate is persisted (`retail_match_candidates`, `wikidata_bridge_candidates`) so the Review drawer can show full score breakdowns. Provider behaviour is driven by JSON config — adding a REST+JSON provider is a zero-code operation. See [`docs/reference/providers.md`](../reference/providers.md).

## Configuration Architecture
**Detail:** [`docs/architecture/configuration.md`](configuration.md)

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

## Universe Graph & Chronicle Engine
**Detail:** [`docs/architecture/universe-graph.md`](universe-graph.md)

Builds a relationship graph connecting characters, locations, factions, and works. Entities and relationships live in SQLite; `Tuvima.Wikidata.Graph` provides in-memory graph queries. Person infrastructure includes biographical data, social links, pseudonym resolution, and character-performer links. Roles: Actor, Voice Actor, Performer, Artist, Composer, Author, Director, Narrator. The Chronicle Engine adds temporal qualifiers, Lore Delta detection, and era-correct actor detection. The Chronicle Explorer at `/universe/{Qid}/explore` visualises the graph with Cytoscape.js.

## Local AI Intelligence Layer
**Detail:** [`docs/architecture/ai-integration.md`](ai-integration.md)

AI is a core function, not an add-on. Model roles are small-first: **text_fast** (Qwen3 0.6B-class on-demand), **text_quality** (Qwen3 1.7B-class batch work), **text_scholar** (4B-class hard enrichment), **text_cjk** (CJK/multilingual), and **audio** (Whisper-compatible timestamped transcription + language detection). `config/ai.json` includes `model_catalog` and `role_requirements`; do not promote Gemma 4 12B or any larger model by hardware availability alone. Features span Ingestion (Smart Labeling, Media Type Classification), Alignment (QID Disambiguation, Series Alignment), Enrichment (Vibe Tags, TL;DR, Audio Similarity), Syncing (Immersive Bake, Subtitle Sync), Personalization (Taste Profiling, "Why" Factor), and Discovery (Intent Search). GBNF grammar constraints force valid JSON output. AI improves matching; the Priority Cascade determines canonical values.

## Settings

Settings at `/settings/{Section}` is the Dashboard's operational hub. `SettingsNav` (`src/MediaEngine.Web/Models/ViewDTOs/SettingsNav.cs`) is the canonical route map: two sidebar groups with role-filtered visibility. Sections (each rendered by a `src/MediaEngine.Web/Components/Settings/*Tab.razor`):

| Group | Sections (slug → tab) |
|---|---|
| **User Settings** | Overview → `UserOverviewTab`, `playback` → `PlaybackTab`, `privacy` → `PrivacyHistoryTab` |
| **Admin Settings** | `admin` → `OverviewTab`, `libraries` → `LibrariesTab`, `ingestion` → the Operations `IngestionTasksTab` (automatic live summary on desktop/mobile), `review` → `SettingsReviewQueueTab`, `dev-harness` → `DevHarnessTab`, `providers` → `ProviderPriorityTab`, `ai` → `LocalAiSettingsTab`, `plugins` → `PluginSettingsTab`, `delivery` → `PlaybackDeliverySettingsTab`, `access` → `UsersAccessSettingsTab`, `provider-tester` → `ProviderTesterToolTab`, `enrichment-tester` → `EnrichmentTesterToolTab` |

Additional tab components composed inside those sections: `EncodeSettingsTab`, `OfflineDownloadsTab`, `ModelsTab`, `AiFeaturesTab`, `VibeVocabularyTab`, `AiScheduleTab`, `WikidataConfigTab`, `UniverseSettingsTab`, `SecurityTab`, `UsersTab`, `ApiKeysTab`.

Supporting components used inside tabs: `CuratorsDrawer`, `FolderBrowserDialog`, `SettingsSectionPanel`, `SettingsStatusBadge`, `MediaRail`, `MediaRailCard`, `IngestionLiveDashboard`, `ProviderStageSelector`.

Navigation is URL-driven: `/settings/review` deep-links straight into the review queue, `/settings/ingestion` opens the ingestion admin view, and `/settings/dev-harness` opens the temporary development wipe/reingest harness.

## Review Queue (inside Settings)

The Review Queue is the Engine's safety net for uncertain matches. It lives at `/settings/review` and is rendered by `SettingsReviewQueueTab`. It opens the shared media editor in review mode for blocked or uncertain items.

The queue surfaces items that need human attention: failed retail matches, ambiguous Wikidata candidates, low-confidence matches, missing titles, and items that fell through during enrichment. Review rows can launch the shared editor in review mode, dismiss an item, skip universe matching where supported, or retry/resolve according to existing Engine rules. `PostPipelineService` auto-resolves queue items when a later enrichment pass pushes confidence above threshold.

Browsing lives on Home, Read, Watch, Listen, Collections, Search, and detail pages; review lives inside Settings/Admin. Do not add all-in-one management routes, components, docs, or navigation. Normal media correction opens `SharedMediaEditorShell` from media pages and details through `MediaEditorLauncherService.OpenAsync`; Review uses the same modal editor in review mode.

## Universal Parameterized Collection System
**Detail:** [`docs/architecture/collections.md`](collections.md)

Every collection is a parameterised query container. Normalised filter predicates are stored as JSON arrays of `{field, op, value}` objects in the `rule_json` column; `CollectionRuleEvaluator` (Storage) translates predicates to SQL. Six collection types as presentation hints:

- **ContentGroup** — engine-owned lane shelves (albums, TV shows, book series) that route through their media-specific surfaces
- **Smart** — auto-generated from library data (by genre, author, director, decade, etc.)
- **System** — per-user, pre-created (Reading List, Watchlist, Favorites, etc.)
- **Mix** — AI-generated per-user (Continue, Heavy Rotation, Discovery Queue, etc.)
- **Playlist** — profile-owned, materialised, and exposed only in Listen
- **Custom** — administrator-curated, library-published, and query-resolved or hand-picked via the collection builder

Resolution is hybrid: query-resolved collections evaluate predicates at display time; materialised collections track membership in `collection_works`. `CollectionAssignmentService` (called by `QuickHydrationWorker`) reads Wikidata series / franchise / universe QIDs and assigns works to a ContentGroup collection via the `collection_id` FK, but lane shelves route by media concept, such as `/watch/tv/show/{CollectionId}` for TV. `collection_placements` maps broader collections/lists to UI locations. The Collections section browses automatic rollups, administrator-curated collections, cross-lane shelves, and canonical people; administrator controls create and manage curated collections.

## Localization & Multi-Language Support

Six language concerns addressed across six phases (all implemented): UI language, metadata display language, content language, provider query language, AI working language, search language. `CoreConfiguration.Language` is a structured `LanguagePreferences` object (Display / Metadata / Additional / AcceptAny). UI localisation uses `IStringLocalizer<SharedStrings>` with .resx files for English, French, German, Spanish. Wikidata searches run in both the file's detected language and the metadata language, deduplicating by QID. FTS5 search uses a `trigram` tokenizer for CJK support. Provider adapters support per-provider `language_strategy` (`source` / `localized` / `both`). See [`docs/guides/language-setup.md`](../guides/language-setup.md).

## Target State Features
**Detail:** [`docs/architecture/target-state.md`](target-state.md)

Not yet implemented: full Authentication & Multi-User (PIN/password, parental controls), a full Transcoding Pipeline (Shadow Transcoder), a deeper Music Domain Model (MusicBrainz, richer `MusicProcessor`), full Interoperability (OPDS 1.2, Audiobookshelf API, webhooks, import wizard, PWA), and advanced Browse & Discovery pages (UniverseDetail, Statistics). Local profiles exist, and the Dashboard persists an active browser profile selection for role-aware navigation.

## Supported Library Types and Policies

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
