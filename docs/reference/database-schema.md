---
title: "Database Schema Reference"
description: "Inspect the database tables, columns, and relationships that back Tuvima Library."
audience: "developer"
category: "reference"
product_area: "storage"
tags:
  - "sqlite"
  - "schema"
  - "storage"
status: current
---

# Database Schema Reference

## In this page

Inspect the database tables, columns, and relationships that back Tuvima Library.

## Where this lives in the code

- `src/MediaEngine.Storage/Schema/Upgrades/` (the numbered upgrade steps, starting with `0001_baseline.sql`)
- `src/MediaEngine.Storage/Schema/schema.snapshot.sql` (generated, read-only view of the structure after every step)
- `src/MediaEngine.Storage/Schema/ReferenceDataTopUp.cs`
- `src/MediaEngine.Storage/StorageEpochGuard.cs`

SQLite database located at `.data/database/library.db` (path set in `config/core.json`).

Latest storage epoch: **guid-blob-v13-profile-held-contract**. The data structure is built by numbered upgrade
steps in `src/MediaEngine.Storage/Schema/Upgrades/`. Each step runs once at startup and is recorded in
`data_store_upgrades`; a fresh data store runs them all, starting with `0001_baseline.sql`, which creates
the whole structure. Every table below is created by that baseline step unless its notes say a later step
changed it. `schema.snapshot.sql` is the generated, read-only result of all the steps. Obsolete development
epochs are rejected until an explicit destructive reset and reingest, rather than being partially migrated
in place. To change the structure, see [Changing the data store](../develop/changing-the-data-store.md).

**Conventions:**
- Internal UUIDs are stored as 16-byte SQLite `BLOB` values in RFC4122/network byte order. API JSON still serializes GUIDs as strings.
- External identifiers remain `TEXT`, including `wikidata_qid`, provider item IDs, URLs, hashes, plugin IDs, cache keys, and provider-specific IDs.
- Booleans stored as `INTEGER` (`0` = false, `1` = true)
- Timestamps stored as `TEXT` in ISO-8601 format (`2026-03-29T14:00:00Z`)
- Foreign keys enabled via `PRAGMA foreign_keys = ON`
- `canonical_values` is scalar-only. Multi-valued keys from `MetadataFieldConstants.MultiValuedKeys` are stored only as rows in `canonical_value_arrays`.

Startup safety:

- Current databases record `storage_metadata.storage_epoch = guid-blob-v13-profile-held-contract`.
- `storage_metadata` also holds server-wide flags as `key`/`value` rows.
- Older epochs, including the legacy TEXT-GUID database and every earlier `guid-blob-` epoch, are
  rejected on startup.
- Setting `TUVIMA_STORAGE_RESET=1` or `TUVIMA_STORAGE_RESET=destructive-reingest` renames the legacy database and starts a clean database for reingestion. The old database is kept as a `.legacy-text-guid.<timestamp>.bak` file.

---

## Core Media

### collections

Represents a Series (user-facing) or Universe (ParentCollection). Both are stored in the same table, distinguished by `collection_type`.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | Internal UUID, primary key |
| `collection_type` | TEXT | `"Collection"` = Series, `"ParentCollection"` = Universe |
| `title` | TEXT | Display title |
| `wikidata_qid` | TEXT | Wikidata entity identifier. Indexed. |
| `parent_collection_id` | BLOB | FK -> `collections.id`. NULL for top-level Universes and standalone Series. |
| `media_type` | TEXT | The media type a Series shelf holds (a `MediaType` name such as `Books`, `Audiobooks` or `TV`); null on collections that are not Series shelves. Added by upgrade step `0012_series_identity_by_media_type`. A Series is its Wikidata Series QID plus this media type, so a book Series and an audiobook Series with the same `wikidata_qid` are two rows, and a lookup by QID and media type never returns the other one (`CollectionRepository.FindSeriesShelfAsync`). Indexed with `wikidata_qid`. |
| `universe_qid` | TEXT | FK -> `narrative_roots.qid` (`ON DELETE SET NULL`). The Universe record this shelf belongs to or presents. On a Series shelf it names the Universe the Series sits in, stored even when it is the only Series of that Universe. On a Universe shelf it is the record the shelf presents and always equals `wikidata_qid`. Set by `ParentCollectionResolver` using `UniverseIdentityRule`; added by upgrade step `0021_universe_record`. |
| `description` | TEXT | Wikipedia or provider description |
| `sort_title` | TEXT | Normalized title for alphabetical sorting |
| `created_at` | TEXT | Timestamp |
| `updated_at` | TEXT | Timestamp |
| `last_enriched_at` | TEXT | Timestamp of last Wikidata enrichment |

**Indices:** `wikidata_qid`, `parent_collection_id`, `collection_type`, `wikidata_qid + media_type` (Series identity), `universe_qid`, and a unique index `idx_collections_one_universe_shelf` on `universe_qid` for Universe shelves (`collection_type = 'Universe'`), so one Universe record is presented by at most one Universe shelf.

### works

A single title, independent of version or format.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | Internal UUID, primary key |
| `collection_id` | BLOB | FK -> `collections.id`. The Series this work belongs to. |
| `wikidata_qid` | TEXT | Wikidata entity identifier. Indexed. |
| `title` | TEXT | Canonical display title |
| `original_title` | TEXT | Title in the work's source language (Phase 2 localization) |
| `media_type` | TEXT | `Books`, `Audiobooks`, `Movies`, `TV`, `Music`, `Comics` |
| `sort_title` | TEXT | Normalized for sorting |
| `status` | TEXT | `Verified`, `Provisional`, `NeedsReview`, `Quarantined`, `Pending` |
| `ordinal_sort` | REAL | Optional normalized child order for sequence shelves; supports decimals, fractions, annuals, specials, and disc/track sorting. |
| `created_at` | TEXT | Timestamp |
| `updated_at` | TEXT | Timestamp |
| `last_enriched_at` | TEXT | Timestamp |
| `ready_at` | TEXT | UTC ISO-8601 time the Work first became Ready. Set by the Engine's readiness service the first time a leaf Work (`standalone` or `child`) is matched to a real title, not in Needs Review, placed in its show or album, and has its artwork present or settled; cleared when it hits a negative outcome (Needs Review, no match, failure, rejection, lost file or placement). NULL means not Ready. It is the only gate for Home, the hero, lanes, Watch, Read, Listen, collections and the Library list. |
| `first_ready_at` | TEXT | UTC ISO-8601 time the Work was Ready for the first time. Set together with the first `ready_at` and never cleared; the Engine announces a Work as added only when this was empty. |
| `placement_unmatched` | INTEGER | 1 when the title was moved into a place with a known list (an album's tracks, a season's episodes, an audiobook's parts) or into a Series with **Add anyway** (a Series takes a title by hand only that way, spec D20) and so has no track, episode or position number and no details borrowed from any entry; 0 otherwise (the default, and every existing row). The detail page shows it as "Not matched". A later Match to a real entry, or a re-match that places the title (D20), clears it: while the mark is set, no scan, re-read of the file, provider or re-identify gives the title or its files a track, disc, episode, season, series-position or part number (`CanonicalValueRepository` skips those values for it, the hierarchy resolver never finds it by title or slot or renumbers it), and the detail page shows it with no number in an "Unnumbered" group at the end. Upgrade step `0024_placement_unmatched`. |
| `series_placement_locked` | INTEGER | `NOT NULL DEFAULT 0`. 1 means `collection_id` is a person's choice made in the editor's Matching section (NULL `collection_id` = they chose "Not in a Series"); nothing automatic (collection assignment, manifest linking, backfill) may change it. Cleared by "Let the Library decide", and by the `trg_works_series_lock_released_with_shelf` trigger (a `BEFORE DELETE` on `collections`) when the chosen Series shelf is deleted, so the Library decides again on the next placement. Added by upgrade step `0025_series_placement_lock`. This is a placement lock, not a claim lock. |

**Indices:** `wikidata_qid`, `collection_id`, `media_type`, `status`, `ready_at`

Note: newer media library surfaces rely on a shared projection layered over work state, review queue state, identity job state, and canonical artwork flags. The `status` column remains part of the stored model, but it is no longer the only source of truth for browse visibility. Visibility is decided once, by `works.ready_at`; display queries read it through `WorkReadinessSql` and never define their own rule.

The readiness service finds what changed since its last pass through two indexes added with `ready_at`: `idx_identity_jobs_updated_at` on `identity_jobs (updated_at)`, and the partial index `idx_canonical_values_artwork_scored` on `canonical_values (last_scored_at)` covering only the artwork keys `cover_state`, `cover_url` and `artwork_settled_at`. A Work's files, for the Ready rule, include the files that cover it (`work_owned_assets`). Ready is sticky: the full rule (matched, placed, artwork) is needed once, then only a negative outcome clears the mark.

### editions

A specific version of a Work (e.g., "4K HDR Blu-ray Remux", "First Edition Hardcover").

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | Internal UUID, primary key |
| `work_id` | BLOB | FK -> `works.id` |
| `title` | TEXT | Edition label |
| `created_at` | TEXT | Timestamp |
| `placement_unmatched` | INTEGER | The same "Not matched" mark as `works.placement_unmatched`, for an audiobook part: a part is one file (one Edition) of its audiobook's recording Work, so it is the Edition that is added anyway. 0 by default. Added by upgrade step `0024_placement_unmatched`. |

### media_assets

A single file on disk.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | Internal UUID, primary key |
| `edition_id` | BLOB | FK -> `editions.id` |
| `file_path` | TEXT | Absolute path on disk |
| `file_name` | TEXT | Filename only |
| `fingerprint` | TEXT | SHA-256 hash. Used for deduplication and move detection. |
| `file_size_bytes` | INTEGER | |
| `media_type` | TEXT | Resolved media type |
| `container` | TEXT | File container format (e.g., `mkv`, `epub`, `m4b`) |
| `duration_sec` | INTEGER | For audio/video assets |
| `ingested_at` | TEXT | Timestamp |
| `status` | TEXT | Current pipeline status |
| `staging_path` | TEXT | Path within `.data/staging/` while in-flight |

**Indices:** `fingerprint`, `edition_id`, `status`

### media_inspection_holds

The durable "inspection pending" marker. When a video or audio file is registered while a tool it needs (today FFmpeg) is missing, the file is not failed: it carries on through identification as normal and gets one row here. Library surfaces show the file as "Waiting for media tools". When a tools check finds the tool, the Engine inspects each held file and deletes its row; a file the working tool cannot read goes to Needs Review (`MediaInspectionFailed`) and its row is deleted too. The Tools and requirements page counts these rows to show how many files are waiting for a tool.

| Column | Type | Notes |
|---|---|---|
| `asset_id` | BLOB | Primary key, FK -> `media_assets.id` (`ON DELETE CASCADE`) |
| `tool_id` | TEXT | The missing tool, for example `ffmpeg` |
| `held_at` | TEXT | ISO 8601 UTC time the file first started waiting |

**Indices:** `ix_media_inspection_holds_tool (tool_id, held_at)`. Added by upgrade step `0002_media_inspection_holds`.

### media_asset_coverage

Lists every episode a single physical file covers, for example `Show S01E01E02.mkv`.
The file stays attached to its first episode through `editions`; this table adds the
rest. Single-episode files have no rows, so existing behaviour is unchanged. Source
files are never split or altered.

| Column | Type | Notes |
|---|---|---|
| `asset_id` | BLOB | FK -> `media_assets.id`, cascade delete |
| `work_id` | BLOB | FK -> `works.id`, cascade delete: the covered episode |
| `position` | INTEGER | Order within the file, starting at 1 |
| `start_seconds` | REAL | Optional start of the episode inside the file |
| `end_seconds` | REAL | Optional end of the episode inside the file |
| `source` | TEXT | `filename`, `chapters`, `manual` or `provider_runtime` |

**Primary key:** (`asset_id`, `work_id`). **Unique:** (`asset_id`, `position`). **Indices:** `work_id`

### work_owned_assets (view)

Every (`work_id`, `edition_id`, `asset_id`) an owned work is reached through: its own files, plus files that cover it through `media_asset_coverage`. A covered episode resolves to the host file's edition; the file is still one file, so file counts must use `COUNT(DISTINCT asset_id)` while episode counts use `COUNT(DISTINCT work_id)`. `is_covered` is 1 for the extra rows contributed by coverage. Read services that decide whether a work is owned or which file represents it join this view instead of `editions` + `media_assets`.

### collection_items

Links works to collections (Series to Universe relationships).

| Column | Type | Notes |
|---|---|---|
| `collection_id` | BLOB | FK -> `collections.id` |
| `work_id` | BLOB | FK -> `works.id` |
| `sort_order` | INTEGER | Position within the collection. A Series shelf stores the manifest ordinal or the work's own position here **unrounded**: the column has integer affinity, which keeps a fractional value such as `2.5` as a real number. Readers order a Series with the shared Series ordering (manifest ordinal, the work's `ordinal_sort`, publication date, title) and do not rely on this column. Only the rule-chosen shelf holds a row for a work. |

**Indices:** `collection_id + sort_order` for ordered paging and
`work_id` for reverse lookup and cascade enforcement.

Path lookups use case-insensitive indexes that match Windows filesystem semantics:
`media_assets.file_path_root`, `file_hash_cache.absolute_path`, and `persons.name`
all have `COLLATE NOCASE` index coverage. The hash-cache path is also unique under
that collation, and its entry is moved transactionally when an organized file moves.

### collection_work_links

Many-to-many cross-links between works and collections for works that span multiple Series.

| Column | Type | Notes |
|---|---|---|
| `collection_id` | TEXT | FK -> `collections.id` |
| `work_id` | TEXT | FK -> `works.id` |
| `link_type` | TEXT | Relationship type (e.g., `"adaptation"`, `"companion"`) |

### folder_organise_log

The ledger of the organising job: one row for each file, and each sidecar, the job looked at while a folder was switched from read-only to managed (or organised again). A move is written as an intent (`moving`) before the file is renamed and as its final outcome after the library points at the new path, so an interrupted job resumes without losing, doubling or half-moving a file. It is also the audit trail and the source of the exportable log (`GET /settings/libraries/folders/jobs/{id}/log/export`); there is no automatic undo. The owning operation is a `media_operations` row (`operation_type = organise_folder`, `queue_name = folder_jobs`); the log keeps no foreign key so its history outlives that row.

| Column | Type | Notes |
|---|---|---|
| `operation_id` | BLOB | The `media_operations` row that owns the switch. Part of the primary key. |
| `source_id` | BLOB | The library source (folder) being switched |
| `old_path` | TEXT | Where the file was. Part of the primary key. |
| `new_path` | TEXT | Where the file went |
| `outcome` | TEXT | `moving` (intent, not yet finished), `moved`, `moved_unique_name` (a different file had the name, so a numbered name was used), `already_correct`, `duplicate_removed` (an exact copy under Keep one copy), `sidecar_moved`, `not_moved`, `needs_review`, `queued_for_identification`, `skipped` or `failed`. A merge job (`operation_type = merge_folder`) also writes `copied`, `copied_unique_name`, `duplicate_skipped` (an exact copy already in the target, nothing copied), `sidecar_copied`, `merged_for_review` (put in the target's From merge folder) and `left_in_source` (the job stopped before reaching the file); for a merge `old_path` is the file in the source folder and `new_path` its copy in the target. A change-of-kind job (`operation_type = change_folder_kind`) writes `kind_changed` (the file moved to the new kind's library and was identified again, or its looked-up details were dropped; `old_path` and `new_path` are the file's path, since filing waits for the batch), `kind_change_refused` (the file and the title it would join both hold a person's own artwork or details; the file is left as it was and is not retried by the same job) and `failed` (the file is untouched in its old library; the job retries it when it resumes, and starting the change again retries it too), with the plain-English `detail` of what was done |
| `at` | TEXT | Timestamp |
| `detail` | TEXT | The plain-English reason for any outcome that needs one (nullable) |

Indexes: `idx_folder_organise_log_source (source_id, at)` and `idx_folder_organise_log_old_path (old_path COLLATE NOCASE)`. The second one backs the check the mutation gate makes before every delete, move or rename: a path with a merge line whose original stays in place (`copied`, `copied_unique_name`, `duplicate_skipped`, `sidecar_copied`, `merged_for_review`, `left_in_source`) is never deleted, moved or renamed by anything except the merge job itself, even after the merge is cancelled. A restarted merge also reads this table (any earlier operation for the same `source_id`) to carry on from copies already made.

### folder_kind_changes

The plan of one "Change what this folder is" job (Library setup spec D40(c), D60, D61, D69; added by upgrade step `0050_folder_kind_changes`). It is written before anything changes, so a job stopped half way carries on from the same plan. One row per job; the owning operation is a `media_operations` row (`operation_type = change_folder_kind`, `queue_name = folder_jobs`) with the same id, and the job's per-file progress is its `folder_organise_log` ledger.

