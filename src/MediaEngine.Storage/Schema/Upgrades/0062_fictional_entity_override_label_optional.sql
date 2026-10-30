-- tuvima:foreign-keys-off
-- Editing a character, place or organisation in the Chronicle explorer saves each field on its own: saving only the description must not
-- copy the provider's name into a local override (and the other way round), or later provider updates to the untouched field would be hidden.
-- The local name therefore becomes optional (NULL means the provider's name keeps showing). SQLite cannot drop NOT NULL in place, so the table is
-- rebuilt with its rows kept. Nothing refers to this table and it has no indexes, triggers or views.
CREATE TABLE fictional_entity_user_overrides_new (
    entity_id      BLOB NOT NULL PRIMARY KEY REFERENCES fictional_entities(id) ON DELETE CASCADE,
    label          TEXT,
    description    TEXT,
    updated_at     TEXT NOT NULL DEFAULT (datetime('now'))
);

INSERT INTO fictional_entity_user_overrides_new (entity_id, label, description, updated_at)
SELECT entity_id, label, description, updated_at FROM fictional_entity_user_overrides;

DROP TABLE fictional_entity_user_overrides;

ALTER TABLE fictional_entity_user_overrides_new RENAME TO fictional_entity_user_overrides;
