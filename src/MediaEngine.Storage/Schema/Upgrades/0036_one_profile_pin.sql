-- One PIN per profile (owner decision 2026-10-21; Videos spec D52).
-- The separate Vault PIN is gone: Vault mode asks for the profile's own PIN when "Ask for my PIN to open Vault mode" is on
-- (profile_nsfw_settings.access_mode = 'pin'), and a profile's admin pages ask for the same PIN when "Ask for my PIN on admin pages"
-- is on (grant_admin_protections.is_enabled). Neither question keeps a PIN of its own, and wrong tries are counted on the profile PIN.

-- The separate Vault PIN and the triggers that watched it.
DROP TRIGGER trg_profile_nsfw_pins_changed;
DROP TRIGGER trg_profile_nsfw_pins_removed;
DROP TABLE profile_nsfw_pins;

-- Pre-release: a profile that asked for its old Vault PIN but has no profile PIN has nothing to ask for, so Vault mode goes back to Off.
UPDATE profile_nsfw_settings SET access_mode = 'off'
WHERE access_mode = 'pin'
  AND NOT EXISTS (SELECT 1 FROM profile_credentials c WHERE c.profile_id = profile_nsfw_settings.profile_id AND c.credential_kind = 'ProfilePin');

-- A changed or removed profile PIN turns Vault mode off on every device (the old Vault PIN triggers did the same for the Vault PIN).
CREATE TRIGGER trg_profile_credentials_vault_locked_on_change
AFTER UPDATE OF secret_hash ON profile_credentials
WHEN OLD.secret_hash <> NEW.secret_hash
BEGIN DELETE FROM nsfw_session_unlocks WHERE profile_id = NEW.profile_id AND method = 'pin'; END;

CREATE TRIGGER trg_profile_credentials_vault_locked_on_removal
AFTER DELETE ON profile_credentials
BEGIN DELETE FROM nsfw_session_unlocks WHERE profile_id = OLD.profile_id AND method = 'pin'; END;

-- Vault mode is the user-facing name now (Videos spec D51): the two refusal messages say so.
DROP TRIGGER trg_profile_nsfw_settings_eligible_insert;
DROP TRIGGER trg_profile_nsfw_settings_eligible_update;

CREATE TRIGGER trg_profile_nsfw_settings_eligible_insert
BEFORE INSERT ON profile_nsfw_settings
WHEN NEW.access_mode <> 'off'
 AND EXISTS (SELECT 1 FROM profiles WHERE id = NEW.profile_id AND (role = 'RestrictedProfile' OR content_limit IS NOT NULL))
BEGIN SELECT RAISE(ABORT, 'A Kids or content-limited profile cannot have Vault access.'); END;

CREATE TRIGGER trg_profile_nsfw_settings_eligible_update
BEFORE UPDATE OF access_mode ON profile_nsfw_settings
WHEN NEW.access_mode <> 'off'
 AND EXISTS (SELECT 1 FROM profiles WHERE id = NEW.profile_id AND (role = 'RestrictedProfile' OR content_limit IS NOT NULL))
BEGIN SELECT RAISE(ABORT, 'A Kids or content-limited profile cannot have Vault access.'); END;

-- Asking for the PIN to open Vault mode needs a profile PIN to ask for. (The Engine checks first and answers in plain words; this
-- keeps any other writer honest.)
CREATE TRIGGER trg_profile_nsfw_settings_pin_needs_profile_pin_insert
BEFORE INSERT ON profile_nsfw_settings
WHEN NEW.access_mode = 'pin'
 AND NOT EXISTS (SELECT 1 FROM profile_credentials WHERE profile_id = NEW.profile_id AND credential_kind = 'ProfilePin')
BEGIN SELECT RAISE(ABORT, 'Set a PIN on this profile first.'); END;

CREATE TRIGGER trg_profile_nsfw_settings_pin_needs_profile_pin_update
BEFORE UPDATE OF access_mode ON profile_nsfw_settings
WHEN NEW.access_mode = 'pin'
 AND NOT EXISTS (SELECT 1 FROM profile_credentials WHERE profile_id = NEW.profile_id AND credential_kind = 'ProfilePin')
BEGIN SELECT RAISE(ABORT, 'Set a PIN on this profile first.'); END;

-- "Ask for my PIN on admin pages" no longer keeps a PIN hash or wrong-try count of its own. Pre-release: every sign-in's switch
-- starts Off again (its old separate PIN no longer exists), and an administrator turns it back on for a profile that has a PIN.
CREATE TABLE grant_admin_protections_new (
    account_id         BLOB NOT NULL,
    profile_id         BLOB NOT NULL,
    is_enabled         INTEGER NOT NULL DEFAULT 0 CHECK (is_enabled IN (0, 1)),
    unlock_mode        TEXT NOT NULL DEFAULT 'FixedDuration'
        CHECK (unlock_mode IN ('FixedDuration','UntilProfileSwitch','LockOnLeave')),
    unlock_minutes     INTEGER,
    protection_version INTEGER NOT NULL DEFAULT 1 CHECK (protection_version > 0),
    updated_at         TEXT NOT NULL,
    PRIMARY KEY (account_id, profile_id),
    FOREIGN KEY (account_id, profile_id) REFERENCES account_profile_grants(account_id, profile_id) ON DELETE CASCADE
);

INSERT INTO grant_admin_protections_new (account_id, profile_id, is_enabled, unlock_mode, unlock_minutes, protection_version, updated_at)
SELECT account_id, profile_id, 0, unlock_mode, unlock_minutes, protection_version + 1, updated_at FROM grant_admin_protections;

DELETE FROM grant_admin_unlocks;
DROP TABLE grant_admin_protections;
ALTER TABLE grant_admin_protections_new RENAME TO grant_admin_protections;
