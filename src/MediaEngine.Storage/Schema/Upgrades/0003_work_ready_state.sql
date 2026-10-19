-- Adds the single stored "Ready" mark for leaf Works.
-- works.ready_at is set by the Engine's readiness service the first time a Work is matched, placed
-- in its show or album, and has its artwork present or settled. It is cleared when the Work stops
-- meeting that rule, and it is the only gate for every display surface.
-- Existing rows start with NULL; the readiness service fills them in at startup.

ALTER TABLE works ADD COLUMN ready_at TEXT;

-- The first time the Work was ever Ready. Set once together with the first ready_at and never cleared,
-- so a Work that leaves Ready and returns is not announced as added a second time.
ALTER TABLE works ADD COLUMN first_ready_at TEXT;

CREATE INDEX idx_works_ready_at ON works (ready_at);

-- The readiness service looks for what changed since its last pass: identity jobs by update time, and the three
-- stored artwork values by scoring time. These keep that look-up from scanning the whole table every few seconds.
-- The artwork index is partial; the service's query repeats the same key list so SQLite can use it.
CREATE INDEX idx_identity_jobs_updated_at ON identity_jobs (updated_at);

CREATE INDEX idx_canonical_values_artwork_scored ON canonical_values (last_scored_at)
    WHERE key IN ('cover_state', 'cover_url', 'artwork_settled_at');
