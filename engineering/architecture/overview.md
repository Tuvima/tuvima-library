# How Tuvima is built

> **Status:** Current · **Last updated:** 2026-10-17 · **Related docs:** [Product overview](../product/overview.md) · [Decisions](decisions/README.md) · [Architecture summary (public, deeper)](../../docs/architecture/architecture-summary.md) · [Technical overview (public)](../../docs/architecture/technical-overview.md)

Part 1 is plain language for planning. Part 2 lists the code patterns and paths that work packets refer to.

## Part 1 — In plain language

Tuvima is two programs that run on the person's computer or server:

- **The Engine** does the work in the background. It watches the media folders, reads each file, works out what it is, fetches artwork and details from metadata providers (and Wikidata, the shared knowledge base that connects works across formats), organises files if the person allows it, and remembers everything in a single local data store. It also streams media to players and native apps.
- **The Dashboard** is the website people use in their browser. It asks the Engine for what to show and sends back what people do (play, rate, edit). Native apps (Android, Apple, Roku, TV web) talk to the same Engine.

**How a file becomes a title on screen:** the Engine waits for a new file to finish copying, fingerprints it, reads what's inside (title, author, embedded art), guesses what it is, looks for a match in a retail provider (for example TMDB for films), confirms it against Wikidata, quickly fills in the basics so it can appear, then enriches it in the background (people, characters, universes, more artwork) and finally organises the file. Every step is a durable job, so a restart picks up where it left off. If the Engine isn't confident, the item goes to **Needs Review** instead of being guessed.

**How Tuvima decides which fact wins:** every source's claim is kept. A **Priority Cascade** picks what to show: the person's own locked edits first, then each field's preferred provider, then Wikidata, then the most confident claim.

**Where things are kept:** one SQLite data store per installation (the catalogue, profiles, progress), plus managed artwork and caches in the library's `.data` folder. Configuration is a set of JSON files in `config/` (providers, scoring thresholds, libraries), so a lot of behaviour changes without code changes. Secrets are kept separately and never committed.

**Who can do what:** accounts sign in and hold permissions; profiles hold the personal experience. Administrators need an active admin grant. Apps get their own revocable permissions.

**Optional local AI** (language and speech models) runs on the same machine when the hardware and model files are available.

## Part 2 — Key patterns (for work packets)

### Projects (`MediaEngine.slnx`, all `net10.0`)

| Project | Role |
|---|---|
| `Domain` | Core concepts (Work, Edition, MediaAsset, Collection, Profile), rules, enums, ports. No outside libraries. |
| `Contracts` | Every type sent between Engine and Dashboard (ADR 0004). |
| `Application` | Read-model DTOs and query interfaces. |
| `Storage` | Dapper repositories, `Schema/schema.sql`, `SchemaMigrator`, `DatabaseConnection`, config loading (ADR 0001, 0003). |
| `Intelligence` | Priority Cascade, identity strategies, collection/universe resolution, fuzzy matching. |
| `Processors` | Embedded-metadata readers (EPUB, audio, video, comic, PDF). |
| `Providers` | Config-driven provider adapters and enrichment workers. |
| `Ingestion` | Folder watchers, hashing, dedup, organisation, write-back. |
| `AI` | Local LLM/Whisper lifecycle. |
| `Identity` | Accounts, profiles, authentication. |
| `Admin` | Host-side admin console (password reset, recovery). |
| `Plugins` + `Plugin.*` | Plugin contracts and in-process plugins (segment detection, Fandom lore). |
| `Api` | The Engine: composition root, endpoints, SignalR hubs. |
| `Web` | The Dashboard (Blazor Server). Code map: `src/MediaEngine.Web/CLAUDE.md`. |

Tests: one `tests/MediaEngine.<X>.Tests` project per source project, plus `Performance.Tests`. Native clients: `clients/` (outside the solution).

### Patterns

- **Data access:** SQLite + Dapper, no Entity Framework. Repositories (`*Repository`) open a short-lived connection with `IDatabaseConnection.CreateConnection()` per unit of work. Schema changes are idempotent startup migrations in `SchemaMigrator`; pre-release breaking changes reset the data store. Main tables: `works`, `editions`, `media_assets`, `collections` (with `parent_collection_id` for universes), `metadata_claims`, `canonical_values` / `canonical_value_arrays`, `identity_jobs`, `review_queue`, `persons`, profile/account tables.
- **Engine composition:** `Api/Program.cs` is thin; services register in focused `AddTuvima*` modules under `Api/DependencyInjection/`. Endpoints stay thin and delegate to services and repositories.
- **Background services:** ~50 hosted services (`IHostedService`/`BackgroundService`), mostly in `Api/Services/` — e.g. `IngestionEngine`, `PipelineStageHostedService`, `DeferredEnrichmentService`, `MetadataHarvestingService`, `EncodeQueueService`, `StorageMaintenanceHostedService`, `ProviderHealthMonitorService`. Work is recorded as durable job rows so restarts recover.
- **Real-time:** two SignalR hubs (`Api/Realtime/Intercom.cs`, `ApplicationEventsHub.cs`) push progress and changes to the Dashboard and apps.
- **Dashboard state:** scoped state services registered in `Web/Program.cs` (for example `UniverseStateContainer`, `IngestionLiveDashboardState`, `ShellActivityState`, `ListenPageState`, `PlaybackStateMachine`, `EngineApiFailureState`). All Engine calls go through the typed `EngineApiClient`.
- **UI components:** first-party `App*` components (no MudBlazor; ADR 0002), feature-sliced folders, component-scoped CSS, design tokens in `wwwroot/tuvima.tokens.css`.
- **Editing:** one shared editor (`SharedMediaEditorShell`) opened through `MediaEditorLauncherService.OpenAsync` from detail pages and Needs Review (ADR 0006).
- **Configuration:** `config/*.json` loaded by `ConfigurationDirectoryLoader`; provider behaviour is JSON-driven in `config/providers/`; secrets in gitignored `config/secrets/`.
- **Guardrail tests:** ~50 `*GuardrailTests.cs` suites enforce architecture boundaries, data-store usage, route authorization, wire contracts, UI composition, CSS ownership and breakpoints.

### Build and run

See root `CLAUDE.md` → Commands. CI (`.github/workflows/ci.yml`) adds warnings-as-errors, a format check, coverage and a Release CSS check.
