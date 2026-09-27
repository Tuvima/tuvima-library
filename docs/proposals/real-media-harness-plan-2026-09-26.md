# Real-media harness: protected existing-library ingestion

Status: implementation authorized and executed on 2026-09-26. This document records the original proposal; see `docs/reports/real-media-harness-2026-09-26.md` for the implementation, actual verification, and preservation incident. The original planning phase performed no reset or source mutation.

## Product walkthrough

1. **Use the real collection where it already lives.** Development Tools gains a Real media mode pointed at `C:\Temp\real_import`. Tuvima discovers the current folders and files as an existing library, similar to pointing Plex at an established collection. It does not connect to Plex or import a Plex database. Every original keeps its exact path, filename, contents, and source-authored tags. The review screen shows the selected folders, file inventory, protected status, and the separate location where Tuvima will store its own data. Acceptance: ingested media resolves to its original path, with no staging copy or renamed replacement. Technical work: W1–W3.

2. **Start with an empty development library, then ingest only this collection.** The action is “Reset library data & ingest real media.” Its preview lists the development content and generated data that will be removed. Old synthetic titles, progress, collections, View samples, and ingestion history disappear; existing accounts, profiles, permissions, provider setup, and application settings remain. Other configured sources are disabled for this run and subsequent real-mode restarts, so old test items cannot return. Originals anywhere on disk are preserved. Acceptance: every owned media asset in the rebuilt library traces to an approved folder under `real_import`; no synthetic fixtures are generated. Technical work: W1, W3, W4.

3. **Browse and use actual media through the usual product experience.** Open a movie from Watch, start playback, select an available subtitle track, seek, stop, and resume through Continue. Open a TV episode through its show, play music and an audiobook, and read an available supported ebook. Existing navigation, identity matching, review handling, and playback controls stay the same. Unsupported files remain untouched and receive an explicit outcome; the harness must not replace them with generated content. Acceptance: a report distinguishes successful ingestion from verified playback and records actual subtitle availability and rendering. Technical work: W4, W5.

4. **Personal media goes to View.** `personal_photos` and `personal_videos` become linked, read-only sources in the selected existing profile's Personal Space. They retain nested folders and use the real View indexing pipeline, without catalogue providers or synthetic location/people annotations. Nothing is submitted to Shared Library. Acceptance: supported personal media appears under the correct profile, and these files do not appear as catalogue movies or books. Technical work: W3–W5.

5. **Make preservation observable.** The run shows source verification alongside ingestion status. A baseline, live change monitoring, and final verification cover every original, including unsupported files and sidecars. Any content change, added file, deletion, rename, or protected metadata change fails preservation verification and stops further harness work. The report keeps evidence and identifies what changed without attempting to restore or overwrite originals. Acceptance: the successful run ends with zero unexplained source differences and zero source writes by Tuvima. Technical work: W2, W6.

Scope: update the existing development harness and its UI/script entry points, close protection gaps exposed by the run, perform one complete development library-state wipe, and ingest this real corpus only. Synthetic tests remain available as a separate explicit mode for regression work. Codec development, DRM removal, ISO conversion, Plex database integration, original cleanup, file relocation, and View sharing/AI recognition are outside scope. Unsupported functionality is reported honestly.

## Findings from repository and source inspection

Read-only filesystem enumeration on 2026-09-26 found 563 files, approximately 144.66 GiB. These are filesystem counts, not expected title counts; tracks, sidecars, and unsupported files must not inflate owned-work totals.

| Folder | Files | Approx. GiB | Observed extensions |
| --- | ---: | ---: | --- |
| audiobooks | 418 | 2.90 | 402 MP3, 8 JPG, 8 JSON |
| books | 19 | 0.02 | 5 EPUB, 1 AZW3, 6 JPG, 6 OPF, 1 `.fuse_hidden…` file |
| movies | 6 | 118.29 | 5 MKV, 1 ISO |
| music | 44 | 0.33 | 43 M4A, 1 MP3 |
| personal_photos | 52 | 0.16 | 37 JPG, 3 HEIC, 5 MOV, 7 MP4 |
| personal_videos | 3 | 4.91 | 2 MP4, 1 PDF |
| tv | 21 | 18.05 | 21 MKV |

