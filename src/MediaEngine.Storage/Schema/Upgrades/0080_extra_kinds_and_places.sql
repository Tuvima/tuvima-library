-- tuvima:foreign-keys-off
-- "What is this file?" (universal editor v4, D20). An Extra gains the kind a person gave it (Trailer, Featurette, Behind the scenes,
-- Deleted scene, Interview, Document, Pictures, Other clip) and, optionally, the season, episode or track it belongs to. Both are null on
-- an Extra the rule found: it reads as the default for its file type. Ignoring a file reuses the excluded-files list, so its reason
-- check is widened to allow 'ignored_by_you'; SQLite cannot widen a CHECK in place, so the table is rebuilt with every row kept.
-- Pre-release rule: no conversion of anything older.
ALTER TABLE item_extras ADD COLUMN extra_kind TEXT
    CHECK (extra_kind IS NULL OR extra_kind IN ('trailer', 'featurette', 'behind_the_scenes', 'deleted_scene', 'interview', 'document', 'pictures', 'other_clip'));
ALTER TABLE item_extras ADD COLUMN place_season INTEGER;
ALTER TABLE item_extras ADD COLUMN place_episode INTEGER;
ALTER TABLE item_extras ADD COLUMN place_track INTEGER;

CREATE TABLE excluded_source_files_new (
    id                     BLOB NOT NULL PRIMARY KEY,
    library_id             TEXT NOT NULL,
    source_id              TEXT NOT NULL,
    relative_path          TEXT NOT NULL,
    content_hash           TEXT,
    file_size              INTEGER,
    last_write_utc         TEXT,
    title                  TEXT,
    excluded_at            TEXT NOT NULL,
    excluded_by_account_id BLOB,
    excluded_by_name       TEXT,
    reason                 TEXT CHECK (reason IS NULL OR reason IN ('as_is', 'protected', 'delete_failed', 'ignored_by_you')),
    UNIQUE (source_id, relative_path)
);

INSERT INTO excluded_source_files_new (id, library_id, source_id, relative_path, content_hash, file_size, last_write_utc, title,
                                       excluded_at, excluded_by_account_id, excluded_by_name, reason)
SELECT id, library_id, source_id, relative_path, content_hash, file_size, last_write_utc, title,
       excluded_at, excluded_by_account_id, excluded_by_name, reason
FROM excluded_source_files;

DROP TABLE excluded_source_files;

ALTER TABLE excluded_source_files_new RENAME TO excluded_source_files;

CREATE INDEX idx_excluded_source_files_library ON excluded_source_files(library_id);
