-- "Change what this folder is" (Library setup spec D40(c), D60, D61, D69, WP7b-3). The plan of one change of a folder's kind, written before
-- anything changes so a job stopped half way carries on from the same plan: which folder, which library and kind it came from and goes to,
-- the media type its files are filed under afterwards, and whether the new kind is plain (its files drop the details looked up for them) or
-- looks details up (a new first check starts for the folder). The per-file progress is the folder job's ledger (folder_organise_log).
-- One row per change job (the media operation that runs it). config_applied_at is set once the folder has moved to the new library in
-- libraries.json and the first checks are settled, so that step is never repeated. Pre-release, so nothing is carried across.
CREATE TABLE folder_kind_changes (
    operation_id      BLOB NOT NULL PRIMARY KEY,
    source_id         BLOB NOT NULL,
    from_library_id   TEXT NOT NULL,
    to_library_id     TEXT NOT NULL,
    from_kind_key     TEXT NOT NULL,
    to_kind_key       TEXT NOT NULL,
    target_media_type TEXT NOT NULL,
    target_is_plain   INTEGER NOT NULL CHECK (target_is_plain IN (0, 1)),
    starts_first_check INTEGER NOT NULL CHECK (starts_first_check IN (0, 1)),
    started_by_profile_id BLOB,                -- the administrator's profile that started the change (nullable); the activity entry names them
    repairs_operation_id BLOB,                 -- the earlier change of this folder whose failed files this one retries (nullable)
    created_at        TEXT NOT NULL,           -- ISO 8601 UTC
    config_applied_at TEXT                     -- ISO 8601 UTC; null until the folder has moved to the new library
);

CREATE INDEX idx_folder_kind_changes_source ON folder_kind_changes (source_id);
