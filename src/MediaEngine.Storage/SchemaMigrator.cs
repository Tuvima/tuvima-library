using Dapper;
using MediaEngine.Domain;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

internal sealed class SchemaMigrator
{
    public void RunStartupTasks(SqliteConnection conn)
    {
        EnsureIdentitySchema(conn);
        EnsureOnboardingSchema(conn);
        EnsureProviderConnectionCheckSchema(conn);
        EnsureAdaptiveDeliverySchema(conn);
        EnsureExpandedArtworkAssetTypes(conn);
        EnsureEditionArtworkOwnerSchema(conn);
        EnsureCanonicalArtworkSchema(conn);
        EnsureArtworkWritebackSchema(conn);
        EnsureMediaEditorCommitSchema(conn);
        EnsureAssetRenditionSchema(conn);
        EnsureCurrentColumns(conn);
        EnsureCurrentIndexes(conn);
        using (var recordingIndex = conn.CreateCommand())
        {
            recordingIndex.CommandText = "SELECT sql FROM sqlite_master WHERE name='ux_works_child_parent_ordinal_sort'";
            var definition = recordingIndex.ExecuteScalar() as string;
            if (definition?.Contains("parent_key IS NULL", StringComparison.Ordinal) != true)
            {
                recordingIndex.CommandText = """
                    DROP INDEX IF EXISTS ux_works_child_parent_ordinal_sort;
                    CREATE UNIQUE INDEX ux_works_child_parent_ordinal_sort ON works(parent_work_id,ordinal_sort)
                    WHERE work_kind IN ('child','catalog') AND parent_work_id IS NOT NULL AND ordinal_sort IS NOT NULL
                    AND (media_type != 'Audiobooks' OR parent_key IS NULL);
                    """;
                recordingIndex.ExecuteNonQuery();
            }
        }
        SeedMetadataProviders(conn);
        SeedDefaultProfile(conn);
        MigrateLegacyProfileLists(conn);
    }

    private static void EnsureAssetRenditionSchema(SqliteConnection conn)
    {
        AddColumnIfMissing(conn, "media_assets", "rendition_purpose",
            "ALTER TABLE media_assets ADD COLUMN rendition_purpose TEXT NOT NULL DEFAULT 'Original';");
        AddColumnIfMissing(conn, "media_assets", "derived_from_asset_id",
            "ALTER TABLE media_assets ADD COLUMN derived_from_asset_id BLOB REFERENCES media_assets(id) ON DELETE SET NULL;");
        AddColumnIfMissing(conn, "media_assets", "encoder_profile_version",
            "ALTER TABLE media_assets ADD COLUMN encoder_profile_version TEXT;");
        AddColumnIfMissing(conn, "media_assets", "rendition_width",
            "ALTER TABLE media_assets ADD COLUMN rendition_width INTEGER;");
        AddColumnIfMissing(conn, "media_assets", "rendition_height",
            "ALTER TABLE media_assets ADD COLUMN rendition_height INTEGER;");
        AddColumnIfMissing(conn, "media_assets", "rendition_bitrate_bps",
            "ALTER TABLE media_assets ADD COLUMN rendition_bitrate_bps INTEGER;");
        AddColumnIfMissing(conn, "media_assets", "rendition_video_codec",
            "ALTER TABLE media_assets ADD COLUMN rendition_video_codec TEXT;");
        AddColumnIfMissing(conn, "media_assets", "rendition_audio_codec",
            "ALTER TABLE media_assets ADD COLUMN rendition_audio_codec TEXT;");
        AddColumnIfMissing(conn, "media_assets", "rendition_dynamic_range",
            "ALTER TABLE media_assets ADD COLUMN rendition_dynamic_range TEXT;");
        AddColumnIfMissing(conn, "media_assets", "rendition_audio_layout",
            "ALTER TABLE media_assets ADD COLUMN rendition_audio_layout TEXT;");
        AddColumnIfMissing(conn, "media_assets", "rendition_generated_at",
            "ALTER TABLE media_assets ADD COLUMN rendition_generated_at TEXT;");
        AddColumnIfMissing(conn, "media_assets", "rendition_source_fingerprint",
            "ALTER TABLE media_assets ADD COLUMN rendition_source_fingerprint TEXT;");
        using var command = conn.CreateCommand();
        command.CommandText = "CREATE INDEX IF NOT EXISTS idx_media_assets_derived_from ON media_assets(derived_from_asset_id) WHERE derived_from_asset_id IS NOT NULL;";
        command.ExecuteNonQuery();
    }

