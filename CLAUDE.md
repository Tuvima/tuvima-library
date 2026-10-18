# CLAUDE.md — Tuvima Library

Loaded into every Claude session and every helper agent, so it holds only what every task needs. Detail lives in `.claude/rules/` (loaded automatically; most are scoped to the folders they govern) and in the docs listed under "Where things live". Do not add dated rule dumps here.

## Project

**Tuvima Library** (company: **Tuvima**) is a local-first media library for people who own their media. Its guiding word is **Presentation**: it doesn't create a library, it presents one — stories already on disk, fragmented across formats and folders, are found, identified, enriched with a Priority Cascade, connected across formats, and surfaced as something coherent and beautiful. The Engine, Dashboard, catalog and optional AI run on the user's machine with no Tuvima-hosted account; configured providers, downloads and the Places map can make external requests (`docs/explanation/privacy-local-first.md`). Library types: Books (EPUB/PDF + audiobooks), TV, Movies, Music, Comics, Personal/Custom, and Photos (a separate View index that never enters the Work/Edition graph). Code namespaces use `MediaEngine.*` deliberately.

| User-facing | Internal | Example |
|---|---|---|
| Library | Library | Everything you own |
| Universe | ParentCollection | Dune, Marvel |
| Series | Collection | Dune Novels |
| Work | Work | Dune Part One |
| Edition | Edition | 4K HDR Blu-ray Remux |
| Media Asset | MediaAsset | the .mkv file |
| Needs Review | review_queue | items the Engine isn't sure about |

Anything the user sees uses the user-facing name; surfaces name and route by group type (a TV show is a show, a book sequence is a series). Full rules: `docs/product/presentation-rules.md`.

## Stack

.NET 10 (`global.json`), C# with nullable and warnings-as-errors everywhere, SQLite + Dapper (no Entity Framework), Blazor Server with first-party `App*` components (no MudBlazor), SignalR, xUnit with hand-written spies (no mocking library). Two apps: the **Engine** (`src/MediaEngine.Api`, HTTP + SignalR) and the **Dashboard** (`src/MediaEngine.Web`). Native clients (Android, Apple, Roku, TV web) live in `clients/`, outside the solution. Dependencies: `Directory.Packages.props`. Code map: `engineering/architecture/overview.md`.

Pipeline: Settle → Lock → Fingerprint → Scan → Identify → retail match → Wikidata bridge → Quick Hydration → enrichment → organise/write-back. Jobs are durable `identity_jobs` rows; claims are append-only.

## Commands (run from repo root)

```bash
dotnet restore MediaEngine.slnx
dotnet build MediaEngine.slnx --no-restore
dotnet test MediaEngine.slnx --no-build
```

