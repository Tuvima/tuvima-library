-- Safer deleting in View (D3, D6, D8). When a file disappears outside Library its place is marked missing (missing_since) instead of
-- being removed; an item reads as Missing (local_items.missing_since) when none of its viewable files has a place left, and part_missing
-- says only a companion (a Live Photo's video, a sidecar) has gone. A folder that cannot be reached marks nothing (folder_reachable).
-- Pre-release rule: no conversion of anything older.
ALTER TABLE local_file_sources ADD COLUMN missing_since TEXT;
ALTER TABLE local_items ADD COLUMN missing_since TEXT;
ALTER TABLE local_items ADD COLUMN part_missing INTEGER NOT NULL DEFAULT 0 CHECK (part_missing IN (0, 1));
ALTER TABLE view_sources ADD COLUMN folder_reachable INTEGER NOT NULL DEFAULT 1 CHECK (folder_reachable IN (0, 1));
CREATE INDEX ix_local_file_sources_missing ON local_file_sources (source_id, missing_since) WHERE missing_since IS NOT NULL;
CREATE INDEX ix_local_items_missing ON local_items (missing_since) WHERE missing_since IS NOT NULL;
