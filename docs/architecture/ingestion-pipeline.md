---
title: "Ingestion Pipeline"
description: "Deep technical documentation for file watching, fingerprinting, staging, browse surfacing, promotion, and organization."
audience: "developer"
category: "architecture"
product_area: "ingestion"
tags:
  - "ingestion"
  - "pipeline"
  - "watchers"
status: current
---

# Ingestion Pipeline

## In this page

Deep technical documentation for file watching, fingerprinting, staging, browse surfacing, promotion, and organization.

## Where this lives in the code

- `src/MediaEngine.Ingestion`
- `src/MediaEngine.Processors/MediaProcessorRouter.cs`
- `src/MediaEngine.Storage/IdentityJobRepository.cs`

## Durable Operation Tracking

Ingestion no longer relies only on in-memory watcher/debounce/worker state for
queue visibility. When a file is discovered, Tuvima Library creates or reuses a
durable `media_operations` row with `operation_type = ingestion.file`.

The operation stage is updated as the item moves through discovery, settling,
lock checks, queueing, hashing, parsing, scoring, registration, and identity
queueing. The live ingestion page reads these durable rows, so a restart can
still show what was waiting, running, interrupted, blocked, or failed.

Durable ingestion status is the source of truth for:

- queued files
- queue position
- active stage
- retry and blocked state
- interrupted work after restart
- per-file rows

The legacy ingestion log can still exist as historical/support data, but product
status should use `media_operations`.

The retail, Wikidata bridge, and quick hydration workers remain durable pollers,
but they also use an in-process signal to wake as soon as upstream work is
created. Timed polling remains the fallback after restarts or missed signals.
Identity retry settings live in `config/hydration.json`:
`identity_retry_max_attempts`, `identity_retry_base_delay_seconds`,
`identity_retry_max_delay_seconds`, `identity_retry_jitter_min_ms`, and
`identity_retry_jitter_max_ms`.

## Storage Reset And Reingest

The current storage epoch is `guid-blob-v1`. Internal IDs are stored in SQLite as
16-byte BLOB GUIDs, while API contracts still expose GUIDs as strings. Legacy
TEXT-GUID databases are rejected at startup unless `TUVIMA_STORAGE_RESET=1` (or
`destructive-reingest`) is set, in which case the old database files are renamed
as backups and a clean database is initialized.

For normal development rebuilds, `POST /dev/reset-and-seed` pauses file watching,
clears catalogued library/database/cache/artwork state while preserving accounts,
profiles, access rules, settings, configured libraries, and provider configuration,
recreates the selected Standard or Stress fixtures, queues them through grouped
normal ingestion scans, and resumes watching. `POST /ingestion/rescan` remains the
canonical no-wipe operation for configured source media.

A reset deletes only files the Library created, so it works when the source folders sit
inside the library folder (the default layout). Seeding records each fixture's path; before
clearing the data store the reset follows each one through `ingestion_log` (the path a file
was first found at) to its media asset's current path, so a fixture is found after
ingestion filed it. It deletes those files, the subtitle and lyric files the Engine wrote
for them (never user-owned tracks), leftover `.tuvima.bak` copies and the folders they
leave empty, then clears the regenerable folders in `.data` (assets, staging, generated,
sidecars-cache, variants).
It deletes only inside managed, writable source folders and the library folder, never in
read-only folders, the Import folder, the photo space or the data store, and it lists the
files it kept because the Library did not create them. The full wipe (`wipeScope=full`),
which empties every managed source folder, refuses when one of them holds the Library's
data folder, the photo space or the Import folder. Development resets are refused while
real-media protection is on. Before deleting anything, a reset waits at most 20 seconds
for background enrichment workers to stop and at most 20 seconds for the data store's
write lock. If either does not let go, the reset stops with nothing deleted, answers `409`
with a message naming what is blocking it, and turns file watching back on.

Fresh-ingest validation is the proof path for sequence, artwork, and attribution
changes. Do not repair bad historical rows in place for these fixes. Stop the
Engine and Dashboard, wipe only the configured validation roots, start both apps
again, ingest the representative fixtures, then validate database/API state
before visual Dashboard checks.

Validation roots:

- `C:\temp\tuvima-watch\books`
- `C:\temp\tuvima-watch\audiobooks`
- `C:\temp\tuvima-watch\tv`
- `C:\temp\tuvima-watch\movies`
- `C:\temp\tuvima-watch\music`
- `C:\temp\tuvima-watch\comics`
- `C:\temp\tuvima-library`

This document describes how Tuvima Library discovers, processes, organises, and stages media files - from the moment a file appears in a watched folder to the moment it is promoted into the organised library.

