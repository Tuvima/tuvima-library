-- Vault access rules (Videos spec WP9). The Vault is its own area (D45): an item is Vault because its file sits in a Vault
-- library, never because of a flag on the item. vault_libraries is the single list of those libraries; every Engine read
-- that lists, searches, counts, streams or reports on items treats anything in them as absent unless the request comes
-- from a device session that has switched NSFW mode on (D17, D36).

-- The Vault libraries and their own settings. library_id is the same text media_assets.library_id carries.
-- lookup_source is where the library looks up details (D26): 'none' or 'stash_box'. A Vault library can never use a normal source.
CREATE TABLE vault_libraries (
    library_id            TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
    lookup_source         TEXT NOT NULL DEFAULT 'none' CHECK (lookup_source IN ('none', 'stash_box')),
    lookup_server_id      TEXT,
    adults_confirmed_at   TEXT NOT NULL,   -- D34: the administrator confirmed everyone who can unlock it is an adult and the content is lawful
    created_by_profile_id BLOB REFERENCES profiles(id) ON DELETE SET NULL,
    created_at            TEXT NOT NULL,
    updated_at            TEXT NOT NULL
);

-- A Vault library cannot be unmarked while any file is still in it, or its items would turn into normal ones.
CREATE TRIGGER trg_vault_libraries_in_use
BEFORE DELETE ON vault_libraries
WHEN EXISTS (SELECT 1 FROM media_assets WHERE library_id = OLD.library_id COLLATE NOCASE)
BEGIN SELECT RAISE(ABORT, 'A Vault library that still has items cannot be removed.'); END;

-- A file never moves into or out of a Vault library: the folder decides, and a file in the wrong place is removed and added again (D6).
CREATE TRIGGER trg_media_assets_vault_library_fixed
BEFORE UPDATE OF library_id ON media_assets
WHEN OLD.library_id IS NOT NEW.library_id
 AND (EXISTS (SELECT 1 FROM vault_libraries WHERE library_id = OLD.library_id)
   OR EXISTS (SELECT 1 FROM vault_libraries WHERE library_id = NEW.library_id))
BEGIN SELECT RAISE(ABORT, 'Files cannot be moved into or out of a Vault library.'); END;

-- Per-profile NSFW mode (D18). No row means Off. An administrator sets it; a Kids or content-limited profile can only be Off.
-- idle_lock_minutes: NULL uses the default (15), 0 means never lock by idle (Allowed profiles only, enforced by the Engine).
CREATE TABLE profile_nsfw_settings (
    profile_id            BLOB NOT NULL PRIMARY KEY REFERENCES profiles(id) ON DELETE CASCADE,
    access_mode           TEXT NOT NULL DEFAULT 'off' CHECK (access_mode IN ('off', 'pin', 'allowed')),
    idle_lock_minutes     INTEGER CHECK (idle_lock_minutes IS NULL OR idle_lock_minutes BETWEEN 0 AND 1440),
    changed_by_profile_id BLOB REFERENCES profiles(id) ON DELETE SET NULL,
    updated_at            TEXT NOT NULL
);

CREATE TRIGGER trg_profile_nsfw_settings_eligible_insert
BEFORE INSERT ON profile_nsfw_settings
WHEN NEW.access_mode <> 'off'
 AND EXISTS (SELECT 1 FROM profiles WHERE id = NEW.profile_id AND (role = 'RestrictedProfile' OR content_limit IS NOT NULL))
BEGIN SELECT RAISE(ABORT, 'A Kids or content-limited profile cannot have NSFW access.'); END;

CREATE TRIGGER trg_profile_nsfw_settings_eligible_update
BEFORE UPDATE OF access_mode ON profile_nsfw_settings
WHEN NEW.access_mode <> 'off'
 AND EXISTS (SELECT 1 FROM profiles WHERE id = NEW.profile_id AND (role = 'RestrictedProfile' OR content_limit IS NOT NULL))
BEGIN SELECT RAISE(ABORT, 'A Kids or content-limited profile cannot have NSFW access.'); END;

-- A profile that becomes Kids or content-limited loses NSFW access at once.
CREATE TRIGGER trg_profiles_nsfw_access_withdrawn
AFTER UPDATE OF role, content_limit ON profiles
WHEN NEW.role = 'RestrictedProfile' OR NEW.content_limit IS NOT NULL
BEGIN
    UPDATE profile_nsfw_settings SET access_mode = 'off' WHERE profile_id = NEW.id AND access_mode <> 'off';
    DELETE FROM nsfw_session_unlocks WHERE profile_id = NEW.id;
END;

