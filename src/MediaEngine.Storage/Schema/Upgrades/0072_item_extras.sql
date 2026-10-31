-- Extras (Extras spec WP1). A file beside a title that is not one of its episodes, tracks or issues (a PDF guidebook, a picture folder,
-- a behind-the-scenes clip) is kept here as an Extra of the folder it sits in. It never becomes a Work, Edition or Media Asset, so it
-- cannot change an episode count, next-up or progress. The row belongs to the item's folder (item_folder), not to a Work, so it stays
-- with the title if it is matched or re-matched later. origin says who decided: the rule, or a person in the editor (WP4).
-- target_season and target_episode are set only on a user_episode row: where the person put the file (WP4), read when the file is ingested.
-- Pre-release rule: no conversion of anything older.
CREATE TABLE item_extras (
    id               BLOB NOT NULL PRIMARY KEY,
    library_id       TEXT NOT NULL,
    item_folder      TEXT NOT NULL COLLATE NOCASE,
    file_path        TEXT NOT NULL COLLATE NOCASE UNIQUE,
    kind             TEXT NOT NULL CHECK (kind IN ('document', 'picture', 'clip')),
    origin           TEXT NOT NULL DEFAULT 'rule' CHECK (origin IN ('rule', 'user_extra', 'user_episode')),
    title            TEXT NOT NULL,
    group_key        TEXT,
    sort_order       INTEGER NOT NULL DEFAULT 0,
    size_bytes       INTEGER NOT NULL DEFAULT 0,
    duration_seconds REAL,
    page_count       INTEGER,
    target_season    INTEGER,
    target_episode   INTEGER,
    found_at         TEXT NOT NULL
);
CREATE INDEX ix_item_extras_folder ON item_extras (item_folder);
