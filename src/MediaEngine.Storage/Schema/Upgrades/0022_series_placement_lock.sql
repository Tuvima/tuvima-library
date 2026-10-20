-- A person's Series choice is a lock. When a person picks a Series for a Work in Matching (or picks "not in a Series"),
-- works.collection_id is their choice and series_placement_locked is 1: nothing automatic (collection assignment, manifest
-- linking, backfill) may change it. A locked Work whose collection_id is NULL was deliberately placed in no Series.
ALTER TABLE works ADD COLUMN series_placement_locked INTEGER NOT NULL DEFAULT 0;
