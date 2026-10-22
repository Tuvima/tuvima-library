-- Batched write-back (spec batched-writeback BW1). Every write into a file waits on one durable to-do list and is worked through on a
-- schedule, instead of being written the moment something changes. Pre-release, so the list is rebuilt in its new shape and its old
-- rows are dropped: nothing waiting is migrated.
--
-- One row per target (a media asset, or later a View item): a newer change bumps the generation and replaces the older one. The move_*
-- columns record a library move step by step (requested, planned, moved, checked) so a stop part-way can be finished or undone from
-- what is on disk. completed_at is when the last write finished.
DROP TABLE media_file_write_intents;

CREATE TABLE media_file_write_intents (
    id BLOB NOT NULL PRIMARY KEY,
    target_kind TEXT NOT NULL CHECK (target_kind IN ('media_asset', 'view_item')),
    asset_id BLOB REFERENCES media_assets(id) ON DELETE CASCADE,
    local_item_id BLOB REFERENCES local_items(id) ON DELETE CASCADE,
    generation INTEGER NOT NULL DEFAULT 1,
    operation_token TEXT NOT NULL,
    trigger TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'writing', 'verified', 'blocked', 'unsupported', 'failed')),
    attempts INTEGER NOT NULL DEFAULT 0,
    lease_expires_at TEXT,
    last_error TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    completed_at TEXT,
    move_step TEXT CHECK (move_step IS NULL OR move_step IN ('requested', 'planned', 'moved', 'checked')),
    move_source_path TEXT,
    move_target_path TEXT,
    move_source_hash TEXT,
    CHECK ((target_kind = 'media_asset' AND asset_id IS NOT NULL AND local_item_id IS NULL)
        OR (target_kind = 'view_item' AND local_item_id IS NOT NULL AND asset_id IS NULL))
);

CREATE UNIQUE INDEX ux_media_file_write_intents_asset ON media_file_write_intents (asset_id) WHERE asset_id IS NOT NULL;
CREATE UNIQUE INDEX ux_media_file_write_intents_local_item ON media_file_write_intents (local_item_id) WHERE local_item_id IS NOT NULL;
CREATE INDEX ix_media_file_write_intents_dispatch ON media_file_write_intents (status, lease_expires_at, updated_at);
CREATE INDEX ix_media_file_write_intents_move ON media_file_write_intents (move_step) WHERE move_step IS NOT NULL;

-- The schedule's own memory: one row. anchor_at is when the scheduler first started, so the first interval counts from there; the
-- last_run_* columns describe the most recent run for the Settings panel.
CREATE TABLE file_write_runs (
    id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
    anchor_at TEXT NOT NULL,
    last_run_started_at TEXT,
    last_run_finished_at TEXT,
    last_run_written INTEGER,
    last_run_failed INTEGER,
    last_run_kept INTEGER
);