No standalone subtitle files appeared in this inventory. Embedded subtitle streams have not yet been probed. Do not promise a subtitle success until a real usable track is identified. The PDF in personal_videos stays in that View source; extension alone must not reroute it into Read. The hidden-looking ebook file must be preserved and classified, not cleaned up.

Relevant existing implementation:

- `tools/Run-FullIntegration.ps1` invokes `/dev/integration-test`; `IntegrationTestEndpoints.cs` runs synthetic-oriented wipe/seed/validation. Its expectations cannot simply be reused for arbitrary real titles.
- `DevHarnessTab.razor` currently exposes synthetic reset/seed, rescan, and View fixture actions.
- `DevHarnessResetService.WipeAsync(Full)` calls `WipeAllSourcePaths`. It is categorically unsuitable for this task. Even generated-state wipe can delete tracked fixture files; the real path must never enter fixture cleanup.
- `PrepareForReingestAsync` clears caches and library data, but does not itself call `EnsureDestructivePathSafety`. Existing reset helpers also report some failures and continue. A protected run needs strict validation and failure propagation before scanning.
- `/dev/reingest-library` currently enumerates every configured library's `ScannableSources`, not a requested real-media allowlist. Its response means scan queued, not ingestion complete. Documentation describing managed-only scanning needs correction against the implementation.
- `SourceMutationPolicyGate` denies mutations and destination use for `ExistingLibrary`; `FileSourceMutationPolicyFactory`, `AutoOrganizeService`, and `WriteBackService` provide existing integration points. These are useful foundations, not proof that every direct filesystem write is protected.
- View has its own source indexing worker and authorization model; it must not pass through catalogue ingestion.

## Technical implementation

### W1 — Explicit real-media run contract and source isolation

Add a typed real-media run request/result in `DevHarnessContracts.cs` and a dedicated development endpoint/orchestrator, shared by the UI and PowerShell runner. Keep real and synthetic orchestration separate. Reject synthetic fixture selection and source-destructive wipe options in real mode, rather than silently interpreting them.

The request identifies the approved source root, selected View profile, and reset intent. The server derives the seven known child sources and validates them; it must not accept an arbitrary deletion root from the client. A preflight produces an immutable run manifest containing effective source IDs, paths, routing, output paths, reset scope, and configuration fingerprint. Execution revalidates that manifest to prevent stale previews.

Create a development configuration overlay that admits only the five catalogue source folders and two View sources. Preserve a backup of previous configuration outside the source tree, but do not automatically restore old source activation at completion. Persist the real-mode selection across restart. Disable legacy watch roots, scheduled scans, stale source registrations, synthetic seeding, and View fixture sources within this mode. Assert the effective worker source set before reset and again at startup. Use normal account/application authorization; development mode is not an authentication bypass.

### W2 — Source protection and integrity evidence

Set each catalogue source to `existing_library`, with all move, rename, delete, writeback, and destination permissions denied. Use View's linked read-only contract for personal folders. Validate that readers open originals only for reading. Audit organization, duplicate handling, quarantine/review, media editing, artwork/sidecar persistence, subtitle extraction/download, FFmpeg/transcode outputs, View indexing, cleanup, and legacy reset/seed endpoints for bypasses of the shared policy. Deny original mutation both when planning and immediately before executing it. Store metadata edits in Tuvima records only.

Every writable location—including database, artwork, logs, reports, thumbnails, subtitle cache, staging, transcoding, and temporary directories—must be explicitly outside the protected tree. Reject both ancestor and descendant overlaps for cleanup targets. Resolve Windows paths and account for case, separator boundaries, UNC/device aliases, junctions/symlinks, and hard-link aliases. Fail closed on unsupported alias situations; do not recurse through unvalidated reparse points. Never set source read-only attributes, modify ACLs, write probe files, or create marker files to test protection.

