-- Vault lookup, validation and Identify (Videos spec WP12). What the Library knows about how each Vault video was matched, the
-- fingerprints it computed on this machine, and the quiet Vault activity list. Every table here holds Vault files only: the data
-- structure itself refuses a row for any other file, so a code path that forgets the rule cannot write one.

-- The record a Vault video was matched to (D30, D37). A Vault video with no row here is Unmatched (D32): nothing is queued for it.
-- matched_by is the signal that decided the match; applied_via says whether the Library applied it by itself or a person did in Identify (D38).
CREATE TABLE vault_matches (
    asset_id    BLOB NOT NULL PRIMARY KEY REFERENCES media_assets(id) ON DELETE CASCADE,
    server_id   TEXT NOT NULL,
    record_id   TEXT NOT NULL,
    matched_by  TEXT NOT NULL CHECK (matched_by IN ('fingerprint', 'product_code', 'title', 'identify')),
    applied_via TEXT NOT NULL CHECK (applied_via IN ('automatic', 'identify')),
    matched_at  TEXT NOT NULL
);

-- Where each field of a Vault video came from (D30): which server, which record, how it was matched, when, and the language noted
-- for the text. One row per field; a later Identify replaces the rows of the fields a person ticked.
CREATE TABLE vault_field_sources (
    asset_id    BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    field       TEXT NOT NULL CHECK (field IN ('title', 'description', 'release_date', 'cover', 'studio', 'performers', 'tags')),
    server_id   TEXT NOT NULL,
    record_id   TEXT NOT NULL,
    matched_by  TEXT NOT NULL CHECK (matched_by IN ('fingerprint', 'product_code', 'title', 'identify')),
    applied_via TEXT NOT NULL CHECK (applied_via IN ('automatic', 'identify')),
    language    TEXT,
    applied_at  TEXT NOT NULL,
    PRIMARY KEY (asset_id, field)
);

-- Fingerprints computed on this machine (D35), kept so a big file is not read again. They are sent only with the separate
-- fingerprint permission; a stored fingerprint is never sent on its own.
CREATE TABLE vault_file_fingerprints (
    asset_id    BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    algorithm   TEXT NOT NULL CHECK (algorithm IN ('OSHASH', 'MD5', 'PHASH')),
    hash        TEXT NOT NULL,
    file_size   INTEGER NOT NULL,
    computed_at TEXT NOT NULL,
    PRIMARY KEY (asset_id, algorithm)
);

-- The quiet Vault activity list (D32). Plain words only: a line never carries a title or a path, only the file's own name, and it
-- goes with the file. Nothing here reaches Needs Review, notifications or the live ingestion page.
CREATE TABLE vault_activity (
    id         BLOB NOT NULL PRIMARY KEY,
    asset_id   BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    kind       TEXT NOT NULL CHECK (kind IN ('added', 'matched', 'unmatched', 'identified', 'failed')),
    file_name  TEXT NOT NULL,
    detail     TEXT NOT NULL,
    created_at TEXT NOT NULL
);
CREATE INDEX idx_vault_activity_created ON vault_activity(created_at DESC);

CREATE TRIGGER trg_vault_matches_vault_only
BEFORE INSERT ON vault_matches
WHEN NOT EXISTS (
    SELECT 1 FROM media_assets ma JOIN vault_libraries vl ON vl.library_id = ma.library_id COLLATE NOCASE WHERE ma.id = NEW.asset_id)
BEGIN SELECT RAISE(ABORT, 'Only a Vault file can have a Vault match.'); END;

CREATE TRIGGER trg_vault_field_sources_vault_only
BEFORE INSERT ON vault_field_sources
WHEN NOT EXISTS (
    SELECT 1 FROM media_assets ma JOIN vault_libraries vl ON vl.library_id = ma.library_id COLLATE NOCASE WHERE ma.id = NEW.asset_id)
BEGIN SELECT RAISE(ABORT, 'Only a Vault file can have Vault field sources.'); END;

CREATE TRIGGER trg_vault_file_fingerprints_vault_only
BEFORE INSERT ON vault_file_fingerprints
WHEN NOT EXISTS (
    SELECT 1 FROM media_assets ma JOIN vault_libraries vl ON vl.library_id = ma.library_id COLLATE NOCASE WHERE ma.id = NEW.asset_id)
BEGIN SELECT RAISE(ABORT, 'Only a Vault file can have Vault fingerprints.'); END;

CREATE TRIGGER trg_vault_activity_vault_only
BEFORE INSERT ON vault_activity
WHEN NOT EXISTS (
    SELECT 1 FROM media_assets ma JOIN vault_libraries vl ON vl.library_id = ma.library_id COLLATE NOCASE WHERE ma.id = NEW.asset_id)
BEGIN SELECT RAISE(ABORT, 'Only a Vault file can have Vault activity.'); END;
