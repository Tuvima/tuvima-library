---
title: "Technical Overview"
description: "A developer-oriented map of Tuvima Library's runtime apps, data flow, extension points, and verification workflow."
audience: "developer"
category: "architecture"
product_area: "system"
tags:
  - "architecture"
  - "developer"
  - "overview"
status: current
---

# Technical Overview

## In this page

Orient yourself in the Engine, Dashboard, and supporting .NET projects. Follow the runtime data flow, extension points, and verification commands before choosing a subsystem to change.

## Where this lives in the code

- `src/MediaEngine.Api/Program.cs`
- `src/MediaEngine.Api/DependencyInjection`
- `src/MediaEngine.Web/Program.cs`
- `website/package.json`

Tuvima Library is a .NET 10 local-first media system. The product name is Tuvima Library; many projects and namespaces still use `MediaEngine.*`.

## Runtime Apps

| App | Project | Role |
|---|---|---|
| Engine | `src/MediaEngine.Api` | Composition root, API, ingestion, storage wiring, provider integration, AI services, background jobs, health checks, and SignalR |
| Dashboard | `src/MediaEngine.Web` | Blazor Server UI that talks to the Engine over HTTP and SignalR |
| Standalone ingestion worker | `src/MediaEngine.Ingestion` | Worker host for ingestion components; not the primary runtime path today |

Normal local development starts the Engine first, then the Dashboard.

## Main Data Flow

```text
Library folders
  -> File watcher / import scan
  -> Settle, lock check, fingerprint
  -> File processor
  -> Identity/scoring
  -> Stage 1 retail provider stage
  -> Stage 2 Wikidata bridge stage
  -> Quick Hydration
  -> Stage 3 universe enrichment
  -> SQLite, .data/assets, organization, write-back
  -> Display/detail/search APIs
  -> Dashboard
```

The Engine owns business rules and persistence. Managed artwork and headshots are stored under `.data/assets/...` and referenced from SQLite. The Dashboard consumes contracts, typed HTTP clients, and SignalR events. Razor components must not contain direct SQL.

## Repository Shape

| Project | Responsibility |
|---|---|
| `MediaEngine.Domain` | Core aggregates, entities, enums, constants, and interfaces |
| `MediaEngine.Contracts` | DTOs that cross the Engine/Dashboard boundary |
| `MediaEngine.Application` | Read-model DTOs and cross-layer service contracts |
| `MediaEngine.Storage` | SQLite repositories, schema bootstrap, migrations, configuration loading |
| `MediaEngine.Intelligence` | Priority Cascade, scoring, fuzzy matching, identity matching, collection arbitration |
| `MediaEngine.Processors` | File processors and media extraction helpers |
| `MediaEngine.Providers` | Metadata providers, hydration pipeline, reconciliation, image/person/universe enrichment |
| `MediaEngine.AI` | Local model lifecycle, hardware checks, AI features, Llama/Whisper inference |
| `MediaEngine.Identity` | Profiles, roles, and user identity concepts |
| `MediaEngine.Plugins` | Plugin contracts and plugin metadata |
| `MediaEngine.Api` | API endpoints, DI, hosted services, SignalR, Swagger |
| `MediaEngine.Web` | Dashboard UI, typed clients, route helpers, playback/editing/theming services |

## Current User Surfaces

- `/` - Home/discovery
- `/read` - books and comics lane
- `/watch` - movies and TV lane
- `/listen` - music and audiobooks lane
- `/collections` - broader rollups and managed collections
- `/for-me` - the active profile's progress, saved references, reactions, and owned containers
- `/view` - personal mixed local media
- `/search` - cross-library search
- `/details/{entityType}/{id}` - canonical media, group, and person details; Playlists retain their specialized Listen surface
- `/settings` and `/settings/{section}` - user/admin settings
- `/settings/review` - Review Queue
- `/settings/libraries` - structured library configuration and profile Personal Space status
- `/settings/ingestion` - ingestion operations dashboard
- `/settings/developer/options` - temporary development wipe, reingest, and validation harness

Normal media corrections launch `MediaEditorLauncherService` and `SharedMediaEditorShell` from the surface where the issue appears. Review Queue uses the same editor in review mode.

## Extension Points

- Add supported file formats through processors in `MediaEngine.Processors`.
- Add REST/JSON provider behavior through `config/providers/*.json` where config-driven adapters are sufficient.
- Add deeper provider logic in `MediaEngine.Providers` when a provider cannot be expressed as config.
- Add Dashboard Engine calls through `IEngineApiClient` and `EngineApiClient`.
- Add settings destinations through `SettingsNav` and `Components/Settings`.
- Add plugin capabilities through `MediaEngine.Plugins` and plugin projects.

## Verification Workflow

Minimum verification before finalizing code changes:

```powershell
dotnet restore MediaEngine.slnx
dotnet build MediaEngine.slnx --no-restore
dotnet test MediaEngine.slnx --no-build
./scripts/docs/build-docs.ps1
```

Docs-only changes still need `pwsh -File scripts/docs/build-docs.ps1`. The wrapper stages canonical Markdown/MDX from `docs/`, runs Astro Starlight diagnostics and regression checks, and validates the generated site. Use `-InstallDependencies` for a clean dependency installation. Node 24 is required for documentation tooling, independently of the .NET runtime.

```powershell
pwsh -File scripts/docs/build-docs.ps1 -InstallDependencies
pwsh -File scripts/docs/serve-docs.ps1 -Address 127.0.0.1:8000
```

Open `http://127.0.0.1:8000/tuvima_library/`. See the [site tooling guide](../../website/README.md) for direct npm commands, staging, route validation, and deployment. Broader code changes should run the relevant restore/build/test gate.

## Related

- [Developer Setup](../tutorials/dev-setup.md)
- [Project Boundaries](project-boundaries.md)
- [Dashboard UI Architecture](dashboard-ui.md)
- [Engine API Reference](../reference/api-endpoints.md)
