# Shared AI storage and repository cleanup

Status: implemented on this Windows workstation on September 11, 2026. The original phased plan is retained below this completion record.

## Completion record

- Repos now contains exactly `tuvima-library` and `tuvima-wikidata`. All 25 secondary registered worktrees and the additional stale working folder were retired after inspection and preservation of unique material. Git now lists only the main checkout.
- The verified text model lives at `E:\Resources\AI Models\llama\Qwen3-1.7B-Q5_K_M.gguf`. Both identical repository-local copies were removed after real inference passed. Existing `AI Models\LLMs` and `AI Models\Media` content belonging to other applications was preserved.
- Shared native installations are under `E:\Resources\AI Runtimes`: LLamaSharp 0.27.0, optional Whisper 1.9.1, and Windows NVIDIA CUDA 12.8.1 redistributables. Persistent user environment variables select both E: roots; the normal launcher honors them.
- Ordinary builds no longer reference native AI payload packages. A build guard rejects their reintroduction into app/test outputs. Explicit bundled deployment modes remain available. Shared model mutations use leases and ownership checks; runtime installation uses version/platform/hash manifests.
- Removed obsolete generated build output. Ordinary Git maintenance reduced `.git` from approximately 14.1 GiB to 1.365 GiB without immediate pruning. OneDrive read-only flags on stale worktree metadata were cleared after backing it up; Git then pruned the obsolete registrations successfully.
- C: free space increased from **84.68 GiB to 233.07 GiB**, an observed gain of **148.39 GiB**. This is a drive-wide free-space observation; logical file totals and concurrent OS activity can differ.

| Retained area | Final logical size |
| --- | ---: |
| Main Library repository, including Git and current build/data files | 7.434 GiB |
| Git data, included in the preceding row | 1.365 GiB |
| Shared text model on E: | 1.171 GiB |
| Shared AI runtimes on E: | 1.247 GiB |
| Recovery material on C: | 0.309 GiB |

Recovery is at `C:\Users\shaya\AppData\Local\Tuvima\Recovery\2026-09-11-storage-cleanup`. It contains the cleanup inventory, unique source/QA evidence, stale Git metadata, the retired social project and both Tanaste archives. Recovery has not been scheduled for automatic deletion.

### Validation result and limits

- Solution restore and final Debug build passed; build reported zero warnings and errors. All 55 AI tests and all 51 contract tests passed. Strict documentation build passed.
- CPU and CUDA probes completed real text inference using the E: model. CUDA was validated on the RTX 5080 with actual loaded native dependency paths outside Repos. Whisper's managed wrapper loaded its shared native runtime and returned runtime information. No optional audio model was downloaded, so transcription was not exercised.
- Full solution tests reported 3,647 passed, 34 skipped and two failures. The unrelated SQLite cleanup-lock failure in `ConnectedServiceSubscriber_StopsAfterExactCredentialRevocation` passed on isolated retry. The existing silent-catch guardrail failure in unchanged `SetupPage.razor` remains; the whole suite is therefore not claimed green.
- Repeated normal builds left no LLamaSharp/ggml/Whisper native payloads or model weights in app/test outputs. Repeated provisioning reused the verified shared installations. Engine and Dashboard startup/health checks passed with the normal launcher.
- Windows local development was exercised. Docker image execution, installer deployment, other operating systems, service-account permissions, and audio transcription remain deployment-specific verification, not completed claims.
- The two-temporary-worktree maximum is documented policy with a reporting warning, not a filesystem quota. Version retirement and recovery cleanup remain explicit operator actions; no recurring cleanup automation was created.

See [shared AI storage guide](../../docs/guides/shared-ai-storage.md) for setup and the read-only storage reporting command.

## Outcome

Keep only `tuvima-library` and `tuvima-wikidata` in the existing Repos directory. Store large AI model files once under the user-selected `E:\Resources\AI Models`, with versioned native AI runtimes in the sibling `E:\Resources\AI Runtimes` directory. Both locations are outside the repositories and OneDrive. App and test builds load those runtimes from that location rather than copying them into every output directory.

This is local library loading within the Engine process; it does not introduce a separate AI server or network dependency. NVIDIA drivers remain installed and maintained by the operating system.

## Findings from the September 11 audit

