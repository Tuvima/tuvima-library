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

- `src/MediaEngine.Storage/Schema/schema.sql`
- `src/MediaEngine.Storage/SchemaMigrator.cs`
- `src/MediaEngine.Storage/StorageEpochGuard.cs`

SQLite database located at `.data/database/library.db` (path set in `config/core.json`).

Latest storage epoch: **guid-blob-v10-graph-timeline-lore**. Fresh databases are initialized from
`src/MediaEngine.Storage/Schema/schema.sql`; obsolete development epochs are
rejected until an explicit destructive reset and reingest, rather than being
partially migrated in place.

**Conventions:**
- Internal UUIDs are stored as 16-byte SQLite `BLOB` values in RFC4122/network byte order. API JSON still serializes GUIDs as strings.
- External identifiers remain `TEXT`, including `wikidata_qid`, provider item IDs, URLs, hashes, plugin IDs, cache keys, and provider-specific IDs.
- Booleans stored as `INTEGER` (`0` = false, `1` = true)
- Timestamps stored as `TEXT` in ISO-8601 format (`2026-03-29T14:00:00Z`)
- Foreign keys enabled via `PRAGMA foreign_keys = ON`
- `canonical_values` is scalar-only. Multi-valued keys from `MetadataFieldConstants.MultiValuedKeys` are stored only as rows in `canonical_value_arrays`.

Startup safety:

- Current databases record `storage_metadata.storage_epoch = guid-blob-v10-graph-timeline-lore`.
- Older epochs, including the legacy TEXT-GUID database and `guid-blob-v1`, are
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
| `media_type` | TEXT | Primary media type for this collection |
| `description` | TEXT | Wikipedia or provider description |
| `sort_title` | TEXT | Normalized title for alphabetical sorting |
| `created_at` | TEXT | Timestamp |
| `updated_at` | TEXT | Timestamp |
| `last_enriched_at` | TEXT | Timestamp of last Wikidata enrichment |

**Indices:** `wikidata_qid`, `parent_collection_id`, `collection_type`

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

**Indices:** `wikidata_qid`, `collection_id`, `media_type`, `status`

Note: newer media library surfaces rely on a shared projection layered over work state, review queue state, identity job state, and canonical artwork flags. The `status` column remains part of the stored model, but it is no longer the only source of truth for browse visibility.

### editions

A specific version of a Work (e.g., "4K HDR Blu-ray Remux", "First Edition Hardcover").

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | Internal UUID, primary key |
| `work_id` | BLOB | FK -> `works.id` |
| `title` | TEXT | Edition label |
| `created_at` | TEXT | Timestamp |

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
| `sort_order` | INTEGER | Position within the collection |

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
  metadata JSON.
- `local_files` stores one exact content hash, size, MIME type, extension, and
  creation time. Hash equality does not imply shared logical ownership.
- `local_file_sources` retains every observed library/source/device path and
  timestamps. Its unique key is library plus case-insensitive path.
- `local_item_files` relates logical items to physical files with roles such as
  primary, original, Live Photo video, RAW, JPEG, sidecar, audio companion, or
  derivative. Only one primary is allowed per item.
- `local_item_tags` stores case-insensitive user/local tags.
- `local_item_search_keys` maps BLOB item IDs to FTS rowids, while the
  `local_item_search` FTS5 table indexes title, filename, MIME/media kind, date,
  device, location, document text, and tags.
- `local_item_annotations` is reserved for provenance-aware extracted/reviewed
  facts. It stores kind, value, confidence, source, model provenance, and review
  time; the table does not perform or imply face recognition, OCR, captions,
  object detection, or semantic processing.

GPS and annotation indexes support Places and evidence-based People discovery.

### Source timeline policy and Shared Library originals

`view_source_policies` stores whether each source participates in the Photos
timeline. Browser uploads default into Timeline; additional linked and managed
folders remain available in Folders and opt in explicitly.

`view_folder_pins` stores profile-private shortcuts by source and relative path.
`view_folder_timeline_policies` stores source-owner branch rules with the
validated absolute prefix used by the timeline query; the most-specific
ancestor rule wins over the source default.

