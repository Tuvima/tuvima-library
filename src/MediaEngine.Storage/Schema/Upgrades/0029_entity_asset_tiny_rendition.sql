-- Right-sized pictures: a tiny (96px long edge) copy for faces and small thumbnails, beside the small, medium and large copies.
ALTER TABLE entity_assets ADD COLUMN local_image_path_t TEXT;
