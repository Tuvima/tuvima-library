-- Plugin host services: the data a plugin's own host services keep. plugin_records holds the records a plugin stores for
-- itself (one row per plugin, collection and key; the value is a JSON document the plugin wrote). Every read and write is
-- scoped to the calling plugin's id by the Library, so one plugin never sees another's records. The Library limits each
-- plugin to 64 KB per value, 10,000 records and 16 MB in total. plugin_event_cursors remembers, per plugin, the last
-- Library event (application_events.sequence) already handed to it, so a restart carries on where it stopped and a plugin
-- never receives the same event twice. No plugin data leaves this machine through either table.
CREATE TABLE plugin_records (
    plugin_id    TEXT    NOT NULL,   -- the plugin's manifest id
    collection   TEXT    NOT NULL,   -- the plugin's own grouping name, 1 to 128 printable characters
    record_key   TEXT    NOT NULL,   -- the key inside the collection, 1 to 128 printable characters
    value_json   TEXT    NOT NULL,   -- the record, a JSON document, at most 64 KB
    size_bytes   INTEGER NOT NULL,   -- UTF-8 size of value_json, so a plugin's total is a cheap sum
    updated_at   TEXT    NOT NULL,   -- ISO 8601 UTC
    PRIMARY KEY (plugin_id, collection, record_key)
);

CREATE TABLE plugin_event_cursors (
    plugin_id      TEXT    NOT NULL PRIMARY KEY,   -- the plugin's manifest id
    last_sequence  INTEGER NOT NULL,               -- the newest application_events.sequence already delivered
    updated_at     TEXT    NOT NULL                -- ISO 8601 UTC
);
