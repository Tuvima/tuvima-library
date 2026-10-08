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
queueing. The batch item endpoint reads these durable rows, so a restart can
still show what was waiting, running, interrupted, blocked, or failed.

Durable ingestion status is the source of truth for:

- queued files
- queue position
- active stage
- retry and blocked state
- interrupted work after restart
- per-batch item rows

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
canonical no-wipe operation for configured source media. Reset paths include guards
that refuse destructive cleanup when a library output path overlaps a source folder.

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
| `primary_destination_source_id` | The explicit managed destination; never inferred from ordering |
| `accepted_intake_modes` | The direct intake mechanisms accepted by this library |

Configuration lives in `config/libraries.json`. Schema 6 is required; obsolete top-level watch folders, flat source paths, and read-only flags are rejected.

File watching is source-folder aware. A flush that contains files from one source records that source path on the ingestion batch; a flush that spans more than one source records `Multiple source folders`. Watcher noise is buffered for `Ingestion:FswQuietPeriodSeconds` seconds, defaulting to 30 seconds, before the batch is released to the debounce queue.

```json
{
  "schema_version": "6.0",
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
        "role": "primary_destination",
        "management_mode": "managed_by_tuvima",
        "source_type": "local_folder",
        "include_subdirectories": true,
        "access_mode": "writable",
        "participates_in_organization": true,
        "intake_role": "direct"
      }],
      "primary_destination_source_id": "44444444-aaaa-4444-8444-444444444444",
      "visibility": "shared",
      "accepted_intake_modes": ["incoming_folder", "browser_upload"],
      "duplicate_policy": "skip_exact",
      "organization_policy": { "mode": "tuvima_standard", "preserve_originals": true }
    }
  ]
}
```

The `category` tells the Engine where to organise files on disk. The `media_types` tell it how to process them - which processor to use, which metadata providers to query, and what confidence prior to apply during identification. A library folder designated for Movies gives any MP4 it finds an 0.80 prior confidence for that media type, skipping much of the heuristic disambiguation that would otherwise be needed.

---

## Ingestion Snapshot

The Dashboard's Ingestion page uses `GET /ingestion/operations`, backed by `IIngestionOperationsStatusService`, as the application-facing ingestion status model. The service aggregates existing persisted state rather than keeping a separate demo task model:

- `ILibraryItemRepository` for total, registered, provisional, and review lifecycle counts
- `IIngestionBatchRepository` for active and recent batches
- `identity_jobs` and `ingestion_log` for pipeline stage counts
- `stage_progress` rows in the snapshot for the numbered user-facing stage bars
- `ingestion_batch_artifacts` for batch-scoped media, metadata, artwork, people, relationship, QID, and review artifacts used by later Activity rollups
- `review_queue` for actionable review reason groups
- `config/libraries.json` for Read, Watch, Listen, the internal personal-library
  bridges behind View Personal Spaces, stable sources, destinations, and
  incoming locations
- `provider_health` and provider config files for provider status
- runtime `IngestionOptions` and `core.json` for organization rule summaries

Live Dashboard updates come from the snapshot endpoint. Existing SignalR Intercom events (`BatchProgress` and `IngestionProgress`) trigger a refresh of that same snapshot model instead of maintaining separate UI progress state. Polling is faster while jobs are active and slower while idle.

`GET /ingestion/operations` includes a `stage_progress` collection. Each row has:

| Field | Meaning |
| --- | --- |
| `stage_number` | User-facing order. Stages 1-8 are progress rows; Stage 9 is review exception state for metrics and Activity rollups. |
| `stage_key` | Stable key such as `scan`, `retail`, `wikidata`, `people`, or `deep_artwork`. |
| `label` | Display label for the stage. |
| `completed_files` / `total_files` / `percent_complete` | File progress against the active batch total. |
| `active_count` / `queued_count` | Current in-flight and queued work where the backend can measure it. |
| `status_label` | Human-readable state: pending, active, complete, stale, or needs review. |
| `active_item_label` | Exact item label only when per-file state is known. |
| `active_group_label` / `active_group_count` | Group label for batched calls such as Wikidata reconciliation. |
| `label_accuracy` | `ExactItem`, `GroupedLookup`, `BatchOnly`, `Stale`, or `None`. |
| `artifact_label` / `artifact_count` | Running artifact count for the stage. |
| `detail_items` | Optional server-generated detail rows shaped as `{ label, value, tone?, icon? }` for expanded UI content and artifact math. |
| `last_updated_time` / `is_stale` | Freshness signal for active worker data. |

The compact Ingestion page renders Stages 1-8 as progress rows:

