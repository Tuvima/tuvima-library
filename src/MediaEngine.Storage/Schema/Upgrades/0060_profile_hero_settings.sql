-- Home hero settings: how each person wants the big banner at the top of Home to behave (Home page redesign spec D5).
-- One row per profile. A profile with no row gets the defaults: hero on, five slides, every type allowed, in-progress items
-- first and new items after. These only narrow what the hero rules choose from; they never change the rules' order.
-- Deleting a profile removes that person's row.
CREATE TABLE profile_hero_settings (
    profile_id     BLOB    NOT NULL PRIMARY KEY REFERENCES profiles(id) ON DELETE CASCADE,
    hero_enabled   INTEGER NOT NULL DEFAULT 1 CHECK (hero_enabled IN (0, 1)),
    slide_count    INTEGER NOT NULL DEFAULT 5 CHECK (slide_count BETWEEN 1 AND 5),
    allow_watch    INTEGER NOT NULL DEFAULT 1 CHECK (allow_watch IN (0, 1)),
    allow_read     INTEGER NOT NULL DEFAULT 1 CHECK (allow_read IN (0, 1)),
    allow_listen   INTEGER NOT NULL DEFAULT 1 CHECK (allow_listen IN (0, 1)),
    continued_only INTEGER NOT NULL DEFAULT 0 CHECK (continued_only IN (0, 1)),
    updated_at     TEXT    NOT NULL   -- ISO 8601 UTC
);