| Area | Approximate logical file size | Disposition |
| --- | ---: | --- |
| 23 sibling `tuvima-library-*` working folders | 105.2 GiB | Review and retire; 101.5 GiB is generated bin/obj output |
| Three worktrees inside `.codex-runlogs/worktrees` | 23.7 GiB | Review and retire after validating replacement runtime loading |
| Main `.git` directory | 14.1 GiB | Separate Git-aware maintenance; never manually remove objects |
| Remaining main-repository files | 20.6 GiB | Retain source/data; clean obsolete output and migrate verified models |

The first two rows total about 128.9 GiB before subtracting anything preserved or staged elsewhere. Do not count the 101.5 GiB again. Actual reclaimed local disk space depends on OneDrive allocation and retained recovery material.

All 25 registered secondary worktree HEADs are ancestors of main. Status commands returned no ordinary changed/untracked entries, but warned that a user-level ignore file was inaccessible. Repeat a full inventory, including ignored files and active task/process associations, before removal. The additional `tuvima-library-settings-advanced` folder has a Git pointer but was absent from the worktree listing and needs separate inspection.

Current code already honors `TUVIMA_MODELS_DIR` in `TuvimaAiServiceCollectionExtensions`, and `ModelInventory` resolves model files under the configured directory. However, `Start-TuvimaApp.ps1` clears that environment variable. Native-library health checks search only `AppContext.BaseDirectory`. LLamaSharp CPU/CUDA and Whisper runtime packages inject build content, which explains why simply moving DLLs would not stop future duplication. CUDA libraries of roughly 500 MB are repeated across app/test outputs and operating systems.

## Target layout

```text
C:\Users\shaya\OneDrive\Documents\Source\Repos\
  tuvima-library\
  tuvima-wikidata\

E:\Resources\
  AI Models\                 # Model weights; no redundant models subfolder
    llama\
      Qwen3-1.7B-Q5_K_M.gguf
    whisper\
    .tuvima\
      manifests\
      staging\
  AI Runtimes\               # Native libraries, separate from model weights
    llamasharp\0.27.0\<package-compatible platform/backend layout>\
    whisper\1.9.1\<package-compatible platform/backend layout>\
    nvidia\12.8.1\bin\       # Shared Windows CUDA/cuBLAS dependencies
    staging\

C:\Users\shaya\AppData\Local\Tuvima\
  Worktrees\                 # Temporary isolated work only when needed
  Recovery\                  # Small, inventoried recovery material
```

`AI Models` is already the model root: use `llama` and `whisper` directly beneath it, matching the existing model inventory convention. Do not add another `AI`, `models` or app-specific container between that root and the model families. Keep Tuvima's model-installation manifests and staging in `.tuvima`; preserve existing verification metadata required by the downloader. Runtime manifests and staging belong under `AI Runtimes`. Inventory any existing files first and preserve models or runtimes owned by other applications.

Configure this workstation with `TUVIMA_MODELS_DIR=E:\Resources\AI Models` and the proposed `TUVIMA_AI_RUNTIME_DIR=E:\Resources\AI Runtimes`. Paths must remain configurable; these are workstation settings, not hardcoded application defaults. Quote paths correctly in launch/provisioning commands. A Windows Service deployment needs access to both configured roots under its service identity. If E: is unavailable, report the configured storage as unavailable rather than silently downloading a second copy onto C:. Containers retain `/models` and receive a Linux runtime installation in their image or configured mount.

## Phase 1: install a shared, versioned runtime

1. Add an idempotent provisioning command that reads pinned versions from `Directory.Packages.props`. Install the current OS/architecture's LLamaSharp CPU runtime, CUDA 12 runtime when supported, and the optional Whisper CPU runtime when enabled. Do not reintroduce the deliberately excluded Vulkan backend.
2. Preserve package-specific directory layouts and all companion libraries. LLamaSharp and Whisper must have separate package roots so similarly named native dependencies cannot overwrite one another.
3. Stage downloads/extraction under `E:\Resources\AI Runtimes\staging`, verify provenance and checksums, and atomically publish a completed manifest. Keep staging and final installation on the same volume. Record package version, platform, backend, relative paths, hashes and byte totals. Re-running provisioning must reuse verified files.
4. Keep installations immutable and resolve the version required by the managed wrapper. Do not load a newer native ABI merely because it is present. Keep an older version only while a supported deployment or active checkout needs it, or during a bounded rollback period.
5. Reuse the existing global NuGet cache as a provisioning source where practical. NuGet may retain one shared package archive/extraction; the objective is to eliminate copies per project and worktree, not promise that package-manager storage disappears.

