-- Vault images and galleries (Videos spec WP16, D44). Images in the Vault are ordinary View items (local_items) kept in a library of
-- their own, so View's scanner, thumbnails, timeline, search, viewer and Smart Gallery rules are reused. What makes them Vault is the
-- library they sit in, never a flag on the item: vault_image_libraries lists those libraries, and every table below belongs to them.
-- Nothing here joins the View galleries, tags, people or sources, and the triggers keep the two worlds apart even if a caller
-- gets it wrong.

-- A View-style library that holds Vault images. library_id is local_items.library_id (a GUID blob); vault_library_id is the Vault
-- library (text) it belongs to, the same id media_assets.library_id carries for that library's videos.
CREATE TABLE vault_image_libraries (
    library_id       BLOB NOT NULL PRIMARY KEY,
    vault_library_id TEXT NOT NULL UNIQUE COLLATE NOCASE REFERENCES vault_libraries(library_id) ON DELETE CASCADE,
    created_at       TEXT NOT NULL
);

-- One folder of images on disk, shown as a gallery of its own. folder_key is a hash of the folder's place under the library folder, so
-- no folder path is stored or put in an address; name is the folder's own name.
CREATE TABLE vault_image_folders (
    id         BLOB NOT NULL PRIMARY KEY,
    library_id BLOB NOT NULL REFERENCES vault_image_libraries(library_id) ON DELETE CASCADE,
    name       TEXT NOT NULL,
    folder_key TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE (library_id, folder_key)
);

-- Every item of a Vault image library (added by a trigger, so nothing in such a library can be missed). recycle_entries holds the
-- recycle bin entries (a JSON list of ids, one per file) while the image is deleted (D14).
CREATE TABLE vault_image_items (
    item_id          BLOB NOT NULL PRIMARY KEY REFERENCES local_items(id) ON DELETE CASCADE,
    library_id       BLOB NOT NULL REFERENCES vault_image_libraries(library_id) ON DELETE CASCADE,
    folder_id        BLOB REFERENCES vault_image_folders(id) ON DELETE SET NULL,
    recycle_entries  TEXT CHECK (recycle_entries IS NULL OR json_valid(recycle_entries)),
    added_at         TEXT NOT NULL
);

-- Tags of Vault images are the Vault's own (vault_tags) and never join the shared tag list (D27).
CREATE TABLE vault_image_tags (
    item_id  BLOB NOT NULL REFERENCES vault_image_items(item_id) ON DELETE CASCADE,
    tag_id   BLOB NOT NULL REFERENCES vault_tags(id) ON DELETE CASCADE,
    added_at TEXT NOT NULL,
    PRIMARY KEY (item_id, tag_id)
);