| Column | Type | Notes |
|---|---|---|
| `operation_id` | BLOB | Primary key; the `media_operations` row that runs the change |
| `source_id` | BLOB | The folder (library source) being changed |
| `from_library_id` | TEXT | The configured library the folder came from |
| `to_library_id` | TEXT | The configured library the folder goes into |
| `from_kind_key` | TEXT | The kind the folder was |
| `to_kind_key` | TEXT | The kind the folder becomes |
| `target_media_type` | TEXT | The Engine media type its files are filed under afterwards (`Movies`, `Videos`, `Music`, ...) |
| `target_is_plain` | INTEGER | 1 when the new kind is plain: its files drop the details looked up for them (D69) |
| `starts_first_check` | INTEGER | 1 when the new kind looks details up: a new first check starts for the folder and its filing moves are held until it passes (D35, D61) |
| `started_by_profile_id` | BLOB | The administrator's profile that started the change (nullable; references `profiles`, set to empty if that profile is deleted); the activity entry names them |
| `repairs_operation_id` | BLOB | The earlier change of this folder (cancelled, or finished with files not changed) whose failed files this change retries (nullable) |
| `created_at` | TEXT | When the plan was written, ISO 8601 UTC |
| `config_applied_at` | TEXT | When the folder moved to the new library in `libraries.json` and its first checks were settled, ISO 8601 UTC; null until then. That step is never repeated |

Index: `idx_folder_kind_changes_source (source_id)`, which finds a folder's latest plan (a change that was cancelled or failed half way can be started again to finish its files).

### excluded_source_files

The media files that stayed on disk when their title was deleted from the library: a file in a read-only folder, a file a folder merge left in place, or a file Tuvima tried to delete and could not. Scans skip an excluded file so the deleted title does not come back; Settings lists them and **Add back** removes the row. The row outlives the title, so it has no foreign keys: the library and source folder are configured folders, identified by their ids.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | Primary key |
| `library_id` | TEXT | The configured library the file sits in |
| `source_id` | TEXT | The configured source folder the file sits in |
| `relative_path` | TEXT | The file's place inside the source folder, written with forward slashes. With `source_id` it is unique: one row per place |
| `content_hash` | TEXT | SHA-256 of the deleted title's media asset (nullable). If the file at that place changes size or modified time, its content is hashed: the same hash is the same file, touched, and stays skipped; a different hash is a different file and is scanned again |
| `file_size` | INTEGER | Size in bytes when it was excluded (nullable) |
| `last_write_utc` | TEXT | The file's last write time, ISO 8601 UTC (nullable) |
| `title` | TEXT | The deleted title's name, for the Settings list (nullable) |
| `excluded_at` | TEXT | When, ISO 8601 UTC |
| `excluded_by_account_id` | BLOB | The account that confirmed the deletion (nullable) |
| `excluded_by_name` | TEXT | That person's name as it was then (nullable) |
| `reason` | TEXT | `as_is`, `protected` or `delete_failed` (nullable) |

Index: `idx_excluded_source_files_library (library_id)`. Deleting a title also removes every row tied to its Work, editions and media assets (chosen values, metadata claims, jobs, Needs Review rows, artwork links and the artwork only it used, search entries, saved items and ratings, every profile's progress and bookmarks, and its own activity history). `personal_activity` is the one exception: it keeps its own snapshot of the title.


### unmatched_assets

The media files the Engine could not match (Library setup spec D34). A file with no retail match, or a file in a YouTube library with no YouTube ID, stays exactly where it is, plays and browses with its file name and tags, and is looked up again later (weekly by default, after a provider or plugin is switched on or updated, or on **Retry now**). One row per media asset; the row is removed when a lookup matches the file (the identity job leaves the no-match state, or a person picks the match) and when the file's asset is deleted. A file the Engine found a match for but is unsure about is not here: it is a Needs Review item.

| Column | Type | Notes |
|---|---|---|
| `asset_id` | BLOB | Primary key; the media asset, which cascades on delete |
| `library_id` | TEXT | The configured library the file sits in (the count and list are per library) |
| `reason` | TEXT | `no_match` (a lookup found nothing) or `no_youtube_id` (a YouTube library file with no video ID in its name or `.info.json`; never looked up) |
| `first_unmatched_at` | TEXT | When it first became Unmatched, ISO 8601 UTC. Kept across retries |
| `last_tried_at` | TEXT | When a lookup last ran, ISO 8601 UTC |
| `next_retry_at` | TEXT | When it is next due, ISO 8601 UTC; null for a kept item. A provider or plugin change and **Retry now** set it to now |
| `kept_as_is` | INTEGER | 1 after **Keep as is**: no retries, and not in the Unmatched count or filter. **Look up again** sets it back to 0 and makes the item due |
| `kept_at` | TEXT | When it was kept (nullable) |

Indexes: `ix_unmatched_assets_library (library_id, kept_as_is, first_unmatched_at)` for the per-library count and list, and the partial `ix_unmatched_assets_due (next_retry_at) WHERE kept_as_is = 0` for the scan of due items.

### folder_first_checks

The first check of a newly added folder (Library setup spec D35, D61; added by upgrade step `0043_folder_first_checks`). A folder added to a library whose kind looks details up (plain kinds and Vault libraries never get one; a folder that existed before never does) is checked before the Engine changes anything in it: its first files are identified as normal, but nothing in it is renamed, moved or tagged. When more than the configured share of the first files are Unmatched (`first_check_files` and `first_check_unmatched_share` in `config/maintenance.json`) the folder is paused: its remaining identity jobs are not claimed and the filing rows held for it stay held until an administrator chooses Keep going or removes the folder. Otherwise the check passes and the held rows become `waiting`, to be filed in the next run with their tags. One row per folder (source).

| Column | Type | Notes |
|---|---|---|
| `source_id` | TEXT | Primary key; the folder's stable ID in `libraries.json` |
| `library_id` | TEXT | The library the folder belongs to |
| `path` | TEXT | The folder as configured |
| `path_prefix` | TEXT | The path with every separator as `/` and exactly one `/` at the end (a root such as `/` or `D:/` keeps its one separator, so a prefix is never empty). Outside Windows a file must also start with it exactly, as the rest of the Engine compares source paths |
| `path_prefix_folded` / `path_range_end_folded` | TEXT | The prefix with ASCII letters lower-cased, and the first string after every string that starts with it (the closing `/` replaced by `0`). A file is in the folder when `lower(replace(its path, '\', '/'))` is at or above the first and below the second, a range that `ix_media_assets_path_folded` answers |
| `state` | TEXT | `checking` (nothing in the folder is touched), `passed`, `paused` (claims stop, nothing is touched) or `kept_going` (an administrator chose Keep going) |
| `tried` / `unmatched` | INTEGER | Files with a final outcome among the first files, and how many of them are Unmatched. Live while checking is shown from `folder_first_check_files`; these hold the counts the check ended or paused on |
| `started_at` / `ended_at` | TEXT | UTC; `ended_at` is null while checking or paused |

Indexes: the partial `ix_folder_first_checks_held (state, library_id) WHERE state IN ('checking', 'paused')`, asked by every identity-job claim and file-write claim, `ix_folder_first_checks_library (library_id)`, and `ix_media_assets_path_folded` on `media_assets (lower(replace(file_path_root, '\', '/')))` for the range lookup of a folder's files.

### folder_first_check_files

The files that reached a final identification outcome (matched, Unmatched or needs review) while their folder was being checked, in the order they did (`seq`). A file counts once; a later outcome for it replaces the earlier one. The rows go when the check passes or the administrator chooses Keep going, and with the check when the folder is removed.

| Column | Type | Notes |
|---|---|---|
| `seq` | INTEGER | Primary key (autoincrement); the order the outcomes arrived |
| `source_id` | TEXT | FK -> `folder_first_checks.source_id` (cascade) |
| `asset_id` | BLOB | FK -> `media_assets.id` (cascade) |
| `outcome` | TEXT | `matched`, `unmatched` (a YouTube file with no video ID counts) or `needs_review` |
| `recorded_at` | TEXT | UTC |

Unique on `(source_id, asset_id)`.

### import_uploads and import_upload_pieces

The resumable uploads of big files into the Import folder (Library setup spec D44), added by upgrade step 0044. One `import_uploads` row per upload an administrator started, so an upload resumes after a dropped connection or an Engine restart. The bytes are never in the data store: they are written into a hidden working file under the Import folder's `.data/uploads` folder (the file scanner skips `.data`), which becomes the real file by a rename into the kind's Import sub-folder when the upload completes. Both tables are transient for backup: an upload in progress is not worth restoring.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | Primary key; the upload id the Dashboard sends with every call |
| `created_by` | BLOB | The administrator's profile id. Only that administrator can read, send to, complete or cancel the upload |
| `kind_key` | TEXT | The library kind the file was dropped on (`movies`, `music`, `other_videos`, ...) |
| `library_id` | TEXT | The library named for a kind that has several; null means the kind's own (nullable) |
| `type_label` | TEXT | The Import folder type the kind maps to (`Movies`, `TV`, `Music`, ...) |
| `file_name` | TEXT | The checked name asked for. The saved name is chosen when the upload completes and gets a number added if the name is taken |
| `total_bytes`, `piece_bytes`, `piece_count` | INTEGER | The file's size, the size of every piece but the last (`upload_piece_bytes` when the upload started) and how many pieces there are |
| `working_path` | TEXT | The hidden working file |
| `destination_folder` | TEXT | The Import folder's sub-folder for the kind |
| `state` | TEXT | `open`, `completed` or `cancelled` (an abandoned upload is cancelled by the sweep) |
| `final_path` | TEXT | Where the file was saved; set on completion (nullable) |
| `created_at`, `last_piece_at`, `closed_at` | TEXT | ISO 8601 UTC. `last_piece_at` moves with every new piece; an open upload idle for `upload_abandon_hours` is abandoned. A finished row is deleted a week after `closed_at` (`closed_at` is nullable) |

`import_upload_pieces` has one row per piece received (`upload_id` cascading on delete, `piece_index` from 0, `received_at`), `WITHOUT ROWID` with the primary key `(upload_id, piece_index)`. A piece is recorded with an insert that ignores an existing row, so sending a piece again changes nothing. Index: `idx_import_uploads_state_last_piece (state, last_piece_at)` for the abandoned-upload sweep.

---

## View Personal Media

View is deliberately separate from `works`, `editions`, `media_assets`,
canonical claims, providers, Wikidata, and Review Queue. Internal library IDs
bridge configuration/intake to one profile-owned Personal Space; users do not
browse several source-based destinations.

### `view_personal_spaces`

One stable Personal Space per enabled profile.

| Column | Type | Notes |
| --- | --- | --- |
| `id` | BLOB | Internal UUID, primary key. |
| `owner_profile_id` | BLOB | Unique FK to `profiles.id`. |
| `library_id` | BLOB | Unique internal bridge used by View queries; it is not a configured library folder. |
| `created_at`, `updated_at` | TEXT | Lifecycle timestamps. |

### `view_sources` and `view_devices`

Provenance records for folders, browser uploads, future device producers, and
other intake origins. They do not create separate Personal Spaces.

`view_sources` stores `personal_space_id`, `source_type`, display `name`, an
optional stable `source_key`, managed/linked storage mode, a managed relative
path or external path, recursion and enabled flags, activity time, and timestamps.
Managed paths resolve beneath the single View root; linked paths remain external
and read-only.
Every source also has a kind, set when it is added: `content_kind` (`photos`,
`personal_videos` or `documents`, default `photos`) decides what the folder
accepts (Photos take pictures, Personal videos take videos, Documents take
documents and pictures of paper; audio and other files are never taken). A file
the folder does not accept is skipped, never moved, deleted or changed, and
`skipped_file_count` records how many the folder's last full scan skipped (reset
by every full scan). `added_by_profile_id` (nullable FK to `profiles.id`, set to
null if that profile is removed) is the profile that added the folder; the files
found in it are tagged with that profile. The built-in upload/backup source of a
personal space and the Shared Library's own intake folders (`shared:timeline`,
`shared:folder:*`) take pictures, videos and documents whatever their stored
kind says, and each item then takes the kind of its own type. Added by upgrade
step `0037_view_kinds_and_added_by`.
`view_devices` stores the Personal Space, optional source, stable client-device
ID, display/device facts, last-backup time, and a modeled backup state. The
mobile/device producer itself is not implemented merely because these rows
exist.

A paired phone's own backup target lives on `client_devices.backup_profile_id`
(nullable FK to `profiles.id`, set to null if that person is removed). Phone
uploads are stored in that person's Personal Space whichever person the phone
is browsing as; with no target they are refused until someone chooses one.

### `local_items`

One profile-owned logical image, short video, document, audio item, or other
personal asset. Compound files share one item.

| Column | Type | Notes |
| --- | --- | --- |
| `id` | BLOB | Internal UUID, primary key. |
| `personal_space_id` | BLOB | Owning Personal Space. |
| `owner_profile_id` | BLOB | Owning profile; indexed access boundary. |
| `library_id` | BLOB | Internal intake/configuration bridge. |
| `media_kind` | TEXT | `image`, `video`, `document`, `audio`, or `other`. |
| `content_kind` | TEXT | The View kind: `photos`, `personal_videos` or `documents` (NULL only for audio and other files). A folder's items take the folder's kind (a picture of paper in a Documents folder is `documents`); uploads, backups and Shared Library intake items take the kind of their own type. Every permission check gates on this column, not on `media_kind`. A row written without one takes the kind that matches its file type (trigger `trg_local_items_content_kind_default`). Added by upgrade step `0037_view_kinds_and_added_by`. |
| `added_by_profile_id` | BLOB | The profile that added the item, shown as that profile's current name (nullable FK to `profiles.id`, set to null if the profile is removed). Folder files take the profile that added the folder; uploads and backups take the profile that made the request; a copy in the Shared Library takes the profile whose space the item came from (else the contributor, else whoever moved it in). Indexed by `ix_local_items_added_by`. Added by upgrade step `0037_view_kinds_and_added_by`. |
| `title`, `primary_file_name`, `primary_mime_type` | TEXT | Local display identity. |
| `captured_at`, `created_at`, `updated_at` | TEXT | Timeline values and timestamps. |
| `favorite`, `hidden` | INTEGER | Profile-owned logical flags. |
| `archived_at`, `trashed_at` | TEXT | Reversible lifecycle timestamps; neither deletes a file. |

Timeline indexes cover owner, library, and Personal Space with lifecycle state,
effective capture/creation time, and stable item ID. Additional indexes cover
kind, favorites, and active discovery.

### Local metadata, files, and provenance

- `local_item_metadata` stores dimensions, duration, page count, camera/device,
  GPS, normalized place, safely extracted document text, and additional local
  metadata JSON. `text_source` records where `document_text` came from:
  `embedded` (the file's own text layer or text), `ocr` (every readable page
  came from text recognition), `mixed` (some pages came from the file's own
  text layer and others from text recognition), `none` (the file was read and
  holds no readable text, is protected, or is corrupt), or NULL (not read yet). The text job writes through `SetDocumentTextAsync`, which also
  rebuilds the search document, and records `document_text_truncated` and
  `document_encrypted` flags in the metadata JSON. Text recognition also records
  `ocr_average_confidence` (0-100) and `ocr_pages` there and keeps low-confidence
  text. Startup widens the stored `text_source` check on older data stores to
  accept `mixed`. A file edited in place (same
  library and path, new bytes) updates its existing item rather than adding a
  duplicate, clearing the stale text until it is read again.
- `local_files` stores one exact content hash, size, MIME type, extension, and
  creation time. Hash equality does not imply shared logical ownership.
- `local_file_sources` retains every observed library/source/device path and
  timestamps. Its unique key is library plus case-insensitive path.
- `local_item_files` relates logical items to physical files with roles such as
  primary, original, Live Photo video, RAW, JPEG, sidecar, audio companion, or
  derivative. Only one primary is allowed per item.
- `tags` is the one global tag list for the install: BLOB `id`, `name` (unique,
  case-insensitive), optional `colour`, `created_by_profile_id` (cleared if that
  profile is removed) and `created_at`. `local_item_tag_links` joins it to
  items (`item_id`, `tag_id`, `added_at`; one row per pair). The earlier free
  text `local_item_tags` table no longer exists; tags live only in `tags` and
  `local_item_tag_links`.
- `doc_correspondents` (`id`, `name` unique ignoring case, `colour`,
  `created_by_profile_id`, `created_at`) and `doc_types` (the same without a
  colour) are the global correspondent and document-type lists.