| # | Stage | Progress Rule | Artifact Count |
| ---: | --- | --- | --- |
| 1 | Scan | Files accepted, skipped, duplicated, or failed / total files | Accepted files |
| 2 | Read Details | Files parsed, skipped, duplicated, or failed / total files | Parsed files |
| 3 | Retail Match | Files retail matched, review-ready, no-result, skipped, or failed / total files | Provider matches |
| 4 | Wikidata | Files with QID, no QID, not applicable, review-ready, skipped, or failed / total files | QIDs |
| 5 | Ready | Files organized, writeback-ready, visible, or terminal / total files | Added files |
| 6 | People | Files person-enriched, skipped, or not applicable / total files | People |
| 7 | Relationships | Files relationship-enriched, skipped, or not applicable / total files | Links |
| 8 | Artwork | Files deep-artwork completed, skipped, or not applicable / total files | Assets |

Stage 3 is the collapsed retail metadata, quick metadata, and primary cover/poster bar. Stage 8 is later deep artwork enrichment. Stages 6, 7, and 8 may run concurrently after their retail/Wikidata prerequisites exist. For grouped `Tuvima.Wikidata` or provider work, the backend must show a group label instead of an exact file label unless it has a correlation key for a specific file.

Stage 7 prefers explicit series order values over Wikidata previous/next backlink consistency. Missing or contradictory public Wikidata chain links are diagnostics, not Review Queue work, unless they expose a local conflict that needs a curator decision.

Review remains live, but it is not a progress row in the Dashboard. `stage_progress` can still expose Stage 9 for API consumers and artifact ledger support; the Dashboard uses the top **Need Review** metric plus latest-batch review delta, and recent batch rows repeat the review count with other artifact totals.

Music remains a conservative organization lane. The status surface emphasizes tag/fingerprint-first handling and preserving album folders instead of implying aggressive rename/move behavior.

---

## Intake Modes

### Library source monitoring

The Engine monitors every catalogued library source for new files. Existing-library sources are indexed in place and remain read-only. Managed writable sources may organize files when their source and library policies allow it.

The `.staging/` directory within the library root is excluded from library source monitoring to prevent re-ingestion loops.

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

### 6. Move to Staging

The file is moved from its source location into `{LibraryRoot}/.data/staging/`, where it waits for hydration and promotion. Cover art is extracted as a claim at this stage and persisted through the managed asset store when the artwork pipeline writes it.

Managed artwork is stored through `AssetPathService` and `entity_assets` under `.data/assets/...`; it is never keyed by provisional QID folders.

---

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
{LibraryRoot}/{Category}/{Title} - {QID}/{Title}{Ext}
```

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

Once a file is identified and enriched, `WriteBackService` embeds the canonical metadata back into the file itself so external players, re-ingestion, and library rebuilds see it without consulting the database. The per-media-type field list lives in `config/writeback-fields.json` - the single source of truth shared by the taggers and the media detail editor.

### Per-media-type writeback hash

Every media asset carries a `writeback_fields_hash` column (migration **M-084**) that combines:

1. The SHA-256 of the JSON slice for the asset's media type in `writeback-fields.json`.
2. The version constant of the specific tagger that wrote the file (`VideoMetadataTagger.TaggerVersion`, `AudioMetadataTagger.TaggerVersion`, `EpubMetadataTagger.TaggerVersion`, `ComicMetadataTagger.TaggerVersion`).

A file is considered stale when its stored hash differs from the currently-computed hash. Bumping a tagger version or editing the field list for that media type invalidates the hash for every matching file.

### Pending diff + Apply flow

`WritebackConfigState` (singleton) watches `writeback-fields.json` via `IConfigurationLoader`. When the file changes, the state computes a **pending diff** (added and removed fields per media type) and surfaces it without running anything. The Auto Re-tag Sweep card on the Maintenance settings tab shows the diff and two buttons:

- **Apply** - commits the pending diff to `CurrentHashes` and signals the worker to start a sweep.
- **Run Now** - re-runs the sweep against the current hashes without applying a new diff (useful if a tagger version was bumped).

No files are touched until the user clicks Apply or Run Now.

### RetagSweepWorker

A `BackgroundService` that wakes on either a cron schedule (`config/maintenance.json` -> `schedules.retag_sweep`, default `0 3 * * *`) or the `PendingApplied` signal from `WritebackConfigState`. Each pass:

1. Calls `IMediaAssetRepository.GetStaleForRetagAsync` to find identified assets whose `writeback_fields_hash` differs from the current hash (or is NULL).
2. Processes in batches, calling `WriteBackService.WriteMetadataAsync(assetId, "config_change")` for each asset. On success, the service stamps the new hash on the row.
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