Before destructive work, start a source observer and create a baseline outside the source root: relative paths with original casing, directory entries including empty directories, file sizes, SHA-256 hashes, last-write/creation times, attributes, and readable security metadata. Stream hashes with bounded memory; report progress for this roughly 145 GiB corpus. A file changing while baselining makes preflight unstable and blocks reset. Last-access time is reported separately because ordinary reads can update it; it is not evidence of a Tuvima content write.

Keep monitoring through reset, ingestion, UI playback verification, and the active real-media harness session. Watcher notifications are advisory: handle buffer overflow as lost coverage requiring a full verification. Supplement with filesystem enumeration and final full hashes, including on failure/cancellation. Record denied application mutation attempts separately from filesystem differences; the observer cannot reliably attribute another process's writes to Tuvima. Monitoring detects changes; the enforced mutation policy prevents Tuvima writes. Never claim the observer alone makes the folder immutable.

### W3 — Full library-state reset without original deletion

Implement a named source-preserving reset path; do not use or alias `wipeScope=full`. Before reset, stop the running Engine and Dashboard development processes, install/validate the real-mode overlay, and start the controlled Engine. For later in-process resets, acquire an exclusive run lock and quiesce all ingestion, identity/enrichment, View indexing, playback/transcoding, and cleanup work. Watcher pause alone is insufficient. Any failure to reach idle aborts reset.

Inventory and back up the development database/configuration to a safe output location. Review the actual schema and enumerate the reset dependency closure: media/assets, canonical claims and relationships, structural groups, collections/playlists and media references, review entries, fingerprints/deduplication, personal progress/bookmarks/favorites, sequence overrides referencing removed series, View indexed assets/annotations/galleries/source jobs, ingestion jobs/operations/history, and derived caches. Preserve security/account/profile/configuration state and rebuild intended library grants against stable library identities. Explicitly reconcile preserved tables containing deleted content IDs.

Delete only Tuvima-owned derived files under validated output roots. Existing managed originals and old synthetic fixture files on disk are also left untouched; disable their sources so they cannot repopulate the database. Remove their catalogue/View records. Never trust a fixture manifest as authority to delete anything within `real_import`. Do not remove a whole output root if it also contains originals. Print exact cleanup targets and preserved source roots in preflight.

Use transactional database changes where supported. Treat cleanup/reset failures as failures, not successful messages with warnings. Do not enqueue ingestion after a partial reset. Leave workers paused on failure and preserve diagnostics; restore a database/config backup only while workers are stopped and after checking consistency. No recovery path restores, deletes, or overwrites original media.

### W4 — Real-only ingestion and truthful completion

Register and scan books, audiobooks, movies, music, and tv as distinct recursive existing-library sources with appropriate type hints. Keep stored asset locations at original paths. Do not infer title-specific exceptions or write generated provider hints beside files. Let normal processors read embedded metadata and supported existing sidecars, then use normal identification/enrichment and collection finalization.

Register both personal folders as linked sources under the explicitly selected existing profile and queue normal View reconciliation. Do not create a second Personal Space, submit contributions, or call View fixture services. Use folder identity to scope personal media, even when extensions resemble catalogue media.

Track every baseline file to a truthful outcome: catalogue asset, View asset, supporting sidecar/artwork, duplicate reference, unsupported/ignored file with reason, review required, or processing failure. Asset counts, track counts, episode counts, and work counts remain distinct. Discover actual codec/container support; do not treat ISO, AZW3, HEIC, PDF, or hidden files as automatically supported or automatically disposable.

Run through durable ingestion/identity/enrichment/organization and View job state. Completion requires no outstanding required work, a stable outcome ledger, and successful source verification. Provider waits, review needs, failures, cancellation, and timeouts remain visible. A queue submission or file scan completing is not run completion. Resume normal watching only against the validated real-mode allowlist; repeat scans and Engine restart must not create duplicate owned items or reintroduce fixtures.

### W5 — Harness experience and actual playback checks

