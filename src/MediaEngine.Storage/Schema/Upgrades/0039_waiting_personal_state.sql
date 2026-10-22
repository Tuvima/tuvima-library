-- State waiting for its title after a restore (backup and restore spec D3 to D5, D13).
-- A backup keeps a person's own state (bookmarks, positions, favourites) and their own corrections against how to recognise the title
-- (its external ids, running time, edition, file fingerprints), never the catalogue. A restore puts settings straight back and parks every
-- row that points at a title here. After each ingestion the Engine looks the titles up again; a row whose titles are all found exactly once
-- is written back and removed, and a row that has waited longer than the waiting period is cleared. Nothing is guessed: a row that matches more than one title keeps waiting.
--
-- backup_restores: one row per restore, with counts for the one-line summary. Rows put straight into place are applied_rows; rows that
-- started waiting are waiting_rows; attached_rows, yielded_rows and purged_rows grow as the waiting area empties. A row yields when the
-- person made the same row in the Library since the restore: theirs stays, the waiting one is removed and it is not counted as attached.
CREATE TABLE backup_restores (
    id                BLOB NOT NULL PRIMARY KEY,
    restored_at       TEXT NOT NULL,
    backup_file_name  TEXT NOT NULL,
    backup_created_at TEXT,
    applied_rows      INTEGER NOT NULL,
    waiting_rows      INTEGER NOT NULL,
    attached_rows     INTEGER NOT NULL DEFAULT 0,
    purged_rows       INTEGER NOT NULL DEFAULT 0,
    yielded_rows      INTEGER NOT NULL DEFAULT 0
);

-- waiting_personal_state: one parked row.
-- group_kind: what the row is (settings, a person's state, a typed correction, or a choice on a catalogue row).
-- table_name / row_json: the table it belongs to and its columns and values in the backup's value encoding, with every reference to a
--   title replaced by the index of that title in titles_json. Rows of tables that hang off this row (for example the items of a bookmark
--   pack) travel inside row_json and come back with it.
-- titles_json: the recognition data (ids, running time, edition, file fingerprints, name and year) of the titles the row refers to.
-- profile_id: whose state it is, when the table has an owner column. is_withheld: a title it refers to is one a backup may not name.
-- created_at: when the row started waiting. The waiting period (the missing-file retention setting) is read when rows are cleared, so
--   changing the setting affects rows already waiting. A row carried back by a later backup keeps its own created_at.
CREATE TABLE waiting_personal_state (
    id              BLOB NOT NULL PRIMARY KEY,
    restore_id      BLOB NOT NULL REFERENCES backup_restores(id) ON DELETE CASCADE,
    group_kind      TEXT NOT NULL CHECK (group_kind IN ('configuration', 'personal', 'correction', 'choice')),
    table_name      TEXT NOT NULL,
    profile_id      BLOB,
    is_withheld     INTEGER NOT NULL DEFAULT 0 CHECK (is_withheld IN (0, 1)),
    row_json        TEXT NOT NULL CHECK (json_valid(row_json)),
    titles_json     TEXT NOT NULL CHECK (json_valid(titles_json)),
    created_at      TEXT NOT NULL,
    last_checked_at TEXT
);

CREATE INDEX idx_waiting_personal_state_created ON waiting_personal_state(created_at);
CREATE INDEX idx_waiting_personal_state_profile ON waiting_personal_state(profile_id);

-- The re-attach looks a title up by the Wikidata id of its edition (editions.wikidata_qid); without this it would scan every edition.
CREATE INDEX IF NOT EXISTS idx_editions_wikidata_qid ON editions(wikidata_qid);
