-- Bookmarks for videos and audiobooks (Videos spec WP15, D43, D43a). The table keeps its name: a saved spot in a video or an
-- audiobook is the same kind of row, and the existing `note` column stays the short note (up to 200 characters), so every bookmark
-- already saved keeps its text and stays personal.
--
-- description: the optional longer description (up to 4,000 characters, enforced by the Engine).
-- is_global:   1 for a bookmark shared with everybody who can see the title; only a server administrator can make or change one.
--              profile_id then records who made it. 0 for a personal bookmark that belongs to profile_id.
ALTER TABLE audiobook_bookmarks ADD COLUMN description TEXT;
ALTER TABLE audiobook_bookmarks ADD COLUMN is_global INTEGER NOT NULL DEFAULT 0 CHECK (is_global IN (0, 1));

CREATE INDEX idx_audiobook_bookmarks_global_work
    ON audiobook_bookmarks(work_id, created_at DESC)
    WHERE is_global = 1;
