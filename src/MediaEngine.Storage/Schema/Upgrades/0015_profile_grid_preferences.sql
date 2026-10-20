-- Grid preferences: the order each person last chose on a "View all" grid, remembered per grid (Watch, Read, Listen,
-- My list, Liked) for their profile, so it follows them to every device.
-- One row per profile and grid. A grid with no row simply uses its default order. Deleting a profile removes that
-- person's rows.
CREATE TABLE profile_grid_preferences (
    profile_id  BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    grid_key    TEXT NOT NULL CHECK (grid_key IN ('watch', 'read', 'listen', 'my-list', 'liked')),
    sort        TEXT NOT NULL CHECK (sort IN ('added', 'title', 'year', 'played')),
    updated_at  TEXT NOT NULL,   -- ISO 8601 UTC
    PRIMARY KEY (profile_id, grid_key)
);
