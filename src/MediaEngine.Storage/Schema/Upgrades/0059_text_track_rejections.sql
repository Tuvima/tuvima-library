-- "This one is wrong" on lyrics or subtitles (Plugin architecture spec C24). The Library remembers which source result a person
-- marked wrong for one item so it is never picked again; it keeps only the source's name and the result's id, never the text.
CREATE TABLE text_track_rejections (
    asset_id    BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
    kind        TEXT NOT NULL CHECK(kind IN ('Lyrics','Subtitles')),
    provider    TEXT NOT NULL,
    source_id   TEXT NOT NULL,
    rejected_at TEXT NOT NULL DEFAULT (datetime('now')),
    PRIMARY KEY (asset_id, kind, provider, source_id)
);