- `doc_item_details` holds one document's organising details: `item_id`
  (primary key), `document_date` (a calendar day, `yyyy-MM-dd`),
  `correspondent_id` and `doc_type_id` (cleared if that entry is deleted),
  `archive_number`, `notes`, and `filing_state` (`inbox` or `filed`). A new
  document gets an `inbox` row when it is first indexed; a document with no row
  (added before filing existed) counts as filed.
- `doc_field_definitions` (`id`, `name` unique ignoring case, `kind` of `text`,
  `number`, `date`, `money`, `bool` or `choice`, `created_by_profile_id`) holds
  the custom fields; `doc_field_choices` (`field_id`, `value`, `sort_order`) one
  row per choice of a choice field; `doc_field_values` one row per document and
  field (`text_value`, `number_value`, `date_value`, `bool_value`). Money keeps
  the amount in `number_value` and the ISO 4217 code in `text_value`; a choice
  keeps the chosen value in `text_value`.
- `doc_item_details.suggestions_json` (nullable, valid JSON) holds what was
  worked out for a document waiting in the Inbox: `document_date`,
  `date_from_file`, `correspondent`, `document_type`, `tags`, `amount` and
  `currency` (names, not ids). It is cleared when the document is filed and is
  never applied on its own.
- `doc_rules` (`id`, `owner_profile_id`, `personal_space_id`, `name`, `enabled`,
  `match_mode` of `all` or `any`, `sort_order`, `files_automatically`,
  `created_at`, `updated_at`) holds auto-filing rules. A rule belongs to its
  creator and covers their Personal Space, or the household's Shared Library
  when `personal_space_id` is null (household administrators only; the rule
  stops applying while its maker is not an enabled household administrator).
  `doc_rule_conditions` (`rule_id`, `position`, `kind`, `value`) holds the
  conditions (`text_contains`, `filename_contains`, `folder_contains`, `source`
  with a source id, `file_kind` of `pdf`, `image` or `office`);
  `doc_rule_actions` (`rule_id`, `position`, `kind`, `ref_id`, `text_value`)
  holds the actions (`add_tag`, `set_correspondent`, `set_document_type`,
  `set_field` with its value in `text_value`). `ref_id` has no foreign key:
  deleting the tag, correspondent, type or field also deletes the actions that
  pointed at it. All ids are 16-byte BLOBs.
- `doc_item_field_sources` (`item_id`, `slot` of `correspondent`,
  `document_type` or `field`, `field_id`, `set_by` of `person` or `rule`)
  remembers who last set a value so a rule never replaces one a person chose.
  Saving the editor, accepting suggestions or filing from the Inbox with a
  value records `person`; a rule records `rule`. A value with no row counts as
  the person's. Shared Library copies carry the rows with them.
- Tags, correspondents, document types and custom fields are global, but who
  sees a name is decided on every read, never stored: a person sees a name
  only when it is used on an item in a library their request is approved for
  (the same library test the item listings use) or they created it
  themselves. Counts are the person's own. Deleting a name removes its links
  and values, never the items. Only the creator or an administrator can rename
  or delete one. Someone else's hidden items, and documents for a profile
  without the Documents permission, never make a name visible. A name is the
  same name whatever its letter case in any script ("Ärzte" and "ärzte").
- `local_item_search_keys` maps BLOB item IDs to FTS rowids, while the
  `local_item_search` FTS5 table indexes title, filename, MIME/media kind, date,
  device, location, document text (with the description and a document's
  notes), and tags (with people, the correspondent, document type and archive
  number, and the text of custom field values). Anything that changes one of
  those rebuilds the item's search document, including a new read of the
  document's words.
- `local_item_annotations` is reserved for provenance-aware extracted/reviewed
  facts. It stores kind, value, confidence, source, model provenance, and review
  time; the table does not perform or imply face recognition, OCR, captions,
  object detection, or semantic processing.

GPS and annotation indexes support Places and evidence-based People discovery.

### Source timeline policy and Shared Library originals

`view_source_policies` stores whether each source participates in the Photos
timeline. Browser uploads default into Timeline; additional linked and managed
folders opt in explicitly with the "Show in Photos timeline" switch on that
folder in the View Folders tab. The switch belongs to the whole source folder;
there are no per-sub-folder rules.

`view_shared_library` holds one Shared Library per household (`household_id` is the primary key, `library_id` is unique and immutable), created the first time the household needs it. The server's own Shared Library is the one that belongs to the server administrator's household. A personal space can never use any Shared Library's identity. Shared files of the server's household stay in the original Shared folder; other households' files live under `Shared/Households/<id>` so one household's files are never indexed into another's library. A Shared Library row can be removed (it goes with its household) only while no shared photos or videos are in it; the data store refuses the delete otherwise.

`view_shared_assets` marks accepted items whose verified originals are owned by
the Shared Library while retaining the original profile as provenance. This marker
does not expose the original profile's other private assets.

`view_shared_contributions` stores the contributor, decision state, destination,
note, optimistic revision, idempotency key, reviewer, and timestamps.
`view_shared_contribution_items` stores the exact submitted logical items,
submission-time provenance, source snapshot, transfer operation, execution
state, and safe error. `view_shared_contribution_events` records the durable
submission, decision, and transfer activity timeline.

`view_shared_transfers` is the durable recovery journal for physical Shared
Library transfer. It stores source and destination manifests and one of
`planned`, `transferring`, `completed`, `cleanup_pending`, or `failed`.
Shared membership is published only after every destination member verifies;
managed-source deletion happens afterward and can remain cleanup-pending.

`item_id` on both `view_shared_contribution_items` and `view_shared_transfers` is
optional and clears itself (`ON DELETE SET NULL`) when the personal item is removed
by Empty Trash, so a finished, failed, declined or cancelled contribution keeps its
history row with no item link (upgrade step 0020). Work still in flight (a pending
contribution, an accepted item that is waiting, copying or cleaning up, or a transfer
that is planned, copying or cleaning up) never reaches that point: Empty Trash skips
the item first. The `contribution_item_id` link from a transfer to its contribution
item is unaffected.

**Empty Trash and the data store.** Emptying a person's Trash removes each item's
`local_items` row inside one transaction. Its cascades clear `local_item_metadata`,
`local_item_files`, `local_item_tag_links`, `local_item_annotations`,
`local_item_search_keys` (a delete trigger clears the search text),
`doc_item_details`, `doc_field_values`, `doc_item_field_sources`,
`view_gallery_items` and `device_photo_downloads`; a Gallery whose cover was the
item has `cover_item_id` set to NULL (`view_galleries` rows themselves are never
removed). The same transaction then deletes the item's `local_file_sources` rows
for files that were removed from disk (or were already gone), and any `local_files`
row left with no item link and no remaining place. Items that share a file on disk
are removed together in one transaction. Before that, the files are parked in a
hidden `.tuvima-staging/<item id>/` folder inside the owner's profile folder, with a
`manifest.json`, so a failure puts every file back; View scans and the folder watcher
ignore that folder, and a startup (and after-each-Empty-Trash) sweep puts parked files
back when their item still exists or deletes them when it does not.

### Galleries

`view_galleries` stores owner, Personal Space, name/description, kind, optional
cover, ordering, and timestamps. `gallery_kind` is `manual` or `smart`; Manual
Galleries must have no rule JSON and Smart Galleries must have valid rule JSON.

`view_gallery_items` stores unique Manual Gallery membership with explicit
position and added time. Smart Galleries reject manual membership.
`view_gallery_shares` stores selected-profile `view` or `contribute`
permission. Sharing one Gallery does not grant Shared View or access to the
owner's other assets.

### `collection_view_sources`

Personal-media sources for administrator-authored Collections. A row is either:

- a dynamic Gallery reference, or
- a saved smart-rule JSON object with `rule_version = 1`.

The mutually exclusive constraints prevent storing both forms, and individual
local asset IDs are intentionally absent. Unique/indexed Gallery references and
ordered Collection lookups keep evaluation dynamic while the projection layer
reapplies View authorization.

### Profile policy and preferences

`profile_view_policies` stores independent administrator-managed capabilities:
View enabled, open the household's Shared Library, send items to it, share
Galleries, and see Documents (`documents_enabled`), Photos (`photos_enabled`) and
Personal videos (`personal_videos_enabled`). The two newest switches default on
for everyone; a kind that is off is simply not granted: its items, counts,
folders, search text, places and people never reach that profile, and a single
item answers "not found". Opening and sending default
on (sending stays off for a child profile until a household administrator allows
it). Documents default on for everyone except a child profile, which starts with
it off; the Documents page, its Engine list and open actions, and the document
files behind them all follow this one switch. Reviewing contributions is not a
per-profile setting: the former `review_shared_library_contributions` column is
dropped once on upgrade, and household administrators review.

`profile_view_preferences` stores the last Shared/Mine/Profile scope and
compact/comfortable/relaxed timeline density. The Profile form requires a
profile ID; other forms forbid one. The authorization layer owns fallback when
a saved scope is no longer permitted. `documents_sort` (nullable; `added`,
`document_date`, `title` or `correspondent`, NULL for the default order) is how
the profile likes the Documents list ordered. It is saved on its own, so saving the scope or
density never resets it. A saved Documents view is a row in `view_galleries`
(`gallery_kind = 'smart'`) whose `smart_rule_json` contains a `media_type` is
`document` condition; no table was added for it.

---

## Metadata

### metadata_claims

Append-only log of every metadata value ever received for an entity, with its source and confidence.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | Internal UUID, primary key |
| `entity_id` | BLOB | Internal entity UUID |
| `entity_type` | TEXT | `"Work"`, `"Collection"`, `"Person"`, etc. |
| `field_key` | TEXT | Field identifier (e.g., `"title"`, `"author"`, `"genre"`). See `MetadataFieldConstants.cs`. |
| `value` | TEXT | Claim value |
| `source_id` | BLOB | Internal provider UUID |
| `source_name` | TEXT | Human-readable provider name |
| `confidence` | REAL | Score 0.0 - 1.0 |
| `source_language` | TEXT | BCP-47 language code of this claim's value (Phase 6) |
| `is_user_lock` | INTEGER | 1 if user explicitly set this value (Tier A - always wins) |
| `created_at` | TEXT | Timestamp |
| `decayed_at` | TEXT | Timestamp when stale decay was applied. NULL if fresh. |

**Indices:** `entity_id + field_key`, `source_id`, `is_user_lock`

### canonical_values

The winning single-valued claim for each field after Priority Cascade resolution.

| Column | Type | Notes |
|---|---|---|
| `entity_id` | BLOB | Internal entity UUID |
| `entity_type` | TEXT | |
| `field_key` | TEXT | |
| `value` | TEXT | Winning value |
| `source_id` | BLOB | Which provider won |
| `confidence` | REAL | Winning confidence score |
| `resolved_at` | TEXT | Timestamp of last resolution |

**Unique index:** `entity_id + field_key`

Artwork truth is also persisted here through canonical keys such as `cover_state`, `cover_source`, `hero_state`, and `artwork_settled_at`.

Sequence facts are scalar canonical values on the accepted container or child:
`sequence_total`, `sequence_total_scope`, `sequence_format`, and related
provider-specific IDs such as `comic_vine_volume_id`. They describe the
immediate shelf container, not a broader franchise unless the scope explicitly
says `BroaderFranchise`.

Description and text provenance also belongs in canonical/claim metadata where
available: provider/source name, source title, source URL, license name, license
URL, retrieval timestamp, and a flag for modified or summarized display text.

### canonical_value_arrays

