-- Library access, one rule (library access spec D2, D3, LA1). A library is visible to a profile when it is not hidden for the profile's
-- household and not hidden for the profile (profile_library_hidden); administrators see everything. Nothing is hidden by default, so a new
-- library and a new household need no rows. A Vault library is never listed (the repository refuses it): its access is per profile.
-- The rows are settings, so they are part of a backup. Deleting a household removes its rows.
CREATE TABLE household_library_hidden (
    household_id          BLOB NOT NULL REFERENCES households(id) ON DELETE CASCADE,
    library_id            BLOB NOT NULL,
    created_at            TEXT NOT NULL,
    created_by_profile_id BLOB REFERENCES profiles(id) ON DELETE SET NULL,
    PRIMARY KEY (household_id, library_id)
);

-- The library-side "Selected profiles" choice is removed (library access D3). Pre-release rule: the rows are dropped with no conversion.
DROP TABLE library_profile_grants;