---

## Libraries and sources

The Engine is configured with logical **Libraries**, each declaring:

| Field | Description |
|---|---|
| `kind` | `catalogued` or `personal` |
| `area` | Read, Watch, Listen, or View |
| `metadata_policy` | Enriched, local-preferred, local-only, or manual |
| `sources` | Stable per-folder source identities and safety policy |
| `primary_destination_source_id` | The one managed folder new media goes into (null when there is none); a read-only folder is never one |
| `accepted_intake_modes` | The direct intake mechanisms accepted by this library |

Configuration lives in `config/libraries.json`. Schema 7 is required (older files are refused, not converted); obsolete top-level watch folders, flat source paths, read-only flags, and the removed per-folder role, intake, write-back and "accepts new media" fields are rejected.

File watching is source-folder aware. Watcher noise is buffered for `Ingestion:FswQuietPeriodSeconds` seconds, defaulting to 30 seconds, before the buffered files are released to the debounce queue; each file then has its own `ingestion.file` operation and identity job, and nothing groups files into a run.

**Watched folders follow the saved library folders.** The watchers are not built once at startup. `IngestionEngine` subscribes to `IOptionsMonitor<IngestionOptions>`, which re-reads `libraries.json` whenever it is saved (`LibrariesOptionsChangeTokenSource`), and `IngestionEngine.WatchedFolders.cs` reconciles the running watchers against the new folder list under one lock, so rapid saves cannot race. A folder that was added starts being watched (log line `Watching:`) and gets an initial scan of just that folder with the same known-path pre-filter as the startup scan, so files already ingested are not queued again. A folder that was removed stops being watched (`Stopped watching:`) and its polling-fallback state is dropped. A folder whose path or `include_subdirectories` setting changed is stopped and started again and re-scanned. Unchanged folders keep their watcher untouched. A folder that already has a watcher the engine did not start is kept when its subfolder setting matches and restarted when it does not. `IFileWatcher.RemoveDirectory` stops one folder without disturbing the others, and an event already in flight for a removed folder is dropped. A watched folder is only read: a missing read-only folder (an offline drive, a mistyped path) is logged and left unwatched rather than created, and only a managed writable folder or the Import folder may be created when missing. The source-mutation gate is unchanged.

Only the folder list and the Import folder move live. The engine keeps a separate, replaceable copy of the folder settings for this; every other ingestion setting (duplicate policy, library root, auto-organise, write-back, lock-probe and poll timings) is still read once at startup by the engine, exactly as before, and changing them still needs a restart. (Other services that read options through `IOptionsMonitor`, such as the file-source policy and the media-type resolver, already followed saves.) If a save cannot be turned into options (for example two folders overlap), no change reaches the engine and the watchers keep the folders they had until a valid save arrives; if such a save was made before the engine began listening, startup logs a warning and keeps the folders it started with.

The polling fallback is started once at startup, and only when `Ingestion:PollIntervalSeconds` is above zero; a save never starts or stops it, it just sweeps whatever the current folder list is each time it runs. A folder skipped because it was missing is therefore swept only if the fallback is running, and it gets a real watcher on the next save (or restart) after it becomes reachable.

```json
{
  "schema_version": "8.0",
  "file_handling": { "duplicate_policy": "keep_one" },
  "libraries": [
    {
      "id": "44444444-4444-4444-8444-444444444444",
      "name": "Movies",
      "category": "Movies",
      "kind": "catalogued",
      "area": "watch",
      "presentation": "catalogue",
      "metadata_policy": "enriched",
      "media_types": ["Movies"],
      "sources": [{
        "id": "44444444-aaaa-4444-8444-444444444444",
        "path": "/media/library/movies",
        "management_mode": "managed_by_tuvima",
        "source_type": "local_folder",
        "include_subdirectories": true,
        "access_mode": "writable",
        "conversion_state": "none",
        "merge_state": "none"
      }],
      "primary_destination_source_id": "44444444-aaaa-4444-8444-444444444444",
      "accepted_intake_modes": ["incoming_folder", "browser_upload"],
      "organization_policy": { "mode": "tuvima_standard", "write_tags": true }
    }
  ]
}
```

The `category` tells the Engine where to organise files on disk. The `media_types` tell it how to process them - which processor to use, which metadata providers to query, and what confidence prior to apply during identification. A library folder designated for Movies gives any MP4 it finds an 0.80 prior confidence for that media type, skipping much of the heuristic disambiguation that would otherwise be needed.

---

## Ingestion Snapshot

The Dashboard's live ingestion page and the menu indicator read three Engine views, all built from the per-file rows (`ingestion_log`, `media_operations`, `identity_jobs`, `review_queue`) and never from a run or batch record:

