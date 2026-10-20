-- Approval records: one append-only row each time a server administrator (or an application holding the matching
-- permission) confirmed what an item can do before it was switched on. Covers plugins, Local AI features, application
-- credentials and webhooks. statement_json is exactly what the person was shown; statement_hash is its fingerprint, so a
-- changed statement (for example a plugin that now asks for another permission) is asked about again. Rows are never
-- updated or deleted.
CREATE TABLE approval_records (
    id                          BLOB NOT NULL PRIMARY KEY,
    item_kind                   TEXT NOT NULL,   -- plugin | ai_feature | application | application_credential | webhook
    item_id                     TEXT NOT NULL,   -- plugin id, AI feature key, credential id, application id or webhook id
    statement_hash              TEXT NOT NULL,   -- SHA-256, lower-case hex, of the canonical statement
    statement_json              TEXT NOT NULL,   -- the statement as shown (title, can-do lines, data out, notes); never a secret value
    approved_by_account_id      BLOB NULL,       -- the account that confirmed; NULL when an application confirmed
    approved_by_profile_id      BLOB NULL,       -- the profile that was active when a person confirmed
    approved_by_application_id  BLOB NULL,       -- the application that confirmed; NULL when a person confirmed
    approved_by_name            TEXT NOT NULL,   -- profile display name (or application name) at the time
    approved_at                 TEXT NOT NULL    -- ISO 8601 UTC
);

CREATE INDEX ix_approval_records_item ON approval_records(item_kind, item_id, approved_at);
