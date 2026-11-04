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

-- Fandom Lore can now describe Species and Concepts too (spec universe-contents, WP7).
CREATE TABLE plugin_lore_entities_new (
    id              BLOB PRIMARY KEY,
    source_id       BLOB NOT NULL REFERENCES plugin_lore_sources(id) ON DELETE CASCADE,
    universe_qid    TEXT NOT NULL,
    plugin_id       TEXT NOT NULL,
    external_key    TEXT NOT NULL,
    wikidata_qid    TEXT,
    label           TEXT NOT NULL,
    description     TEXT,
    entity_type     TEXT NOT NULL DEFAULT 'Unknown'
                        CHECK (entity_type IN ('Character', 'Location', 'Organization', 'Event', 'Object', 'Species', 'Concept', 'Unknown')),
    aliases_json    TEXT NOT NULL DEFAULT '[]',
    source_url      TEXT NOT NULL,
    confidence      REAL NOT NULL DEFAULT 0.0,
    evidence_json   TEXT NOT NULL DEFAULT '{}',
    created_at      TEXT NOT NULL,
    updated_at      TEXT NOT NULL,
    UNIQUE (source_id, external_key)
);

INSERT INTO plugin_lore_entities_new
    (id, source_id, universe_qid, plugin_id, external_key, wikidata_qid, label, description, entity_type,
     aliases_json, source_url, confidence, evidence_json, created_at, updated_at)
SELECT id, source_id, universe_qid, plugin_id, external_key, wikidata_qid, label, description, entity_type,
       aliases_json, source_url, confidence, evidence_json, created_at, updated_at
FROM plugin_lore_entities;

DROP TABLE plugin_lore_entities;
ALTER TABLE plugin_lore_entities_new RENAME TO plugin_lore_entities;

CREATE INDEX idx_plugin_lore_entities_qid
    ON plugin_lore_entities (wikidata_qid) WHERE wikidata_qid IS NOT NULL;

CREATE INDEX idx_plugin_lore_entities_universe
    ON plugin_lore_entities (universe_qid, entity_type);
