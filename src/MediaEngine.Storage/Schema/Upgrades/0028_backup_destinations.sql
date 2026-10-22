-- Backup destinations (Developer tools WP25). backup_settings holds the schedule, how many backups to keep, which destinations
-- are switched on, and the backup passphrase's public half (the passphrase itself is never stored) as JSON rows.
-- backup_copies is the Library's record of each copy it sent to a destination: how big it was, its checksum, and whether it was
-- read back intact. It lets the settings page say "last copy 2 days ago, checked" without asking the destination.
CREATE TABLE backup_settings (
    setting_key  TEXT NOT NULL PRIMARY KEY,
    value_json   TEXT NOT NULL,
    updated_at   TEXT NOT NULL
);

CREATE TABLE backup_copies (
    id              TEXT    NOT NULL PRIMARY KEY,   -- GUID
    destination_id  TEXT    NOT NULL,
    remote_name     TEXT    NOT NULL,               -- the file name at the destination
    size_bytes      INTEGER NOT NULL,
    sha256          TEXT    NOT NULL,               -- of the bytes sent (the encrypted file)
    state           TEXT    NOT NULL,               -- sent, verified, failed or deleted
    verified_at     TEXT,                           -- ISO 8601 UTC; set when the copy was read back and matched
    last_error      TEXT,                           -- plain words, never a secret
    created_at      TEXT    NOT NULL                -- ISO 8601 UTC
);
CREATE INDEX ix_backup_copies_destination ON backup_copies (destination_id, created_at DESC);
