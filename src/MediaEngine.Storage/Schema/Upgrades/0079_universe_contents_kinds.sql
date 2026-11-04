-- tuvima:foreign-keys-off
-- Universe contents (spec universe-contents, WP1 and WP4). A Universe now holds seven kinds of thing: Characters, Places (Location),
-- Factions (Organization), Events, Objects, Species and Concepts, so the kind rule is widened. A person can also add a thing by hand
-- (added_by_user = 1): it keeps a local ID in wikidata_qid ('local:' plus a GUID), is never overwritten by a provider, and never leaves
-- the machine. SQLite cannot widen a CHECK in place, so the table is rebuilt with every row kept. Children refer to it by id only, and
-- the two indexes are recreated. The local overrides also gain aliases, a "belongs to" link (a character to a faction, a faction to a place)
-- and a hidden flag (hide a provider thing without deleting it). Pre-release rule: no conversion of anything older.
CREATE TABLE fictional_entities_new (
    id                       BLOB NOT NULL PRIMARY KEY,
    wikidata_qid             TEXT NOT NULL UNIQUE,
    label                    TEXT NOT NULL,
    description              TEXT,
    entity_sub_type          TEXT NOT NULL
                                 CHECK (entity_sub_type IN ('Character', 'Location', 'Organization', 'Event', 'Object', 'Species', 'Concept')),
    fictional_universe_qid   TEXT,
    fictional_universe_label TEXT,
    image_url                TEXT,
    local_image_path         TEXT,
    created_at               TEXT NOT NULL,
    enriched_at              TEXT,
    wikidata_revision_id     INTEGER,
    added_by_user            INTEGER NOT NULL DEFAULT 0 CHECK (added_by_user IN (0, 1))
);

INSERT INTO fictional_entities_new (id, wikidata_qid, label, description, entity_sub_type, fictional_universe_qid, fictional_universe_label,
                                    image_url, local_image_path, created_at, enriched_at, wikidata_revision_id)
SELECT id, wikidata_qid, label, description, entity_sub_type, fictional_universe_qid, fictional_universe_label,
       image_url, local_image_path, created_at, enriched_at, wikidata_revision_id
FROM fictional_entities;

DROP TABLE fictional_entities;

ALTER TABLE fictional_entities_new RENAME TO fictional_entities;

CREATE INDEX idx_fictional_entities_type
    ON fictional_entities (entity_sub_type);

CREATE INDEX idx_fictional_entities_universe
    ON fictional_entities (fictional_universe_qid);

ALTER TABLE fictional_entity_user_overrides ADD COLUMN aliases_json TEXT;
ALTER TABLE fictional_entity_user_overrides ADD COLUMN belongs_to_entity_id BLOB REFERENCES fictional_entities(id) ON DELETE SET NULL;
ALTER TABLE fictional_entity_user_overrides ADD COLUMN is_hidden INTEGER NOT NULL DEFAULT 0 CHECK (is_hidden IN (0, 1));
