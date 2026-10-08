---
title: "Shared AI storage"
description: "Configure shared local AI runtimes and model storage for development without duplicating large native libraries."
audience: developer
category: guide
product_area: ai
status: current
---

# Shared AI storage

## In this page

Configure shared local AI runtimes and model storage for development without duplicating large native libraries.

## Where this lives in the code

- `tools/Install-AiRuntime.ps1`
- `config/ai.json`
- `src/MediaEngine.AI`

Local development loads large native AI libraries from a shared installation. Ordinary app and test projects reference only the managed wrappers. `Directory.Build.targets` rejects native AI payloads in their output folders, preventing the previous multiplication across projects and worktrees.

## Workstation setup

Set these process variables, or persist them as user environment variables and start a new terminal. `tools/Start-TuvimaApp.ps1` also reads persisted user values when the current process has no override.

```powershell
$env:TUVIMA_MODELS_DIR = 'E:\Resources\AI Models'
$env:TUVIMA_AI_RUNTIME_DIR = 'E:\Resources\AI Runtimes'
./tools/Install-AiRuntime.ps1 -RuntimeDirectory $env:TUVIMA_AI_RUNTIME_DIR -Cuda -Whisper
```

Omit `-Cuda` for a CPU-only installation and `-Whisper` when the optional transcription pack is not needed. The provisioning command restores pinned packages separately from the solution, installs only the requested platform, and verifies existing files on reuse. CUDA on Windows additionally installs NVIDIA's checksum-pinned CUDA 12.8.1 runtime and cuBLAS redistributables. GPU drivers remain system-managed. Linux CUDA deployments require their matching NVIDIA runtime dependencies from the platform installation; container builds remain CPU-only.

```text
E:\Resources\
  AI Models\
    llama\<model>.gguf
    whisper\<model>.bin
    .tuvima\             # Hardware state, leases, ownership manifests, staging
  AI Runtimes\
    llamasharp\0.27.0\runtimes\<rid>\native\...
    whisper\1.9.1\runtimes\<rid>\...
    nvidia\12.8.1\bin\...   # Windows CUDA dependencies
    staging\
```

Existing model folders for other applications are preserved. Do not put another `models` directory beneath `AI Models`. Each runtime component has a manifest describing its platform, version, files and SHA-256 hashes. Retain its native file layout, including companion DLLs/SOs and licenses. Runtime installations are immutable: provisioning refuses to overwrite an incomplete or different installation. Review and retire an unused CPU-only installation explicitly before replacing it with a CUDA-enabled bundle of the same version.

## Configuration and operation

`TUVIMA_MODELS_DIR` overrides `models_directory`; `TUVIMA_AI_RUNTIME_DIR` overrides `native_runtime_directory`. The native path must be absolute. An empty native setting selects the OS local-application-data directory under `Tuvima/AI Runtimes`. Environment values are not copied into portable configuration by AI settings saves. Storage path changes require an Engine restart and must be made in configuration or the process environment before restarting.

The loader checks the selected version and platform manifest before loading. Health diagnostics expose the actual backend, DLL path and version. Missing CUDA dependencies allow the installed CPU backend; a corrupt runtime manifest fails verification. An unavailable configured model volume never triggers downloads into another directory.

Models are protected by shared read leases while resident and exclusive leases during Tuvima downloads or deletion. Downloads use unique staging files on the same volume and publish only after integrity checks. Valid existing artifacts are reused. Models installed outside Tuvima are readable but are not automatically adopted or deleted by its download manager. Tuvima records ownership after its own downloads; explicit externally managed model cleanup stays with the operator.

## Verification

Run each backend probe in a separate process because native libraries are process-wide:

```powershell
dotnet run --project tools/AiRuntimeProbe -- --cpu
dotnet run --project tools/AiRuntimeProbe -- --cuda
dotnet run --project tools/AiRuntimeProbe -- --whisper
```

CPU/CUDA probes perform real text inference and print the actual loaded native module paths. The Whisper probe uses the managed wrapper's native runtime-information API and prints loaded module paths; it does not download an audio model or claim transcription validation. Normal unit tests use isolated temporary models and do not mutate the workstation's installed models. Two successive normal builds must leave no AI native payloads in app/test output directories.

## Deployment and cleanup

Docker uses the explicit `bundled` runtime mode and preserves `/models`. Windows installer builds use `TuvimaBundledAiRuntime=true`, which includes deployment-native assets and selects bundled loading by default. Shared development paths are not embedded in distributed binaries. A service using external storage needs access to both roots under its own identity.

After verification, remove obsolete build output and identical old model copies. Retire merged worktrees with Git after inspecting ignored files. Keep the two real repositories in Repos; temporary worktrees belong outside it and must be removed after integration. Preserve unique source and QA evidence separately rather than archiving multi-gigabyte build output. Treat Git object maintenance separately, preserving refs, stashes and normal recovery retention.

Run `tools/Get-AiStorageReport.ps1` to measure the configured model folders, staging, shared runtime and Git storage. It also reports whether the temporary-worktree limit of two has been exceeded. Review abandoned staging files when no installer or model download is active; the report never deletes data.

The September 2026 migration keeps recovery material in `%LOCALAPPDATA%\Tuvima\Recovery\2026-09-11-storage-cleanup`. Old social-project assets and ZIP archives are preserved there rather than deleted.

In product terms, development work reuses one AI installation instead of creating another copy for each task or test project.
