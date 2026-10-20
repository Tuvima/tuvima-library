-- Webhook delivery history: one row per attempt to send an event to a webhook receiver, including the "Send test" button's
-- test sends. application_webhook_deliveries keeps one row per event and only its latest state, so this table is what lets
-- the Integrations page show the last few attempts and what each one returned. A row holds the attempt's number, time and a
-- short plain result such as "HTTP 503" or "Receiver unavailable"; it never holds the event body, the signing secret or
-- anything the receiver sent back. Rows are pruned with the delivery rows (after seven days), but each webhook keeps its
-- newest twenty attempts regardless of age.
CREATE TABLE application_webhook_attempts (
    id             BLOB NOT NULL PRIMARY KEY,
    webhook_id     BLOB NOT NULL REFERENCES application_webhooks(id) ON DELETE CASCADE,
    delivery_id    BLOB NOT NULL,      -- the delivery row this attempt belongs to (may be pruned before the attempt is)
    event_id       BLOB NOT NULL,
    event_type     TEXT NOT NULL,      -- for example ingestion.completed, or webhook.test for a test send
    attempt_number INTEGER NOT NULL,   -- 1 for the first try of a delivery
    attempted_at   TEXT NOT NULL,      -- ISO 8601 UTC
    delivered      INTEGER NOT NULL,   -- 1 when the receiver accepted it
    result         TEXT NOT NULL,      -- short plain text; never a response body
    http_status    INTEGER NULL,       -- the receiver's HTTP status code, when it answered
    duration_ms    INTEGER NOT NULL,
    is_test        INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX ix_application_webhook_attempts_webhook_time
    ON application_webhook_attempts(webhook_id, attempted_at DESC);
