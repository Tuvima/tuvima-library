-- Profile notice dismissals: which Settings attention lines (Needs Review, missing tools) a profile has dismissed with
-- the X, and what was true when it did. A line returns only when something new needs attention, which the Dashboard
-- works out by comparing the stored fingerprint with the present one. One row per profile and notice key; deleting the
-- profile removes its rows.
CREATE TABLE profile_notice_dismissals (
    profile_id   BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    notice_key   TEXT NOT NULL,   -- a known notice, for example needs-review or missing-tools
    fingerprint  TEXT NOT NULL,   -- what was true when it was dismissed (the review count, or the sorted missing tool ids)
    dismissed_at TEXT NOT NULL,   -- ISO 8601 UTC
    PRIMARY KEY (profile_id, notice_key)
);
