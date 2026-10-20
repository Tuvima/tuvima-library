-- One Universe record. A Series shelf now names the Universe it belongs to (universe_qid), even when it is the only Series of that
-- Universe, and a Universe shelf names the record it presents (always equal to its own wikidata_qid). The narrative_roots row for
-- that QID is the Universe record; the shelf is how the Library presents it. At most one Universe shelf may present one record.
ALTER TABLE collections ADD COLUMN universe_qid TEXT REFERENCES narrative_roots(qid) ON DELETE SET NULL;

CREATE UNIQUE INDEX idx_collections_one_universe_shelf
    ON collections(universe_qid)
    WHERE collection_type = 'Universe' AND universe_qid IS NOT NULL;

CREATE INDEX idx_collections_universe_qid ON collections(universe_qid);
