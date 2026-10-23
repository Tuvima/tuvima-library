-- Profile-held access (users, profiles and households spec, wave UPH1a: expand). A profile holds the person and what the person may see and do;
-- a user (accounts row) becomes only a sign-in attached to one profile. This step adds the profile side next to the account side, which is
-- still what authority reads until the Engine switches over. Pre-release, so nothing is converted here.
ALTER TABLE profiles ADD COLUMN is_server_admin INTEGER NOT NULL DEFAULT 0 CHECK (is_server_admin IN (0, 1));
ALTER TABLE profiles ADD COLUMN is_household_admin INTEGER NOT NULL DEFAULT 0 CHECK (is_household_admin IN (0, 1));
ALTER TABLE profiles ADD COLUMN authorization_version INTEGER NOT NULL DEFAULT 1 CHECK (authorization_version > 0);

CREATE TABLE profile_feature_grants (
    profile_id BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    feature_id TEXT NOT NULL CHECK (feature_id IN ('read','watch','listen','view')),
    granted_at TEXT NOT NULL,
    PRIMARY KEY (profile_id, feature_id)
);

-- Library access is one rule (simpler access D2, D11): every library in a profile's areas is visible unless an administrator hid it from
-- that profile. One row per hidden library; a Vault library is never listed (the repository refuses it). A new library needs no row.
CREATE TABLE profile_library_hidden (
    profile_id            BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    library_id            BLOB NOT NULL,
    created_at            TEXT NOT NULL,
    created_by_profile_id BLOB REFERENCES profiles(id) ON DELETE SET NULL,
    PRIMARY KEY (profile_id, library_id)
);

CREATE TABLE profile_admin_protections (
    profile_id         BLOB NOT NULL PRIMARY KEY REFERENCES profiles(id) ON DELETE CASCADE,
    is_enabled         INTEGER NOT NULL DEFAULT 0 CHECK (is_enabled IN (0, 1)),
    unlock_mode        TEXT NOT NULL DEFAULT 'FixedDuration'
        CHECK (unlock_mode IN ('FixedDuration','UntilProfileSwitch','LockOnLeave')),
    unlock_minutes     INTEGER,
    protection_version INTEGER NOT NULL DEFAULT 1 CHECK (protection_version > 0),
    updated_at         TEXT NOT NULL
);

CREATE TABLE profile_admin_unlocks (
    session_id BLOB NOT NULL PRIMARY KEY REFERENCES auth_sessions(id) ON DELETE CASCADE,
    profile_id BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    protection_version INTEGER NOT NULL,
    method TEXT NOT NULL,
    granted_at TEXT NOT NULL,
    expires_at TEXT
);
CREATE INDEX ix_profile_admin_unlocks_profile ON profile_admin_unlocks(profile_id);

-- A user opens one profile; a profile has at most one user.
ALTER TABLE accounts ADD COLUMN profile_id BLOB REFERENCES profiles(id) ON DELETE CASCADE;
CREATE UNIQUE INDEX ux_accounts_profile ON accounts(profile_id) WHERE profile_id IS NOT NULL;

-- The default household is the server administrator's original household; exactly one.
ALTER TABLE households ADD COLUMN is_default INTEGER NOT NULL DEFAULT 0 CHECK (is_default IN (0, 1));
CREATE UNIQUE INDEX ux_households_default ON households(is_default) WHERE is_default = 1;
