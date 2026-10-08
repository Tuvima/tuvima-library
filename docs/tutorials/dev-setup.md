---
title: "Developer Setup"
description: "Clone the repo, configure local development, and run the services needed to work on Tuvima Library."
audience: "developer"
category: "tutorial"
product_area: "developer"
tags:
  - "contributors"
  - "setup"
  - "local-dev"
---
This tutorial walks you through cloning the repository, building the solution, running the test suite, and getting the Engine and Dashboard running locally. By the end you will have a working development environment and an understanding of where things live in the codebase.

---

## Prerequisites

- **.NET 10 SDK** - `dotnet --version` should report `10.0.x` or later. Download from [dot.net](https://dotnet.microsoft.com/en-us/download).
- **Git** - `git --version` to confirm.
- Approximately **10 GB free disk space** (AI models download on first Engine startup).

No other global tools are required. All project dependencies are declared in `.csproj` files and restored by the .NET toolchain.

---

## Step 1 - Clone and branch

```bash
git clone https://github.com/shyfaruqi/tuvima-library.git
cd tuvima-library
git checkout -b feature/your-branch-name
```

The `main` branch is the integration target. All work goes on a feature branch.

---

## Step 2 - Project structure overview

The solution is split into focused projects under `src/` and `tests/`. Each project has a single responsibility:

| Project | Role |
|---|---|
| `src/MediaEngine.Domain` | Domain entities, interfaces, value objects. Pure business logic - no I/O dependencies. |
| `src/MediaEngine.Storage` | SQLite data access via Dapper. Repositories, migrations, and query logic. |
| `src/MediaEngine.Intelligence` | Priority Cascade engine. Scores and resolves metadata claims. |
| `src/MediaEngine.Processors` | File processors - reads embedded metadata from EPUB, ID3 tags, video containers, etc. |
| `src/MediaEngine.Providers` | External provider adapters - Apple API, TMDB, Comic Vine, MusicBrainz, and Wikidata Reconciliation API. |
| `src/MediaEngine.Ingestion` | Folder watcher, ingestion pipeline, file organiser, staging logic. |
| `src/MediaEngine.AI` | Local LLM and Whisper inference. Hardware profiling, model management, AI feature implementations. |
| `src/MediaEngine.Api` | ASP.NET Core host. HTTP endpoints, SignalR collection, background services. Exposes the Engine. |
| `src/MediaEngine.Web` | Blazor Server host. Dashboard UI - components, pages, services. |
| `tests/` | xUnit test projects, one per domain area. |

The `config/` directory holds all runtime configuration as individual JSON files (committed to git; provider secrets in `config/secrets/`, gitignored). The `.data/` directory holds the SQLite database, cover art images, and staging files (gitignored).

---

## Step 3 - Configuration

Configuration files are already in the repository. Add secret files for any providers that require API keys (e.g. `config/secrets/tmdb.json` with `{"api_key": "your-key"}`).

The key file is `config/core.json`. The defaults work without modification for local development:

```json
{
  "database_path": ".data/database/library.db",
  "data_root": ".data"
}
```

Other config files of interest during development:

| File | Purpose |
|---|---|
| `config/ai.json` | AI model paths, hardware tier overrides, feature flags |
| `config/libraries.json` | Watch folders (add entries here to seed a dev library) |
| `config/scoring.json` | Priority Cascade weights and tier configuration |
| `config/providers/*.json` | Per-provider settings (endpoints, language strategy, non-secret defaults) |
| `config/secrets/*.json` | Provider API keys and secret overlays; gitignored |
| `config/hydration.json` | Hydration pipeline slot configuration |

Sensitive values, including provider API keys, go in `config/secrets/{provider}.json`. Base provider files can leave `http_client.api_key` blank; the Engine applies the matching secret overlay at runtime.

---

## Step 4 - Build

From the repository root:

```bash
dotnet build
```

The build must produce **0 errors and 0 warnings**. If warnings appear, treat them as errors - the CI pipeline enforces this. Investigate and fix before proceeding.

If you see `NU1101` package restore errors, check your NuGet source configuration. The `Tuvima.Wikidata` package is published to the project's private NuGet feed; ensure your `NuGet.Config` points to it.

---

## Step 5 - Run tests

```bash
dotnet test
```

All tests must pass before committing. Test projects mirror the `src/` structure:

```
tests/
  MediaEngine.Domain.Tests/
  MediaEngine.Storage.Tests/
  MediaEngine.Intelligence.Tests/
  MediaEngine.Processors.Tests/
  MediaEngine.Providers.Tests/
  MediaEngine.Ingestion.Tests/
```

To run a specific project:

```bash
dotnet test tests/MediaEngine.Intelligence.Tests/
```

To run with coverage:

```bash
dotnet test --collect:"XPlat Code Coverage"
```

Coverage reports are written to `TestResults/` inside each test project.

---

## Step 6 - Start the Engine

```bash
dotnet run --project src/MediaEngine.Api
```

The Engine starts on `http://localhost:61495`. On first run it will:

1. Run a hardware benchmark (10-30 seconds).
2. Apply any pending SQLite migrations automatically.
3. Start the folder watcher for any configured library folders.
4. Begin downloading AI models in the background (~9 GB total).

The Engine is ready when you see:

```
Now listening on: http://localhost:61495
```

To run without optional AI startup work (useful for UI-only development):

```json
// config/ai.json
{
  "enabled": false
}
```

---

## Step 7 - Start the Dashboard

Open a second terminal:

```bash
dotnet run --project src/MediaEngine.Web
```

The Dashboard starts on `http://localhost:5016`. Open it in your browser.

Hot reload is available during development:

```bash
dotnet watch --project src/MediaEngine.Web
```

This rebuilds and refreshes the browser on file saves. Note: Blazor Server hot reload works best for component changes. Changes to DI registrations, middleware, or static assets may require a full restart.

---

## Step 8 - Swagger / interactive API explorer

With the Engine running, open:

```
http://localhost:61495/swagger
```

This shows all Engine endpoints, organised by controller. You can send requests directly from the browser - useful for testing endpoint behaviour without writing test code.

The Swagger UI is generated automatically from XML doc comments on controller methods. Keep doc comments up to date when adding or modifying endpoints.

---

## Step 9 - Key conventions

### Headless design: Engine and Dashboard are strictly separated

The Dashboard never imports types from Engine projects. All data flows via HTTP (REST) and SignalR. The contract lives in:

- `src/MediaEngine.Web/Services/Integration/ILibraryApiClient.cs` - interface for all Engine calls
- `src/MediaEngine.Web/Services/Integration/LibraryApiClient.cs` - implementation (maps JSON responses to view DTOs)
- `src/MediaEngine.Web/Models/ViewDTOs/` - data shapes used only by the Dashboard

Never add a project reference from `MediaEngine.Web` to any Engine project.

### Feature-Sliced Dashboard layout

Dashboard code is organised by feature slice, not by technical layer. When adding new UI code, put it in the correct slice:

| Code type | Location |
|---|---|
| Engine HTTP call | `Services/Integration/LibraryApiClient.cs` + interface |
| Dashboard data shape | `Models/ViewDTOs/` |
| Reusable component | `Components/<FeatureName>/` |
| Full page (routed) | `Components/Pages/` |
| Browse/detail sub-component | `Components/Browse/`, `Components/Library/`, `Components/LibraryItems/`, or the feature-specific folder |
| Settings tab | `Components/Settings/` |

See `CLAUDE.md` section 6 for the full layout reference.

### SQLite with Dapper

The data access layer uses Dapper (not Entity Framework). SQL queries are written by hand in repository classes under `src/MediaEngine.Storage/Repositories/`. Migrations are numbered sequentially (`M-001`, `M-002`, ...) and applied automatically at startup by `MigrationRunner`.

When adding a new column or table:
1. Create a new migration file in `src/MediaEngine.Storage/Migrations/`.
2. Number it one above the current highest migration.
3. Register it in `MigrationRunner.cs`.
4. Never modify an existing migration that has been applied to production.

### Zero warnings policy

The solution sets `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in `Directory.Build.props`. New code must be warning-free. This includes nullable reference type warnings - all new code must be null-safe.

---

## Step 10 - Where to find things

**Engine endpoints** live in `src/MediaEngine.Api/Endpoints/`. Each file groups related actions (e.g., `LibraryEndpoints.cs`, `CollectionEndpoints.cs`, `MetadataEndpoints.cs`, `AiEndpoints.cs`). Endpoints use minimal API style (`MapGet`, `MapPost`, etc.) registered in `Program.cs`.

**Domain entities** live in `src/MediaEngine.Domain/Entities/`. The core hierarchy: `MediaAsset` -> `Edition` -> `Work` -> `Collection` (Series) -> `ParentCollection` (Universe).

**Dashboard components** live in `src/MediaEngine.Web/Components/`. Navigation is in `Components/Navigation/`, shared browse helpers are in `Components/Browse/`, `Components/Library/`, and `Components/LibraryItems/`, and Settings/Admin tabs are in `Components/Settings/`.

**Background services** live in `src/MediaEngine.Api/Services/`. Long-running services (ingestion watcher, hydration scheduler, AI batch processor) are registered as `IHostedService` implementations.

**Provider adapters** live in `src/MediaEngine.Providers/Adapters/`. Each adapter implements `IMetadataProvider`. The `ReconciliationAdapter` handles all Wikidata interaction.

**AI features** live in `src/MediaEngine.AI/Features/`. Each feature is a focused service (e.g., `SmartLabeler`, `VibeTagger`, `QidDisambiguator`, `DescriptionIntelligenceService`).

---

## Commit conventions

Commits follow this format:

```
Short summary line (imperative mood, 72 chars max)

Optional longer body explaining why, not what.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
```

The build must pass before committing. Run `dotnet build` and `dotnet test` and confirm 0 errors before pushing.

---

## Next steps

- Read the [architecture deep-dives](../architecture/ingestion-pipeline.md) for the subsystem you are working on.
- Check `CLAUDE.md` for project conventions and vocabulary rules.
- Check `MEMORY.md` for recorded architectural decisions.
- Use `http://localhost:61495/swagger` to explore the Engine's action surface while reading endpoint code.

## Related

- [How to Build, Test, and Verify Changes](../guides/running-tests.md)
- [How to Write a New File Format Processor](../guides/writing-a-processor.md)
- [How to Add a New Metadata Provider](../guides/adding-a-provider.md)