`view_shared_library` holds one Shared Library per household (`household_id` is the primary key, `library_id` is unique and immutable), created the first time the household needs it. The server's own Shared Library is the one that belongs to the server administrator's household. Upgrade: a data store from before household administrators had one row for the whole server; the startup migration moves that row, keeping its library identity, to the server administrator's household and recreates the View scope triggers without the single-row condition. A personal space can never use any Shared Library's identity. Shared files of the server's household stay in the original Shared folder; other households' files live under `Shared/Households/<id>` so one household's files are never indexed into another's library.

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
View enabled, open the household's Shared Library, send items to it, and share
Galleries. Opening and sending default on (sending stays off for a child profile
until a household administrator allows it). Reviewing contributions is not a
per-profile setting: the former `review_shared_library_contributions` column is
dropped once on upgrade, and household administrators review.

`profile_view_preferences` stores the last Shared/Mine/Profile scope and
compact/comfortable/relaxed timeline density. The Profile form requires a
profile ID; other forms forbid one. The authorization layer owns fallback when
a saved scope is no longer permitted.

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

The winning multi-valued claims (genres, authors, vibe tags, cast members). Each value is one row with an ordinal and optional QID; packed delimiter strings are not supported.

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

### search_index

FTS5 full-text search index with trigram tokenizer (migration M-062). Handles CJK and languages without word boundaries. Short queries under 3 characters fall back to a `LIKE` scan.

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

Universe-level root records with Wikidata provenance.

| Column | Type | Notes |
|---|---|---|
| `id` | TEXT | UUID, primary key |
| `collection_id` | TEXT | FK -> `collections.id` (the ParentCollection) |
| `wikidata_qid` | TEXT | |
| `franchise_qid` | TEXT | Wikidata P8345 franchise identifier |
| `series_qid` | TEXT | Wikidata P179 series identifier |

### qid_labels

Cached Wikidata display labels for QIDs, avoiding repeated API lookups.

| Column | Type | Notes |
|---|---|---|
| `qid` | TEXT | Primary key |
| `label` | TEXT | Display label in the configured metadata language |
| `fetched_at` | TEXT | Timestamp |

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

One row per person who signs in. Every account has an email: there are no email-less "local-only" accounts. A data store from before this rule is migrated at startup (each email-less account's profiles move into the server administrator's household, the email-less account and its sessions are removed, and the table is rebuilt with the required email).

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | GUID, primary key |
| `email` | TEXT | Sign-in email as entered. Required. |
| `normalized_email` | TEXT | Lower-cased form used for lookups. Required and unique. |
| `is_enabled` | INTEGER | 1 when the account can sign in |
| `is_administrator` | INTEGER | 1 when the account is eligible for administration |
| `authorization_version` | INTEGER | Bumped whenever access changes, so open sessions re-check |
| `created_at`, `updated_at` | TEXT | Timestamps |
| `household_id` | BLOB | The household the account belongs to (see `households`). Filled by the startup migration and by the repository when an account is saved, so it is set on every account the Engine writes. |
| `grants_inherit_from_account_id` | BLOB | When set, the account has no feature or library grants of its own and follows those of the named account (a person's own sign-in follows the household's main sign-in). Read at use time, so changes to the main sign-in reach the person. Cleared to the household's next main sign-in when the named account is deleted. |
| `this_computer_only` | INTEGER | 1 for an account that was started on this computer without a password. It can be used only in a browser on that computer (never from the home network), counts as no remote sign-in, and blocks the actions that would let others in (see Security). Defaults to 0; added by an idempotent startup migration. |
| `must_change_password` | INTEGER | 1 while the password is one an administrator chose. The person can then do nothing except choose their own password, check their session and sign out; the Engine refuses everything else with 403 `password_change_required`. Defaults to 0; added by an idempotent startup migration. |
| `temporary_password_expires_at` | TEXT | When an administrator-set temporary password stops working; null when none is set. Sessions of an account whose temporary password has run out stop validating. |
| `household_admin` | INTEGER | 1 when the account looks after its own household (a **household administrator**). It never reaches another household or the server's settings. A person's own sign-in (`grants_inherit_from_account_id` set) can never have it. Defaults to 0; added by an idempotent startup migration, and a household's first main sign-in gets it when the household is created. |

### households

