-- Per-library limits for plugins and metadata sources (Plugin architecture spec D6). A plugin or source with no rows here works on
-- every library it can work on (the default); once an administrator picks libraries, it works only on those. library_id is the
-- library's id in config/libraries.json (the same text media_assets.library_id holds). Vault libraries are refused when the
-- choice is saved, so no row here ever names one. The rows are settings, so they are part of a backup.
CREATE TABLE plugin_library_limits (
    plugin_id  TEXT NOT NULL,   -- the plugin's manifest id, or the metadata source's name
    library_id TEXT NOT NULL,
    PRIMARY KEY (plugin_id, library_id)
);
