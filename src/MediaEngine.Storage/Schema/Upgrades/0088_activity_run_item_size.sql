-- Library activity (review mock-ups of 2026-11-18, D3): the batch list shows each item's size, so the size on disk is kept when the item
-- joins a batch (0 when the file was not there to measure, such as a removal). Pre-release rule: no back-fill.
ALTER TABLE activity_run_items ADD COLUMN size_bytes INTEGER NOT NULL DEFAULT 0;
