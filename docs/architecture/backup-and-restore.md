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

## What a backup is for

A backup holds what is hard to recreate: your settings, your preferences and your own state (reading and watching positions, bookmarks, favourites). It does not hold your library's catalogue (titles, matches, enrichment results, artwork), which a rescan rebuilds, and it never holds your media files or folders. Backing those up is a job for your computer's own backup tool, such as File History, Time Machine or a NAS.

After a restore and a rescan, your own state finds its way back to the right titles, even if a file has since changed. To make that possible, a kept row that points at a catalogue row does not rely on the row's id (ids do not survive a rescan). It is stored with recognition data instead: the title's external ids, running time and edition, with the file fingerprint as a fallback.

## The four kinds

**Configuration** is settings and access, kept as rows: accounts, passkeys, profiles, households, grants, view policies and preferences, provider settings without secrets, notification settings, document types, fields and rules, backup settings, plugin approvals, and collections and playlists a person built. Password hashes, passkey public keys and two-step data stay, because they are what lets people sign in after a restore.

**Personal state** is a person's own state tied to a title: bookmarks, highlights, reading statistics, audiobook bookmarks and listening history, music play stats, consumption history, reactions, saved items, taste overrides, personal activity and playback positions. It is kept and re-attached to titles by recognition data. Each rule names an owner column (the profile or user the row belongs to), so one profile's state can be exported on its own.

**Catalogue** is anything a rescan rebuilds: works, editions, media assets, canonical values, claims, artwork, fingerprints, subtitles, people, provider caches, search indexes, the Needs Review queue, and identity and enrichment jobs, plus View items and everything tied to them. It is never kept. The Vault's catalogue is in this group too.

**Transient** is not kept and not rebuilt from files: sessions and tokens, one-time codes and challenges, run, operation and audit logs, telemetry, device pairing requests, notifications already sent, job queues, backup copy records and data store bookkeeping.

Tables that mix kinds are split in two ways. By column, a rule can name catalogue references (columns that point at catalogue rows, swapped for recognition data) and excluded columns (secret or machine-bound columns left out). By row, a rule can carry a keep condition (for example, only collections a person built, or only provider settings that are not secrets).

Secrets are never kept. Provider settings marked secret, webhook secrets and similar values are left out, in the same way as today's backups.

## Backup contents (manifest)

A backup's manifest will name its kind (`settings-and-personal-state`) and list what the backup leaves out: media files, folders, artwork, the catalogue, the Vault catalogue and secrets. The manifest and the export itself are built in a later step; this page defines only what is kept.

## Configuration tables (53)

