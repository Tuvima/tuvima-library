-- Media inspection holds: a media file whose technical inspection is waiting on a missing tool (for example FFmpeg).
-- The file is registered and identified as normal; this row is the durable "inspection pending" marker. It is removed
-- when the inspection succeeds or fails for a real reason, and it disappears with the asset.
CREATE TABLE media_inspection_holds (
    asset_id BLOB NOT NULL PRIMARY KEY REFERENCES media_assets(id) ON DELETE CASCADE,
    tool_id  TEXT NOT NULL,   -- the tool that is missing, for example ffmpeg
    held_at  TEXT NOT NULL    -- ISO 8601 UTC
);

CREATE INDEX ix_media_inspection_holds_tool ON media_inspection_holds(tool_id, held_at);
