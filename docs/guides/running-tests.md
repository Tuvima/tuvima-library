---
title: "How to Build, Test, and Verify Changes"
description: "Run the project quality checks and verify changes before you commit or open a pull request."
audience: "developer"
category: "guide"
product_area: "testing"
tags:
  - "build"
  - "test"
  - "verification"
---
This guide covers the full verification workflow: building the solution, running unit
tests, using the development endpoints for integration testing, and cleaning up.

---

## Prerequisites

- .NET 10 SDK installed (`dotnet --version` should return `10.x.x`)
- The solution root is `tuvima-library/`
- Both projects built successfully at least once so restore has run

---

## 1. Building the solution

Always build from the repository root. The build must produce **0 errors and 0 warnings**
before any change is considered complete.

```bash
dotnet restore MediaEngine.slnx
dotnet build MediaEngine.slnx --no-restore
```

A clean build looks like:

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

Any warning is treated as a defect and must be resolved. Common warning sources:

- Nullable reference annotations: add `?` or null-check as appropriate
- Unused `using` directives: remove them
- Async method without `await`: either add `await` or return a `ValueTask`/`Task.CompletedTask`

### Building a single project

When iterating on one project to get faster feedback:

```bash
dotnet build src/MediaEngine.Processors/MediaEngine.Processors.csproj
dotnet build src/MediaEngine.Api/MediaEngine.Api.csproj
```

---

## 2. Running the unit test suite

```bash
dotnet test MediaEngine.slnx --no-build
```

This runs all test projects under `tests/`. Each project maps to one source project:

| Test project | Tests for |
|---|---|
| `MediaEngine.Domain.Tests` | Domain entities, enums, business rules |
| `MediaEngine.Storage.Tests` | Repository queries, schema bootstrap, clean-cutover validation |
| `MediaEngine.Intelligence.Tests` | Priority Cascade scoring logic |
| `MediaEngine.Processors.Tests` | `IMediaProcessor` implementations, `MediaProcessorRouter` |
| `MediaEngine.Providers.Tests` | Provider config parsing, field mapping logic |
| `MediaEngine.Ingestion.Tests` | Ingestion pipeline steps |
| `MediaEngine.AI.Tests` | AI feature contracts, hardware tier policy |
| `MediaEngine.Api.Tests` | API contracts, architecture boundaries, Engine smoke guardrails, DB access rules |
| `MediaEngine.Web.Tests` | Dashboard shell, navigation, product guardrails, shared editor launch behavior |

To run a single project:

```bash
dotnet test tests/MediaEngine.Processors.Tests
```

To run with verbose output (shows individual test names):

```bash
dotnet test --logger "console;verbosity=normal"
```

To run with code coverage (requires coverlet, already referenced in test projects):

```bash
dotnet test --collect:"XPlat Code Coverage"
```

Coverage reports land in `tests/*/TestResults/`.

### Dashboard guardrail suites

These tests are intentionally about preventing regressions, not only checking one feature:

- `UiCompositionGuardrailTests` prevents removed all-in-one management workflows, outdated navigation labels, current Dashboard docs for removed workflows, and non-shared media editing paths from returning.
- `ArchitectureBoundaryTests` keeps Domain independent, blocks Web-to-Storage implementation coupling in active UI, flags direct SQL in Razor, and tracks endpoint files that still use direct database access.
- `DatabaseConnectionGuardrailTests` reserves `IDatabaseConnection.Open()` for startup/schema work and keeps silent catches limited to documented legacy locations.
- `DatabaseStartupSafetyTests` initializes temporary SQLite databases from scratch, verifies idempotent startup, checks WAL/foreign-key/integrity settings, and rejects retired schema state. Pre-beta databases are reset and reingested rather than migrated in place.
- `DockerfileGuardrailTests` verifies Docker restore inputs and readiness-based container startup.

Run a targeted guardrail pass with:

```bash
dotnet test tests/MediaEngine.Api.Tests --filter "ArchitectureBoundaryTests|DatabaseConnectionGuardrailTests|EngineSmokeGuardrailTests|DockerfileGuardrailTests"
dotnet test tests/MediaEngine.Storage.Tests --filter DatabaseStartupSafetyTests
dotnet test tests/MediaEngine.Web.Tests --filter UiCompositionGuardrailTests
```

CI also runs `dotnet format MediaEngine.slnx --verify-no-changes`, `dotnet list MediaEngine.slnx package --vulnerable --include-transitive`, Docker build, docs build, and coverage collection. Coverage uses the conservative repository floor documented below; critical storage, API, Web shell/editor, review, ingestion, provider failure, and API-client failure paths should be ratcheted upward as those areas stabilize.

