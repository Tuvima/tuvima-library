-- A household's Shared Library row could never be deleted, so a household with an empty Shared Library could never be removed.
-- The row may now go while nothing has been shared into it; with shared photos or videos in it, deleting is still refused.
DROP TRIGGER trg_view_shared_library_delete;

CREATE TRIGGER trg_view_shared_library_delete
BEFORE DELETE ON view_shared_library
WHEN EXISTS (SELECT 1 FROM local_items WHERE scope_kind='shared' AND library_id=OLD.library_id)
BEGIN SELECT RAISE(ABORT,'Shared library identity cannot be deleted while it holds items'); END;