-- The NSFW PIN (D19): hashed like the profile PIN but kept apart, with its own attempt count and lock. It is never the same
-- digits as the profile PIN (the Engine refuses to save both), and it never signs in to the profile.
CREATE TABLE profile_nsfw_pins (
    id                   BLOB NOT NULL PRIMARY KEY,
    profile_id           BLOB NOT NULL UNIQUE REFERENCES profiles(id) ON DELETE CASCADE,
    secret_hash          TEXT NOT NULL,
    hash_scheme          TEXT NOT NULL,
    hash_version         INTEGER NOT NULL,
    security_stamp       TEXT NOT NULL,
    failed_attempt_count INTEGER NOT NULL DEFAULT 0,
    locked_until         TEXT,
    created_at           TEXT NOT NULL,
    updated_at           TEXT NOT NULL,
    last_used_at         TEXT
);

-- Per device session (D36): switching NSFW mode on a phone does not switch it on another browser. expires_at is the idle
-- limit, moved forward by activity; NULL never runs out. The row belongs to one profile of the session.
CREATE TABLE nsfw_session_unlocks (
    session_id       BLOB NOT NULL PRIMARY KEY REFERENCES auth_sessions(id) ON DELETE CASCADE,
    profile_id       BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    method           TEXT NOT NULL CHECK (method IN ('pin', 'allowed')),
    unlocked_at      TEXT NOT NULL,
    last_activity_at TEXT NOT NULL,
    expires_at       TEXT
);
CREATE INDEX idx_nsfw_session_unlocks_profile ON nsfw_session_unlocks(profile_id);

-- Every way the unlock can stop being true removes it, so a later switch back never finds it again.
CREATE TRIGGER trg_auth_sessions_nsfw_locked_on_profile_switch
AFTER UPDATE OF active_profile_id ON auth_sessions
WHEN OLD.active_profile_id <> NEW.active_profile_id
BEGIN DELETE FROM nsfw_session_unlocks WHERE session_id = NEW.id; END;

CREATE TRIGGER trg_profile_nsfw_settings_mode_changed
AFTER UPDATE OF access_mode ON profile_nsfw_settings
WHEN OLD.access_mode <> NEW.access_mode
BEGIN DELETE FROM nsfw_session_unlocks WHERE profile_id = NEW.profile_id; END;

CREATE TRIGGER trg_profile_nsfw_settings_removed
AFTER DELETE ON profile_nsfw_settings
BEGIN DELETE FROM nsfw_session_unlocks WHERE profile_id = OLD.profile_id; END;

CREATE TRIGGER trg_profile_nsfw_pins_changed
AFTER UPDATE OF secret_hash ON profile_nsfw_pins
WHEN OLD.secret_hash <> NEW.secret_hash
BEGIN DELETE FROM nsfw_session_unlocks WHERE profile_id = NEW.profile_id; END;

CREATE TRIGGER trg_profile_nsfw_pins_removed
AFTER DELETE ON profile_nsfw_pins
BEGIN DELETE FROM nsfw_session_unlocks WHERE profile_id = OLD.profile_id; END;

-- The Vault's own names (D27, D28): performers, studios and tags a Vault lookup brought back. They never create or join
-- shared People, Tags, Series or artwork records. A name exists only while at least one scene refers to it.
CREATE TABLE vault_performers (
    id         BLOB NOT NULL PRIMARY KEY,
    library_id TEXT NOT NULL REFERENCES vault_libraries(library_id) ON DELETE CASCADE,
    name       TEXT NOT NULL COLLATE NOCASE,
    sort_name  TEXT,
    source     TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE (library_id, name)
);

CREATE TABLE vault_studios (
    id         BLOB NOT NULL PRIMARY KEY,
    library_id TEXT NOT NULL REFERENCES vault_libraries(library_id) ON DELETE CASCADE,
    name       TEXT NOT NULL COLLATE NOCASE,
    source     TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE (library_id, name)
);

CREATE TABLE vault_tags (
    id         BLOB NOT NULL PRIMARY KEY,
    library_id TEXT NOT NULL REFERENCES vault_libraries(library_id) ON DELETE CASCADE,
    name       TEXT NOT NULL COLLATE NOCASE,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE (library_id, name)
);

CREATE TABLE vault_asset_performers (
    asset_id     BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    performer_id BLOB NOT NULL REFERENCES vault_performers(id) ON DELETE CASCADE,
    PRIMARY KEY (asset_id, performer_id)
);
CREATE INDEX idx_vault_asset_performers_performer ON vault_asset_performers(performer_id);