-- Galleries made by hand or by a rule (the same rule document View's Smart Galleries use), for Vault images only.
CREATE TABLE vault_galleries (
    id                    BLOB NOT NULL PRIMARY KEY,
    library_id            BLOB NOT NULL REFERENCES vault_image_libraries(library_id) ON DELETE CASCADE,
    created_by_profile_id BLOB REFERENCES profiles(id) ON DELETE SET NULL,
    name                  TEXT NOT NULL,
    description           TEXT,
    gallery_kind          TEXT NOT NULL CHECK (gallery_kind IN ('manual', 'smart')),
    smart_rule_json       TEXT CHECK (smart_rule_json IS NULL OR json_valid(smart_rule_json)),
    cover_item_id         BLOB REFERENCES local_items(id) ON DELETE SET NULL,
    sort_order            INTEGER NOT NULL DEFAULT 0,
    created_at            TEXT NOT NULL,
    updated_at            TEXT NOT NULL,
    CHECK ((gallery_kind = 'manual' AND smart_rule_json IS NULL)
        OR (gallery_kind = 'smart' AND smart_rule_json IS NOT NULL))
);

CREATE TABLE vault_gallery_items (
    gallery_id BLOB NOT NULL REFERENCES vault_galleries(id) ON DELETE CASCADE,
    item_id    BLOB NOT NULL REFERENCES vault_image_items(item_id) ON DELETE CASCADE,
    position   INTEGER NOT NULL DEFAULT 0,
    added_at   TEXT NOT NULL,
    PRIMARY KEY (gallery_id, item_id)
);

CREATE INDEX ix_vault_image_items_folder  ON vault_image_items(folder_id, item_id);
CREATE INDEX ix_vault_image_items_library ON vault_image_items(library_id, item_id);
CREATE INDEX ix_vault_image_tags_tag      ON vault_image_tags(tag_id, item_id);
CREATE INDEX ix_vault_galleries_library   ON vault_galleries(library_id, sort_order, updated_at DESC, id);
CREATE INDEX ix_vault_gallery_items_item  ON vault_gallery_items(item_id, gallery_id);

-- Everything that lands in a Vault image library is a Vault image, in the same transaction that adds it.
CREATE TRIGGER trg_local_items_vault_image_register
AFTER INSERT ON local_items
WHEN EXISTS (SELECT 1 FROM vault_image_libraries WHERE library_id = NEW.library_id)
BEGIN
    INSERT INTO vault_image_items (item_id, library_id, added_at) VALUES (NEW.id, NEW.library_id, NEW.created_at);
END;

-- A Vault image is stored as a shared item with no owner, so the two checks that keep a Shared item in the household's Shared library
-- also let it sit in a Vault image library. They are otherwise exactly as before: a Personal item must still match its owner and Personal
-- Space library, and a Shared item must still be in the Shared library or in a Vault image library.
DROP TRIGGER trg_local_items_scope_insert;
CREATE TRIGGER trg_local_items_scope_insert BEFORE INSERT ON local_items BEGIN
    SELECT CASE WHEN NEW.scope_kind='personal' AND NOT EXISTS (SELECT 1 FROM view_personal_spaces s WHERE s.id=NEW.personal_space_id AND s.library_id=NEW.library_id AND s.owner_profile_id=NEW.owner_profile_id) THEN RAISE(ABORT,'personal View item must match its owner and Personal Space library') WHEN NEW.scope_kind='shared' AND NOT EXISTS (SELECT 1 FROM view_shared_library s WHERE s.library_id=NEW.library_id) AND NOT EXISTS (SELECT 1 FROM vault_image_libraries v WHERE v.library_id=NEW.library_id) THEN RAISE(ABORT,'Shared View item must match the Shared library') END;
END;

DROP TRIGGER trg_local_items_scope_update;
CREATE TRIGGER trg_local_items_scope_update BEFORE UPDATE OF scope_kind,personal_space_id,owner_profile_id,library_id ON local_items BEGIN
    SELECT CASE WHEN NEW.scope_kind='personal' AND NOT EXISTS (SELECT 1 FROM view_personal_spaces s WHERE s.id=NEW.personal_space_id AND s.library_id=NEW.library_id AND s.owner_profile_id=NEW.owner_profile_id) THEN RAISE(ABORT,'personal View item must match its owner and Personal Space library') WHEN NEW.scope_kind='shared' AND NOT EXISTS (SELECT 1 FROM view_shared_library s WHERE s.library_id=NEW.library_id) AND NOT EXISTS (SELECT 1 FROM vault_image_libraries v WHERE v.library_id=NEW.library_id) THEN RAISE(ABORT,'Shared View item must match the Shared library') END;
END;

-- A file never moves into or out of a Vault image library, and the library of an ordinary View item can never become one.
CREATE TRIGGER trg_local_items_vault_library_fixed
BEFORE UPDATE OF library_id ON local_items
WHEN OLD.library_id IS NOT NEW.library_id
 AND (EXISTS (SELECT 1 FROM vault_image_libraries WHERE library_id = OLD.library_id)
   OR EXISTS (SELECT 1 FROM vault_image_libraries WHERE library_id = NEW.library_id))
BEGIN SELECT RAISE(ABORT, 'Items cannot be moved into or out of a Vault image library.'); END;

CREATE TRIGGER trg_vault_image_libraries_not_view
BEFORE INSERT ON vault_image_libraries
WHEN EXISTS (SELECT 1 FROM local_items WHERE library_id = NEW.library_id)
  OR EXISTS (SELECT 1 FROM view_personal_spaces WHERE library_id = NEW.library_id)
  OR EXISTS (SELECT 1 FROM view_shared_library WHERE library_id = NEW.library_id)
BEGIN SELECT RAISE(ABORT, 'A library that View already uses cannot hold Vault images.'); END;

CREATE TRIGGER trg_view_personal_spaces_not_vault
BEFORE INSERT ON view_personal_spaces
WHEN EXISTS (SELECT 1 FROM vault_image_libraries WHERE library_id = NEW.library_id)
BEGIN SELECT RAISE(ABORT, 'A Vault image library cannot be a View library.'); END;

CREATE TRIGGER trg_view_shared_library_not_vault
BEFORE INSERT ON view_shared_library
WHEN EXISTS (SELECT 1 FROM vault_image_libraries WHERE library_id = NEW.library_id)
BEGIN SELECT RAISE(ABORT, 'A Vault image library cannot be a View library.'); END;

-- A Vault library cannot be unmarked while Vault images are still in it (the same rule as its videos).
CREATE TRIGGER trg_vault_libraries_images_in_use
BEFORE DELETE ON vault_libraries
WHEN EXISTS (SELECT 1 FROM vault_image_items vii
               JOIN vault_image_libraries vil ON vil.library_id = vii.library_id
              WHERE vil.vault_library_id = OLD.library_id)
BEGIN SELECT RAISE(ABORT, 'A Vault library that still has items cannot be removed.'); END;

-- Vault images never join View galleries, View covers or the shared tag list (D27, D44).
CREATE TRIGGER trg_view_gallery_items_no_vault_image
BEFORE INSERT ON view_gallery_items
WHEN EXISTS (SELECT 1 FROM vault_image_items WHERE item_id = NEW.item_id)
BEGIN SELECT RAISE(ABORT, 'A Vault image cannot be added to a View gallery.'); END;

CREATE TRIGGER trg_view_galleries_cover_no_vault_image_insert
BEFORE INSERT ON view_galleries
WHEN NEW.cover_item_id IS NOT NULL AND EXISTS (SELECT 1 FROM vault_image_items WHERE item_id = NEW.cover_item_id)
BEGIN SELECT RAISE(ABORT, 'A Vault image cannot be a View gallery cover.'); END;

CREATE TRIGGER trg_view_galleries_cover_no_vault_image_update
BEFORE UPDATE OF cover_item_id ON view_galleries
WHEN NEW.cover_item_id IS NOT NULL AND EXISTS (SELECT 1 FROM vault_image_items WHERE item_id = NEW.cover_item_id)
BEGIN SELECT RAISE(ABORT, 'A Vault image cannot be a View gallery cover.'); END;

CREATE TRIGGER trg_local_item_tag_links_no_vault_image
BEFORE INSERT ON local_item_tag_links
WHEN EXISTS (SELECT 1 FROM vault_image_items WHERE item_id = NEW.item_id)
BEGIN SELECT RAISE(ABORT, 'A Vault image cannot use the shared tag list.'); END;

-- Vault galleries and tags stay inside the library of the image.
CREATE TRIGGER trg_vault_gallery_items_same_library
BEFORE INSERT ON vault_gallery_items
WHEN (SELECT library_id FROM vault_image_items WHERE item_id = NEW.item_id)
     IS NOT (SELECT library_id FROM vault_galleries WHERE id = NEW.gallery_id)
BEGIN SELECT RAISE(ABORT, 'A gallery can only hold images of its own Vault library.'); END;

CREATE TRIGGER trg_vault_galleries_cover_same_library_insert
BEFORE INSERT ON vault_galleries
WHEN NEW.cover_item_id IS NOT NULL
 AND NOT EXISTS (SELECT 1 FROM vault_image_items WHERE item_id = NEW.cover_item_id AND library_id = NEW.library_id)
BEGIN SELECT RAISE(ABORT, 'A gallery cover must be an image of its own Vault library.'); END;

CREATE TRIGGER trg_vault_galleries_cover_same_library
BEFORE UPDATE OF cover_item_id ON vault_galleries
WHEN NEW.cover_item_id IS NOT NULL
 AND NOT EXISTS (SELECT 1 FROM vault_image_items WHERE item_id = NEW.cover_item_id AND library_id = NEW.library_id)
BEGIN SELECT RAISE(ABORT, 'A gallery cover must be an image of its own Vault library.'); END;

CREATE TRIGGER trg_vault_image_tags_same_library
BEFORE INSERT ON vault_image_tags
WHEN lower((SELECT vil.vault_library_id FROM vault_image_items vii
              JOIN vault_image_libraries vil ON vil.library_id = vii.library_id
             WHERE vii.item_id = NEW.item_id))
     IS NOT lower((SELECT library_id FROM vault_tags WHERE id = NEW.tag_id))
BEGIN SELECT RAISE(ABORT, 'An image can only use the tags of its own Vault library.'); END;
