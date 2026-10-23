-- Unmatched files (Library setup spec D18, D34, D40). A file the Engine cannot match stays exactly where it is and is marked Unmatched
-- instead of becoming a Needs Review item: it still plays and browses with its file name and tags, and is looked up again later without
-- anyone doing anything. One row per media asset that is Unmatched now; the row is removed when a lookup finally matches (or the file
-- is removed). Pre-release, so nothing is carried across from older data stores.
--
-- reason: why there was no match. 'no_match' is a lookup that found nothing; 'no_youtube_id' is a file in a YouTube library with no
-- video ID in its name or .info.json (it is never looked up). first_unmatched_at is when it first became Unmatched, last_tried_at when
-- a lookup last ran, next_retry_at when the schedule (or a provider or plugin change, or Retry now) makes it due again. kept_as_is = 1
-- stops its retries and takes it out of the Unmatched count and filter; kept_at records when. "Look up again" clears the mark.
CREATE TABLE unmatched_assets (
    asset_id BLOB NOT NULL PRIMARY KEY REFERENCES media_assets(id) ON DELETE CASCADE,
    library_id TEXT NOT NULL,
    reason TEXT NOT NULL CHECK (reason IN ('no_match', 'no_youtube_id')),
    first_unmatched_at TEXT NOT NULL,
    last_tried_at TEXT NOT NULL,
    next_retry_at TEXT,
    kept_as_is INTEGER NOT NULL DEFAULT 0 CHECK (kept_as_is IN (0, 1)),
    kept_at TEXT
);

-- The per-library count and list (kept items are filtered out of the count).
CREATE INDEX ix_unmatched_assets_library ON unmatched_assets (library_id, kept_as_is, first_unmatched_at);

-- The scan for items that are due a retry (kept items are never retried, so they are not in the index).
CREATE INDEX ix_unmatched_assets_due ON unmatched_assets (next_retry_at) WHERE kept_as_is = 0;
