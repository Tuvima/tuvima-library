-- tuvima:foreign-keys-off
-- Empty Trash removes a personal item for good. Shared Library contribution history and transfer history used to block that
-- (their item link was RESTRICT and NOT NULL). The link is now optional and is cleared (SET NULL) when the item is removed, so
-- a finished, failed, declined or cancelled contribution keeps its history row with no item. Work still in flight is protected
-- by Empty Trash itself, which skips such an item. Only the item link changes; every other column and value is copied as is.
--
-- SQLite cannot change a column's constraint in place, so both tables are rebuilt. The runner has foreign keys off for this
-- step (so dropping the old tables fires no foreign-key actions and the transfer -> contribution links are not cleared) and
-- checks for orphaned rows before committing. The tables have no triggers or views, and the only indexes are recreated below.

CREATE TABLE view_shared_contribution_items_new (
    id                  BLOB NOT NULL PRIMARY KEY,
    contribution_id     BLOB NOT NULL REFERENCES view_shared_contributions(id) ON DELETE CASCADE,
    item_id              BLOB REFERENCES local_items(id) ON DELETE SET NULL,
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

INSERT INTO view_shared_contribution_items_new
    (id, contribution_id, item_id, original_profile_id, original_profile_name, position, operation,
     source_manifest_json, execution_state, error, updated_at)
SELECT id, contribution_id, item_id, original_profile_id, original_profile_name, position, operation,
       source_manifest_json, execution_state, error, updated_at
  FROM view_shared_contribution_items;

CREATE TABLE view_shared_transfers_new (
    id                     BLOB NOT NULL PRIMARY KEY,
    item_id                BLOB REFERENCES local_items(id) ON DELETE SET NULL,
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

INSERT INTO view_shared_transfers_new
    (id, item_id, contribution_item_id, operation, state, source_manifest_json, destination_manifest_json,
     error, created_at, updated_at, completed_at)
SELECT id, item_id, contribution_item_id, operation, state, source_manifest_json, destination_manifest_json,
       error, created_at, updated_at, completed_at
  FROM view_shared_transfers;

DROP TABLE view_shared_transfers;
DROP TABLE view_shared_contribution_items;
ALTER TABLE view_shared_contribution_items_new RENAME TO view_shared_contribution_items;
ALTER TABLE view_shared_transfers_new RENAME TO view_shared_transfers;

CREATE INDEX idx_view_shared_contribution_items_batch
    ON view_shared_contribution_items(contribution_id, position);
