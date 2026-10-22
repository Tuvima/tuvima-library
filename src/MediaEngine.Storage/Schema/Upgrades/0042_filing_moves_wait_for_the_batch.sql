-- Filing moves wait for the batch with the tags (Library setup spec D61 to D63, WP15; Batched write-back D10 as amended by D62).
-- A move Library makes on its own (filing a newly identified file into its folder's pattern, filing out of the Import folder) no longer
-- happens the moment the file is identified: it waits on the same durable to-do list as the tags and is done in the next scheduled run,
-- moves first. Two more kinds of row are needed for that:
--   move_step 'waiting' = an automatic filing move queued for the next run (the file has not moved and stays where it is, playable);
--   move_step 'held'    = an automatic filing move held back because its newly added Managed folder has not passed its first check
--                         (D61); it is never moved until something releases it.
-- move_origin says who asked for the move, so a person's own move (done at once) and an automatic filing (done in the run, and set
-- aside untouched when the file is in use) can be told apart: 'person' or 'filing'. It is set exactly when move_step is.
-- Existing rows keep everything; a row that already has a move step was a person's move.
CREATE TABLE media_file_write_intents_new (
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
    move_step TEXT CHECK (move_step IS NULL OR move_step IN ('waiting', 'held', 'requested', 'planned', 'moved', 'checked')),
    move_origin TEXT CHECK (move_origin IS NULL OR move_origin IN ('person', 'filing')),
    move_source_path TEXT,
    move_target_path TEXT,
    move_source_hash TEXT,
    move_generation INTEGER,
    next_attempt_at TEXT,
    recovery_attempts INTEGER NOT NULL DEFAULT 0,
    claimed_run_id TEXT,
    CHECK ((target_kind = 'media_asset' AND asset_id IS NOT NULL AND local_item_id IS NULL)
        OR (target_kind = 'view_item' AND local_item_id IS NOT NULL AND asset_id IS NULL)),
    CHECK ((move_step IS NULL) = (move_origin IS NULL)),
    CHECK (move_step IS NULL OR move_step IN ('requested', 'planned', 'moved', 'checked') OR move_origin = 'filing')
);

INSERT INTO media_file_write_intents_new
    (id, target_kind, asset_id, local_item_id, generation, operation_token, trigger, status, attempts, lease_expires_at, last_error,
     created_at, updated_at, completed_at, move_step, move_origin, move_source_path, move_target_path, move_source_hash,
     move_generation, next_attempt_at, recovery_attempts, claimed_run_id)
SELECT id, target_kind, asset_id, local_item_id, generation, operation_token, trigger, status, attempts, lease_expires_at, last_error,
       created_at, updated_at, completed_at, move_step, CASE WHEN move_step IS NULL THEN NULL ELSE 'person' END,
       move_source_path, move_target_path, move_source_hash, move_generation, next_attempt_at, recovery_attempts, claimed_run_id
FROM media_file_write_intents;

DROP TABLE media_file_write_intents;
ALTER TABLE media_file_write_intents_new RENAME TO media_file_write_intents;

CREATE UNIQUE INDEX ux_media_file_write_intents_asset ON media_file_write_intents (asset_id) WHERE asset_id IS NOT NULL;
CREATE UNIQUE INDEX ux_media_file_write_intents_local_item ON media_file_write_intents (local_item_id) WHERE local_item_id IS NOT NULL;
CREATE INDEX ix_media_file_write_intents_dispatch ON media_file_write_intents (status, lease_expires_at, updated_at);
CREATE INDEX ix_media_file_write_intents_move ON media_file_write_intents (move_step) WHERE move_step IS NOT NULL;
