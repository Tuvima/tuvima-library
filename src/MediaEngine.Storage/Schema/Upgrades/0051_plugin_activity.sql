-- Plugin activity: the durable list of what each plugin or source did and every way it failed (Plugin architecture spec D10 and
-- C17). One row per run that changed items, failure, deliberate skip (a Vault item, an audience, a library limit) or note. A failure
-- always carries a cause from one fixed list (reason) so a broken plugin can be told apart from everything else; the detail line is
-- redacted of keys and personal paths before it is written. The Library keeps only the newest rows per plugin. Nothing here leaves
-- this machine. Items in a Vault section are recorded without their id.
CREATE TABLE plugin_activity (
    id             INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    occurred_at    TEXT    NOT NULL,   -- ISO 8601 UTC
    plugin_id      TEXT    NOT NULL,   -- the plugin's manifest id, or the metadata source's name
    plugin_version TEXT    NOT NULL,
    kind           TEXT    NOT NULL CHECK (kind IN ('run', 'failure', 'skipped', 'note')),
    reason         TEXT,               -- the cause of a failure or skip, one of the fixed words (service_unreachable, tool_missing, ...)
    item_id        BLOB,               -- the item it concerned, when there is one and it is not in a Vault section
    items_changed  INTEGER NOT NULL DEFAULT 0,
    summary        TEXT    NOT NULL,   -- one plain sentence
    detail         TEXT                -- a technical line, redacted of keys and personal paths
);

CREATE INDEX idx_plugin_activity_plugin ON plugin_activity (plugin_id, id DESC);
CREATE INDEX idx_plugin_activity_time ON plugin_activity (occurred_at);