- `GET /ingestion/queue-progress` (and the `IngestionQueueProgress` event) is the one source for the bar, the tally by type and the state (`working`, `waiting`, `idle`). `IngestionQueueProgressService` works it out once and the Dashboard only displays it.
- `GET /ingestion/presentation` is the page projection: `current_media` is the groups in flight, `recent_days` is what became Ready on each recent day, and `attention` and the group counts sit beside them.
- `GET /ingestion/recent-additions` is the paged Recently Added list, and `GET /ingestion/media-groups/{groupId}` (with `/children`) feeds the drawer.

**A group is its structural identity alone.** A TV show, an album, a comic series or a multi-part audiobook is one group however many intakes its files arrived in or how often they were scanned again. The newest ingestion log row of each file and the newest `ingestion.file` row of each file decide what the page shows for it.

**In flight** means a group is not Ready and not failed (its availability is adding, waiting for tools, finishing or in review), or one of its files still has an open `ingestion.file` row. **Added** means a Work has a `first_ready_at` time, and the day comes from that time, so a group appears under the day it became Ready whatever else happens to its files later. The Ready rule itself is `WorkReadinessSql`.

The Engine no longer keeps a separate "Operations" snapshot: the old `GET /ingestion/operations` stage-by-stage view and its recent-batches list were removed with the batch screens (spec `ingestion-and-readiness`, D12 and D14).

Music remains a conservative organization lane. The status surface emphasizes tag/fingerprint-first handling and preserving album folders instead of implying aggressive rename/move behavior.

---

## Intake Modes

### Library source monitoring

The Engine monitors every catalogued library source for new files. Existing-library sources are indexed in place and remain read-only. Managed writable sources may organize files when their source and library policies allow it.

The `.staging/` directory within the library root is excluded from library source monitoring to prevent re-ingestion loops.

### Import folder

The optional Import folder (storage location `incoming`) is watched like a library source but is not one: it is never a destination and no library claims it. `ImportFolderService` answers every question about it from the current configuration, so a change applies without a restart: where the folder is, which media type a per-type folder implies (`Movies`, `TV`, `Music`, `Books`, `Audiobooks`, `Comics`), and the one managed folder marked New media for a type (checked right now for being reachable, writable and roomy enough; a read-only folder is never returned and there is no fallback).

A file takes the same path as every other file (Settle, Lock, Fingerprint, Scan, Identify), with these differences:

- **Type prior.** The per-type folder gives the file a strong media type prior and its type's library (`MediaTypeResolver`, `IngestionEngine`). A file at the root is classified from its content; an ambiguous one goes to Review as a root drop. Files inside the Import folder's `.data` working folder are never ingested.
- **Filing.** After identity matching, `AutoOrganizeService` files the asset (`TryFileFromImportAsync`): pending reviews, an unresolved title or type, or a destination that is not ready all leave the file exactly where it is. Both ends pass the source-policy gate (`ImportFolderGate`): the Import folder must allow the move and the destination must be a managed destination. `ImportFileMover` renames on one drive and copies, verifies against the ingestion fingerprint, renames into place and then removes the original across drives; a byte-identical file already at the destination (or at a unique name beside it) is adopted so a restart never leaves two copies. `ImportFilingJournal` records each filing in `.data/filing` before the file moves and `ImportFolderFiler.RecoverInterruptedFilingsAsync` finishes any that were cut short before the startup missing-file check; reconciliation also defers cleanup for Import files while their drive is offline or a filing is in progress. Filing is serialised, so the pipeline and the sweep cannot file one file twice.
- **Sweep.** `ImportFolderFiler.FilePendingAsync` runs on the poll interval, on **Check folders for changes** (`POST /ingestion/rescan`, through `IIngestionEngine.RefreshImportFolderAsync`) and after the folder settings are saved. It retries registered files whose identity work has settled, which is how files wait for a managed folder, a returning drive or a resolved review without any waiting state.
- **Duplicates.** `file_handling.duplicate_policy` applies: `keep_one` removes the import copy only (gate-checked delete of that one path); `keep_both` files the import copy beside the held copy with a unique name (`FileDuplicateCopyAsync`), or marks the operation interrupted so the next pass retries when the destination is not ready. A copy held in a library folder is never deleted.
- **Cleanup.** Empty folders left by a download program are removed inside the Import folder only; the per-type folders, the Import folder and `.data` are kept.

`ImportFolderWaitingReader` describes the files left in the Import folder for want of a managed folder or an available one (`GET /ingestion/import-folder/waiting`); review items carry `from_import_folder` for the "From the Import folder" badge.

