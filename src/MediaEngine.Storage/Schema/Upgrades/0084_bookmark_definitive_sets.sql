-- Definitive bookmark sets (universal editor v5, WP15, D37). A title's Everyone bookmarks are, until an administrator curates them in the
-- editor's Bookmarks tab, every profile's bookmarks on the title pooled together (same-spot ones merged). The first change an administrator
-- saves there makes the set definitive: one row here for the title (its work), and from then on Everyone is exactly the bookmarks flagged
-- is_global on it. "Start over from profiles" deletes the row and clears the flags, so Everyone pools again. A bookmark is never copied: the
-- flag lives on the owner's own row (audiobook_bookmarks.is_global), so the owner still sees it under Mine.
-- Pre-release rule: no conversion of anything older; a title with is_global bookmarks and no row here simply reads as pooled.
CREATE TABLE bookmark_definitive_sets (
    work_id  BLOB NOT NULL PRIMARY KEY REFERENCES works(id) ON DELETE CASCADE,
    made_at  TEXT NOT NULL,
    made_by  BLOB REFERENCES profiles(id) ON DELETE SET NULL
);