## Phase 2: make Engine loading and configuration explicit

1. Keep `TUVIMA_MODELS_DIR` for models. Introduce a proposed `TUVIMA_AI_RUNTIME_DIR` and corresponding `native_runtime_directory` AI setting for native runtimes. Environment overrides configuration; shared local defaults must be independent of the current working directory.
2. Add a common runtime resolver/bootstrap in `MediaEngine.AI`. Configure LLamaSharp's version-supported native search/loading API and Whisper's `RuntimeOptions.LibraryPath` before any native initialization, benchmarking, inference or health probing. Verify dependency resolution as well as the main DLL/SO.
3. Make backend selection and health checks use that same resolved installation. Hardware detection alone is insufficient: prove that the selected compatible backend can load. Preserve a tested CPU fallback when CUDA is unavailable; a malformed explicit configuration must produce a clear diagnostic rather than silently picking an unrelated binary.
4. Report the actual loaded backend, version and location in diagnostics. Do not scan arbitrary descendants of the executable directory to choose a runtime.
5. Preserve model/runtime overrides in `Start-TuvimaApp.ps1`; align direct `dotnet run`, test gates, service startup and Docker paths. Require restart when changing native-runtime location/version because these libraries are process-wide once initialized.
6. Carry new settings through schema, validator, contracts, API mappings and settings persistence. Ensure saving unrelated AI settings does not discard the effective path or write a workstation-specific environment override into portable config.

LLamaSharp 0.27.0's installed XML documentation exposes `WithLibrary`, `WithSearchDirectory` and `WithSearchDirectories`. Whisper 1.9.1 exposes a custom library path and requires configuration before factory creation. These establish feasibility; an actual native-load spike is the first implementation gate.

## Phase 3: prevent build copies from returning

1. Separate native runtime provisioning from ordinary managed projects. Retain managed LLamaSharp/Whisper references; isolate native package restore and content-copy behavior in the provisioning path.
2. Inspect and suppress the native packages' imported build/content behavior explicitly. `ExcludeAssets=runtime` alone is not sufficient evidence: Whisper's package also declares copy-to-output items in build targets. Do not remove all native files globally; unrelated SkiaSharp/SQLite/media dependencies must continue to work.
3. Ensure transitive project references and test projects cannot copy these AI runtime payloads back into bin/obj. Ordinary builds should not provision or download large assets as a side effect. Provide one explicit setup command and an actionable missing-runtime message.
4. Make shared external runtime loading the local development path. Release packaging must explicitly install the target platform runtime once per deployment. Preserve Docker's CPU-only `TuvimaContainerBuild` behavior and existing model volume.
5. Add a build-output check for forbidden AI payloads and inspect both Debug and Release outputs. Verify a second clean build produces no additional shared runtime/model files.

## Phase 4: migrate existing models safely

1. Stop Engine/Web and inventory model files, verification metadata and active configuration. Compare hashes for the two observed copies of `Qwen3-1.7B-Q5_K_M.gguf`; matching filenames alone are insufficient.
2. Copy one verified copy of each required artifact into `E:\Resources\AI Models\llama` or `E:\Resources\AI Models\whisper`, switch configuration, then prove readiness and real inference from that location before deleting source copies. Treat C:-to-E: migration as copy, destination hash verification, configuration switch and later source deletion; a cross-volume move is not atomic. Reuse an existing matching destination artifact and never overwrite a different file solely because its name matches.
3. Add interprocess coordination to shared downloads and mutations. The existing download manager uses in-process locks and a shared `.downloading` path; separate Engine instances must not overwrite partial downloads, delete an active model or replace one another's artifacts. Use per-artifact leases, unique staging files under `E:\Resources\AI Models\.tuvima\staging` and atomic completion on E:. Restrict automated deletion to explicitly Tuvima-managed artifacts; an artifact potentially shared with another application must be preserved unless its removal is explicitly requested.
4. Keep tests isolated for destructive download/delete behavior. Native smoke tests can read shared artifacts, but must not delete or modify the workstation's installed models.

## Phase 5: retire workspaces and clean Repos

