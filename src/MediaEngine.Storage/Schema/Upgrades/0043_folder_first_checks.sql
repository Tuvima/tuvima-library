-- The first check of a newly added folder (Library setup spec D35, D61, WP9b). A folder added to a library whose kind looks details up is
-- checked before the Engine changes anything in it: its first files are identified as normal, but nothing in the folder is renamed, moved
-- or tagged. When most of them are Unmatched the folder is paused (its remaining identity jobs are not claimed and the moves held for it
-- stay held) until an administrator chooses; otherwise the check passes and the held moves and tags are released to the next run.
-- Pre-release, so nothing is carried across from older data stores; a folder that already exists never gets a check.
--
-- One row per folder (source). path is the folder as configured; path_key is the same path with every separator turned into '/', no
-- trailing separator and ASCII letters lower-cased, so a file is "in the folder" when lower(replace(its path, '\', '/')) starts with
-- path_key || '/'. state: 'checking' (the first files are being identified; nothing in the folder is touched), 'passed', 'paused' (most of
-- the tried files are Unmatched; claims stop and nothing is touched) or 'kept_going' (an administrator chose Keep going). tried and
-- unmatched are the live counts while checking and the counts the check ended on afterwards.
CREATE TABLE folder_first_checks (
    source_id  TEXT NOT NULL PRIMARY KEY,
    library_id TEXT NOT NULL,
    path       TEXT NOT NULL,
    path_key   TEXT NOT NULL,
    state      TEXT NOT NULL CHECK (state IN ('checking', 'passed', 'paused', 'kept_going')),
    tried      INTEGER NOT NULL DEFAULT 0 CHECK (tried >= 0),
    unmatched  INTEGER NOT NULL DEFAULT 0 CHECK (unmatched >= 0),
    started_at TEXT NOT NULL,
    ended_at   TEXT
);

-- Every claim of an identity job or a file write asks which folders are held, so the held ones are indexed on their own (the rows that are
-- no longer held stay behind as history and cost nothing).
CREATE INDEX ix_folder_first_checks_held ON folder_first_checks (state, path_key) WHERE state IN ('checking', 'paused');
CREATE INDEX ix_folder_first_checks_library ON folder_first_checks (library_id);

-- The files that have reached a final identification outcome while their folder was being checked, in the order they did. A file counts
-- once; a later outcome for it (a retried lookup) replaces the earlier one. The rows go when the check ends.
CREATE TABLE folder_first_check_files (
    seq         INTEGER PRIMARY KEY AUTOINCREMENT,
    source_id   TEXT NOT NULL REFERENCES folder_first_checks(source_id) ON DELETE CASCADE,
    asset_id    BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    outcome     TEXT NOT NULL CHECK (outcome IN ('matched', 'unmatched', 'needs_review')),
    recorded_at TEXT NOT NULL,
    UNIQUE (source_id, asset_id)
);