CREATE TABLE vault_asset_studios (
    asset_id  BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    studio_id BLOB NOT NULL REFERENCES vault_studios(id) ON DELETE CASCADE,
    PRIMARY KEY (asset_id, studio_id)
);
CREATE INDEX idx_vault_asset_studios_studio ON vault_asset_studios(studio_id);

CREATE TABLE vault_asset_tags (
    asset_id BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    tag_id   BLOB NOT NULL REFERENCES vault_tags(id) ON DELETE CASCADE,
    PRIMARY KEY (asset_id, tag_id)
);
CREATE INDEX idx_vault_asset_tags_tag ON vault_asset_tags(tag_id);

-- A name with no scene left goes with its last link (D28).
CREATE TRIGGER trg_vault_asset_performers_prune
AFTER DELETE ON vault_asset_performers
BEGIN DELETE FROM vault_performers WHERE id = OLD.performer_id AND NOT EXISTS (SELECT 1 FROM vault_asset_performers WHERE performer_id = OLD.performer_id); END;

CREATE TRIGGER trg_vault_asset_studios_prune
AFTER DELETE ON vault_asset_studios
BEGIN DELETE FROM vault_studios WHERE id = OLD.studio_id AND NOT EXISTS (SELECT 1 FROM vault_asset_studios WHERE studio_id = OLD.studio_id); END;

CREATE TRIGGER trg_vault_asset_tags_prune
AFTER DELETE ON vault_asset_tags
BEGIN DELETE FROM vault_tags WHERE id = OLD.tag_id AND NOT EXISTS (SELECT 1 FROM vault_asset_tags WHERE tag_id = OLD.tag_id); END;

-- Vault artwork (D27, D31): stored apart from normal artwork and served only to a profile with access. owner_id is a media
-- asset, a Vault performer or a Vault studio, depending on owner_kind.
CREATE TABLE vault_artwork (
    id           BLOB NOT NULL PRIMARY KEY,
    owner_kind   TEXT NOT NULL CHECK (owner_kind IN ('asset', 'performer', 'studio')),
    owner_id     BLOB NOT NULL,
    artwork_kind TEXT NOT NULL CHECK (artwork_kind IN ('cover', 'thumbnail', 'portrait', 'logo', 'still')),
    file_path    TEXT NOT NULL,
    content_hash TEXT,
    width        INTEGER,
    height       INTEGER,
    source       TEXT,
    created_at   TEXT NOT NULL
);
CREATE INDEX idx_vault_artwork_owner ON vault_artwork(owner_kind, owner_id);

CREATE TRIGGER trg_vault_artwork_asset_removed
AFTER DELETE ON media_assets
BEGIN DELETE FROM vault_artwork WHERE owner_kind = 'asset' AND owner_id = OLD.id; END;

CREATE TRIGGER trg_vault_artwork_performer_removed
AFTER DELETE ON vault_performers
BEGIN DELETE FROM vault_artwork WHERE owner_kind = 'performer' AND owner_id = OLD.id; END;

CREATE TRIGGER trg_vault_artwork_studio_removed
AFTER DELETE ON vault_studios
BEGIN DELETE FROM vault_artwork WHERE owner_kind = 'studio' AND owner_id = OLD.id; END;

-- Nothing about the Vault reaches an app, a webhook or anyone's activity history (D17). These are skipped by the data structure
-- itself, whichever service writes them, so a new sender cannot forget the rule. A skipped event or entry is simply not stored.
CREATE TRIGGER trg_application_events_no_vault
BEFORE INSERT ON application_events
WHEN (NEW.library_id IS NOT NULL
      AND EXISTS (SELECT 1 FROM vault_libraries WHERE UPPER(REPLACE(library_id, '-', '')) = HEX(NEW.library_id)))
  OR EXISTS (
      SELECT 1
      FROM media_assets ma
      JOIN vault_libraries vl ON vl.library_id = ma.library_id COLLATE NOCASE
      WHERE ma.id = UNHEX(REPLACE(NEW.subject_id, '-', ''))
         OR ma.edition_id IN (SELECT e.id FROM editions e WHERE e.work_id = UNHEX(REPLACE(NEW.subject_id, '-', ''))))
BEGIN SELECT RAISE(IGNORE); END;

CREATE TRIGGER trg_personal_activity_no_vault
BEFORE INSERT ON personal_activity
WHEN NEW.asset_id IS NOT NULL
 AND EXISTS (
      SELECT 1
      FROM media_assets ma
      JOIN vault_libraries vl ON vl.library_id = ma.library_id COLLATE NOCASE
      WHERE ma.id = NEW.asset_id)
BEGIN SELECT RAISE(IGNORE); END;
