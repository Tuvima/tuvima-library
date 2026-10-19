-- Scheduled task runs: one durable row per run of a recurring background task (activity pruning, Local AI batches,
-- plugin segment detection, and so on). System > Tools and requirements > Scheduled tasks reads the latest row per task,
-- and plugin job history survives an Engine restart. Rows are pruned to 90 days and 500 per task when a run finishes.
CREATE TABLE scheduled_task_runs (
    id              BLOB    NOT NULL PRIMARY KEY,
    task_key        TEXT    NOT NULL,   -- snake_case task key, for example vibe_tags
    plugin_id       TEXT    NULL,       -- set when the run belongs to a plugin
    started_at      TEXT    NOT NULL,   -- ISO 8601 UTC
    finished_at     TEXT    NULL,       -- ISO 8601 UTC; NULL while the run is going
    outcome         TEXT    NOT NULL,   -- running | succeeded | failed | skipped | interrupted
    items_processed INTEGER NULL,
    message         TEXT    NULL        -- plain-English result, failure message or skip reason
);

CREATE INDEX ix_scheduled_task_runs_task_started ON scheduled_task_runs(task_key, started_at DESC);
