-- =============================================================================
-- Tuvima Library - SQLite initialization script
-- Current storage epoch: guid-blob-v10-graph-timeline-lore
--
-- Internal UUIDs are stored as 16-byte BLOBs where the current domain model owns
-- the identifier. External provider identifiers, QIDs, hashes, URLs, and file
-- paths remain TEXT. Unsupported database epochs are rejected unless an
-- explicit destructive reset is requested.
-- =============================================================================

PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;
PRAGMA temp_store = MEMORY;

CREATE TABLE IF NOT EXISTS alignment_jobs (
    id                  BLOB NOT NULL PRIMARY KEY,
    ebook_asset_id      BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    audiobook_asset_id  BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    status              TEXT NOT NULL DEFAULT 'Pending'
                            CHECK (status IN ('Pending', 'Processing', 'Completed', 'Failed')),
    alignment_data      TEXT,
    error_message       TEXT,
    created_at          TEXT NOT NULL DEFAULT (datetime('now')),
    completed_at        TEXT
);

CREATE TABLE IF NOT EXISTS audio_fingerprints (
    asset_id      BLOB NOT NULL PRIMARY KEY,
    fingerprint   BLOB NOT NULL,
    duration_sec  REAL NOT NULL DEFAULT 0,
    created_at    TEXT NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (asset_id) REFERENCES media_assets(id)
);

CREATE TABLE IF NOT EXISTS bridge_ids (
    id                BLOB NOT NULL PRIMARY KEY,
    entity_id         BLOB NOT NULL,
    id_type           TEXT NOT NULL,
    id_value          TEXT NOT NULL,
    wikidata_property TEXT,
    provider_id       TEXT,
    created_at        TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(entity_id, id_type)
);

CREATE TABLE IF NOT EXISTS canonical_value_arrays (
    entity_id BLOB    NOT NULL,
    key       TEXT    NOT NULL,
    ordinal   INTEGER NOT NULL DEFAULT 0,
    value     TEXT    NOT NULL,
    value_qid TEXT,
    PRIMARY KEY (entity_id, key, ordinal)
);

CREATE TABLE IF NOT EXISTS canonical_values (
    entity_id           BLOB    NOT NULL,
    key                 TEXT    NOT NULL,
    value               TEXT    NOT NULL,
    last_scored_at      TEXT    NOT NULL,
    -- Phase B: tracks whether the scoring engine could not pick a clear winner.
    -- 1 = conflicted (runner-up within epsilon of winner); 0 = resolved.
    is_conflicted       INTEGER NOT NULL DEFAULT 0
                            CHECK (is_conflicted IN (0, 1)),
    -- Unit 4: the provider that supplied the winning claim for this field.
    -- NULL for user-locked values or rows created before this column was added.
    winning_provider_id BLOB,
    -- Unit 5: field-level review flag.  1 = needs human review (conflicted,
    -- missing expected field, or local-only source with low confidence); 0 = ok.
    needs_review        INTEGER NOT NULL DEFAULT 0
                            CHECK (needs_review IN (0, 1)),
    PRIMARY KEY (entity_id, key)
);

CREATE TABLE IF NOT EXISTS character_performer_links (
    person_id           BLOB NOT NULL REFERENCES persons(id) ON DELETE CASCADE,
    fictional_entity_id BLOB NOT NULL REFERENCES fictional_entities(id) ON DELETE CASCADE,
    work_qid            TEXT, character_image_path TEXT,
    PRIMARY KEY (person_id, fictional_entity_id, work_qid)
);

CREATE TABLE IF NOT EXISTS character_portraits (
    id                  BLOB PRIMARY KEY,
    person_id           BLOB NOT NULL REFERENCES persons(id) ON DELETE CASCADE,
    fictional_entity_id BLOB NOT NULL REFERENCES fictional_entities(id) ON DELETE CASCADE,
    image_url           TEXT,
    local_image_path    TEXT,
    source_provider     TEXT,
    is_default          INTEGER NOT NULL DEFAULT 0,
    created_at          TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at          TEXT
);

