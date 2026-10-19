-- Account notice dismissals: which one-time reminders ("Not now") an account has dismissed, so the choice follows the
-- person to any browser. One row per account and notice key (for example secure-account); deleting the account removes its rows.
CREATE TABLE account_notice_dismissals (
    account_id   BLOB NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    notice_key   TEXT NOT NULL,   -- a known reminder, for example secure-account
    dismissed_at TEXT NOT NULL,   -- ISO 8601 UTC
    PRIMARY KEY (account_id, notice_key)
);