1. Generate a dry-run manifest of exact absolute paths, sizes, Git status, ignored local data and intended action. Exclude both valid repository roots, the Wikidata local package feed, runtime data and media originals from recursive cleanup.
2. Recheck the 25 registered secondary worktrees for active tasks/processes, unique commits and all local files. Preserve unique material outside Repos, then remove completed worktrees through `git worktree remove` without force. Investigate the stale-pointer folder separately; do not force deletion past unexplained contents.
3. Remove obsolete bin/obj and test/build scratch output from the retained main repository after external loading passes. Keep relevant diagnostics and current configuration/data.
4. Relocate the retired `tuvima_social` folder and both `Tanaste` ZIP archives into an inventoried archive outside Repos. This leaves exactly two folders without assuming old assets are disposable. Do not archive generated multi-gigabyte build directories.
5. Inspect Git object/reflog/snapshot retention separately. Preserve required commits and recovery references, then perform ordinary Git maintenance with enough free working space. Do not use aggressive immediate pruning or promise a fixed saving from the 14.1 GiB store.
6. Refresh saved project/task locations and document the rule: use the existing checkout for sequential work; any necessary isolated worktree lives outside Repos and is retired after integration. No new sibling task folders or nested `.codex-runlogs/worktrees`.

## Validation and completion criteria

- Run an early subprocess smoke test loading the shared CPU/CUDA libraries, including companion dependencies. Verify the actual loaded module paths are outside every repository/bin directory. Test backends in separate processes to avoid process-wide native initialization contamination.
- Run real text inference from the shared model; validate CUDA on this machine and CPU fallback independently. Run a short Whisper transcription when the optional pack is installed.
- Verify absent, unreadable, corrupt, wrong-version and wrong-architecture installations produce actionable diagnostics. Test the actual paths containing `AI Models` and `AI Runtimes`, unavailable E: storage, launch from another working directory, and service-account access where applicable. Verify unavailable configured storage does not recreate repository-local model/runtime directories.
- Test concurrent provisioning/download coordination, cancellation recovery and safe model deletion. Native unit tests use fakes or isolated fixtures; shared installed assets remain intact.
- Run required `dotnet restore MediaEngine.slnx`, `dotnet build MediaEngine.slnx --no-restore` and `dotnet test MediaEngine.slnx --no-build`; run relevant Docker/docs checks. Visually validate any changed Settings surface.
- Build twice and compare output and shared-store inventories. No LLamaSharp/CUDA/Whisper native payload copies appear in ordinary app/test outputs; no duplicate model appears under either repository.
- Record before/after logical bytes and actual free space separately for C: and E:, including temporary recovery material. Distinguish bytes relocated to E: from duplicate bytes eliminated. Repos contains only the two valid repositories; retired worktrees are unregistered.

Retain source model copies until shared inference succeeds, then remove them. If validation fails before cleanup, restore the previous path/build configuration. Keep a bounded recovery manifest and required commits rather than a full second copy of all generated files.

## Growth policy

Keep one installed native bundle per required version/platform/backend and one verified model artifact shared by its roles. New model downloads show their size and check available space. Provisioning reports total installed bytes and obsolete versions; removal respects active leases and configured deployments. Limit temporary worktrees to two and remove them when integrated. Keep recovery material for a proposed seven-day window after successful validation, with no automatic deletion of unresolved unique work. A storage audit should report growth in AI, worktrees, build outputs and Git separately.

## Sources

- Local code: `MediaEngine.AI.csproj`, `GpuBackendDetector`, `ModelInventory`, `ModelDownloadManager`, `TuvimaAiServiceCollectionExtensions`, `LaunchDependencyHealthChecks`, `Start-TuvimaApp.ps1`, Dockerfile and compose configuration.
- Installed package documentation and build assets: LLamaSharp 0.27.0 and Whisper.net/Whisper.net.Runtime 1.9.1.
- [Whisper 1.9.1 runtime configuration](https://github.com/sandrohanea/whisper.net/blob/1.9.1/Whisper.net/LibraryLoader/RuntimeOptions.cs).

## Product-owner summary

Tuvima now keeps model files in `E:\Resources\AI Models` and shared native dependencies alongside them in `E:\Resources\AI Runtimes`. Development builds reuse this installation, and a build check prevents the old duplication from returning. Cleanup freed approximately 148 GiB on C: and left only the Library and Wikidata repositories in Repos. Unique old work was preserved in a small recovery folder.