### Import Mode

Import mode performs a one-time scan of an existing collection. It follows the same processing steps as Watch mode, then either **moves** or **copies** the file depending on `import_action`. Copy mode leaves originals untouched. After an import completes, the folder can optionally be switched to Watch mode for ongoing monitoring.

---

## Processing Steps

Every file - regardless of intake mode - goes through the same sequential processing pipeline:

### Code-Level Stage Chain

`IngestionEngine` is the lifecycle facade for watcher startup, pause/resume,
scanning, shutdown, and dry-run entry points. Per-file work runs through the
ordered `IIngestionStage` chain:

1. `settle/detect`
2. `hash/dedupe`
3. `process`
4. `score/identify`
5. `organize`
6. `write-back`
7. `identity-job creation`

The hash/dedupe stage acquires the content-hash lock, and the coordinator
releases it only after the chain terminates. Duplicate resolution, asset
registration, organization-gate review creation, safe write-back, and identity
job creation therefore retain the same serialized scope.

The organize stage is deliberately a readiness and review decision during
initial ingestion. It does not move the file into the final library. The file
remains in place until the retail-first identity pipeline has enough context for
`AutoOrganizeService` to promote it. Write-back is likewise deferred for files
still in a monitored source folder because changing their bytes would change the content
hash and trigger re-ingestion.

Nullable integrations are grouped with the stage that consumes them: hash
cache, capability planning, provisional-review outcomes, managed artwork/export,
and the identity-pipeline wake signal. This keeps optional behavior from leaking
into unrelated stages.

### 1. Settle

The Engine waits briefly after detecting a file to confirm it has finished being written to disk. This prevents reading partially-copied files from network shares or slow storage.

### 2. Lock Check

The Engine verifies that no other process has an exclusive lock on the file before attempting to read it.

### 3. Fingerprint

A SHA-256 content hash is computed from the file's bytes. This hash is the file's permanent identity throughout its lifetime in the library. It survives renaming, moving, and metadata edits. If a file is ingested a second time (e.g. after a database rebuild), the hash allows the Engine to recognise it immediately.

### 4. Scan

The appropriate processor for the file's format opens the file and extracts all embedded metadata:

- **EpubProcessor** - reads OPF package metadata: title, author, publisher, year, series, language, cover image
- **AudioProcessor** - reads ID3v2 (MP3), iTunes atoms (M4B/M4A), Vorbis comments (FLAC/OGG): title, artist, album, track number, chapter markers, genre, ASIN, embedded artwork
- **VideoProcessor** - reads container metadata (MP4, MKV): title, resolution, duration, codec, embedded subtitles, chapter list
- **ComicProcessor** - reads ComicInfo.xml from CBZ/CBR archives: title, series, issue number, writer, artist, publisher

The processor also emits media type candidates when the format is ambiguous. See the Media Type Disambiguation section below.

### 5. Identify

The Priority Cascade Engine scores all available claims for this file - from embedded metadata, filename parsing, and any prior library folder hints - and assigns the file to an existing Collection or creates a new one. This is where the title, author, series, and other canonical values are resolved.

If multiple files from the same source folder have already been processed (e.g. a TV season with 22 episodes), the Engine uses **Ingestion Hinting**: the first file's resolved metadata is cached as a folder-level prior. Subsequent siblings receive the collection ID, QID, and bridge IDs from that prior as high-confidence claims, dramatically reducing the number of Wikidata lookups needed.

**Work deduplication fallback:** `MediaEntityChainFactory` checks whether a Work already exists before creating a new one (matching by title + author + media type via `IWorkRepository`). When `canonical_values` has not yet been populated for an in-flight asset, the deduplication check falls back to a raw `metadata_claims` lookup so that duplicate files arriving close together in time do not bypass the check. Duplicate files create a new Edition under the existing Work rather than creating a duplicate Work.

**Technical inspection never fails a file.** Right after the asset row is written, `PlaybackInspectionWriter` records the file's technical facts (audio and video files only) and returns an outcome instead of throwing. When FFmpeg is not available it does not probe: the asset gets a `media_inspection_holds` row and every later step runs as normal, so the file is organised and queued for identity like any other. When FFmpeg is available but cannot read the file, the file gets a `MediaInspectionFailed` Needs Review item ("This file couldn't be read. It may be damaged or incomplete.") and still gets its identity job. `PendingMediaInspectionResumer` listens to the tools check (start-up and **Check again**) and, when FFmpeg is found, inspects the held files one at a time in the background and clears their holds. A file whose processing fails for any other reason now has its durable operation closed as `failed_terminal` instead of staying `running`.

