-- View kinds (Library setup WP8a). Every View folder has a kind (photos, personal_videos or documents) that decides what it
-- accepts; every View item records its own kind and the profile that added it (shown as the profile name).
-- skipped_file_count is how many files the folder's last full scan passed over because they do not fit its kind.
ALTER TABLE view_sources ADD COLUMN content_kind TEXT NOT NULL DEFAULT 'photos'
    CHECK (content_kind IN ('photos', 'personal_videos', 'documents'));
ALTER TABLE view_sources ADD COLUMN added_by_profile_id BLOB REFERENCES profiles(id) ON DELETE SET NULL;
ALTER TABLE view_sources ADD COLUMN skipped_file_count INTEGER NOT NULL DEFAULT 0 CHECK (skipped_file_count >= 0);

-- NULL only for audio and other files, which no View kind accepts. The permission checks gate on this column.
ALTER TABLE local_items ADD COLUMN content_kind TEXT
    CHECK (content_kind IS NULL OR content_kind IN ('photos', 'personal_videos', 'documents'));
ALTER TABLE local_items ADD COLUMN added_by_profile_id BLOB REFERENCES profiles(id) ON DELETE SET NULL;
CREATE INDEX ix_local_items_added_by ON local_items (added_by_profile_id);

-- Per-kind permission, like documents_enabled.
ALTER TABLE profile_view_policies ADD COLUMN photos_enabled INTEGER NOT NULL DEFAULT 1 CHECK (photos_enabled IN (0, 1));
ALTER TABLE profile_view_policies ADD COLUMN personal_videos_enabled INTEGER NOT NULL DEFAULT 1 CHECK (personal_videos_enabled IN (0, 1));

-- An item always has a kind: when a row is written without one, it takes the kind that matches its file type.
-- The same rule fills the rows that already exist, so no item is ever outside the permission checks.
UPDATE local_items SET content_kind = CASE media_kind
        WHEN 'image' THEN 'photos' WHEN 'video' THEN 'personal_videos' WHEN 'document' THEN 'documents' END
 WHERE content_kind IS NULL;

CREATE TRIGGER trg_local_items_content_kind_default AFTER INSERT ON local_items
WHEN NEW.content_kind IS NULL AND NEW.media_kind IN ('image', 'video', 'document')
BEGIN
    UPDATE local_items SET content_kind = CASE NEW.media_kind
            WHEN 'image' THEN 'photos' WHEN 'video' THEN 'personal_videos' ELSE 'documents' END
     WHERE id = NEW.id;
END;
