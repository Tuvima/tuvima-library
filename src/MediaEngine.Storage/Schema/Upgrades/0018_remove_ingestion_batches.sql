-- Ingestion batches are gone. The live ingestion numbers come from each file's own intake operation (media_operations rows of
-- type 'ingestion.file') and its identity job, so the batch tables, the batch / run link on six other tables, and the indexes
-- that led with that link are removed. Nothing here touches a file's own rows: only the link columns and the two batch tables.
--
-- Indexes first (SQLite will not drop a column an index still names).
DROP INDEX IF EXISTS idx_identity_jobs_activity_latest;
DROP INDEX IF EXISTS idx_identity_jobs_ingestion_run_id;
DROP INDEX IF EXISTS idx_identity_jobs_run_entity_updated;
DROP INDEX IF EXISTS idx_ingestion_batch_artifacts_artifact;
DROP INDEX IF EXISTS idx_ingestion_batch_artifacts_batch;
DROP INDEX IF EXISTS idx_ingestion_batch_artifacts_parent;
DROP INDEX IF EXISTS idx_ingestion_batch_artifacts_type;
DROP INDEX IF EXISTS idx_ingestion_batches_created;
DROP INDEX IF EXISTS idx_ingestion_batches_status;
DROP INDEX IF EXISTS idx_ingestion_log_run;
DROP INDEX IF EXISTS idx_ingestion_log_run_created;
DROP INDEX IF EXISTS idx_media_operation_events_batch;
DROP INDEX IF EXISTS idx_media_operation_events_batch_entity;
DROP INDEX IF EXISTS idx_media_operations_batch;
DROP INDEX IF EXISTS idx_media_operations_batch_entity_type;
DROP INDEX IF EXISTS idx_system_activity_run_entity_action;

DROP TABLE IF EXISTS ingestion_batch_artifacts;
DROP TABLE IF EXISTS ingestion_batches;

ALTER TABLE identity_jobs DROP COLUMN ingestion_run_id;
ALTER TABLE ingestion_log DROP COLUMN ingestion_run_id;
ALTER TABLE system_activity DROP COLUMN ingestion_run_id;
ALTER TABLE entity_events DROP COLUMN ingestion_run_id;
ALTER TABLE media_operations DROP COLUMN batch_id;
ALTER TABLE media_operation_events DROP COLUMN batch_id;

-- The newest identity job of a file is found by the file's id and the job's times (the live ingestion list reads it for every
-- file in the active period). The old index led with the run link; the file's id is the part the reads use. The old
-- (run, entity, updated) index was a prefix of it and is not recreated. media_operations needs no replacement: its status,
-- entity and (type, status) indexes already cover what the batch indexes served.
CREATE INDEX idx_identity_jobs_activity_latest
    ON identity_jobs(entity_id, updated_at, created_at);
