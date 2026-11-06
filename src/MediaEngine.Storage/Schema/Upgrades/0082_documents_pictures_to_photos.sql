-- Pictures dropped on Documents: 1 once a profile ticked "Don't ask again" on the picture window, so its pictures go
-- straight to Photos. 0 (the default) means Library asks.
ALTER TABLE profile_view_preferences ADD COLUMN documents_pictures_to_photos INTEGER NOT NULL DEFAULT 0 CHECK (documents_pictures_to_photos IN (0, 1));
