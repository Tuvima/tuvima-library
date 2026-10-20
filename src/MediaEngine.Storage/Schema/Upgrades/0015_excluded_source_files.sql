-- Excluded source files: media files that stayed on disk when their title was deleted from the library (a file in an as-is
-- folder, a merge original, or a file that could not be removed). Scans skip an excluded file so the title does not come
-- back; Settings lists them and "Add back" removes the row. A file that changes (a different hash, size or modified time at
-- the same place) is a different file and is scanned again.
-- library_id and source_id are the configured library and source folder ids (text, as in media_assets.library_id), so there
-- are no foreign keys: the folders live in configuration, and the row must outlive the deleted title. relative_path is the
-- file's place inside the source folder with forward slashes. title, excluded_by_name and the file facts are snapshots for the
-- Settings list.
CREATE TABLE excluded_source_files (
    id                     BLOB NOT NULL PRIMARY KEY,
    library_id             TEXT NOT NULL,
    source_id              TEXT NOT NULL,
    relative_path          TEXT NOT NULL,
    content_hash           TEXT,                -- SHA-256 hex of the deleted title's media asset
    file_size              INTEGER,
    last_write_utc         TEXT,                -- ISO 8601 UTC
    title                  TEXT,
    excluded_at            TEXT NOT NULL,       -- ISO 8601 UTC
    excluded_by_account_id BLOB,
    excluded_by_name       TEXT,
    reason                 TEXT CHECK (reason IS NULL OR reason IN ('as_is', 'protected', 'delete_failed')),
    UNIQUE (source_id, relative_path)
);

CREATE INDEX idx_excluded_source_files_library ON excluded_source_files(library_id);
