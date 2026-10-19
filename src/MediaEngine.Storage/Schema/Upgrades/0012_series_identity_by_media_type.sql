-- A Series is its Wikidata Series QID plus its media type: a book Series and an audiobook Series that share a QID are two
-- shelves. collections.media_type names the media type a Series shelf holds (null on shelves that are not Series).
ALTER TABLE collections ADD COLUMN media_type TEXT;
CREATE INDEX idx_collections_series_identity ON collections(wikidata_qid, media_type);

-- A hydration used to be keyed by the Series QID alone, which allowed only one media type per QID. It is now one per
-- shelf (the collection holding the manifest), so each media type keeps its own manifest of the same Series.
CREATE TABLE series_manifest_hydrations_new (
    series_qid            TEXT NOT NULL,
    collection_id         BLOB NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
    series_label          TEXT,
    manifest_source       TEXT NOT NULL DEFAULT 'Tuvima.Wikidata',
    manifest_version      TEXT,
    manifest_hash         TEXT,
    known_item_qids_hash  TEXT,
    warnings_json         TEXT NOT NULL DEFAULT '[]',
    api_metadata_json     TEXT NOT NULL DEFAULT '{}',
    last_hydrated_at      TEXT NOT NULL,
    created_at            TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at            TEXT NOT NULL DEFAULT (datetime('now')),
    PRIMARY KEY (collection_id, series_qid)
);

INSERT INTO series_manifest_hydrations_new
    (series_qid, collection_id, series_label, manifest_source, manifest_version, manifest_hash, known_item_qids_hash,
     warnings_json, api_metadata_json, last_hydrated_at, created_at, updated_at)
SELECT series_qid, collection_id, series_label, manifest_source, manifest_version, manifest_hash, known_item_qids_hash,
       warnings_json, api_metadata_json, last_hydrated_at, created_at, updated_at
FROM series_manifest_hydrations;

DROP TABLE series_manifest_hydrations;
ALTER TABLE series_manifest_hydrations_new RENAME TO series_manifest_hydrations;
CREATE INDEX idx_series_manifest_hydrations_series_qid ON series_manifest_hydrations(series_qid);
