-- What plugins show on the Library's own surfaces (Plugin architecture spec C21): a line on a Home slide, a chip or a titled block on
-- an item's page, a small badge on a tile, a status line in Settings. A plugin supplies plain data only and the Library draws it, so
-- the data is small (limits are enforced when it is published), is read without ever waiting on the plugin, and is rebuilt by the
-- plugin when it next runs, which is why it is not part of a backup.
CREATE TABLE plugin_display_contributions (
    plugin_id     TEXT NOT NULL,                 -- the plugin's manifest id
    slot          TEXT NOT NULL,                 -- one of the fixed slot names
    subject_id    TEXT NOT NULL DEFAULT '',      -- the item it is about, or empty when it is not about one item
    key           TEXT NOT NULL,                 -- the plugin's own name for this contribution, so it can replace or withdraw it
    title         TEXT NULL,
    text          TEXT NOT NULL,
    icon          TEXT NULL,
    strength      INTEGER NOT NULL DEFAULT 0,    -- 0 to 100, how strongly the plugin suggests it
    from_local_ai INTEGER NOT NULL DEFAULT 0,    -- 1 when Local AI produced it, so the Library can mark it "AI"
    updated_at    TEXT NOT NULL,                 -- ISO 8601 UTC
    PRIMARY KEY (plugin_id, slot, subject_id, key)
);

CREATE INDEX idx_plugin_display_slot ON plugin_display_contributions (slot, subject_id);
