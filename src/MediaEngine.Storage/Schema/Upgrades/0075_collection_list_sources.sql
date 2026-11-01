-- Imported lists (Collections and lists spec WP5, D5 and D6). A collection made from a list keeps where the list came from
-- (collection_list_sources: kind, file name or address, refresh choice, sync mode, order, last refresh, counts) and every
-- entry of the list (collection_list_entries: position, title, year, identifiers, the library title it matched and its status).
-- source_kind has no CHECK on purpose: web sources (later packets) add their own kinds.
-- collection_items.added_by says who put a title in a collection: 'hand' (a person) or 'list' (an imported list). A refresh
-- only ever adds or removes 'list' rows, so it never touches a hand-added title.
-- Pre-release rule: no conversion of anything older.
CREATE TABLE collection_list_sources (
    collection_id     BLOB NOT NULL PRIMARY KEY REFERENCES collections(id) ON DELETE CASCADE,
    source_kind       TEXT NOT NULL,
    source_label      TEXT NOT NULL,
    source_name       TEXT NOT NULL DEFAULT '',
    source_address    TEXT,
    shelf             TEXT,
    order_by          TEXT NOT NULL DEFAULT 'list' CHECK (order_by IN ('list', 'release', 'title')),
    refresh           TEXT NOT NULL DEFAULT 'never' CHECK (refresh IN ('never', 'daily', 'weekly')),
    sync_mode         TEXT NOT NULL DEFAULT 'follow' CHECK (sync_mode IN ('follow', 'keep')),
    last_refreshed_at TEXT,
    next_refresh_at   TEXT,
    entry_count       INTEGER NOT NULL DEFAULT 0,
    matched_count     INTEGER NOT NULL DEFAULT 0,
    unmatched_count   INTEGER NOT NULL DEFAULT 0,
    created_at        TEXT NOT NULL,
    updated_at        TEXT NOT NULL
);

CREATE TABLE collection_list_entries (
    id              BLOB NOT NULL PRIMARY KEY,
    collection_id   BLOB NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
    position        INTEGER NOT NULL CHECK (position >= 1),
    title           TEXT NOT NULL,
    year            INTEGER,
    creator         TEXT,
    shelf           TEXT,
    ids_json        TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(ids_json)),
    media_types     TEXT,
    entry_key       TEXT NOT NULL,
    status          TEXT NOT NULL CHECK (status IN ('matched', 'unmatched', 'ignored', 'hand_fixed')),
    matched_work_id BLOB REFERENCES works(id) ON DELETE SET NULL,
    match_basis     TEXT,
    on_list         INTEGER NOT NULL DEFAULT 1 CHECK (on_list IN (0, 1))
);

CREATE INDEX idx_collection_list_entries_collection ON collection_list_entries(collection_id, position);
CREATE INDEX idx_collection_list_sources_due ON collection_list_sources(next_refresh_at) WHERE refresh <> 'never';

ALTER TABLE collection_items ADD COLUMN added_by TEXT NOT NULL DEFAULT 'hand' CHECK (added_by IN ('hand', 'list'));
