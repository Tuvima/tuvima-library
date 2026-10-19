-- Taste overrides: what a person removed from, or added to, the taste the Library learned about them. The learned taste
-- (the taste profiler's reading of their history) is never changed; these rows are applied on top of it whenever taste is
-- shown. 'removed' hides a learned value; 'added' shows a value the person typed, marked "Added by you". One row per
-- person, group and value; re-adding or re-removing a value replaces its row. Deleting the profile removes its rows.
CREATE TABLE profile_taste_overrides (
    profile_id  BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    taste_group TEXT NOT NULL CHECK (taste_group IN ('genre', 'mood', 'era')),
    value       TEXT NOT NULL,   -- trimmed and lower-cased, the same way the taste profiler writes its keys
    action      TEXT NOT NULL CHECK (action IN ('removed', 'added')),
    created_at  TEXT NOT NULL,   -- ISO 8601 UTC
    PRIMARY KEY (profile_id, taste_group, value)
);