### 6. Move to Staging

The file is moved from its source location into `{LibraryRoot}/.data/staging/`, where it waits for hydration and promotion. Cover art is extracted as a claim at this stage and persisted through the managed asset store when the artwork pipeline writes it.

Managed artwork is stored through `AssetPathService` and `entity_assets` under `.data/assets/...`; it is never keyed by provisional QID folders.

---

## When an item becomes Ready

The Engine keeps one stored "Ready" mark per leaf Work (`works.ready_at`, with `works.first_ready_at` recording the first time). Display queries read only this mark, through `WorkReadinessSql` (`ReadyWorkPredicate`); a guardrail test fails if a query over `works` omits it or defines its own rule. The rule itself lives in `WorkReadinessSql`: a leaf Work is Ready when it is not rejected or provisional, has a visible file (including the files that cover it, such as one file holding several episodes), its latest identity job is matched (Wikidata is not required; libraries whose metadata policy skips identity count as matched without a job), no Needs Review row is pending, an episode sits under its show and a track under its album, and its artwork is stored as present or settled. That full rule is needed for the first Ready; after that only the "still ready" rule is checked (`StillReadyPredicate`), so a rematch (Queued, searching, hydrating) does not remove the mark and artwork is not asked for again. Review, no match, failure, rejection, a lost file or a lost place clear it.

`WorkReadinessService` (a hosted service) keeps the mark current:

- **Startup.** It takes the change watermarks, then checks every Work and sets the mark. It announces nothing, because those items are already known.
- **Every 2 seconds.** It asks which Works had an identity job, a review row or a stored artwork value change since the last pass (`FindTouchedWorkIdsAsync`), re-checks only those, and for each Work that became Ready for the first time publishes `MediaAdded` (with the real work id). A failed announcement is kept and retried on the next passes, up to a limit.
- **Every 10 minutes.** It re-checks every Work, which catches inputs that carry no timestamp (curator changes, parent placement, files that moved), re-reads the libraries configuration for the identity-bypass list, and forgets timing entries for Works no longer waiting for artwork.

Change detection uses one watermark per table, each the newest value seen in that table's own time column (the tables write different text formats, so values are never compared across tables). Each pass also re-reads a 30 second stretch below the watermark, because a row is stamped before its write commits and a slow write can appear behind it; once a row is older than that stretch an idle library re-reads nothing. Re-checking a Work is idempotent. The look-ups use the `idx_identity_jobs_updated_at` and partial `idx_canonical_values_artwork_scored` indexes. The Engine logs how long each Work waited between being matched and being Ready (`Work {WorkId} is ready ({MediaType}) {Seconds}s after it was matched`).

## Staging-First Flow

All ingested files land in `.staging/` before reaching the organised library. The library invariant is that every file within the library root (outside `.staging/`) has been hydrated, has reached a settled identity outcome, and has a settled artwork outcome. That may mean a resolved QID with art present, or a precision-preserving QID-missing result with artwork explicitly confirmed missing.

```
Library source  --(detect + process)-->  .staging/  --(hydration + promote)-->  Managed destination
                                           |
                                      stays here if:
                                      - low confidence
                                      - unidentifiable
                                      - needs review
                                      - media type ambiguous
```

### Staging Subcategories

Files are routed to one of four subcategories based on their overall confidence score after the Identify step:

| Subcategory | Condition | Behaviour |
|---|---|---|
| `.staging/pending/` | Confidence >= 0.85, or any user-locked claim | AutoOrganizeService promotes after hydration |
| `.staging/low-confidence/` | Confidence 0.40 - 0.85, no user locks | Awaits hydration improvement or manual review |
| `.staging/unidentifiable/` | Confidence < 0.40, no user locks | Requires user to provide a title or match |
| `.staging/other/` | Resolves to "Other" category | Requires media type classification |

**Staging lifecycle logging:** The Engine logs staging progress at `Information` level at four points: (1) when an asset is moved into staging, (2) when a review queue item is created, (3) when a gap is detected in expected review creation (e.g. a confidence score that should have triggered a review but did not), and (4) when an asset is promoted out of staging into the organised library. These log entries allow the staging pipeline to be audited from the activity log.

**browse readiness gate:** main browse surfaces visibility is no longer a simple "in staging or not" decision. The shared library item projection computes browse visibility, pipeline step, artwork state, and readiness from identity jobs, review state, and canonical artwork flags. An item is visible in the main browse surfaces only after it has a non-placeholder title, a resolved media type, and settled artwork (`present`, or `missing` after explicit settlement). Review-only or still-hidden items remain available in Activity, Review, and the Review Queue.