A household is the group of people (profiles) who live together and the sign-ins (accounts) that open them. A profile and an account each belong to exactly one household. A household holds up to 8 profiles; the Engine refuses a ninth with "A household can have up to 8 people." A sign-in can only be granted profiles from its own household. Setup creates the first household with the administrator account; a new invitation for someone outside the household starts a new household for the new account.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | GUID, primary key |
| `name` | TEXT | Display name, for example `Alex's household` |
| `created_at` | TEXT | Timestamp |
| `primary_account_id` | BLOB | The household's main sign-in (the one the server administrator set up; household administrators can only hand out what it holds). Set when the first main sign-in is saved; re-pointed to the next oldest enabled main sign-in when it is deleted. Null only for a household with no main sign-in. |

**Upgrade.** The idempotent startup migration adds `household_id` to `accounts` and `profiles`, then gives every account without one its own household (named after the account's default profile) and moves every profile granted to an account into that account's household. A profile granted to two accounts joins the household of the account where it is the default grant, otherwise the oldest account; the startup log notes each such profile. Re-running changes nothing. Only the seeded Owner profile can be without a household, and only until the first administrator account is created.

### profiles

User profiles for multi-user support.

| Column | Type | Notes |
|---|---|---|
| `household_id` | BLOB | The household the profile belongs to (see `households`) |
| `avatar_color` | TEXT | Avatar circle colour, `#RRGGBB`. The profile editor offers a fixed palette of 12 swatches. |
| `avatar_image_path` | TEXT | Path of an uploaded avatar photo; NULL when none. A photo wins over an icon. |
| `avatar_icon` | TEXT | Key of a built-in illustrated icon drawn on `avatar_color` (`cat`, `fox`, `owl`, `bear`, `panda`, `penguin`, `robot`, `rocket`, `planet`, `moon`, `crown`, `ghost`); NULL shows the person's initial. Added by an idempotent startup migration. |

| Column | Type | Notes |
|---|---|---|
| `id` | TEXT | UUID, primary key |
| `name` | TEXT | Display name |
| `pin_hash` | TEXT | PIN hash. NULL if no PIN set. |
| `role` | TEXT | |
| `created_at` | TEXT | Timestamp |

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
| `issued_ingress` | TEXT | Where the sign-in came from: `this_computer`, `home_network` or `remote`. Default `home_network`, so rows from before this column existed are treated as home-only (fails closed). Added by the idempotent startup migration. |
| `authenticated_at` | TEXT | When the person last proved it was them (password, passkey or provider sign-in, or a "Confirm it's you" check). Sensitive account actions need this within the last 10 minutes. NULL on older rows, which are read as `created_at`. Added by the idempotent startup migration. |
| `created_at`, `last_seen_at`, `expires_at` | TEXT | Timestamps |
| `revoked_at`, `revoked_reason` | TEXT | NULL while active |

### device_profile_preferences

"Always open as this person on this device". At most one row per signed-in account and browser/device; the *Who's using Tuvima?* page writes it, and a new sign-in from that device starts in the saved profile (when that profile has a PIN, the picker asks for it instead).

| Column | Type | Notes |
|---|---|---|
| `account_id` | BLOB | Owning account (primary key with `device_id`); deleting the account removes the row |
| `device_id` | TEXT | The `Tuvima.Device` cookie value stored in `auth_sessions.device_id` |
| `profile_id` | BLOB | The profile to open as; deleting the profile removes the row. A saved profile the account no longer has is ignored |
| `updated_at` | TEXT | Timestamp |

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

### ingestion_log

One row per file processed through the ingestion pipeline.

| Column | Type | Notes |
|---|---|---|
| `id` | TEXT | UUID, primary key |
| `batch_id` | TEXT | FK -> `ingestion_batches.id` |
| `asset_id` | TEXT | FK -> `media_assets.id` |
| `file_path` | TEXT | Original file path |
| `fingerprint` | TEXT | SHA-256 hash |
| `outcome` | TEXT | `"Promoted"`, `"Rejected"`, `"NeedsReview"`, `"Duplicate"`, `"Failed"` |
| `outcome_reason` | TEXT | Natural-language explanation for non-promoted outcomes |
| `pipeline_state` | TEXT | JSON snapshot of pipeline stage completion |
| `created_at` | TEXT | Timestamp |

### ingestion_batches

Groups ingestion_log rows into named runs.

| Column | Type | Notes |
|---|---|---|
| `id` | TEXT | UUID, primary key |
| `started_at` | TEXT | Timestamp |
| `completed_at` | TEXT | NULL until batch finishes |
| `file_count` | INTEGER | Total files in batch |
| `promoted_count` | INTEGER | |
| `rejected_count` | INTEGER | |

### ingestion_batch_artifacts

Current-schema ledger for the concrete things an ingestion batch added, updated, linked, or routed to review. This table supports later Activity page rollups without relying on UI-only state.

| Column | Type | Notes |
|---|---|---|
| `id` | BLOB | UUID, primary key |
| `batch_id` | BLOB | Ingestion batch/run id |
| `artifact_type` | TEXT | Media, metadata field, cover/poster URL, stored artwork, person, relationship, series, QID, review item, or other batch artifact |
| `artifact_id` | BLOB | Optional id of the created or updated artifact |
| `parent_entity_id` | BLOB | Optional owning asset, work, collection, person, or review entity |
| `parent_entity_type` | TEXT | Optional owner type |
| `action` | TEXT | Added, updated, linked, resolved, skipped, review-created, or similar action |
| `display_name` | TEXT | Human-readable artifact label |
| `provider_id` | TEXT | Provider that supplied the artifact, when relevant |
| `source` | TEXT | Source subsystem or worker |
| `detail_json` | TEXT | Structured detail for Activity page display and diagnostics |
| `occurred_at` | TEXT | Timestamp |

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
| `entity_id` | TEXT | |
| `entity_type` | TEXT | |
| `id_type` | TEXT | `"ISBN"`, `"ASIN"`, `"TMDB"`, `"MBID"`, etc. |
| `value` | TEXT | The identifier value |
| `source_id` | TEXT | Which provider supplied this ID |

**Index:** `entity_id + id_type`, `value + id_type` (for reverse lookup)

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
| `cfi` | TEXT | EPUB Canonical Fragment Identifier for precise position |
| `created_at` | TEXT | Timestamp |

### audiobook_bookmarks

Saved audiobook playback positions. These are separate from EPUB reader bookmarks because they use audio seconds, optional chapter metadata, and the owning audiobook work.

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
| `created_at` | TEXT | Timestamp |

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
| `cfi_range` | TEXT | CFI range for highlighted text |
| `text` | TEXT | Highlighted text content |
| `note` | TEXT | User annotation. NULL if no note. |
| `color` | TEXT | Highlight color |
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

Rolling log of Engine actions. Pruned automatically per `config/maintenance.json`.

| Column | Type | Notes |
|---|---|---|
| `id` | TEXT | UUID, primary key |
| `run_id` | TEXT | Groups related activities into a single run |
| `activity_type` | TEXT | Type code (e.g., `"Ingestion"`, `"Enrichment"`, `"Writeback"`) |
| `entity_id` | TEXT | Related entity. NULL for system-level activities. |
| `outcome` | TEXT | `"Success"`, `"Failure"`, `"Skipped"` |
| `message` | TEXT | Human-readable description |
| `created_at` | TEXT | Timestamp |

**Index:** `run_id`, `activity_type`, `created_at`

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

## Related

- [Engine API Reference](api-endpoints.md)
- [Priority Cascade Engine](../architecture/scoring-and-cascade.md)
- [Settings Architecture and Review Queue](../architecture/dashboard-ui.md)

### series_manifest_hydrations

Series-level cache and provenance for Wikidata series manifests. One row is stored per canonical series QID so later sibling imports can link against the cached named checklist before deciding whether a Wikidata refresh is needed.

| Column | Type | Notes |
|---|---|---|
| `series_qid` | TEXT | Wikidata QID for the series. Primary key. |
| `collection_id` | TEXT | FK to `collections.id` for the local ContentGroup/series collection. |
| `series_label` | TEXT | Localized display name returned by Wikidata. |
| `manifest_source` | TEXT | Source library, normally `Tuvima.Wikidata`. |
| `manifest_version` | TEXT | Source package/version metadata. |
| `manifest_hash` | TEXT | Hash of the fetched manifest payload. |
| `known_item_qids_hash` | TEXT | Hash of the ordered known item QID set. |
| `warnings_json` | TEXT | Manifest-level warnings from Wikidata modeling. |
| `api_metadata_json` | TEXT | Request options used for the fetch. |
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
