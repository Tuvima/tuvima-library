-- The first check of a newly added folder (Library setup spec D35, D61, WP9b). A folder added to a library whose kind looks details up is
-- checked before the Engine changes anything in it: its first files are identified as normal, but nothing in the folder is renamed, moved
-- or tagged. When most of them are Unmatched the folder is paused (its remaining identity jobs are not claimed and the moves held for it
-- stay held) until an administrator chooses; otherwise the check passes and the held moves and tags are released to the next run.
-- Pre-release, so nothing is carried across from older data stores; a folder that already exists never gets a check.
--
-- One row per folder (source). path is the folder as configured. path_prefix is the same path with every separator turned into '/' and
-- exactly one '/' at the end (a root such as '/' or 'D:/' keeps its one separator, so a prefix is never empty and never matches every path);
-- path_prefix_folded is that prefix with ASCII letters lower-cased and path_range_end_folded the first string after every string that starts
-- with it (the closing '/' replaced by '0'). A file is "in the folder" when lower(replace(its path, '\', '/')) is at or above
-- path_prefix_folded and below path_range_end_folded, which ix_media_assets_path_folded answers as a range; outside Windows the Engine
-- also requires the case-sensitive prefix to match (path_prefix), as the rest of the Engine compares source paths.
-- state: 'checking' (the first files are being identified; nothing in the folder is touched), 'passed', 'paused' (most of the tried files are
-- Unmatched; claims stop and nothing is touched) or 'kept_going' (an administrator chose Keep going). tried and unmatched are the live
-- counts while checking and the counts the check ended on afterwards.
CREATE TABLE folder_first_checks (
    source_id            TEXT NOT NULL PRIMARY KEY,
    library_id           TEXT NOT NULL,
    path                 TEXT NOT NULL,
    path_prefix          TEXT NOT NULL CHECK (length(path_prefix) >= 1),
    path_prefix_folded   TEXT NOT NULL,
    path_range_end_folded TEXT NOT NULL,
    state                TEXT NOT NULL CHECK (state IN ('checking', 'passed', 'paused', 'kept_going')),
    tried                INTEGER NOT NULL DEFAULT 0 CHECK (tried >= 0),
    unmatched            INTEGER NOT NULL DEFAULT 0 CHECK (unmatched >= 0),
    started_at           TEXT NOT NULL,
    ended_at             TEXT
);

-- The range lookup of the files in a folder (the first check counts them, releases and drops their held rows, and lists their subtitles).
CREATE INDEX ix_media_assets_path_folded ON media_assets (lower(replace(file_path_root, '\', '/')));

-- Every claim of an identity job or a file write asks which folders are held, so the held ones are indexed on their own (the rows that are
-- no longer held stay behind as history and cost nothing).
CREATE INDEX ix_folder_first_checks_held ON folder_first_checks (state, library_id) WHERE state IN ('checking', 'paused');
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
