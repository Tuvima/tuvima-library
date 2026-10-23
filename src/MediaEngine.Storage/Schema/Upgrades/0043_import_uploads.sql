-- Resumable uploads into the Import folder (Library setup spec D44, WP12a).
-- One row per upload an administrator started: who started it, which kind of media it is for, the file's name and size, how big each
-- piece is, the hidden working file it is being written into (under the Import folder's .data folder, so the final move is a rename on
-- the same drive) and where it will land. The row survives an Engine restart, so an upload resumes after one.
-- import_upload_pieces holds one row per piece received; recording a piece twice changes nothing, which keeps a resent piece harmless.
CREATE TABLE import_uploads (
    id                 BLOB NOT NULL PRIMARY KEY,
    created_by         BLOB NOT NULL,        -- the signed-in person's profile id, or the application id for an application caller
    kind_key           TEXT NOT NULL,        -- the library kind the file was dropped on (movies, music, ...)
    library_id         TEXT,                 -- the library named for a kind that has several; null means the kind's own
    type_label         TEXT NOT NULL,        -- the Import folder type the kind maps to (Movies, TV, Music, ...)
    file_name          TEXT NOT NULL,        -- the cleaned name asked for; the saved name is chosen when the upload completes
    total_bytes        INTEGER NOT NULL CHECK (total_bytes > 0),
    piece_bytes        INTEGER NOT NULL CHECK (piece_bytes > 0),
    piece_count        INTEGER NOT NULL CHECK (piece_count > 0),
    working_path       TEXT NOT NULL,        -- the hidden working file, inside <Import folder>/.data/uploads
    destination_folder TEXT NOT NULL,        -- the Import folder's sub-folder for the kind
    state              TEXT NOT NULL DEFAULT 'open' CHECK (state IN ('open', 'completed', 'cancelled')),
    final_path         TEXT,                 -- set when the upload completes
    created_at         TEXT NOT NULL,        -- ISO 8601 UTC
    last_piece_at      TEXT NOT NULL,        -- ISO 8601 UTC; starts as created_at, moves with every new piece
    closed_at          TEXT                  -- ISO 8601 UTC; set when the upload completes or is cancelled
);

CREATE TABLE import_upload_pieces (
    upload_id   BLOB NOT NULL REFERENCES import_uploads(id) ON DELETE CASCADE,
    piece_index INTEGER NOT NULL CHECK (piece_index >= 0),
    received_at TEXT NOT NULL,               -- ISO 8601 UTC
    PRIMARY KEY (upload_id, piece_index)
) WITHOUT ROWID;

CREATE INDEX idx_import_uploads_state_last_piece ON import_uploads (state, last_piece_at);
