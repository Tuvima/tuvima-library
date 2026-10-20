-- "Not matched" marks for items that were moved into a place with a known list (an album's tracks, a season's episodes, a
-- series' positions, an audiobook's parts) with "Add anyway", so they sit there without a number and without details
-- borrowed from any entry (Details overlay spec, D13). A later Match to a real entry clears the mark.
-- works.placement_unmatched is the mark for a title; editions.placement_unmatched is the mark for an audiobook part,
-- because a part is one file (one Edition) of its audiobook's recording Work.
-- 0 = placed normally (every existing row), 1 = added anyway.

ALTER TABLE works ADD COLUMN placement_unmatched INTEGER NOT NULL DEFAULT 0 CHECK (placement_unmatched IN (0, 1));

ALTER TABLE editions ADD COLUMN placement_unmatched INTEGER NOT NULL DEFAULT 0 CHECK (placement_unmatched IN (0, 1));
