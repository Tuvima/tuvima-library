-- A person's Series choice is a lock. When a person picks a Series for a Work in Matching (or picks "not in a Series"),
-- works.collection_id is their choice and series_placement_locked is 1: nothing automatic (collection assignment, manifest
-- linking, backfill) may change it. A locked Work whose collection_id is NULL was deliberately placed in no Series.
ALTER TABLE works ADD COLUMN series_placement_locked INTEGER NOT NULL DEFAULT 0;

-- A person's choice names a Series shelf; when that shelf is deleted the choice goes with it, so the Library decides again on the
-- next placement instead of the work staying locked to "no Series". Every way a shelf can be deleted passes through this trigger.
-- It runs before the delete so the work still points at the shelf when its lock is cleared.
CREATE TRIGGER trg_works_series_lock_released_with_shelf
BEFORE DELETE ON collections
BEGIN UPDATE works SET series_placement_locked = 0 WHERE collection_id = OLD.id AND series_placement_locked = 1; END;