CREATE TABLE IF NOT EXISTS collection_items (
    id                BLOB NOT NULL PRIMARY KEY,  -- UUID
    collection_id            BLOB NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
    work_id           BLOB NOT NULL REFERENCES works(id) ON DELETE CASCADE,
    sort_order        INTEGER NOT NULL DEFAULT 0,
    progress_state    TEXT NOT NULL DEFAULT 'not_started'
                          CHECK (progress_state IN ('not_started','in_progress','completed')),
    progress_position TEXT,
    added_at          TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS collection_placements (
    id            BLOB PRIMARY KEY,
    collection_id BLOB NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
    location      TEXT NOT NULL,
    position      INTEGER NOT NULL DEFAULT 0,
    display_limit INTEGER NOT NULL DEFAULT 0,
    display_mode  TEXT NOT NULL DEFAULT 'swimlane',
    is_visible    INTEGER NOT NULL DEFAULT 1,
    created_at    TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS collection_relationships (
    id            BLOB NOT NULL PRIMARY KEY,
    collection_id BLOB NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
    rel_type      TEXT NOT NULL,
    rel_qid       TEXT NOT NULL,
    rel_label     TEXT,
    confidence    REAL NOT NULL DEFAULT 0.9,
    discovered_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS collections (
    id                BLOB NOT NULL PRIMARY KEY,  -- UUID
    universe_id       BLOB,                       -- NULLABLE: cross-hub grouping
    parent_collection_id     BLOB REFERENCES collections(id) ON DELETE SET NULL,  -- franchise parent
    display_name      TEXT,                       -- Phase 7: human-readable hub name
    display_overrides_json TEXT,
    collection_type          TEXT NOT NULL DEFAULT 'Universe',
    description       TEXT,
    icon_name         TEXT,
    scope             TEXT NOT NULL DEFAULT 'library',
    profile_id        BLOB REFERENCES profiles(id) ON DELETE CASCADE,
    membership_mode   TEXT NOT NULL DEFAULT 'Smart' CHECK(membership_mode IN ('Manual','Smart')),
    primary_area      TEXT NOT NULL DEFAULT 'Mixed' CHECK(primary_area IN ('Read','Watch','Listen','Mixed')),
    owner_kind        TEXT NOT NULL DEFAULT 'Library' CHECK(owner_kind IN ('Profile','Library')),
    audience          TEXT NOT NULL DEFAULT 'Everyone' CHECK(audience IN ('Private','SelectedProfiles','Everyone')),
    is_enabled        INTEGER NOT NULL DEFAULT 1,
    is_featured       INTEGER NOT NULL DEFAULT 0,
    min_items         INTEGER NOT NULL DEFAULT 0,
    rule_json         TEXT,
    refresh_schedule  TEXT,
    last_refreshed_at TEXT,
    modified_at       TEXT,
    wikidata_qid      TEXT,
    universe_status   TEXT NOT NULL DEFAULT 'Unknown',
    created_at        TEXT NOT NULL DEFAULT (datetime('now'))
, resolution TEXT NOT NULL DEFAULT 'query', rule_hash TEXT, group_by_field TEXT, match_mode TEXT NOT NULL DEFAULT 'all', sort_field TEXT, sort_direction TEXT NOT NULL DEFAULT 'desc', secondary_sort_field TEXT, secondary_sort_direction TEXT, cover_artwork_path TEXT, cover_artwork_mime_type TEXT, background_artwork_path TEXT, background_artwork_mime_type TEXT, banner_artwork_path TEXT, banner_artwork_mime_type TEXT, logo_artwork_path TEXT, logo_artwork_mime_type TEXT);

CREATE TABLE IF NOT EXISTS collection_profile_audience (
    collection_id BLOB NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
    profile_id    BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    created_at    TEXT NOT NULL DEFAULT (datetime('now')),
    PRIMARY KEY(collection_id, profile_id)
);

CREATE TABLE IF NOT EXISTS deferred_enrichment_queue (
    id           BLOB NOT NULL PRIMARY KEY,
    entity_id    BLOB NOT NULL,
    wikidata_qid TEXT,
    media_type   TEXT NOT NULL,
    hints_json   TEXT,
    created_at   TEXT NOT NULL,
    status       TEXT NOT NULL DEFAULT 'Pending',
    processed_at TEXT
, failure_type TEXT, failed_provider_name TEXT);

CREATE TABLE IF NOT EXISTS editions (
    id           BLOB NOT NULL PRIMARY KEY,  -- UUID
    work_id      BLOB NOT NULL REFERENCES works(id) ON DELETE CASCADE,
    format_label TEXT                        -- e.g. '4K Bluray', 'First Edition'
, wikidata_qid TEXT);

CREATE TABLE IF NOT EXISTS encode_jobs (
    id             BLOB NOT NULL PRIMARY KEY,
    asset_id       BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    owner_profile_id BLOB,
    owner_device_id  BLOB,
    profile_key    TEXT NOT NULL,
    source_hash    TEXT NOT NULL,
    status         TEXT NOT NULL,
    created_at     TEXT NOT NULL,
    scheduled_for  TEXT,
    started_at     TEXT,
    completed_at   TEXT,
    progress_pct   REAL NOT NULL DEFAULT 0,
    output_path    TEXT,
    output_bytes   INTEGER,
    last_error     TEXT,
    retry_count    INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS entity_assets (
    id               BLOB PRIMARY KEY,
    entity_id        BLOB NOT NULL,
    entity_type      TEXT NOT NULL CHECK(entity_type IN ('Work','Edition','Person','Universe','FictionalEntity')),
    asset_type       TEXT NOT NULL CHECK(asset_type IN ('CoverArt','Headshot','Banner','Logo','NetworkLogo','StudioLogo','Background','SeasonPoster','SeasonThumb','EpisodeStill','CharacterPortrait')),
    image_url        TEXT,
    local_image_path TEXT,
    local_image_path_s TEXT,
    local_image_path_m TEXT,
    local_image_path_l TEXT,
    source_provider  TEXT,
    width_px         INTEGER,
    height_px        INTEGER,
    aspect_class     TEXT NOT NULL DEFAULT 'UnsupportedRect',
    primary_hex      TEXT,
    secondary_hex    TEXT,
    accent_hex       TEXT,
    asset_class      TEXT NOT NULL DEFAULT 'Artwork',
    storage_location TEXT NOT NULL DEFAULT 'Central',
    owner_scope      TEXT NOT NULL DEFAULT 'Unknown',
    is_preferred     INTEGER NOT NULL DEFAULT 0,
    is_user_override INTEGER NOT NULL DEFAULT 0,
    is_locally_exported   INTEGER NOT NULL DEFAULT 0,
    is_preferred_exported INTEGER NOT NULL DEFAULT 0,
    created_at       TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at       TEXT
);

CREATE TABLE IF NOT EXISTS entity_capability_states (
  id                  BLOB PRIMARY KEY,
  entity_id           BLOB NOT NULL,
  entity_kind         TEXT NOT NULL,
  media_type          TEXT,
  capability_id       TEXT NOT NULL,
  capability_kind     TEXT NOT NULL,
  capability_version  TEXT,
  sub_key             TEXT,
  status              TEXT NOT NULL,
  requiredness        TEXT NOT NULL DEFAULT 'optional',
  source              TEXT,
  confidence          REAL,
  artifact_count      INTEGER NOT NULL DEFAULT 0,
  artifact_summary    TEXT,
  result_summary      TEXT,
  last_operation_id   BLOB,
  first_attempted_at  TEXT,
  last_attempted_at   TEXT,
  succeeded_at        TEXT,
  next_retry_at       TEXT,
  stale               INTEGER NOT NULL DEFAULT 0,
  needs_rerun         INTEGER NOT NULL DEFAULT 0,
  missing_reason      TEXT,
  last_error          TEXT,
  last_outcome_category TEXT,
  created_at          TEXT NOT NULL,
  updated_at          TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS entity_events (
    id                BLOB NOT NULL PRIMARY KEY,
    entity_id         BLOB NOT NULL,
    entity_type       TEXT NOT NULL,
    event_type        TEXT NOT NULL,
    stage             INTEGER,
    trigger           TEXT NOT NULL,
    provider_id       TEXT,
    provider_name     TEXT,
    bridge_id_type    TEXT,
    bridge_id_value   TEXT,
    resolved_qid      TEXT,
    confidence        REAL,
    score_title       REAL,
    score_author      REAL,
    score_year        REAL,
    score_format      REAL,
    score_cross_field REAL,
    score_cover_art   REAL,
    score_composite   REAL,
    occurred_at       TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    ingestion_run_id  BLOB,
    detail            TEXT
);

CREATE TABLE IF NOT EXISTS entity_field_changes (
    id              BLOB NOT NULL PRIMARY KEY,
    event_id        BLOB NOT NULL REFERENCES entity_events(id) ON DELETE CASCADE,
    entity_id       BLOB NOT NULL,
    field           TEXT NOT NULL,
    old_value       TEXT,
    new_value       TEXT,
    old_provider_id TEXT,
    new_provider_id TEXT,
    confidence      REAL,
    is_file_original INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS entity_relationships (
    id                      BLOB NOT NULL PRIMARY KEY,
    statement_key           TEXT NOT NULL UNIQUE,
    subject_qid             TEXT NOT NULL,
    relationship_type       TEXT NOT NULL,
    object_qid              TEXT NOT NULL,
    confidence              REAL NOT NULL DEFAULT 0.9,
    context_work_qid        TEXT,
    source_provider         TEXT,
    provenance              TEXT NOT NULL DEFAULT 'Wikidata',
    is_supplemental         INTEGER NOT NULL DEFAULT 0 CHECK (is_supplemental IN (0, 1)),
    discovered_at           TEXT NOT NULL,
    start_time              TEXT,
    end_time                TEXT
);

CREATE TABLE IF NOT EXISTS entity_relationship_qualifiers (
    id                      BLOB NOT NULL PRIMARY KEY,
    relationship_id         BLOB NOT NULL REFERENCES entity_relationships(id) ON DELETE CASCADE,
    qualifier_type          TEXT NOT NULL,
    value                   TEXT NOT NULL,
    value_kind              TEXT NOT NULL DEFAULT 'Text',
    source_provider         TEXT,
    provenance              TEXT NOT NULL DEFAULT 'Wikidata',
    is_supplemental         INTEGER NOT NULL DEFAULT 0 CHECK (is_supplemental IN (0, 1)),
    confidence              REAL
);

CREATE TABLE IF NOT EXISTS fictional_entities (
    id                       BLOB NOT NULL PRIMARY KEY,
    wikidata_qid             TEXT NOT NULL UNIQUE,
    label                    TEXT NOT NULL,
    description              TEXT,
    entity_sub_type          TEXT NOT NULL
                                 CHECK (entity_sub_type IN ('Character', 'Location', 'Organization', 'Event', 'Object')),
    fictional_universe_qid   TEXT,
    fictional_universe_label TEXT,
    image_url                TEXT,
    local_image_path         TEXT,
    created_at               TEXT NOT NULL,
    enriched_at              TEXT
, wikidata_revision_id INTEGER);

-- User-authored graph-editor details are intentionally isolated from provider fields.
-- Enrichment can update fictional_entities without undoing editor choices.
CREATE TABLE IF NOT EXISTS fictional_entity_user_overrides (
    entity_id      BLOB NOT NULL PRIMARY KEY REFERENCES fictional_entities(id) ON DELETE CASCADE,
    label          TEXT NOT NULL,
    description    TEXT,
    updated_at     TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS narrative_root_user_overrides (
    qid            TEXT NOT NULL PRIMARY KEY REFERENCES narrative_roots(qid) ON DELETE CASCADE,
    label          TEXT NOT NULL,
    description    TEXT,
    updated_at     TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS fictional_entity_work_links (
    id                   BLOB NOT NULL PRIMARY KEY,
    appearance_key       TEXT NOT NULL UNIQUE,
    entity_id            BLOB NOT NULL REFERENCES fictional_entities(id) ON DELETE CASCADE,
    work_qid             TEXT NOT NULL,
    work_label           TEXT,
    link_type            TEXT NOT NULL DEFAULT 'appears_in',
    appearance_role      TEXT,
    work_context         TEXT,
    anchor_kind          TEXT,
    anchor_value         TEXT,
    narrative_time_index TEXT,
    start_time           TEXT,
    end_time             TEXT,
    spoiler_for_work_qid TEXT,
    source_provider      TEXT,
    provenance           TEXT NOT NULL DEFAULT 'Wikidata',
    is_supplemental      INTEGER NOT NULL DEFAULT 0 CHECK (is_supplemental IN (0, 1)),
    confidence           REAL
);

-- Durable provenance and orchestration state for generated AI features.
-- Canonical values remain the user-facing projection; this table records who
-- generated that projection and whether it was published, gated for review,
-- deferred for retry, or quarantined as poison work.
CREATE TABLE IF NOT EXISTS ai_feature_artifacts (
    entity_id             BLOB NOT NULL,
    feature_key           TEXT NOT NULL,
    source_provider_id    BLOB NOT NULL,
    status                TEXT NOT NULL
        CHECK (status IN ('Published', 'ReviewRequired', 'Protected', 'InsufficientData', 'RetryPending', 'Poisoned')),
    confidence            REAL,
    model_id              TEXT,
    prompt_version        TEXT,
    input_fingerprint     TEXT,
    output_fingerprint    TEXT,
    attempts              INTEGER NOT NULL DEFAULT 0,
    last_outcome_category TEXT,
    next_retry_at         TEXT,
    last_error            TEXT,
    outcome_reason        TEXT,
    published_fields_json TEXT NOT NULL DEFAULT '[]',
    protected_fields_json TEXT NOT NULL DEFAULT '[]',
    published_values_json TEXT NOT NULL DEFAULT '{}',
    updated_at            TEXT NOT NULL,
    PRIMARY KEY (entity_id, feature_key)
);

CREATE TABLE IF NOT EXISTS plugin_lore_sources (
    id                  BLOB PRIMARY KEY,
    universe_qid        TEXT NOT NULL,
    plugin_id           TEXT NOT NULL,
    source_key          TEXT NOT NULL,
    source_name         TEXT NOT NULL,
    base_url            TEXT NOT NULL,
    api_url             TEXT NOT NULL,
    status              TEXT NOT NULL DEFAULT 'Pending'
                            CHECK (status IN ('Pending', 'Approved', 'Rejected')),
    confidence          REAL NOT NULL DEFAULT 0.0,
    evidence_json       TEXT NOT NULL DEFAULT '{}',
    license             TEXT,
    approved_at         TEXT,
    approved_by         TEXT,
    rejected_at         TEXT,
    last_discovered_at  TEXT,
    last_enriched_at    TEXT,
    created_at          TEXT NOT NULL,
    updated_at          TEXT NOT NULL,
    UNIQUE (universe_qid, plugin_id, source_key)
);

CREATE TABLE IF NOT EXISTS plugin_lore_entities (
    id              BLOB PRIMARY KEY,
    source_id       BLOB NOT NULL REFERENCES plugin_lore_sources(id) ON DELETE CASCADE,
    universe_qid    TEXT NOT NULL,
    plugin_id       TEXT NOT NULL,
    external_key    TEXT NOT NULL,
    wikidata_qid    TEXT,
    label           TEXT NOT NULL,
    description     TEXT,
    entity_type     TEXT NOT NULL DEFAULT 'Unknown'
                        CHECK (entity_type IN ('Character', 'Location', 'Organization', 'Event', 'Object', 'Unknown')),
    aliases_json    TEXT NOT NULL DEFAULT '[]',
    source_url      TEXT NOT NULL,
    confidence      REAL NOT NULL DEFAULT 0.0,
    evidence_json   TEXT NOT NULL DEFAULT '{}',
    created_at      TEXT NOT NULL,
    updated_at      TEXT NOT NULL,
    UNIQUE (source_id, external_key)
);

CREATE TABLE IF NOT EXISTS plugin_lore_relationships (
    id                   BLOB PRIMARY KEY,
    source_id            BLOB NOT NULL REFERENCES plugin_lore_sources(id) ON DELETE CASCADE,
    universe_qid         TEXT NOT NULL,
    plugin_id            TEXT NOT NULL,
    subject_external_key TEXT NOT NULL,
    subject_qid          TEXT,
    object_external_key  TEXT NOT NULL,
    object_qid           TEXT,
    relationship_type    TEXT NOT NULL,
    source_url           TEXT NOT NULL,
    confidence           REAL NOT NULL DEFAULT 0.0,
    evidence_json        TEXT NOT NULL DEFAULT '{}',
    created_at           TEXT NOT NULL,
    updated_at           TEXT NOT NULL,
    UNIQUE (source_id, subject_external_key, relationship_type, object_external_key)
);

CREATE TABLE IF NOT EXISTS file_hash_cache (
    absolute_path TEXT COLLATE NOCASE NOT NULL PRIMARY KEY,
    size_bytes    INTEGER NOT NULL,
    mtime_utc     TEXT    NOT NULL,
    sha256        TEXT    NOT NULL,
    cached_at     TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS identity_jobs (
    id                     BLOB PRIMARY KEY,
    entity_id              BLOB NOT NULL,
    entity_type            TEXT NOT NULL,
    media_type             TEXT NOT NULL,
    ingestion_run_id       BLOB,
    state                  TEXT NOT NULL DEFAULT 'Queued',
    pass                   TEXT NOT NULL DEFAULT 'Quick',
    attempt_count          INTEGER NOT NULL DEFAULT 0,
    poison_attempt_count   INTEGER NOT NULL DEFAULT 0,
    last_outcome_category  TEXT,
    lease_owner            TEXT,
    lease_expires_at       TEXT,
    selected_candidate_id  BLOB,
    resolved_qid          TEXT,
    last_error             TEXT,
    next_retry_at          TEXT,
    created_at             TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at             TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS image_cache (
    content_hash  TEXT NOT NULL PRIMARY KEY,
    file_path     TEXT NOT NULL,
    source_url    TEXT,
    downloaded_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
, is_user_override INTEGER NOT NULL DEFAULT 0, phash INTEGER);

CREATE TABLE IF NOT EXISTS image_cache_sources (
    source_url    TEXT NOT NULL PRIMARY KEY,
    content_hash  TEXT NOT NULL,
    first_seen_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    FOREIGN KEY(content_hash) REFERENCES image_cache(content_hash) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS ingestion_batches (
    id                BLOB NOT NULL PRIMARY KEY,
    status            TEXT NOT NULL DEFAULT 'running',
    source_path       TEXT,
    category          TEXT,
    files_total       INTEGER NOT NULL DEFAULT 0,
    files_processed   INTEGER NOT NULL DEFAULT 0,
    files_registered  INTEGER NOT NULL DEFAULT 0,
    files_review      INTEGER NOT NULL DEFAULT 0,
    files_no_match    INTEGER NOT NULL DEFAULT 0,
    files_failed      INTEGER NOT NULL DEFAULT 0,
    started_at        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    completed_at      TEXT,
    created_at        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE TABLE IF NOT EXISTS ingestion_log (
    id                BLOB NOT NULL PRIMARY KEY,
    file_path         TEXT NOT NULL,
    media_asset_id    BLOB,
    content_hash      TEXT,
    status            TEXT NOT NULL DEFAULT 'detected',
    media_type        TEXT,
    confidence_score  REAL,
    detected_title    TEXT,
    normalized_title  TEXT,
    wikidata_qid      TEXT,
    error_detail      TEXT,
    ingestion_run_id  BLOB,
    created_at        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE TABLE IF NOT EXISTS ingestion_batch_artifacts (
    id                BLOB NOT NULL PRIMARY KEY,
    batch_id          BLOB NOT NULL,
    artifact_type     TEXT NOT NULL,
    artifact_id       BLOB,
    parent_entity_id  BLOB,
    parent_entity_type TEXT,
    action            TEXT NOT NULL,
    display_name      TEXT,
    provider_id       TEXT,
    source            TEXT,
    detail_json       TEXT,
    occurred_at       TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE TABLE IF NOT EXISTS media_assets (
    id             BLOB NOT NULL PRIMARY KEY,  -- UUID
    edition_id     BLOB NOT NULL REFERENCES editions(id) ON DELETE CASCADE,
    content_hash   TEXT NOT NULL UNIQUE,       -- reconciliation key; SHA-256 hex, lowercase
    file_path_root TEXT NOT NULL,              -- FS path, no BLOBs
    presented_at   TEXT,                       -- first time the asset became visible in browse
    status         TEXT NOT NULL DEFAULT 'Normal'
                       CHECK (status IN ('Normal', 'Conflicted', 'Orphaned')),

    -- ── Auto re-tag sweep state (M-084) ──────────────────────────────
    -- writeback_fields_hash: applied SHA-256 only for verified writes;
    -- unverified:/unsupported: prefixes record a bounded attempt for one
    -- config/adapter hash. NULL means no sweep attempt.
    writeback_fields_hash    TEXT,
    -- writeback_status: 'ok' | 'pending' | 'retry' | 'failed' |
    -- 'unverified' | 'unsupported'. NULL until
    -- the sweep first touches the asset.
    writeback_status         TEXT,
    writeback_last_error     TEXT,
    writeback_attempts       INTEGER NOT NULL DEFAULT 0,
    -- writeback_next_retry_at: unix epoch seconds — used by the sweep
    -- worker to skip rows whose retry window hasn't opened yet.
    writeback_next_retry_at  INTEGER
, library_id TEXT, is_orphaned INTEGER NOT NULL DEFAULT 0, orphaned_at TEXT,
    rendition_purpose TEXT NOT NULL DEFAULT 'Original'
        CHECK(rendition_purpose IN ('Original','Mobile','Offline','Compatibility','Other')),
    derived_from_asset_id BLOB REFERENCES media_assets(id) ON DELETE SET NULL,
    encoder_profile_version TEXT,
    rendition_width INTEGER,
    rendition_height INTEGER,
    rendition_bitrate_bps INTEGER,
    rendition_video_codec TEXT,
    rendition_audio_codec TEXT,
    rendition_dynamic_range TEXT,
    rendition_audio_layout TEXT,
    rendition_generated_at TEXT,
    rendition_source_fingerprint TEXT,
    CHECK(derived_from_asset_id IS NULL OR derived_from_asset_id != id));

-- An editor commit and its file-sync intent are recorded in the same transaction
-- as the owned-file reassociation. A retry of the same operation token reads this
-- receipt instead of applying the edit twice.
CREATE TABLE IF NOT EXISTS media_editor_commits (
    operation_token TEXT NOT NULL PRIMARY KEY,
    request_hash TEXT NOT NULL,
    asset_id BLOB NOT NULL,
    source_work_id BLOB NOT NULL,
    target_work_id BLOB NOT NULL,
    target_tvdb_episode_id TEXT NOT NULL,
    committed_at TEXT NOT NULL,
    sync_state TEXT NOT NULL DEFAULT 'pending'
);
CREATE INDEX IF NOT EXISTS ix_media_editor_commits_asset ON media_editor_commits(asset_id, committed_at);

-- Coalescing durable command for synchronizing the latest effective metadata to
-- an owned file. A later library edit increments generation and returns the row
-- to pending; workers complete only the generation they claimed.
CREATE TABLE IF NOT EXISTS media_file_write_intents (
    asset_id BLOB NOT NULL PRIMARY KEY REFERENCES media_assets(id) ON DELETE CASCADE,
    generation INTEGER NOT NULL DEFAULT 1,
    operation_token TEXT NOT NULL,
    trigger TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending'
        CHECK(status IN ('pending','writing','verified','blocked','unsupported','failed')),
    attempts INTEGER NOT NULL DEFAULT 0,
    lease_expires_at TEXT,
    last_error TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_media_file_write_intents_dispatch
    ON media_file_write_intents(status, lease_expires_at, updated_at);

CREATE TABLE IF NOT EXISTS media_editor_commit_items (
    operation_token TEXT NOT NULL REFERENCES media_editor_commits(operation_token) ON DELETE CASCADE,
    asset_id BLOB NOT NULL,
    source_edition_id BLOB NOT NULL,
    source_work_id BLOB NOT NULL,
    target_work_id BLOB NOT NULL,
    source_season_work_id BLOB NOT NULL,
    target_season_work_id BLOB NOT NULL,
    PRIMARY KEY (operation_token, asset_id)
);

CREATE TABLE IF NOT EXISTS media_editor_music_pairing_commits (
    operation_token TEXT NOT NULL PRIMARY KEY,
    request_hash TEXT NOT NULL,
    committed_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS media_editor_music_pairing_commit_items (
    operation_token TEXT NOT NULL REFERENCES media_editor_music_pairing_commits(operation_token) ON DELETE CASCADE,
    asset_id BLOB NOT NULL,
    edition_id BLOB NOT NULL,
    work_id BLOB NOT NULL,
    release_id TEXT NOT NULL,
    release_track_id TEXT NOT NULL,
    PRIMARY KEY (operation_token, asset_id)
);

-- An optional reviewed episode-still choice is audited with the same commit
-- receipt. Existing artwork links remain available after preference changes.
CREATE TABLE IF NOT EXISTS media_editor_commit_artwork (
    operation_token TEXT NOT NULL PRIMARY KEY REFERENCES media_editor_commits(operation_token) ON DELETE CASCADE,
    owner_work_id BLOB NOT NULL,
    artwork_asset_id BLOB NOT NULL,
    expected_preference_revision TEXT NOT NULL,
    previous_preferred_ids_json TEXT NOT NULL,
    affected_asset_ids_json TEXT NOT NULL,
    committed_at TEXT NOT NULL
);

-- Storage-only receipt for a reviewed preferred-artwork choice. The associated
-- source file is not modified by this preference transaction.
CREATE TABLE IF NOT EXISTS media_editor_preferred_artwork_commits (
    operation_token TEXT NOT NULL PRIMARY KEY,
    request_hash TEXT NOT NULL,
    owner_work_id BLOB NOT NULL,
    owner_scope TEXT NOT NULL,
    role TEXT NOT NULL,
    artwork_asset_id BLOB NOT NULL,
    expected_owner_revision TEXT NOT NULL,
    previous_preferred_ids_json TEXT NOT NULL,
    affected_assets_json TEXT NOT NULL,
    committed_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS media_editor_edition_artwork_commits (
    operation_token TEXT NOT NULL PRIMARY KEY,
    request_hash TEXT NOT NULL,
    edition_id BLOB NOT NULL,
    work_id BLOB NOT NULL,
    artwork_asset_id BLOB NOT NULL,
    expected_revision TEXT NOT NULL,
    previous_preferred_ids_json TEXT NOT NULL,
    affected_assets_json TEXT NOT NULL,
    committed_at TEXT NOT NULL
);

-- Artwork embedding is tracked separately from metadata retagging and sidecar export.
-- 'embedded' is recorded only after the physical file tag is read back and verified.
CREATE TABLE IF NOT EXISTS media_artwork_writeback (
    media_asset_id BLOB NOT NULL PRIMARY KEY REFERENCES media_assets(id) ON DELETE CASCADE,
    desired_artwork_asset_id BLOB NOT NULL,
    desired_version TEXT,
    embedded_artwork_asset_id BLOB,
    status TEXT NOT NULL CHECK(status IN ('pending','writing','embedded','failed')),
    attempts INTEGER NOT NULL DEFAULT 0,
    last_error TEXT,
    file_modified_utc TEXT,
    file_size_bytes INTEGER,
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX IF NOT EXISTS idx_media_artwork_writeback_status
    ON media_artwork_writeback(status, updated_at);

-- A Personal Space is the stable View identity owned by exactly one profile.
-- library_id remains the bridge to configured intake libraries while sources
-- and devices describe where files came from. Shared View is intentionally not
-- persisted here: it is an authorized projection across Personal Spaces.
CREATE TABLE IF NOT EXISTS view_personal_spaces (
    id                  BLOB NOT NULL PRIMARY KEY,
    owner_profile_id    BLOB NOT NULL UNIQUE REFERENCES profiles(id) ON DELETE CASCADE,
    library_id          BLOB NOT NULL UNIQUE,
    created_at          TEXT NOT NULL,
    updated_at          TEXT NOT NULL
);

-- Storage labels are immutable reservations, independent of login names.
CREATE TABLE IF NOT EXISTS view_storage_labels (
    personal_space_id BLOB NOT NULL PRIMARY KEY,
    label TEXT NOT NULL UNIQUE COLLATE NOCASE
);

CREATE TABLE IF NOT EXISTS view_sources (
    id                  BLOB NOT NULL PRIMARY KEY,
    scope_kind          TEXT NOT NULL CHECK (scope_kind IN ('personal','shared')),
    personal_space_id   BLOB REFERENCES view_personal_spaces(id) ON DELETE CASCADE,
    library_id          BLOB NOT NULL,
    source_type         TEXT NOT NULL
                            CHECK (source_type IN ('folder', 'browser_upload', 'device_import',
                                                   'mobile_backup', 'network', 'other')),
    name                TEXT NOT NULL,
    source_key          TEXT,
    storage_mode        TEXT NOT NULL DEFAULT 'managed'
                            CHECK (storage_mode IN ('managed', 'linked')),
    relative_path       TEXT,
    external_path       TEXT,
    include_subdirectories INTEGER NOT NULL DEFAULT 1
                            CHECK (include_subdirectories IN (0, 1)),
    enabled             INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    last_activity_at    TEXT,
    created_at          TEXT NOT NULL,
    updated_at          TEXT NOT NULL,
    CHECK ((storage_mode = 'managed' AND relative_path IS NOT NULL AND external_path IS NULL)
        OR (storage_mode = 'linked' AND external_path IS NOT NULL AND relative_path IS NULL)),
    CHECK ((scope_kind = 'personal' AND personal_space_id IS NOT NULL)
        OR (scope_kind = 'shared' AND personal_space_id IS NULL))
);

CREATE TABLE IF NOT EXISTS view_shared_library (
    singleton_key INTEGER NOT NULL PRIMARY KEY CHECK (singleton_key = 1),
    library_id BLOB NOT NULL UNIQUE,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TRIGGER IF NOT EXISTS trg_view_personal_space_shared_collision_insert
BEFORE INSERT ON view_personal_spaces WHEN EXISTS (
    SELECT 1 FROM view_shared_library WHERE library_id=NEW.library_id)
BEGIN SELECT RAISE(ABORT,'Personal Space library cannot use the Shared library identity'); END;
CREATE TRIGGER IF NOT EXISTS trg_view_personal_space_shared_collision_update
BEFORE UPDATE OF library_id ON view_personal_spaces WHEN EXISTS (
    SELECT 1 FROM view_shared_library WHERE library_id=NEW.library_id)
BEGIN SELECT RAISE(ABORT,'Personal Space library cannot use the Shared library identity'); END;
CREATE TRIGGER IF NOT EXISTS trg_view_personal_space_identity_immutable
BEFORE UPDATE OF owner_profile_id,library_id ON view_personal_spaces
WHEN NEW.owner_profile_id<>OLD.owner_profile_id OR NEW.library_id<>OLD.library_id
BEGIN SELECT RAISE(ABORT,'Personal Space owner and library identities are immutable'); END;
CREATE TRIGGER IF NOT EXISTS trg_view_shared_library_collision_insert
BEFORE INSERT ON view_shared_library WHEN EXISTS (
    SELECT 1 FROM view_personal_spaces WHERE library_id=NEW.library_id)
BEGIN SELECT RAISE(ABORT,'Shared library identity cannot be used by a Personal Space'); END;
CREATE TRIGGER IF NOT EXISTS trg_view_shared_library_identity_immutable
BEFORE UPDATE OF library_id ON view_shared_library WHEN NEW.library_id<>OLD.library_id
BEGIN SELECT RAISE(ABORT,'Shared library identity is immutable'); END;
CREATE TRIGGER IF NOT EXISTS trg_view_shared_library_delete
BEFORE DELETE ON view_shared_library
BEGIN SELECT RAISE(ABORT,'Shared library identity cannot be deleted'); END;
INSERT OR IGNORE INTO view_shared_library(singleton_key, library_id, created_at, updated_at)
VALUES (1, X'00000000000000000000000000000003',
    strftime('%Y-%m-%dT%H:%M:%fZ','now'), strftime('%Y-%m-%dT%H:%M:%fZ','now'));

-- Timeline inclusion is separate from source registration. Browser uploads
-- participate by default; additional folders opt in explicitly.
CREATE TABLE IF NOT EXISTS view_source_policies (
    source_id            BLOB NOT NULL PRIMARY KEY REFERENCES view_sources(id) ON DELETE CASCADE,
    include_in_timeline  INTEGER NOT NULL DEFAULT 0 CHECK (include_in_timeline IN (0, 1)),
    updated_at           TEXT NOT NULL
);

-- Folder-level View preferences. Pins are private to the profile. Timeline
-- policies belong to the source and are evaluated from the most-specific
-- ancestor so a branch can override its source default.
CREATE TABLE IF NOT EXISTS view_folder_pins (
    profile_id      BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    source_id       BLOB NOT NULL REFERENCES view_sources(id) ON DELETE CASCADE,
    relative_path   TEXT NOT NULL,
    created_at      TEXT NOT NULL,
    PRIMARY KEY (profile_id, source_id, relative_path)
);

CREATE TABLE IF NOT EXISTS view_folder_timeline_policies (
    source_id           BLOB NOT NULL REFERENCES view_sources(id) ON DELETE CASCADE,
    relative_path       TEXT NOT NULL,
    absolute_path       TEXT NOT NULL,
    include_in_timeline INTEGER NOT NULL CHECK (include_in_timeline IN (0, 1)),
    updated_by_profile_id BLOB NOT NULL REFERENCES profiles(id),
    updated_at          TEXT NOT NULL,
    PRIMARY KEY (source_id, relative_path)
);

-- An accepted Shared Library item remains traceable to its originating profile
-- while its durable physical keeper location belongs to the server library.
CREATE TABLE IF NOT EXISTS view_shared_assets (
    item_id                BLOB NOT NULL PRIMARY KEY REFERENCES local_items(id) ON DELETE RESTRICT,
    origin_item_id         BLOB REFERENCES local_items(id) ON DELETE SET NULL,
    original_profile_id    BLOB REFERENCES profiles(id) ON DELETE SET NULL,
    destination_kind       TEXT NOT NULL CHECK (destination_kind IN ('timeline', 'folder')),
    destination_label      TEXT,
    promoted_by_profile_id BLOB REFERENCES profiles(id) ON DELETE SET NULL,
    promoted_at            TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS view_shared_contributions (
    id                         BLOB NOT NULL PRIMARY KEY,
    submitted_by_profile_id    BLOB REFERENCES profiles(id) ON DELETE SET NULL,
    submitted_by_name          TEXT NOT NULL,
    status                     TEXT NOT NULL CHECK (status IN ('pending', 'accepted', 'declined', 'cancelled')),
    destination_kind           TEXT NOT NULL CHECK (destination_kind IN ('timeline', 'folder')),
    destination_label          TEXT,
    note                       TEXT,
    revision                   INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
    idempotency_key            TEXT NOT NULL,
    submitted_at               TEXT NOT NULL,
    decided_at                 TEXT,
    decided_by_profile_id      BLOB REFERENCES profiles(id) ON DELETE SET NULL,
    decided_by_name            TEXT,
    decision_reason            TEXT,
    updated_at                 TEXT NOT NULL,
    UNIQUE(submitted_by_profile_id, idempotency_key)
);

CREATE TABLE IF NOT EXISTS view_shared_contribution_items (
    id                  BLOB NOT NULL PRIMARY KEY,
    contribution_id     BLOB NOT NULL REFERENCES view_shared_contributions(id) ON DELETE CASCADE,
    item_id              BLOB NOT NULL REFERENCES local_items(id) ON DELETE RESTRICT,
    original_profile_id BLOB REFERENCES profiles(id) ON DELETE SET NULL,
    original_profile_name TEXT NOT NULL,
    position            INTEGER NOT NULL,
    operation           TEXT NOT NULL CHECK (operation IN ('move', 'copy')),
    source_manifest_json TEXT NOT NULL CHECK (json_valid(source_manifest_json)),
    execution_state     TEXT NOT NULL CHECK (execution_state IN ('waiting', 'transferring', 'completed', 'cleanup_pending', 'needs_attention', 'failed')),
    error               TEXT,
    updated_at          TEXT NOT NULL,
    UNIQUE(contribution_id, item_id)
);

CREATE INDEX IF NOT EXISTS idx_view_shared_contributions_submitter
    ON view_shared_contributions(submitted_by_profile_id, submitted_at DESC);
CREATE INDEX IF NOT EXISTS idx_view_shared_contributions_review
    ON view_shared_contributions(status, submitted_at);
CREATE INDEX IF NOT EXISTS idx_view_shared_contribution_items_batch
    ON view_shared_contribution_items(contribution_id, position);

CREATE TABLE IF NOT EXISTS view_shared_contribution_events (
    id               BLOB NOT NULL PRIMARY KEY,
    contribution_id  BLOB NOT NULL REFERENCES view_shared_contributions(id) ON DELETE CASCADE,
    actor_profile_id BLOB REFERENCES profiles(id) ON DELETE SET NULL,
    actor_name        TEXT NOT NULL,
    event_type        TEXT NOT NULL,
    detail            TEXT,
    occurred_at       TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_view_shared_contribution_events_batch
    ON view_shared_contribution_events(contribution_id, occurred_at, id);

CREATE TABLE IF NOT EXISTS view_shared_transfers (
    id                     BLOB NOT NULL PRIMARY KEY,
    item_id                BLOB NOT NULL REFERENCES local_items(id) ON DELETE RESTRICT,
    contribution_item_id   BLOB REFERENCES view_shared_contribution_items(id) ON DELETE SET NULL,
    operation              TEXT NOT NULL CHECK (operation IN ('move', 'copy')),
    state                  TEXT NOT NULL CHECK (state IN ('planned', 'transferring', 'completed', 'cleanup_pending', 'failed')),
    source_manifest_json   TEXT NOT NULL CHECK (json_valid(source_manifest_json)),
    destination_manifest_json TEXT CHECK (destination_manifest_json IS NULL OR json_valid(destination_manifest_json)),
    error                  TEXT,
    created_at             TEXT NOT NULL,
    updated_at             TEXT NOT NULL,
    completed_at           TEXT,
    UNIQUE(item_id)
);

CREATE TABLE IF NOT EXISTS view_devices (
    id                  BLOB NOT NULL PRIMARY KEY,
    personal_space_id   BLOB NOT NULL REFERENCES view_personal_spaces(id) ON DELETE CASCADE,
    source_id           BLOB REFERENCES view_sources(id) ON DELETE SET NULL,
    client_device_id    TEXT NOT NULL,
    name                TEXT NOT NULL,
    make                TEXT,
    model               TEXT,
    last_backup_at      TEXT,
    backup_state        TEXT NOT NULL DEFAULT 'unknown'
                            CHECK (backup_state IN ('unknown', 'idle', 'backing_up', 'complete', 'error')),
    created_at          TEXT NOT NULL,
    updated_at          TEXT NOT NULL,
    UNIQUE (personal_space_id, client_device_id)
);

-- Generalized local assets are the mixed-media model for personal View.
-- A logical item is explicitly owner- and Personal-Space-scoped and may be composed of
-- several exact, hash-addressed files (for example HEIC + MOV Live Photos,
-- RAW + JPEG + XMP sets, or generated previews). Each physical file retains
-- every path at which it was observed, so exact duplicates never erase source
-- provenance. This model is intentionally independent from catalogue identity.
CREATE TABLE IF NOT EXISTS local_items (
    id                  BLOB NOT NULL PRIMARY KEY,
    scope_kind          TEXT NOT NULL CHECK (scope_kind IN ('personal','shared')),
    personal_space_id   BLOB REFERENCES view_personal_spaces(id) ON DELETE CASCADE,
    owner_profile_id    BLOB REFERENCES profiles(id) ON DELETE CASCADE,
    library_id          BLOB NOT NULL,
    media_kind          TEXT NOT NULL
                            CHECK (media_kind IN ('image', 'video', 'document', 'audio', 'other')),
    title               TEXT,
    primary_file_name   TEXT NOT NULL,
    primary_mime_type   TEXT NOT NULL,
    captured_at         TEXT,
    embedded_captured_at TEXT,
    captured_at_user_override INTEGER NOT NULL DEFAULT 0 CHECK (captured_at_user_override IN (0, 1)),
    created_at          TEXT NOT NULL,
    updated_at          TEXT NOT NULL,
    favorite            INTEGER NOT NULL DEFAULT 0 CHECK (favorite IN (0, 1)),
    hidden              INTEGER NOT NULL DEFAULT 0 CHECK (hidden IN (0, 1)),
    archived_at         TEXT,
    trashed_at          TEXT,
    CHECK ((scope_kind = 'personal' AND personal_space_id IS NOT NULL AND owner_profile_id IS NOT NULL)
        OR (scope_kind = 'shared' AND personal_space_id IS NULL AND owner_profile_id IS NULL)),
    UNIQUE (personal_space_id, id)
);

CREATE TABLE IF NOT EXISTS local_item_metadata (
    item_id             BLOB NOT NULL PRIMARY KEY REFERENCES local_items(id) ON DELETE CASCADE,
    width               INTEGER CHECK (width IS NULL OR width >= 0),
    height              INTEGER CHECK (height IS NULL OR height >= 0),
    duration_seconds    REAL CHECK (duration_seconds IS NULL OR duration_seconds >= 0),
    page_count          INTEGER CHECK (page_count IS NULL OR page_count >= 0),
    device_make         TEXT,
    device_model        TEXT,
    latitude            REAL CHECK (latitude IS NULL OR (latitude >= -90 AND latitude <= 90)),
    longitude           REAL CHECK (longitude IS NULL OR (longitude >= -180 AND longitude <= 180)),
    location_name       TEXT,
    location_city       TEXT,
    location_region     TEXT,
    location_country    TEXT,
    location_country_code TEXT,
    location_source     TEXT,
    location_user_override INTEGER NOT NULL DEFAULT 0 CHECK (location_user_override IN (0, 1)),
    embedded_latitude   REAL CHECK (embedded_latitude IS NULL OR (embedded_latitude >= -90 AND embedded_latitude <= 90)),
    embedded_longitude  REAL CHECK (embedded_longitude IS NULL OR (embedded_longitude >= -180 AND embedded_longitude <= 180)),
    description         TEXT,
    lens_model          TEXT,
    exposure_time       TEXT,
    aperture            REAL,
    iso                 INTEGER,
    focal_length_mm     REAL,
    video_codec         TEXT,
    frame_rate          REAL,
    document_text       TEXT,
    metadata_json       TEXT,
    updated_at          TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS local_files (
    id                  BLOB NOT NULL PRIMARY KEY,
    content_hash        TEXT NOT NULL UNIQUE COLLATE NOCASE,
    byte_size           INTEGER NOT NULL CHECK (byte_size >= 0),
    mime_type           TEXT NOT NULL,
    extension           TEXT,
    created_at          TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS local_file_sources (
    id                  BLOB NOT NULL PRIMARY KEY,
    file_id             BLOB NOT NULL REFERENCES local_files(id) ON DELETE CASCADE,
    library_id          BLOB NOT NULL,
    source_id           BLOB REFERENCES view_sources(id) ON DELETE SET NULL,
    device_id           BLOB REFERENCES view_devices(id) ON DELETE SET NULL,
    file_path           TEXT NOT NULL,
    modified_at         TEXT NOT NULL,
    indexed_at          TEXT NOT NULL,
    UNIQUE (library_id, file_path COLLATE NOCASE)
);

CREATE TABLE IF NOT EXISTS local_item_files (
    item_id             BLOB NOT NULL REFERENCES local_items(id) ON DELETE CASCADE,
    file_id             BLOB NOT NULL REFERENCES local_files(id) ON DELETE RESTRICT,
    role                TEXT NOT NULL
                            CHECK (role IN ('primary', 'original', 'live_photo_video', 'raw',
                                            'jpeg', 'sidecar', 'audio_companion', 'derivative')),
    derivative_kind     TEXT,
    position            INTEGER NOT NULL DEFAULT 0,
    added_at            TEXT NOT NULL,
    PRIMARY KEY (item_id, file_id, role)
);

CREATE TABLE IF NOT EXISTS local_item_tags (
    item_id             BLOB NOT NULL REFERENCES local_items(id) ON DELETE CASCADE,
    tag                 TEXT NOT NULL COLLATE NOCASE,
    added_at            TEXT NOT NULL,
    PRIMARY KEY (item_id, tag)
);

CREATE TABLE IF NOT EXISTS view_galleries (
    id                  BLOB NOT NULL PRIMARY KEY,
    owner_profile_id    BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    personal_space_id   BLOB NOT NULL REFERENCES view_personal_spaces(id) ON DELETE CASCADE,
    name                TEXT NOT NULL,
    description         TEXT,
    gallery_kind        TEXT NOT NULL CHECK (gallery_kind IN ('manual', 'smart')),
    smart_rule_json     TEXT CHECK (smart_rule_json IS NULL OR json_valid(smart_rule_json)),
    cover_item_id       BLOB REFERENCES local_items(id) ON DELETE SET NULL,
    soundtrack_playlist_id BLOB REFERENCES collections(id) ON DELETE SET NULL,
    sort_order          INTEGER NOT NULL DEFAULT 0,
    created_at          TEXT NOT NULL,
    updated_at          TEXT NOT NULL,
    CHECK ((gallery_kind = 'manual' AND smart_rule_json IS NULL)
        OR (gallery_kind = 'smart' AND smart_rule_json IS NOT NULL))
);

CREATE TABLE IF NOT EXISTS view_gallery_items (
    gallery_id          BLOB NOT NULL REFERENCES view_galleries(id) ON DELETE CASCADE,
    item_id             BLOB NOT NULL REFERENCES local_items(id) ON DELETE CASCADE,
    position            INTEGER NOT NULL DEFAULT 0,
    added_at            TEXT NOT NULL,
    PRIMARY KEY (gallery_id, item_id)
);

CREATE TABLE IF NOT EXISTS view_gallery_shares (
    gallery_id          BLOB NOT NULL REFERENCES view_galleries(id) ON DELETE CASCADE,
    profile_id          BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    permission          TEXT NOT NULL DEFAULT 'view' CHECK (permission IN ('view', 'contribute')),
    shared_at           TEXT NOT NULL,
    PRIMARY KEY (gallery_id, profile_id)
);

-- Personal-media inputs for administrator-authored Custom Collections. Each
-- row remains a dynamic Gallery reference or a saved View rule; local asset
-- IDs and expanded Gallery membership are intentionally absent.
CREATE TABLE IF NOT EXISTS collection_view_sources (
    id                  BLOB NOT NULL PRIMARY KEY,
    collection_id       BLOB NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
    owner_profile_id    BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    source_kind         TEXT NOT NULL CHECK (source_kind IN ('gallery', 'smart_rule')),
    gallery_id          BLOB REFERENCES view_galleries(id) ON DELETE CASCADE,
    rule_version        INTEGER CHECK (rule_version IS NULL OR rule_version = 1),
    rule_json           TEXT CHECK (
                            rule_json IS NULL
                            OR CASE WHEN json_valid(rule_json)
                                    THEN json_type(rule_json) = 'object'
                                    ELSE 0 END),
    position            INTEGER NOT NULL DEFAULT 0 CHECK (position >= 0),
    created_at          TEXT NOT NULL,
    updated_at          TEXT NOT NULL,
    CHECK ((source_kind = 'gallery'
            AND gallery_id IS NOT NULL AND rule_version IS NULL AND rule_json IS NULL)
        OR (source_kind = 'smart_rule'
            AND gallery_id IS NULL AND rule_version IS NOT NULL AND rule_json IS NOT NULL))
);

-- Reserved for optional, provenance-aware inferred metadata. The table does
-- not run inference; it only prevents future annotations from being confused
-- with user-authored or extracted facts.
CREATE TABLE IF NOT EXISTS local_item_annotations (
    id                  BLOB NOT NULL PRIMARY KEY,
    item_id             BLOB NOT NULL REFERENCES local_items(id) ON DELETE CASCADE,
    annotation_kind     TEXT NOT NULL,
    annotation_value    TEXT NOT NULL,
    confidence          REAL CHECK (confidence IS NULL OR (confidence >= 0 AND confidence <= 1)),
    source              TEXT NOT NULL,
    model_name          TEXT,
    model_version       TEXT,
    provenance_json     TEXT,
    created_at          TEXT NOT NULL,
    reviewed_at         TEXT
);

-- FTS rowids are mapped to BLOB GUIDs in a normal table. This avoids storing
-- an internal identifier as text merely because FTS5 is text-oriented.
CREATE TABLE IF NOT EXISTS local_item_search_keys (
    rowid               INTEGER PRIMARY KEY AUTOINCREMENT,
    item_id             BLOB NOT NULL UNIQUE REFERENCES local_items(id) ON DELETE CASCADE
);

CREATE VIRTUAL TABLE IF NOT EXISTS local_item_search USING fts5(
    title,
    file_name,
    mime_type,
    media_kind,
    captured_at,
    device,
    location,
    document_text,
    tags,
    tokenize = 'unicode61 remove_diacritics 2'
);

CREATE TRIGGER IF NOT EXISTS trg_local_items_delete_search
BEFORE DELETE ON local_items
BEGIN
    DELETE FROM local_item_search
     WHERE rowid = (SELECT rowid FROM local_item_search_keys WHERE item_id = OLD.id);
END;

CREATE INDEX IF NOT EXISTS ix_view_personal_spaces_library
    ON view_personal_spaces(library_id, owner_profile_id);
CREATE INDEX IF NOT EXISTS ix_view_sources_space_activity
    ON view_sources(personal_space_id, last_activity_at DESC, id);
CREATE INDEX IF NOT EXISTS ix_view_devices_space_backup
    ON view_devices(personal_space_id, last_backup_at DESC, id);
CREATE INDEX IF NOT EXISTS ix_local_items_owner_timeline
    ON local_items(owner_profile_id, archived_at, trashed_at,
                   COALESCE(captured_at, created_at) DESC, id DESC);
CREATE INDEX IF NOT EXISTS ix_local_items_library_timeline
    ON local_items(library_id, archived_at, trashed_at,
                   COALESCE(captured_at, created_at) DESC, id DESC);
CREATE INDEX IF NOT EXISTS ix_local_items_space_timeline
    ON local_items(personal_space_id, archived_at, trashed_at,
                   COALESCE(captured_at, created_at) DESC, id DESC);
CREATE INDEX IF NOT EXISTS ix_local_items_library_kind
    ON local_items(library_id, media_kind, hidden, archived_at, trashed_at);
CREATE INDEX IF NOT EXISTS ix_local_items_library_favorite_timeline
    ON local_items(library_id, favorite, archived_at, trashed_at,
                   COALESCE(captured_at, created_at) DESC, id DESC);
CREATE INDEX IF NOT EXISTS ix_local_item_metadata_location
    ON local_item_metadata(location_name, latitude, longitude, item_id)
    WHERE latitude IS NOT NULL AND longitude IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_local_items_library_atlas_timeline
    ON local_items(library_id, hidden, archived_at, trashed_at, media_kind,
                   favorite, COALESCE(captured_at, created_at), id);
CREATE INDEX IF NOT EXISTS ix_local_items_active_discovery
    ON local_items(library_id, hidden, archived_at, trashed_at, id);
CREATE INDEX IF NOT EXISTS ix_local_files_hash
    ON local_files(content_hash COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS ix_local_file_sources_file
    ON local_file_sources(file_id, indexed_at DESC);
CREATE INDEX IF NOT EXISTS ix_local_file_sources_library
    ON local_file_sources(library_id, file_path COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS ix_local_file_sources_source
    ON local_file_sources(source_id, indexed_at DESC, id);
CREATE INDEX IF NOT EXISTS ix_local_file_sources_device
    ON local_file_sources(device_id, indexed_at DESC, id);
CREATE INDEX IF NOT EXISTS ix_local_item_files_item
    ON local_item_files(item_id, role, position);
CREATE INDEX IF NOT EXISTS ix_local_item_files_file
    ON local_item_files(file_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_local_item_single_primary
    ON local_item_files(item_id)
    WHERE role = 'primary';
CREATE INDEX IF NOT EXISTS ix_view_galleries_owner_order
    ON view_galleries(owner_profile_id, sort_order, updated_at DESC, id);
CREATE INDEX IF NOT EXISTS ix_view_galleries_space_kind
    ON view_galleries(personal_space_id, gallery_kind, updated_at DESC, id);
CREATE INDEX IF NOT EXISTS ix_view_gallery_items_gallery
    ON view_gallery_items(gallery_id, position, added_at, item_id);
CREATE INDEX IF NOT EXISTS ix_view_gallery_items_item
    ON view_gallery_items(item_id, gallery_id);
CREATE INDEX IF NOT EXISTS ix_view_gallery_shares_profile
    ON view_gallery_shares(profile_id, shared_at DESC, gallery_id);
CREATE INDEX IF NOT EXISTS ix_local_item_tags_tag
    ON local_item_tags(tag, item_id);
CREATE INDEX IF NOT EXISTS ix_collection_view_sources_collection
    ON collection_view_sources(collection_id, position, id);
CREATE INDEX IF NOT EXISTS ix_collection_view_sources_owner
    ON collection_view_sources(owner_profile_id, source_kind, collection_id, id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_collection_view_sources_gallery
    ON collection_view_sources(collection_id, gallery_id)
    WHERE source_kind = 'gallery';
CREATE INDEX IF NOT EXISTS ix_local_item_annotations_item
    ON local_item_annotations(item_id, annotation_kind, created_at);
CREATE INDEX IF NOT EXISTS ix_local_item_annotations_lookup
    ON local_item_annotations(annotation_kind, annotation_value, item_id);
CREATE INDEX IF NOT EXISTS ix_local_item_annotations_people
    ON local_item_annotations(annotation_kind, reviewed_at, annotation_value, item_id)
    WHERE annotation_kind IN ('person_name', 'named_person', 'face_name',
                              'person_identity', 'face_identity');

CREATE TABLE IF NOT EXISTS media_operation_events (
  id             BLOB PRIMARY KEY,
  operation_id   BLOB NOT NULL,
  entity_id      BLOB,
  batch_id       BLOB,
  event_type     TEXT NOT NULL,
  old_status     TEXT,
  new_status     TEXT,
  old_stage      TEXT,
  new_stage      TEXT,
  message        TEXT,
  detail_json    TEXT,
  occurred_at    TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS media_operations (
  id                  BLOB PRIMARY KEY,
  operation_type      TEXT NOT NULL,
  operation_kind      TEXT NOT NULL,
  entity_id           BLOB,
  entity_kind         TEXT,
  batch_id            BLOB,
  source_path         TEXT,
  content_hash        TEXT,
  capability_id       TEXT,
  capability_version  TEXT,
  sub_key             TEXT,
  plugin_id           TEXT,
  plugin_version      TEXT,
  provider_id         TEXT,
  model_id            TEXT,
  status              TEXT NOT NULL,
  stage               TEXT,
  priority            INTEGER NOT NULL DEFAULT 100,
  queue_name          TEXT NOT NULL DEFAULT 'default',
  position_key        INTEGER NOT NULL,
  attempt_count       INTEGER NOT NULL DEFAULT 0,
  poison_attempt_count INTEGER NOT NULL DEFAULT 0,
  lease_owner         TEXT,
  lease_expires_at    TEXT,
  heartbeat_at        TEXT,
  next_retry_at       TEXT,
  progress_percent    INTEGER NOT NULL DEFAULT 0,
  items_total         INTEGER NOT NULL DEFAULT 0,
  items_completed     INTEGER NOT NULL DEFAULT 0,
  items_failed        INTEGER NOT NULL DEFAULT 0,
  result_summary      TEXT,
  last_error          TEXT,
  last_outcome_category TEXT,
  missing_reason      TEXT,
  created_at          TEXT NOT NULL,
  started_at          TEXT,
  updated_at          TEXT NOT NULL,
  completed_at        TEXT,
  idempotency_key     TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS metadata_claims (
    id          BLOB NOT NULL PRIMARY KEY,  -- UUID
    entity_id   BLOB NOT NULL,              -- FK → works.id | editions.id (polymorphic)
    provider_id BLOB NOT NULL REFERENCES metadata_providers(id),
    decision_source_provider_id BLOB REFERENCES metadata_providers(id),
    observation_set_id BLOB,
    claim_key   TEXT NOT NULL,
    claim_value TEXT NOT NULL,
    confidence  REAL NOT NULL DEFAULT 1.0,
    -- Timestamp used by the scoring engine for stale-claim time-decay.
    -- Spec: Phase 6 – Stale Claim Handling.
    claimed_at     TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    -- When 1, the scoring engine treats this claim as unconditional winner
    -- (confidence 1.0); no automated provider may set this to 1.
    -- Spec: Phase 8 – Field-Level Arbitration § User-Locked Claims.
    is_user_locked INTEGER NOT NULL DEFAULT 0
                       CHECK (is_user_locked IN (0, 1)),
    is_current     INTEGER NOT NULL DEFAULT 1
                       CHECK (is_current IN (0, 1)),
    superseded_at  TEXT
);

CREATE TABLE IF NOT EXISTS metadata_providers (
    id         BLOB    NOT NULL PRIMARY KEY,  -- UUID
    name       TEXT    NOT NULL UNIQUE,
    version    TEXT    NOT NULL,
    is_enabled INTEGER NOT NULL DEFAULT 1     -- BOOLEAN: 1=true, 0=false
);

CREATE TABLE IF NOT EXISTS narrative_roots (
    qid         TEXT NOT NULL PRIMARY KEY,
    label       TEXT NOT NULL,
    level       TEXT NOT NULL
                    CHECK (level IN ('Universe', 'Franchise', 'Series', 'Standalone')),
    parent_qid  TEXT,
    created_at  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS offline_variants (
    id            BLOB NOT NULL PRIMARY KEY,
    asset_id      BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    owner_profile_id BLOB,
    owner_device_id  BLOB,
    profile_key   TEXT NOT NULL,
    source_hash   TEXT NOT NULL,
    display_name  TEXT NOT NULL,
    status        TEXT NOT NULL,
    output_path   TEXT,
    file_size     INTEGER,
    container     TEXT,
    video_codec   TEXT,
    audio_codec   TEXT,
    width         INTEGER,
    height        INTEGER,
    bitrate_kbps  INTEGER,
    created_at    TEXT NOT NULL,
    expires_at    TEXT
);

CREATE TABLE IF NOT EXISTS adaptive_hls_packages (
    id              BLOB NOT NULL PRIMARY KEY,
    asset_id        BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    source_hash     TEXT NOT NULL,
    profile_key     TEXT NOT NULL,
    status          TEXT NOT NULL CHECK(status IN ('preparing', 'ready', 'failed', 'deleting')),
    root_path       TEXT NOT NULL,
    total_bytes     INTEGER NOT NULL DEFAULT 0,
    created_at      TEXT NOT NULL,
    last_accessed   TEXT NOT NULL,
    completed_at    TEXT,
    last_error      TEXT,
    UNIQUE(asset_id, source_hash, profile_key)
);

CREATE TABLE IF NOT EXISTS pending_person_signals (
    id          BLOB NOT NULL PRIMARY KEY,
    entity_id   BLOB NOT NULL,
    name        TEXT NOT NULL,
    role        TEXT NOT NULL,
    source      TEXT NOT NULL,
    pattern     TEXT,
    media_type  TEXT NOT NULL,
    created_at  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS person_aliases (
    pseudonym_person_id BLOB NOT NULL REFERENCES persons(id) ON DELETE CASCADE,
    real_person_id      BLOB NOT NULL REFERENCES persons(id) ON DELETE CASCADE,
    PRIMARY KEY (pseudonym_person_id, real_person_id)
);

CREATE TABLE IF NOT EXISTS person_group_members (
    group_id    BLOB NOT NULL REFERENCES persons(id) ON DELETE CASCADE,
    member_id   BLOB NOT NULL REFERENCES persons(id) ON DELETE CASCADE,
    start_date  TEXT,   -- ISO-8601 date when member joined (from Wikidata P580 qualifier)
    end_date    TEXT,   -- ISO-8601 date when member left (from Wikidata P582 qualifier)
    PRIMARY KEY (group_id, member_id)
);

CREATE TABLE IF NOT EXISTS person_media_links (
    media_asset_id  BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    person_id       BLOB NOT NULL REFERENCES persons(id)       ON DELETE CASCADE,
    role            TEXT NOT NULL,
    PRIMARY KEY (media_asset_id, person_id, role)
);

CREATE TABLE IF NOT EXISTS person_roles (
    person_id BLOB NOT NULL REFERENCES persons(id) ON DELETE CASCADE,
    role      TEXT NOT NULL CHECK (role IN (
                  'Author','Narrator','Director',
                  'Actor','Voice Actor','Composer',
                  'Artist','Performer','Screenwriter')),
    PRIMARY KEY (person_id, role)
);

CREATE TABLE IF NOT EXISTS persons (
    id                BLOB    NOT NULL PRIMARY KEY,  -- UUID
    name              TEXT    NOT NULL,
    wikidata_qid      TEXT,                          -- e.g. Q42
    headshot_url      TEXT,                          -- Wikimedia Commons image URL
    biography         TEXT,                          -- Wikidata entity description
    occupation        TEXT,                          -- Wikidata P106 (e.g. "Writer", "Actor")
    instagram         TEXT,                          -- Wikidata P2003 (Instagram handle)
    twitter           TEXT,                          -- Wikidata P2002 (Twitter/X handle)
    tiktok            TEXT,                          -- Wikidata P7085 (TikTok handle)
    mastodon          TEXT,                          -- Wikidata P4033 (Mastodon address)
    website           TEXT,                          -- Wikidata P856 (Official website URL)
    local_headshot_path TEXT,                        -- Path to locally cached headshot
    date_of_birth     TEXT,                          -- Wikidata P569
    date_of_death     TEXT,                          -- Wikidata P570
    place_of_birth    TEXT,                          -- Wikidata P19
    place_of_death    TEXT,                          -- Wikidata P20
    nationality       TEXT,                          -- Wikidata P27
    display_overrides_json TEXT,                     -- durable user presentation overrides
    is_pseudonym      INTEGER NOT NULL DEFAULT 0,    -- 1 = pen name / stage name
    created_at        TEXT    NOT NULL,              -- ISO-8601
    enriched_at       TEXT                           -- NULL = not yet enriched
, is_group INTEGER NOT NULL DEFAULT 0);

CREATE TABLE IF NOT EXISTS playback_inspection_cache (
    asset_id       BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    source_hash    TEXT NOT NULL,
    inspected_at   TEXT NOT NULL,
    file_size      INTEGER,
    duration_secs  REAL,
    container      TEXT,
    metadata_json  TEXT,
    PRIMARY KEY (asset_id, source_hash)
);

-- Durable recurrence state for entity enrichment. Future refreshes are kept
-- separate from media_operations: an operation is created only when work is
-- actually queued, while this table remains the calendar/read model.
CREATE TABLE IF NOT EXISTS enrichment_refresh_schedule (
  entity_type       TEXT NOT NULL,
  entity_id         BLOB NOT NULL,
  stage             TEXT NOT NULL,
  provider_id       TEXT NOT NULL,
  policy_key        TEXT NOT NULL,
  interval_days     INTEGER NOT NULL CHECK (interval_days > 0),
  last_success_at   TEXT,
  last_attempt_at   TEXT,
  next_due_at       TEXT NOT NULL,
  status            TEXT NOT NULL DEFAULT 'Scheduled',
  failure_count     INTEGER NOT NULL DEFAULT 0,
  retry_after       TEXT,
  operation_id      BLOB,
  reason            TEXT,
  updated_at        TEXT NOT NULL,
  PRIMARY KEY (entity_type, entity_id, stage, provider_id)
);

CREATE TABLE IF NOT EXISTS player_sessions (
    profile_id            BLOB NOT NULL PRIMARY KEY REFERENCES profiles(id) ON DELETE CASCADE,
    session_id            BLOB NOT NULL,
    device_id             TEXT NOT NULL,
    client                TEXT NOT NULL,
    playback_state        TEXT NOT NULL DEFAULT 'stopped'
                          CHECK (playback_state IN ('stopped','playing','paused')),
    current_queue_item_id BLOB REFERENCES player_queue_items(id) ON DELETE SET NULL,
    position_seconds      REAL NOT NULL DEFAULT 0.0,
    duration_seconds      REAL,
    progress_pct          REAL NOT NULL DEFAULT 0.0,
    volume                REAL NOT NULL DEFAULT 0.8,
    is_muted              INTEGER NOT NULL DEFAULT 0 CHECK (is_muted IN (0, 1)),
    playback_rate         REAL NOT NULL DEFAULT 1.0,
    shuffle_enabled       INTEGER NOT NULL DEFAULT 0 CHECK (shuffle_enabled IN (0, 1)),
    repeat_mode           TEXT NOT NULL DEFAULT 'off'
                          CHECK (repeat_mode IN ('off','one','all')),
    source_label          TEXT,
    state_version         INTEGER NOT NULL DEFAULT 1,
    created_at            TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at            TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    last_heartbeat_at     TEXT
);

CREATE TABLE IF NOT EXISTS player_queue_items (
    id               BLOB NOT NULL PRIMARY KEY,
    profile_id       BLOB NOT NULL REFERENCES player_sessions(profile_id) ON DELETE CASCADE,
    position         INTEGER NOT NULL,
    work_id          BLOB NOT NULL REFERENCES works(id) ON DELETE CASCADE,
    asset_id         BLOB REFERENCES media_assets(id) ON DELETE SET NULL,
    collection_id    BLOB REFERENCES collections(id) ON DELETE SET NULL,
    media_type       TEXT NOT NULL,
    title            TEXT NOT NULL,
    subtitle         TEXT,
    album            TEXT,
    author           TEXT,
    artist           TEXT,
    narrator         TEXT,
    series           TEXT,
    year             TEXT,
    content_rating   TEXT,
    season_number    TEXT,
    episode_number   TEXT,
    episode_title    TEXT,
    quality          TEXT,
    cover_url        TEXT,
    duration_seconds REAL,
    stream_url       TEXT,
    download_url     TEXT,
    added_at         TEXT NOT NULL,
    source_label     TEXT
);

CREATE TABLE IF NOT EXISTS audiobook_listen_active_segments (
    profile_id             BLOB NOT NULL PRIMARY KEY REFERENCES profiles(id) ON DELETE CASCADE,
    work_id                BLOB NOT NULL REFERENCES works(id) ON DELETE CASCADE,
    asset_id               BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    queue_item_id          BLOB,
    title                  TEXT NOT NULL,
    chapter_title          TEXT,
    chapter_index          INTEGER,
    started_at             TEXT NOT NULL,
    started_position_seconds REAL NOT NULL DEFAULT 0.0,
    last_position_seconds  REAL NOT NULL DEFAULT 0.0,
    duration_seconds       REAL,
    device_id              TEXT NOT NULL,
    client                 TEXT NOT NULL,
    last_heartbeat_at      TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS audiobook_listen_history (
    id                     BLOB NOT NULL PRIMARY KEY,
    profile_id             BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    work_id                BLOB NOT NULL REFERENCES works(id) ON DELETE CASCADE,
    asset_id               BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    title                  TEXT NOT NULL,
    chapter_title          TEXT,
    chapter_index          INTEGER,
    position_seconds       REAL NOT NULL DEFAULT 0.0,
    duration_seconds       REAL,
    progress_pct           REAL NOT NULL DEFAULT 0.0,
    device_id              TEXT NOT NULL,
    client                 TEXT NOT NULL,
    started_at             TEXT NOT NULL,
    ended_at               TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS audiobook_bookmarks (
    id                     BLOB NOT NULL PRIMARY KEY,
    profile_id             BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    work_id                BLOB NOT NULL REFERENCES works(id) ON DELETE CASCADE,
    asset_id               BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    chapter_index          INTEGER,
    chapter_title          TEXT,
    position_seconds       REAL NOT NULL DEFAULT 0.0,
    duration_seconds       REAL,
    label                  TEXT,
    note                   TEXT,
    created_at             TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS music_play_active_segments (
    profile_id             BLOB NOT NULL PRIMARY KEY REFERENCES profiles(id) ON DELETE CASCADE,
    work_id                BLOB NOT NULL REFERENCES works(id) ON DELETE CASCADE,
    asset_id               BLOB REFERENCES media_assets(id) ON DELETE SET NULL,
    queue_item_id          BLOB,
    started_at             TEXT NOT NULL,
    last_position_seconds  REAL NOT NULL DEFAULT 0.0,
    listened_seconds       REAL NOT NULL DEFAULT 0.0,
    duration_seconds       REAL,
    qualified              INTEGER NOT NULL DEFAULT 0 CHECK (qualified IN (0, 1)),
    last_heartbeat_at      TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS music_play_stats (
    profile_id             BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    work_id                BLOB NOT NULL REFERENCES works(id) ON DELETE CASCADE,
    play_count             INTEGER NOT NULL DEFAULT 0,
    last_played_at         TEXT NOT NULL,
    PRIMARY KEY (profile_id, work_id)
);

CREATE TABLE IF NOT EXISTS audiobook_chapter_title_overrides (
    work_id                BLOB NOT NULL REFERENCES works(id) ON DELETE CASCADE,
    asset_id               BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    chapter_index          INTEGER NOT NULL,
    title                  TEXT NOT NULL,
    title_source           TEXT NOT NULL,
    updated_at             TEXT NOT NULL,
    PRIMARY KEY (asset_id, chapter_index)
);

CREATE TABLE IF NOT EXISTS playback_segments (
    id            BLOB NOT NULL PRIMARY KEY,
    asset_id      BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    kind          TEXT NOT NULL,
    start_seconds REAL NOT NULL,
    end_seconds   REAL,
    confidence    REAL NOT NULL DEFAULT 0.0,
    source        TEXT NOT NULL,
    plugin_id     TEXT,
    is_skippable  INTEGER NOT NULL DEFAULT 1,
    review_status TEXT NOT NULL DEFAULT 'detected',
    created_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS households (
    id         BLOB NOT NULL PRIMARY KEY,
    name       TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS profiles (
    id           BLOB NOT NULL PRIMARY KEY,  -- UUID
    display_name TEXT NOT NULL,
    avatar_color TEXT NOT NULL DEFAULT '#7C4DFF',
    avatar_image_path TEXT,
    role         TEXT NOT NULL DEFAULT 'RestrictedProfile'
                     CHECK (role IN ('Administrator', 'StandardUser', 'RestrictedProfile')),
    created_at   TEXT NOT NULL
, navigation_config TEXT
, household_id BLOB REFERENCES households(id));

CREATE TABLE IF NOT EXISTS accounts (
    id               BLOB NOT NULL PRIMARY KEY,
    email            TEXT NOT NULL,
    normalized_email TEXT NOT NULL,
    is_enabled       INTEGER NOT NULL DEFAULT 1 CHECK (is_enabled IN (0, 1)),
    is_administrator INTEGER NOT NULL DEFAULT 0 CHECK (is_administrator IN (0, 1)),
    authorization_version INTEGER NOT NULL DEFAULT 1 CHECK (authorization_version > 0),
    created_at       TEXT NOT NULL,
    updated_at       TEXT NOT NULL,
    household_id     BLOB REFERENCES households(id),
    this_computer_only INTEGER NOT NULL DEFAULT 0 CHECK (this_computer_only IN (0, 1)),
    must_change_password INTEGER NOT NULL DEFAULT 0 CHECK (must_change_password IN (0, 1)),
    temporary_password_expires_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_accounts_normalized_email
    ON accounts(normalized_email);

CREATE TABLE IF NOT EXISTS account_profile_grants (
    account_id BLOB NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    profile_id BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    is_default INTEGER NOT NULL DEFAULT 0 CHECK (is_default IN (0, 1)),
    is_enabled INTEGER NOT NULL DEFAULT 1 CHECK (is_enabled IN (0, 1)),
    admin_enabled INTEGER NOT NULL DEFAULT 0 CHECK (admin_enabled IN (0, 1)),
    authorization_version INTEGER NOT NULL DEFAULT 1 CHECK (authorization_version > 0),
    granted_at TEXT NOT NULL,
    PRIMARY KEY (account_id, profile_id)
);

CREATE TABLE IF NOT EXISTS account_invitations (
    id          BLOB NOT NULL PRIMARY KEY,
    account_id  BLOB NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    token_hash  TEXT NOT NULL UNIQUE,
    created_at  TEXT NOT NULL,
    expires_at  TEXT NOT NULL,
    consumed_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_account_profile_default
    ON account_profile_grants(account_id) WHERE is_default = 1;

CREATE TABLE IF NOT EXISTS account_feature_grants (
    account_id BLOB NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    feature_id TEXT NOT NULL CHECK (feature_id IN ('read','watch','listen','view')),
    granted_at TEXT NOT NULL,
    PRIMARY KEY (account_id, feature_id)
);

CREATE TRIGGER IF NOT EXISTS trg_view_sources_scope_insert BEFORE INSERT ON view_sources BEGIN
    SELECT CASE WHEN NEW.scope_kind='personal' AND NOT EXISTS (SELECT 1 FROM view_personal_spaces s WHERE s.id=NEW.personal_space_id AND s.library_id=NEW.library_id) THEN RAISE(ABORT,'personal View source must match its Personal Space library') WHEN NEW.scope_kind='shared' AND NOT EXISTS (SELECT 1 FROM view_shared_library s WHERE s.singleton_key=1 AND s.library_id=NEW.library_id) THEN RAISE(ABORT,'Shared View source must match the Shared library') END;
END;
CREATE TRIGGER IF NOT EXISTS trg_view_sources_scope_update BEFORE UPDATE OF scope_kind,personal_space_id,library_id ON view_sources BEGIN
    SELECT CASE WHEN NEW.scope_kind='personal' AND NOT EXISTS (SELECT 1 FROM view_personal_spaces s WHERE s.id=NEW.personal_space_id AND s.library_id=NEW.library_id) THEN RAISE(ABORT,'personal View source must match its Personal Space library') WHEN NEW.scope_kind='shared' AND NOT EXISTS (SELECT 1 FROM view_shared_library s WHERE s.singleton_key=1 AND s.library_id=NEW.library_id) THEN RAISE(ABORT,'Shared View source must match the Shared library') END;
END;
CREATE TRIGGER IF NOT EXISTS trg_local_items_scope_insert BEFORE INSERT ON local_items BEGIN
    SELECT CASE WHEN NEW.scope_kind='personal' AND NOT EXISTS (SELECT 1 FROM view_personal_spaces s WHERE s.id=NEW.personal_space_id AND s.library_id=NEW.library_id AND s.owner_profile_id=NEW.owner_profile_id) THEN RAISE(ABORT,'personal View item must match its owner and Personal Space library') WHEN NEW.scope_kind='shared' AND NOT EXISTS (SELECT 1 FROM view_shared_library s WHERE s.singleton_key=1 AND s.library_id=NEW.library_id) THEN RAISE(ABORT,'Shared View item must match the Shared library') END;
END;
CREATE TRIGGER IF NOT EXISTS trg_local_items_scope_update BEFORE UPDATE OF scope_kind,personal_space_id,owner_profile_id,library_id ON local_items BEGIN
    SELECT CASE WHEN NEW.scope_kind='personal' AND NOT EXISTS (SELECT 1 FROM view_personal_spaces s WHERE s.id=NEW.personal_space_id AND s.library_id=NEW.library_id AND s.owner_profile_id=NEW.owner_profile_id) THEN RAISE(ABORT,'personal View item must match its owner and Personal Space library') WHEN NEW.scope_kind='shared' AND NOT EXISTS (SELECT 1 FROM view_shared_library s WHERE s.singleton_key=1 AND s.library_id=NEW.library_id) THEN RAISE(ABORT,'Shared View item must match the Shared library') END;
END;
CREATE UNIQUE INDEX IF NOT EXISTS ux_view_sources_personal_key ON view_sources(personal_space_id, source_key)
    WHERE scope_kind = 'personal' AND source_key IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_view_sources_shared_key ON view_sources(library_id, source_key)
    WHERE scope_kind = 'shared' AND source_key IS NOT NULL;

CREATE TABLE IF NOT EXISTS account_library_grants (
    account_id BLOB NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    library_id BLOB NOT NULL,
    granted_at TEXT NOT NULL,
    PRIMARY KEY (account_id, library_id)
);

CREATE TABLE IF NOT EXISTS grant_admin_protections (
    account_id BLOB NOT NULL,
    profile_id BLOB NOT NULL,
    is_enabled INTEGER NOT NULL DEFAULT 0 CHECK (is_enabled IN (0, 1)),
    unlock_mode TEXT NOT NULL DEFAULT 'FixedDuration'
        CHECK (unlock_mode IN ('FixedDuration','UntilProfileSwitch','LockOnLeave')),
    unlock_minutes INTEGER,
    pin_hash TEXT,
    hash_scheme TEXT,
    failed_attempt_count INTEGER NOT NULL DEFAULT 0,
    locked_until TEXT,
    protection_version INTEGER NOT NULL DEFAULT 1 CHECK (protection_version > 0),
    updated_at TEXT NOT NULL,
    PRIMARY KEY (account_id, profile_id),
    FOREIGN KEY (account_id, profile_id) REFERENCES account_profile_grants(account_id, profile_id) ON DELETE CASCADE,
    CHECK ((is_enabled = 0 AND pin_hash IS NULL AND hash_scheme IS NULL)
        OR (is_enabled = 1 AND pin_hash IS NOT NULL AND hash_scheme IS NOT NULL))
);

CREATE TABLE IF NOT EXISTS account_external_logins (
    id            BLOB NOT NULL PRIMARY KEY,
    account_id    BLOB NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    provider      TEXT NOT NULL,
    issuer        TEXT NOT NULL,
    subject       TEXT NOT NULL,
    email         TEXT,
    display_name  TEXT,
    linked_at     TEXT NOT NULL,
    last_login_at TEXT,
    UNIQUE(provider, issuer, subject)
);

-- Versioned first-party identity and access storage. Human credentials are
-- salted, work-factored payloads produced by ASP.NET Core PasswordHasher.
CREATE TABLE IF NOT EXISTS schema_migrations (
    migration_id TEXT NOT NULL PRIMARY KEY,
    applied_at   TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS account_credentials (
    id                   BLOB NOT NULL PRIMARY KEY,
    account_id           BLOB NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    credential_kind      TEXT NOT NULL CHECK (credential_kind = 'Password'),
    secret_hash          TEXT NOT NULL,
    hash_scheme          TEXT NOT NULL,
    hash_version         INTEGER NOT NULL,
    security_stamp       TEXT NOT NULL,
    failed_attempt_count INTEGER NOT NULL DEFAULT 0,
    locked_until         TEXT,
    created_at           TEXT NOT NULL,
    updated_at           TEXT NOT NULL,
    last_used_at         TEXT,
    UNIQUE(account_id, credential_kind)
);

CREATE TABLE IF NOT EXISTS profile_credentials (
    id                   BLOB NOT NULL PRIMARY KEY,
    profile_id           BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    credential_kind      TEXT NOT NULL CHECK (credential_kind = 'ProfilePin'),
    secret_hash          TEXT NOT NULL,
    hash_scheme          TEXT NOT NULL,
    hash_version         INTEGER NOT NULL,
    security_stamp       TEXT NOT NULL,
    failed_attempt_count INTEGER NOT NULL DEFAULT 0,
    locked_until         TEXT,
    created_at           TEXT NOT NULL,
    updated_at           TEXT NOT NULL,
    last_used_at         TEXT,
    UNIQUE(profile_id, credential_kind)
);

CREATE TABLE IF NOT EXISTS auth_sessions (
    id                    BLOB NOT NULL PRIMARY KEY,
    account_id            BLOB NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    active_profile_id     BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    token_hash            TEXT NOT NULL UNIQUE,
    device_id             TEXT NOT NULL,
    device_name           TEXT NOT NULL,
    client                TEXT NOT NULL,
    authentication_method TEXT NOT NULL,
    issued_ingress        TEXT NOT NULL DEFAULT 'home_network',
    security_stamp        TEXT NOT NULL,
    created_at            TEXT NOT NULL,
    last_seen_at          TEXT NOT NULL,
    expires_at            TEXT NOT NULL,
    authenticated_at      TEXT,
    revoked_at            TEXT,
    revoked_reason        TEXT,
    profile_pending       INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_auth_sessions_account_active
    ON auth_sessions(account_id, revoked_at, expires_at);

-- "Always open as this person on this device": one row per signed-in account and browser/device.
CREATE TABLE IF NOT EXISTS device_profile_preferences (
    account_id BLOB NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    device_id  TEXT NOT NULL,
    profile_id BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (account_id, device_id)
);

CREATE TABLE IF NOT EXISTS grant_admin_unlocks (
    session_id BLOB NOT NULL PRIMARY KEY REFERENCES auth_sessions(id) ON DELETE CASCADE,
    account_id BLOB NOT NULL,
    profile_id BLOB NOT NULL,
    protection_version INTEGER NOT NULL,
    method TEXT NOT NULL,
    granted_at TEXT NOT NULL,
    expires_at TEXT,
    FOREIGN KEY (account_id, profile_id) REFERENCES account_profile_grants(account_id, profile_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS applications (
    id BLOB NOT NULL PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT,
    application_type TEXT NOT NULL CHECK (application_type IN ('UserClient','ServerIntegration','Automation','Other')),
    is_enabled INTEGER NOT NULL DEFAULT 1 CHECK (is_enabled IN (0, 1)),
    is_administrator INTEGER NOT NULL DEFAULT 0 CHECK (is_administrator IN (0, 1)),
    authorization_version INTEGER NOT NULL DEFAULT 1 CHECK (authorization_version > 0),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    last_used_at TEXT
);

CREATE TABLE IF NOT EXISTS application_credentials (
    id BLOB NOT NULL PRIMARY KEY,
    application_id BLOB NOT NULL REFERENCES applications(id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    credential_hash TEXT NOT NULL UNIQUE,
    hash_scheme TEXT NOT NULL,
    created_at TEXT NOT NULL,
    expires_at TEXT,
    last_used_at TEXT,
    revoked_at TEXT
);

CREATE TABLE IF NOT EXISTS application_permission_grants (
    application_id BLOB NOT NULL REFERENCES applications(id) ON DELETE CASCADE,
    permission_id TEXT NOT NULL,
    granted_at TEXT NOT NULL,
    PRIMARY KEY (application_id, permission_id)
);
CREATE TABLE IF NOT EXISTS application_client_bindings (
    application_id BLOB NOT NULL REFERENCES applications(id) ON DELETE CASCADE,
    client_id TEXT NOT NULL PRIMARY KEY COLLATE BINARY,
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS account_passkeys (
    credential_id BLOB NOT NULL PRIMARY KEY,
    account_id    BLOB NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    name          TEXT NOT NULL,
    data_json     TEXT NOT NULL,
    created_at    TEXT NOT NULL,
    last_used_at  TEXT
);

CREATE TABLE IF NOT EXISTS password_reset_challenges (
    id         BLOB NOT NULL PRIMARY KEY,
    account_id BLOB NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    token_hash TEXT NOT NULL UNIQUE,
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    consumed_at TEXT
);

-- Server-issued identities for Dashboard, television, mobile and future
-- clients. Request fields and browser storage are never identity authorities.
CREATE TABLE IF NOT EXISTS client_devices (
    id                BLOB NOT NULL PRIMARY KEY,
    application_id    BLOB NOT NULL REFERENCES applications(id) ON DELETE RESTRICT,
    account_id        BLOB NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    profile_id        BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    device_name       TEXT NOT NULL,
    device_class      TEXT NOT NULL CHECK (device_class IN ('web','mobile','television','automotive')),
    client_id         TEXT NOT NULL,
    client_name       TEXT NOT NULL,
    client_version    TEXT NOT NULL,
    scopes            TEXT NOT NULL,
    capabilities_json TEXT NOT NULL DEFAULT '{}',
    created_at        TEXT NOT NULL,
    last_seen_at      TEXT NOT NULL,
    revoked_at        TEXT,
    revoked_reason    TEXT
);
CREATE INDEX IF NOT EXISTS idx_client_devices_profile_active
    ON client_devices(profile_id, revoked_at, last_seen_at DESC);

CREATE TABLE IF NOT EXISTS device_pairing_requests (
    id                     BLOB NOT NULL PRIMARY KEY,
    application_id         BLOB NOT NULL REFERENCES applications(id) ON DELETE CASCADE,
    device_code_hash       TEXT NOT NULL UNIQUE,
    user_code_hash         TEXT NOT NULL UNIQUE,
    client_id              TEXT NOT NULL,
    client_name            TEXT NOT NULL,
    client_version         TEXT NOT NULL,
    device_name            TEXT NOT NULL,
    device_class           TEXT NOT NULL CHECK (device_class IN ('web','mobile','television','automotive')),
    requested_scopes       TEXT NOT NULL,
    approved_scopes        TEXT NOT NULL DEFAULT '',
    capabilities_json      TEXT NOT NULL DEFAULT '{}',
    status                 TEXT NOT NULL DEFAULT 'pending'
                           CHECK (status IN ('pending','approved','denied','consumed','expired')),
    poll_interval_seconds  INTEGER NOT NULL DEFAULT 5,
    last_polled_at         TEXT,
    profile_id             BLOB REFERENCES profiles(id) ON DELETE CASCADE,
    account_id             BLOB REFERENCES accounts(id) ON DELETE CASCADE,
    approved_by_profile_id BLOB REFERENCES profiles(id) ON DELETE SET NULL,
    created_at             TEXT NOT NULL,
    expires_at             TEXT NOT NULL,
    decided_at             TEXT,
    consumed_at            TEXT
);
CREATE INDEX IF NOT EXISTS idx_device_pairing_expiry
    ON device_pairing_requests(status, expires_at);

CREATE TABLE IF NOT EXISTS client_tokens (
    id              BLOB NOT NULL PRIMARY KEY,
    application_id  BLOB NOT NULL REFERENCES applications(id) ON DELETE RESTRICT,
    account_id      BLOB NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    device_id       BLOB NOT NULL REFERENCES client_devices(id) ON DELETE CASCADE,
    profile_id      BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    token_family_id BLOB NOT NULL,
    token_kind      TEXT NOT NULL CHECK (token_kind IN ('access','refresh')),
    token_hash      TEXT NOT NULL UNIQUE,
    scopes          TEXT NOT NULL,
    account_authorization_version INTEGER NOT NULL,
    grant_authorization_version INTEGER NOT NULL,
    application_authorization_version INTEGER NOT NULL,
    generation      INTEGER NOT NULL DEFAULT 0,
    created_at      TEXT NOT NULL,
    expires_at      TEXT NOT NULL,
    consumed_at     TEXT,
    revoked_at      TEXT,
    revoked_reason  TEXT
);
CREATE INDEX IF NOT EXISTS idx_client_tokens_family_active
    ON client_tokens(token_family_id, token_kind, revoked_at, expires_at);
CREATE INDEX IF NOT EXISTS idx_client_tokens_device_active
    ON client_tokens(device_id, revoked_at, expires_at);

CREATE TABLE IF NOT EXISTS password_recovery_codes (
    id          BLOB NOT NULL PRIMARY KEY,
    account_id  BLOB NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    code_hash   TEXT NOT NULL,
    created_at  TEXT NOT NULL,
    expires_at  TEXT NOT NULL,
    consumed_at TEXT,
    UNIQUE(account_id, code_hash)
);

CREATE TABLE IF NOT EXISTS service_credentials (
    id           BLOB NOT NULL PRIMARY KEY,
    purpose      TEXT NOT NULL,
    key_id       TEXT NOT NULL UNIQUE,
    token_hash   TEXT NOT NULL UNIQUE,
    created_at   TEXT NOT NULL,
    last_used_at TEXT,
    revoked_at   TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_service_credentials_active_purpose
    ON service_credentials(purpose)
    WHERE revoked_at IS NULL;

CREATE TABLE IF NOT EXISTS identity_audit_events (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id  BLOB REFERENCES accounts(id) ON DELETE SET NULL,
    profile_id  BLOB REFERENCES profiles(id) ON DELETE SET NULL,
    session_id  BLOB,
    event_type  TEXT NOT NULL,
    succeeded   INTEGER NOT NULL CHECK (succeeded IN (0, 1)),
    detail      TEXT,
    occurred_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_identity_audit_profile_occurred
    ON identity_audit_events(profile_id, occurred_at DESC);

CREATE TABLE IF NOT EXISTS authorization_audit_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    event_type TEXT NOT NULL,
    occurred_at TEXT NOT NULL,
    actor_account_id BLOB REFERENCES accounts(id) ON DELETE SET NULL,
    actor_profile_id BLOB REFERENCES profiles(id) ON DELETE SET NULL,
    actor_application_id BLOB REFERENCES applications(id) ON DELETE SET NULL,
    subject_type TEXT NOT NULL,
    subject_id TEXT NOT NULL,
    changes_json TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_authorization_audit_subject
    ON authorization_audit_events(subject_type, subject_id, occurred_at DESC);

-- Administrator-owned View capability policy. Viewing, submitting to, and
-- reviewing the Shared Library are deliberately independent permissions.
CREATE TABLE IF NOT EXISTS profile_view_policies (
    profile_id              BLOB NOT NULL PRIMARY KEY REFERENCES profiles(id) ON DELETE CASCADE,
    view_enabled            INTEGER NOT NULL DEFAULT 1 CHECK (view_enabled IN (0, 1)),
    access_shared_library   INTEGER NOT NULL DEFAULT 0 CHECK (access_shared_library IN (0, 1)),
    submit_to_shared_library INTEGER NOT NULL DEFAULT 0 CHECK (submit_to_shared_library IN (0, 1)),
    review_shared_library_contributions INTEGER NOT NULL DEFAULT 0 CHECK (review_shared_library_contributions IN (0, 1)),
    share_galleries         INTEGER NOT NULL DEFAULT 0 CHECK (share_galleries IN (0, 1)),
    updated_at              TEXT NOT NULL
);

-- User-owned presentation preferences travel with the profile across devices.
-- Scope authorization and revoked-scope fallback are resolved above storage.
CREATE TABLE IF NOT EXISTS profile_view_preferences (
    profile_id              BLOB NOT NULL PRIMARY KEY REFERENCES profiles(id) ON DELETE CASCADE,
    last_scope_kind         TEXT CHECK (last_scope_kind IS NULL OR last_scope_kind IN ('shared', 'mine', 'profile')),
    last_scope_profile_id   BLOB REFERENCES profiles(id) ON DELETE SET NULL,
    timeline_density        TEXT NOT NULL DEFAULT 'comfortable'
                                 CHECK (timeline_density IN ('compact', 'comfortable', 'relaxed')),
    viewer_info_open        INTEGER NOT NULL DEFAULT 1 CHECK (viewer_info_open IN (0, 1)),
    updated_at              TEXT NOT NULL,
    CHECK ((last_scope_kind = 'profile' AND last_scope_profile_id IS NOT NULL)
        OR (last_scope_kind IS NULL OR last_scope_kind IN ('shared', 'mine'))
           AND last_scope_profile_id IS NULL)
);

CREATE TABLE IF NOT EXISTS provider_config (
    provider_id BLOB    NOT NULL REFERENCES metadata_providers(id) ON DELETE CASCADE,
    key         TEXT    NOT NULL,
    value       TEXT    NOT NULL,
    is_secret   INTEGER NOT NULL DEFAULT 0,   -- BOOLEAN: marks credentials
    PRIMARY KEY (provider_id, key)
);

CREATE TABLE IF NOT EXISTS provider_health (
    provider_id          TEXT NOT NULL PRIMARY KEY,
    status               TEXT NOT NULL DEFAULT 'Healthy',
    consecutive_failures INTEGER NOT NULL DEFAULT 0,
    last_check_at        TEXT,
    last_success_at      TEXT,
    last_failure_at      TEXT,
    last_failure_reason  TEXT,
    next_check_at        TEXT,
    down_since           TEXT
);

-- Last read-only connection result for the configured provider. Never stores a key.
CREATE TABLE IF NOT EXISTS provider_connection_checks (
    provider_name    TEXT NOT NULL PRIMARY KEY,
    status           TEXT NOT NULL,
    message          TEXT NOT NULL,
    checked_at       TEXT NOT NULL,
    response_time_ms INTEGER
);

CREATE TABLE IF NOT EXISTS provider_response_cache (
    cache_key     TEXT NOT NULL PRIMARY KEY,
    provider_id   TEXT NOT NULL,
    query_hash    TEXT NOT NULL,
    response_json TEXT NOT NULL,
    etag          TEXT,
    fetched_at    TEXT NOT NULL,
    expires_at    TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS qid_labels (
    qid         TEXT NOT NULL PRIMARY KEY,
    label       TEXT NOT NULL,
    description TEXT,
    entity_type TEXT,
    fetched_at  TEXT NOT NULL,
    updated_at  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS reader_bookmarks (
    id             BLOB NOT NULL PRIMARY KEY,
    user_id        TEXT NOT NULL,
    asset_id       BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    chapter_index  INTEGER NOT NULL,
    cfi_position   TEXT,
    label          TEXT,
    created_at     TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS reader_highlights (
    id             BLOB NOT NULL PRIMARY KEY,
    user_id        TEXT NOT NULL,
    asset_id       BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    chapter_index  INTEGER NOT NULL,
    start_offset   INTEGER NOT NULL,
    end_offset     INTEGER NOT NULL,
    selected_text  TEXT NOT NULL,
    color          TEXT NOT NULL DEFAULT '#EAB308',
    note_text      TEXT,
    created_at     TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS reader_statistics (
    id                      BLOB NOT NULL PRIMARY KEY,
    user_id                 TEXT NOT NULL,
    asset_id                BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    chapters_read           INTEGER NOT NULL DEFAULT 0,
    total_reading_time_secs INTEGER NOT NULL DEFAULT 0,
    words_read              INTEGER NOT NULL DEFAULT 0,
    sessions_count          INTEGER NOT NULL DEFAULT 0,
    avg_words_per_minute    REAL NOT NULL DEFAULT 0.0,
    last_session_at         TEXT,
    UNIQUE (user_id, asset_id)
);

CREATE TABLE IF NOT EXISTS resolver_cache (
    cache_key        TEXT NOT NULL PRIMARY KEY,
    normalized_title TEXT NOT NULL,
    media_type       TEXT NOT NULL,
    wikidata_qid     TEXT,
    confidence       REAL,
    entity_label     TEXT,
    created_at       TEXT NOT NULL,
    expires_at       TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS retail_match_candidates (
    id                    BLOB PRIMARY KEY,
    job_id                BLOB NOT NULL REFERENCES identity_jobs(id) ON DELETE CASCADE,
    provider_id           BLOB NOT NULL,
    provider_name         TEXT NOT NULL,
    provider_item_id      TEXT,
    rank                  INTEGER NOT NULL DEFAULT 0,
    title                 TEXT NOT NULL,
    creator               TEXT,
    year                  TEXT,
    score_total           REAL NOT NULL DEFAULT 0.0,
    score_breakdown_json  TEXT,
    bridge_ids_json       TEXT,
    description           TEXT,
    image_url             TEXT,
    outcome               TEXT NOT NULL DEFAULT 'Rejected',
    created_at            TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS review_queue (
    id               BLOB NOT NULL PRIMARY KEY,
    entity_id        BLOB NOT NULL,
    entity_type      TEXT NOT NULL,
    trigger          TEXT NOT NULL,
    status           TEXT NOT NULL DEFAULT 'Pending'
                         CHECK (status IN ('Pending', 'Resolved', 'Dismissed')),
    proposed_collection_id  TEXT,
    confidence_score REAL,
    candidates_json  TEXT,
    detail           TEXT,
    created_at       TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    resolved_at      TEXT,
    resolved_by      TEXT
, source_operation_id BLOB, source_capability_id TEXT, source_capability_sub_key TEXT, review_ready_at TEXT, automation_completed_at TEXT);

CREATE VIRTUAL TABLE IF NOT EXISTS search_index USING fts5(
    entity_id UNINDEXED,
    title,
    original_title,
    alternate_titles,
    author,
    description,
    tokenize = 'trigram'
);

CREATE TABLE IF NOT EXISTS search_results_cache (
    entity_id    BLOB NOT NULL PRIMARY KEY,
    results_json TEXT NOT NULL,
    searched_at  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS series_manifest_hydrations (
    series_qid            TEXT PRIMARY KEY,
    collection_id         BLOB NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
    series_label          TEXT,
    manifest_source       TEXT NOT NULL DEFAULT 'Tuvima.Wikidata',
    manifest_version      TEXT,
    manifest_hash         TEXT,
    known_item_qids_hash  TEXT,
    warnings_json         TEXT NOT NULL DEFAULT '[]',
    api_metadata_json     TEXT NOT NULL DEFAULT '{}',
    last_hydrated_at      TEXT NOT NULL,
    created_at            TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at            TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS series_manifest_items (
    id                          BLOB PRIMARY KEY,
    collection_id               BLOB NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
    series_qid                  TEXT NOT NULL,
    item_qid                    TEXT NOT NULL,
    item_label                  TEXT,
    item_description            TEXT,
    media_type                  TEXT,
    media_kind                  TEXT,
    instance_of_qids_json       TEXT NOT NULL DEFAULT '[]',
    raw_ordinal                 TEXT,
    parsed_ordinal              REAL,
    ordinal_scope_qid           TEXT,
    sort_order                  REAL,
    publication_date            TEXT,
    duration                    TEXT,
    previous_qid                TEXT,
    next_qid                    TEXT,
    parent_collection_qid       TEXT,
    parent_collection_label     TEXT,
    is_collection               INTEGER NOT NULL DEFAULT 0,
    is_expanded_from_collection INTEGER NOT NULL DEFAULT 0,
    membership_scope            TEXT NOT NULL DEFAULT 'MainSequence'
        CHECK (membership_scope IN ('MainSequence','Supplementary','CollectedContent','BroaderContext','Unpositioned')),
    source_properties_json      TEXT NOT NULL DEFAULT '[]',
    relationships_json          TEXT NOT NULL DEFAULT '[]',
    order_source                TEXT NOT NULL,
    ownership_state             TEXT NOT NULL DEFAULT 'Missing'
        CHECK (ownership_state IN ('Owned','Missing','Provisional','Ambiguous')),
    linked_work_id              BLOB REFERENCES works(id) ON DELETE SET NULL,
    last_hydrated_at            TEXT NOT NULL,
    created_at                  TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at                  TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(collection_id, item_qid)
);

CREATE TABLE IF NOT EXISTS series_members (
    series_qid TEXT NOT NULL,
    work_qid   TEXT NOT NULL,
    work_label TEXT,
    position   TEXT,
    owned      INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    PRIMARY KEY (series_qid, work_qid)
);

CREATE TABLE IF NOT EXISTS storage_metadata (
    key   TEXT NOT NULL PRIMARY KEY,
    value TEXT NOT NULL
);

INSERT OR REPLACE INTO storage_metadata (key, value)
VALUES ('storage_epoch', 'guid-blob-v10-graph-timeline-lore');

-- Seed the built-in native-client Application once for a new access epoch. The
-- marker preserves later administrative disable, delete, permission, and binding edits.
INSERT OR IGNORE INTO applications
    (id,name,description,application_type,is_enabled,is_administrator,authorization_version,created_at,updated_at)
SELECT X'00000000000000000000000000000004','Tuvima native clients',
    'Built-in delegated identity for paired native clients.','UserClient',1,0,1,
    strftime('%Y-%m-%dT%H:%M:%fZ','now'),strftime('%Y-%m-%dT%H:%M:%fZ','now')
WHERE NOT EXISTS (SELECT 1 FROM storage_metadata WHERE key='access_native_seeded');

WITH pending AS (
    SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM storage_metadata WHERE key='access_native_seeded')
), permissions(permission_id) AS (VALUES
    ('library.read'),('artwork.read'),('progress.read'),('progress.write'),
    ('queue.read'),('queue.write'),('playback.read'),('playback.write'),
    ('downloads.read'),('downloads.write'))
INSERT OR IGNORE INTO application_permission_grants(application_id,permission_id,granted_at)
SELECT X'00000000000000000000000000000004',permission_id,strftime('%Y-%m-%dT%H:%M:%fZ','now')
FROM pending CROSS JOIN permissions;

WITH pending AS (
    SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM storage_metadata WHERE key='access_native_seeded')
), clients(client_id) AS (VALUES
    ('tuvima-tv'),('tuvima-mobile'),('tuvima-automotive'),('tuvima-web'))
INSERT OR IGNORE INTO application_client_bindings(application_id,client_id,created_at)
SELECT X'00000000000000000000000000000004',client_id,strftime('%Y-%m-%dT%H:%M:%fZ','now')
FROM pending CROSS JOIN clients;

INSERT OR IGNORE INTO storage_metadata(key,value) VALUES ('access_native_seeded','1');

CREATE TABLE IF NOT EXISTS system_activity (
    id           INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    occurred_at  TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    action_type  TEXT    NOT NULL,
    collection_name     TEXT,
    entity_id    BLOB,
    entity_type  TEXT,
    profile_id   BLOB,
    changes_json TEXT,
    detail       TEXT,
    ingestion_run_id BLOB
);

CREATE TABLE IF NOT EXISTS text_tracks (
    id                  BLOB PRIMARY KEY,
    asset_id            BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    kind                TEXT NOT NULL CHECK(kind IN ('Lyrics','Subtitles')),
    language            TEXT NOT NULL DEFAULT 'und',
    provider            TEXT NOT NULL,
    confidence          REAL NOT NULL DEFAULT 0,
    source_id           TEXT,
    source_url          TEXT,
    source_format       TEXT NOT NULL,
    normalized_format   TEXT NOT NULL,
    local_path          TEXT NOT NULL,
    sidecar_path        TEXT,
    timing_mode         TEXT NOT NULL DEFAULT 'Line',
    duration_match_score REAL,
    is_hearing_impaired INTEGER NOT NULL DEFAULT 0,
    is_preferred        INTEGER NOT NULL DEFAULT 0,
    is_user_owned       INTEGER NOT NULL DEFAULT 0,
    created_at          TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at          TEXT
);

CREATE TABLE IF NOT EXISTS transaction_log (
    id          INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    event_type  TEXT    NOT NULL,
    entity_type TEXT    NOT NULL,
    entity_id   BLOB    NOT NULL,  -- UUID of affected entity
    timestamp   TEXT    NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS ui_settings_cache (
    scope       TEXT NOT NULL PRIMARY KEY,
    settings    TEXT NOT NULL,
    cached_at   TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE TABLE IF NOT EXISTS user_playback_settings (
    profile_id    BLOB NOT NULL PRIMARY KEY REFERENCES profiles(id) ON DELETE CASCADE,
    settings_json TEXT NOT NULL,
    updated_at    TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS profile_work_preferences (
    profile_id                 BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    work_id                    BLOB NOT NULL REFERENCES works(id) ON DELETE CASCADE,
    local_tags_json            TEXT,
    revision                   INTEGER NOT NULL DEFAULT 0,
    updated_at                 TEXT NOT NULL,
    PRIMARY KEY (profile_id, work_id)
);

CREATE INDEX IF NOT EXISTS idx_profile_work_preferences_work
    ON profile_work_preferences(work_id, profile_id);

CREATE TABLE IF NOT EXISTS profile_person_preferences (
    profile_id       BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    person_id        BLOB NOT NULL REFERENCES persons(id) ON DELETE CASCADE,
    local_tags_json  TEXT,
    revision         INTEGER NOT NULL DEFAULT 0,
    updated_at       TEXT NOT NULL,
    PRIMARY KEY (profile_id, person_id)
);

CREATE INDEX IF NOT EXISTS idx_profile_person_preferences_person
    ON profile_person_preferences(person_id, profile_id);

-- Explicit profile-and-series overrides only. Media-level defaults remain in
-- config/ui/library-preferences.json and are inherited when no row exists.
CREATE TABLE IF NOT EXISTS profile_sequence_preferences (
    profile_id    BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    media_type    TEXT NOT NULL,
    container_key TEXT NOT NULL,
    show_missing  INTEGER NOT NULL,
    updated_at    TEXT NOT NULL,
    PRIMARY KEY (profile_id, media_type, container_key)
);

CREATE INDEX IF NOT EXISTS idx_profile_sequence_preferences_container
    ON profile_sequence_preferences(media_type, container_key, profile_id);

-- Personal hub state is deliberately independent from Collections, Playlists,
-- Galleries, and playback progress. A saved container remains one reference.
CREATE TABLE IF NOT EXISTS profile_saved_items (
    profile_id   BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    entity_kind  TEXT NOT NULL CHECK(entity_kind IN ('Movie','TvShow','Book','Comic','Audiobook','Album','Song','Collection','Playlist')),
    entity_id    BLOB NOT NULL,
    saved_at     TEXT NOT NULL,
    position     INTEGER,
    PRIMARY KEY (profile_id, entity_kind, entity_id)
);

-- Canonical artwork model. image_cache remains the byte/download cache; these
-- tables provide one durable image identity plus any number of entity usages.
CREATE TABLE IF NOT EXISTS artwork_assets (
    id                  BLOB NOT NULL PRIMARY KEY,
    content_hash        TEXT NOT NULL UNIQUE,
    original_path       TEXT,
    small_path          TEXT,
    medium_path         TEXT,
    large_path          TEXT,
    width_px            INTEGER,
    height_px           INTEGER,
    aspect_class        TEXT NOT NULL DEFAULT 'UnsupportedRect',
    primary_hex         TEXT,
    secondary_hex       TEXT,
    accent_hex          TEXT,
    source_provider     TEXT,
    source_url          TEXT,
    provider_reference  TEXT,
    perceptual_hash     INTEGER,
    created_at          TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at          TEXT
);

CREATE TABLE IF NOT EXISTS entity_artwork_links (
    id                  BLOB NOT NULL PRIMARY KEY,
    entity_id           BLOB NOT NULL,
    entity_type         TEXT NOT NULL,
    artwork_asset_id    BLOB NOT NULL REFERENCES artwork_assets(id) ON DELETE RESTRICT,
    role                TEXT NOT NULL CHECK(role IN ('Primary','Background','Portrait','Logo')),
    context             TEXT,
    source_asset_type   TEXT,
    is_preferred        INTEGER NOT NULL DEFAULT 0,
    is_user_override    INTEGER NOT NULL DEFAULT 0,
    sort_order          INTEGER NOT NULL DEFAULT 0,
    created_at          TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at          TEXT,
    UNIQUE(entity_id, entity_type, artwork_asset_id, role, context)
);

CREATE TABLE IF NOT EXISTS artwork_asset_context (
    artwork_asset_id    BLOB NOT NULL REFERENCES artwork_assets(id) ON DELETE CASCADE,
    entity_id           BLOB NOT NULL,
    entity_type         TEXT NOT NULL,
    entity_label        TEXT NOT NULL,
    media_type          TEXT,
    year                TEXT,
    role                TEXT,
    provider            TEXT,
    canonical_id        TEXT,
    search_text         TEXT NOT NULL,
    created_at          TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at          TEXT,
    PRIMARY KEY(artwork_asset_id, entity_id, entity_type, role)
);

CREATE INDEX IF NOT EXISTS idx_entity_artwork_links_entity_role
    ON entity_artwork_links(entity_id, entity_type, role, is_preferred DESC, sort_order);
CREATE INDEX IF NOT EXISTS idx_entity_artwork_links_asset
    ON entity_artwork_links(artwork_asset_id);
CREATE INDEX IF NOT EXISTS idx_artwork_asset_context_search
    ON artwork_asset_context(search_text COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS idx_artwork_asset_context_facets
    ON artwork_asset_context(entity_type, media_type, year, role, provider, entity_id);

CREATE VIRTUAL TABLE IF NOT EXISTS artwork_asset_search USING fts5(
    artwork_asset_id UNINDEXED,
    search_text,
    tokenize = 'trigram'
);

CREATE TRIGGER IF NOT EXISTS trg_artwork_asset_context_search_insert
AFTER INSERT ON artwork_asset_context BEGIN
    INSERT INTO artwork_asset_search(artwork_asset_id, search_text)
    VALUES (new.artwork_asset_id, new.search_text);
END;

CREATE TRIGGER IF NOT EXISTS trg_artwork_asset_context_search_update
AFTER UPDATE OF search_text, artwork_asset_id ON artwork_asset_context BEGIN
    DELETE FROM artwork_asset_search
    WHERE artwork_asset_id = old.artwork_asset_id AND search_text = old.search_text;
    INSERT INTO artwork_asset_search(artwork_asset_id, search_text)
    VALUES (new.artwork_asset_id, new.search_text);
END;

CREATE TRIGGER IF NOT EXISTS trg_artwork_asset_context_search_delete
AFTER DELETE ON artwork_asset_context BEGIN
    DELETE FROM artwork_asset_search
    WHERE artwork_asset_id = old.artwork_asset_id AND search_text = old.search_text;
END;

CREATE INDEX IF NOT EXISTS idx_profile_saved_items_recent
    ON profile_saved_items(profile_id, saved_at DESC);

CREATE TABLE IF NOT EXISTS profile_reactions (
    profile_id   BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    entity_kind  TEXT NOT NULL CHECK(entity_kind IN ('Movie','TvShow','Book','Comic','Audiobook','Album','Song','Collection','Playlist')),
    entity_id    BLOB NOT NULL,
    reaction     TEXT NOT NULL CHECK(reaction IN ('Like','Dislike','Love')),
    updated_at   TEXT NOT NULL,
    PRIMARY KEY (profile_id, entity_kind, entity_id)
);

CREATE INDEX IF NOT EXISTS idx_profile_reactions_recent
    ON profile_reactions(profile_id, reaction, updated_at DESC);

CREATE TABLE IF NOT EXISTS user_states (
    revision INTEGER NOT NULL DEFAULT 0,
    user_id             BLOB NOT NULL,
    asset_id            BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    content_hash        TEXT,
    progress_pct        REAL NOT NULL DEFAULT 0.0,
    last_accessed       TEXT NOT NULL DEFAULT (datetime('now')),
    extended_properties TEXT,
    PRIMARY KEY (user_id, asset_id)
);

CREATE TABLE IF NOT EXISTS user_taste_profiles (
    user_id       BLOB NOT NULL PRIMARY KEY,
    profile_json  TEXT NOT NULL DEFAULT '{}',
    summary       TEXT,
    updated_at    TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS wikidata_bridge_candidates (
    id                    BLOB PRIMARY KEY,
    job_id                BLOB NOT NULL REFERENCES identity_jobs(id) ON DELETE CASCADE,
    qid                   TEXT NOT NULL,
    label                 TEXT NOT NULL,
    description           TEXT,
    matched_by            TEXT NOT NULL,
    bridge_id_type        TEXT,
    is_exact_match        INTEGER NOT NULL DEFAULT 0,
    score_total           REAL NOT NULL DEFAULT 0.0,
    score_breakdown_json  TEXT,
    outcome               TEXT NOT NULL DEFAULT 'Rejected',
    created_at            TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS works (
    id                   BLOB    NOT NULL PRIMARY KEY,  -- UUID
    collection_id               BLOB    REFERENCES collections(id) ON DELETE SET NULL,
    media_type           TEXT    NOT NULL,              -- e.g. 'Movies', 'Books'

    -- Parent/child hierarchy
    work_kind            TEXT    NOT NULL DEFAULT 'standalone'
                                 CHECK (work_kind IN ('standalone','parent','child','catalog')),
    parent_work_id       BLOB    REFERENCES works(id) ON DELETE SET NULL,
    ordinal              INTEGER,                       -- track #, episode #, issue #, volume #
    ordinal_sort         REAL,                          -- decimal/disc-aware sequence sort value
    is_catalog_only      INTEGER NOT NULL DEFAULT 0,    -- 1 = no file in library yet
    external_identifiers TEXT,                          -- JSON: {"isbn_13":"...","tmdb_id":"..."}
    display_overrides_json TEXT,                        -- JSON: presentation-only aliases and sort/display overrides

    -- Parent key shadow column for indexed find-or-create lookups.
    -- Populated for parent Works only (work_kind = 'parent') as a normalized
    -- "key1|key2" string the HierarchyResolver uses to dedup albums, shows, and
    -- series before any QID is known. NULL on standalone/child/catalog rows.
    parent_key           TEXT,

    -- Ownership flag. 'Owned' = file is in the library;
    -- 'Unowned' = catalog-only row discovered from Wikidata or a retail
    -- provider but not yet ingested. Kept in sync with is_catalog_only.
    ownership            TEXT NOT NULL DEFAULT 'Owned'
, universe_mismatch INTEGER NOT NULL DEFAULT 0 CHECK (universe_mismatch IN (0, 1)), universe_mismatch_at TEXT, wikidata_status TEXT DEFAULT 'pending', wikidata_checked_at TEXT, wikidata_qid TEXT, curator_state TEXT, rejected_at TEXT, provisional_metadata_json TEXT, match_level TEXT DEFAULT 'work', wikidata_match_source TEXT, wikidata_match_locked INTEGER NOT NULL DEFAULT 0, wikidata_rejected_qids_json TEXT);


CREATE INDEX IF NOT EXISTS idx_bridge_ids_entity
    ON bridge_ids(entity_id);

CREATE INDEX IF NOT EXISTS idx_bridge_ids_type_value
    ON bridge_ids(id_type, id_value);

CREATE INDEX IF NOT EXISTS idx_canonical_value_arrays_key_value_entity
    ON canonical_value_arrays(key, value, entity_id);

CREATE INDEX IF NOT EXISTS idx_canonical_values_key_value_entity
    ON canonical_values(key, value, entity_id);

CREATE INDEX IF NOT EXISTS idx_canonical_values_entity_key
    ON canonical_values(entity_id, key);

CREATE INDEX IF NOT EXISTS idx_character_portraits_character
    ON character_portraits(fictional_entity_id);

CREATE UNIQUE INDEX IF NOT EXISTS idx_character_portraits_pair
    ON character_portraits(person_id, fictional_entity_id);

CREATE INDEX IF NOT EXISTS idx_character_portraits_person
    ON character_portraits(person_id);

CREATE INDEX IF NOT EXISTS idx_collection_placements_collection_id
    ON collection_placements(collection_id);

CREATE INDEX IF NOT EXISTS idx_collection_items_collection_sort
    ON collection_items(collection_id, sort_order);

CREATE INDEX IF NOT EXISTS idx_collection_items_work
    ON collection_items(work_id);

CREATE INDEX IF NOT EXISTS idx_collection_placements_location
    ON collection_placements(location);

CREATE INDEX IF NOT EXISTS idx_collection_rel_collection_id
    ON collection_relationships(collection_id);

CREATE INDEX IF NOT EXISTS idx_collection_rel_type_qid
    ON collection_relationships(rel_type, rel_qid);

CREATE INDEX IF NOT EXISTS idx_collections_collection_type ON collections(collection_type);

CREATE INDEX IF NOT EXISTS idx_collections_resolution ON collections(resolution);

CREATE INDEX IF NOT EXISTS idx_collections_rule_hash ON collections(rule_hash);
CREATE UNIQUE INDEX IF NOT EXISTS ux_collections_custom_rule_hash
    ON collections(rule_hash)
    WHERE rule_hash IS NOT NULL AND is_enabled = 1 AND collection_type = 'Custom';

CREATE INDEX IF NOT EXISTS idx_cva_key_qid
    ON canonical_value_arrays(key, value_qid);

CREATE INDEX IF NOT EXISTS idx_deferred_queue_created
    ON deferred_enrichment_queue(created_at);

CREATE INDEX IF NOT EXISTS idx_deferred_queue_failure_type
    ON deferred_enrichment_queue(failure_type)
    WHERE failure_type IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_deferred_queue_status
    ON deferred_enrichment_queue(status);

CREATE INDEX IF NOT EXISTS idx_editions_work_id
    ON editions(work_id);

CREATE INDEX IF NOT EXISTS idx_encode_jobs_status_schedule
    ON encode_jobs(status, scheduled_for, created_at);

CREATE INDEX IF NOT EXISTS idx_encode_jobs_native_owner
    ON encode_jobs(owner_profile_id, owner_device_id, created_at);

CREATE INDEX IF NOT EXISTS idx_offline_variants_native_owner
    ON offline_variants(asset_id, owner_profile_id, owner_device_id, created_at);

CREATE INDEX IF NOT EXISTS idx_entity_assets_entity
    ON entity_assets(entity_id, entity_type);

CREATE INDEX IF NOT EXISTS idx_entity_assets_type
    ON entity_assets(entity_id, asset_type);

CREATE UNIQUE INDEX IF NOT EXISTS ux_entity_assets_entity_type_source_url
    ON entity_assets(entity_id, asset_type, image_url COLLATE NOCASE)
    WHERE image_url IS NOT NULL AND length(trim(image_url)) > 0;

CREATE INDEX IF NOT EXISTS idx_entity_capability_states_entity
ON entity_capability_states(entity_id);

CREATE INDEX IF NOT EXISTS idx_entity_capability_states_status
ON entity_capability_states(capability_id, status, next_retry_at);

CREATE INDEX IF NOT EXISTS idx_entity_rel_object
    ON entity_relationships (object_qid);

CREATE INDEX IF NOT EXISTS idx_entity_rel_subject
    ON entity_relationships (subject_qid);

CREATE INDEX IF NOT EXISTS idx_entity_rel_context_work
    ON entity_relationships (context_work_qid);

CREATE INDEX IF NOT EXISTS idx_entity_rel_qualifier_lookup
    ON entity_relationship_qualifiers (qualifier_type, value);

CREATE INDEX IF NOT EXISTS idx_entity_rel_qualifier_relationship
    ON entity_relationship_qualifiers (relationship_id);

CREATE INDEX IF NOT EXISTS idx_events_entity ON entity_events(entity_id);

CREATE INDEX IF NOT EXISTS idx_events_entity_stage ON entity_events(entity_id, stage);

CREATE INDEX IF NOT EXISTS idx_events_entity_type ON entity_events(entity_type);

CREATE INDEX IF NOT EXISTS idx_events_occurred ON entity_events(occurred_at);

CREATE INDEX IF NOT EXISTS idx_events_provider ON entity_events(provider_id);

CREATE INDEX IF NOT EXISTS idx_events_type ON entity_events(event_type);

CREATE INDEX IF NOT EXISTS idx_fewl_work_qid
    ON fictional_entity_work_links (work_qid);

CREATE INDEX IF NOT EXISTS idx_fewl_entity_work
    ON fictional_entity_work_links (entity_id, work_qid);

CREATE INDEX IF NOT EXISTS idx_fictional_entities_type
    ON fictional_entities (entity_sub_type);

CREATE INDEX IF NOT EXISTS idx_fictional_entities_universe
    ON fictional_entities (fictional_universe_qid);

CREATE INDEX IF NOT EXISTS idx_plugin_lore_sources_universe
    ON plugin_lore_sources (universe_qid, status);

CREATE INDEX IF NOT EXISTS idx_plugin_lore_entities_universe
    ON plugin_lore_entities (universe_qid, entity_type);

CREATE INDEX IF NOT EXISTS idx_plugin_lore_entities_qid
    ON plugin_lore_entities (wikidata_qid) WHERE wikidata_qid IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_plugin_lore_relationships_universe
    ON plugin_lore_relationships (universe_qid, relationship_type);

CREATE INDEX IF NOT EXISTS idx_field_changes_entity ON entity_field_changes(entity_id);

CREATE INDEX IF NOT EXISTS idx_field_changes_event ON entity_field_changes(event_id);

CREATE INDEX IF NOT EXISTS idx_field_changes_field ON entity_field_changes(entity_id, field);

CREATE INDEX IF NOT EXISTS idx_file_hash_cache_sha256
    ON file_hash_cache(sha256);

CREATE UNIQUE INDEX IF NOT EXISTS ux_file_hash_cache_path_nocase
    ON file_hash_cache(absolute_path COLLATE NOCASE);

CREATE INDEX IF NOT EXISTS idx_identity_jobs_entity_id ON identity_jobs (entity_id);

CREATE INDEX IF NOT EXISTS idx_identity_jobs_ingestion_run_id ON identity_jobs (ingestion_run_id);

CREATE INDEX IF NOT EXISTS idx_identity_jobs_lease ON identity_jobs (state, lease_expires_at);

CREATE INDEX IF NOT EXISTS idx_identity_jobs_run_entity_updated
    ON identity_jobs(ingestion_run_id, entity_id, updated_at);

CREATE INDEX IF NOT EXISTS idx_identity_jobs_activity_latest
    ON identity_jobs(ingestion_run_id, entity_id, updated_at, created_at);

CREATE INDEX IF NOT EXISTS idx_identity_jobs_state ON identity_jobs (state);

CREATE INDEX IF NOT EXISTS idx_identity_jobs_next_retry
    ON identity_jobs(next_retry_at) WHERE next_retry_at IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_image_cache_phash
    ON image_cache(phash) WHERE phash IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_ingestion_batches_created ON ingestion_batches(created_at);

CREATE INDEX IF NOT EXISTS idx_ingestion_batches_status ON ingestion_batches(status);

CREATE INDEX IF NOT EXISTS idx_ingestion_batch_artifacts_batch
    ON ingestion_batch_artifacts(batch_id, occurred_at);

CREATE INDEX IF NOT EXISTS idx_ingestion_batch_artifacts_type
    ON ingestion_batch_artifacts(batch_id, artifact_type);

CREATE INDEX IF NOT EXISTS idx_ingestion_batch_artifacts_artifact
    ON ingestion_batch_artifacts(artifact_type, artifact_id);

CREATE INDEX IF NOT EXISTS idx_ingestion_batch_artifacts_parent
    ON ingestion_batch_artifacts(parent_entity_id);

CREATE INDEX IF NOT EXISTS idx_ingestion_log_media_asset ON ingestion_log(media_asset_id);

CREATE INDEX IF NOT EXISTS idx_ingestion_log_run
    ON ingestion_log(ingestion_run_id);

CREATE INDEX IF NOT EXISTS idx_ingestion_log_run_created
    ON ingestion_log(ingestion_run_id, created_at);

CREATE INDEX IF NOT EXISTS idx_ingestion_log_status
    ON ingestion_log(status);

CREATE INDEX IF NOT EXISTS idx_media_assets_content_hash
    ON media_assets (content_hash);

CREATE INDEX IF NOT EXISTS idx_media_assets_file_path_root
    ON media_assets (file_path_root COLLATE NOCASE);

CREATE INDEX IF NOT EXISTS idx_media_assets_status
    ON media_assets(status);

CREATE INDEX IF NOT EXISTS idx_media_assets_presented
    ON media_assets(presented_at) WHERE presented_at IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_media_assets_edition_id
    ON media_assets (edition_id);
CREATE INDEX IF NOT EXISTS idx_media_assets_derived_from
    ON media_assets (derived_from_asset_id) WHERE derived_from_asset_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_media_assets_library_id
    ON media_assets(library_id) WHERE library_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_media_assets_orphaned
    ON media_assets(is_orphaned) WHERE is_orphaned = 1;

CREATE INDEX IF NOT EXISTS idx_media_operation_events_batch
ON media_operation_events(batch_id, occurred_at);

CREATE INDEX IF NOT EXISTS idx_media_operation_events_entity
ON media_operation_events(entity_id, occurred_at);

CREATE INDEX IF NOT EXISTS idx_media_operation_events_operation
ON media_operation_events(operation_id, occurred_at);

CREATE INDEX IF NOT EXISTS idx_media_operation_events_batch_entity
ON media_operation_events(batch_id, entity_id, occurred_at);

CREATE INDEX IF NOT EXISTS idx_media_operations_batch
ON media_operations(batch_id, status);

CREATE INDEX IF NOT EXISTS idx_media_operations_batch_entity_type
ON media_operations(batch_id, entity_id, operation_type);

CREATE INDEX IF NOT EXISTS idx_media_operations_capability
ON media_operations(capability_id, status, next_retry_at);

CREATE INDEX IF NOT EXISTS idx_media_operations_entity
ON media_operations(entity_id);

CREATE INDEX IF NOT EXISTS idx_media_operations_lease
ON media_operations(status, lease_expires_at);

CREATE INDEX IF NOT EXISTS idx_media_operations_queue
ON media_operations(queue_name, status, priority, position_key);

CREATE INDEX IF NOT EXISTS idx_media_operations_source_path
ON media_operations(operation_type, source_path, status);

CREATE INDEX IF NOT EXISTS idx_metadata_claims_claimed_at
    ON metadata_claims (claimed_at);

CREATE INDEX IF NOT EXISTS idx_metadata_claims_entity_id
    ON metadata_claims (entity_id);

CREATE INDEX IF NOT EXISTS idx_metadata_claims_provider_id
    ON metadata_claims (provider_id);

CREATE INDEX IF NOT EXISTS idx_metadata_claims_current_lookup
    ON metadata_claims (entity_id, provider_id, claim_key, is_current);

CREATE INDEX IF NOT EXISTS idx_offline_variants_asset
    ON offline_variants(asset_id, status);

CREATE INDEX IF NOT EXISTS idx_adaptive_hls_packages_eviction
    ON adaptive_hls_packages(status, last_accessed);

CREATE INDEX IF NOT EXISTS idx_pending_person_signals_name_role
    ON pending_person_signals (name, role);

CREATE INDEX IF NOT EXISTS idx_person_aliases_real ON person_aliases (real_person_id);

CREATE INDEX IF NOT EXISTS idx_person_group_members_member
    ON person_group_members (member_id);

CREATE INDEX IF NOT EXISTS idx_enrichment_refresh_schedule_due
    ON enrichment_refresh_schedule (status, next_due_at);

CREATE INDEX IF NOT EXISTS idx_person_media_links_asset
    ON person_media_links (media_asset_id);

CREATE INDEX IF NOT EXISTS idx_person_media_links_asset_role_person
    ON person_media_links(media_asset_id, role, person_id);

CREATE INDEX IF NOT EXISTS idx_person_media_links_person
    ON person_media_links(person_id);

CREATE INDEX IF NOT EXISTS idx_persons_name
    ON persons (name);

CREATE INDEX IF NOT EXISTS idx_persons_name_nocase
    ON persons (name COLLATE NOCASE);

CREATE UNIQUE INDEX IF NOT EXISTS idx_persons_wikidata_qid
    ON persons (wikidata_qid) WHERE wikidata_qid IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_playback_segments_asset
    ON playback_segments(asset_id, kind, start_seconds);

CREATE INDEX IF NOT EXISTS idx_player_queue_items_profile_position
    ON player_queue_items(profile_id, position);

CREATE INDEX IF NOT EXISTS idx_player_sessions_heartbeat
    ON player_sessions(last_heartbeat_at);

CREATE INDEX IF NOT EXISTS idx_audiobook_listen_history_profile_work
    ON audiobook_listen_history(profile_id, work_id, ended_at DESC);

CREATE INDEX IF NOT EXISTS idx_audiobook_listen_history_profile_asset
    ON audiobook_listen_history(profile_id, asset_id, ended_at DESC);

CREATE INDEX IF NOT EXISTS idx_music_play_stats_profile_recent
    ON music_play_stats(profile_id, last_played_at DESC);

CREATE INDEX IF NOT EXISTS idx_audiobook_bookmarks_profile_work
    ON audiobook_bookmarks(profile_id, work_id, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_audiobook_chapter_title_overrides_work
    ON audiobook_chapter_title_overrides(work_id, asset_id, chapter_index);

CREATE INDEX IF NOT EXISTS idx_prc_expires ON provider_response_cache (expires_at);

CREATE INDEX IF NOT EXISTS idx_prc_provider ON provider_response_cache (provider_id);

CREATE INDEX IF NOT EXISTS idx_account_external_logins_account
    ON account_external_logins(account_id);

CREATE INDEX IF NOT EXISTS idx_qid_labels_type ON qid_labels(entity_type);

CREATE INDEX IF NOT EXISTS idx_reader_bookmarks_user_asset
    ON reader_bookmarks (user_id, asset_id);

CREATE INDEX IF NOT EXISTS idx_reader_highlights_user_asset
    ON reader_highlights (user_id, asset_id);

CREATE INDEX IF NOT EXISTS idx_resolver_cache_expires
    ON resolver_cache(expires_at);

CREATE INDEX IF NOT EXISTS idx_retail_candidates_job_id ON retail_match_candidates (job_id);

CREATE INDEX IF NOT EXISTS idx_retail_candidates_outcome ON retail_match_candidates (outcome);

CREATE INDEX IF NOT EXISTS idx_review_queue_entity_id
    ON review_queue (entity_id);

CREATE INDEX IF NOT EXISTS idx_review_queue_status
    ON review_queue (status);

CREATE INDEX IF NOT EXISTS idx_review_queue_status_entity_ready
    ON review_queue(status, entity_id, review_ready_at);

CREATE UNIQUE INDEX IF NOT EXISTS ux_review_queue_pending_entity_trigger
    ON review_queue (entity_id, trigger)
    WHERE status = 'Pending';

CREATE INDEX IF NOT EXISTS idx_series_manifest_hydrations_collection
    ON series_manifest_hydrations(collection_id);

CREATE INDEX IF NOT EXISTS idx_series_manifest_items_collection
    ON series_manifest_items(collection_id);

CREATE INDEX IF NOT EXISTS idx_series_manifest_items_item_qid
    ON series_manifest_items(item_qid);

CREATE INDEX IF NOT EXISTS idx_series_manifest_items_linked_work
    ON series_manifest_items(linked_work_id);

CREATE INDEX IF NOT EXISTS idx_series_manifest_items_ownership
    ON series_manifest_items(ownership_state);

CREATE INDEX IF NOT EXISTS idx_series_manifest_items_series_qid
    ON series_manifest_items(series_qid);

CREATE INDEX IF NOT EXISTS idx_series_members_series
    ON series_members(series_qid);

CREATE INDEX IF NOT EXISTS idx_system_activity_action_type
    ON system_activity (action_type);

CREATE INDEX IF NOT EXISTS idx_system_activity_occurred_at
    ON system_activity (occurred_at);

CREATE INDEX IF NOT EXISTS idx_system_activity_run_entity_action
    ON system_activity(ingestion_run_id, entity_id, action_type, occurred_at);

CREATE INDEX IF NOT EXISTS idx_text_tracks_asset_kind
    ON text_tracks(asset_id, kind);

CREATE INDEX IF NOT EXISTS idx_text_tracks_preferred
    ON text_tracks(asset_id, kind, language, is_preferred);

CREATE INDEX IF NOT EXISTS idx_wikidata_candidates_job_id ON wikidata_bridge_candidates (job_id);

CREATE INDEX IF NOT EXISTS idx_wikidata_candidates_outcome ON wikidata_bridge_candidates (outcome);

CREATE INDEX IF NOT EXISTS idx_works_collection_id
    ON works (collection_id);

CREATE INDEX IF NOT EXISTS idx_works_collection_ordinal_sort
    ON works(collection_id, ordinal_sort);

CREATE INDEX IF NOT EXISTS idx_works_media_type
    ON works (media_type);

CREATE INDEX IF NOT EXISTS idx_works_ownership_media_type
    ON works(ownership, media_type);

CREATE INDEX IF NOT EXISTS idx_works_curator_state
    ON works(curator_state) WHERE curator_state IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_works_catalog_media_type
    ON works(is_catalog_only, media_type);

CREATE INDEX IF NOT EXISTS idx_works_parent_key
    ON works(media_type, parent_key) WHERE parent_key IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_works_root_parent_key
    ON works(media_type, parent_key)
    WHERE work_kind = 'parent'
      AND parent_key IS NOT NULL
      AND parent_work_id IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_works_nested_parent_ordinal
    ON works(media_type, parent_work_id, ordinal)
    WHERE work_kind = 'parent'
      AND parent_work_id IS NOT NULL
      AND ordinal IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_works_child_parent_ordinal_sort
    ON works(parent_work_id, ordinal_sort)
    WHERE work_kind IN ('child', 'catalog')
      AND parent_work_id IS NOT NULL
      AND ordinal_sort IS NOT NULL
      AND (media_type != 'Audiobooks' OR parent_key IS NULL);

CREATE INDEX IF NOT EXISTS idx_works_parent_work_id ON works(parent_work_id);

CREATE INDEX IF NOT EXISTS idx_works_wikidata_qid
                    ON works(wikidata_qid) WHERE wikidata_qid IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_works_work_kind      ON works(work_kind) WHERE work_kind != 'standalone';

CREATE UNIQUE INDEX IF NOT EXISTS ux_entity_capability_states_key
ON entity_capability_states(entity_id, capability_id, COALESCE(sub_key, ''));

CREATE UNIQUE INDEX IF NOT EXISTS ux_identity_jobs_entity_pass_active
    ON identity_jobs (entity_id, pass)
    WHERE state NOT IN ('Ready', 'ReadyWithoutUniverse', 'Failed', 'RetailNoMatch', 'QidNoMatch', 'QidNeedsReview');

CREATE UNIQUE INDEX IF NOT EXISTS ux_media_operations_idempotency
ON media_operations(idempotency_key);

-- Presentation-facing person credits must follow the same ordered canonical
-- contributor source used by detail pages. person_media_links intentionally
-- remains the broader enrichment graph and can include assistant directors,
-- additional performers, and other non-primary relationships.
DROP VIEW IF EXISTS primary_person_media_credits;
CREATE VIEW primary_person_media_credits AS
WITH asset_scope AS (
    SELECT ma.id AS media_asset_id,
           w.id AS work_id,
           COALESCE(gp.id, p.id, w.id) AS root_work_id
    FROM media_assets ma
    INNER JOIN editions e ON e.id = ma.edition_id
    INNER JOIN works w ON w.id = e.work_id
    LEFT JOIN works p ON p.id = w.parent_work_id
    LEFT JOIN works gp ON gp.id = p.parent_work_id
),
array_candidates AS (
    SELECT scope.media_asset_id,
           cva.key AS credit_key,
           cva.ordinal AS billing_order,
           TRIM(cva.value) AS person_name,
           NULLIF(TRIM(cva.value_qid), '') AS person_qid,
           CASE
               WHEN cva.entity_id = scope.root_work_id THEN 0
               WHEN cva.entity_id = scope.work_id THEN 1
               ELSE 2
           END AS scope_priority
    FROM asset_scope scope
    INNER JOIN canonical_value_arrays cva
        ON cva.entity_id IN (scope.root_work_id, scope.work_id, scope.media_asset_id)
    WHERE cva.key IN (
              'author', 'narrator', 'director', 'artist', 'performer',
              'composer', 'illustrator', 'screenwriter', 'cast_member')
      AND NULLIF(TRIM(cva.value), '') IS NOT NULL
),
selected_arrays AS (
    SELECT candidate.media_asset_id,
           candidate.credit_key,
           candidate.billing_order,
           candidate.person_name,
           candidate.person_qid
    FROM array_candidates candidate
    WHERE candidate.scope_priority = (
        SELECT MIN(peer.scope_priority)
        FROM array_candidates peer
        WHERE peer.media_asset_id = candidate.media_asset_id
          AND peer.credit_key = candidate.credit_key
    )
),
claim_candidates AS (
    SELECT scope.media_asset_id,
           mc.claim_key AS credit_key,
           mc.rowid AS billing_order,
           TRIM(mc.claim_value) AS person_name,
           NULL AS person_qid,
           CASE
               WHEN mc.entity_id = scope.root_work_id THEN 0
               WHEN mc.entity_id = scope.work_id THEN 1
               ELSE 2
           END AS scope_priority
    FROM asset_scope scope
    INNER JOIN metadata_claims mc
        ON mc.entity_id IN (scope.root_work_id, scope.work_id, scope.media_asset_id)
    WHERE mc.claim_key IN (
              'author', 'narrator', 'director', 'artist', 'performer',
              'composer', 'illustrator', 'screenwriter', 'cast_member')
      AND NULLIF(TRIM(mc.claim_value), '') IS NOT NULL
      AND NOT EXISTS (
          SELECT 1
          FROM selected_arrays array_credit
          WHERE array_credit.media_asset_id = scope.media_asset_id
            AND array_credit.credit_key = mc.claim_key
      )
),
selected_claims AS (
    SELECT candidate.media_asset_id,
           candidate.credit_key,
           candidate.billing_order,
           candidate.person_name,
           candidate.person_qid
    FROM claim_candidates candidate
    WHERE candidate.scope_priority = (
        SELECT MIN(peer.scope_priority)
        FROM claim_candidates peer
        WHERE peer.media_asset_id = candidate.media_asset_id
          AND peer.credit_key = candidate.credit_key
    )
),
scalar_candidates AS (
    SELECT scope.media_asset_id,
           cv.key AS credit_key,
           0 AS billing_order,
           TRIM(cv.value) AS person_name,
           NULL AS person_qid,
           CASE
               WHEN cv.entity_id = scope.root_work_id THEN 0
               WHEN cv.entity_id = scope.work_id THEN 1
               ELSE 2
           END AS scope_priority
    FROM asset_scope scope
    INNER JOIN canonical_values cv
        ON cv.entity_id IN (scope.root_work_id, scope.work_id, scope.media_asset_id)
    WHERE cv.key IN (
              'author', 'narrator', 'director', 'artist', 'performer',
              'composer', 'illustrator', 'screenwriter', 'cast_member')
      AND NULLIF(TRIM(cv.value), '') IS NOT NULL
      AND NOT EXISTS (
          SELECT 1
          FROM selected_arrays array_credit
          WHERE array_credit.media_asset_id = scope.media_asset_id
            AND array_credit.credit_key = cv.key
      )
      AND NOT EXISTS (
          SELECT 1
          FROM selected_claims claim_credit
          WHERE claim_credit.media_asset_id = scope.media_asset_id
            AND claim_credit.credit_key = cv.key
      )
),
selected_scalars AS (
    SELECT candidate.media_asset_id,
           candidate.credit_key,
           candidate.billing_order,
           candidate.person_name,
           candidate.person_qid
    FROM scalar_candidates candidate
    WHERE candidate.scope_priority = (
        SELECT MIN(peer.scope_priority)
        FROM scalar_candidates peer
        WHERE peer.media_asset_id = candidate.media_asset_id
          AND peer.credit_key = candidate.credit_key
    )
),
selected_credits AS (
    SELECT * FROM selected_arrays
    UNION ALL
    SELECT * FROM selected_claims
    UNION ALL
    SELECT * FROM selected_scalars
)
SELECT DISTINCT credit.media_asset_id,
       person.id AS person_id,
       person.name AS person_name,
       person.wikidata_qid AS person_qid,
       credit.credit_key,
       credit.billing_order,
       CASE credit.credit_key
           WHEN 'author' THEN 'Author'
           WHEN 'narrator' THEN 'Narrator'
           WHEN 'director' THEN 'Director'
           WHEN 'artist' THEN 'Artist'
           WHEN 'performer' THEN 'Performer'
           WHEN 'composer' THEN 'Composer'
           WHEN 'illustrator' THEN 'Illustrator'
           WHEN 'screenwriter' THEN 'Screenwriter'
           WHEN 'cast_member' THEN 'Actor'
       END AS role
FROM selected_credits credit
INNER JOIN persons person
    ON (credit.person_qid IS NOT NULL
        AND person.wikidata_qid = credit.person_qid COLLATE NOCASE)
    OR (credit.person_qid IS NULL
        AND person.name = credit.person_name COLLATE NOCASE)
WHERE credit.credit_key != 'author'
   OR NOT EXISTS (
    SELECT 1
    FROM selected_credits collective_credit
    INNER JOIN persons collective_person
        ON (collective_credit.person_qid IS NOT NULL
            AND collective_person.wikidata_qid = collective_credit.person_qid COLLATE NOCASE)
        OR (collective_credit.person_qid IS NULL
            AND collective_person.name = collective_credit.person_name COLLATE NOCASE)
    WHERE collective_credit.media_asset_id = credit.media_asset_id
      AND collective_credit.credit_key = credit.credit_key
      AND collective_person.is_pseudonym = 1
      AND collective_person.id != person.id
);


-- Exact episode participation; replacement is atomic and isolated from show enrichment.
CREATE TABLE IF NOT EXISTS tv_episode_credits (
    work_id BLOB PRIMARY KEY REFERENCES works(id) ON DELETE CASCADE,
    show_work_id BLOB NOT NULL REFERENCES works(id) ON DELETE CASCADE,
    provider_episode_id TEXT NOT NULL,
    season INTEGER NOT NULL,
    episode INTEGER NOT NULL,
    credits_json TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_tv_episode_credits_show ON tv_episode_credits(show_work_id, season, episode);

CREATE TABLE IF NOT EXISTS personal_status_commands (
    id BLOB PRIMARY KEY,
    profile_id BLOB NOT NULL,
    target_id BLOB NOT NULL,
    media_type TEXT NOT NULL,
    command TEXT NOT NULL,
    changed_at TEXT NOT NULL,
    snapshot_json TEXT NOT NULL,
    undone INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_personal_status_history ON personal_status_commands(profile_id, target_id, changed_at);

CREATE TABLE IF NOT EXISTS application_events (
    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    event_id BLOB NOT NULL UNIQUE,
    event_type TEXT NOT NULL,
    version INTEGER NOT NULL CHECK(version > 0),
    occurred_at TEXT NOT NULL,
    server_id TEXT NOT NULL,
    subject_type TEXT NOT NULL,
    subject_id TEXT NOT NULL,
    library_id BLOB,
    profile_id BLOB,
    feature_id TEXT CHECK(feature_id IS NULL OR feature_id IN ('read', 'watch', 'listen')),
    payload_json TEXT NOT NULL CHECK(length(CAST(payload_json AS BLOB)) <= 65536)
);
CREATE INDEX IF NOT EXISTS ix_application_events_type_sequence ON application_events(event_type, sequence);
CREATE INDEX IF NOT EXISTS ix_application_events_library_sequence ON application_events(library_id, sequence);
CREATE INDEX IF NOT EXISTS ix_application_events_profile_sequence ON application_events(profile_id, sequence);
CREATE INDEX IF NOT EXISTS ix_application_events_occurred ON application_events(occurred_at);

CREATE TABLE IF NOT EXISTS application_webhooks (
    id BLOB PRIMARY KEY,
    application_id BLOB NOT NULL REFERENCES applications(id) ON DELETE CASCADE,
    url TEXT NOT NULL,
    event_types_json TEXT NOT NULL,
    allow_local_network INTEGER NOT NULL DEFAULT 0,
    is_enabled INTEGER NOT NULL DEFAULT 1,
    secret_ciphertext TEXT NOT NULL,
    version INTEGER NOT NULL DEFAULT 1,
    last_event_sequence INTEGER NOT NULL DEFAULT 0,
    last_status TEXT NOT NULL DEFAULT 'Waiting for events',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    last_attempt_at TEXT,
    last_success_at TEXT
);
CREATE INDEX IF NOT EXISTS ix_application_webhooks_application ON application_webhooks(application_id);
CREATE TABLE IF NOT EXISTS application_webhook_deliveries (
    id BLOB PRIMARY KEY,
    webhook_id BLOB NOT NULL REFERENCES application_webhooks(id) ON DELETE CASCADE,
    webhook_version INTEGER NOT NULL,
    event_id BLOB NOT NULL,
    event_sequence INTEGER NOT NULL,
    event_json TEXT NOT NULL CHECK(length(CAST(event_json AS BLOB)) <= 70000),
    status TEXT NOT NULL DEFAULT 'pending',
    attempt_count INTEGER NOT NULL DEFAULT 0,
    next_attempt_at TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE(webhook_id,event_id)
);
CREATE INDEX IF NOT EXISTS ix_application_webhook_delivery_due ON application_webhook_deliveries(status,next_attempt_at);

CREATE TABLE IF NOT EXISTS playback_telemetry_sessions (
    id BLOB NOT NULL PRIMARY KEY,
    player_session_id BLOB NOT NULL,
    authority_session_id BLOB,
    account_id BLOB NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    profile_id BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    application_id BLOB REFERENCES applications(id) ON DELETE SET NULL,
    device_id BLOB REFERENCES client_devices(id) ON DELETE SET NULL,
    asset_id BLOB REFERENCES media_assets(id) ON DELETE SET NULL,
    library_id BLOB NOT NULL,
    media_type TEXT NOT NULL,
    feature_id TEXT NOT NULL CHECK(feature_id IN('read','watch','listen')),
    state TEXT NOT NULL CHECK(state IN('playing','paused','stopped','completed')),
    started_at TEXT NOT NULL,
    last_observed_at TEXT NOT NULL,
    ended_at TEXT,
    started_position_seconds REAL NOT NULL CHECK(started_position_seconds >= 0),
    last_position_seconds REAL NOT NULL CHECK(last_position_seconds >= 0),
    duration_seconds REAL CHECK(duration_seconds IS NULL OR duration_seconds >= 0),
    played_duration_seconds REAL NOT NULL DEFAULT 0 CHECK(played_duration_seconds >= 0),
    last_sequence INTEGER NOT NULL DEFAULT -1 CHECK(last_sequence >= -1),
    completion_reason TEXT CHECK(completion_reason IS NULL OR completion_reason IN('stopped','completed','replaced','takeover','stale','error')),
    delivery_mode TEXT CHECK(delivery_mode IS NULL OR delivery_mode IN('direct-play','remux','transcode','reader','offline','unknown')),
    container TEXT,
    video_codec TEXT,
    audio_codec TEXT,
    width INTEGER CHECK(width IS NULL OR width > 0),
    height INTEGER CHECK(height IS NULL OR height > 0),
    bitrate_kbps INTEGER CHECK(bitrate_kbps IS NULL OR bitrate_kbps > 0),
    connection_type TEXT CHECK(connection_type IS NULL OR connection_type IN('local','remote','unknown')),
    client_name TEXT,
    client_version TEXT,
    CHECK((ended_at IS NULL AND completion_reason IS NULL AND state IN('playing','paused'))
        OR (ended_at IS NOT NULL AND completion_reason IS NOT NULL AND state IN('stopped','completed')))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_playback_telemetry_active_player ON playback_telemetry_sessions(player_session_id) WHERE ended_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_playback_telemetry_active ON playback_telemetry_sessions(last_observed_at DESC) WHERE ended_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_playback_telemetry_history ON playback_telemetry_sessions(ended_at DESC,id);
CREATE INDEX IF NOT EXISTS ix_playback_telemetry_library ON playback_telemetry_sessions(library_id,started_at DESC,id);
CREATE INDEX IF NOT EXISTS ix_playback_telemetry_profile ON playback_telemetry_sessions(account_id,profile_id,started_at DESC,id);
CREATE INDEX IF NOT EXISTS ix_playback_telemetry_device ON playback_telemetry_sessions(device_id,started_at DESC,id) WHERE device_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS consumption_history (
    id BLOB PRIMARY KEY,
    profile_id BLOB NOT NULL,
    asset_id BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    session_key TEXT NOT NULL,
    experience TEXT NOT NULL,
    started_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    progress_pct REAL NOT NULL,
    UNIQUE(profile_id, asset_id, session_key)
);
