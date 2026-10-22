-- Bookmark packs (Videos spec D46). A pack is a small file of one person's bookmarks for one title, shared by hand. Importing one adds
-- read-only "pack bookmarks" to that title for the profile that imported it. Nothing here is a file path, a profile name or watch history.
--
-- end_seconds: the optional end of a bookmark that marks a part of the video (a scene), in seconds. NULL for a plain bookmark.
ALTER TABLE audiobook_bookmarks ADD COLUMN end_seconds REAL CHECK (end_seconds IS NULL OR end_seconds > position_seconds);

-- One imported pack on one title, for one profile. asset_id is the file it was imported on, so a file the caller cannot see (a Vault
-- file without an unlocked session) contributes nothing, as for ordinary bookmarks.
-- content_hash: fingerprint of the pack's bookmarks, so the same pack is not imported twice on a title.
-- source_running_time_seconds / source_edition: what the pack author's copy was, kept so a later screen can say why a pack was aligned.
-- is_aligned: 1 when the pack was for a different cut and its times were moved by chapter name on import.
-- is_enabled: 0 hides the pack's ticks and rows without removing the pack.
CREATE TABLE bookmark_packs (
    id                           BLOB NOT NULL PRIMARY KEY,
    profile_id                   BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    work_id                      BLOB NOT NULL REFERENCES works(id) ON DELETE CASCADE,
    asset_id                     BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    name                         TEXT NOT NULL,
    author                       TEXT,
    content_hash                 TEXT NOT NULL,
    source_running_time_seconds  REAL,
    source_edition               TEXT,
    is_aligned                   INTEGER NOT NULL DEFAULT 0 CHECK (is_aligned IN (0, 1)),
    is_enabled                   INTEGER NOT NULL DEFAULT 1 CHECK (is_enabled IN (0, 1)),
    imported_at                  TEXT NOT NULL,
    UNIQUE (profile_id, work_id, content_hash)
);

CREATE INDEX idx_bookmark_packs_profile_work
    ON bookmark_packs(profile_id, work_id, imported_at DESC);

CREATE TABLE bookmark_pack_items (
    id               BLOB NOT NULL PRIMARY KEY,
    pack_id          BLOB NOT NULL REFERENCES bookmark_packs(id) ON DELETE CASCADE,
    position_seconds REAL NOT NULL CHECK (position_seconds >= 0),
    end_seconds      REAL CHECK (end_seconds IS NULL OR end_seconds > position_seconds),
    chapter_title    TEXT,
    note             TEXT,
    description      TEXT
);

CREATE INDEX idx_bookmark_pack_items_pack
    ON bookmark_pack_items(pack_id, position_seconds);
