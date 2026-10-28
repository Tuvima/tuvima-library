-- Selected-profiles libraries (Library pages spec D9, WP6). A library whose access is "selected" (the mode lives in config/libraries.json) is
-- visible to server administrators and to the profiles listed here, and to nobody else. One row per profile that may see the library; a
-- library with no rows is visible to administrators only, and a new profile has no rows, so it starts without access. A Vault library is never
-- listed (the repository refuses it). The rows are settings, so they are part of a backup; library ids differ between Libraries, so a user file
-- never carries them. Deleting a profile removes its rows.
CREATE TABLE library_profile_grants (
    library_id            BLOB NOT NULL,
    profile_id            BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    created_at            TEXT NOT NULL,
    created_by_profile_id BLOB REFERENCES profiles(id) ON DELETE SET NULL,
    PRIMARY KEY (library_id, profile_id)
);
CREATE INDEX ix_library_profile_grants_profile ON library_profile_grants(profile_id);
