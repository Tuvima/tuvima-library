-- tuvima:foreign-keys-off
-- Users, profiles and households (wave UPH1d: contract). Roles and access now live only on the profile, so the sign-in-held copies
-- are removed: the three account grant tables (profiles, Read/Watch/Listen/View, libraries), the per-grant admin PIN tables (replaced
-- by profile_admin_protections and profile_admin_unlocks in step 0041), and the sign-in columns is_administrator, household_admin,
-- household_id and grants_inherit_from_account_id, and the household's primary_account_id. A sign-in cannot exist without its profile
-- (spec D2, D7), so accounts.profile_id is now required and at most one sign-in opens a profile. The household of a user is its
-- profile's household.
--
-- Pre-release: nothing is converted. The storage epoch moves on, so a data store from before this step is reset instead of carried
-- forward (spec D11), and StorageEpochGuard.CurrentEpoch is the same value. SQLite cannot change a column to NOT NULL in place, so
-- accounts is rebuilt; the runner has foreign keys off for this step (dropping the old table fires no foreign-key actions) and checks
-- for orphaned rows before committing. accounts has no triggers or views; its two indexes are recreated below.
UPDATE storage_metadata SET value = 'guid-blob-v13-profile-held-contract' WHERE key = 'storage_epoch';

DROP TABLE grant_admin_unlocks;
DROP TABLE grant_admin_protections;
DROP TABLE account_library_grants;
DROP TABLE account_feature_grants;
DROP TABLE account_profile_grants;

ALTER TABLE households DROP COLUMN primary_account_id;

CREATE TABLE accounts_new (
    id               BLOB NOT NULL PRIMARY KEY,
    email            TEXT,
    normalized_email TEXT,
    is_enabled       INTEGER NOT NULL DEFAULT 1 CHECK (is_enabled IN (0, 1)),
    authorization_version INTEGER NOT NULL DEFAULT 1 CHECK (authorization_version > 0),
    created_at       TEXT NOT NULL,
    updated_at       TEXT NOT NULL,
    this_computer_only INTEGER NOT NULL DEFAULT 0 CHECK (this_computer_only IN (0, 1)),
    must_change_password INTEGER NOT NULL DEFAULT 0 CHECK (must_change_password IN (0, 1)),
    temporary_password_expires_at TEXT,
    profile_id       BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE
);

INSERT INTO accounts_new
    (id, email, normalized_email, is_enabled, authorization_version, created_at, updated_at,
     this_computer_only, must_change_password, temporary_password_expires_at, profile_id)
SELECT id, email, normalized_email, is_enabled, authorization_version, created_at, updated_at,
       this_computer_only, must_change_password, temporary_password_expires_at, profile_id
  FROM accounts;

DROP TABLE accounts;
ALTER TABLE accounts_new RENAME TO accounts;

CREATE UNIQUE INDEX ux_accounts_normalized_email
    ON accounts(normalized_email);

CREATE UNIQUE INDEX ux_accounts_profile ON accounts(profile_id);