The winning multi-valued claims (genres, authors, vibe tags, cast members, and a video file's `subtitle_languages`, one ISO 639-1 code per distinct language). Each value is one row with an ordinal and optional QID; packed delimiter strings are not supported.

| Column | Type | Notes |
|---|---|---|
| `entity_id` | BLOB | Internal entity UUID |
| `key` | TEXT | Canonical field key |
| `ordinal` | INTEGER | Display order |
| `value` | TEXT | One display value |
| `value_qid` | TEXT | Optional external Wikidata QID for this value |
| `source_id` | BLOB | Optional winning provider UUID |
| `last_scored_at` | TEXT | Timestamp |

**Unique index:** `entity_id + field_key`

`series_placement` holds every (Series, position) pair a Work has: `value` is the position text exactly as Wikidata gives it (for example `1.5`), `value_qid` is the Series QID, and `ordinal` follows the statement order. A Work in two Series keeps each Series' own number here, and readers ask for the pair of the Series they show before falling back to the single `series_position`. A series statement with no position adds no row (membership stays in `series` and `series_qid`).

### search_index

FTS5 full-text search index with trigram tokenizer. Handles CJK and languages without word boundaries. Short queries under 3 characters fall back to a `LIKE` scan.

| Column | Type | Notes |
|---|---|---|
| `entity_id` | TEXT | UNINDEXED - used for JOIN back to source tables |
| `title` | TEXT | Primary display title |
| `original_title` | TEXT | Source-language title |
| `alternate_titles` | TEXT | Wikidata aliases and romanizations (e.g., "Sen to Chihiro no Kamikakushi") |
| `author` | TEXT | Author/creator names |
| `description` | TEXT | Full description text |

---

## Persons

### persons

Wikidata-sourced person records. Always enriched from Wikidata; never manually created.

| Column | Type | Notes |
|---|---|---|
| `id` | TEXT | UUID, primary key |
| `wikidata_qid` | TEXT | Wikidata identifier. Indexed. |
| `name` | TEXT | Display name |
| `description` | TEXT | Wikipedia short description |
| `birth_date` | TEXT | ISO-8601 date |
| `death_date` | TEXT | ISO-8601 date. NULL if living. |
| `nationality` | TEXT | |
| `headshot_url` | TEXT | Cached headshot image URL |
| `last_enriched_at` | TEXT | Timestamp. Records enriched within 30 days skip re-fetch. |
| `last_revision_id` | TEXT | Wikidata revision ID. Used for freshness checks before full property re-fetch. |

**Index:** `wikidata_qid`

### person_roles

Roles a person plays in the library (author, director, narrator, composer, etc.).

| Column | Type | Notes |
|---|---|---|
| `person_id` | TEXT | FK -> `persons.id` |
| `role` | TEXT | Wikidata property code (e.g., `P50` = author, `P57` = director, `P161` = cast member) |
| `work_id` | TEXT | FK -> `works.id` |

### person_media_links

Aggregated library presence - which media types a person appears in and how many works.

| Column | Type | Notes |
|---|---|---|
| `person_id` | TEXT | FK -> `persons.id` |
| `media_type` | TEXT | |
| `work_count` | INTEGER | |

### person_aliases

Pseudonyms and alternate names, including Wikidata-resolved aliases.

| Column | Type | Notes |
|---|---|---|
| `person_id` | TEXT | FK -> `persons.id` |
| `alias` | TEXT | Alternate name |
| `alias_type` | TEXT | `"pseudonym"`, `"birth_name"`, `"wikidata_alias"` |
| `merge_target_id` | TEXT | If this alias resolves to a different person record, the canonical person's id |

### pending_person_signals

Person names from file metadata or AI extraction awaiting standalone Wikidata reconciliation.

| Column | Type | Notes |
|---|---|---|
| `id` | TEXT | UUID, primary key |
| `entity_id` | TEXT | The work or edition this name came from |
| `name` | TEXT | Person name to reconcile |
| `signal_source` | TEXT | `"file_metadata"` (confidence 0.80) or `"ai_extracted"` (confidence 0.75) |
| `created_at` | TEXT | Timestamp |
| `resolved_at` | TEXT | NULL until reconciliation completes |

### character_performer_links

Links fictional characters to the real-world performers who portray them.

| Column | Type | Notes |
|---|---|---|
| `character_id` | TEXT | FK -> `fictional_entities.id` |
| `person_id` | TEXT | FK -> `persons.id` |
| `work_id` | TEXT | FK -> `works.id`. Scopes the link to a specific adaptation. |
| `era_qualifier` | TEXT | Temporal qualifier (e.g., "young", "2024 series") for era-correct actor matching |

---

## Universe Graph

### fictional_entities

Characters, locations, factions, events, objects/artifacts, and other fictional
elements within Universes. `entity_sub_type` is a strong discriminator; Objects
are not overloaded as locations or organizations.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | UUID, primary key |
| `fictional_universe_qid` | TEXT | Authoritative narrative-universe QID |
| `wikidata_qid` | TEXT | Canonical Wikidata QID (unique) |
| `entity_sub_type` | TEXT | `"Character"`, `"Location"`, `"Organization"`, `"Event"`, or `"Object"` |
| `label` | TEXT | Display name |
| `description` | TEXT | |
| `wikidata_revision_id` | INTEGER | Last observed Wikidata revision for lore-delta refresh |

User-authored display fields are kept separately in `fictional_entity_user_overrides` and
`narrative_root_user_overrides`, so enrichment never overwrites a curator edit.

### fictional_entity_work_links

First-class fictional-entity appearances in works. Each appearance keeps its
role/type, work-specific context, optional chapter/episode/scene anchor,
narrative or temporal context, spoiler boundary, and provenance.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | Appearance identifier |
| `appearance_key` | TEXT | Stable complete-appearance idempotency key |
| `entity_id` | BLOB | FK -> `fictional_entities.id` |
| `work_qid` | TEXT | Canonical work identity |
| `link_type`, `appearance_role` | TEXT | Appearance type and role |
| `work_context`, `anchor_kind`, `anchor_value` | TEXT | Adaptation and precise local anchor |
| `narrative_time_index`, `start_time`, `end_time` | TEXT | In-universe chronology without coercing it to Gregorian dates |
| `spoiler_for_work_qid` | TEXT | Optional spoiler boundary |
| `source_provider`, `provenance`, `is_supplemental`, `confidence` | TEXT / INTEGER / REAL | Source distinction and reliability |

### entity_relationships

Directed graph facts between fictional entities (e.g., "Frodo" -> `parent_of`
-> "Sam"). Facts are statements: the same triple may legitimately occur in
multiple adaptations, time periods, or spoiler scopes.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | Fact identifier |
| `statement_key` | TEXT | Stable complete-statement idempotency key |
| `subject_qid`, `object_qid` | TEXT | Canonical graph endpoints |
| `relationship_type` | TEXT | Wikidata-aligned predicate |
| `context_work_qid`, `start_time`, `end_time` | TEXT | Common query projections for scoped facts |
| `source_provider`, `provenance`, `is_supplemental`, `confidence` | TEXT / INTEGER / REAL | Source distinction and reliability |

### entity_relationship_qualifiers

Normalized, queryable qualifier rows for graph facts. This preserves source
statement semantics such as applies-to-work, fictional time index, spoiler-for,
statement nature, source/reference, and confidence without adding one column per
Wikidata qualifier.

| Column | Type | Notes |
|---|---|---|
| `id`, `relationship_id` | BLOB | Qualifier and owning fact IDs |
| `qualifier_type`, `value`, `value_kind` | TEXT | Typed, searchable qualifier value |
| `source_provider`, `provenance`, `is_supplemental`, `confidence` | TEXT / INTEGER / REAL | Per-qualifier provenance |

### narrative_roots

The Universe record. One row per Wikidata QID that anchors a group of works (a fictional universe, franchise or Series); the graph, the Library and the Universe page all read the same row. A Universe shelf in `collections` is how the Library presents the record (`collections.universe_qid`), and a Series shelf names the record it belongs to with the same column. The record is created before any shelf names it, is never overwritten by a later shelf (`EnsureAsync`), and keeps a parent recorded earlier when a later write names none.

| Column | Type | Notes |
|---|---|---|
| `qid` | TEXT | Wikidata QID, primary key |
| `label` | TEXT | Display label (a user override lives in `narrative_root_user_overrides`) |
| `level` | TEXT | `Universe`, `Franchise`, `Series` or `Standalone` |
| `parent_qid` | TEXT | The broader record this one sits under (a franchise within a universe, a Series within a franchise); NULL for a top-level record |
| `created_at` | TEXT | Timestamp |

### qid_labels

Cached Wikidata display labels for QIDs, avoiding repeated API lookups.

| Column | Type | Notes |
|---|---|---|
| `qid` | TEXT | Primary key |
| `label` | TEXT | Display label in the configured metadata language |
| `fetched_at` | TEXT | Timestamp |

### text_track_rejections

"This one is wrong" on lyrics or subtitles (Plugin architecture C24). One row per source result a person marked wrong for an item, so
the Library never picks it again for that item. It keeps only the source's name and the result's id, never the text, and goes with the
file (`ON DELETE CASCADE`).

| Column | Type | Notes |
|---|---|---|
| `asset_id` | BLOB | FK -> `media_assets.id`; part of the primary key |
| `kind` | TEXT | `Lyrics` or `Subtitles`; part of the primary key |
| `provider` | TEXT | The source's name; part of the primary key |
| `source_id` | TEXT | The result's id inside that source; part of the primary key |
| `rejected_at` | TEXT | When it was marked wrong |

### subtitle_index_tracks, subtitle_lines, subtitle_name_tags

The subtitle text index (Universes U-WP14). One `subtitle_index_tracks` row per subtitle track that was read: either a
stored, normalised `text_tracks` row (whichever source supplied it) or a text subtitle stream embedded in the video file.
Its lines and name tags go with it (`ON DELETE CASCADE`), and with the file (`asset_id`).

| Table | Column | Type | Notes |
|---|---|---|---|
| `subtitle_index_tracks` | `id` | BLOB | Stable id derived from the source, so a re-read keeps it |
| | `asset_id` | BLOB | FK -> `media_assets.id` |
| | `source_kind` | TEXT | `TextTrack` or `Embedded` |
| | `text_track_id` | BLOB | FK -> `text_tracks.id` for `TextTrack`; null for `Embedded` |
| | `stream_index` | INTEGER | ffmpeg stream index for `Embedded` |
| | `state` | TEXT | `Indexed` or `Failed` (with `attempt_count`, `last_error`, `next_retry_at`) |
| | `source_stamp` | TEXT | Changes when the stored track changes; the track is then read again |
| | `entity_signature` | TEXT | Hash of the Universe entities the tags were matched against |
| | `tagged_at` | TEXT | When the tags were last made or checked |
| `subtitle_lines` | `track_id`, `line_index` | BLOB, INTEGER | Primary key |
| | `start_ms`, `end_ms` | INTEGER | Milliseconds from the start of the file |
| | `text` | TEXT | Plain text with markup removed |
| `subtitle_name_tags` | `track_id`, `entity_id`, `window_start_ms` | | Primary key; `entity_id` -> `fictional_entities.id` |
| | `window_end_ms` | INTEGER | A window ends at the last mention before a gap of more than 30 seconds |
| | `first_line_index`, `last_line_index`, `mention_count` | INTEGER | Lines in the window that name the entity |
| | `matched_text` | TEXT | The name as written in the first mention |

Tags are data only: plain name matching (labels, given names and surnames of characters, supplied aliases), never AI.
Nothing displays them yet.

### subtitle_index_assets and media_asset_chapters

`subtitle_index_assets` is the durable to-do row of the background indexer for a video file (movies and TV): a row in
state `Done` means the file was probed once; `Failed` rows are retried up to five times. `media_asset_chapters` holds that
probe's chapter times (`start_ms`, `end_ms`, `title`) for the file.

---

## Providers

### metadata_providers

Registered providers and their runtime state.

| Column | Type | Notes |
|---|---|---|
| `id` | TEXT | GUID - matches `provider_id` in config files. Primary key. |
| `name` | TEXT | Display name |
| `media_types` | TEXT | JSON array of served media types |
| `stage` | TEXT | `"Stage1"` or `"Stage2"` |
| `enabled` | INTEGER | |
| `last_checked_at` | TEXT | Timestamp of last health check |
| `health_status` | TEXT | `"Healthy"`, `"Degraded"`, `"Unavailable"` |

### provider_config

Live provider configuration values (mirrors `config/providers/*.json` after load).

| Column | Type | Notes |
|---|---|---|
| `provider_id` | TEXT | FK -> `metadata_providers.id` |
| `config_key` | TEXT | Configuration field name |
| `config_value` | TEXT | Configuration field value |

### provider_response_cache

Cached provider API responses to eliminate redundant calls.

| Column | Type | Notes |
|---|---|---|
| `id` | TEXT | UUID, primary key |
| `provider_id` | TEXT | |
| `cache_key` | TEXT | Hashed request parameters |
| `response_body` | TEXT | Cached JSON response |
| `cached_at` | TEXT | Timestamp |
| `expires_at` | TEXT | TTL-based expiry timestamp |

### provider_health

Historical health check log per provider.

| Column | Type | Notes |
|---|---|---|
| `provider_id` | TEXT | |
| `checked_at` | TEXT | Timestamp |
| `status` | TEXT | |
| `latency_ms` | INTEGER | |

---

## Security

### api_keys

API keys for Engine authentication.

| Column | Type | Notes |
|---|---|---|
| `id` | TEXT | UUID, primary key |
| `key_hash` | TEXT | SHA-256 hash of the key. The plaintext key is never stored after creation. |
| `label` | TEXT | Human-readable label |
| `role` | TEXT | `"Administrator"`, `"Curator"`, `"Consumer"` |
| `created_at` | TEXT | Timestamp |
| `last_used_at` | TEXT | Timestamp. NULL if never used. |
| `revoked_at` | TEXT | NULL if active. Timestamp if revoked. |

### accounts

One row per person who signs in. A sign-in that works away from this computer has an email. The only account without one is a this-computer account made at setup (`this_computer_only = 1`); it gets an email when it is secured. There are no email-less "local-only" accounts. `email` and `normalized_email` allow NULL, and the baseline step creates `accounts` that way.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | GUID, primary key |
| `email` | TEXT | Sign-in email as entered. Null only for a this-computer account that has not been secured yet. |
| `normalized_email` | TEXT | Upper-cased form used for lookups. Unique; many rows may be null. |
| `is_enabled` | INTEGER | 1 when the account can sign in |
| `authorization_version` | INTEGER | Bumped whenever access changes, so open sessions re-check |
| `created_at`, `updated_at` | TEXT | Timestamps |
| `this_computer_only` | INTEGER | 1 for an account that was started on this computer without a password. It signs in without a password and, since Tuvima is open from the start, can be used from any device that reaches the server until a password or passkey is added (see Security). Defaults to 0. |
| `must_change_password` | INTEGER | 1 while the password is one an administrator chose. The person can then do nothing except choose their own password, check their session and sign out; the Engine refuses everything else with 403 `password_change_required`. Defaults to 0. |
| `temporary_password_expires_at` | TEXT | When an administrator-set temporary password stops working; null when none is set. Sessions of an account whose temporary password has run out stop validating. |
| `profile_id` | BLOB | NOT NULL. The profile this sign-in opens (a user is a sign-in attached to one profile and cannot exist without it; references `profiles`, deleting the profile deletes the sign-in). A unique index (`ux_accounts_profile`) allows exactly one sign-in per profile. Signing in always opens this profile, and the sign-in may switch to any other profile of the same household (never another household's). Its roles (server administrator, household administrator) and its Read/Watch/Listen/View and library access are the profile's, not the sign-in's (users, profiles and households spec, steps `0046_profile_held_access`, `0047_storage_epoch_profile_held_access` and `0048_drop_account_held_access`). |

### households

A household is the group of people (profiles) who live together and the sign-ins (accounts) that open them. A profile and an account each belong to exactly one household. A household holds up to 8 profiles; the Engine refuses a ninth with "A household can have up to 8 people." A sign-in opens one profile and can switch to the other profiles of that profile's household. Setup creates the first household with the administrator account; a new user for someone outside the household starts a new household for the new profile.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | GUID, primary key |
| `name` | TEXT | Display name, for example `Alex's household` |
| `created_at` | TEXT | Timestamp |
| `is_default` | INTEGER | 1 for the server administrator's original household. At most one household has it (unique partial index `ux_households_default`); setup marks the first administrator's household. It is never deleted. Added by step `0046_profile_held_access`. |
| `kid_watch_level` | TEXT | The highest rating a Kid's profile in this household may see in Watch: `G`, `PG`, `PG-13` or `R` (CHECK-constrained). Defaults to `PG`. Added by step `0068_household_kid_settings` (library access D9). |
| `kid_read_level` | TEXT | The same for Read. Defaults to `PG`. |
| `kid_listen_level` | TEXT | The same for Listen. Defaults to `PG`. |
| `kid_show_unrated` | INTEGER | 1 (the default) shows items with no recognised rating to the household's Kid's profiles in every area; 0 hides them. Rated items always follow the level. |

**Creation.** The baseline step creates `household_id` on `profiles`. Only the seeded Owner profile can be without a household, and only until the first administrator account is created.

**Kid's profiles.** A Kid's profile is a profile with the `RestrictedProfile` role (`is_kids` on the wire). Its limits are these four household columns, so changing them updates every Kid's profile in the household at once; a profile has no rating limit of its own. Step `0068_household_kid_settings` dropped `profiles.content_limit` and `profiles.content_limit_allow_unrated` with no conversion, and rewrote the Vault eligibility triggers to key on the role (a Kid's profile can never have Vault access).

### profiles

User profiles for multi-user support.

| Column | Type | Notes |
|---|---|---|
| `household_id` | BLOB | The household the profile belongs to (see `households`) |
| `avatar_color` | TEXT | Avatar circle colour, `#RRGGBB`. The profile editor offers a fixed palette of 12 swatches. |
| `avatar_image_path` | TEXT | Path of an uploaded avatar photo; NULL when none. A photo wins over an icon. |
| `avatar_icon` | TEXT | Key of a built-in illustrated icon drawn on `avatar_color` (`cat`, `fox`, `owl`, `bear`, `panda`, `penguin`, `robot`, `rocket`, `planet`, `moon`, `crown`, `ghost`); NULL shows the person's initial. |

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | GUID, primary key |
| `display_name` | TEXT | Name shown on the profile picker and in Settings |
| `role` | TEXT | `Administrator`, `StandardUser` or `RestrictedProfile` (a Kids profile) |
| `created_at` | TEXT | Timestamp |
| `is_server_admin` | INTEGER | 1 when the profile may look after the whole Library. Defaults to 0. Setup sets it on the first administrator's profile. |
| `is_household_admin` | INTEGER | 1 when the profile looks after its own household. Defaults to 0. Setup sets it on the first administrator's profile. |
| `authorization_version` | INTEGER | Above zero, starts at 1, bumped on every change to the profile's administrator roles, feature and library access or admin PIN question, so open sessions re-check. |

These three columns (and the four `profile_*` access tables below) were added by upgrade step `0046_profile_held_access`: the profile holds the person's access and a user (`accounts`) is only a sign-in. Since step `0047_storage_epoch_profile_held_access` (storage epoch `guid-blob-v12-profile-held-access`) the Engine reads roles and access only from the profile being acted as, and writes them only there. Step `0048_drop_account_held_access` (storage epoch `guid-blob-v13-profile-held-contract`) then removed the earlier account-side model: the tables `account_profile_grants`, `account_feature_grants`, `account_library_hidden` (added by step 0045 and replaced by `profile_library_hidden`), `grant_admin_protections` and `grant_admin_unlocks`, and the columns `accounts.is_administrator`, `accounts.household_admin`, `accounts.household_id`, `accounts.grants_inherit_from_account_id` and `households.primary_account_id`. It rebuilt `accounts` so `profile_id` is NOT NULL. A user's household is its profile's household. A new sign-in's profile is set up once from how the sign-in was created, and a person who already exists is never changed by getting a sign-in. Export and import of people was removed by library access (D7); a full backup and restore is the only way to move people between Libraries.

The PIN is not stored on this row. It is the `profile_credentials` row described below.

### account_two_step

One row per account that started optional two-step codes. `account_id` (primary key, references `accounts`), `secret_protected` (the authenticator key, encrypted with Data Protection), `created_at`, `enabled_at` (null while setup is unconfirmed), `last_used_step` (the last accepted 30-second step, so a code cannot be reused).

### two_step_challenges

Pending second-step sign-ins. `token_hash` (SHA-256 of the one-time token), `account_id`, `ingress`, `device_id`, `device_name`, `client`, `failed_attempts`, `created_at`, `expires_at` (5 minutes), `consumed_at`.

### auth_sessions

Revocable, device-scoped sign-in sessions. A session remembers where it started.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | GUID, primary key |
| `account_id` | BLOB | Owning account |
| `active_profile_id` | BLOB | Profile in use |
| `token_hash` | TEXT | Hash of the session token (unique); the token itself is never stored |
| `authentication_method` | TEXT | `Password`, `Passkey`, `Oidc` |
| `issued_ingress` | TEXT | Where the sign-in came from: `this_computer`, `home_network` or `remote`. Default `home_network`, so rows from before this column existed are treated as home-only (fails closed). |
| `authenticated_at` | TEXT | When the person last proved it was them (password, passkey or provider sign-in, or a "Confirm it's you" check). Sensitive account actions need this within the last 10 minutes. NULL on older rows, which are read as `created_at`. |
| `created_at`, `last_seen_at`, `expires_at` | TEXT | Timestamps |
| `first_used_at` | TEXT | When the session was first used after sign-in. NULL means never used. At the session limit (`maximum_active_sessions`), never-used sessions end first, then sessions idle for 10 minutes or more, then the least recently seen. |
| `revoked_at`, `revoked_reason` | TEXT | NULL while active |
| `profile_pending` | INTEGER | 1 from an interactive sign-in until a profile of the household is chosen (the switch sets it back to 0). While 1 the Engine allows only the profile choice, a session check and sign-out |

### account_credentials

The account password (`credential_kind` = `Password`, one row per account). Only a hash is stored: `secret_hash`, `hash_scheme` and `hash_version`, with a `security_stamp`, the failed-attempt and lockout fields, and the usual timestamps.

### profile_credentials

A profile's PIN, when one is set (`credential_kind` = `ProfilePin`, one row per profile). Only a hash is stored: `secret_hash`, `hash_scheme`, `hash_version`, `security_stamp`, `failed_attempt_count`, `locked_until`, `created_at`, `updated_at` and `last_used_at`. Deleting the profile removes the row.

### account_notice_dismissals

Which one-time reminders an account chose **Not now** for, so the choice follows the person to any browser (for example the **Secure your account** card in the profile menu, key `secure-account`). One row per account and notice: `account_id` (deleting the account removes its rows), `notice_key` (primary key with `account_id`; the Engine accepts only known keys) and `dismissed_at`.

### profile_notice_dismissals

Which Settings attention lines (the Needs Review line and the missing tools line in the banner above every Settings page) a profile dismissed with the X, and what was true when it did, so a line returns only when something new needs attention. One row per profile and notice: `profile_id` (deleting the profile removes its rows), `notice_key` (primary key with `profile_id`; `needs-review` or `missing-tools`; the Engine accepts only known keys), `fingerprint` (the Needs Review count, or the sorted comma-joined ids of the missing tools; `0` or `none` once the problem was solved) and `dismissed_at`.

### account_passkeys

Passkeys a person added under **Settings > Security**. `credential_id` (BLOB, primary key), `account_id` (deleting the account removes its passkeys), `name`, `data_json` (the stored credential), `created_at`, and `last_used_at` (NULL until the passkey is used).

### account_external_logins

Linked sign-ins: an account at an outside provider, linked under **Settings > Security > Extra protection > Linked sign-ins**. `id`, `account_id`, `provider`, `issuer`, `subject` (unique together with `provider` and `issuer`), `email`, `display_name`, `linked_at` and `last_login_at`.

### password_recovery_codes

The saved one-time recovery codes for an account, made under **Settings > Security > Extra protection > Recovery codes**. `id`, `account_id`, `code_hash` (unique per account), `created_at`, `expires_at` and `consumed_at`. Only a hash is stored, and each code works once.

### password_reset_challenges

Email password-reset links, used only when email recovery is set up. `id`, `account_id`, `token_hash` (unique), `created_at`, `expires_at` (30 minutes) and `consumed_at`. Only a hash is stored, and each link works once.

### account_invitations

Invitation codes made by an administrator (**Users and Sign-in > Users**). `id`, `account_id`, `token_hash` (unique; only a fingerprint of the code is stored), `created_at`, `expires_at` (seven days) and `consumed_at`.

### profile_feature_grants

The features a profile may use: `profile_id` (references `profiles`, deleting the profile removes the rows) and `feature_id` (`read`, `watch`, `listen` or `view`, primary key together) and `granted_at`. Added by step `0046_profile_held_access`.

### profile_library_hidden

The libraries an administrator hid from a profile (simpler access D2, D11; the profile holds the access): one row per hidden library, `profile_id` (references `profiles`, deleting the profile removes its rows) and `library_id` (a 16-byte id, no foreign key; primary key together), `created_at` and `created_by_profile_id` (the administrator's profile that saved it; set to empty if that profile is deleted). A profile sees every library in the areas it has (`profile_feature_grants`) unless a row hides it, so a new library needs no row. Nothing is inherited from another profile. A Vault library is never listed and the repository refuses one. Server administrator profiles see every library whatever the rows say. Created by upgrade step `0046_profile_held_access`; it replaces both the earlier per-profile library grants and `account_library_hidden` (step 0045), which step `0048_drop_account_held_access` drops. Nothing was carried across. A library is visible to a profile only when it is also not hidden for the profile's household (`household_library_hidden`).

### household_library_hidden

The household ceiling (library access D2, LA1): the libraries the server administrator has hidden for a whole household. One row per hidden library: `household_id` (references `households`, deleting the household removes its rows), `library_id` (a 16-byte id, no foreign key; primary key together), `created_at` and `created_by_profile_id` (the administrator's profile that saved it; set to empty if that profile is deleted). A library is visible to a profile when it is **not hidden for the profile's household** and **not hidden for the profile** (`profile_library_hidden`); administrators see everything. Nothing is hidden by default, so a new library, household or profile needs no row. A Vault library is never listed (its access is per profile) and the repository refuses one. A household administrator can hide more for the household's profiles but cannot show what the household lacks (the Engine answers `409`). The rows are configuration and are part of a backup. Created by upgrade step `0067_household_library_hidden`, which also dropped `library_profile_grants` (the old "Selected profiles" table) with no conversion.

### profile_admin_protections

The "Ask for my PIN on admin pages" switch on one profile (off unless turned on). `profile_id` (primary key; references `profiles`), `is_enabled`, `unlock_mode` (`FixedDuration`, `UntilProfileSwitch` or `LockOnLeave`), `unlock_minutes`, `protection_version` and `updated_at`. The PIN asked for is the profile's own PIN; none is stored here.

### profile_admin_unlocks

An administrator unlock held by one signed-in session. `session_id` (primary key, references `auth_sessions`), `profile_id` (references `profiles`), `protection_version`, `method`, `granted_at` and `expires_at` (NULL when the unlock has no fixed end). Indexed by `ix_profile_admin_unlocks_profile`.

### client_devices

Paired phones and TVs, listed under **Settings > Security > Devices**. `id`, `application_id`, `account_id`, `profile_id`, `device_name`, `device_class` (`web`, `mobile`, `television` or `automotive`), `client_id`, `client_name`, `client_version`, `scopes`, `capabilities_json`, `created_at`, `last_seen_at`, `revoked_at` and `revoked_reason`. `backup_profile_id` is the profile a phone backs its photos up to (NULL means not chosen; it is cleared if that profile is deleted).

### device_pairing_requests

An app's request to pair, waiting for approval or already decided. `id`, `application_id`, `device_code_hash` and `user_code_hash` (both unique; only hashes are stored), `client_id`, `client_name`, `client_version`, `device_name`, `device_class`, `requested_scopes`, `approved_scopes`, `capabilities_json`, `status` (`pending`, `approved`, `denied`, `consumed` or `expired`), `poll_interval_seconds`, `last_polled_at`, `profile_id`, `account_id`, `approved_by_profile_id`, `created_at`, `expires_at`, `decided_at` and `consumed_at`.

### device_downloads

What a paired app reports it has stored on the device for offline use (`GET/POST /api/v1/devices/current/downloads`). One row per device and offline variant. `device_id` (references `client_devices`), `profile_id` (the profile the app was using when it reported; a device switching profile does not show the earlier person's rows), `asset_id` (references `media_assets`), `variant_id` (references `offline_variants`), `stored_bytes` (never negative; the Engine clamps it to the variant's size), `stored_at` (the device's time, never later than the Engine's clock) and `updated_at`. The primary key is (`device_id`, `variant_id`), so reporting the same variant again updates its row. A device holds at most 500 rows. Rows are removed with their device, profile, asset or variant. `offline_variants.expires_at` is written when a variant is prepared (`variant_retention_days`), and the cleanup service removes an expired variant, its file and any `device_downloads` row that still marks it as stored. Indexes: `idx_device_downloads_device_profile` (`device_id`, `profile_id`, `stored_at`) for the list and `idx_device_downloads_variant` (`variant_id`) for removal. The table is created by the baseline step.

### device_photo_downloads

The photos a paired app reports it keeps on the device (kind `photo` on `GET/POST /api/v1/devices/current/downloads`). One row per device, person and View item. `device_id` (references `client_devices`), `profile_id` (the person the app was using when it reported), `item_id` (references `local_items`), `stored_bytes` (never negative), `stored_at` (never later than the Engine's clock) and `updated_at`. The primary key is (`device_id`, `profile_id`, `item_id`), so reporting a photo again updates its row, and two people on one device can each keep the same shared photo. A photo never expires. Rows are removed with their device, profile or item. A device's `device_downloads` and `device_photo_downloads` rows together are limited to 500 listed entries. Indexes: `idx_device_photo_downloads_device_profile` (`device_id`, `profile_id`, `stored_at`) for the list and `idx_device_photo_downloads_item` (`item_id`). Created by the baseline step.

### setup_codes

One-time codes that let a person on the home network start first-run setup (`tuvima-admin setup code`). Only a hash is stored. At most one code is active; creating a new one invalidates the others.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | GUID, primary key |
| `code_hash` | TEXT | SHA-256 (hex) of the normalised 8-character code; the code itself is never stored |
| `created_at`, `expires_at` | TEXT | Timestamps; a code lives 30 minutes |
| `failed_attempts` | INTEGER | Wrong tries; the fifth invalidates the code |
| `consumed_at` | TEXT | Set when the code is used (single use) |
| `invalidated_at` | TEXT | Set when a newer code replaces it or it is retired after too many wrong tries |

---

## Operations

### data_store_upgrades

One row per upgrade step the data store has had (see [Changing the data store](../develop/changing-the-data-store.md)). The Engine reads it at every start to decide which steps still need to run, and refuses to start when the rows no longer match the steps it knows. The Settings "Data store version" line reads the highest row.

| Column | Type | Notes |
|---|---|---|
| `number` | INTEGER | Step number, primary key. Numbers run `1, 2, 3...` with no gaps |
| `name` | TEXT | Step name, such as `baseline` |
| `fingerprint` | TEXT | SHA-256 of the step's content when it ran, compared on every later start |
| `applied_at` | TEXT | ISO-8601 time the step ran |
| `engine_version` | TEXT | Version of the Engine that ran it |

### scheduled_task_runs

One row per run of a recurring background task (activity log cleanup, Local AI batches, plugin segment detection, and the other tasks listed by `GET /system/scheduled-tasks`). A run is written when it starts and finished when it ends; any run still marked `running` when the Engine starts is marked `interrupted`. Plugin job history is read from here, so it survives a restart. Webhook delivery ticks every few seconds and writes no rows; its last run comes from `application_webhook_deliveries`. When a run finishes, that task's rows older than 90 days are deleted and so is everything past its newest 500.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | 16-byte GUID, primary key |
| `task_key` | TEXT | Snake_case task key, such as `vibe_tags` or `plugin_segment_detection` |
| `plugin_id` | TEXT | Set when the run belongs to a plugin |
| `started_at` | TEXT | ISO 8601 UTC |
| `finished_at` | TEXT | ISO 8601 UTC; null while the run is going |
| `outcome` | TEXT | `running`, `succeeded`, `failed`, `skipped` or `interrupted` |
| `items_processed` | INTEGER | How many items the run handled, when that applies |
| `message` | TEXT | Plain-English result, failure message or skip reason |

**Indices:** `ix_scheduled_task_runs_task_started (task_key, started_at DESC)`. Added by upgrade step `0005_scheduled_task_runs`.

### approval_records

One append-only row each time a server administrator (or an application holding the matching permission) confirmed what an item can do before it was switched on: a plugin, a Local AI feature, an application credential, a webhook, or an integration whose existing keys gained permissions or that was switched back on while it held keys. Nothing updates or deletes a row; `IApprovalRecordRepository` offers add, list and exists only. `statement_json` is exactly what the person was shown, and `statement_hash` is the SHA-256 of its canonical form, so an item whose statement later changes (for example a plugin that now asks for another permission) is asked about again. No secret value is ever stored here: a webhook is recorded by its receiver's host only, and a credential's key is never part of its statement.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | 16-byte GUID, primary key |
| `item_kind` | TEXT | `plugin`, `ai_feature`, `application_credential`, `webhook` or `application` |
| `item_id` | TEXT | Plugin id, AI feature key, credential id, webhook id or (for `application`) application id |
| `statement_hash` | TEXT | SHA-256, lower-case hex |
| `statement_json` | TEXT | The statement as shown: title, kind label, what it can do, whether data leaves the machine and where, notes |
| `approved_by_account_id` | BLOB | The account that confirmed; null when an application confirmed |
| `approved_by_profile_id` | BLOB | The profile that was active when a person confirmed |
| `approved_by_application_id` | BLOB | The application that confirmed (a non-Dashboard caller with `plugins.manage` or `ai.manage`); null when a person confirmed |
| `approved_by_name` | TEXT | Profile display name (or application name) at the time |
| `approved_at` | TEXT | ISO 8601 UTC |

**Indices:** `ix_approval_records_item (item_kind, item_id, approved_at)`. Added by upgrade step `0017_approval_records`.

### ai_usage_records

One row for every request that reached the Local AI engine room: text written or structured by the local model, and text read from an image by Tesseract. A request is recorded whatever happened to it (succeeded, failed, unavailable, refused over a plugin's hourly allowance, cancelled or timed out); a request that was only deferred (a background poll that found the machine busy) writes nothing. A plugin's hourly allowance (`plugin_budget` in `config/ai.json`) is counted from these rows over the last rolling hour, so it survives an Engine restart; refused and unavailable requests do not count against it. The engine room writes the row; a failed write is logged as a warning and never fails the AI request. Rows older than 90 days are deleted by the daily activity cleanup. The prompt and the text read are never stored.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | 16-byte GUID, primary key |
| `started_at` | TEXT | ISO 8601 UTC; when the request reached the engine room |
| `caller_kind` | TEXT | `person`, `ingestion`, `library_background` or `plugin` |
| `caller_id` | TEXT | Stable key: a feature key such as `smart_labeling` or `vibe_tags`, a text-reading key, or the plugin manifest id |
| `ability` | TEXT | `write_text_quick`, `write_text_careful`, `write_text_long`, `extract_structured`, `classify`, `read_text_from_image` (and `transcribe_speech`, `find_similar`, `describe_image`, which are recorded for later and always answer unavailable) |
| `engine` | TEXT | `text_model`, `tesseract` or `none` |
| `wait_ms` | INTEGER | Milliseconds spent waiting in the queue |
| `duration_ms` | INTEGER | Milliseconds spent running once it had a turn |
| `outcome` | TEXT | `succeeded`, `failed`, `unavailable`, `refused_over_budget`, `cancelled` or `timed_out` |
| `message` | TEXT | Plain-English result or reason |

**Indices:** `ix_ai_usage_records_caller (caller_kind, caller_id, started_at)` and `ix_ai_usage_records_started (started_at)`. Added by upgrade step `0019_ai_usage_records`.

### application_webhook_attempts

One row per attempt to send an event to a webhook receiver, including the Send test button's test sends. `application_webhook_deliveries` keeps one row per event and only its latest state, so this table is the history the Integrations page shows: each webhook's last six attempts, with the time, the attempt number and what the receiver returned. A row never holds the event body, the signing secret or anything the receiver sent back; `result` is the short status text only. Rows older than seven days are removed with the delivery rows, but each webhook keeps its newest twenty attempts regardless of age. Deleting a webhook deletes its attempts.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | 16-byte GUID, primary key |
| `webhook_id` | BLOB | The webhook; rows are deleted with it |
| `delivery_id` | BLOB | The delivery row the attempt belongs to (no foreign key: the delivery may be pruned first) |
| `event_id` | BLOB | The event sent; for a test, a fresh id |
| `event_type` | TEXT | For example `ingestion.completed`; `webhook.test` for a test send |
| `attempt_number` | INTEGER | 1 for the first try of a delivery; always 1 for a test |
| `attempted_at` | TEXT | ISO 8601 UTC |
| `delivered` | INTEGER | 1 when the receiver accepted it |
| `result` | TEXT | Short plain text, such as `HTTP 503`, `Receiver unavailable` or `Delivery timed out` |
| `http_status` | INTEGER | The receiver's HTTP status code, when it answered |
| `duration_ms` | INTEGER | How long the send took |
| `is_test` | INTEGER | 1 for a Send test |

**Indices:** `ix_application_webhook_attempts_webhook_time (webhook_id, attempted_at DESC)`. Added by upgrade step `0022_webhook_delivery_attempts`.

### plugin_records

The records a plugin keeps for itself, written through the plugin's own record store (the `plugin-records` permission). One row per plugin, collection and key; the value is a JSON document the plugin wrote. The Library scopes every read and write to the calling plugin's id, so a plugin never sees another plugin's rows, and it limits each plugin to 64 KB per value, 10,000 records and 16 MB in total (a write over a limit is refused and the plugin's card shows Needs attention). Records survive an Engine restart and are not touched when a plugin is switched off. Removing a plugin's records when the plugin is uninstalled belongs to the plugin install and removal work, not to this table.

| Column | Type | Notes |
|---|---|---|
| `plugin_id` | TEXT | The plugin's manifest id; first part of the primary key |
| `collection` | TEXT | The plugin's own grouping name, 1 to 128 printable characters |
| `record_key` | TEXT | The key inside the collection, 1 to 128 printable characters |
| `value_json` | TEXT | The record, a JSON document, at most 64 KB |
| `size_bytes` | INTEGER | UTF-8 size of `value_json`, so a plugin's total is a cheap sum |
| `updated_at` | TEXT | ISO 8601 UTC |

**Key:** `PRIMARY KEY (plugin_id, collection, record_key)`. Added by upgrade step `0023_plugin_host_services`.

### plugin_activity

Each plugin's or metadata source's Activity list (Plugin architecture spec D10 and C17): one row per run that changed items, failure, deliberate skip (a Vault item, an audience, a library limit) or note. A failure always carries a cause from one fixed list in `reason` (`plugin_crashed`, `service_unreachable`, `service_refused`, `service_rate_limited`, `key_rejected`, `key_expired`, `tool_missing`, `local_ai_off`, `local_ai_busy`, `local_ai_over_allowance`, `item_unreadable`, `item_unsupported`, `limit_storage`, `limit_records`, `limit_requests`, `not_allowed_audience`, `not_allowed_vault`, `not_allowed_permission`, `dependency_off`, `library_data_store`, `library_disk`). The `detail` line is redacted of keys and personal paths before it is written, and a Vault item is recorded without its id. The Library keeps the newest 1,000 rows per plugin. Not part of backups; it fills again as plugins run.

| Column | Type | Notes |
|---|---|---|
| `id` | INTEGER | Primary key, auto-increment |
| `occurred_at` | TEXT | ISO 8601 UTC |
| `plugin_id` | TEXT | The plugin's manifest id, or the source's name |
| `plugin_version` | TEXT | The version that wrote the row |
| `kind` | TEXT | `run`, `failure`, `skipped` or `note` |
| `reason` | TEXT | The cause of a failure or skip; null otherwise |
| `item_id` | BLOB | The item concerned, null when none or when it is in a Vault section |
| `items_changed` | INTEGER | How many items a run changed |
| `summary` | TEXT | One plain sentence |
| `detail` | TEXT | A technical line, redacted |

**Indexes:** `(plugin_id, id DESC)` and `(occurred_at)`. Added by upgrade step `0051_plugin_activity`.

### plugin_library_limits

The libraries an administrator limited a plugin or metadata source to (Plugin architecture spec D6). A plugin or source with no rows works on every library it can work on; once libraries are chosen it works only on those. Vault libraries are refused when the choice is saved. Part of backups, because it is a setting.

| Column | Type | Notes |
|---|---|---|
| `plugin_id` | TEXT | The plugin's manifest id, or the source's name; first part of the primary key |
| `library_id` | TEXT | The library's id in `config/libraries.json` (the text `media_assets.library_id` holds) |

**Key:** `PRIMARY KEY (plugin_id, library_id)`. Added by upgrade step `0052_plugin_library_limits`.

### plugin_profile_connections

Accounts a person linked to a plugin for their own profile (Plugin architecture spec C13), such as a ListenBrainz token. The value is protected with this machine's Data Protection key before it is stored, so it is not readable here, is not part of a backup or a user export, and each person links again after a restore. Removing a profile or the plugin removes its rows.

| Column | Type | Meaning |
|---|---|---|
| `plugin_id` | TEXT | The plugin's manifest id |
| `profile_id` | BLOB | The profile that linked it (`profiles.id`, cascade delete) |
| `credential_name` | TEXT | The personal credential's name from the plugin's declaration |
| `secret_ciphertext` | TEXT | The value, protected with this machine's key |
| `linked_at` | TEXT | ISO 8601 UTC |

**Key:** `PRIMARY KEY (plugin_id, profile_id, credential_name)`. Added by upgrade step `0053_plugin_profile_connections`.

### plugin_outbox

The retry queue for plugins that send things to an outside service on a person's behalf (Plugin architecture spec C13). A play is queued here first, so a service that is offline never loses it or slows the player; the Library sends it when it can and tries again after 1 minute, 5, 30, 2 hours and then every 12 hours, and drops an item after a week or 12 tries. At most 5,000 items wait per plugin (the oldest go first) and each is at most 64 KB. Items are removed with their profile, when the profile unlinks its account, and when the plugin is removed. Not part of a backup.

| Column | Type | Meaning |
|---|---|---|
| `id` | BLOB | The item's id |
| `plugin_id` | TEXT | The plugin's manifest id |
| `profile_id` | BLOB | The profile it belongs to (`profiles.id`, cascade delete) |
| `payload_json` | TEXT | What to send, written by the plugin |
| `created_at` | TEXT | ISO 8601 UTC |
| `attempts` | INTEGER | How many deliveries have been tried |
| `next_attempt_at` | TEXT | ISO 8601 UTC; due once this has passed |
| `last_reason` | TEXT | The last failure word, or NULL |

**Indexes:** `(plugin_id, next_attempt_at)`. Added by upgrade step `0054_plugin_outbox`.

### plugin_display_contributions

What plugins want shown on the Library's own surfaces (Plugin architecture spec C21): a line on a Home slide, a chip or titled block on an item's page, a badge on a tile, a status line in Settings. A plugin supplies plain data and the Library draws it, so the data is small (name 64 characters, text 280, title 80, icon 40, at most 2,000 per plugin, the least recently updated going first) and is read without waiting on the plugin. Removed when the plugin is removed. Not part of a backup; each plugin publishes again when it next runs.

| Column | Type | Meaning |
|---|---|---|
| `plugin_id` | TEXT | The plugin's manifest id |
| `slot` | TEXT | The surface: HomeHero, HomeHeroCandidate, ItemHero, ItemSection, TileBadge or SettingsOverview |
| `subject_id` | TEXT | The item it is about, or empty for Settings |
| `key` | TEXT | The plugin's own name for it, so it can replace or withdraw it |
| `title` | TEXT | An optional heading |
| `text` | TEXT | The line or body |
| `icon` | TEXT | An optional icon name |
| `strength` | INTEGER | 0 to 100; orders contributions in the same slot |
| `from_local_ai` | INTEGER | 1 when Local AI produced it |
| `updated_at` | TEXT | ISO 8601 UTC |

**Key:** `PRIMARY KEY (plugin_id, slot, subject_id, key)`. **Indexes:** `(slot, subject_id)`. Added by upgrade step `0055_plugin_display_contributions`.

### plugin_event_cursors

Where each plugin has got to in the Library's event list (`application_events`): the sequence of the newest event already handed to it. The plugin event dispatcher reads events after this sequence, so a restart carries on where it stopped. A plugin that subscribes for the first time, or that is switched off and on again, starts at the newest event with no backlog.

| Column | Type | Notes |
|---|---|---|
| `plugin_id` | TEXT | The plugin's manifest id, primary key |
| `last_sequence` | INTEGER | The newest `application_events.sequence` already delivered |
| `updated_at` | TEXT | ISO 8601 UTC |

Added by upgrade step `0023_plugin_host_services`.

### notifications

The Dashboard inbox of the notification service: what the Library has told its administrators. A repeat of the same event about the same thing inside ten minutes is folded into one row instead of adding another.

| Column | Type | Notes |
|---|---|---|
| `id` | TEXT | GUID, primary key |
| `event_id` | TEXT | The event from the notification catalogue, for example `backup.failed` |
| `title` | TEXT | One short generic line |
| `body` | TEXT | A few plain sentences; shown in the Dashboard, and sent outside only when the administrator turns on "include details" |
| `group_key` | TEXT | `event_id` and what it was about; repeats share it |
| `occurrence_count` | INTEGER | How many times it happened in the grouping window |
| `created_at` / `updated_at` | TEXT | ISO 8601 UTC; `updated_at` moves forward when a repeat is folded in |
| `read_at` | TEXT | ISO 8601 UTC, null while unread; a folded repeat makes the row unread again |

**Indices:** `ix_notifications_updated` on `(updated_at DESC)` and `ix_notifications_group` on `(group_key, updated_at)`. The inbox keeps the newest 200 rows. Added by upgrade step `0027_notification_service`.

### notification_channel_state

How each outside channel (email, later channel plugins) is doing. A channel that fails five sends in a row switches itself off and shows Needs attention until an administrator turns it back on or a test send works.

| Column | Type | Notes |
|---|---|---|
| `channel_id` | TEXT | `email` or a channel plugin id, primary key |
| `consecutive_failures` | INTEGER | Failures since the last success |
| `state` | TEXT | `healthy`, `needs_attention` or `off` |
| `last_error` | TEXT | Plain words, never a secret or a server address; at most 300 characters |
| `last_success_at` / `last_attempt_at` / `switched_off_at` | TEXT | ISO 8601 UTC |

Added by upgrade step `0027_notification_service`.

### notification_rate_marks

Makes "at most one message per channel and event a minute" hold across restarts, and counts what was held back so the next message can say so.

| Column | Type | Notes |
|---|---|---|
| `channel_id`, `event_id` | TEXT | Primary key together |
| `last_sent_at` | TEXT | ISO 8601 UTC |
| `suppressed_count` | INTEGER | Messages held back since `last_sent_at` |

Added by upgrade step `0027_notification_service`.

### notification_settings

The administrator's choices as JSON rows (currently the single key `settings`): which channels each event goes to, quiet hours, and whether details may leave this computer (off by default).

| Column | Type | Notes |
|---|---|---|
| `setting_key` | TEXT | Primary key |
| `value_json` | TEXT | The settings document |
| `updated_at` | TEXT | ISO 8601 UTC |

Added by upgrade step `0027_notification_service`.

### media_operations (file intake rows)

The Engine's durable work queue. Each file noticed by ingestion has one row with `operation_type = ingestion.file`; the live ingestion page's numbers are worked out from these rows (see `IngestionQueueProgressRepository`). Only the columns that matter to that count are listed here.

| Column | Type | Notes |
|---|---|---|
| `status` / `stage` | TEXT | Where the file is: `stage` runs `discovered`, `queued`, `hashing`, `parsing`, `scoring`, `registered`, `queued_identity`, `completed`; `status` says whether it is queued, running, retrying or finished |
| `entity_id` / `entity_kind` | BLOB / TEXT | Set to the file's media asset id and `MediaAsset` the moment the asset is known (when it is registered, or the existing asset a duplicate or re-detected file points at). Empty before that |
| `result_summary` | TEXT | What the file ended as when it finished, for example `duplicate` or `same_path_redetected` |
| `requested_audience` | TEXT | For work a person asked for (a lyrics or subtitle refresh): `administrators`, `adults` or `everyone`, the widest group that person belongs to, so a plugin or source is used only within its own audience when the queued work really runs. NULL when the Library asked itself (added by upgrade step `0056_media_operation_requested_audience`) |
| `created_at` | TEXT | When the file was first noticed |

**Indices:** `idx_media_operations_entity` on `(entity_id)`, and for the live count `idx_media_operations_type_created` on `(operation_type, created_at)` and `idx_media_operations_type_status` on `(operation_type, status)`, so each second's look finds the files since the active period began, and the files still being worked on, without reading every intake row the Library has ever had.

### media_file_write_intents (the file write to-do list)

The durable to-do list for every write into a file (added by upgrade step `0038_batched_writeback`). A change is saved in Library at once and only adds or refreshes one row here; a scheduler works through the list in one pass every `run_interval_minutes` (`config/writeback.json`, 60 by default) or when an administrator chooses Write now. What a run writes is always built from Library's values at the moment of writing.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | Primary key |
| `target_kind` | TEXT | `media_asset` or `view_item` |
| `asset_id` | BLOB | FK -> `media_assets.id` (cascade); set only for `media_asset`. Unique, so a file has at most one waiting row |
| `local_item_id` | BLOB | FK -> `local_items.id` (cascade); set only for `view_item`. Unique |
| `generation` | INTEGER | Goes up with every newer change, so a write that finishes for an older generation never marks the newer one done |
| `operation_token` | TEXT | Identifies the change that last touched the row |
| `trigger` | TEXT | Why it was queued (`auto_match`, `manual_override`, `library_move`, `artwork`, `restore`, ...) |
| `status` | TEXT | `pending`, `writing`, `verified`, `blocked`, `unsupported` or `failed` |
| `attempts` / `lease_expires_at` / `last_error` | INTEGER / TEXT / TEXT | Claim attempts, when a claimed row may be claimed again (a stopped Engine's rows come back after this), and the last reason |
| `created_at` / `updated_at` / `completed_at` | TEXT | UTC; `completed_at` is when the last write finished |
| `move_step` | TEXT | A library move: `waiting` (Library's own filing, queued for the next run; the file has not moved and is not "in progress"), `held` (filing held until a newly added Managed folder passes its first check; never moved), or in progress: `requested`, `planned`, `moved` or `checked` (NULL when none). A person's move runs at once, a filing move is promoted to `requested` when a run starts, and a row with a move is not written until the move is done. Each step is written before and after it happens (`planned` before the disk is touched, `moved` once the file is at its new place, `checked` once the stored path matches and the file was verified there); a second move asked for meanwhile keeps the first step and is planned again from the data store when the first is checked |
| `move_origin` | TEXT | Who asked for the move: `person` (done at once) or `filing` (Library's own, done in the next run; set aside untouched when the file is in use). Set exactly when `move_step` is (added by upgrade step `0042_filing_moves_wait_for_the_batch`). Trigger `filing` means no tags were asked for on the row, so it is removed once the move is checked |
| `move_source_path` / `move_target_path` / `move_source_hash` | TEXT | What a move was about to do, so a stop part-way can be finished or undone from what is on disk. Cleared when the move is done. A move given up as failed keeps them for diagnosis, but no longer counts as a move in progress. The old and new place of every row with a move in progress is the waiting mark: the folder watcher, the rescan and the daily missing-file check leave those files alone |
| `move_generation` | INTEGER | The generation of the row when the move was planned. Finishing the move after a stop completes with it, so a change that arrived since is seen and the move is planned again from the data store |
| `recovery_attempts` | INTEGER | How many starts or passes found a part-way move that could not be settled (a drive that is offline does not count); the move is given up as failed at a cap |
| `next_attempt_at` | TEXT | UTC; when a failed or unsettled person's move is tried again (a minute, five minutes, half an hour, two hours, then failed). NULL means now |
| `claimed_run_id` | TEXT | The write run that last claimed the row; a run never takes a row it already took, however many chunks it needs |

**Indices:** unique partial indexes on `asset_id` and `local_item_id`, `ix_media_file_write_intents_dispatch` on `(status, lease_expires_at, updated_at)` and a partial index on `move_step`.

### file_write_runs

One row (`id = 1`) that remembers the schedule: `anchor_at` (when the scheduler first started, so the first interval counts from there), `last_run_started_at`, `last_run_finished_at`, and how many files the last run wrote (`last_run_written`), failed (`last_run_failed`) and kept in Library only (`last_run_kept`), and how many files it filed (moved) on their own (`last_run_filed`, added by upgrade step `0042_filing_moves_wait_for_the_batch`; counted apart from the tag writes).

### ingestion_log

One row per file processed through the ingestion pipeline.

| Column | Type | Notes |
|---|---|---|
| `id` | TEXT | UUID, primary key |
| `asset_id` | TEXT | FK -> `media_assets.id` |
| `file_path` | TEXT | Original file path |
| `fingerprint` | TEXT | SHA-256 hash |
| `outcome` | TEXT | `"Promoted"`, `"Rejected"`, `"NeedsReview"`, `"Duplicate"`, `"Failed"` |
| `outcome_reason` | TEXT | Natural-language explanation for non-promoted outcomes |
| `pipeline_state` | TEXT | JSON snapshot of pipeline stage completion |
| `created_at` | TEXT | Timestamp |

### deferred_enrichment_queue

Works queued for background enrichment (Pass 2).

| Column | Type | Notes |
|---|---|---|
| `id` | TEXT | UUID, primary key |
| `entity_id` | TEXT | |
| `entity_type` | TEXT | |
| `priority` | INTEGER | Lower = higher priority |
| `queued_at` | TEXT | Timestamp |
| `started_at` | TEXT | NULL until processing begins |

### review_queue

Items the Engine could not confidently match and that require human review.

| Column | Type | Notes |
|---|---|---|
| `id` | TEXT | UUID, primary key |
| `entity_id` | TEXT | |
| `entity_type` | TEXT | |
| `review_reason` | TEXT | Why this item is in the queue |
| `candidates` | TEXT | JSON array of ranked candidates |
| `created_at` | TEXT | Timestamp |
| `resolved_at` | TEXT | NULL until resolved or dismissed |
| `resolution_type` | TEXT | `"Selected"`, `"Provisional"`, `"Dismissed"`, `"SkippedUniverse"` |

### resolver_cache

Cached candidate lists from previous search/resolve operations.

| Column | Type | Notes |
|---|---|---|
| `cache_key` | TEXT | Hashed query parameters. Primary key. |
| `candidates` | TEXT | JSON array |
| `cached_at` | TEXT | Timestamp |

---

## Content

### entity_assets

One row per picture kept for an entity (cover art, headshot, background, logo and the like). The Engine keeps the original and, for every type except logos, four smaller copies so a screen can ask for the size it draws. The long edge of each copy is: tiny 96 px (faces and small thumbnails), small 320, medium 960, large 2160.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | Primary key; also the picture's address (`/stream/artwork/{id}`) |
| `entity_id`, `entity_type` | BLOB or TEXT, TEXT | Owner (a Work, Person, Universe or fictional entity) |
| `asset_type` | TEXT | `CoverArt`, `Headshot`, `Background`, `Logo` and similar |
| `local_image_path` | TEXT | The original file |
| `local_image_path_t` | TEXT | Tiny copy (96 px long edge). Added by upgrade step `0029_entity_asset_tiny_rendition`; empty until the copy exists. |
| `local_image_path_s`, `local_image_path_m`, `local_image_path_l` | TEXT | Small (320), medium (960) and large (2160) copies |
| `width_px`, `height_px`, `aspect_class`, `primary_hex`, `secondary_hex`, `accent_hex` | | Measured size and palette |
| `is_preferred`, `is_user_override` | INTEGER | The chosen picture for the slot, and whether the person chose it |

A missing copy is made the first time it is asked for (or by the start-up catch-up) and recorded here; until then the next bigger copy is sent. People's faces are `Headshot` rows like any other picture; a face saved before this was so is registered by the start-up catch-up.

### image_cache

Tracks managed entity artwork stored under `.data/assets`.

| Column | Type | Notes |
|---|---|---|
| `id` | TEXT | UUID, primary key |
| `entity_id` | TEXT | |
| `entity_type` | TEXT | |
| `image_type` | TEXT | `"CoverArt"`, `"Headshot"`, `"Banner"`, `"Logo"`, `"Backdrop"` |
| `file_path` | TEXT | Absolute path on disk |
| `source_url` | TEXT | Original provider URL |
| `provider_id` | TEXT | |
| `user_override` | INTEGER | 1 if uploaded by user. Protected from orphan sweep. |
| `phash` | TEXT | Perceptual hash for visual similarity matching |
| `created_at` | TEXT | Timestamp |
| `is_preferred` | INTEGER | 1 if this is the selected image for display |

### search_results_cache

Cached results from Intent Search queries.

| Column | Type | Notes |
|---|---|---|
| `cache_key` | TEXT | Hashed query. Primary key. |
| `results` | TEXT | JSON array of entity IDs |
| `cached_at` | TEXT | Timestamp |

### ui_settings_cache

Server-side cache of resolved UI settings per profile and device class.

| Column | Type | Notes |
|---|---|---|
| `profile_id` | TEXT | |
| `device_class` | TEXT | |
| `settings_json` | TEXT | Resolved merged settings |
| `computed_at` | TEXT | Timestamp |

### bridge_ids

External provider identifiers (ISBN, ASIN, TMDB ID, MBID) used for cross-provider linking.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | Primary key |
| `entity_id` | BLOB | Asset or Work the identifier describes |
| `id_type` | TEXT | Bridge key such as `isbn`, `asin`, `tmdb_id`, `musicbrainz_release_id` |
| `id_value` | TEXT | The identifier value |
| `wikidata_property` | TEXT | Matching Wikidata property, when there is one |
| `provider_id` | TEXT | Which provider supplied this ID. A Wikidata confirmation of the same value keeps the earlier non-Wikidata source, because Stage 2 resolves only from ids Wikidata did not supply. |
| `created_at` | TEXT | Timestamp |

**Unique:** `entity_id + id_type`

---

## Reader

### reader_bookmarks

Saved reading positions.

| Column | Type | Notes |
|---|---|---|
| `id` | TEXT | UUID, primary key |
| `asset_id` | TEXT | FK -> `media_assets.id` |
| `profile_id` | TEXT | FK -> `profiles.id` |
| `chapter_index` | INTEGER | |
| `cfi_position` | TEXT | EPUB Canonical Fragment Identifier of the bookmarked page (`epubcfi(...)`). The book reader writes `chapter_index` as 0 and puts "section name · percent" in `label`; rows from the retired reader hold offsets and are ignored by the book reader. |
| `created_at` | TEXT | Timestamp |

### audiobook_bookmarks

Saved playback positions in a video or an audiobook (the table keeps its audiobook name; a row works for any playable file). These are separate from EPUB reader bookmarks because they use seconds, optional chapter metadata, and the owning work. A bookmark is personal to `profile_id`, or global (`is_global = 1`): global bookmarks are seen by every profile that can see the title and are made and deleted only by a server administrator, with `profile_id` recording who made it. A bookmark on a Vault file is never listed, counted or found while Vault mode is off.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | UUID, primary key |
| `profile_id` | BLOB | FK -> `profiles.id` |
| `work_id` | BLOB | FK -> `works.id` |
| `asset_id` | BLOB | FK -> `media_assets.id` |
| `chapter_index` | INTEGER | Optional chapter index at the saved position |
| `chapter_title` | TEXT | Optional display chapter title |
| `position_seconds` | REAL | Exact audio position |
| `duration_seconds` | REAL | Optional asset duration |
| `label` | TEXT | Optional user-facing bookmark label |
| `note` | TEXT | The short note (up to 200 characters); on a bookmark saved before the longer description existed, its text |
| `description` | TEXT | Optional longer description (up to 4,000 characters) |
| `is_global` | INTEGER | 1 when shared with everyone (upgrade step 0032); 0 for a personal bookmark |
| `end_seconds` | REAL | Optional end of a bookmark that marks a part of the video (a scene), after `position_seconds` (upgrade step 0034); NULL for a plain bookmark |
| `created_at` | TEXT | Timestamp |

### bookmark_packs

One imported bookmark pack on one title for one profile (Videos spec D46, upgrade step 0034). The pack's bookmarks are read-only and live in `bookmark_pack_items`. `asset_id` is the file the pack was imported on: a pack on a file the caller cannot see (a Vault file without an unlocked session) is not listed, counted or found. A pack is never exported from the Vault.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | UUID, primary key |
| `profile_id` | BLOB | FK -> `profiles.id`; the profile that imported the pack |
| `work_id` | BLOB | FK -> `works.id` |
| `asset_id` | BLOB | FK -> `media_assets.id` |
| `name` | TEXT | The pack's name, shown on its bookmarks |
| `author` | TEXT | Optional author typed by whoever exported the pack |
| `content_hash` | TEXT | Fingerprint of the pack's content; unique per profile and title so the same pack is not added twice |
| `source_running_time_seconds` | REAL | Running time of the copy the pack was made on |
| `source_edition` | TEXT | Edition label of that copy |
| `is_aligned` | INTEGER | 1 when the pack was for a different cut and was aligned by chapter on import |
| `is_enabled` | INTEGER | 0 hides the pack's ticks and rows without removing it |
| `imported_at` | TEXT | Timestamp |

### bookmark_pack_items

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | UUID, primary key |
| `pack_id` | BLOB | FK -> `bookmark_packs.id` (cascade) |
| `position_seconds` | REAL | Time of the bookmark |
| `end_seconds` | REAL | Optional end, after the time |
| `chapter_title` | TEXT | Optional chapter name |
| `note` | TEXT | Short note (up to 200 characters) |
| `description` | TEXT | Longer description (up to 4,000 characters) |

### audiobook_chapter_title_overrides

Display-only audiobook track title overrides. These come only from manual entry, are keyed by asset plus track index, and never alter embedded media timings or write back to the source file.

| Column | Type | Notes |
|---|---|---|
| `work_id` | BLOB | FK -> `works.id` |
| `asset_id` | BLOB | FK -> `media_assets.id`; part of primary key |
| `chapter_index` | INTEGER | Embedded track index; part of primary key |
| `title` | TEXT | User-facing display title |
| `title_source` | TEXT | Always `Override` |
| `updated_at` | TEXT | Timestamp |

### music_play_active_segments

One in-flight music listening segment per profile. This transient row accumulates genuine forward playback time while excluding seeks and is replaced when the active track or queue item changes.

| Column | Type | Notes |
|---|---|---|
| `profile_id` | BLOB | FK -> `profiles.id`; primary key |
| `work_id` | BLOB | FK -> `works.id` |
| `asset_id` | BLOB | Optional FK -> `media_assets.id` |
| `queue_item_id` | BLOB | Player queue identity |
| `last_position_seconds` | REAL | Most recent observed transport position |
| `listened_seconds` | REAL | Credited playback time for this segment |
| `duration_seconds` | REAL | Track duration when known |
| `qualified` | INTEGER | Whether this segment has already incremented the play count |
| `last_heartbeat_at` | TEXT | Timestamp used for gap and seek detection |

### music_play_stats

Durable per-profile music play totals. A play qualifies after 30 seconds, or 50% of duration for a track shorter than 30 seconds.

| Column | Type | Notes |
|---|---|---|
| `profile_id` | BLOB | FK -> `profiles.id`; part of primary key |
| `work_id` | BLOB | FK -> `works.id`; part of primary key |
| `play_count` | INTEGER | Qualified play total |
| `last_played_at` | TEXT | Timestamp of the most recent qualified play |

### reader_highlights

Text highlights and annotations.

| Column | Type | Notes |
|---|---|---|
| `id` | TEXT | UUID, primary key |
| `asset_id` | TEXT | |
| `profile_id` | TEXT | |
| `chapter_index` | INTEGER | |
| `start_offset`, `end_offset` | INTEGER | Character offsets of the retired reader; 0 for rows made by the book reader |
| `cfi_range` | TEXT | EPUB CFI range of the highlighted text (`epubcfi(...)`). NULL for rows made by the retired reader, which the book reader does not show. |
| `selected_text` | TEXT | Highlighted text content |
| `note_text` | TEXT | User annotation. NULL if no note. |
| `color` | TEXT | Highlight colour as a hex code (one of the supported highlight colours) |
| `created_at` | TEXT | Timestamp |

### reader_statistics

Aggregated reading session data.

| Column | Type | Notes |
|---|---|---|
| `asset_id` | TEXT | |
| `profile_id` | TEXT | |
| `total_seconds` | INTEGER | Total reading time |
| `completion_pct` | REAL | 0.0 - 1.0 |
| `last_position_cfi` | TEXT | Last known reading position |
| `session_count` | INTEGER | Number of reading sessions |
| `last_opened_at` | TEXT | Timestamp |

### alignment_jobs

Whispersync audio-to-text alignment jobs.

| Column | Type | Notes |
|---|---|---|
| `id` | TEXT | UUID, primary key |
| `asset_id` | TEXT | |
| `status` | TEXT | `"Pending"`, `"Running"`, `"Completed"`, `"Failed"` |
| `alignment_data` | TEXT | JSON alignment map |
| `created_at` | TEXT | Timestamp |

---

## Companion

The shared player (`player_sessions` with its queue) holds one transport and one queue per profile. Live state per device, and the companion choices that read it, are kept apart so devices never overwrite each other.

### player_device_states

One row per device on a profile: what that device is playing, written from its own heartbeats. Rows not refreshed for 24 hours are removed when the profile writes another row. Readers treat a row as live only when it is playing or paused and was updated within 45 seconds.

| Column | Type | Notes |
|---|---|---|
| `profile_id` | BLOB | FK -> `profiles.id` (cascade); part of primary key |
| `device_id` | TEXT | The device id the client reported; part of primary key |
| `client` | TEXT | Client label, such as `web` |
| `player_session_id` | BLOB | The player session id the heartbeat carried, when any |
| `asset_id` | BLOB | FK -> `media_assets.id` (cascade) |
| `work_id` | BLOB | FK -> `works.id` (set null on delete) |
| `playback_state` | TEXT | `playing`, `paused` or `ended` (CHECK-constrained) |
| `position_seconds` | REAL | Exact position, 0 or more |
| `duration_seconds` | REAL | Optional, 0 or more |
| `updated_at` | TEXT | ISO 8601 UTC timestamp of the last heartbeat |

### companion_sessions

One row per companion device: either following another device or holding a position set by hand.

| Column | Type | Notes |
|---|---|---|
| `profile_id` | BLOB | FK -> `profiles.id` (cascade); part of primary key |
| `companion_device_id` | TEXT | The device that is following; part of primary key |
| `mode` | TEXT | `follow` or `manual` (CHECK-constrained) |
| `followed_device_id` | TEXT | Required for `follow`, null for `manual` |
| `manual_work_id` | BLOB | FK -> `works.id` (cascade); required for `manual`, null for `follow` |
| `manual_chapter_index` | INTEGER | Optional, 0 or more |
| `manual_position_seconds` | REAL | Optional, 0 or more |
| `updated_at` | TEXT | ISO 8601 UTC timestamp |

## User

### user_states

Per-profile, per-asset playback and reading state.

| Column | Type | Notes |
|---|---|---|
| `profile_id` | TEXT | |
| `asset_id` | TEXT | |
| `state_type` | TEXT | `"Reading"`, `"Watching"`, `"Listening"` |
| `position` | TEXT | Progress position (CFI for EPUB, seconds for audio/video) |
| `updated_at` | TEXT | Timestamp |

### user_taste_profiles

AI-generated per-user taste vectors, updated by the Taste Profiling feature.

| Column | Type | Notes |
|---|---|---|
| `profile_id` | TEXT | Primary key. FK -> `profiles.id`. |
| `genre_weights` | TEXT | JSON map of genre -> weight |
| `vibe_weights` | TEXT | JSON map of vibe tag -> weight |
| `creator_weights` | TEXT | JSON map of person QID -> weight |
| `computed_at` | TEXT | Timestamp |

---

## Activity

### adaptive_hls_packages

Lifecycle index for prepared adaptive playback packages. Media bytes remain on disk under the configured variant cache; SQLite stores only package identity, state, size, and reclamation metadata.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | Package UUID, primary key. |
| `asset_id` | BLOB | Owning media asset; cascade-deleted with the asset. |
| `source_hash` | TEXT | Source fingerprint that invalidates a package when the media changes. |
| `profile_key` | TEXT | Adaptive packaging profile identity. |
| `status` | TEXT | `preparing`, `ready`, `failed`, or `deleting`. |
| `root_path` | TEXT | Managed package directory below the HLS variant cache. |
| `total_bytes` | INTEGER | Completed package size used for bounded LRU cleanup. |
| `created_at`, `last_accessed`, `completed_at` | TEXT | Lifecycle and eviction timestamps. |
| `last_error` | TEXT | Most recent package preparation failure. |

The unique key is `(asset_id, source_hash, profile_key)`. Cleanup scans the `(status, last_accessed)` index.

### system_activity

The Activity log's store: one row per thing the Library did (added media, Needs Review, failures, system events such as a missing tool, and photo or personal video counts). Pruned automatically per `config/maintenance.json`.

| Column | Type | Notes |
|---|---|---|
| `id` | INTEGER | Auto-increment primary key |
| `occurred_at` | TEXT | UTC timestamp |
| `action_type` | TEXT | Kind of event (for example `MediaAdded`, `MediaFailed`, `ToolMissing`, `PersonalMediaAdded`, `PersonalTrashEmptied`) |
| `collection_name` | TEXT | The headline the log shows: a title, a file name or a count such as "3,000 photos" |
| `entity_id` | BLOB | Related entity, when there is one |
| `entity_type` | TEXT | Kind of related entity (for example `Work`, `MediaAsset`, `PersonalMedia`) |
| `profile_id` | BLOB | The profile that caused it, when a person did |
| `changes_json` | TEXT | Event-specific facts (for example the media type, photo and video counts, or for `PersonalTrashEmptied` the item count, bytes freed and skipped count: never a file name or title) |
| `detail` | TEXT | The plain explanation under the headline |

**Indexes:** `action_type`, `occurred_at`. There is no run or batch link (removed by upgrade step 0018).

### transaction_log

Ordered log of all database writes, used for debugging and rollback analysis.

| Column | Type | Notes |
|---|---|---|
| `id` | INTEGER | Auto-increment primary key |
| `table_name` | TEXT | Which table was modified |
| `row_id` | TEXT | The affected row's primary key |
| `operation` | TEXT | `"INSERT"`, `"UPDATE"`, `"DELETE"` |
| `old_values` | TEXT | JSON snapshot before change. NULL for INSERT. |
| `new_values` | TEXT | JSON snapshot after change. NULL for DELETE. |
| `created_at` | TEXT | Timestamp |

### personal_activity

Each person's own record of what they watched, listened to and read, and what they added to My list (Settings > Personal > Activity). Added by upgrade step `0009_personal_activity`. Every row keeps a **snapshot** of the item as it was at the time, so history still reads correctly after the media file or the work is deleted; for that reason `work_id` and `asset_id` have no foreign keys. Rows are **kept forever**: nothing prunes them, and only a server administrator removes history, by hand (**Delete history...**, which is logged). Photos and the View area are never recorded.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | Primary key. |
| `profile_id` | BLOB | The person; deleting the profile removes their rows. |
| `kind` | TEXT | `watch`, `listen`, `read` or `my_list`. |
| `telemetry_session_id` | BLOB | The `playback_telemetry_sessions.id` this row follows (unique, no foreign key); makes repeated writes idempotent. NULL for `my_list`. |
| `music_session_id` | BLOB | Shared by the songs of one music session: songs less than 30 minutes apart with no other activity by the person in between. NULL for everything else. |
| `started_at`, `ended_at` | TEXT | UTC. `ended_at` is the last time the session was seen while it is still open. |
| `played_seconds` | INTEGER | Time actually played or read. |
| `progress_pct` | REAL | Furthest point reached, 0 to 100. NULL when the duration is unknown or for `my_list`. |
| `finished` | INTEGER | 1 when the session completed or reached 99.5%. |
| `device_name`, `device_kind` | TEXT | The paired device (`web`, `mobile`, `television`, `automotive`) or the browser client. |
| `work_id`, `asset_id` | BLOB | Snapshot: the work and file at the time. No foreign keys. |
| `media_type` | TEXT | `Movies`, `TV`, `Books`, `Comics`, `Audiobooks` or `Music`. |
| `title`, `subtitle`, `parent_title`, `year` | TEXT, TEXT, TEXT, INTEGER | Snapshot of the item's current canonical values: the title (the show for an episode), the episode label (season and episode, then its name), the author or the artist, the album, the year. |
| `artwork_key` | TEXT | The `personal_activity_artwork.key` of the small cover copy. No foreign key. |

**Indexes:** `(profile_id, started_at DESC)`, `(profile_id, work_id)`, `(started_at)`, `(music_session_id)`.

### personal_activity_artwork

The small pictures `personal_activity` rows point at: a copy of the cover with a longest edge of at most 240 px (JPEG, or WebP when the cover has transparency), made once when the activity began and shared by every row with the same source cover. `key` is the SHA-256 of the source cover file (primary key), with `image` (BLOB), `content_type`, `width`, `height` and `created_at`. Deleting history removes the pictures no row uses any more.

### profile_hero_settings

How a person wants the Home hero (the big banner at the top of Home) to behave. Added by upgrade step `0060_profile_hero_settings`. One row per profile; a profile with no row gets the defaults (hero on, five slides, every type, in-progress items first and new items after). The settings only narrow what the hero rules choose from. Deleting a profile removes its row.

| Column | Type | Meaning |
|---|---|---|
| `profile_id` | BLOB | The profile. References `profiles(id)` with `ON DELETE CASCADE`. Primary key. |
| `hero_enabled` | INTEGER | 1 shows the hero, 0 hides it and Home starts at the first row. |
| `slide_count` | INTEGER | How many slides at most, 1 to 5. |
| `allow_watch` / `allow_read` / `allow_listen` | INTEGER | 1 lets movies and shows / books and comics / music and audiobooks appear in the hero. |
| `continued_only` | INTEGER | 1 shows only things the person has started; 0 adds new items after them. |
| `updated_at` | TEXT | ISO 8601 UTC. |

### profile_hero_plugin_switches

Which plugins' Home slides a person switched off for themselves (Home page redesign spec WP8). Added by upgrade step `0061_profile_hero_plugin_switches`. A row means the profile does not want that plugin's slides; no row means they are on. Deleting a profile removes its rows.

| Column | Type | Meaning |
|---|---|---|
| `profile_id` | BLOB | The profile. References `profiles(id)` with `ON DELETE CASCADE`. Part of the primary key. |
| `plugin_id` | TEXT | The plugin's manifest id. Part of the primary key. |
| `updated_at` | TEXT | ISO 8601 UTC. |

### profile_grid_preferences

The order a person last chose on each "View all" grid (Watch, Read, Listen, My list, Liked), remembered per profile so it follows them to every device. Added by upgrade step `0016_profile_grid_preferences`. One row per profile and grid; a grid with no row uses its default order. Deleting a profile removes its rows.

| Column | Type | Meaning |
|---|---|---|
| `profile_id` | BLOB | The profile. References `profiles(id)` with `ON DELETE CASCADE`. Part of the primary key. |
| `grid_key` | TEXT | `watch`, `read`, `listen`, `my-list` or `liked`. Anything else is refused by a CHECK. Part of the primary key. |
| `sort` | TEXT | `added`, `title`, `year` or `played`. Anything else is refused by a CHECK. |
| `updated_at` | TEXT | ISO 8601 UTC. |

### profile_taste_overrides

What a person removed from, or added to, the taste the Library learned about them (Settings > Personal > Overview > Taste). Added by upgrade step `0010_profile_taste_overrides`. The learned taste (the taste profiler's reading of their history) is never changed; these rows are applied on top whenever taste is shown, so a removed value is hidden and an added value is shown as "Added by you". One row per `profile_id` (cascades on profile deletion), `taste_group` (`genre`, `mood` or `era`) and `value` (trimmed and lower-cased, the way the profiler writes its keys), with `action` (`removed` or `added`) and `created_at` (UTC). Setting a value again replaces its row. Only the signed-in person writes their own rows.

## Vault

Added by upgrade step `0030_vault_access` (Videos spec WP9). A **Vault library** is a library whose items are absent for anyone without an unlocked Vault mode session. An item is a Vault item because its `media_assets.library_id` appears in `vault_libraries`; that table is the single source of truth (there is no flag on the item). Rules that must hold whichever service writes are enforced by triggers, so a new code path cannot forget them.

| Table | Meaning |
|---|---|
| `vault_libraries` | One row per Vault library: `library_id` (primary key, text), `lookup_source` (`none` or `stash_box`; the only sources a Vault library may use), `lookup_server_id` (the chosen server), `adults_confirmed_at`, `created_by_profile_id`, `created_at`, `updated_at`. A library that already holds items cannot be marked, and a Vault library that holds items cannot be unmarked. |
| `profile_nsfw_settings` | A profile's Vault mode: `access_mode` (`off`; `pin`, which asks for the profile PIN and can only be saved for a profile that has one; `allowed`, which needs no PIN) and `idle_lock_minutes` (5, 15, 30 or 60; 0 means never, for `allowed` only; the default is 60). There is no separate Vault PIN: the profile PIN in `profile_credentials` is the only PIN. No row means Off. A Kids profile or a profile with a content limit can never be given a mode other than `off` (trigger). |
| `nsfw_session_unlocks` | One row per device session that has switched Vault mode on: `session_id`, `profile_id`, `method` (`pin` or `allowed`), `unlocked_at`, `last_activity_at`, `expires_at` (the sliding idle limit; NULL means never). Triggers delete the row when the session changes profile, or the profile's mode, PIN, role or content limit changes. |
| `vault_performers`, `vault_studios`, `vault_tags` | Names that exist only inside the Vault, never shared with the People, Series or Tags of ordinary libraries. |
| `vault_asset_performers`, `vault_asset_studios`, `vault_asset_tags` | Links from a Vault file to those names. A name with no remaining link is removed (trigger). |
| `vault_artwork` | Artwork for a Vault file, performer or studio, stored only here (never in the shared artwork tables) and removed with its owner (trigger). |

**Lookup and Identify (upgrade step `0031_vault_lookup`, Videos spec WP12).** Four more tables hold what the Library knows about how each Vault video was matched. Each refuses a row for a file that is not in a Vault library (before-insert trigger) and goes with its file (cascade).

| Table | Meaning |
|---|---|
| `vault_matches` | One row per matched Vault video: `asset_id` (primary key), `server_id`, `record_id`, `matched_by` (`fingerprint`, `product_code`, `title` or `identify`), `applied_via` (`automatic` or `identify`), `matched_at`. A Vault video with no row is Unmatched; nothing is queued in Needs Review for it. |
| `vault_field_sources` | Where each field came from: one row per `asset_id` and `field` (`title`, `description`, `release_date`, `cover`, `studio`, `performers`, `tags`) with the server, record, how it was matched, how it was applied, the language noted for the text, and when. An Identify replaces the rows of the fields that were ticked. |
| `vault_file_fingerprints` | Fingerprints computed on this machine (`OSHASH`, `MD5`, `PHASH`) with the file size they were computed at, kept so a big file is not read twice. Computed and sent only while the administrator's separate fingerprint permission is on. |
| `vault_activity` | The quiet Vault activity list: `kind` (`added`, `matched`, `unmatched`, `identified`, `failed`), the file's own name, a fixed plain-words detail (never a title or path) and the time. Shown only inside Vault mode to an administrator. |

**Sections and moves (upgrade steps `0064` and `0065`, Vault sections and Move to Vault).** Three more tables.

| Table | Meaning |
|---|---|
| `vault_section_grants` | Which Vault sections (Vault libraries) one profile may open: `profile_id`, `library_id`, `granted_by_profile_id`, `granted_at`. Nothing is granted by default, an administrator opens every section without a row, and a Kids or content-limited profile can never hold one. Read live on every request. Kept in a backup (it is a setting, not catalogue). |
| `vault_moves` | One row per Move to Vault or Move out job, saved before any file is touched: `direction` (`in` or `out`), `kind_key`, the source and destination library and folder, `remove_read_only_originals` (the separate D30 choice), `state` (`queued`, `running`, `done`, `attention` or `failed`), a plain `message` and times. Never holds a title. Not kept in a backup (transient). |
| `vault_move_items` | One row per title in a job: `position`, the work and asset ids, `new_asset_id`, the source path and final file name, size and `content_hash`, the sidecar list, a snapshot of the profiles' progress, ratings, bookmarks and highlights (`snapshot_json`), `original_left` (the copy is in place but the original could not be removed) and `state` (`planned`, `copied`, `retired`, `published`, `done`, `skipped`, `failed`) with a fixed `reason`. Saved after every step so a crash resumes. Not kept in a backup (transient). |

**Vault bookmarks after a restore.** Step `0035_vault_pending_bookmarks` added a Vault-only waiting table; step `0040_drop_vault_pending_bookmarks` drops it. A restored Vault bookmark now waits in `waiting_personal_state` (below) like every other bookmark, marked `is_withheld`, and the Vault settings count reads those rows.

**State waiting for its title after a restore (upgrade step `0039_waiting_personal_state`).** A backup holds a person's own state against how to recognise the title, not against the title's row. A restore puts settings straight back and parks every row that points at a title here; the Engine writes a row back when all its titles are in the Library, and clears it when it has waited longer than the waiting period (`missing_file_retention_days`, 90 by default, read live so a change affects rows already waiting). The same step adds `idx_editions_wikidata_qid` on `editions(wikidata_qid)`, so a title can be looked up by the Wikidata id of its edition without reading every edition. Both tables are empty on a data store that was never restored, and both are Configuration in the backup classification, so a backup taken while items still wait keeps them.

| Table | What it holds |
|---|---|
| `backup_restores` | One row per restore: `restored_at`, `backup_file_name`, `backup_created_at`, `applied_rows` (rows put straight into place), `waiting_rows` (rows that started waiting), and `attached_rows`, `yielded_rows` (rows removed because the person made the same row since the restore; theirs stays) and `purged_rows`, which grow as the waiting area empties. |
| `waiting_personal_state` | One parked row: `restore_id` (cascade), `group_kind` (`configuration`, `personal`, `correction`, `choice`), `table_name`, `profile_id` (the owner, when the table has an owner column), `is_withheld` (a title it refers to is one a backup may not name), `row_json` (the row's columns and values in the backup's value encoding, each title reference replaced by the index of its title in `titles_json`, plus the rows that hang off it), `titles_json` (the recognition data of those titles), `created_at` (when it started waiting; the waiting period counts from here, and a row carried back by a later backup keeps it), `last_checked_at`. Indexed by `created_at` and `profile_id`. |

**Skipped writes (triggers):** `trg_application_events_no_vault` stops an event about a Vault item from reaching `application_events` (and so apps and webhooks); `trg_personal_activity_no_vault` stops a Vault file from entering anyone's activity history. A skipped write is simply not stored. A file cannot move into or out of a Vault library once it exists (`trg_media_assets_vault_library_fixed`).

## Related

- [Engine API Reference](api-endpoints.md)
- [Priority Cascade Engine](../architecture/scoring-and-cascade.md)
- [Settings Architecture and Review Queue](../architecture/dashboard-ui.md)

### series_manifest_hydrations

Series-level cache and provenance for Wikidata series manifests. One row is stored per shelf (the collection holding the manifest), so a book Series and an audiobook Series with the same QID each keep their own manifest and later sibling imports can link against the cached named checklist before deciding whether a Wikidata refresh is needed. Upgrade step `0012_series_identity_by_media_type` changed the key from the series QID alone to the shelf plus the series QID.

| Column | Type | Notes |
|---|---|---|
| `series_qid` | TEXT | Wikidata QID for the series. Part of the primary key (`collection_id`, `series_qid`); also indexed on its own. |
| `collection_id` | TEXT | FK to `collections.id` for the local ContentGroup/series collection. Part of the primary key. |
| `series_label` | TEXT | Localized display name returned by Wikidata. |
| `manifest_source` | TEXT | Source library, normally `Tuvima.Wikidata`. |
| `manifest_version` | TEXT | Source package/version metadata. |
| `manifest_hash` | TEXT | Hash of the fetched manifest payload. |
| `known_item_qids_hash` | TEXT | Hash of the ordered known item QID set. |
| `warnings_json` | TEXT | Manifest-level warnings from Wikidata modeling. |
| `api_metadata_json` | TEXT | Request options used for the fetch. Whether a collection is a parent collection (for example Memory's Legion) is not stored here: it is read from the data, as a collection that another Series manifest of the same media type lists as a collection item. |
| `last_hydrated_at` | TEXT | Last successful manifest hydration timestamp. |

### series_manifest_items

Named factual entries in a Wikidata series manifest, including works the local library does not own. Missing entries are represented here, not as fake media files.

| Column | Type | Notes |
|---|---|---|
| `collection_id` | TEXT | FK to `collections.id`. |
| `series_qid` | TEXT | Wikidata QID for the parent series. |
| `item_qid` | TEXT | Wikidata QID for the series entry. Unique with `collection_id`. |
| `item_label` | TEXT | Localized item name used for owned/missing lists. |
| `item_description` | TEXT | Optional short description when fetched. |
| `media_type` | TEXT | Tuvima media type context that triggered hydration. |
| `raw_ordinal` | TEXT | Raw Wikidata ordinal qualifier. |
| `parsed_ordinal` | REAL | Parsed numeric ordinal when available. |
| `ordinal_scope_qid` | TEXT | Container QID that supplied the ordinal, so an anthology position is not presented as a broader-series position. |
| `sort_order` | REAL | Display order for the manifest list. |
| `publication_date` | TEXT | Publication date from Wikidata when available. |
| `previous_qid`, `next_qid` | TEXT | Previous/next chain links when modeled. |
| `parent_collection_qid`, `parent_collection_label` | TEXT | Parent collection for expanded collection entries such as short fiction. |
| `membership_scope` | TEXT | Structural relationship scope: `MainSequence`, `Supplementary`, `CollectedContent`, `BroaderContext`, or `Unpositioned`. Main totals exclude non-main scopes without deleting their rows. |
| `source_properties_json` | TEXT | Wikidata properties that included this row. |
| `relationships_json` | TEXT | Relationship evidence from Tuvima.Wikidata. |
| `order_source` | TEXT | How ordering was determined. |
| `ownership_state` | TEXT | `Owned`, `Missing`, `Provisional`, or `Ambiguous`. |
| `linked_work_id` | TEXT | FK to `works.id` when exactly one local work matches. |

### backup_settings

The backup schedule, how many backups to keep, which destinations are switched on, the destinations an administrator added, and the last restore check (JSON rows; currently the single key `settings`). Backups are never encrypted, so no key is kept; a destination's own login is stored encrypted with the machine key.

| Column | Type | Notes |
|---|---|---|
| `setting_key` | TEXT | Primary key |
| `value_json` | TEXT | The settings document |
| `updated_at` | TEXT | ISO 8601 UTC |

Added by upgrade step `0028_backup_destinations`.

### backup_copies

One row per copy sent to a destination, so the Backup destinations section can say when the newest copy arrived and whether it was read back and matched.

| Column | Type | Notes |
|---|---|---|
| `id` | TEXT | Primary key |
| `destination_id` | TEXT | `local` or the destination's own id |
| `remote_name` | TEXT | File name at the destination |
| `size_bytes`, `sha256` | INTEGER, TEXT | What was sent |
| `state` | TEXT | `sent`, `verified`, `failed` or `deleted` |
| `verified_at`, `last_error` | TEXT | When it was read back; plain-words reason when it failed |
| `created_at` | TEXT | ISO 8601 UTC |

Added by upgrade step `0028_backup_destinations`.
