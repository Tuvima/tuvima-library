-- The retry queue for plugins that send things to an outside service on a person's behalf (Plugin architecture spec C13 and the
-- sharing plugins in section 6): a play is queued here first, so a service that is offline never loses it and never slows the
-- player; the Library sends it when it can and tries again with a growing pause. Items belong to one profile, go when that profile
-- or the plugin goes, and are not part of a backup (a queued share is worth little after a restore).
CREATE TABLE plugin_outbox (
    id              BLOB NOT NULL PRIMARY KEY,
    plugin_id       TEXT NOT NULL,   -- the plugin's manifest id
    profile_id      BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    payload_json    TEXT NOT NULL,   -- what to send, written by the plugin, at most 64 KB
    created_at      TEXT NOT NULL,   -- ISO 8601 UTC
    attempts        INTEGER NOT NULL DEFAULT 0,
    next_attempt_at TEXT NOT NULL,   -- ISO 8601 UTC; the item is due when this has passed
    last_reason     TEXT NULL        -- the last failure word from the fixed list, or NULL
);

CREATE INDEX idx_plugin_outbox_due ON plugin_outbox (plugin_id, next_attempt_at);
