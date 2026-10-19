-- When a session was first used after sign-in. NULL means the session has never been used. At the sign-in limit, the
-- Library ends never-used sessions first, so a burst of new sign-ins cannot push out a session someone is using.
ALTER TABLE auth_sessions ADD COLUMN first_used_at TEXT;
