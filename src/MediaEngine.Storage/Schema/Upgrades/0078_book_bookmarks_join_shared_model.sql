-- Book bookmarks join the one bookmark model (Global bookmarks spec D13). A bookmark now says what kind of position it holds:
-- 'time' (seconds into a film, episode or audiobook), 'text' (a saved reader location in an EPUB) or 'page' (a page of a PDF or comic,
-- stage 3). A text bookmark keeps its reader location in locator and its chapter in chapter_index; the same Global flag, Promote,
-- Find bookmarks and compare then work for books exactly as for films. Existing reader bookmarks move across so their owners still
-- see them; the old table is dropped.
ALTER TABLE audiobook_bookmarks ADD COLUMN position_kind TEXT NOT NULL DEFAULT 'time' CHECK (position_kind IN ('time', 'text', 'page'));
ALTER TABLE audiobook_bookmarks ADD COLUMN locator TEXT;

INSERT INTO audiobook_bookmarks (id, profile_id, work_id, asset_id, chapter_index, label, created_at, position_kind, locator)
SELECT rb.id, p.id, e.work_id, rb.asset_id, rb.chapter_index, rb.label, rb.created_at, 'text', rb.cfi_position
FROM reader_bookmarks rb
JOIN media_assets ma ON ma.id = rb.asset_id
JOIN editions e ON e.id = ma.edition_id
JOIN profiles p ON lower(hex(p.id)) = lower(replace(rb.user_id, '-', ''))
WHERE rb.cfi_position LIKE 'epubcfi(%';

DROP TABLE reader_bookmarks;
