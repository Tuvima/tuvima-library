-- Trakt sign-in for private and own lists (Collections and lists spec D7, WP6e; Plugin architecture spec C13).
-- A scheduled refresh has no signed-in person, so a web list that was read with a profile's own account remembers which
-- profile's connection it used. The refresh reads the list with that profile's token and with nobody else's. Null for a file
-- list and for a public list that needed no account. When the profile is removed the list keeps what it has and simply cannot
-- be read with an account any more.
ALTER TABLE collection_list_sources ADD COLUMN connection_profile_id BLOB REFERENCES profiles(id) ON DELETE SET NULL;
