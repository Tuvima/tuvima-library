-- Personal activity: what each person watched, listened to and read, and what they added to My list, kept for good.
-- Each row carries its own snapshot of the item (title, episode label, year, a small picture) as it was at the time,
-- so history survives the media file or the work being deleted. That is why work_id and asset_id have no foreign keys.
-- Nothing prunes these rows automatically; only a server administrator deletes history, by hand. Deleting a profile
-- removes that person's rows.
CREATE TABLE personal_activity (
    id                   BLOB NOT NULL PRIMARY KEY,
    profile_id           BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    kind                 TEXT NOT NULL CHECK (kind IN ('watch', 'listen', 'read', 'my_list')),
    telemetry_session_id BLOB UNIQUE,         -- the playback session this row follows; makes repeated writes idempotent (no foreign key)
    music_session_id     BLOB,                -- shared by the songs of one music session (30 minutes of quiet or a different activity ends it)
    started_at           TEXT NOT NULL,       -- ISO 8601 UTC
    ended_at             TEXT,                -- ISO 8601 UTC; the last time the session was seen while it is still open
    played_seconds       INTEGER NOT NULL DEFAULT 0,
    progress_pct         REAL,                -- the furthest point reached, 0 to 100
    finished             INTEGER NOT NULL DEFAULT 0 CHECK (finished IN (0, 1)),
    device_name          TEXT,
    device_kind          TEXT,
    work_id              BLOB,                -- snapshot: no foreign key, must survive deletion
    asset_id             BLOB,                -- snapshot: no foreign key, must survive deletion
    media_type           TEXT NOT NULL,
    title                TEXT NOT NULL,
    subtitle             TEXT,                -- episode label ("S2 E4 - Name"), author or artist
    parent_title         TEXT,                -- album
    year                 INTEGER,
    artwork_key          TEXT                 -- personal_activity_artwork.key (no foreign key)
);

CREATE INDEX idx_personal_activity_profile_started ON personal_activity(profile_id, started_at DESC);
CREATE INDEX idx_personal_activity_profile_work ON personal_activity(profile_id, work_id);
CREATE INDEX idx_personal_activity_started ON personal_activity(started_at);
CREATE INDEX idx_personal_activity_music_session ON personal_activity(music_session_id);

-- The small pictures personal_activity rows point at: a copy of the cover (longest edge 240 px) made when the activity
-- began, shared by every row with the same source cover. Deleting history removes the pictures nothing uses any more.
CREATE TABLE personal_activity_artwork (
    key          TEXT NOT NULL PRIMARY KEY,   -- stable hash of the source cover
    image        BLOB NOT NULL,
    content_type TEXT NOT NULL,
    width        INTEGER,
    height       INTEGER,
    created_at   TEXT NOT NULL
);
