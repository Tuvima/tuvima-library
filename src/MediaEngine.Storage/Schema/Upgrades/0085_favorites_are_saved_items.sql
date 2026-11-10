-- Favorite is a list, Like and Dislike are taste (owner decision 2026-11-10). The heart means "added to a list": a profile's Favorites
-- (songs) and My List (every other kind) are its saved items. Like and Dislike stay the only reactions, kept apart for the taste profiles
-- to come. Pre-release: every old Love reaction becomes a saved item (dated when it was set; one already saved stays as it was) and the
-- reaction is dropped, then profile_reactions is rebuilt so it no longer accepts Love.
INSERT OR IGNORE INTO profile_saved_items (profile_id, entity_kind, entity_id, saved_at)
SELECT profile_id, entity_kind, entity_id, updated_at FROM profile_reactions WHERE reaction = 'Love';

DELETE FROM profile_reactions WHERE reaction = 'Love';

CREATE TABLE profile_reactions_new (
    profile_id   BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    entity_kind  TEXT NOT NULL CHECK(entity_kind IN ('Movie','TvShow','Book','Comic','Audiobook','Album','Song','Collection','Playlist')),
    entity_id    BLOB NOT NULL,
    reaction     TEXT NOT NULL CHECK(reaction IN ('Like','Dislike')),
    updated_at   TEXT NOT NULL,
    PRIMARY KEY (profile_id, entity_kind, entity_id)
);

INSERT INTO profile_reactions_new (profile_id, entity_kind, entity_id, reaction, updated_at)
SELECT profile_id, entity_kind, entity_id, reaction, updated_at FROM profile_reactions;

DROP TABLE profile_reactions;
ALTER TABLE profile_reactions_new RENAME TO profile_reactions;

CREATE INDEX idx_profile_reactions_recent
    ON profile_reactions(profile_id, reaction, updated_at DESC);