---

## 3. Starting the Engine for manual testing

The Engine (`MediaEngine.Api`) runs on `http://localhost:61495`.

```bash
dotnet run --project src/MediaEngine.Api/MediaEngine.Api.csproj
```

The Dashboard (`MediaEngine.Web`) runs on `http://localhost:5016`.

```bash
dotnet run --project src/MediaEngine.Web/MediaEngine.Web.csproj
```

Run both in separate terminals. The Dashboard makes HTTP requests to the Engine;
both must be running for the full UI to work.

---

## 4. Swagger - interactive API exploration

With the Engine running, open:

```
http://localhost:61495/swagger
```

Swagger lists every endpoint grouped by tag. Use it to:

- Explore what routes exist and their request/response schemas
- Test individual endpoints with real data without writing code
- Inspect authentication requirements (most endpoints require an `Authorization: ApiKey <key>` header)

The local development environment has an authentication bypass for `localhost` traffic
(configurable in `config/server.json`), so you can call most endpoints from Swagger
without setting an API key during development.

---

## 5. Development-only endpoints

### Protected real-media harness

Use `tools/Run-RealMediaHarness.ps1` for an existing collection, defaulting to
`C:\Temp\real_import`. It stops the development apps, builds them, hashes every
original, backs up the database/configuration, and clears development library
records offline while holding the Engine and Dashboard process leases. Accounts,
profiles, permissions, and storage identity reservations remain. View content and
old source registrations are cleared too. Original media and old fixture files
are never deleted. Derived content uses a fresh cache namespace; old caches are
retained for recovery rather than recursively deleting mixed storage.

The five catalogue folders are registered as read-only existing-library sources.
`personal_photos` and `personal_videos` are linked to the selected profile's View
Personal Space. Use `-ProfileId <guid>` if more than one profile exists.
`-PrepareOnly` performs the protected reset without starting ingestion.
`tools/Run-FullIntegration.ps1 -Mode Real` invokes the same runner; real mode
rejects `-WipeScope full`.

Real mode persists in `config/real-media-harness.json`. Startup verifies its
configuration and baseline before starting workers. Source changes or loss of
observer coverage stop the Engine. Live configuration/source edits and legacy
seed/reset endpoints are blocked. Do not remove this marker while the Engine is
running. Returning to generated fixtures requires stopping both apps and restoring
the saved configuration deliberately; it must not happen automatically.

Development Tools displays protection status and offers original-file
verification. These endpoints use effective-administrator authorization; neither
the runner nor the UI introduces a localhost authentication bypass.

For an audit while the Engine runs:

```powershell
dotnet src/MediaEngine.Api/bin/Debug/net10.0/MediaEngine.Api.dll --audit-real-media config --verify
```

Reports and database/configuration backups live in the timestamped
`tools/reports/real-media-*` directory. The audit reports per-file indexing,
duplicates, supporting files, unresolved files, identity/operation state, review
counts, and actual subtitle records. Hash equality confirms source preservation,
not successful playback. Check moving picture, audio, subtitles, seeking, and
resume in the real player separately. A queued scan is never ingestion completion.

If protection stops the Engine, preserve the original baseline and failure report.
Do not generate a fresh baseline to hide changes. The narrow offline recovery
command `--resume-real-media-directory-timestamps config` handles only evidenced
`.tuvima_probe_*` directory timestamp changes: it rehashes every original, rejects
any file/path/attribute/security difference, and records the historical violation.
It never restores timestamps or modifies originals. Subsequent monitoring uses a
separate snapshot, while final audits still compare the immutable original
baseline and retain the failed preservation result. This is for recovery after
fixing a defect, not a general way to accept changing test sources.

The following endpoints are **only registered when `ASPNETCORE_ENVIRONMENT == "Development"`**.
They are absent in any other environment.

### POST /dev/reset-and-seed

This is the primary Development Tools workflow. It preserves application configuration and configured source media, clears catalogued library and derived ingestion state, creates the selected fixtures, queues them through normal ingestion, and resumes file watching. Generated fixture paths are recorded in a manifest; cleanup deletes only those recorded paths inside configured source roots.

```bash
curl -X POST "http://localhost:61495/dev/reset-and-seed?types=books,tv,music&fixtureSet=standard"
```

Use `fixtureSet=standard` for a small deterministic structural regression set or `fixtureSet=stress` for the full high-volume fixture catalog.

