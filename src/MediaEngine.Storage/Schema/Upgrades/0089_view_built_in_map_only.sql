-- View (spec view-people-search WP2): a person can choose the built-in map only, so Places and the viewer's small map never ask an online
-- tile service for pictures. Pre-release rule: no back-fill; off by default.
ALTER TABLE profile_view_preferences ADD COLUMN built_in_map_only INTEGER NOT NULL DEFAULT 0 CHECK (built_in_map_only IN (0, 1));
