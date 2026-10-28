-- Home hero plugin slides (Home page redesign spec WP8, Plugin architecture C7): a person can switch a plugin's Home slides off for themselves.
-- A row means "this profile does not want this plugin's slides"; no row means they are on. plugin_id is the plugin's manifest id.
-- Deleting a profile removes that person's rows.
CREATE TABLE profile_hero_plugin_switches (
    profile_id BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    plugin_id  TEXT NOT NULL,
    updated_at TEXT NOT NULL,   -- ISO 8601 UTC
    PRIMARY KEY (profile_id, plugin_id)
);
