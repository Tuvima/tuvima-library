-- Per-profile Vault sections (Vault sections spec D28, D39). Each Vault library is a section. A profile whose Vault mode is on opens only the
-- sections listed here; server administrators open every section without a row, and a new profile has none, so it opens nothing until an
-- administrator ticks one. These are settings of the Vault, not account library grants, and a Vault library is never an account grant.
-- Removing a Vault library or a profile removes its rows.
CREATE TABLE vault_section_grants (
    library_id            TEXT NOT NULL COLLATE NOCASE REFERENCES vault_libraries(library_id) ON DELETE CASCADE,
    profile_id            BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    created_at            TEXT NOT NULL,
    created_by_profile_id BLOB REFERENCES profiles(id) ON DELETE SET NULL,
    PRIMARY KEY (library_id, profile_id)
);
CREATE INDEX ix_vault_section_grants_profile ON vault_section_grants(profile_id);

-- A Kids or content-limited profile never has a section (D39), however the row would arrive.
CREATE TRIGGER trg_vault_section_grants_eligible
BEFORE INSERT ON vault_section_grants
WHEN EXISTS (SELECT 1 FROM profiles WHERE id = NEW.profile_id AND (role = 'RestrictedProfile' OR content_limit IS NOT NULL))
BEGIN SELECT RAISE(ABORT, 'A Kids or content-limited profile cannot open a Vault section.'); END;

-- A profile that becomes Kids or content-limited loses its sections at once.
CREATE TRIGGER trg_profiles_vault_sections_withdrawn
AFTER UPDATE OF role, content_limit ON profiles
WHEN NEW.role = 'RestrictedProfile' OR NEW.content_limit IS NOT NULL
BEGIN
    DELETE FROM vault_section_grants WHERE profile_id = NEW.id;
END;
