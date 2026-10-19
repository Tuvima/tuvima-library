-- Per-device live playback state and companion sessions. The shared player (player_sessions) keeps one queue and one
-- transport per profile; this adds a separate record per device, written from each device's own heartbeats, so two
-- devices on one profile never overwrite each other's live state. A companion session is one device choosing another
-- device to follow, or holding a position the person set by hand.
CREATE TABLE player_device_states (
    profile_id        BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    device_id         TEXT NOT NULL,
    client            TEXT NOT NULL,
    player_session_id BLOB,
    asset_id          BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    work_id           BLOB REFERENCES works(id) ON DELETE SET NULL,
    playback_state    TEXT NOT NULL CHECK (playback_state IN ('playing','paused','ended')),
    position_seconds  REAL NOT NULL DEFAULT 0 CHECK (position_seconds >= 0),
    duration_seconds  REAL CHECK (duration_seconds IS NULL OR duration_seconds >= 0),
    updated_at        TEXT NOT NULL,
    PRIMARY KEY (profile_id, device_id)
);

CREATE TABLE companion_sessions (
    profile_id              BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    companion_device_id     TEXT NOT NULL,
    mode                    TEXT NOT NULL CHECK (mode IN ('follow','manual')),
    followed_device_id      TEXT,
    manual_work_id          BLOB REFERENCES works(id) ON DELETE CASCADE,
    manual_chapter_index    INTEGER CHECK (manual_chapter_index IS NULL OR manual_chapter_index >= 0),
    manual_position_seconds REAL CHECK (manual_position_seconds IS NULL OR manual_position_seconds >= 0),
    updated_at              TEXT NOT NULL,
    PRIMARY KEY (profile_id, companion_device_id),
    CHECK ((mode = 'follow' AND followed_device_id IS NOT NULL AND manual_work_id IS NULL)
        OR (mode = 'manual' AND manual_work_id IS NOT NULL AND followed_device_id IS NULL))
);
