-- Listen playlists (spec listen-playlists WP1): a profile can pin a playlist to the top of its own list. The pin belongs to the profile
-- (another profile sees the same playlist unpinned) and goes with the profile or the playlist when either is deleted.
-- A playlist's description and cover choice need no new column: collections.description exists, and the cover is the person's own
-- picture when collections.cover_artwork_path is set, otherwise the automatic mosaic.
CREATE TABLE profile_pinned_playlists (
    profile_id    BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    collection_id BLOB NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
    pinned_at     TEXT NOT NULL,
    PRIMARY KEY (profile_id, collection_id)
);

CREATE INDEX idx_profile_pinned_playlists_collection ON profile_pinned_playlists(collection_id);
