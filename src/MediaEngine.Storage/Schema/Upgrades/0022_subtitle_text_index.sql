-- Subtitle text index (Universes U-WP14). Subtitle lines of a film or episode become time-stamped, searchable text, and plain
-- name matching (no AI) tags the time windows in which a Universe character, place or other entity is named.
-- subtitle_index_assets records that a video file was probed once (its chapter times and embedded subtitle streams). It is the
-- durable job row of the background indexer: no row means the file still waits.
CREATE TABLE subtitle_index_assets (
    asset_id             BLOB NOT NULL PRIMARY KEY REFERENCES media_assets(id) ON DELETE CASCADE,
    state                TEXT NOT NULL CHECK (state IN ('Done', 'Failed')),
    chapter_count        INTEGER NOT NULL DEFAULT 0,
    embedded_stream_count INTEGER NOT NULL DEFAULT 0,
    attempt_count        INTEGER NOT NULL DEFAULT 0,
    last_error           TEXT,
    next_retry_at        TEXT,
    probed_at            TEXT NOT NULL
);

-- Chapter start and end times of a video file, in milliseconds.
CREATE TABLE media_asset_chapters (
    asset_id       BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    chapter_index  INTEGER NOT NULL CHECK (chapter_index >= 0),
    start_ms       INTEGER NOT NULL CHECK (start_ms >= 0),
    end_ms         INTEGER CHECK (end_ms IS NULL OR end_ms >= start_ms),
    title          TEXT,
    PRIMARY KEY (asset_id, chapter_index)
);

-- One indexed subtitle track: a stored, normalised text track (any source) or a subtitle stream embedded in the video file.
-- source_stamp changes when the stored track changes, so the track is read again. entity_signature records which Universe
-- entities were matched against, so tags are refreshed when the Universe graph grows.
CREATE TABLE subtitle_index_tracks (
    id                BLOB NOT NULL PRIMARY KEY,
    asset_id          BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    source_kind       TEXT NOT NULL CHECK (source_kind IN ('TextTrack', 'Embedded')),
    text_track_id     BLOB REFERENCES text_tracks(id) ON DELETE CASCADE,
    stream_index      INTEGER,
    language          TEXT NOT NULL DEFAULT 'und',
    state             TEXT NOT NULL CHECK (state IN ('Indexed', 'Failed')),
    source_stamp      TEXT NOT NULL,
    line_count        INTEGER NOT NULL DEFAULT 0,
    tag_count         INTEGER NOT NULL DEFAULT 0,
    entity_signature  TEXT,
    attempt_count     INTEGER NOT NULL DEFAULT 0,
    last_error        TEXT,
    next_retry_at     TEXT,
    indexed_at        TEXT NOT NULL,
    tagged_at         TEXT
);

CREATE INDEX idx_subtitle_index_tracks_asset ON subtitle_index_tracks(asset_id);
CREATE INDEX idx_subtitle_index_tracks_text_track ON subtitle_index_tracks(text_track_id);

-- The time-stamped lines of an indexed track, in milliseconds from the start of the file.
CREATE TABLE subtitle_lines (
    track_id    BLOB NOT NULL REFERENCES subtitle_index_tracks(id) ON DELETE CASCADE,
    line_index  INTEGER NOT NULL CHECK (line_index >= 0),
    start_ms    INTEGER NOT NULL CHECK (start_ms >= 0),
    end_ms      INTEGER NOT NULL CHECK (end_ms >= start_ms),
    text        TEXT NOT NULL,
    PRIMARY KEY (track_id, line_index)
);

CREATE INDEX idx_subtitle_lines_time ON subtitle_lines(track_id, start_ms);

-- A time window in which one Universe entity is named, found by plain name matching. Data only: nothing here is shown yet.
CREATE TABLE subtitle_name_tags (
    track_id         BLOB NOT NULL REFERENCES subtitle_index_tracks(id) ON DELETE CASCADE,
    entity_id        BLOB NOT NULL REFERENCES fictional_entities(id) ON DELETE CASCADE,
    window_start_ms  INTEGER NOT NULL CHECK (window_start_ms >= 0),
    window_end_ms    INTEGER NOT NULL CHECK (window_end_ms >= window_start_ms),
    first_line_index INTEGER NOT NULL,
    last_line_index  INTEGER NOT NULL,
    mention_count    INTEGER NOT NULL CHECK (mention_count > 0),
    matched_text     TEXT NOT NULL,
    PRIMARY KEY (track_id, entity_id, window_start_ms)
);

CREATE INDEX idx_subtitle_name_tags_entity ON subtitle_name_tags(entity_id);
CREATE INDEX idx_subtitle_name_tags_time ON subtitle_name_tags(track_id, window_start_ms);
