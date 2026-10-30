-- Recycle bin keeps people's progress (delete groups D21). While a deleted file waits in the Recycle bin, every profile's progress,
-- listening history, bookmarks and highlights on it are kept here, keyed by the file's fingerprint (content_hash), because the records
-- themselves leave with the title. When the same file comes back (restored, then found by a scan) the rows are put back on the new copy.
-- row_json holds one row of the named table. Pre-release rule: no conversion of anything older.
CREATE TABLE recycle_bin_personal_state (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    content_hash TEXT NOT NULL,
    table_name   TEXT NOT NULL,
    row_json     TEXT NOT NULL,
    kept_at      TEXT NOT NULL
);
CREATE INDEX idx_recycle_bin_personal_state_hash ON recycle_bin_personal_state (content_hash);