### POST /dev/reset-library-data

Clears catalogued media, relationships, enrichment, generated artwork, and ingestion/activity state while preserving accounts, profiles, configured libraries, permissions, application settings, provider configuration, and source files.

```bash
curl -X POST http://localhost:61495/dev/reset-library-data
```

### POST /dev/factory-reset

Clears the development database and generated state, including accounts and profiles. Configured source media is not deleted. The Dashboard requires a strong confirmation before invoking this endpoint.

The endpoints below are retained for automated and specialized harness scripts. They are no longer exposed as parallel workflows on the Development Tools page.

### POST /dev/seed-library

Drops synthetic test files (EPUB, MP3, MP4, FLAC, CBZ) into configured library source folders.
The ingestion engine picks them up automatically.

```bash
curl -X POST http://localhost:61495/dev/seed-library
```

Returns a summary of how many files were seeded per media type.

### POST /dev/wipe

Wipes generated-state fixture files and catalogued data by default while preserving unrelated configured source media and application configuration. Pass the explicit legacy `wipeScope=full` option only from disposable automated harnesses that intentionally own every configured source file.
The operation pauses file watching to avoid processing files mid-wipe.

```bash
curl -X POST http://localhost:61495/dev/wipe
```

Use this before a full integration test run to start from a clean state.

### POST /dev/full-test

Runs wipe then seed in sequence and returns a per-media-type summary.

```bash
curl -X POST http://localhost:61495/dev/full-test
```

### POST /dev/reingest-library

Development-only harness for rebuilding a clean `guid-blob-v1` database from the configured media folders without deleting source media.

The harness pauses file watching, validates destructive path safety, removes generated database/cache/artwork state, scans configured managed sources through ingestion, and leaves file watching paused until the Engine restarts or an explicit resume is requested. Existing-library sources are excluded from destructive fixture cleanup.

```bash
curl -X POST http://localhost:61495/dev/reingest-library
```

Use this when validating a clean-break storage reset against a real or large library. Use `/dev/full-test` for synthetic coverage first.

### POST /dev/integration-test

The most thorough validation. Runs the complete cycle:

1. Wipes database and library root
2. Seeds synthetic files for all media types
3. Waits for the ingestion engine to process them
4. Validates identification rates, metadata quality, and pipeline completion
5. Writes a timestamped HTML report to `tools/reports/` and `src/MediaEngine.Api/DevSupport/`

```bash
curl -X POST http://localhost:61495/dev/integration-test
```

The report file is named `integration-test-YYYY-MM-DD-HHmmss.html`. Open it in a browser
to see per-asset results, claim values, confidence scores, and any failures. The latest
run is always aliased to `tools/reports/integration-test-latest.html`.

---

## 6. Debug lookup - test enrichment without persisting

The `/debug/lookup` endpoint runs a live Wikidata Reconciliation + retail provider pass
against a given title, returning every claim that would be written to the database
without actually writing anything.

```http
POST http://localhost:61495/debug/lookup
Content-Type: application/json

{
  "title": "Dune",
  "author": "Frank Herbert",
  "mediaType": "Books",
  "isbn": "9780441013593"
}
```

Use this to verify that a new provider config returns claims, or to debug why an item
is not matching during hydration.

---

## 7. SignalR - testing real-time events

The SignalR collection for real-time dashboard updates is at:

```
ws://localhost:61495/intercom
```

Connect using the `@microsoft/signalr` client (already vendored in the Dashboard) or
a standalone tool like `wscat`:

```bash
wscat -c ws://localhost:61495/intercom
```

Events published by the Engine include ingestion progress updates, hydration stage
completions, and library change notifications. Event names and payload shapes are
defined in `src/MediaEngine.Web/Services/Integration/IntercomEvents.cs`.

To observe events during a seeded test run:
1. Connect a SignalR client to `/intercom`
2. POST to `/dev/seed-library`
3. Watch for ingestion progress and completion events in real time

---

## 8. Configuration for development

All config files live in `config/`. They are JSON files with one concern per file.
Config files are committed in `config/`. Provider secrets go in `config/secrets/` (gitignored).

Key files for development:

| File | Purpose |
|---|---|
| `config/core.json` | Library root, display name, language |
| `config/libraries.json` | Library source paths, media types, and folder policies |
| `config/server.json` | Port, auth bypass, rate limits |
| `config/hydration.json` | Retail, Wikidata, and enrichment worker config |
| `config/providers/*.json` | One file per metadata provider |
| `config/ai.json` | AI model roles and feature toggles |
| `config/scoring.json` | Priority Cascade weights |

