---
title: "Developer setup"
description: "Prepare a source checkout, review machine-specific settings, and verify the .NET solution before development."
audience: "developer"
category: "tutorial"
product_area: "developer"
status: current
---

# Developer setup

Prepare a checkout where you can run and verify Tuvima Library. Allow 15–30 minutes after installing tools, plus package and optional AI downloads.

## Check your tools

Install Git, PowerShell for repository scripts, and a stable .NET 10 SDK.

```powershell
git --version
dotnet --version
```

`global.json` requests SDK `10.0.100`, permits `latestFeature` roll-forward, and excludes prereleases. A host using stable `10.0.401` therefore satisfies the selection policy; the file does not pin that later version.

The Engine and Dashboard use .NET startup commands. The documentation website has its own Node tools; those are not required to start the app.

## Clone and prepare a branch

```powershell
git clone https://github.com/Tuvima/tuvima_library.git
cd tuvima_library
git checkout -b codex/your-change
```

Read `AGENTS.md` and the relevant project instructions before changing code. Stop existing repository Engine and Dashboard processes before development or build verification.

## Review configuration before running

Do not assume committed paths match your machine.

| File or location | Check |
| --- | --- |
| `config/core.json` | Data root, library root, database path, language |
| `config/libraries.json` | Approved storage, sources, permissions, View root |
| `config/providers/*.json` | Provider behavior and non-secret settings |
| `config/secrets/` | Ignored provider key overlays |
| `config/.secrets/` | Dedicated authentication/email secrets |
| `config/ai.json` | AI features and operational settings |
| `nuget.config` | Package sources and mappings |

This checkout uses nuget.org for packages. Older or local setups may map `Tuvima.Wikidata*` to `C:\Users\shaya\OneDrive\Documents\Source\Repos\tuvima-wikidata\artifacts`. If restore reports those packages missing, check the configured feed and sibling output before changing references.

Keep experimental media separate from originals. Use read-only source folders for real media. See [shared AI storage](../guides/shared-ai-storage.md) before provisioning native AI runtimes or model files.

## Restore and verify

Run from the repository root:

```powershell
dotnet restore MediaEngine.slnx
dotnet build MediaEngine.slnx --no-restore
dotnet test MediaEngine.slnx --no-build
```

Keep native build filtering enabled. It limits output to the selected runtime or SDK host and its fallbacks. Use ignored `.tmp/` folders for isolated QA output.

For a focused test run:

```powershell
dotnet test tests/MediaEngine.Intelligence.Tests/MediaEngine.Intelligence.Tests.csproj
```

See [running tests](../guides/running-tests.md) for live-provider opt-ins and verification policy.

## Run the apps

1. Start the Engine from the root:

   ```powershell
   dotnet run --project src/MediaEngine.Api
   ```

2. Wait for `http://localhost:61495` to report listening.
3. Start the Dashboard in a second root terminal:

   ```powershell
   dotnet run --project src/MediaEngine.Web
   ```

4. Open `http://localhost:5016` and complete setup if needed.

Both launch profiles use `TUVIMA_CONFIG_DIR=../../config`. Overrides must resolve to the same config and data-protection key directory. `TUVIMA_ENGINE_URL` changes the Dashboard's Engine address.

The HTTPS launch addresses are `https://localhost:61494` for the Engine and `https://localhost:7062` for the Dashboard's HTTPS profile. Keep the Engine private.

## Find the right project

| Project | Responsibility |
| --- | --- |
| `MediaEngine.Domain` | Domain rules, configuration shapes, inward contracts |
| `MediaEngine.Contracts` | HTTP and SignalR data contracts |
| `MediaEngine.Application` | Read models and query interfaces |
| `MediaEngine.Storage` | SQLite, Dapper, repositories, startup migrations |
| `MediaEngine.Intelligence` | Identity decisions and metadata precedence |
| `MediaEngine.Processors` | Embedded file metadata |
| `MediaEngine.Providers` | Provider adapters and enrichment |
| `MediaEngine.Ingestion` | Watching, hashing, organization, durable intake |
| `MediaEngine.AI` | Local models and inference |
| `MediaEngine.Identity` / `MediaEngine.Admin` | Access control and host recovery |
| `MediaEngine.Api` / `MediaEngine.Web` | Engine host and Dashboard |
| `tests/` | Focused tests and guardrails |

## Use developer tools

For Dashboard component work, start hot reload from the repository root:

```powershell
dotnet watch --project src/MediaEngine.Web
```

Changes to service registration or middleware can require a full restart. The Engine's development Swagger page is `http://localhost:61495/swagger`; protected actions still require the appropriate authenticated authority.

For coverage output:

```powershell
dotnet test MediaEngine.slnx --collect:"XPlat Code Coverage"
```

Test output appears under each project's `TestResults/` folder. See the running-tests guide for the current coverage gates.

## Stop and clean up

Press `Ctrl+C` in both runtime terminals. When obsolete build output needs removal, preview the repository cleaner:

```powershell
pwsh -File tools/Clean-RepoOutputs.ps1 -WhatIf
```

Run it without `-WhatIf` after checking the targets. `-IncludeQa` covers only its listed QA folders; retain review evidence until accepted.

<details>
<summary>Technical details: code ownership and data changes</summary>

The Dashboard uses typed clients and shared Contracts over HTTP and SignalR. Keep serialized shapes in `MediaEngine.Contracts`; Dashboard-only presentation models belong in `Models/ViewDTOs/`.

Reusable UI belongs in its feature's `Components/` directory, routed pages in `Components/Pages/`, and settings panels in `Components/Settings/`. Engine connection points live in `MediaEngine.Api/Endpoints/`, with registration in focused composition modules.

Storage uses SQLite and Dapper. Startup changes belong to idempotent `SchemaMigrator` steps invoked by startup checks. Use short-lived `IDatabaseConnection.CreateConnection()` connections for normal work; startup-only `Open()` is not a request-path helper.

Keep new warnings out of the solution and run the relevant guardrails before a change is complete. Follow the repository instructions for save points and proposed changes.

</details>

## Next steps

- [Read the architecture summary](../architecture/architecture-summary.md).
- [Run tests](../guides/running-tests.md).
- [Manage repository storage](../guides/repository-storage.md).
