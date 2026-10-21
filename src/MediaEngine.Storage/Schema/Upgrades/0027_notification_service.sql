-- Notification service: the messages the Library sends to its administrators and how the channels they travel by are doing.
-- notifications is the in-Dashboard inbox: one row per message, with repeats of the same message inside a short window folded
-- into one row (occurrence_count) so a failing job does not bury the list. notification_channel_state remembers, per channel,
-- how many sends in a row failed; after enough failures the channel switches itself off and shows Needs attention until an
-- administrator turns it back on. notification_rate_marks makes the one-message-a-minute rule hold across restarts and counts
-- what was held back so the next message can say so. notification_settings holds the routes, quiet hours and privacy choice
-- as JSON. Message text is kept short and generic by default; nothing in these tables leaves the machine by itself.
CREATE TABLE notifications (
    id                TEXT    NOT NULL PRIMARY KEY,   -- GUID
    event_id          TEXT    NOT NULL,               -- the event from the notification catalogue, e.g. backup.failed
    title             TEXT    NOT NULL,               -- one short line
    body              TEXT    NOT NULL,               -- a few lines; may name an item, so it is shown in the Dashboard only unless the administrator chooses otherwise
    group_key         TEXT    NOT NULL,               -- repeats of the same event and subject share a key
    occurrence_count  INTEGER NOT NULL DEFAULT 1,     -- how many times it happened inside the grouping window
    created_at        TEXT    NOT NULL,               -- ISO 8601 UTC
    updated_at        TEXT    NOT NULL,               -- ISO 8601 UTC; moves forward when a repeat is folded in
    read_at           TEXT                            -- ISO 8601 UTC; null while unread
);
CREATE INDEX ix_notifications_updated ON notifications (updated_at DESC);
CREATE INDEX ix_notifications_group ON notifications (group_key, updated_at);

CREATE TABLE notification_channel_state (
    channel_id            TEXT    NOT NULL PRIMARY KEY,   -- dashboard, email or a channel plugin id
    consecutive_failures  INTEGER NOT NULL DEFAULT 0,
    state                 TEXT    NOT NULL,               -- healthy, needs_attention or off
    last_error            TEXT,                           -- plain words, never a secret
    last_success_at       TEXT,
    last_attempt_at       TEXT,
    switched_off_at       TEXT
);

CREATE TABLE notification_rate_marks (
    channel_id        TEXT    NOT NULL,
    event_id          TEXT    NOT NULL,
    last_sent_at      TEXT    NOT NULL,
    suppressed_count  INTEGER NOT NULL DEFAULT 0,         -- sends held back since last_sent_at
    PRIMARY KEY (channel_id, event_id)
);

CREATE TABLE notification_settings (
    setting_key  TEXT NOT NULL PRIMARY KEY,
    value_json   TEXT NOT NULL,
    updated_at   TEXT NOT NULL
);
