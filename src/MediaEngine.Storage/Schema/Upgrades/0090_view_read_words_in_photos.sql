-- View (spec view-people-search WP5): a person can switch on reading the words inside their own photos (screenshots, receipts) so a search finds them.
-- Pre-release rule: no back-fill; off by default, so nothing is scanned until the person turns it on.
ALTER TABLE profile_view_preferences ADD COLUMN read_words_in_photos INTEGER NOT NULL DEFAULT 0 CHECK (read_words_in_photos IN (0, 1));
