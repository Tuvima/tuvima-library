-- Lanes (Collections and lists spec WP4, D9 and D10). A placement row with no profile is the shared "Show on" choice for a
-- location (home, area:watch, area:read, area:listen). A row with a profile is that one profile's own choice for the same
-- collection and location: is_visible = 0 is "Hide this lane" for that profile. Deleting the profile deletes its rows.
-- Pre-release rule: no conversion of anything older.
ALTER TABLE collection_placements ADD COLUMN profile_id BLOB REFERENCES profiles(id) ON DELETE CASCADE;
CREATE INDEX idx_collection_placements_profile ON collection_placements(profile_id) WHERE profile_id IS NOT NULL;
