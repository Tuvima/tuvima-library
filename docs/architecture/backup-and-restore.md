---
title: "Backup and Restore: What a Backup Keeps"
description: "Which data store tables a backup keeps, which it leaves out, and how each table is classified."
audience: "developer"
category: "architecture"
product_area: "backup"
status: current
---

# Backup and Restore: What a Backup Keeps

## In this page

Every table in the Library's data store is classified as one of four kinds, in one code registry, and a test fails when a table is not classified. This page explains the kinds and lists every table.

## Where this lives in the code

- `src/MediaEngine.Storage/Backup/BackupTableClassification.cs` (the registry)
- `src/MediaEngine.Storage/Backup/BackupTableRule.cs` and `BackupDataKind.cs`
- `tests/MediaEngine.Storage.Tests/BackupTableClassificationGuardrailTests.cs` (the guardrail)
- `src/MediaEngine.Storage/Backup/PersonalStateBackupRepository.cs`, `TitleRecognitionReader.cs` and `BackupArchiveFormat.cs` (the exporter that follows the registry, the one place that reads a title's recognition data, and the file format it writes)
- `src/MediaEngine.Storage/Backup/IBackupPortableValueCodec.cs` (the port that makes a machine-protected value readable for a backup) and `src/MediaEngine.Api/Security/DataProtectionBackupPortableValueCodec.cs` (the Engine's implementation for the two-step secret, registered with the exporter in `src/MediaEngine.Api/DependencyInjection/TuvimaBackupServiceCollectionExtensions.cs`)

## What a backup is for

A backup holds what is hard to recreate: your settings, your preferences and your own state (reading and watching positions, bookmarks, favourites). It does not hold your library's catalogue (titles, matches, enrichment results, artwork), which a rescan rebuilds (apart from the corrections you made to it yourself, such as a title field you locked or a chapter you renamed), and it never holds your media files or folders. Backing those up is a job for your computer's own backup tool, such as File History, Time Machine or a NAS.

After a restore and a rescan, your own state finds its way back to the right titles, even if a file has since changed. To make that possible, a kept row that points at a catalogue row does not rely on the row's id (ids do not survive a rescan). It is stored with recognition data instead: the title's external ids, running time and edition, with the file fingerprint as a fallback.

## The four kinds

**Configuration** is settings and access, kept as rows: accounts, passkeys, profiles, households, grants, view policies and preferences, provider settings without secrets, notification settings, document types, fields and rules, backup settings, plugin approvals, collections, playlists and View galleries a person built, values a person typed into document fields, and files a person chose to keep out of the Library. Rows that point at View items are re-attached by file fingerprint. Password hashes, passkey public keys and two-step data stay, because they are what lets people sign in after a restore. A person's name and description corrections for a fictional thing or a universe stay too (a fictional thing is found again by its Wikidata id). Collections the Library built (universes and series) are rebuilt, but a person's own switches on them (shown, featured, display changes) are kept.

**Personal state** is a person's own state tied to a title: bookmarks, highlights, reading statistics, audiobook bookmarks and listening history, music play stats, consumption history, reactions, saved items, taste overrides, personal activity and playback positions. It is kept and re-attached to titles by recognition data. Each rule names an owner column (the profile or user the row belongs to), so one profile's state can be exported on its own.

**Catalogue** is anything a rescan rebuilds: works, editions, media assets, canonical values, claims, artwork, fingerprints, subtitles, people, provider caches, search indexes, the Needs Review queue, and identity and enrichment jobs, and View items with what the Engine works out about them. It is not kept. The adult-video area's catalogue is in this group too. The exception is what a person did to it themselves: a person's own corrections on catalogue rows are kept, in two ways (see "A person's own choices and corrections" below).

**Transient** is not kept and not rebuilt from files: sessions and tokens, one-time codes and challenges, run, operation and audit logs, telemetry, device pairing requests, notifications already sent, job queues, backup copy records and data store bookkeeping.

Tables that mix kinds are split in three ways. By column, a rule can name catalogue references (columns that point at catalogue rows, swapped for recognition data), excluded columns (secret or machine-bound columns, never read: a column that may be empty is written as empty, any other is left out of the file) and excluded JSON properties (a named property removed, at any depth, from a JSON column, for example the machine-encrypted destination logins inside the backup settings). By row, a rule can carry a keep condition (for example, only collections a person built, or only provider settings that are not secrets). A rule can also name portable columns (see "Portable sign-in data" below).

A catalogue reference is normally an id. A reference stored as text is treated the same way when it parses as an id; any other text (a Wikidata id, a provider key) is an external key and is kept as written. A reference whose catalogue row no longer exists drops the row, and the count of dropped rows is recorded.

Secrets are never kept. Provider settings marked secret, webhook secrets and similar values are left out, in the same way as today's backups. The one deliberate exception is the two-step sign-in secret, which is kept readable so a restore works on a new computer (see "Portable sign-in data").

## A person's own choices and corrections

A rescan rebuilds the catalogue, so anything a person changed on a catalogue row would be lost. Two kinds of rule keep it:

- **Choices** are single columns on a row that a rescan recreates: a favourite flag on a View item, a display change typed on a title, a switch on a collection the Library built. The rule names the choice columns (and, when they sit on a side table such as a View item's metadata row, the column that says whose choice the row is, otherwise the row's own id); the exporter writes them to `data/choices/<table>.json` as a reference to the row (recognised by its external ids, or its file fingerprint) plus the values, and only for rows where a choice differs from its default. A choice column that points at another catalogue row (the series a title was placed in) is written as a reference too. A column can be gated by a sibling flag: it counts only while the flag is set, so a value the Engine read itself (a capture date from the file, a match source the person never locked) is never written. A Configuration table that keeps only some of its rows (collections) can name choice columns as well; they apply to the rows its keep condition leaves out.
- **Corrections** are whole rows a person made on a Catalogue table: a title field they locked or edited, a renamed audiobook chapter, a name they gave to a face in a photo, a cover they chose from a provider. The rule carries a correction condition; matching rows are written to `data/corrections/<table>.json` like personal rows, with every catalogue reference turned into a reference to a title and machine-only columns left out. A row whose required reference no longer exists is dropped and counted. A correction or choice on a withheld title (one a backup may not name, which today is none) is left out and counted separately, and the title is not written for it. A profile backup holds no corrections or Library choices, because they belong to the Library.

Structural moves are not kept: a locked or rejected match, a locked series placement, which episodes a file covers, or an edition moved to another title. A rescan sorts these out, and it usually fixes what the move was for. Only what a person typed or picked stays.

The reader accepts a choices or corrections file only for a table whose rule allows it. Restoring them (re-applying each correction to the rescanned title) is part of restore, not of the export.

Artwork a person chose is kept only when it has a stable outside identity: a cover picked from a provider is found again by provider and image address (`entity_assets`) or by the artwork's provider, source address and provider reference (`entity_artwork_links`). Artwork a person uploaded is never kept, because artwork files are not in a backup.

## Portable sign-in data

The two-step sign-in secret is stored sealed with this computer's Data Protection key, so on its own it would be useless on a new computer. The `account_two_step` rule names `secret_protected` as a portable column: the exporter asks the Engine's codec to unseal it and writes it readable, marked as portable in the file, and a restore seals it again with the new computer's key. Plainly: **without a backup password, the backup file can be used to sign in as you, so keep it safe; with a password, the file is locked.** (The optional backup password itself arrives with restore.) If the key is gone and the secret cannot be unsealed, it is kept as it is, counted in the backup's contents, and works only on this computer. Without a codec registered, portable columns are written unchanged. The codec handles exactly this one column and refuses any other.

## Backup contents (manifest)

A backup's manifest will name its kind (`settings-and-personal-state`) and list what the backup leaves out: media files, folders, artwork, the catalogue, the adult-video catalogue and secrets. The export of kept rows is built (`PersonalStateBackupRepository`, `BackupArchiveFormat`); writing it into backups, the manifest and restore come in the next step.

## Configuration tables (62)

| Table | Why | Points at the catalogue | Left out | Only rows where |
|---|---|---|---|---|
| `accounts` | Sign-in accounts and their administrator and household settings. | none | none | all rows |
| `account_credentials` | Password hashes, needed to sign in after a restore. | none | none | all rows |
| `account_external_logins` | Links between an account and an outside sign-in provider. | none | none | all rows |
| `account_passkeys` | Passkey public keys, needed to sign in after a restore. | none | none | all rows |
| `account_two_step` | Two-step sign-in data, needed to sign in after a restore; the secret is protected with this computer's Data Protection key, so the export writes it readable (portable column `secret_protected`), or keeps it as it is when it cannot be read. | none | none | all rows |
| `account_feature_grants` | Which features an account may use. | none | none | all rows |
| `account_library_grants` | Which libraries an account may use. | none | none | all rows |
| `account_profile_grants` | Which profiles an account may use. | none | none | all rows |
| `account_notice_dismissals` | Notices an account has dismissed, so they do not return. | none | none | all rows |
| `grant_admin_protections` | Optional extra lock (PIN hash and lock settings) on an administrator grant. | none | none | all rows |
| `households` | Households that group accounts and profiles. | none | none | all rows |
| `profiles` | People using the Library, with their display, content limit and navigation settings. | none | `avatar_image_path` | all rows |
| `profile_credentials` | Profile PIN hashes, needed to switch profile after a restore. | none | none | all rows |
| `profile_grid_preferences` | How a profile sorts each grid. | none | none | all rows |
| `profile_notice_dismissals` | Notices a profile has dismissed, so they do not return. | none | none | all rows |
| `profile_sequence_preferences` | Whether a profile shows missing items in a series or sequence. | `container_key` (other, text) | none | all rows |
| `profile_view_policies` | What a profile may do in View (access, sharing, documents). | none | none | all rows |
| `profile_view_preferences` | A profile's View display preferences. | none | none | all rows |
| `user_playback_settings` | A profile's playback settings. | none | none | all rows |
| `device_profile_preferences` | Which profile a device opens by default for an account. | none | none | all rows |
| `client_devices` | Paired devices and their capabilities (their tokens are not kept, so a device signs in again). | none | none | all rows |
| `applications` | Registered outside applications and what kind of access each has. | none | none | all rows |
| `application_client_bindings` | Which client ids belong to which application. | none | none | all rows |
| `application_credentials` | Hashed application credentials, which are access settings. | none | none | all rows |
| `application_permission_grants` | Which permissions an application has been granted. | none | none | all rows |
| `application_webhooks` | Webhook destinations an application has registered. | none | `secret_ciphertext` | all rows |
| `approval_records` | Approvals an administrator has given, with the statement approved. | none | none | all rows |
| `plugin_lore_sources` | Lore sources a person approved or rejected; sources only discovered but never decided on are rebuilt. | none | none | `approved_at IS NOT NULL OR rejected_at IS NOT NULL` |
| `metadata_providers` | The list of metadata providers and whether each is switched on. | none | none | all rows |
| `provider_config` | Provider settings; rows marked secret are left out of a backup. | none | none | `is_secret = 0` |
| `notification_settings` | Notification channels and rules. | none | none | all rows |
| `backup_settings` | Backup schedule, destination and retention settings; the machine-encrypted destination logins are left out. | none | the `EncryptedSecret` property inside `value_json` | all rows |
| `doc_types` | Document types a person defined. | none | none | all rows |
| `doc_field_definitions` | Custom document fields a person defined. | none | none | all rows |
| `doc_field_choices` | The choices offered by a document field. | none | none | all rows |
| `doc_correspondents` | Correspondents (senders and recipients) a person defined for documents. | none | none | all rows |
| `doc_rules` | Rules that file documents automatically. | none | none | all rows |
| `doc_rule_conditions` | The conditions of a document filing rule. | none | none | all rows |
| `doc_rule_actions` | The actions of a document filing rule. | none | none | all rows |
| `tags` | Tags a person created for View items. | none | none | all rows |
| `view_personal_spaces` | Each profile's private View space. | none | none | all rows |
| `view_sources` | Folders and devices a View space reads from. | none | none | all rows |
| `view_source_policies` | Whether a View source appears in the timeline. | none | none | all rows |
| `view_devices` | Devices that back up into a View space. | none | none | all rows |
| `view_folder_pins` | Folders a profile pinned in View. | none | none | all rows |
| `view_folder_timeline_policies` | Whether a folder appears in the View timeline. | none | none | all rows |
| `view_storage_labels` | Names a person gave to View storage locations. | none | none | all rows |
| `view_shared_library` | Which library a household shares in View. | none | none | all rows |
| `view_galleries` | Galleries a person built in View. | `cover_item_id` (local item), `soundtrack_playlist_id` (collection) | none | all rows |
| `view_gallery_items` | The View items in a person-built gallery. | `item_id` (local item) | none | all rows |
| `view_gallery_shares` | Who a person-built View gallery is shared with. | none | none | all rows |
| `doc_field_values` | Values a person entered in custom document fields. | `item_id` (local item) | none | all rows |
| `local_item_tag_links` | Which tags a person put on which View item. | `item_id` (local item) | none | all rows |
| `doc_item_details` | Filing details a person entered for a document (machine suggestions are rebuilt). | `item_id` (local item) | `suggestions_json` | all rows |
| `excluded_source_files` | Files a person removed from the library and chose to keep out, so a rescan does not add them back. | none | `title`, `excluded_by_name` | all rows |
| `collections` | Collections and playlists a person built; collections built from providers (universes and series) are rebuilt, but a person's switches on them are kept: choice columns `is_enabled`, `is_featured`, `display_overrides_json`, for the rows not kept here. | `universe_id` (other), `parent_collection_id` (collection) | `cover_artwork_path`, `background_artwork_path`, `banner_artwork_path`, `logo_artwork_path` | `collection_type IN ('Custom', 'Playlist', 'PlaylistFolder', 'Smart')` |
| `collection_items` | The titles in a person-built collection. | `work_id` (work) | none | `collection_id IN (SELECT id FROM collections WHERE collection_type IN ('Custom', 'Playlist', 'PlaylistFolder', 'Smart'))` |
| `collection_placements` | Where a person-built collection appears. | none | none | `collection_id IN (SELECT id FROM collections WHERE collection_type IN ('Custom', 'Playlist', 'PlaylistFolder', 'Smart'))` |
| `collection_profile_audience` | Which profiles can see a person-built collection. | none | none | `collection_id IN (SELECT id FROM collections WHERE collection_type IN ('Custom', 'Playlist', 'PlaylistFolder', 'Smart'))` |
| `collection_view_sources` | The gallery or rule feeding a person-built collection. | none | none | `collection_id IN (SELECT id FROM collections WHERE collection_type IN ('Custom', 'Playlist', 'PlaylistFolder', 'Smart'))` |
| `fictional_entity_user_overrides` | A person's name and description corrections for a fictional thing, found again by its Wikidata id. | `entity_id` (fictional thing) | none | all rows |
| `narrative_root_user_overrides` | A person's name and description corrections for a universe (already keyed by its Wikidata id). | none | none | all rows |

## Personal state tables (16)

| Table | Why | Owner column | Points at the catalogue | Left out |
|---|---|---|---|---|
| `reader_bookmarks` | Bookmarks a person placed in a book. | `user_id` | `asset_id` (media asset) | none |
| `reader_highlights` | Highlights and notes a person made in a book. | `user_id` | `asset_id` (media asset) | none |
| `reader_statistics` | A person's reading time and speed per book. | `user_id` | `asset_id` (media asset) | none |
| `audiobook_bookmarks` | Bookmarks a person placed in an audiobook. | `profile_id` | `work_id` (work), `asset_id` (media asset) | none |
| `audiobook_listen_history` | What a person listened to in audiobooks. | `profile_id` | `work_id` (work), `asset_id` (media asset) | none |
| `music_play_stats` | Play counts and last played time per album or track. | `profile_id` | `work_id` (work) | none |
| `consumption_history` | What a person watched, read or listened to, with progress. | `profile_id` | `asset_id` (media asset) | none |
| `personal_activity` | A person's own activity feed (what they played and for how long). | `profile_id` | `work_id` (work), `asset_id` (media asset) | `artwork_key` |
| `player_device_states` | Where a person stopped on each device (playback positions). | `profile_id` | `asset_id` (media asset), `work_id` (work) | `player_session_id` |
| `user_states` | A person's progress and extended state per file. | `user_id` | `asset_id` (media asset) | none |
| `user_taste_profiles` | A person's taste profile. | `user_id` | none | none |
| `profile_reactions` | A person's reactions (likes and dislikes) to titles and other items. | `profile_id` | `entity_id` (other) | none |
| `profile_saved_items` | Items a person saved (favourites and watch later). | `profile_id` | `entity_id` (other) | none |
| `profile_taste_overrides` | Genres and tastes a person switched on or off. | `profile_id` | none | none |
| `profile_work_preferences` | A person's own tags and preferences for a title. | `profile_id` | `work_id` (work) | none |
| `profile_person_preferences` | A person's own tags and preferences for a creator or performer. | `profile_id` | `person_id` (person) | none |

## Catalogue tables (72)

The rows a rescan rebuilds are not kept; the tables below say where a person's own choices or corrections on them are. The search indexes (`search_index`, `local_item_search`, `artwork_asset_search`) are listed; their hidden storage tables are not.

| Table | Why |
|---|---|
| `works` | Titles; rebuilt by a rescan, but display changes a person typed are kept: choice column `display_overrides_json`. Matching and placement choices (locked or rejected matches, a locked series placement) are left to the rescan. |
| `editions` | Editions of a title; rebuilt by a rescan. |
| `media_assets` | The files themselves; rebuilt by a rescan. |
| `media_asset_chapters` | Chapters read from a file. |
| `media_asset_coverage` | Which part of a work a file covers; rebuilt by a rescan, including coverage a person set by hand (moves are left to the rescan). |
| `media_artwork_writeback` | Artwork write-back state for files. |
| `media_inspection_holds` | Inspection holds on files. |
| `playback_inspection_cache` | Cached file inspection results. |
| `playback_segments` | Intro, credits and recap segments found in files. |
| `audio_fingerprints` | Audio fingerprints computed from files. |
| `file_hash_cache` | Cached file hashes. |
| `canonical_values` | Winning metadata values, rebuilt from claims. |
| `canonical_value_arrays` | Winning list-type metadata values, rebuilt from claims. |
| `metadata_claims` | What each provider said about a title; rebuilt by enrichment. Current claims a person made themselves are kept as corrections: `is_current = 1` and either locked by a person or written by the manual-edit provider. `entity_id` is a reference (work, edition, person or file). |
| `bridge_ids` | External ids found for a title. |
| `entity_events` | Identity and enrichment history of a title. |
| `entity_field_changes` | Field changes recorded in a title's history. |
| `entity_relationships` | Relationships between titles, people and characters from Wikidata. |
| `entity_relationship_qualifiers` | Details of those relationships. |
| `entity_capability_states` | What enrichment has done for each title. |
| `identity_jobs` | Identification and enrichment jobs. |
| `retail_match_candidates` | Retail match candidates for an identification job. |
| `wikidata_bridge_candidates` | Wikidata candidates for an identification job. |
| `deferred_enrichment_queue` | Titles waiting for enrichment. |
| `enrichment_refresh_schedule` | When each title is next refreshed. |
| `ai_feature_artifacts` | Results of optional AI features. |
| `pending_person_signals` | People spotted in files, waiting to be matched. |
| `review_queue` | Needs Review items; a rescan raises them again. |
| `artwork_assets` | Artwork files known to the Library. |
| `artwork_asset_context` | Where each artwork file is used. |
| `artwork_asset_search` | Search index over artwork. |
| `entity_artwork_links` | Which artwork belongs to which title or person. A cover a person picked from a provider is kept as a correction (`is_user_override = 1`): `entity_id` is a reference to the title or person, and `artwork_asset_id` is a reference to the artwork, found again by its provider, source address and provider reference (never its content hash, because artwork files are not in a backup). A link to an uploaded artwork, or one with no source address and no provider reference, cannot be found again, so the correction condition leaves it out (it is simply absent, not counted as dropped). |
| `entity_assets` | Images attached to titles and people. A cover a person chose from a provider is kept as a correction (`is_user_override = 1`, a provider other than `user_upload`, an `http` image address), found again by provider and image address; `entity_id` is a reference, and the row id, local file paths and export flags are left out. Uploads are not kept. |
| `image_cache` | Cached downloaded images (not kept: a cache row has no owner to attach a choice to). |
| `image_cache_sources` | Where cached images came from. |
| `character_portraits` | Character portraits. |
| `character_performer_links` | Which performer plays which character. |
| `fictional_entities` | Characters, places and other fictional things from Wikidata. |
| `fictional_entity_work_links` | Where a fictional thing appears. |
| `narrative_roots` | Universes and franchises from Wikidata. |
| `persons` | Creators and performers; rebuilt from files and providers, but a person's display changes are kept: choice column `display_overrides_json`. |
| `person_aliases` | Pseudonyms of people. |
| `person_group_members` | Members of bands and groups. |
| `person_media_links` | Which person worked on which file. |
| `person_roles` | The roles a person has. |
| `collection_relationships` | Relationships of provider-built collections. |
| `series_manifest_hydrations` | Series information fetched from providers. |
| `series_manifest_items` | The items of a series fetched from providers. |
| `series_members` | Series membership fetched from providers. |
| `tv_episode_credits` | Episode credits from providers. |
| `audiobook_chapter_title_overrides` | Chapter titles; the ones a person renamed are kept as corrections (`title_source = 'Override'`, with `work_id` and `asset_id` as references), the ones read from the file are rebuilt. |
| `subtitle_index_assets` | Subtitle indexing state per file. |
| `subtitle_index_tracks` | Subtitle tracks that were indexed. |
| `subtitle_lines` | Indexed subtitle text. |
| `subtitle_name_tags` | Characters found in subtitle text. |
| `text_tracks` | Subtitle and lyric tracks found for files. |
| `local_items` | Photos, documents and other View items found on disk; a person's own choices on them (favourite, hidden, archived, trashed, capture date they set) are kept: choice columns `favorite`, `hidden`, `archived_at`, `trashed_at`, `captured_at_user_override`, `captured_at` (the date only while the person set it). |
| `local_files` | Files behind View items. |
| `local_file_sources` | Where View files were found. |
| `local_item_files` | Which files belong to which View item. |
| `local_item_metadata` | Metadata read from View items; a place a person set on a photo and a description they wrote are kept as choices on the item named by `item_id`: choice columns `description`, `location_user_override`, and the place columns `latitude`, `longitude`, `location_name`, `location_city`, `location_region`, `location_country`, `location_country_code`, `location_source` (only while `location_user_override` is set). The embedded coordinates, camera and lens details and document text are never kept. |
| `local_item_annotations` | Labels and detections found on View items; a name a person gave, or a detection they reviewed, is kept as a correction (`source = 'user_assignment' OR reviewed_at IS NOT NULL`, `item_id` as a reference), without the machine's own details (`confidence`, `model_name`, `model_version`, `provenance_json`). |
| `local_item_search` | Search index over View items. |
| `local_item_search_keys` | Row numbers linking View items to their search index entries. |
| `doc_item_field_sources` | Where document field values came from (tied to View items). |
| `view_shared_assets` | View items promoted to the shared library. |
| `personal_activity_artwork` | Cached pictures for the activity feed. |
| `provider_response_cache` | Cached provider answers. |
| `resolver_cache` | Cached identification answers. |
| `search_results_cache` | Cached search answers. |
| `qid_labels` | Cached Wikidata labels. |
| `search_index` | Search index over titles. |

## Transient tables (65)

None of these are kept.

| Table | Why |
|---|---|
| `auth_sessions` | Sign-in sessions. |
| `client_tokens` | Device and application tokens. |
| `service_credentials` | Service credentials; they are issued again. |
| `account_invitations` | One-time invitation tokens. |
| `password_recovery_codes` | One-time recovery codes. |
| `password_reset_challenges` | One-time reset links. |
| `two_step_challenges` | One-time sign-in challenges. |
| `setup_codes` | One-time setup codes. |
| `grant_admin_unlocks` | Temporary administrator unlocks. |
| `device_pairing_requests` | Pairing requests from devices. |
| `authorization_audit_events` | Access change audit log. |
| `identity_audit_events` | Sign-in audit log. |
| `system_activity` | System activity log. |
| `transaction_log` | Change log. |
| `ingestion_log` | Ingestion run log. |
| `folder_organise_log` | Folder organise run log. |
| `scheduled_task_runs` | Scheduled task run log. |
| `media_operations` | Media operation queue. |
| `media_operation_events` | Media operation log. |
| `media_file_write_intents` | Pending file write-back jobs. |
| `file_write_runs` | When the file write-back schedule last ran. |
| `encode_jobs` | Encode job queue. |
| `alignment_jobs` | Book and audiobook alignment job queue. |
| `adaptive_hls_packages` | Streaming copies prepared for playback. |
| `offline_variants` | Offline copies prepared for devices. |
| `device_downloads` | What each device has downloaded. |
| `device_photo_downloads` | Which photos each device has downloaded. |
| `playback_telemetry_sessions` | Playback telemetry. |
| `audiobook_listen_active_segments` | Audiobook listening in progress. |
| `music_play_active_segments` | Music playing in progress. |
| `player_sessions` | Live player sessions. |
| `player_queue_items` | Live player queues. |
| `companion_sessions` | Companion screen sessions. |
| `personal_status_commands` | Recent status changes kept for undo. |
| `ai_usage_records` | AI usage telemetry. |
| `provider_health` | Provider health telemetry. |
| `provider_connection_checks` | Provider connection checks. |
| `application_events` | Application event outbox. |
| `application_webhook_attempts` | Webhook delivery attempts. |
| `application_webhook_deliveries` | Webhook delivery queue. |
| `plugin_event_cursors` | How far each plugin has read events. |
| `plugin_records` | Plugin working data. |
| `plugin_lore_entities` | Lore entities found by plugins. |
| `plugin_lore_relationships` | Lore relationships found by plugins. |
| `media_editor_commits` | Editor save records, kept for repeat requests. |
| `media_editor_commit_items` | Items of editor save records. |
| `media_editor_commit_artwork` | Artwork of editor save records. |
| `media_editor_edition_artwork_commits` | Edition artwork save records. |
| `media_editor_preferred_artwork_commits` | Preferred artwork save records. |
| `media_editor_music_pairing_commits` | Music pairing save records. |
| `media_editor_music_pairing_commit_items` | Items of music pairing save records. |
| `view_shared_contributions` | Shared library contribution requests. |
| `view_shared_contribution_items` | Items of shared library contribution requests. |
| `view_shared_contribution_events` | Shared library contribution log. |
| `view_shared_transfers` | Shared library transfers in progress. |
| `notifications` | Notifications already sent. |
| `notification_channel_state` | Notification channel health. |
| `notification_rate_marks` | Notification rate limiting marks. |
| `backup_copies` | Records of backup copies already made. |
| `ui_settings_cache` | Cached Dashboard settings. |
| `onboarding_workflows` | First-run setup progress. |
| `onboarding_steps` | First-run setup steps. |
| `onboarding_sessions` | First-run setup sessions. |
| `onboarding_restore_operations` | Restore operations started from first-run setup. |
| `data_store_upgrades` | Which data store upgrade steps have run. |
| `storage_metadata` | Data store bookkeeping. |

## The Vault

The Vault keeps its settings and each person's own state in a backup, and never its catalogue (a rescan rebuilds it).

| Table | Kind | Why |
|---|---|---|
| `profile_nsfw_settings` | Configuration | How each profile's Vault mode is protected and when it turns off. |
| `vault_libraries` | Configuration | Which libraries are Vault libraries and how they look things up. |
| `bookmark_packs` | Personal state | Bookmark packs a person imported for a title. |
| `bookmark_pack_items` | Personal state | The bookmarks inside a pack (owned through the pack's profile). |
| `vault_pending_bookmarks` | Personal state | Vault bookmarks waiting for their title after a restore. |
| `nsfw_session_unlocks` | Transient | Temporary Vault mode sessions. |
| `vault_activity` | Catalogue | Quiet list of Vault ingestion activity. |
| `vault_artwork` | Catalogue | Vault artwork. |
| `vault_asset_performers` | Catalogue | Vault performers per video. |
| `vault_asset_studios` | Catalogue | Vault studios per video. |
| `vault_asset_tags` | Catalogue | Vault tags per video. |
| `vault_field_sources` | Catalogue | Where each Vault field came from. |
| `vault_file_fingerprints` | Catalogue | Vault file fingerprints. |
| `vault_matches` | Catalogue | Vault lookup matches. |
| `vault_performers` | Catalogue | Vault performers. |
| `vault_studios` | Catalogue | Vault studios. |
| `vault_tags` | Catalogue | Vault tags. |
| `vault_galleries` | Catalogue | Vault image galleries. |
| `vault_gallery_items` | Catalogue | Images inside Vault galleries. |
| `vault_image_folders` | Catalogue | Vault image folders. |
| `vault_image_items` | Catalogue | Vault images. |
| `vault_image_libraries` | Catalogue | Vault image libraries. |
| `vault_image_tags` | Catalogue | Vault image tags. |

## How to add a table

When you add a table through a numbered data store upgrade step, add one line for it in `BackupTableClassification`, choosing Configuration, Personal state, Catalogue or Transient, with a one-sentence reason. The guardrail test fails and names the table if you forget. Then:

- For Configuration or Personal state, list every column that holds the id of a catalogue row as a catalogue reference, and every secret or machine-bound column as an excluded column. Personal state also names its owner column.
- If only some rows are kept, give the rule a keep condition.
- If a secret sits inside a JSON column, name the JSON property to remove. If a person's own choice sits on a Catalogue row, name the choice columns on that Catalogue rule (and gate a column by its flag when only a person-set value counts). If a person makes whole rows on a Catalogue table, give it a correction condition, its catalogue references and its machine-only excluded columns. If a Configuration column is sealed with this computer's key, name it as a portable column and extend the Engine's codec (it refuses anything it does not know).
- Add the table to the matching list on this page. A second test fails if a classified table is missing here.
- If unsure whether a column holds a secret, exclude it and ask.