| Task | Command |
|---|---|
| CI-strength build | `dotnet build MediaEngine.slnx --no-restore --warnaserror` |
| One test project | `dotnet test tests/MediaEngine.Storage.Tests/MediaEngine.Storage.Tests.csproj` |
| Filtered tests | `dotnet test tests/<Project>.Tests --filter "FullyQualifiedName~Name"` |
| CI test filter | `dotnet test MediaEngine.slnx --no-build --filter "Category!=LiveProvider"` |
| Format check | `dotnet format MediaEngine.slnx --verify-no-changes --no-restore` |
| Node tests | `node --test tests/build/*.test.mjs tests/MediaEngine.Web.Tests/JavaScript/*.test.mjs` |
| Run Engine, then Dashboard | `dotnet run --project src/MediaEngine.Api` (http://localhost:61495), `dotnet run --project src/MediaEngine.Web` (http://localhost:5016) |
| Docs site | `pwsh -File scripts/docs/build-docs.ps1` (`-InstallDependencies` first time, Node 24) |
| Wire snapshot refresh | `pwsh -File tools/Update-ContractFixtures.ps1` |
| Clean outputs | `pwsh -File tools/Clean-RepoOutputs.ps1` (after stopping Engine/Dashboard) |
| Release locked binaries | `taskkill //F //IM dotnet.exe` |

Stop running Engine/Dashboard before building. Aim for **0 errors, 0 warnings, all tests passing**. Dashboard changes also need the visual checks in `src/MediaEngine.Web/CLAUDE.md`; a passing build is not visual proof.

## Conventions actually used

- One test project per source project; real temp SQLite; ~50 guardrail suites (`*GuardrailTests.cs`) encode architecture rules — fix the code, not the guardrail.
- Engine services register through focused `AddTuvima*` modules in `src/MediaEngine.Api/DependencyInjection/`; `Program.cs` stays thin.
- Data access: repositories in `Storage/` with the `Repository` suffix; short-lived `IDatabaseConnection.CreateConnection()` per unit of work.
- `src/MediaEngine.Contracts/` alone owns Engine↔Dashboard wire types.
- Dashboard: feature-sliced folders, scoped state services (`*State`, `*StateContainer`), shared `App*` primitives, component-scoped CSS.
- Behaviour that looks configurable lives in `config/*.json` (providers in `config/providers/`, secrets in gitignored `config/secrets/`) — check there before changing C#.
- Long-running work runs in hosted background services with durable job rows.
- Test names read as behaviour sentences (`Pending…_DoesNot…`); `.editorconfig`: CRLF, 4 spaces, system usings first.

## Never

1. Recreate the old all-in-one management workflow (routes, types, navigation labels, docs presenting it as current, or correction workbenches). A single item's fixes happen in **Editing Mode** on its detail page (hero Edit/Done, one pencil per field saving straight away, locks on provider facts, and an `EditingDrawer` re-housing the `SharedMediaEditorShell` sections beside the unchanged page; no undo journal; never on a deep link). The modal `MediaEditorLauncherService.OpenAsync` stays only for Needs Review, browse shells, Recently Added and people/series/curated-collection editors; exceptions go to **Needs Review** (Settings → Recently Added). **Editing is desktop-only:** `EditorDeviceRule.CanEdit` (desktop form factor plus a fine, hover-capable pointer; unknown means no) gates every Dashboard launcher and edit handler, not just the buttons; the Engine's permission check still decides who may edit. In the editor: Details stays lean (metadata, local fields and sorting; no Options tab), provider facts are read-only, structural parent moves live in Matching; File shows physical-file state only; History owns identity, metadata, artwork and ingestion events; a retail rematch synchronously replaces provider artwork and refreshes the hero before background Wikidata alignment. Settings is configuration and operational state, never a correction workspace.
2. Put SQL in Razor components, or let UI consume storage models.
3. Keep a second JSON DTO in the Dashboard or change wire names/shapes without updating Contracts tests and fixtures.
4. Write a silent `catch { }`.
5. Call `IDatabaseConnection.Open()` outside startup/schema/integrity code.
6. Add backwards-compatibility shims or data-store migrations for pre-release data — resets are acceptable.
7. Restore role-based guest keys, automatic compatibility conversion of old identity state, localhost administration, seed-owner fallback, private-space reassignment, or role checks done by URL flag. Original source media stay protected.
8. Copy native AI runtimes or model weights into build outputs or the repo.
9. Add a tool/library without a license check (AGPLv3-compatible only).
10. Rewrite the body of a Locked spec, or go beyond a spec's scope without asking.
11. `git add -A`, merge work Shy hasn't approved (see Agent workflow step 9), or commit `*.db`, `bin/`, `obj/`, `.vs/`, `.idea/`, `tuvima_master.json`, `appsettings.*.json` with real keys, `.codex/`, `site/`, or QA screenshots (those stay in ignored `.tmp/`).
12. Present guesses as facts — name the options and ask Shy.

## Where things live

| Need | Read |
|---|---|
| Index of every doc and its status | `engineering/README.md` |
| Product overview and UX principles | `engineering/product/overview.md` |
| How the app is built, key patterns | `engineering/architecture/overview.md`; decisions in `engineering/architecture/decisions/` |
| Design system (colours, type, spacing, breakpoints) | `engineering/design/design-system.md` |
| Specs (source of truth for feature work) | `engineering/specs/` (template: `_template.md`) |
| Per-surface product behaviour | `docs/product/presentation-rules.md` |
| Subsystem deep dives | `docs/architecture/architecture-summary.md` → `docs/architecture/*.md` |
| Dashboard code map and UI rules | `src/MediaEngine.Web/CLAUDE.md` |
| Developer code tour | `AGENTS.md` (large — search it) |
| Detailed rules | `.claude/rules/` — `product-owner`, `model-routing`, `data-store`, `architecture-boundaries`, `security`, `build-and-ai-runtime`, `docs`, `dependencies`, `dashboard-surfaces` |
| Public docs site | https://tuvima.github.io/tuvima-library/ (source `docs/`, built by `website/`) |

## Agent workflow

1. **Specs are the source of truth.** Feature work starts from a spec in `engineering/specs/`. Only `Status: Locked` specs are built; Draft specs are for discussion. If there is no spec, plan first (`.claude/rules/product-owner.md`) and wait for "go ahead".
2. **Mark the start.** When work begins, set the spec's Status to `In progress` and update `engineering/README.md`.
3. **Plan waves.** Group the spec's numbered work packets into waves by their "depends on" lines. Run each wave in parallel, **at most 2 packets at once**, each in its own worktree.
4. **Route by size.** `Mechanical` packets → `mechanic` agent (Haiku). `Standard`/`Complex` packets → `builder` agent (Sonnet). Use `scout` to fill missing facts before briefing; never make a builder explore.
5. **After each wave:** merge the packets' work, run `dotnet build MediaEngine.slnx --no-restore` (the build-check hook also runs automatically when a turn ends with code changes), run the `reviewer` agent against the spec, and fix every must-fix before the next wave.
6. **Stop and ask Shy** when a spec is unclear, contradicts the code, or the work would go outside the spec's scope. Helpers stop and report; they never decide.
7. **Finish.** Run the full restore/build/test, update docs per `.claude/rules/docs.md`, set the spec's Status to `Built`, move it to `engineering/specs/archive/`, update the index, and give Shy a Plain English Summary.
8. **Save points.** On a feature branch: `git add <specific files>`, commit with a short summary ending in `Co-Authored-By: Claude <model> <noreply@anthropic.com>`, push the branch. Turn on Auto-fix for every proposed change as soon as it is opened.
9. **Merge without asking again.** Shy's approval of the work (a Locked spec, a "go ahead" on a plan, or a direct request) is the yes to merge it. When that work is finished, the CI-strength build (`dotnet build MediaEngine.slnx --no-restore --warnaserror --no-incremental`) and tests pass, CI is green, and the independent review the rules require has passed, merge it yourself: add the `ready-to-merge` label while checks are still running on a `claude/` branch, otherwise `gh pr merge N --merge --delete-branch`. This includes security, privacy and access changes that were part of the approved work. Ask Shy first only for changes Shy never approved: new scope, a decision the spec didn't make, or a security/access fix found along the way. Never leave approved work waiting for a second yes.
10. **Close out.** A session's last message is one of two things: a single explicit question Shy must answer (one decision, with a recommendation, in the next-step box), or a close-out (what merged, what was built, anything left in a follow-up card). With nothing waiting on Shy, merge, clean up the session's worktrees and branches, and archive the session (`archive_session` with `self`; sessions with a merged proposed change archive automatically). Never end on "waiting for approval" for approved work.

Escalate to a higher model only per `.claude/rules/model-routing.md`.

## Contacts

Product Owner: **Shy** · Code history: [github.com/Tuvima/tuvima-library](https://github.com/Tuvima/tuvima-library) · License: AGPLv3 · Brand assets: `assets/images/tuvima-logo.svg`, `tuvima-logo-dark.svg`, `tuvima-icon.svg`