### AutoOrganize Gate

`AutoOrganizeService` promotes a staged file to the organised library when:

```
overallConfidence >= 0.85  OR  any claim has IsUserLocked = true
```

This threshold (`AutoLinkThreshold = 0.85`) is defined once in `ScoringConfiguration` and reused by both the staging router and the promotion gate. It governs filesystem promotion, not main browse surfaces visibility.

### Hero Banner

Hero banner generation (blur + vignette + grain, via SkiaSharp) runs during promotion by `AutoOrganizeService`. It is a post-hydration step, not an ingestion step, because it benefits from the enriched metadata and high-resolution cover art that hydration provides.

### Manual Reclamation

Staged files retain their fingerprint and metadata in the database. A user can manually resolve a staged file from the Dashboard - by dragging it to a Collection or providing a user-locked title - triggering promotion to the organised library structure. The `.staging/` directory is excluded from library source monitoring to prevent re-ingestion loops.

On startup, if `{LibraryRoot}/.orphans/` exists and `.staging/` does not, the Engine renames the directory and updates all database file paths automatically.

---

## File Organisation

### Data Authority

The database is the authoritative data store for all metadata, relationships, and canonical values. User metadata edits are additionally written back into the file's embedded metadata via `IMetadataTagger` (EPUB OPF, ID3 tags, M4B atoms), ensuring portability - the file carries its own metadata independently of the database.

Wikidata properties are re-fetchable through provider reconciliation. Managed artwork is indexed in the database through `entity_assets`; optional local sidecars are exports, not runtime fallback reads.

Recovery scenarios:
- **Standard:** Scheduled SQLite backups (by domain: universe, people, library) as primary recovery
- **Wikidata data loss:** Re-fetch via batch Reconciliation API
- **Full wipe:** Re-ingest from library root; file embedded metadata and batch Wikidata reconciliation rebuild the library

### Folder Structure Templates

The default organisation template is:

```
{Title} - {QID}/{Title}{Ext}
```

A template names the path inside the type's own folder (for example `library/Movies`); the Engine joins it onto that folder, so templates never start with the category.

Per-media-type overrides:

| Media Type | Template |
|---|---|
| Books | `{Category}/{Title} - {QID}/Epub/{Title}{Ext}` |
| Audiobooks | `{Category}/{Title} - {QID}/Audiobook/{Title}{Ext}` |
| TV | `{Category}/{Title} - {QID}/S{Season:00}E{Episode:00} - {EpisodeTitle}{Ext}` |
| Music | `{Category}/{Artist}/{Album} - {QID}/{TrackNumber:00} - {Title}{Ext}` |
| Movies | `{Category}/{Title} - {QID}/{Title}{Ext}` |
| Comics | `{Category}/{Title} - {QID}/{Title}{Ext}` |

Books and Audiobooks share the same title folder under the `Books` category, distinguished by their format subfolder. This means an ebook and its audiobook counterpart live at:

```
{LibraryRoot}/Books/Dune - Q190159/Epub/Dune.epub
{LibraryRoot}/Books/Dune - Q190159/Audiobook/Dune.m4b
{LibraryRoot}/.data/assets/artwork/Work/{workId}/CoverArt/{variantId}.jpg
```

Cover art is owned by the work-level entity asset, so ebook and audiobook variants can share the same preferred cover without duplicating files beside each media item.

### Category Mapping

The `{Category}` path segment is derived from the file's media type:

| Media Types | Category folder |
|---|---|
| Epub, Audiobook | `Books` |
| TV | `TV` |
| Movies | `Movies` |
| Music | `Music` |
| Comics | `Comics` |
| Unknown | `Other` |

### Migration Note

Existing libraries organised under older folder patterns continue to work. On the next hydration pass or a manual "Re-organise Library" action, files are moved to the current structure automatically.

---

## Media Type Disambiguation

Some file formats map to multiple possible media types. Magic bytes identify the container format but not the content type. An MP3 file could be an audiobook chapter or a music track. An MP4 could be a feature film or a TV episode. The disambiguation system resolves this using heuristic signals treated as voted claims.

### Signal Sources

Media-type detection collects competing candidates with associated confidence values. Canonical metadata selection uses the Priority Cascade; detection evidence should not be confused with provider-priority selection or retail-match scores:

| Signal source | Confidence range | Examples |
|---|---|---|
| Magic bytes (unambiguous formats) | 0.95-1.0 | EPUB -> Books, CBZ -> Comics, M4B -> Audiobooks |
| Processor heuristics | 0.30-0.80 | File duration, bitrate, chapter markers, genre tag |
| Filename and path patterns | 0.25-0.65 | `S01E01` in filename -> TV, `audiobooks` in path -> Audiobooks |
| User lock | 1.0 | Manual override - always wins |