If a config file is missing on startup, the Engine logs a warning and uses defaults
for most settings. Required files (database path, library root) will cause a startup
failure with a descriptive error in `engine.log`.

---

## 9. Serilog logs

The Engine writes rolling logs to `engine.log` in the repository root. Use it to
diagnose silent failures, HTTP errors from providers, and ingestion pipeline events.

The log level defaults to `Information`. To see all provider HTTP activity, temporarily
set `Minimum Level: Verbose` in the Serilog config section of `appsettings.Development.json`
(not committed) or `config/server.json` depending on your setup.

---

## 10. Cleaning up after work

After finishing any coding session, stop all `dotnet` processes to release file locks
on the SQLite database and log files:

```bash
taskkill //F //IM dotnet.exe
```

On Linux/macOS:

```bash
pkill -f dotnet
```

This is especially important before switching branches or running migrations, as an
open `dotnet` process holds a write lock on `library.db`.

---

## 11. Code conventions quick reference

Follow these conventions so the build stays clean and code review is predictable.

### Endpoint registration

New API endpoints go in `src/MediaEngine.Api/Endpoints/`. Each file registers a feature
group via a static extension method on `IEndpointRouteBuilder`:

```csharp
public static class MyFeatureEndpoints
{
    public static IEndpointRouteBuilder MapMyFeatureEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/my-feature").WithTags("MyFeature");

        group.MapGet("/", async (...) => { ... });
        group.MapPost("/action", async (...) => { ... });

        return app;
    }
}
```

Register the method in `Program.cs` alongside the other `MapXxxEndpoints` calls.

### Dashboard UI (Feature-Sliced layout)

New Dashboard code goes into the correct slice of `src/MediaEngine.Web/`:

| What you're adding | Where it goes |
|---|---|
| Engine HTTP call | `Services/Integration/LibraryApiClient.cs` + interface |
| Dashboard data shape | `Models/ViewDTOs/` |
| Reusable component | `Components/<FeatureName>/` |
| Full page (routed) | `Components/Pages/` |
| Browse/detail sub-component | `Components/Browse/`, `Components/Library/`, `Components/LibraryItems/`, or the feature-specific folder |
| Settings tab | `Components/Settings/{GroupName}Tab.razor` |

### Config files

One concern per file. Names are lowercase with underscores. Provider configs go in
`config/providers/`. Never mix multiple subsystem settings into one file.

---

## 12. Common issues

**Build error: `CS8618` - Non-nullable property not initialised**
Add `= null!;` for properties initialised by the framework (e.g. `[Inject]` in Blazor
components), or make the property nullable with `?` if it can legitimately be null.

**Build warning: `CS1998` - Async method lacks `await`**
Either add `await` to an async operation in the body, or remove `async` and return
`Task.CompletedTask` or `ValueTask.CompletedTask`.

**`dotnet test` fails with SQLite locked**
Another `dotnet` process has the database open. Run `taskkill //F //IM dotnet.exe`
(Windows) or `pkill -f dotnet` (Linux/macOS) and retry.

**`/dev/seed-library` returns 0 seeded files**
The configured library source path in `config/libraries.json` does not exist or the Engine does
not have write permission. Check the path and create the directory if needed.

**Provider returns no claims in `/debug/lookup`**
Check: `enabled: true` in the provider config, `can_handle.media_types` includes the
requested type, and the `required_fields` for at least one search strategy are present
in the request body. See `engine.log` for HTTP-level errors.

**Dashboard shows no data after seeding**
The Dashboard connects to the Engine via SignalR and HTTP. Verify both services are
running, and that the Engine base URL in `src/MediaEngine.Web/appsettings.Development.json`
matches `http://localhost:61495`.

---

## Related

- [Developer Setup](../tutorials/dev-setup.md)
- [Engine API Reference](../reference/api-endpoints.md)
- [Database Schema Reference](../reference/database-schema.md)

# Coverage Threshold

CI still runs restore, warning-as-error build, format verification, tests with `XPlat Code Coverage`, vulnerable package checks, and Docker build/push behavior. Wave 5 adds a Cobertura threshold step:

```powershell
./scripts/coverage/Enforce-Coverage.ps1 -ResultsDirectory ./TestResults
```

The initial floor lives in `scripts/coverage/coverage-thresholds.json`: 13% line coverage and 7% branch coverage, rounded down from the Wave 5 baseline run. This is a conservative regression gate for the current repository, with documented targets of 70% line and 60% branch coverage to ratchet toward over time.
