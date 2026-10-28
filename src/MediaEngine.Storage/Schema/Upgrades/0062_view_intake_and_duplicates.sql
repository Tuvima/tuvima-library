-- Photo and video filing (spec photo-video-filing, D1, D6, D15, D16): every file that waits in Library's Import area, or was refused into
-- the Rejected area, has a row here, so nothing in those folders is ever only a file on disk. The same row is the filing journal: a file
-- that is being moved has state 'filing' and a target_path, so a stop in the middle is finished or put back at the next start.
CREATE TABLE view_intake_files (
    id                  BLOB NOT NULL PRIMARY KEY,
    content_kind        TEXT NOT NULL CHECK (content_kind IN ('photos', 'personal_videos')),
    scope_kind          TEXT NOT NULL CHECK (scope_kind IN ('personal', 'shared')),
    owner_profile_id    BLOB REFERENCES profiles(id) ON DELETE SET NULL, -- whose space the file is filed into; null for the Shared Library
    added_by_profile_id BLOB REFERENCES profiles(id) ON DELETE SET NULL, -- who put it there (the uploader), when known
    original_name       TEXT NOT NULL,
    current_path        TEXT NOT NULL,             -- where the file is now: in the Import area or the Rejected area
    target_path         TEXT,                      -- while state = 'filing': where it is being moved to
    filed_path          TEXT,                      -- once filed: where it ended up
    group_key           TEXT,                      -- files filed together (RAW + JPEG + sidecar, Live Photo + video) share a key
    group_role          TEXT,                      -- the file's role in its group (primary, raw, jpeg, sidecar, live_photo_video)
    byte_size           INTEGER NOT NULL DEFAULT 0 CHECK (byte_size >= 0),
    content_hash        TEXT,
    state               TEXT NOT NULL CHECK (state IN ('waiting', 'filing', 'filed', 'duplicate', 'rejected', 'stray')),
    reason_code         TEXT,                      -- why a file was refused (damaged, empty, unsupported_type, ...)
    reason_text         TEXT,                      -- the same reason in plain words
    created_at          TEXT NOT NULL,             -- ISO 8601 UTC
    updated_at          TEXT NOT NULL,
    expires_at          TEXT                       -- a refused file is removed automatically after this time
);

CREATE INDEX ix_view_intake_files_state ON view_intake_files(state, content_kind);
CREATE UNIQUE INDEX ux_view_intake_files_open_path ON view_intake_files(current_path COLLATE NOCASE)
    WHERE state IN ('waiting', 'filing', 'rejected', 'stray');

-- Duplicate groups the cleanup job found (D16): exact copies already inside one space or the Shared Library. The members are read live
-- from the file locations, so this row only remembers that the group exists and whether an administrator dismissed it.
CREATE TABLE view_duplicate_groups (
    id                  BLOB NOT NULL PRIMARY KEY,
    scope_kind          TEXT NOT NULL CHECK (scope_kind IN ('personal', 'shared')),
    library_id          BLOB NOT NULL,             -- the space's or the Shared Library's library id
    content_hash        TEXT NOT NULL COLLATE NOCASE,
    copy_count          INTEGER NOT NULL CHECK (copy_count >= 2),
    state               TEXT NOT NULL CHECK (state IN ('open', 'dismissed')),
    first_found_at      TEXT NOT NULL,
    last_seen_at        TEXT NOT NULL,
    dismissed_at        TEXT,
    dismissed_by        BLOB,
    UNIQUE (library_id, content_hash)
);

CREATE INDEX ix_view_duplicate_groups_state ON view_duplicate_groups(scope_kind, state);