| Table | Why | Points at the catalogue | Left out | Only rows where |
|---|---|---|---|---|
| `accounts` | Sign-in accounts and their administrator and household settings. | none | none | all rows |
| `account_credentials` | Password hashes, needed to sign in after a restore. | none | none | all rows |
| `account_external_logins` | Links between an account and an outside sign-in provider. | none | none | all rows |
| `account_passkeys` | Passkey public keys, needed to sign in after a restore. | none | none | all rows |
| `account_two_step` | Two-step sign-in data, needed to sign in after a restore (the secret is protected with this machine's Data Protection keys). | none | none | all rows |
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
| `profile_sequence_preferences` | Whether a profile shows missing items in a series or sequence. | none | none | all rows |
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
| `approval_records` | Approvals an administrator has given, with the statement approved. | `item_id` (other) | none | all rows |
| `plugin_lore_sources` | Lore sources a person approved or rejected; sources only discovered but never decided on are rebuilt. | `universe_qid` (other) | none | `approved_at IS NOT NULL OR rejected_at IS NOT NULL` |
| `metadata_providers` | The list of metadata providers and whether each is switched on. | none | none | all rows |
| `provider_config` | Provider settings; rows marked secret are left out of a backup. | none | none | `is_secret = 0` |
| `notification_settings` | Notification channels and rules. | none | none | all rows |
| `backup_settings` | Backup schedule, destination and retention settings. | none | none | all rows |
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
| `collections` | Collections and playlists a person built; collections built from providers (universes and series) are rebuilt. | `universe_id` (other), `parent_collection_id` (collection), `universe_qid` (other) | `cover_artwork_path`, `background_artwork_path`, `banner_artwork_path`, `logo_artwork_path` | `collection_type IN ('Custom', 'Playlist')` |
| `collection_items` | The titles in a person-built collection. | `work_id` (work) | none | `collection_id IN (SELECT id FROM collections WHERE collection_type IN ('Custom', 'Playlist'))` |
| `collection_placements` | Where a person-built collection appears. | none | none | `collection_id IN (SELECT id FROM collections WHERE collection_type IN ('Custom', 'Playlist'))` |
| `collection_profile_audience` | Which profiles can see a person-built collection. | none | none | `collection_id IN (SELECT id FROM collections WHERE collection_type IN ('Custom', 'Playlist'))` |
| `collection_view_sources` | The gallery or rule feeding a person-built collection. | `gallery_id` (other) | none | `collection_id IN (SELECT id FROM collections WHERE collection_type IN ('Custom', 'Playlist'))` |

## Personal state tables (16)

| Table | Why | Owner column | Points at the catalogue |
|---|---|---|---|
| `reader_bookmarks` | Bookmarks a person placed in a book. | `user_id` | `asset_id` (media asset) |
| `reader_highlights` | Highlights and notes a person made in a book. | `user_id` | `asset_id` (media asset) |
| `reader_statistics` | A person's reading time and speed per book. | `user_id` | `asset_id` (media asset) |
| `audiobook_bookmarks` | Bookmarks a person placed in an audiobook. | `profile_id` | `work_id` (work), `asset_id` (media asset) |
| `audiobook_listen_history` | What a person listened to in audiobooks. | `profile_id` | `work_id` (work), `asset_id` (media asset) |
| `music_play_stats` | Play counts and last played time per album or track. | `profile_id` | `work_id` (work) |
| `consumption_history` | What a person watched, read or listened to, with progress. | `profile_id` | `asset_id` (media asset) |
| `personal_activity` | A person's own activity feed (what they played and for how long). | `profile_id` | `work_id` (work), `asset_id` (media asset) |
| `player_device_states` | Where a person stopped on each device (playback positions). | `profile_id` | `asset_id` (media asset), `work_id` (work) |
| `user_states` | A person's progress and extended state per file. | `user_id` | `asset_id` (media asset) |
| `user_taste_profiles` | A person's taste profile. | `user_id` | none |
| `profile_reactions` | A person's reactions (likes and dislikes) to titles and other items. | `profile_id` | `entity_id` (other) |
| `profile_saved_items` | Items a person saved (favourites and watch later). | `profile_id` | `entity_id` (other) |
| `profile_taste_overrides` | Genres and tastes a person switched on or off. | `profile_id` | none |
| `profile_work_preferences` | A person's own tags and preferences for a title. | `profile_id` | `work_id` (work) |
| `profile_person_preferences` | A person's own tags and preferences for a creator or performer. | `profile_id` | `person_id` (person) |

## Catalogue tables (81)

None of these are kept. The search indexes (`search_index`, `local_item_search`, `artwork_asset_search`) are listed; their hidden storage tables are not.

| Table | Why |
|---|---|
| `works` | Titles; rebuilt by a rescan. |
| `editions` | Editions of a title; rebuilt by a rescan. |
| `media_assets` | The files themselves; rebuilt by a rescan. |
| `media_asset_chapters` | Chapters read from a file. |
| `media_asset_coverage` | Which part of a work a file covers. |
| `media_artwork_writeback` | Artwork write-back state for files. |
| `media_inspection_holds` | Inspection holds on files. |
| `playback_inspection_cache` | Cached file inspection results. |
| `playback_segments` | Intro, credits and recap segments found in files. |
| `audio_fingerprints` | Audio fingerprints computed from files. |
| `file_hash_cache` | Cached file hashes. |
| `canonical_values` | Winning metadata values, rebuilt from claims. |
| `canonical_value_arrays` | Winning list-type metadata values, rebuilt from claims. |
| `metadata_claims` | What each provider said about a title; rebuilt by enrichment. |
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
| `entity_artwork_links` | Which artwork belongs to which title or person. |
| `entity_assets` | Images attached to titles and people. |
| `image_cache` | Cached downloaded images. |
| `image_cache_sources` | Where cached images came from. |
| `character_portraits` | Character portraits. |
| `character_performer_links` | Which performer plays which character. |
| `fictional_entities` | Characters, places and other fictional things from Wikidata. |
| `fictional_entity_work_links` | Where a fictional thing appears. |
| `fictional_entity_user_overrides` | A person's name and description corrections for a fictional thing. |
| `narrative_roots` | Universes and franchises from Wikidata. |
| `narrative_root_user_overrides` | A person's name and description corrections for a universe. |
| `persons` | Creators and performers; rebuilt from files and providers. |
| `person_aliases` | Pseudonyms of people. |
| `person_group_members` | Members of bands and groups. |
| `person_media_links` | Which person worked on which file. |
| `person_roles` | The roles a person has. |
| `collection_relationships` | Relationships of provider-built collections. |
| `series_manifest_hydrations` | Series information fetched from providers. |
| `series_manifest_items` | The items of a series fetched from providers. |
| `series_members` | Series membership fetched from providers. |
| `tv_episode_credits` | Episode credits from providers. |
| `audiobook_chapter_title_overrides` | A person's chapter title corrections (no owner is recorded, so they are not kept). |
| `subtitle_index_assets` | Subtitle indexing state per file. |
| `subtitle_index_tracks` | Subtitle tracks that were indexed. |
| `subtitle_lines` | Indexed subtitle text. |
| `subtitle_name_tags` | Characters found in subtitle text. |
| `text_tracks` | Subtitle and lyric tracks found for files. |
| `local_items` | Photos, documents and other View items found on disk. |
| `local_files` | Files behind View items. |
| `local_file_sources` | Where View files were found. |
| `local_item_files` | Which files belong to which View item. |
| `local_item_metadata` | Metadata read from View items. |
| `local_item_annotations` | Labels and detections found on View items. |
| `local_item_tag_links` | Which tags are on which View item. |
| `local_item_search` | Search index over View items. |
| `local_item_search_keys` | Row numbers linking View items to their search index entries. |
| `doc_item_details` | Filing details of documents (tied to View items, which have no stable id after a rescan). |
| `doc_field_values` | Custom field values on documents (tied to View items). |
| `doc_item_field_sources` | Where document field values came from (tied to View items). |
| `view_galleries` | Galleries of View items (tied to View items, which have no stable id after a rescan). |
| `view_gallery_items` | The items in a View gallery. |
| `view_gallery_shares` | Who a View gallery is shared with. |
| `view_shared_assets` | View items promoted to the shared library. |
| `personal_activity_artwork` | Cached pictures for the activity feed. |
| `excluded_source_files` | Files a person removed from the library; a rescan finds them again. |
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

## Registered ahead of the Vault

The Vault (Videos Wave 3) adds its own tables in a separate proposed change. They are classified now so the Vault cannot ship an unclassified table. They do not exist yet on the main line, and the guardrail allows a rule for a missing table only while it is named in the registry's registered-ahead list. When the Vault lands, those names come out of the list and the guardrail checks the real columns. The Vault's catalogue is never in a backup; its settings and access, and a person's own Vault state, are.

| Table | Kind | Why |
|---|---|---|
| `vault_libraries` | Configuration | Which libraries are Vault libraries and where they look titles up. |
| `profile_nsfw_settings` | Configuration | How each profile's Vault is locked. |
| `profile_nsfw_pins` | Configuration | Vault PIN hashes, needed to open the Vault after a restore. |
| `vault_pending_bookmarks` | Personal state | A person's Vault bookmarks waiting for their titles, already keyed by recognition data. |
| `bookmark_packs` | Personal state | Bookmark packs a person imported for a title. |
| `bookmark_pack_items` | Personal state | The bookmarks inside a pack (owned through the pack's profile). |
| `vault_activity` | Catalogue | Vault activity feed. |
| `vault_artwork` | Catalogue | Vault artwork. |
| `vault_asset_performers` | Catalogue | Which Vault performers are on which Vault title. |
| `vault_asset_studios` | Catalogue | Which Vault studios made which Vault title. |
| `vault_asset_tags` | Catalogue | Which Vault tags are on which Vault title. |
| `vault_field_sources` | Catalogue | Where Vault metadata came from. |
| `vault_file_fingerprints` | Catalogue | Vault file fingerprints. |
| `vault_matches` | Catalogue | Vault title matches. |
| `vault_performers` | Catalogue | Vault performers. |
| `vault_studios` | Catalogue | Vault studios. |
| `vault_tags` | Catalogue | Vault tags. |
| `vault_galleries` | Catalogue | Vault image galleries. |
| `vault_gallery_items` | Catalogue | The items in a Vault image gallery. |
| `vault_image_folders` | Catalogue | Vault image folders. |
| `vault_image_items` | Catalogue | Vault image items. |
| `vault_image_libraries` | Catalogue | Vault image libraries. |
| `vault_image_tags` | Catalogue | Which Vault tags are on which Vault image. |
| `nsfw_session_unlocks` | Transient | Temporary Vault unlocks. |

## How to add a table

When you add a table through a numbered data store upgrade step, add one line for it in `BackupTableClassification`, choosing Configuration, Personal state, Catalogue or Transient, with a one-sentence reason. The guardrail test fails and names the table if you forget. Then:

- For Configuration or Personal state, list every column that holds the id of a catalogue row as a catalogue reference, and every secret or machine-bound column as an excluded column. Personal state also names its owner column.
- If only some rows are kept, give the rule a keep condition.
- Add the table to the matching list on this page. A second test fails if a classified table is missing here.
- If unsure whether a column holds a secret, exclude it and ask.
