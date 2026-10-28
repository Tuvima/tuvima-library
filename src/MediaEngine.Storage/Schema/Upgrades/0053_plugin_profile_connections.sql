-- Per-profile connections (Plugin architecture spec C13): a plugin that declares a personal credential lets each profile link its
-- own account (for example a ListenBrainz token). The value is encrypted with this machine's Data Protection key before it is
-- stored, so it is never readable here and is not part of a backup or a user export; after a restore each person links again.
-- Removing a profile removes its connections, and so does removing the plugin.
CREATE TABLE plugin_profile_connections (
    plugin_id         TEXT NOT NULL,   -- the plugin's manifest id
    profile_id        BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    credential_name   TEXT NOT NULL,   -- the personal credential's name from the plugin's declaration
    secret_ciphertext TEXT NOT NULL,   -- the value, protected with this machine's Data Protection key
    linked_at         TEXT NOT NULL,   -- ISO 8601 UTC
    PRIMARY KEY (plugin_id, profile_id, credential_name)
);
