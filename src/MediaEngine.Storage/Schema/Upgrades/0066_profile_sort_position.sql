-- Profile order (profile menu and Who's watching spec, packet 3). One position per profile, shared by the whole household: the Who's
-- watching screen and the profile menu list people in this order. Existing people keep their join order (the position ties break on
-- created_at); a new person is given the next position by the insert, so new people go last.
ALTER TABLE profiles ADD COLUMN sort_position INTEGER NOT NULL DEFAULT 0 CHECK (sort_position >= 0);
