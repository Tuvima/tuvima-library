-- Move to Vault and Move out (Vault sections spec D31, D36 to D38, D40). A move is a durable job: the job and every item are saved before any
-- file is touched, and each item records how far it got, so a restart carries on from the last saved step. These are Vault records: the folders
-- and file names stay in these tables and are shown only to an unlocked administrator; nothing here is shared with the ordinary library.
CREATE TABLE vault_moves (
    id                         BLOB NOT NULL PRIMARY KEY,
    direction                  TEXT NOT NULL CHECK (direction IN ('in', 'out')),
    kind_key                   TEXT NOT NULL,
    source_library_id          TEXT NOT NULL COLLATE NOCASE,
    destination_library_id     TEXT NOT NULL COLLATE NOCASE,
    source_folder              TEXT NOT NULL,
    destination_folder         TEXT NOT NULL,
    remove_read_only_originals INTEGER NOT NULL DEFAULT 0 CHECK (remove_read_only_originals IN (0, 1)),
    state                      TEXT NOT NULL CHECK (state IN ('queued', 'running', 'done', 'attention', 'failed')),
    message                    TEXT,
    created_by_profile_id      BLOB REFERENCES profiles(id) ON DELETE SET NULL,
    created_at                 TEXT NOT NULL,
    updated_at                 TEXT NOT NULL
);
CREATE INDEX ix_vault_moves_state ON vault_moves(state);

CREATE TABLE vault_move_items (
    id             BLOB NOT NULL PRIMARY KEY,
    move_id        BLOB NOT NULL REFERENCES vault_moves(id) ON DELETE CASCADE,
    position       INTEGER NOT NULL,
    work_id        BLOB,
    asset_id       BLOB,
    new_asset_id   BLOB,
    source_path    TEXT NOT NULL,
    final_name     TEXT NOT NULL,
    size_bytes     INTEGER NOT NULL DEFAULT 0,
    content_hash   TEXT,
    files_json     TEXT,
    snapshot_json  TEXT,
    original_left  INTEGER NOT NULL DEFAULT 0 CHECK (original_left IN (0, 1)),
    state          TEXT NOT NULL CHECK (state IN ('planned', 'copied', 'retired', 'published', 'done', 'skipped', 'failed')),
    reason         TEXT,
    updated_at     TEXT NOT NULL
);
CREATE INDEX ix_vault_move_items_move ON vault_move_items(move_id, state);