Update `DevHarnessTab.razor`, its DTO/client mapping, and the PowerShell entry point to expose Real media and Generated fixtures explicitly. Real mode shows preflight inventory, reset boundaries, progress, protected-source status, and report links. Disable conflicting reset/seed actions during a run. Reuse shared controls and verify desktop/tablet/mobile alignment. Retain existing strong destructive-state confirmation, with copy stating that original files are preserved.

Probe real video/audio streams read-only and select representative playable files by observed capabilities. Store probes in the report output. Verify browser playback, moving picture and audible audio, duration, seeking, audio-track selection where present, pause/resume persistence, TV episode targeting, and direct-play or transcoding behavior. Validate streaming/range requests and cancellation cleanup as relevant to the actual playback path.

For subtitles, verify enumeration, language selection, enabling/disabling, visible rendered cues, seeking synchronization, and supported conversion/burn-in paths where available. Extract/convert only into Tuvima cache. If the corpus contains no usable track, record the subtitle test as blocked by corpus coverage; do not manufacture a subtitle or claim success. Provider acquisition, if already enabled, must also write only to cache. Unsupported codecs/subtitle formats produce explicit gaps.

Also verify one supported ebook, music playback and album grouping, audiobook track order/progress with source-authored titles, and View image/video display and folder navigation. Use the real profile permissions. Correlate every selected item with its source-manifest entry. Do not apply fixed synthetic title/QID/count expectations or require all real titles to resolve perfectly.

### W6 — Regression coverage, run report, and execution

Add behavioral tests using disposable test folders for source-mutation denial, cleanup overlap/alias rejection, stale manifests, failed quiescence, worker races, View routing, real-mode seed rejection, persistent source isolation, cancellation, restart, and database reset reference integrity. Inject failures and verify no scan follows an incomplete reset. Test integrity detection for edits, timestamp-only changes, additions, renames, deletions, and observer overflow. Never use real originals for mutation tests.

Extend the existing harness/reset and Dashboard tests; run relevant .NET suites and build affected projects. Follow the repository's .NET SDK, local Wikidata feed, and shared AI runtime configuration. Update `docs/guides/running-tests.md` to document real versus synthetic behavior, authentication, reset scope, and the fact that queued scans are not completed runs.

After protection checks pass, execute the full run in this order: preflight and source baseline; validated state reset; source registration; catalogue/View ingestion; durable completion accounting; real playback and subtitle checks; final source verification; Engine restart/rescan isolation check and repeat preservation verification. Keep monitoring for the active harness session and write a final closeout check when it stops.

Write local JSON and readable reports outside the originals, containing run/config identity, reset targets, source inventory and hashes, stage timing, per-file outcomes, identity/review summaries, playback/subtitle evidence, source diffs, denied writes, and unresolved issues. Keep personal filenames and provider credentials out of committed/public artifacts. Do not erase the run's safety evidence while clearing old logs.

## Delivery order and acceptance gate

Implement W1/W2 first, W3/W4 next, then W5 and W6. No actual wipe is attempted until path validation, mutation-denial tests, worker quiescence, and baseline capture pass. This plan authorizes no execution by itself; a subsequent implementation task carries out the work and the requested development-state reset.

The run is accepted only when:

1. All 563 currently observed files, or an explicitly reviewed newer inventory, are accounted for with zero unexplained source differences.
2. Every owned asset originates from the approved real source set; old synthetic items do not return after restart/rescan.
3. Original folders, filenames, bytes, and protected metadata remain unchanged; all derived writes are outside the protected tree.
4. Supported catalogue items and personal media reach their correct surfaces, with unsupported/review/failed outcomes visible and no fabricated replacements.
5. A real movie and TV episode demonstrably play; subtitles are demonstrated when the corpus provides a supported track. Missing capability is reported as an unmet criterion, not hidden by overall ingestion success.
6. Required background work has finished or is explicitly reported blocked/failed, repeat ingestion is idempotent, and the final report is available.

## Plain-English completion summary

The planned update replaces generated test content with your actual collection while leaving every original in its current location. It clears Tuvima's development library records and rebuilds them from these folders alone. Movies and subtitles are checked through actual playback, personal photos/videos use View, and before/after verification makes any source change visible. This document is the plan only; your library has not been wiped or ingested.