### Confidence Thresholds

| Threshold | Behaviour |
|---|---|
| >= 0.70 (`auto_assign_threshold`) | Accept automatically, proceed normally |
| 0.40-0.70 (`review_threshold`) | Accept provisionally, create `AmbiguousMediaType` review queue entry |
| < 0.40 | Assign `MediaType.Unknown`, block auto-organize, create review entry |

### AudioProcessor Disambiguation

The `AudioProcessor` runs at priority 95 (above VideoProcessor at 90) and handles audio format detection.

Unambiguous assignments:
- `.m4b` -> Audiobooks (0.98 confidence)
- `.flac`, `.ogg`, `.wav` -> Music (0.95 confidence)

For ambiguous formats (`.mp3`, `.m4a`), the processor emits weighted candidates using additive heuristic signals:

- **Duration:** Very long files (> 60 min) bias toward Audiobooks; short files (< 5 min) bias toward Music
- **Chapter markers:** Presence of chapter metadata strongly indicates Audiobooks
- **Genre tags:** Genre values matching known audiobook indicators (e.g. "Spoken Word", "Audiobook") or music genres (e.g. "Rock", "Jazz") push the score in respective directions
- **Album and track metadata:** Presence of track numbers and album names strongly indicates Music
- **Bitrate:** Low bitrate speech-range audio biases toward Audiobooks
- **Path keywords:** Parent folder names like `audiobooks`, `music` in the source path
- **File size:** Very large single files bias toward Audiobooks

Each type (Audiobook, Music) starts at a base score of 0.25. Signals are additive and the final scores are normalized to `[0.0, 1.0]` before comparison against the confidence thresholds.

### VideoProcessor Disambiguation

The `VideoProcessor` resolves ambiguity between Movies and TV:

- **TV filename patterns:** `SxxExx` or `NxNN` patterns in the filename strongly indicate TV
- **Duration:** Short files bias toward TV episodes; feature-length files bias toward Movies
- **Path keywords:** Parent folder structures containing season or series names
- **Sibling file count:** Many similarly-named files in the same folder indicate a TV series

Base score per type (Movie, TV) is 0.35. Signals are additive and normalized to `[0.20, 0.90]`.

### Configuration

All disambiguation thresholds and heuristic parameters - duration bands, bitrate thresholds, path keywords, genre tag lists, TV filename patterns - are configurable in `config/disambiguation.json`. No code changes are needed to tune the system's behaviour.

### Review Resolution

When a file lands in the review queue with an `AmbiguousMediaType` trigger, the user selects the correct media type from candidate cards in the Needs Review tab. The selected type is saved as a user-locked claim at confidence 1.0, the review item is resolved, and the hydration pipeline re-runs for that entity.

After Stage 3 retail metadata returns 3 or more claims, the pipeline can auto-resolve pending `AmbiguousMediaType` review items - the provider results provide enough signal to confirm the media type without user input.

---

## Writeback & Auto Re-tag Sweep

Once a file is identified and enriched, the canonical metadata is queued for the file itself, so external players, re-ingestion, and library rebuilds see it without consulting the database. Each change saves straight away in the data store and adds or refreshes one row on the durable file-write list (`media_file_write_intents`). A later change to the same file replaces the earlier row. The scheduled run (`FileWriteScheduler`, interval `run_interval_minutes` in `config/writeback.json`) works through the list and writes each file once with its final values. Moves are described in [libraries, sources, and intake](library-model-and-intake.md). The per-media-type field list lives in `config/writeback-fields.json` - the single source of truth shared by the taggers and the media detail editor.

### Per-media-type writeback hash

Every media asset carries a `writeback_fields_hash` column that combines:

1. The SHA-256 of the JSON slice for the asset's media type in `writeback-fields.json`.
2. The version constant of the specific tagger that wrote the file (`VideoMetadataTagger.TaggerVersion`, `AudioMetadataTagger.TaggerVersion`, `EpubMetadataTagger.TaggerVersion`, `ComicMetadataTagger.TaggerVersion`).

A file is considered stale when its stored hash differs from the currently-computed hash. Bumping a tagger version or editing the field list for that media type invalidates the hash for every matching file.

### Pending diff + Apply flow

`WritebackConfigState` (singleton) watches `writeback-fields.json` via `IConfigurationLoader`. When the file changes, the state computes a **pending diff** (added and removed fields per media type) and surfaces it without running anything. The Auto Re-tag Sweep card on the Maintenance settings tab shows the diff and two buttons:

