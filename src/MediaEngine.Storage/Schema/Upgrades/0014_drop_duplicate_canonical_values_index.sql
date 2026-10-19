-- idx_canonical_values_entity_key repeated canonical_values' own primary key (entity_id, key), so every write kept two
-- identical indexes and no read used the second one.
DROP INDEX IF EXISTS idx_canonical_values_entity_key;
