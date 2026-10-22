-- Vault bookmarks waiting for their video (Videos spec WP17, backups).
-- A backup never holds the Vault catalogue, so a person's Vault bookmarks are kept in the backup as the way to recognise the title (its
-- canonical IDs, running time, edition and exact file fingerprints) plus the bookmark itself. After a restore and a rescan the Engine
-- re-links each one to the video that matches and removes the row. A row whose video never turns up stays and is counted for the
-- Vault settings page. The table is empty on a store that was never restored.
--
-- identity_json: {"ids":{...},"running_time_seconds":n,"edition":"...","content_hash":"...","fingerprints":["..."]}.
-- profile_id: the person who made the bookmark (a shared one keeps the administrator who made it).
CREATE TABLE vault_pending_bookmarks (
    id                    BLOB NOT NULL PRIMARY KEY,
    profile_id            BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    is_global             INTEGER NOT NULL DEFAULT 0 CHECK (is_global IN (0, 1)),
    identity_json         TEXT NOT NULL CHECK (json_valid(identity_json)),
    position_seconds      REAL NOT NULL DEFAULT 0.0,
    end_seconds           REAL CHECK (end_seconds IS NULL OR end_seconds > position_seconds),
    duration_seconds      REAL,
    chapter_index         INTEGER,
    chapter_title         TEXT,
    chapter_start_seconds REAL,
    label                 TEXT,
    note                  TEXT,
    description           TEXT,
    created_at            TEXT NOT NULL
);

CREATE INDEX idx_vault_pending_bookmarks_profile ON vault_pending_bookmarks(profile_id);
