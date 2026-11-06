-- Library activity (spec library-activity, WP1). Migration 0018 removed the old batch tables, so each file was tracked alone and the Engine
-- could not say "these 14 files were one run". A run is one scan, refresh, fix or removal in one library; its items are the titles it
-- touched. History on the Activity page reads these two tables, and the "New" tag rule (D14) reads is_first_run and ended_at.
-- Pre-release rule: no back-fill of older imports, no conversion of anything older.
CREATE TABLE activity_runs (
    id               BLOB NOT NULL PRIMARY KEY,
    library_id       TEXT NOT NULL,              -- the configured library's id, lowercase with dashes
    kind             TEXT NOT NULL CHECK (kind IN ('added', 'updated', 'fixed', 'removed')),
    status           TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'completed', 'failed')),
    is_first_run     INTEGER NOT NULL DEFAULT 0, -- the library's first run: its initial load never marks anything New
    started_at       TEXT NOT NULL,              -- ISO 8601 UTC
    last_activity_at TEXT NOT NULL,              -- when the newest item joined; a run closes after 5 quiet minutes
    ended_at         TEXT,                       -- set when the run completes; the true end is the last item
    item_count       INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX idx_activity_runs_library_started ON activity_runs (library_id, started_at DESC);
CREATE INDEX idx_activity_runs_status ON activity_runs (status, last_activity_at);
CREATE INDEX idx_activity_runs_started ON activity_runs (started_at DESC);

CREATE TABLE activity_run_items (
    id            BLOB NOT NULL PRIMARY KEY,
    run_id        BLOB NOT NULL REFERENCES activity_runs(id) ON DELETE CASCADE,
    work_id       BLOB,                          -- the Work that was added (null for a removed file)
    asset_id      BLOB,
    media_type    TEXT NOT NULL DEFAULT '',      -- Movies, TV, Music, Books, Audiobooks, Comics, Videos
    title         TEXT NOT NULL,
    group_key     TEXT NOT NULL,                 -- the stacking rule (D12): one tile per show, album, folder or title
    group_title   TEXT NOT NULL,
    group_work_id BLOB,                          -- the show or album a tile opens, when there is one
    created_at    TEXT NOT NULL
);

CREATE INDEX idx_activity_run_items_run ON activity_run_items (run_id, group_key);
CREATE INDEX idx_activity_run_items_title ON activity_run_items (title COLLATE NOCASE);
CREATE INDEX idx_activity_run_items_group_title ON activity_run_items (group_title COLLATE NOCASE);
CREATE INDEX idx_activity_run_items_work ON activity_run_items (work_id);