    private static void EnsureProviderConnectionCheckSchema(SqliteConnection conn)
    {
        using var command = conn.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS provider_connection_checks (
                provider_name TEXT NOT NULL PRIMARY KEY,
                status TEXT NOT NULL,
                message TEXT NOT NULL,
                checked_at TEXT NOT NULL,
                response_time_ms INTEGER
            );
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureArtworkWritebackSchema(SqliteConnection conn)
    {
        using var command = conn.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS media_artwork_writeback (
                media_asset_id BLOB NOT NULL PRIMARY KEY REFERENCES media_assets(id) ON DELETE CASCADE,
                desired_artwork_asset_id BLOB NOT NULL,
                desired_version TEXT,
                embedded_artwork_asset_id BLOB,
                status TEXT NOT NULL CHECK(status IN ('pending','writing','embedded','failed')),
                attempts INTEGER NOT NULL DEFAULT 0,
                last_error TEXT,
                file_modified_utc TEXT,
                file_size_bytes INTEGER,
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            );
            CREATE INDEX IF NOT EXISTS idx_media_artwork_writeback_status
                ON media_artwork_writeback(status, updated_at);
            """;
        command.ExecuteNonQuery();
        AddColumnIfMissing(conn, "media_artwork_writeback", "desired_version",
            "ALTER TABLE media_artwork_writeback ADD COLUMN desired_version TEXT;");
    }

    private static void EnsureMediaEditorCommitSchema(SqliteConnection conn)
    {
        using var command = conn.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS media_editor_commits (
                operation_token TEXT NOT NULL PRIMARY KEY,
                request_hash TEXT NOT NULL,
                asset_id BLOB NOT NULL,
                source_work_id BLOB NOT NULL,
                target_work_id BLOB NOT NULL,
                target_tvdb_episode_id TEXT NOT NULL,
                committed_at TEXT NOT NULL,
                sync_state TEXT NOT NULL DEFAULT 'pending'
            );
            CREATE INDEX IF NOT EXISTS ix_media_editor_commits_asset
                ON media_editor_commits(asset_id, committed_at);
            CREATE TABLE IF NOT EXISTS media_editor_commit_items (
                operation_token TEXT NOT NULL REFERENCES media_editor_commits(operation_token) ON DELETE CASCADE,
                asset_id BLOB NOT NULL,
                source_edition_id BLOB NOT NULL,
                source_work_id BLOB NOT NULL,
                target_work_id BLOB NOT NULL,
                source_season_work_id BLOB NOT NULL,
                target_season_work_id BLOB NOT NULL,
                PRIMARY KEY (operation_token, asset_id)
            );
            CREATE TABLE IF NOT EXISTS media_editor_music_pairing_commits (
                operation_token TEXT NOT NULL PRIMARY KEY,
                request_hash TEXT NOT NULL,
                committed_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS media_editor_music_pairing_commit_items (
                operation_token TEXT NOT NULL REFERENCES media_editor_music_pairing_commits(operation_token) ON DELETE CASCADE,
                asset_id BLOB NOT NULL,
                edition_id BLOB NOT NULL,
                work_id BLOB NOT NULL,
                release_id TEXT NOT NULL,
                release_track_id TEXT NOT NULL,
                PRIMARY KEY (operation_token, asset_id)
            );
            CREATE TABLE IF NOT EXISTS media_editor_commit_artwork (
                operation_token TEXT NOT NULL PRIMARY KEY REFERENCES media_editor_commits(operation_token) ON DELETE CASCADE,
                owner_work_id BLOB NOT NULL,
                artwork_asset_id BLOB NOT NULL,
                expected_preference_revision TEXT NOT NULL,
                previous_preferred_ids_json TEXT NOT NULL,
                affected_asset_ids_json TEXT NOT NULL,
                committed_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS media_editor_preferred_artwork_commits (
                operation_token TEXT NOT NULL PRIMARY KEY,
                request_hash TEXT NOT NULL,
                owner_work_id BLOB NOT NULL,
                owner_scope TEXT NOT NULL,
                role TEXT NOT NULL,
                artwork_asset_id BLOB NOT NULL,
                expected_owner_revision TEXT NOT NULL,
                previous_preferred_ids_json TEXT NOT NULL,
                affected_assets_json TEXT NOT NULL,
                committed_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS media_editor_edition_artwork_commits (
                operation_token TEXT NOT NULL PRIMARY KEY,
                request_hash TEXT NOT NULL,
                edition_id BLOB NOT NULL,
                work_id BLOB NOT NULL,
                artwork_asset_id BLOB NOT NULL,
                expected_revision TEXT NOT NULL,
                previous_preferred_ids_json TEXT NOT NULL,
                affected_assets_json TEXT NOT NULL,
                committed_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS media_file_write_intents (
                asset_id BLOB NOT NULL PRIMARY KEY REFERENCES media_assets(id) ON DELETE CASCADE,
                generation INTEGER NOT NULL DEFAULT 1,
                operation_token TEXT NOT NULL,
                trigger TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'pending'
                    CHECK(status IN ('pending','writing','verified','blocked','unsupported','failed')),
                attempts INTEGER NOT NULL DEFAULT 0,
                lease_expires_at TEXT,
                last_error TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_media_file_write_intents_dispatch
                ON media_file_write_intents(status, lease_expires_at, updated_at);
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureCanonicalArtworkSchema(SqliteConnection conn)
    {
        DatabaseConnection.ExecuteStartupTransaction(conn, transaction =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS artwork_assets (
                    id BLOB NOT NULL PRIMARY KEY,
                    content_hash TEXT NOT NULL UNIQUE,
                    original_path TEXT,
                    small_path TEXT,
                    medium_path TEXT,
                    large_path TEXT,
                    width_px INTEGER,
                    height_px INTEGER,
                    aspect_class TEXT NOT NULL DEFAULT 'UnsupportedRect',
                    primary_hex TEXT,
                    secondary_hex TEXT,
                    accent_hex TEXT,
                    source_provider TEXT,
                    source_url TEXT,
                    provider_reference TEXT,
                    perceptual_hash INTEGER,
                    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                    updated_at TEXT
                );

                CREATE TABLE IF NOT EXISTS entity_artwork_links (
                    id BLOB NOT NULL PRIMARY KEY,
                    entity_id BLOB NOT NULL,
                    entity_type TEXT NOT NULL,
                    artwork_asset_id BLOB NOT NULL REFERENCES artwork_assets(id) ON DELETE RESTRICT,
                    role TEXT NOT NULL CHECK(role IN ('Primary','Background','Portrait','Logo')),
                    context TEXT,
                    source_asset_type TEXT,
                    is_preferred INTEGER NOT NULL DEFAULT 0,
                    is_user_override INTEGER NOT NULL DEFAULT 0,
                    sort_order INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                    updated_at TEXT,
                    UNIQUE(entity_id, entity_type, artwork_asset_id, role, context)
                );

                CREATE TABLE IF NOT EXISTS artwork_asset_context (
                    artwork_asset_id BLOB NOT NULL REFERENCES artwork_assets(id) ON DELETE CASCADE,
                    entity_id BLOB NOT NULL,
                    entity_type TEXT NOT NULL,
                    entity_label TEXT NOT NULL,
                    media_type TEXT,
                    year TEXT,
                    role TEXT,
                    provider TEXT,
                    canonical_id TEXT,
                    search_text TEXT NOT NULL,
                    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                    updated_at TEXT,
                    PRIMARY KEY(artwork_asset_id, entity_id, entity_type, role)
                );

                CREATE INDEX IF NOT EXISTS idx_entity_artwork_links_entity_role
                    ON entity_artwork_links(entity_id, entity_type, role, is_preferred DESC, sort_order);
                CREATE INDEX IF NOT EXISTS idx_entity_artwork_links_asset
                    ON entity_artwork_links(artwork_asset_id);
                CREATE INDEX IF NOT EXISTS idx_artwork_asset_context_search
                    ON artwork_asset_context(search_text COLLATE NOCASE);
                CREATE INDEX IF NOT EXISTS idx_artwork_asset_context_facets
                    ON artwork_asset_context(entity_type, media_type, year, role, provider, entity_id);

                CREATE VIRTUAL TABLE IF NOT EXISTS artwork_asset_search USING fts5(
                    artwork_asset_id UNINDEXED,
                    search_text,
                    tokenize = 'trigram'
                );

                CREATE TRIGGER IF NOT EXISTS trg_artwork_asset_context_search_insert
                AFTER INSERT ON artwork_asset_context BEGIN
                    INSERT INTO artwork_asset_search(artwork_asset_id, search_text)
                    VALUES (new.artwork_asset_id, new.search_text);
                END;

                CREATE TRIGGER IF NOT EXISTS trg_artwork_asset_context_search_update
                AFTER UPDATE OF search_text, artwork_asset_id ON artwork_asset_context BEGIN
                    DELETE FROM artwork_asset_search
                    WHERE artwork_asset_id = old.artwork_asset_id AND search_text = old.search_text;
                    INSERT INTO artwork_asset_search(artwork_asset_id, search_text)
                    VALUES (new.artwork_asset_id, new.search_text);
                END;

                CREATE TRIGGER IF NOT EXISTS trg_artwork_asset_context_search_delete
                AFTER DELETE ON artwork_asset_context BEGIN
                    DELETE FROM artwork_asset_search
                    WHERE artwork_asset_id = old.artwork_asset_id AND search_text = old.search_text;
                END;

                INSERT OR IGNORE INTO artwork_assets (
                    id, content_hash, original_path, small_path, medium_path, large_path,
                    width_px, height_px, aspect_class, primary_hex, secondary_hex, accent_hex,
                    source_provider, source_url, perceptual_hash, created_at, updated_at)
                SELECT ea.id,
                       COALESCE(ic.content_hash, 'legacy:' || lower(hex(ea.id))),
                       ea.local_image_path, ea.local_image_path_s, ea.local_image_path_m, ea.local_image_path_l,
                       ea.width_px, ea.height_px, ea.aspect_class,
                       ea.primary_hex, ea.secondary_hex, ea.accent_hex,
                       ea.source_provider, ea.image_url, ic.phash, ea.created_at, ea.updated_at
                FROM entity_assets ea
                LEFT JOIN image_cache ic ON ic.file_path = ea.local_image_path
                WHERE COALESCE(ea.asset_class, 'Artwork') = 'Artwork';

                INSERT OR IGNORE INTO entity_artwork_links (
                    id, entity_id, entity_type, artwork_asset_id, role, context, source_asset_type,
                    is_preferred, is_user_override, created_at, updated_at)
                SELECT ea.id, ea.entity_id, ea.entity_type, aa.id,
                       CASE
                           WHEN ea.asset_type IN ('Headshot','CharacterPortrait') THEN 'Portrait'
                           WHEN ea.asset_type IN ('Background','Banner','SeasonThumb') THEN 'Background'
                           WHEN ea.asset_type IN ('Logo','NetworkLogo','StudioLogo') THEN 'Logo'
                           ELSE 'Primary'
                       END,
                       CASE
                           WHEN ea.asset_type = 'SeasonPoster' THEN 'Season'
                           WHEN ea.asset_type = 'EpisodeStill' THEN 'Episode'
                           ELSE NULL
                       END,
                       ea.asset_type, ea.is_preferred, ea.is_user_override, ea.created_at, ea.updated_at
                FROM entity_assets ea
                LEFT JOIN image_cache ic ON ic.file_path = ea.local_image_path
                JOIN artwork_assets aa ON aa.content_hash = COALESCE(ic.content_hash, 'legacy:' || lower(hex(ea.id)))
                WHERE COALESCE(ea.asset_class, 'Artwork') = 'Artwork';

                INSERT OR IGNORE INTO artwork_asset_context (
                    artwork_asset_id, entity_id, entity_type, entity_label, media_type,
                    year, role, provider, search_text, created_at)
                SELECT link.artwork_asset_id, work.id, 'Work',
                       COALESCE((SELECT value FROM canonical_values WHERE entity_id=work.id AND key='title'), 'Untitled media'),
                       work.media_type,
                       (SELECT value FROM canonical_values WHERE entity_id=work.id AND key IN ('release_year','year') ORDER BY CASE key WHEN 'release_year' THEN 0 ELSE 1 END LIMIT 1),
                       link.role, asset.source_provider,
                       trim(COALESCE((SELECT value FROM canonical_values WHERE entity_id=work.id AND key='title'), 'Untitled media') || ' ' ||
                            work.media_type || ' ' || link.role || ' ' || COALESCE(asset.source_provider, '')),
                       link.created_at
                FROM entity_artwork_links link
                JOIN works work ON work.id = link.entity_id
                JOIN artwork_assets asset ON asset.id = link.artwork_asset_id
                WHERE link.entity_type = 'Work';

                INSERT OR IGNORE INTO artwork_asset_context (
                    artwork_asset_id, entity_id, entity_type, entity_label, role, provider, search_text, created_at)
                SELECT link.artwork_asset_id, person.id, 'Person', person.name,
                       link.role, asset.source_provider,
                       trim(person.name || ' Person ' || link.role || ' ' || COALESCE(person.occupation, '') || ' ' || COALESCE(asset.source_provider, '')),
                       link.created_at
                FROM entity_artwork_links link
                JOIN persons person ON person.id = link.entity_id
                JOIN artwork_assets asset ON asset.id = link.artwork_asset_id
                WHERE link.entity_type = 'Person';

                INSERT OR IGNORE INTO artwork_asset_context (
                    artwork_asset_id, entity_id, entity_type, entity_label, media_type,
                    role, provider, canonical_id, search_text, created_at)
                SELECT link.artwork_asset_id, collection.id, 'Collection', collection.display_name,
                       collection.primary_area, link.role, asset.source_provider, collection.wikidata_qid,
                       trim(collection.display_name || ' ' || collection.collection_type || ' ' ||
                            COALESCE(collection.primary_area, '') || ' ' || link.role || ' ' ||
                            COALESCE(collection.wikidata_qid, '') || ' ' || COALESCE(asset.source_provider, '')),
                       link.created_at
                FROM entity_artwork_links link
                JOIN collections collection ON collection.id = link.entity_id
                JOIN artwork_assets asset ON asset.id = link.artwork_asset_id
                WHERE link.entity_type = 'Collection';

                INSERT OR IGNORE INTO artwork_asset_context (
                    artwork_asset_id, entity_id, entity_type, entity_label,
                    role, provider, canonical_id, search_text, created_at)
                SELECT link.artwork_asset_id, entity.id, 'FictionalEntity', entity.label,
                       link.role, asset.source_provider, entity.wikidata_qid,
                       trim(entity.label || ' ' || entity.entity_sub_type || ' ' ||
                            COALESCE(entity.fictional_universe_label, '') || ' ' || link.role || ' ' ||
                            entity.wikidata_qid || ' ' || COALESCE(asset.source_provider, '')),
                       link.created_at
                FROM entity_artwork_links link
                JOIN fictional_entities entity ON entity.id = link.entity_id
                JOIN artwork_assets asset ON asset.id = link.artwork_asset_id
                WHERE link.entity_type = 'FictionalEntity';

                INSERT INTO artwork_asset_search(artwork_asset_id, search_text)
                SELECT context.artwork_asset_id, context.search_text
                FROM artwork_asset_context context
                WHERE NOT EXISTS (
                    SELECT 1 FROM artwork_asset_search indexed_search
                    WHERE indexed_search.artwork_asset_id = context.artwork_asset_id
                      AND indexed_search.search_text = context.search_text);

                INSERT OR IGNORE INTO schema_migrations (migration_id, applied_at)
                VALUES ('007_canonical_artwork_assets', strftime('%Y-%m-%dT%H:%M:%fZ','now'));
                """;
            cmd.ExecuteNonQuery();
        });
    }

    private static void MigrateLegacyProfileLists(SqliteConnection conn)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE migration_id = '006_profile_state_for_me_cutover';";
            if (Convert.ToInt32(check.ExecuteScalar()) > 0)
            {
                return;
            }
        }

        DatabaseConnection.ExecuteStartupTransaction(conn, transaction =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                -- The old Dashboard created these exact private Playlists as an
                -- implementation detail. Restrict by both name and product-owned
                -- description so a user Playlist with the same name is untouched.
                INSERT OR IGNORE INTO profile_saved_items(profile_id, entity_kind, entity_id, saved_at, position)
                SELECT c.profile_id,
                       CASE w.media_type
                           WHEN 'Movies' THEN 'Movie'
                           WHEN 'TV' THEN 'TvShow'
                           WHEN 'Books' THEN 'Book'
                           WHEN 'Comics' THEN 'Comic'
                           WHEN 'Audiobooks' THEN 'Audiobook'
                           ELSE 'Book'
                       END,
                       ci.work_id,
                       ci.added_at,
                       ci.sort_order
                FROM collections c
                JOIN collection_items ci ON ci.collection_id = c.id
                JOIN works w ON w.id = ci.work_id
                WHERE c.scope = 'user'
                  AND c.profile_id IS NOT NULL
                  AND c.collection_type = 'Playlist'
                  AND c.resolution = 'materialized'
                  AND (
                    (c.display_name = 'Favorites' AND c.description = 'Profile-level favorites across the library.' AND w.media_type <> 'Music')
                    OR (c.display_name = 'Watchlist' AND c.description = 'Quick-save shows and movies to watch later.')
                  );

                -- Music rows in the legacy Favorites Playlist represented the heart
                -- action, not saved-for-later intent. Preserve them as Likes.
                INSERT OR IGNORE INTO profile_reactions(profile_id, entity_kind, entity_id, reaction, updated_at)
                SELECT c.profile_id, 'Song', ci.work_id, 'Like', ci.added_at
                FROM collections c
                JOIN collection_items ci ON ci.collection_id = c.id
                JOIN works w ON w.id = ci.work_id
                WHERE c.scope = 'user'
                  AND c.profile_id IS NOT NULL
                  AND c.collection_type = 'Playlist'
                  AND c.resolution = 'materialized'
                  AND c.display_name = 'Favorites'
                  AND c.description = 'Profile-level favorites across the library.'
                  AND w.media_type = 'Music';

                INSERT OR REPLACE INTO profile_reactions(profile_id, entity_kind, entity_id, reaction, updated_at)
                SELECT c.profile_id,
                       CASE w.media_type
                           WHEN 'Movies' THEN 'Movie'
                           WHEN 'TV' THEN 'TvShow'
                           WHEN 'Books' THEN 'Book'
                           WHEN 'Comics' THEN 'Comic'
                           WHEN 'Audiobooks' THEN 'Audiobook'
                           WHEN 'Music' THEN 'Song'
                           ELSE 'Book'
                       END,
                       ci.work_id,
                       CASE c.display_name
                           WHEN 'Disliked Media' THEN 'Dislike'
                           WHEN 'Loved Media' THEN 'Love'
                           ELSE 'Like'
                       END,
                       ci.added_at
                FROM collections c
                JOIN collection_items ci ON ci.collection_id = c.id
                JOIN works w ON w.id = ci.work_id
                WHERE c.scope = 'user'
                  AND c.profile_id IS NOT NULL
                  AND c.collection_type = 'Playlist'
                  AND c.resolution = 'materialized'
                  AND (
                    (c.display_name = 'Liked Media' AND c.description = 'Profile-level positive feedback across the library.')
                    OR (c.display_name = 'Disliked Media' AND c.description = 'Profile-level dislikes across the library.')
                    OR (c.display_name = 'Loved Media' AND c.description = 'Profile-level strongest positive feedback across the library.')
                  );

                DELETE FROM collections
                WHERE scope = 'user'
                  AND profile_id IS NOT NULL
                  AND collection_type = 'Playlist'
                  AND resolution = 'materialized'
                  AND (
                    (display_name = 'Favorites' AND description = 'Profile-level favorites across the library.')
                    OR (display_name = 'Watchlist' AND description = 'Quick-save shows and movies to watch later.')
                    OR (display_name = 'Liked Media' AND description = 'Profile-level positive feedback across the library.')
                    OR (display_name = 'Disliked Media' AND description = 'Profile-level dislikes across the library.')
                    OR (display_name = 'Loved Media' AND description = 'Profile-level strongest positive feedback across the library.')
                  );

                INSERT OR IGNORE INTO schema_migrations (migration_id, applied_at)
                VALUES ('006_profile_state_for_me_cutover', strftime('%Y-%m-%dT%H:%M:%fZ','now'));
                """;
            cmd.ExecuteNonQuery();
        });
    }

    private static void EnsureExpandedArtworkAssetTypes(SqliteConnection conn)
    {
        using var inspect = conn.CreateCommand();
        inspect.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'entity_assets';";
        var tableSql = inspect.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(tableSql)
            || tableSql.Contains("NetworkLogo", StringComparison.Ordinal))
        {
            return;
        }

        DatabaseConnection.ExecuteStartupTransaction(conn, transaction =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                ALTER TABLE entity_assets RENAME TO entity_assets_legacy_asset_types;

                CREATE TABLE entity_assets (
                    id               BLOB PRIMARY KEY,
                    entity_id        BLOB NOT NULL,
                    entity_type      TEXT NOT NULL CHECK(entity_type IN ('Work','Person','Universe','FictionalEntity')),
                    asset_type       TEXT NOT NULL CHECK(asset_type IN ('CoverArt','Headshot','Banner','Logo','NetworkLogo','StudioLogo','Background','SeasonPoster','SeasonThumb','EpisodeStill','CharacterPortrait')),
                    image_url        TEXT,
                    local_image_path TEXT,
                    local_image_path_s TEXT,
                    local_image_path_m TEXT,
                    local_image_path_l TEXT,
                    source_provider  TEXT,
                    width_px         INTEGER,
                    height_px        INTEGER,
                    aspect_class     TEXT NOT NULL DEFAULT 'UnsupportedRect',
                    primary_hex      TEXT,
                    secondary_hex    TEXT,
                    accent_hex       TEXT,
                    asset_class      TEXT NOT NULL DEFAULT 'Artwork',
                    storage_location TEXT NOT NULL DEFAULT 'Central',
                    owner_scope      TEXT NOT NULL DEFAULT 'Unknown',
                    is_preferred     INTEGER NOT NULL DEFAULT 0,
                    is_user_override INTEGER NOT NULL DEFAULT 0,
                    is_locally_exported   INTEGER NOT NULL DEFAULT 0,
                    is_preferred_exported INTEGER NOT NULL DEFAULT 0,
                    created_at       TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at       TEXT
                );

                INSERT INTO entity_assets (
                    id, entity_id, entity_type, asset_type, image_url,
                    local_image_path, local_image_path_s, local_image_path_m, local_image_path_l,
                    source_provider, width_px, height_px, aspect_class,
                    primary_hex, secondary_hex, accent_hex, asset_class, storage_location, owner_scope,
                    is_preferred, is_user_override, is_locally_exported, is_preferred_exported,
                    created_at, updated_at)
                SELECT
                    id, entity_id, entity_type, asset_type, image_url,
                    local_image_path, local_image_path_s, local_image_path_m, local_image_path_l,
                    source_provider, width_px, height_px, aspect_class,
                    primary_hex, secondary_hex, accent_hex, asset_class, storage_location, owner_scope,
                    is_preferred, is_user_override, is_locally_exported, is_preferred_exported,
                    created_at, updated_at
                FROM entity_assets_legacy_asset_types;

                DROP TABLE entity_assets_legacy_asset_types;

                CREATE INDEX idx_entity_assets_entity
                    ON entity_assets(entity_id, entity_type);
                CREATE INDEX idx_entity_assets_type
                    ON entity_assets(entity_id, asset_type);
                CREATE UNIQUE INDEX ux_entity_assets_entity_type_source_url
                    ON entity_assets(entity_id, asset_type, image_url COLLATE NOCASE)
                    WHERE image_url IS NOT NULL AND length(trim(image_url)) > 0;

                INSERT OR IGNORE INTO schema_migrations (migration_id, applied_at)
                VALUES ('005_expanded_artwork_asset_types', strftime('%Y-%m-%dT%H:%M:%fZ','now'));
                """;
            cmd.ExecuteNonQuery();
        });
    }

    private static void EnsureAdaptiveDeliverySchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS adaptive_hls_packages (
                id BLOB NOT NULL PRIMARY KEY,
                asset_id BLOB NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
                source_hash TEXT NOT NULL,
                profile_key TEXT NOT NULL,
                status TEXT NOT NULL CHECK(status IN ('preparing', 'ready', 'failed', 'deleting')),
                root_path TEXT NOT NULL,
                total_bytes INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                last_accessed TEXT NOT NULL,
                completed_at TEXT,
                last_error TEXT,
                UNIQUE(asset_id, source_hash, profile_key)
            );
            CREATE INDEX IF NOT EXISTS idx_adaptive_hls_packages_eviction
                ON adaptive_hls_packages(status, last_accessed);
            """;
        cmd.ExecuteNonQuery();
    }

    private static void EnsureOnboardingSchema(SqliteConnection conn)
    {
        DatabaseConnection.ExecuteStartupTransaction(conn, transaction =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS onboarding_workflows (
                workflow_version INTEGER NOT NULL PRIMARY KEY,
                state TEXT NOT NULL CHECK (state IN ('in_progress', 'complete')),
                current_step TEXT NOT NULL,
                administrator_profile_id BLOB REFERENCES profiles(id) ON DELETE SET NULL,
                revision INTEGER NOT NULL DEFAULT 0,
                completed_at TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS onboarding_steps (
                workflow_version INTEGER NOT NULL REFERENCES onboarding_workflows(workflow_version) ON DELETE CASCADE,
                step_key TEXT NOT NULL,
                status TEXT NOT NULL CHECK (status IN ('not_started', 'in_progress', 'passed', 'deferred', 'blocked')),
                detail TEXT,
                repair_target TEXT,
                completed_at TEXT,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (workflow_version, step_key)
            );

            CREATE TABLE IF NOT EXISTS onboarding_sessions (
                id BLOB NOT NULL PRIMARY KEY,
                workflow_version INTEGER NOT NULL REFERENCES onboarding_workflows(workflow_version) ON DELETE CASCADE,
                token_hash TEXT NOT NULL UNIQUE,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                last_used_at TEXT NOT NULL,
                revoked_at TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_onboarding_sessions_active
                ON onboarding_sessions(workflow_version, expires_at, revoked_at);

            CREATE TABLE IF NOT EXISTS onboarding_restore_operations (
                id BLOB NOT NULL PRIMARY KEY,
                workflow_version INTEGER NOT NULL REFERENCES onboarding_workflows(workflow_version) ON DELETE CASCADE,
                archive_path TEXT NOT NULL,
                original_file_name TEXT NOT NULL,
                status TEXT NOT NULL CHECK (status IN ('inspected', 'scheduled', 'applied', 'failed', 'cancelled')),
                manifest_version TEXT NOT NULL,
                database_epoch TEXT NOT NULL,
                summary_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            INSERT OR IGNORE INTO onboarding_workflows
                (workflow_version, state, current_step, revision, created_at, updated_at)
            VALUES
                (1, 'in_progress', 'preflight', 0, strftime('%Y-%m-%dT%H:%M:%fZ','now'), strftime('%Y-%m-%dT%H:%M:%fZ','now'));

            INSERT OR IGNORE INTO onboarding_steps (workflow_version, step_key, status, updated_at)
            VALUES
                (1, 'preflight', 'not_started', strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                (1, 'administrator', 'not_started', strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                (1, 'media-locations', 'not_started', strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                (1, 'providers', 'not_started', strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                (1, 'readiness', 'not_started', strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
            cmd.ExecuteNonQuery();
        });
    }

    private static void EnsureIdentitySchema(SqliteConnection conn)
    {
        using var legacyIdentity = conn.CreateCommand();
        legacyIdentity.CommandText = """
            SELECT CASE WHEN
                EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'profile_external_logins')
                OR EXISTS (SELECT 1 FROM pragma_table_info('profile_credentials') WHERE name = 'normalized_username')
                OR EXISTS (SELECT 1 FROM pragma_table_info('auth_sessions') WHERE name = 'profile_id')
                OR EXISTS (SELECT 1 FROM pragma_table_info('password_recovery_codes') WHERE name = 'profile_id')
            THEN 1 ELSE 0 END;
            """;
        if (Convert.ToInt32(legacyIdentity.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0)
        {
            throw new InvalidOperationException(
                "Unsupported pre-beta profile-owned authentication schema was found. Reset the disposable development database and configure the new account/profile identity model.");
        }

        using var legacyPin = conn.CreateCommand();
        legacyPin.CommandText = "SELECT COUNT(1) FROM pragma_table_info('profiles') WHERE name = 'pin_hash';";
        if (Convert.ToInt32(legacyPin.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0)
        {
            using var populated = conn.CreateCommand();
            populated.CommandText = "SELECT COUNT(1) FROM profiles WHERE pin_hash IS NOT NULL AND trim(pin_hash) <> '';";
            if (Convert.ToInt32(populated.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0)
            {
                throw new InvalidOperationException(
                    "Unsupported pre-beta SHA-256 profile PIN state was found. Reset the disposable development database and configure a new password or profile PIN.");
            }
        }

        DatabaseConnection.ExecuteStartupTransaction(conn, transaction =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                migration_id TEXT NOT NULL PRIMARY KEY,
                applied_at TEXT NOT NULL
            );

            INSERT OR IGNORE INTO schema_migrations (migration_id, applied_at)
            VALUES ('004_account_profile_identity', strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
            cmd.ExecuteNonQuery();
        });
    }

    private static void EnsureCurrentColumns(SqliteConnection conn)
    {
        var addedMembershipMode = AddColumnIfMissing(conn, "collections", "membership_mode",
            "ALTER TABLE collections ADD COLUMN membership_mode TEXT NOT NULL DEFAULT 'Smart';");
        AddColumnIfMissing(conn, "collections", "primary_area",
            "ALTER TABLE collections ADD COLUMN primary_area TEXT NOT NULL DEFAULT 'Mixed';");
        var addedOwnerKind = AddColumnIfMissing(conn, "collections", "owner_kind",
            "ALTER TABLE collections ADD COLUMN owner_kind TEXT NOT NULL DEFAULT 'Library';");
        var addedAudience = AddColumnIfMissing(conn, "collections", "audience",
            "ALTER TABLE collections ADD COLUMN audience TEXT NOT NULL DEFAULT 'Everyone';");
        if (addedMembershipMode || addedOwnerKind || addedAudience)
        {
            using var backfill = conn.CreateCommand();
            backfill.CommandText = """
                UPDATE collections
                SET membership_mode = CASE
                        WHEN collection_type IN ('Custom', 'Playlist')
                             AND (resolution = 'materialized' OR rule_json IS NULL OR trim(rule_json) = '')
                            THEN 'Manual'
                        ELSE membership_mode
                    END,
                    primary_area = CASE
                        WHEN collection_type = 'Playlist' THEN 'Listen'
                        ELSE primary_area
                    END,
                    owner_kind = CASE WHEN scope = 'user' THEN 'Profile' ELSE 'Library' END,
                    audience = CASE WHEN scope = 'user' THEN 'Private' ELSE 'Everyone' END;
                """;
            backfill.ExecuteNonQuery();
        }
        using (var audienceTable = conn.CreateCommand())
        {
            audienceTable.CommandText = """
                CREATE TABLE IF NOT EXISTS collection_profile_audience (
                    collection_id BLOB NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
                    profile_id BLOB NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    PRIMARY KEY(collection_id, profile_id)
                );
                """;
            audienceTable.ExecuteNonQuery();
        }

        AddColumnIfMissing(conn, "collections", "background_artwork_path",
            "ALTER TABLE collections ADD COLUMN background_artwork_path TEXT;");
        AddColumnIfMissing(conn, "collections", "background_artwork_mime_type",
            "ALTER TABLE collections ADD COLUMN background_artwork_mime_type TEXT;");
        AddColumnIfMissing(conn, "collections", "logo_artwork_path",
            "ALTER TABLE collections ADD COLUMN logo_artwork_path TEXT;");
        AddColumnIfMissing(conn, "collections", "logo_artwork_mime_type",
            "ALTER TABLE collections ADD COLUMN logo_artwork_mime_type TEXT;");
        AddColumnIfMissing(conn, "collections", "banner_artwork_path",
            "ALTER TABLE collections ADD COLUMN banner_artwork_path TEXT;");
        AddColumnIfMissing(conn, "collections", "banner_artwork_mime_type",
            "ALTER TABLE collections ADD COLUMN banner_artwork_mime_type TEXT;");
        AddColumnIfMissing(conn, "collections", "secondary_sort_field",
            "ALTER TABLE collections ADD COLUMN secondary_sort_field TEXT;");
        AddColumnIfMissing(conn, "collections", "secondary_sort_direction",
            "ALTER TABLE collections ADD COLUMN secondary_sort_direction TEXT;");
        AddColumnIfMissing(conn, "collections", "display_overrides_json",
            "ALTER TABLE collections ADD COLUMN display_overrides_json TEXT;");
        AddColumnIfMissing(conn, "view_galleries", "soundtrack_playlist_id",
            "ALTER TABLE view_galleries ADD COLUMN soundtrack_playlist_id BLOB REFERENCES collections(id) ON DELETE SET NULL;");
        AddColumnIfMissing(conn, "local_item_metadata", "location_city", "ALTER TABLE local_item_metadata ADD COLUMN location_city TEXT;");
        AddColumnIfMissing(conn, "local_item_metadata", "location_region", "ALTER TABLE local_item_metadata ADD COLUMN location_region TEXT;");
        AddColumnIfMissing(conn, "local_item_metadata", "location_country", "ALTER TABLE local_item_metadata ADD COLUMN location_country TEXT;");
        AddColumnIfMissing(conn, "local_item_metadata", "location_country_code", "ALTER TABLE local_item_metadata ADD COLUMN location_country_code TEXT;");
        AddColumnIfMissing(conn, "local_item_metadata", "location_source", "ALTER TABLE local_item_metadata ADD COLUMN location_source TEXT;");
        var addedLocationOverride = AddColumnIfMissing(conn, "local_item_metadata", "location_user_override", "ALTER TABLE local_item_metadata ADD COLUMN location_user_override INTEGER NOT NULL DEFAULT 0;");
        var addedEmbeddedLatitude = AddColumnIfMissing(conn, "local_item_metadata", "embedded_latitude", "ALTER TABLE local_item_metadata ADD COLUMN embedded_latitude REAL;");
        var addedEmbeddedLongitude = AddColumnIfMissing(conn, "local_item_metadata", "embedded_longitude", "ALTER TABLE local_item_metadata ADD COLUMN embedded_longitude REAL;");
        AddColumnIfMissing(conn, "local_item_metadata", "description", "ALTER TABLE local_item_metadata ADD COLUMN description TEXT;");
        AddColumnIfMissing(conn, "local_item_metadata", "lens_model", "ALTER TABLE local_item_metadata ADD COLUMN lens_model TEXT;");
        AddColumnIfMissing(conn, "local_item_metadata", "exposure_time", "ALTER TABLE local_item_metadata ADD COLUMN exposure_time TEXT;");
        AddColumnIfMissing(conn, "local_item_metadata", "aperture", "ALTER TABLE local_item_metadata ADD COLUMN aperture REAL;");
        AddColumnIfMissing(conn, "local_item_metadata", "iso", "ALTER TABLE local_item_metadata ADD COLUMN iso INTEGER;");
        AddColumnIfMissing(conn, "local_item_metadata", "focal_length_mm", "ALTER TABLE local_item_metadata ADD COLUMN focal_length_mm REAL;");
        AddColumnIfMissing(conn, "local_item_metadata", "video_codec", "ALTER TABLE local_item_metadata ADD COLUMN video_codec TEXT;");
        AddColumnIfMissing(conn, "local_item_metadata", "frame_rate", "ALTER TABLE local_item_metadata ADD COLUMN frame_rate REAL;");
        AddColumnIfMissing(conn, "profile_view_preferences", "viewer_info_open", "ALTER TABLE profile_view_preferences ADD COLUMN viewer_info_open INTEGER NOT NULL DEFAULT 1;");
        var addedEmbeddedCapturedAt = AddColumnIfMissing(conn, "local_items", "embedded_captured_at", "ALTER TABLE local_items ADD COLUMN embedded_captured_at TEXT;");
        AddColumnIfMissing(conn, "local_items", "captured_at_user_override", "ALTER TABLE local_items ADD COLUMN captured_at_user_override INTEGER NOT NULL DEFAULT 0;");
        if (addedEmbeddedCapturedAt)
        {
            conn.Execute("UPDATE local_items SET embedded_captured_at = captured_at WHERE captured_at IS NOT NULL;");
        }
        if (addedLocationOverride || addedEmbeddedLatitude || addedEmbeddedLongitude)
        {
            using var localLocationBackfill = conn.CreateCommand();
            localLocationBackfill.CommandText = """
                UPDATE local_item_metadata
                   SET embedded_latitude = COALESCE(embedded_latitude, latitude),
                       embedded_longitude = COALESCE(embedded_longitude, longitude),
                       location_source = CASE
                           WHEN location_source IS NOT NULL THEN location_source
                           WHEN latitude IS NOT NULL AND longitude IS NOT NULL THEN 'embedded'
                           ELSE NULL END;
                """;
            localLocationBackfill.ExecuteNonQuery();
        }
        AddColumnIfMissing(
            conn,
            "media_assets",
            "presented_at",
            "ALTER TABLE media_assets ADD COLUMN presented_at TEXT;");

        AddColumnIfMissing(
            conn,
            "works",
            "ordinal_sort",
            "ALTER TABLE works ADD COLUMN ordinal_sort REAL;");

        AddColumnIfMissing(
            conn,
            "series_manifest_items",
            "membership_scope",
            "ALTER TABLE series_manifest_items ADD COLUMN membership_scope TEXT NOT NULL DEFAULT 'MainSequence';");

        AddColumnIfMissing(
            conn,
            "series_manifest_items",
            "ordinal_scope_qid",
            "ALTER TABLE series_manifest_items ADD COLUMN ordinal_scope_qid TEXT;");

        AddColumnIfMissing(
            conn,
            "series_manifest_items",
            "duration",
            "ALTER TABLE series_manifest_items ADD COLUMN duration TEXT;");

        AddColumnIfMissing(
            conn,
            "metadata_claims",
            "decision_source_provider_id",
            "ALTER TABLE metadata_claims ADD COLUMN decision_source_provider_id BLOB REFERENCES metadata_providers(id);");

        AddColumnIfMissing(conn, "metadata_claims", "observation_set_id",
            "ALTER TABLE metadata_claims ADD COLUMN observation_set_id BLOB;");
        AddColumnIfMissing(conn, "metadata_claims", "is_current",
            "ALTER TABLE metadata_claims ADD COLUMN is_current INTEGER NOT NULL DEFAULT 1 CHECK (is_current IN (0, 1));");
        AddColumnIfMissing(conn, "metadata_claims", "superseded_at",
            "ALTER TABLE metadata_claims ADD COLUMN superseded_at TEXT;");

        AddColumnIfMissing(conn, "identity_jobs", "poison_attempt_count",
            "ALTER TABLE identity_jobs ADD COLUMN poison_attempt_count INTEGER NOT NULL DEFAULT 0;");
        AddColumnIfMissing(conn, "identity_jobs", "last_outcome_category",
            "ALTER TABLE identity_jobs ADD COLUMN last_outcome_category TEXT;");
        AddColumnIfMissing(conn, "media_operations", "last_outcome_category",
            "ALTER TABLE media_operations ADD COLUMN last_outcome_category TEXT;");
        AddColumnIfMissing(conn, "media_operations", "poison_attempt_count",
            "ALTER TABLE media_operations ADD COLUMN poison_attempt_count INTEGER NOT NULL DEFAULT 0;");
        AddColumnIfMissing(conn, "entity_capability_states", "last_outcome_category",
            "ALTER TABLE entity_capability_states ADD COLUMN last_outcome_category TEXT;");
        AddColumnIfMissing(conn, "ai_feature_artifacts", "last_outcome_category",
            "ALTER TABLE ai_feature_artifacts ADD COLUMN last_outcome_category TEXT;");

        AddColumnIfMissing(
            conn,
            "player_queue_items",
            "year",
            "ALTER TABLE player_queue_items ADD COLUMN year TEXT;");

        AddColumnIfMissing(
            conn,
            "player_queue_items",
            "content_rating",
            "ALTER TABLE player_queue_items ADD COLUMN content_rating TEXT;");

        AddColumnIfMissing(
            conn,
            "player_queue_items",
            "season_number",
            "ALTER TABLE player_queue_items ADD COLUMN season_number TEXT;");

        AddColumnIfMissing(
            conn,
            "player_queue_items",
            "episode_number",
            "ALTER TABLE player_queue_items ADD COLUMN episode_number TEXT;");

        AddColumnIfMissing(
            conn,
            "player_queue_items",
            "episode_title",
            "ALTER TABLE player_queue_items ADD COLUMN episode_title TEXT;");

        AddColumnIfMissing(
            conn,
            "player_queue_items",
            "quality",
            "ALTER TABLE player_queue_items ADD COLUMN quality TEXT;");
    }

    private static void SeedMetadataProviders(SqliteConnection conn)
    {
        ReadOnlySpan<(Guid Id, string Name, string Version)> providers =
        [
            (WellKnownProviders.LocalProcessor, "local_processor", "1.0"),
            (WellKnownProviders.LibraryScanner, "library_scanner", "1.0"),
            (WellKnownProviders.AppleApi, "apple_api", "2.0"),
            (WellKnownProviders.Wikidata, "wikidata", "1.0"),
            (WellKnownProviders.Wikipedia, "wikipedia", "1.0"),
            (WellKnownProviders.OpenLibrary, "open_library", "1.0"),
            (WellKnownProviders.MusicBrainz, "musicbrainz", "1.0"),
            (WellKnownProviders.Tmdb, "tmdb", "1.0"),
            (WellKnownProviders.ComicVine, "comicvine", "1.0"),
            (WellKnownProviders.Lrclib, "lrclib", "1.0"),
            (WellKnownProviders.OpenSubtitles, "opensubtitles", "1.0"),
            (WellKnownProviders.Subdl, "subdl", "2.0"),
            (WellKnownProviders.UserManual, "user_manual", "1.0"),
            (WellKnownProviders.AiProvider, "ai_provider", "1.0"),
        ];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO metadata_providers (id, name, version, is_enabled)
            VALUES (@id, @name, @version, 1);
            """;

        var pId = cmd.Parameters.Add("@id", SqliteType.Blob);
        var pName = cmd.Parameters.Add("@name", SqliteType.Text);
        var pVersion = cmd.Parameters.Add("@version", SqliteType.Text);

        foreach (var (id, name, version) in providers)
        {
            pId.Value = GuidSql.ToBlob(id);
            pName.Value = name;
            pVersion.Value = version;
            cmd.ExecuteNonQuery();
        }
    }

    private static void SeedDefaultProfile(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO profiles (id, display_name, avatar_color, role, created_at)
            VALUES (@id, @name, @color, @role, @created);
            """;
        cmd.Parameters.Add("@id", SqliteType.Blob).Value =
            GuidSql.ToBlob(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        cmd.Parameters.AddWithValue("@name", "Owner");
        cmd.Parameters.AddWithValue("@color", "#7C4DFF");
        cmd.Parameters.AddWithValue("@role", "Administrator");
        cmd.Parameters.AddWithValue("@created", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static void EnsureCurrentIndexes(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH duplicate_pending_reviews AS (
                SELECT rowid,
                       ROW_NUMBER() OVER (
                           PARTITION BY entity_id, trigger
                           ORDER BY created_at ASC, rowid ASC
                       ) AS rn
                FROM review_queue
                WHERE status = 'Pending'
            )
            UPDATE review_queue
            SET status = 'Resolved',
                resolved_at = strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                resolved_by = 'system:review-dedupe'
            WHERE rowid IN (
                SELECT rowid
                FROM duplicate_pending_reviews
                WHERE rn > 1
            );

            CREATE INDEX IF NOT EXISTS idx_editions_work_id
                ON editions(work_id);

            CREATE INDEX IF NOT EXISTS idx_media_assets_edition_id
                ON media_assets(edition_id);

            CREATE INDEX IF NOT EXISTS idx_media_assets_presented
                ON media_assets(presented_at) WHERE presented_at IS NOT NULL;

            CREATE INDEX IF NOT EXISTS idx_media_assets_file_path_root
                ON media_assets(file_path_root COLLATE NOCASE);

            CREATE INDEX IF NOT EXISTS idx_media_assets_content_hash
                ON media_assets(content_hash);

            CREATE INDEX IF NOT EXISTS idx_media_assets_status
                ON media_assets(status);

            DELETE FROM file_hash_cache
            WHERE rowid NOT IN (
                SELECT MAX(rowid)
                FROM file_hash_cache
                GROUP BY absolute_path COLLATE NOCASE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_file_hash_cache_path_nocase
                ON file_hash_cache(absolute_path COLLATE NOCASE);

            CREATE INDEX IF NOT EXISTS idx_collection_items_collection_sort
                ON collection_items(collection_id, sort_order);

            CREATE INDEX IF NOT EXISTS idx_collection_items_work
                ON collection_items(work_id);

            CREATE INDEX IF NOT EXISTS idx_works_collection_ordinal_sort
                ON works(collection_id, ordinal_sort);

            CREATE INDEX IF NOT EXISTS idx_canonical_values_key_value_entity
                ON canonical_values(key, value, entity_id);

            CREATE INDEX IF NOT EXISTS idx_canonical_values_entity_key
                ON canonical_values(entity_id, key);

            CREATE INDEX IF NOT EXISTS idx_canonical_value_arrays_key_value_entity
                ON canonical_value_arrays(key, value, entity_id);

            CREATE INDEX IF NOT EXISTS idx_canonical_value_arrays_key_qid_entity
                ON canonical_value_arrays(key, value_qid, entity_id);

            CREATE INDEX IF NOT EXISTS idx_metadata_claims_current_lookup
                ON metadata_claims(entity_id, provider_id, claim_key, is_current);

            CREATE INDEX IF NOT EXISTS idx_person_media_links_person
                ON person_media_links(person_id);

            CREATE INDEX IF NOT EXISTS idx_person_media_links_asset_role_person
                ON person_media_links(media_asset_id, role, person_id);

            CREATE INDEX IF NOT EXISTS idx_persons_name_nocase
                ON persons(name COLLATE NOCASE);

            CREATE INDEX IF NOT EXISTS idx_works_curator_state
                ON works(curator_state) WHERE curator_state IS NOT NULL;

            CREATE INDEX IF NOT EXISTS idx_works_catalog_media_type
                ON works(is_catalog_only, media_type);

            CREATE INDEX IF NOT EXISTS idx_ingestion_log_run_created
                ON ingestion_log(ingestion_run_id, created_at);

            CREATE INDEX IF NOT EXISTS idx_identity_jobs_run_entity_updated
                ON identity_jobs(ingestion_run_id, entity_id, updated_at);

            CREATE INDEX IF NOT EXISTS idx_identity_jobs_activity_latest
                ON identity_jobs(ingestion_run_id, entity_id, updated_at, created_at);

            CREATE INDEX IF NOT EXISTS idx_identity_jobs_next_retry
                ON identity_jobs(next_retry_at) WHERE next_retry_at IS NOT NULL;

            CREATE INDEX IF NOT EXISTS idx_media_operations_source_path
                ON media_operations(operation_type, source_path, status);

            CREATE INDEX IF NOT EXISTS idx_media_operations_batch_entity_type
                ON media_operations(batch_id, entity_id, operation_type);

            CREATE INDEX IF NOT EXISTS idx_media_operation_events_batch_entity
                ON media_operation_events(batch_id, entity_id, occurred_at);

            CREATE INDEX IF NOT EXISTS idx_review_queue_status_entity_ready
                ON review_queue(status, entity_id, review_ready_at);

            CREATE INDEX IF NOT EXISTS idx_review_queue_status
                ON review_queue(status);

            CREATE INDEX IF NOT EXISTS idx_review_queue_entity_id
                ON review_queue(entity_id);

            CREATE INDEX IF NOT EXISTS idx_ingestion_batches_status
                ON ingestion_batches(status);

            CREATE INDEX IF NOT EXISTS idx_ingestion_batch_artifacts_batch
                ON ingestion_batch_artifacts(batch_id);

            CREATE INDEX IF NOT EXISTS ix_local_items_owner_timeline
                ON local_items(owner_profile_id, archived_at, trashed_at, COALESCE(captured_at, created_at) DESC, id DESC);

            CREATE INDEX IF NOT EXISTS ix_local_items_space_timeline
                ON local_items(personal_space_id, archived_at, trashed_at, COALESCE(captured_at, created_at) DESC, id DESC);

            CREATE INDEX IF NOT EXISTS ix_local_items_library_favorite_timeline
                ON local_items(library_id, favorite, archived_at, trashed_at, COALESCE(captured_at, created_at) DESC, id DESC);

            CREATE INDEX IF NOT EXISTS ix_local_item_metadata_location
                ON local_item_metadata(location_name, latitude, longitude, item_id)
                WHERE latitude IS NOT NULL AND longitude IS NOT NULL;

            CREATE INDEX IF NOT EXISTS ix_local_items_library_atlas_timeline
                ON local_items(library_id, hidden, archived_at, trashed_at, media_kind,
                               favorite, COALESCE(captured_at, created_at), id);

            CREATE INDEX IF NOT EXISTS ix_view_galleries_owner_order
                ON view_galleries(owner_profile_id, sort_order, updated_at DESC, id);

            CREATE INDEX IF NOT EXISTS ix_view_gallery_shares_profile
                ON view_gallery_shares(profile_id, shared_at DESC, gallery_id);

            CREATE INDEX IF NOT EXISTS ix_local_item_tags_tag
                ON local_item_tags(tag, item_id);

            CREATE INDEX IF NOT EXISTS ix_local_item_annotations_lookup
                ON local_item_annotations(annotation_kind, annotation_value, item_id);

            CREATE INDEX IF NOT EXISTS ix_collection_view_sources_collection
                ON collection_view_sources(collection_id, position, id);

            CREATE INDEX IF NOT EXISTS ix_collection_view_sources_owner
                ON collection_view_sources(owner_profile_id, source_kind, collection_id, id);

            CREATE UNIQUE INDEX IF NOT EXISTS ux_collection_view_sources_gallery
                ON collection_view_sources(collection_id, gallery_id)
                WHERE source_kind = 'gallery';

            CREATE INDEX IF NOT EXISTS idx_system_activity_run_entity_action
                ON system_activity(ingestion_run_id, entity_id, action_type, occurred_at);

            CREATE UNIQUE INDEX IF NOT EXISTS ux_review_queue_pending_entity_trigger
                ON review_queue(entity_id, trigger)
                WHERE status = 'Pending';

            CREATE UNIQUE INDEX IF NOT EXISTS ux_collections_custom_rule_hash
                ON collections(rule_hash)
                WHERE rule_hash IS NOT NULL AND is_enabled = 1 AND collection_type = 'Custom';
            """;
        cmd.ExecuteNonQuery();
    }

    private static bool AddColumnIfMissing(
        SqliteConnection conn,
        string table,
        string column,
        string alterSql)
    {
        using var exists = conn.CreateCommand();
        exists.CommandText = $"PRAGMA table_info([{table}]);";
        using var reader = exists.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        using var alter = conn.CreateCommand();
        alter.CommandText = alterSql;
        alter.ExecuteNonQuery();
        return true;
    }

    private static void EnsureEditionArtworkOwnerSchema(SqliteConnection conn)
    {
        using var inspect = conn.CreateCommand();
        inspect.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='entity_assets';";
        var tableSql = inspect.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(tableSql)
            || tableSql.Contains("'Edition'", StringComparison.Ordinal))
        {
            return;
        }

        // Keep canonical and legacy preference stores in step. A canonical-only
        // Edition link would be invisible to readers still using entity_assets.
        DatabaseConnection.ExecuteStartupTransaction(conn, transaction =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                ALTER TABLE entity_assets RENAME TO entity_assets_pre_edition_owner;

                CREATE TABLE entity_assets (
                    id BLOB PRIMARY KEY,
                    entity_id BLOB NOT NULL,
                    entity_type TEXT NOT NULL CHECK(entity_type IN ('Work','Edition','Person','Universe','FictionalEntity')),
                    asset_type TEXT NOT NULL CHECK(asset_type IN ('CoverArt','Headshot','Banner','Logo','NetworkLogo','StudioLogo','Background','SeasonPoster','SeasonThumb','EpisodeStill','CharacterPortrait')),
                    image_url TEXT,
                    local_image_path TEXT,
                    local_image_path_s TEXT,
                    local_image_path_m TEXT,
                    local_image_path_l TEXT,
                    source_provider TEXT,
                    width_px INTEGER,
                    height_px INTEGER,
                    aspect_class TEXT NOT NULL DEFAULT 'UnsupportedRect',
                    primary_hex TEXT,
                    secondary_hex TEXT,
                    accent_hex TEXT,
                    asset_class TEXT NOT NULL DEFAULT 'Artwork',
                    storage_location TEXT NOT NULL DEFAULT 'Central',
                    owner_scope TEXT NOT NULL DEFAULT 'Unknown',
                    is_preferred INTEGER NOT NULL DEFAULT 0,
                    is_user_override INTEGER NOT NULL DEFAULT 0,
                    is_locally_exported INTEGER NOT NULL DEFAULT 0,
                    is_preferred_exported INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at TEXT
                );

                INSERT INTO entity_assets (
                    id, entity_id, entity_type, asset_type, image_url,
                    local_image_path, local_image_path_s, local_image_path_m, local_image_path_l,
                    source_provider, width_px, height_px, aspect_class,
                    primary_hex, secondary_hex, accent_hex, asset_class, storage_location, owner_scope,
                    is_preferred, is_user_override, is_locally_exported, is_preferred_exported,
                    created_at, updated_at)
                SELECT id, entity_id, entity_type, asset_type, image_url,
                    local_image_path, local_image_path_s, local_image_path_m, local_image_path_l,
                    source_provider, width_px, height_px, aspect_class,
                    primary_hex, secondary_hex, accent_hex, asset_class, storage_location, owner_scope,
                    is_preferred, is_user_override, is_locally_exported, is_preferred_exported,
                    created_at, updated_at
                FROM entity_assets_pre_edition_owner;

                DROP TABLE entity_assets_pre_edition_owner;
                CREATE INDEX idx_entity_assets_entity ON entity_assets(entity_id, entity_type);
                CREATE INDEX idx_entity_assets_type ON entity_assets(entity_id, asset_type);
                CREATE UNIQUE INDEX ux_entity_assets_entity_type_source_url
                    ON entity_assets(entity_id, asset_type, image_url COLLATE NOCASE)
                    WHERE image_url IS NOT NULL AND length(trim(image_url)) > 0;
                INSERT OR IGNORE INTO schema_migrations (migration_id, applied_at)
                VALUES ('007_edition_artwork_owner', strftime('%Y-%m-%dT%H:%M:%fZ','now'));
                """;
            cmd.ExecuteNonQuery();
        });
    }
}
