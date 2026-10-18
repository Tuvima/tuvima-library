---
paths:
  - "Directory.Build.props"
  - "Directory.Build.targets"
  - "Directory.Packages.props"
  - "global.json"
  - "**/*.csproj"
  - "tools/**"
  - "scripts/**"
  - "src/MediaEngine.AI/**"
  - "Dockerfile"
  - "docker-compose.yml"
  - ".github/workflows/**"
---

# Build outputs and local AI runtime

- Normal builds/publishes select only the explicit runtime/SDK host and its RID fallbacks — keep that filtering on (`Directory.Build.targets` fails the build if another platform's runtime or AI native DLLs land in `bin`).
- QA outputs live in ignored `.tmp/`. After stopping Engine/Dashboard, clean with `pwsh -File tools/Clean-RepoOutputs.ps1` (`-WhatIf` previews; `-IncludeQa` only for its listed QA folders). See `docs/guides/repository-storage.md`.
- Local AI: native runtimes load from `TUVIMA_AI_RUNTIME_DIR`, weights from `TUVIMA_MODELS_DIR` (workstation: `E:\Resources\AI Models` and `E:\Resources\AI Runtimes`); provision with `tools/Install-AiRuntime.ps1`. Never copy native AI packages back into build outputs, restore native backend packages to ordinary app/test references, copy model weights into repositories, or remove the build-output guard. Bundled deployment assets require `TuvimaBundledAiRuntime=true` (Docker uses `TuvimaContainerBuild=true`). See `docs/guides/shared-ai-storage.md`.
- Shared models use interprocess leases; tests must use isolated temporary model files.
- Keep only `tuvima-library` and `tuvima-wikidata` in Repos. Use the existing checkout for sequential work; temporary worktrees belong outside Repos, with at most two active; retire them after integration, preserving unique files first. Never create sibling task folders or nested `.codex-runlogs/worktrees`.
- CI (`.github/workflows/ci.yml`) builds with `--warnaserror` (including the out-of-solution `benchmarks/MediaEngine.Storage.Benchmarks` and `tools/MediaEngine.PerformanceProbe`), runs `dotnet format --verify-no-changes`, tests with `--filter "Category!=LiveProvider"` plus coverage, node tests, a Release Dashboard publish with `scripts/build/verify-dashboard-css.mjs`, and a vulnerable-package scan. Match it locally before calling work done.