- **Apply** - commits the pending diff to `CurrentHashes` and signals the worker to start a sweep.
- **Run Now** - re-runs the sweep against the current hashes without applying a new diff (useful if a tagger version was bumped).

No files are touched until the user clicks Apply or Run Now, and even then the sweep only queues each file: the scheduled file-write run writes it (see the write-back paragraph above).

### RetagSweepWorker

A `BackgroundService` that wakes on either a cron schedule (`config/maintenance.json` -> `schedules.retag_sweep`, default `0 3 * * *`) or the `PendingApplied` signal from `WritebackConfigState`. Each pass:

1. Calls `IMediaAssetRepository.GetStaleForRetagAsync` to find identified assets whose `writeback_fields_hash` differs from the current hash (or is NULL).
2. Processes in batches, queuing each asset with `IFileWriteQueue.EnqueueAssetAsync(assetId, "config_change")` (files already waiting are skipped). The scheduled run writes the file through `WriteBackService`, which stamps the new hash on the row on success.
3. Classifies failures via `RetagFailureClassifier`:
   - **Locked** / **IoFailed** -> `ScheduleRetagRetryAsync` with the next off-hours window start. The sweep picks these up on the next run.
   - **Corrupt** / **Unknown** (after retries exhausted) -> inserts a `ReviewQueueEntry` with trigger `WritebackFailed`, routing the file to the Review Queue.
4. Broadcasts live progress via SignalR (`RetagSweepProgress` and `RetagSweepCompleted` events) so the Maintenance tab shows a processed / succeeded / transient / terminal counter during a sweep.

### Endpoints

| Route | Role | Purpose |
|---|---|---|
| `GET /maintenance/retag-sweep/state` | Admin or Curator | Returns pending diff + current hashes |
| `POST /maintenance/retag-sweep/apply` | Admin | Commits pending diff, signals worker |
| `POST /maintenance/retag-sweep/run-now` | Admin | Signals worker without applying a diff |
| `POST /maintenance/retag-sweep/retry/{assetId}` | Admin | Manual retry for a specific failed asset |

### Review Queue integration

Review Queue surfaces `WritebackFailed` review items alongside other review triggers. The message reads "Re-tag failed - file may be locked or corrupt"; resolving the item either manually (fix the file, click retry) or via a successful next-sweep pass clears the review row.

---

## Supported Library Types

| Library Type | Formats | Notes |
|---|---|---|
| **Books** | EPUB, PDF | Combined with Audiobooks under the `Books` category |
| **Audiobooks** | M4B, MP3, M4A | Combined with Books under the `Books` category |
| **TV** | MP4, MKV, AVI, WebM | Season/episode folder structure |
| **Movies** | MP4, MKV, AVI, WebM | Single-work, flat folder structure |
| **Music** | MP3, FLAC, OGG, M4A, WAV | Album = Collection, Track = Work |
| **Comics** | CBZ, CBR | Sequential art; ComicInfo.xml metadata |

**Future library types planned but not yet implemented:**

- **Other** - YouTube videos, lectures, personal recordings, and any media that does not fit the primary types. Files would be stored and user-provided metadata accepted, but automated enrichment would be limited.
- **View intelligence** - Optional local features such as deeper EXIF/XMP extraction, maps, face/object detection, memories, and event grouping. This future privacy-sensitive scope builds on the isolated local-asset index rather than catalogue ingestion.

## Related

- [How File Ingestion Works](../explanation/how-ingestion-works.md)
- [Supported Media Types and Formats](../reference/media-types.md)
- [How to Write a New File Format Processor](../guides/writing-a-processor.md)

## Series Manifest Hydration

After Stage 4 resolves a Wikidata QID and full property claims have been persisted, `WikidataBridgeWorker` asks `WikidataSeriesManifestHydrationService` whether the item belongs to a canonical series. The service only uses QID-backed relationship facts such as P179/`series_qid`, never fuzzy title matching.

For books, audiobooks, comics, and TV, a canonical series QID triggers a Tuvima.Wikidata manifest fetch. Tuvima stores every named item in `series_manifest_items`, including missing works the user does not own. Later imports from the same series first link against the cached named manifest, so adding another Dune ebook or audiobook usually does not require downloading the whole series again while the cache is fresh.

Manifest hydration is diagnostic unless the external container is sequence
compatible for the media type. Wikimedia list articles, publisher production
lists, franchises, and universes must not become lane shelves. Provider-backed
containers are preferred when they are more specific: Comic Vine volumes for
comics, Apple collections for albums, TMDB seasons and film collections for
watch media, and Wikidata manifests for books/audiobooks when no retail sequence
source exists.
