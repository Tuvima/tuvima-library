-- AI usage records: one row for every request that reached the Local AI engine room (text model and text-from-image
-- reading alike), whether it succeeded, failed, was unavailable, refused over a plugin's hourly allowance, cancelled or
-- timed out. Requests that were only deferred (background polls) write nothing. Plugin allowances are counted from these
-- rows over the last hour, so they survive an Engine restart; Settings > Local AI > Usage reads them. Rows older than 90
-- days are pruned.
CREATE TABLE ai_usage_records (
    id           BLOB    NOT NULL PRIMARY KEY,
    started_at   TEXT    NOT NULL,   -- ISO 8601 UTC; when the request reached the engine room
    caller_kind  TEXT    NOT NULL,   -- person | ingestion | library_background | plugin
    caller_id    TEXT    NOT NULL,   -- stable key: a feature key such as smart_labeling, or the plugin manifest id
    ability      TEXT    NOT NULL,   -- write_text_quick | write_text_careful | write_text_long | extract_structured | classify | read_text_from_image | ...
    engine       TEXT    NOT NULL,   -- text_model | tesseract | none
    wait_ms      INTEGER NOT NULL,   -- time spent waiting in the queue
    duration_ms  INTEGER NOT NULL,   -- time spent running once it had a turn
    outcome      TEXT    NOT NULL,   -- succeeded | failed | unavailable | refused_over_budget | cancelled | timed_out
    message      TEXT    NULL        -- plain-English result or reason; never the prompt or the text read
);

CREATE INDEX ix_ai_usage_records_caller ON ai_usage_records(caller_kind, caller_id, started_at);
CREATE INDEX ix_ai_usage_records_started ON ai_usage_records(started_at);
