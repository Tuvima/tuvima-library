-- Library activity (Shy, 10 Nov 2026): a scan that covers several libraries is ONE History entry. Each library still has its own run (its
-- first-run flag and the library chips depend on that); runs that open in the same scan share a scan_id, and History groups by it.
-- A run with no scan_id is its own scan. Pre-release rule: no back-fill.
ALTER TABLE activity_runs ADD COLUMN scan_id BLOB;

CREATE INDEX idx_activity_runs_scan ON activity_runs (scan_id);
