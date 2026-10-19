-- The Engine works out the live ingestion numbers once a second from the per-file intake operations
-- (media_operations rows of type 'ingestion.file'): every file noticed since the active period began, plus every file
-- still being worked on. These two indexes let each look find those rows without reading every intake operation the
-- Library has ever had.
CREATE INDEX idx_media_operations_type_created ON media_operations (operation_type, created_at);

CREATE INDEX idx_media_operations_type_status ON media_operations (operation_type, status);
