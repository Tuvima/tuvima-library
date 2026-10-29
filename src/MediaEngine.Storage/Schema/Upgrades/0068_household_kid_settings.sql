-- Library access LA2 (D4, D9, D10). What a Kid's profile may see is one household setting: a maximum level for each area (Watch, Read, Listen)
-- on the G / PG / PG-13 / R ladder, and one "Show unrated items" switch. A Kid's profile is the profile with the restricted role; there is no
-- per-profile limit or "allow unrated" copy any more, so changing the household updates every Kid's profile at once. Defaults: PG for all three,
-- unrated shown. Pre-release rule: the old per-profile columns are dropped with no conversion.
ALTER TABLE households ADD COLUMN kid_watch_level TEXT NOT NULL DEFAULT 'PG' CHECK (kid_watch_level IN ('G', 'PG', 'PG-13', 'R'));
ALTER TABLE households ADD COLUMN kid_read_level TEXT NOT NULL DEFAULT 'PG' CHECK (kid_read_level IN ('G', 'PG', 'PG-13', 'R'));
ALTER TABLE households ADD COLUMN kid_listen_level TEXT NOT NULL DEFAULT 'PG' CHECK (kid_listen_level IN ('G', 'PG', 'PG-13', 'R'));
ALTER TABLE households ADD COLUMN kid_show_unrated INTEGER NOT NULL DEFAULT 1 CHECK (kid_show_unrated IN (0, 1));

-- The Vault rules named the profile's content limit; a Kid's profile (restricted role) is now the only profile kept out of the Vault.
DROP TRIGGER trg_profile_nsfw_settings_eligible_insert;
DROP TRIGGER trg_profile_nsfw_settings_eligible_update;
DROP TRIGGER trg_profiles_nsfw_access_withdrawn;
DROP TRIGGER trg_profiles_vault_sections_withdrawn;
DROP TRIGGER trg_vault_section_grants_eligible;

ALTER TABLE profiles DROP COLUMN content_limit;
ALTER TABLE profiles DROP COLUMN content_limit_allow_unrated;

CREATE TRIGGER trg_profile_nsfw_settings_eligible_insert
BEFORE INSERT ON profile_nsfw_settings
WHEN NEW.access_mode <> 'off'
 AND EXISTS (SELECT 1 FROM profiles WHERE id = NEW.profile_id AND role = 'RestrictedProfile')
BEGIN SELECT RAISE(ABORT, 'A Kids profile cannot have Vault access.'); END;

CREATE TRIGGER trg_profile_nsfw_settings_eligible_update
BEFORE UPDATE OF access_mode ON profile_nsfw_settings
WHEN NEW.access_mode <> 'off'
 AND EXISTS (SELECT 1 FROM profiles WHERE id = NEW.profile_id AND role = 'RestrictedProfile')
BEGIN SELECT RAISE(ABORT, 'A Kids profile cannot have Vault access.'); END;

CREATE TRIGGER trg_profiles_nsfw_access_withdrawn
AFTER UPDATE OF role ON profiles
WHEN NEW.role = 'RestrictedProfile'
BEGIN
    UPDATE profile_nsfw_settings SET access_mode = 'off' WHERE profile_id = NEW.id AND access_mode <> 'off';
    DELETE FROM nsfw_session_unlocks WHERE profile_id = NEW.id;
END;

CREATE TRIGGER trg_profiles_vault_sections_withdrawn
AFTER UPDATE OF role ON profiles
WHEN NEW.role = 'RestrictedProfile'
BEGIN
    DELETE FROM vault_section_grants WHERE profile_id = NEW.id;
END;

CREATE TRIGGER trg_vault_section_grants_eligible
BEFORE INSERT ON vault_section_grants
WHEN EXISTS (SELECT 1 FROM profiles WHERE id = NEW.profile_id AND role = 'RestrictedProfile')
BEGIN SELECT RAISE(ABORT, 'A Kids profile cannot open a Vault section.'); END;
