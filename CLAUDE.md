# CLAUDE.md — Tuvima Library

> Loaded into every Claude session **and every helper agent**, so it is kept deliberately short. It holds only what every task needs. Detail lives elsewhere and is read on demand:
>
> | Need | Read |
> |---|---|
> | Dashboard work (`src/MediaEngine.Web/`) | `src/MediaEngine.Web/CLAUDE.md` (auto-loads in that folder) |
> | Current per-surface product behaviour (cards, Home, details, TV, Collections, Libraries, View, editor, player) | `docs/product/presentation-rules.md` |
> | Subsystem summaries | `docs/architecture/architecture-summary.md`, then the deep dive in `docs/architecture/*.md` |
> | Developer code tour, local run notes | `AGENTS.md` (large — search it, don't read it whole) |
>
> Do not add dated "October 2026"-style rule dumps here. New detailed rules go to the owning doc above; only cross-cutting guardrails belong in this file.

---

## 1. Product

**Tuvima Library** is the product; **Tuvima** is the company. Code namespaces use `MediaEngine.*` deliberately, decoupled from branding.

The guiding word is **Presentation**: Tuvima doesn't create a library, it presents one. Stories already on disk — fragmented across formats and folders — are found, understood, unified, and surfaced as something coherent and beautiful. Use this frame for copy, feature names, and explanations.

Tuvima's Engine, Dashboard, catalog and optional AI inference run on the user's machine, with no Tuvima-hosted account or subscription. Configured providers, downloads and the Places map can make external requests; local-first is not a guarantee of network-free operation. It watches folders, identifies and enriches owned media, resolves metadata with a Priority Cascade, and connects works across formats. See `docs/explanation/privacy-local-first.md` for network behavior.

### Terminology — user-facing vs internal

| Level | User-facing | Internal code | Example |
|---|---|---|---|
| Entire library | **Library** | Library | Everything you own |
| Franchise grouping | **Universe** | ParentCollection | Dune, Marvel, Tolkien |
| Series / collection | **Series** | Collection | Dune Novels, Dune Films |
| Single title | **Work** | Work | Dune Part One |
| Specific version | **Edition** | Edition | 4K HDR Blu-ray Remux |
| File on disk | **Media Asset** | MediaAsset | the .mkv file |

Anything the user sees uses the user-facing name. Universes are optional; both Universes and Series are resolved at scoring time and have no filesystem presence. A Series is the lane-level shelf in Read/Watch/Listen; a broader Universe/Collection appears on `/collections` only when a shared series/franchise/universe relationship connects multiple shelves. Multiple formats of one work are variants, not collection triggers. User-facing surfaces name and route by group type (a TV show is a show, a book sequence is a series, a curated rollup is a collection/list). Full rules: `docs/product/presentation-rules.md`.

Library types: Books (EPUB/PDF + audiobooks M4B/MP3), TV, Movies, Music, Comics (CBZ/CBR/PDF), Personal/Custom (local-only metadata, bypasses providers), and Photos (separate asset index; never enters the Work/Edition graph).

---

## 2. Guardrails (non-negotiable)

**Product workflow**
- Do not recreate the old all-in-one management workflow: no new routes, implementation types, navigation labels, docs presenting it as current behaviour, or all-in-one correction workbenches.
- Normal detail-page fixes use `MediaEditorLauncherService.OpenAsync` to show `SharedMediaEditorShell` modally over the unchanged detail page and URL; Review and Batch reuse the same shell in dialogs. Keep Details lean, provider facts read-only, and structural parent moves in Matching.
- Single-item editing keeps metadata, local fields, and sorting in Details (no separate Options tab). File shows physical-file state only; History owns identity, metadata, artwork, and ingestion events. A retail rematch synchronously replaces provider-managed artwork and refreshes the hero before background Wikidata alignment.
- Review Queue (`/settings/review`) is the exception workflow for blocked/uncertain/low-confidence items. Settings/Admin is configuration and operational state, not a correction workspace.

**Data store**
- Use `IDatabaseConnection.CreateConnection()` for repository, read-service, endpoint, background-job, and request-path work; dispose each short-lived connection with `using`. `IDatabaseConnection.Open()` is startup/schema/integrity-only — new uses elsewhere must fail guardrail tests.
- Storage epoch `guid-blob-v1`: internal SQLite GUIDs are 16-byte BLOBs, external IDs stay TEXT, API JSON returns GUID strings; legacy TEXT-GUID databases are reset and reingested, not migrated.
- SQLite: WAL, `synchronous=NORMAL`, `busy_timeout=5000`, memory temp store, 16 MiB page cache, 256 MiB mmap cap. Accepted tradeoff: an OS/power crash may require reingesting the latest changes.
- `canonical_values` is scalar-only; multi-valued metadata goes in `canonical_value_arrays`. No packed delimiters or compatibility readers.
- Schema changes are idempotent startup migrations owned by `SchemaMigrator` (via `DatabaseConnection.RunStartupChecks()`). Pre-release: breaking changes and DB resets are acceptable; no backwards-compatibility shims.

**Architecture**
- Domain stays independent of Web, API, Storage, Providers, Ingestion, Processors, AI, and UI packages. UI consumes view models, Contracts, and typed clients — never storage models.
- Domain aggregates expose children, property bags, and lifecycle state read-only; mutate through explicit aggregate methods (`Work.LinkToWikidata`, `Collection.SetVisibility`, `Collection.ChangeResolution`, …). Persisted aggregate enums convert only through `AggregateStateSerializer`; unknown values fail fast.
- Razor components contain no SQL. API endpoints move SQL-heavy behaviour into repositories/read services when touched.
- **Wire ownership:** `src/MediaEngine.Contracts/` solely owns types serialized over HTTP/SignalR. Engine endpoints map explicitly into Contracts; the Dashboard deserializes Contracts directly and never keeps a second JSON DTO. Preserve `[JsonPropertyName]`, casing, defaults, nullability, and collection shape. Only frozen exception: `LoreDeltaDiscoveredEvent` and `UniverseEnrichmentProgressEvent` in `Services/Integration/IntercomEvents.cs` — never extend it. Enforced by `BoundaryContractGuardrailTests` and `WireContractSnapshotTests`.
- No silent `catch { }`: best-effort failures need a justification comment or guardrail allowlist entry; user-visible failures need logging and degraded/error UI.
- Dashboard service credentials resolve at request send time, never at typed-client construction; missing/invalid/rotated bundles fail closed with recoverable errors. No anonymous requests or stale credentials; the service header and any View signature share one credential snapshot.
- Security (`docs/architecture/security.md`): obsolete pre-beta identity state fails fast. Never restore role-based guest keys, automatic compatibility conversion, localhost administration, seed-owner fallback, or private-space reassignment. Original source media remain protected.
- Before changing C# for behaviour that looks configurable, check `config/` first (provider behaviour is JSON-driven in `config/providers/`; secrets in gitignored `config/secrets/`).

**Build and outputs**
- Normal builds/publishes select only the explicit runtime/SDK host and its RID fallbacks — keep that filtering on. QA outputs live in ignored `.tmp/`. After stopping Engine/Dashboard, clean with `pwsh -File tools/Clean-RepoOutputs.ps1` (`-WhatIf` previews; `-IncludeQa` only for its listed QA folders). See `docs/guides/repository-storage.md`.
- Local AI: native runtimes load from `TUVIMA_AI_RUNTIME_DIR`, weights from `TUVIMA_MODELS_DIR` (workstation: `E:\Resources\AI Models` and sibling `AI Runtimes`); provision with `tools/Install-AiRuntime.ps1`. Never copy native AI packages back into build outputs. Keep at most two temporary worktrees, outside `Repos`. See `docs/guides/shared-ai-storage.md`.

---

## 3. Codebase map

.NET 10 (`global.json`), SQLite + Dapper, Blazor Server with first-party `App*` components, SignalR, xUnit. Authoritative dependency list: `Directory.Packages.props`. The Engine (`MediaEngine.Api`, HTTP + SignalR) and Dashboard (`MediaEngine.Web`) are separate apps.

| Project | Role |
|---|---|
| `Domain` | Aggregates, entities, enums, config shapes, inward ports. No external packages. |
| `Contracts` | Engine↔Dashboard DTOs (`Details/`, `Display/`, `Paging/`, `Playback/`, `Settings/`). Depends only on Domain. |
| `Application` | Read-model DTOs and query-service interfaces. |
| `Storage` | All repositories (`*Repository`), Dapper SQL, schema + migrations, `DatabaseConnection`, `ConfigurationDirectoryLoader`. |
| `Intelligence` | Priority Cascade, identity strategies, `IdentityDecisionService`, `CollectionArbiter`, `ParentCollectionResolver`, fuzzy matching. |
| `Processors` | Embedded-metadata readers (EPUB, audio, video, comic, PDF, generic). |
| `Providers` | Config-driven provider adapters, hydration workers, enrichment services. |
| `Ingestion` | Watchers, hashing, dedup, organisation, write-back; standalone worker host. |
| `AI` | Local LLM/Whisper lifecycle and AI feature services. |
| `Identity` | Accounts, profile grants, authentication (`TreatWarningsAsErrors`). |
| `Admin` | Host-side admin console (e.g. administrator password reset / host recovery). |
| `Plugins` + `Plugin.CommercialSkip`, `Plugin.MediaSegments`, `Plugin.FandomLore` | In-process plugin contracts and plugins (segment detection, Fandom universe lore). |
| `Api` | Composition root: thin `Program.cs`, `AddTuvima*` modules in `DependencyInjection/`, endpoints, `Intercom` hub. |
| `Web` | Blazor Dashboard — see `src/MediaEngine.Web/CLAUDE.md`. |
| `tests/` | One test project per source project; strong guardrail suites; real temp SQLite; hand-written spies (no Moq/NSubstitute). |

Pipeline in one line: Settle → Lock → Fingerprint → Scan → Identify → Stage 1 retail match → Stage 2 Wikidata bridge → Quick Hydration → Stage 3 enrichment → organise/write-back. Jobs are durable `identity_jobs` rows. Cascade tiers: user locks → per-field provider priority → Wikidata authority → highest confidence. Claims are append-only.

Where new non-Dashboard code goes: Engine↔Dashboard type → `Contracts/<Concern>/` (+ boundary tests); read-model DTO → `Application/ReadModels/`; query contract → `Application/Services/IReadServices.cs`; config shape/port → `Domain/Configuration/` or `Domain/Contracts/`; SQLite repository → `Storage/` (`Repository` suffix); API service without persistence → `Api/Services/` (`Service` suffix); registration → a focused `Api/DependencyInjection/Tuvima*ServiceCollectionExtensions.cs`; plugin → new `src/MediaEngine.Plugin.<Name>/` implementing `ITuvimaPlugin`.

Local dev: Engine `dotnet run --project src/MediaEngine.Api` (`http://localhost:61495`, HTTPS 61494) first, then Dashboard `dotnet run --project src/MediaEngine.Web` (`http://localhost:5016`, HTTPS 7062). Run from repo root. Managed data lives in `{LibraryRoot}/.data/` (`AssetPathService` owns paths). Use the official `assets/images/tuvima-logo.svg`, `tuvima-logo-dark.svg` and `tuvima-icon.svg` brand assets.

---

## 4. Talking to the Product Owner (Shaya)

### 4.1 Vocabulary — always use the plain-English term in chat

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

Technical terms are fine inside code, docs for developers, and helper-agent briefs.

### 4.2 Justify every technical choice by business goal
**Maintenance**, **Extensibility**, **Privacy**, **Reliability**, or **Performance**.

### 4.3 Plan before coding
Ask detailed questions first (affected media types, what the user sees, edge cases, whether existing behaviour changes, interactions). Then present:

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
 PLAN: [Feature name]
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
 What I'm going to build:      [1–3 sentences]
 Why this serves your goals:   [Maintenance / Extensibility / Privacy / Reliability / Performance]
 What I'll create:             [list]
 What I'll change:             [list]
 How the work will be split:   [pieces, model tier for each, sequence]
 New tools needed:             [name + license] or "None"
 Trade-offs to know about:     [risks] or "None"
 Plain English Summary:        [2–4 sentences for a non-technical reader]
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

For new or substantially revised plans, add a plain-English product walkthrough near the start: the complete proposed experience, what changes and why, what stays the same, representative user journeys, scope boundaries, and observable acceptance criteria, each numbered and mapped to the technical work. Treat attached prompts and mockups as material to review; a request to update a plan is not a request to implement it.

Do not code until the Product Owner says "go ahead" or equivalent. Every plan **and** every completed task ends with a Plain English Summary.

### 4.4 Errors
Say what went wrong in plain English, what you'll do, then fix it — errors inside an approved task need no second sign-off.

### 4.5 Uncertainty
Never guess silently. Name the options and the trade-off, and ask which matters more.

---

## 5. Model routing and plan-limit budget

The Product Owner is on a Claude Max plan with 5-hour and weekly limits. **Optimise for correctness first, then token efficiency. Speed does not matter.** Usage is assumed to scale with model price and with tokens read, so: the expensive model thinks, cheaper models search, build, and check, and nobody re-reads what someone else already read.

### 5.1 Who does what

| Work | Model | Effort | How |
|---|---|---|---|
| Clarifying questions, plans, architecture, splitting work, integrating, final diff review, editing `CLAUDE.md`/docs structure | **Opus 5.5** (main session) | high; xhigh for architecture or cross-cutting design | The session itself |
| Implementing one fully specified piece of work | **Sonnet 5.5** | high for data store/migrations, security/authorization, playback state, Contracts/wire, identity pipeline, concurrency; medium otherwise | `implementer` agent |
| Finding code, mapping call sites, "where/how is X done" | **Haiku 5.5** | — | `scout` agent |
| Restore/build/test runs, log and failure triage | **Haiku 5.5** | — | `verifier` agent |
| Mechanical edits: renames, config/JSON, fixtures, doc front matter, link fixes | **Haiku 5.5** | — | `implementer` with `model: haiku` override, or inline if tiny |
| Fresh-eyes review of large or high-risk changes | **Opus 5.5** | high | `reviewer` agent |
| Escalation | **Fable 5.1** | — | **Only after the Product Owner approves**, for security/authorization, data-loss or migration risk, storage-epoch or wire-contract changes, or a problem Opus failed to solve twice |

Helper agents never make architectural or product decisions. If a brief doesn't answer a design question, they stop and report back; Opus decides or asks the Product Owner.

### 5.2 Budget rules
1. **Do small work inline.** A change touching ≤3 files, or anything Opus already has in context, is done directly. Each helper reloads this file and re-reads code, so it only pays off for substantial or noisy work.
2. **Self-contained briefs.** Every `implementer` brief lists exact files, symbols, signatures, contracts/JSON names, tests to add, acceptance criteria, and what must not change, so the helper does not explore. Use `scout` first if facts are missing; don't make the implementer discover them.
3. **Sequential by default; at most 2 helpers in parallel**, and only for independent pieces that touch different files.
4. **Keep noisy output out of Opus.** Build/test logs, large searches, and file dumps go through `verifier`/`scout`, which return short summaries with `file:line` anchors.
5. **Verify proportionally.** During the work, build and run the targeted test project for each piece. Run the full restore/build/test (§6 step 3) once, at the end.
6. **Two-strike escalation.** A helper that fails twice on the same problem hands back to Opus with the full error. If Opus also fails twice, ask the Product Owner about Fable 5.1.
7. **One feature per session.** Start a fresh session for unrelated work; compact at natural phase boundaries (after planning, after implementation). Write plans and briefs to the scratchpad or `.tmp/` so they survive compaction.
8. **Read narrowly.** Read files and line ranges, not whole large documents (`AGENTS.md`, big Razor files, `schema.sql`). Use Grep before Read.
9. **Visual checks are expensive.** Iterate with text-based page reads and computed styles; screenshot only for final before/after proof (see `src/MediaEngine.Web/CLAUDE.md`).

---

## 6. Workflow

1. **Read** this file plus only the docs and code relevant to the task. Never assume current state.
2. **Plan** (§4.3) and wait for approval.
3. **Implement and verify.** Before starting, stop running `MediaEngine.Api`/`MediaEngine.Web` processes. Finish with all three, aiming for **0 errors, 0 warnings, all tests passing**:
   ```bash
   dotnet restore MediaEngine.slnx
   dotnet build MediaEngine.slnx --no-restore
   dotnet test MediaEngine.slnx --no-build
   ```
   Run docs (`scripts/docs/build-docs.ps1`), Docker, format, and dependency checks when those areas are touched. Dashboard changes also need the visual validation in `src/MediaEngine.Web/CLAUDE.md`; a passing build is not visual proof. Afterwards, release locked binaries with `taskkill //F //IM dotnet.exe`.
4. **Document** in the same change when product concepts, navigation, editing flows, data-store lifecycle, Docker startup, config, endpoints, schema, or CI change:

   | Trigger | Update |
   |---|---|
   | Feature / UI surface | relevant `docs/explanation/*` page, `docs/product/presentation-rules.md` for surface behaviour |
   | Config field | `docs/reference/configuration.md` |
   | Endpoint | `docs/reference/api-endpoints.md` |
   | Table/column | `docs/reference/database-schema.md` |
   | File format / processor | `docs/reference/media-types.md` (+ `docs/guides/writing-a-processor.md`) |
   | Provider | `docs/reference/configuration.md`, `docs/reference/providers.md`, `docs/guides/configuring-providers.md` |
   | Terminology | `docs/reference/glossary.md` |
   | Architecture | `docs/architecture/*.md` and `docs/architecture/architecture-summary.md` |
   | Install/config/usage | `README.md`; repo map/startup → `AGENTS.md` |
   | New dependency | §6.1 below |

   Docs follow Diátaxis; author sources stay under `docs/`, with `docs/index.mdx` as the landing page. The pinned Astro Starlight project is `website/`. Every published page needs `title`, `description`, `audience`, `category`, `product_area` and explicit `status` (`current`, `early-access`, or `target-state`), validated at build time. Add pages to `website/publication.json`; moves need redirects or an explicit retirement. Run `scripts/docs/build-docs.ps1 -InstallDependencies` with Node 24. Keep engineering plans/reports in `engineering/`, review captures in ignored `.tmp/`, and current product screenshots absent until replacements are commissioned. The protected Wikidata page has one named metadata adapter. See `docs/develop/writing-docs.md`.
5. **Save point and push.** `git add <specific files>` (never `-A`), then commit with a short summary ending in `Co-Authored-By: Claude <model of the main session> <noreply@anthropic.com>`, and push.
   **Never commit:** `tuvima_master.json`, `*.db`, `bin/`, `obj/`, `.vs/`, `.idea/`, `appsettings.*.json` with real keys, `.codex/`, `site/`, or review/QA screenshots (keep them in ignored `.tmp/`; only documentation images belong in the repo).

`.agent/` (the former Antigravity/Gemini mirror) is retired and is not kept in sync.

### 6.1 License: AGPLv3
Every new tool must be license-compatible; check before adding. Safe: MIT, Apache 2.0, BSD-2/3, LGPL 2.1/3, GPL 3/AGPL 3. Ask first: GPL 2-only. Block: SSPL, Commons Clause, proprietary. Record approved additions in `Directory.Packages.props` and `docs/reference/attributions.md`/`THIRD-PARTY-NOTICES.md` as applicable.

---

## 7. Contacts

Product Owner: Shaya · Code history: [github.com/Tuvima/tuvima_library](https://github.com/Tuvima/tuvima_library) · License: AGPLv3 · Brand source art: `C:\Users\shaya\OneDrive\Documents\Projects\Tuvima\Graphics\`
