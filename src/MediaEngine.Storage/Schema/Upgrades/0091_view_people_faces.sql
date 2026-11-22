-- View (spec view-people-search WP7): faces found in a person's own photos, the people (named or not yet named groups) they belong to, and which photos were already looked at.
-- Pre-release rule: no back-fill. Face data is local only, stays inside the profile that owns it, and is removed with the profile or by "Delete all face data".
CREATE TABLE view_people (
    id                BLOB NOT NULL PRIMARY KEY,
    owner_profile_id  BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    -- NULL while the group is still unnamed ("Who's this?").
    name              TEXT,
    cover_face_id     BLOB,
    hidden            INTEGER NOT NULL DEFAULT 0 CHECK (hidden IN (0, 1)),
    created_at        TEXT NOT NULL,
    updated_at        TEXT NOT NULL
);
CREATE INDEX idx_view_people_owner ON view_people(owner_profile_id);

CREATE TABLE view_faces (
    id                BLOB NOT NULL PRIMARY KEY,
    item_id           BLOB NOT NULL REFERENCES local_items(id) ON DELETE CASCADE,
    owner_profile_id  BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    person_id         BLOB REFERENCES view_people(id) ON DELETE SET NULL,
    -- The face box as fractions of the photo (0 to 1), so it fits any size of the picture.
    box_x             REAL NOT NULL,
    box_y             REAL NOT NULL,
    box_width         REAL NOT NULL,
    box_height        REAL NOT NULL,
    quality           REAL NOT NULL,
    -- A short list of numbers (little-endian 32-bit floats) that describes how the face looks; two looks of one person are close.
    embedding         BLOB NOT NULL,
    -- 1 once a person said "yes, this is them".
    confirmed         INTEGER NOT NULL DEFAULT 0 CHECK (confirmed IN (0, 1)),
    created_at        TEXT NOT NULL
);
CREATE INDEX idx_view_faces_item ON view_faces(item_id);
CREATE INDEX idx_view_faces_person ON view_faces(person_id);
CREATE INDEX idx_view_faces_owner ON view_faces(owner_profile_id);

-- "Not Sam": this face may never be put with that person again.
CREATE TABLE view_face_rejections (
    face_id           BLOB NOT NULL REFERENCES view_faces(id) ON DELETE CASCADE,
    person_id         BLOB NOT NULL REFERENCES view_people(id) ON DELETE CASCADE,
    owner_profile_id  BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    PRIMARY KEY (face_id, person_id)
);

-- A photo that was looked at (even when it held no face), so a scan never repeats and resumes where it stopped.
CREATE TABLE view_face_scans (
    item_id           BLOB NOT NULL PRIMARY KEY REFERENCES local_items(id) ON DELETE CASCADE,
    owner_profile_id  BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    scanned_at        TEXT NOT NULL,
    face_count        INTEGER NOT NULL,
    model             TEXT NOT NULL
);
CREATE INDEX idx_view_face_scans_owner ON view_face_scans(owner_profile_id);

-- The person's own switch: face recognition is on automatically and can be turned off per profile.
ALTER TABLE profile_view_preferences ADD COLUMN recognize_people INTEGER NOT NULL DEFAULT 1 CHECK (recognize_people IN (0, 1));
