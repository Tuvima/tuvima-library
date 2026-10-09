using System.Data;
using System.Threading.Tasks;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

public sealed class DatabaseStartupSafetyTests
{
    [Fact]
    public void FreshDatabase_CanonicalArtworkSupportsOneImageManyEntityRoles()
    {
        using var fixture = TempDatabase.Create();
        fixture.Database.InitializeSchema();
        fixture.Database.RunStartupChecks();

        using var conn = fixture.Database.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO artwork_assets(id, content_hash, original_path)
            VALUES (X'11111111111111111111111111111111', 'same-bytes', 'managed/image.jpg');

            INSERT INTO entity_artwork_links(id, entity_id, entity_type, artwork_asset_id, role, is_preferred)
            VALUES (X'21111111111111111111111111111111', X'31111111111111111111111111111111', 'Work', X'11111111111111111111111111111111', 'Primary', 1);
            INSERT INTO entity_artwork_links(id, entity_id, entity_type, artwork_asset_id, role, is_preferred)
            VALUES (X'41111111111111111111111111111111', X'51111111111111111111111111111111', 'Collection', X'11111111111111111111111111111111', 'Logo', 1);

            SELECT (SELECT COUNT(*) FROM artwork_assets),
                   (SELECT COUNT(*) FROM entity_artwork_links),
                   (SELECT COUNT(DISTINCT role) FROM entity_artwork_links);
            """;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(2L, reader.GetInt64(1));
        Assert.Equal(2L, reader.GetInt64(2));
    }

    [Fact]
    public void CanonicalArtwork_RemovingOneLinkKeepsSharedAssetAndOtherUsage()
    {
        using var fixture = TempDatabase.Create();
        fixture.Database.InitializeSchema();
        fixture.Database.RunStartupChecks();

        using var conn = fixture.Database.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO artwork_assets(id, content_hash, original_path)
            VALUES (X'12111111111111111111111111111111', 'shared-content', 'managed/shared.jpg');
            INSERT INTO entity_artwork_links(id, entity_id, entity_type, artwork_asset_id, role)
            VALUES (X'22111111111111111111111111111111', X'32111111111111111111111111111111', 'Work', X'12111111111111111111111111111111', 'Primary');
            INSERT INTO entity_artwork_links(id, entity_id, entity_type, artwork_asset_id, role)
            VALUES (X'42111111111111111111111111111111', X'52111111111111111111111111111111', 'Person', X'12111111111111111111111111111111', 'Portrait');
            DELETE FROM entity_artwork_links WHERE id=X'22111111111111111111111111111111';
            SELECT (SELECT COUNT(*) FROM artwork_assets), (SELECT COUNT(*) FROM entity_artwork_links);
            """;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    [Fact]
    public void CanonicalArtwork_ContentHashPreventsDuplicateAssets()
    {
        using var fixture = TempDatabase.Create();
        fixture.Database.InitializeSchema();
        fixture.Database.RunStartupChecks();

        using var conn = fixture.Database.CreateConnection();
        using var first = conn.CreateCommand();
        first.CommandText = "INSERT INTO artwork_assets(id, content_hash) VALUES (randomblob(16), 'duplicate-hash');";
        first.ExecuteNonQuery();
        using var duplicate = conn.CreateCommand();
        duplicate.CommandText = "INSERT INTO artwork_assets(id, content_hash) VALUES (randomblob(16), 'duplicate-hash');";
        Assert.Throws<SqliteException>(() => duplicate.ExecuteNonQuery());
    }

    [Fact]
    public void StartupWriteCheck_ReadOnlyDatabase_ReturnsActionableFailure()
    {
        using var fixture = TempDatabase.Create();
        fixture.Database.InitializeSchema();
        fixture.Database.Dispose();

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fixture.Path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var exception = Assert.Throws<DatabaseWriteAccessException>(() =>
            new DatabaseIntegrityChecker().EnsureWritable(connection, fixture.Path));

        Assert.Contains("cannot start", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read-only", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.GetFullPath(fixture.Path), exception.Message, StringComparison.Ordinal);
        Assert.Contains("parent directory", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<SqliteException>(exception.InnerException);
    }

    [Fact]
    public void FreshDatabase_AllowsManagedNetworkAndStudioLogos()
    {
        using var fixture = TempDatabase.Create();
        fixture.Database.InitializeSchema();
        fixture.Database.RunStartupChecks();

        using var conn = fixture.Database.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO entity_assets (id, entity_id, entity_type, asset_type)
            VALUES (randomblob(16), randomblob(16), 'Work', 'NetworkLogo');
            INSERT INTO entity_assets (id, entity_id, entity_type, asset_type)
            VALUES (randomblob(16), randomblob(16), 'Work', 'StudioLogo');
            SELECT COUNT(*) FROM entity_assets
            WHERE asset_type IN ('NetworkLogo', 'StudioLogo');
            """;

        Assert.Equal(2L, Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void FreshDatabase_InitializesRequiredTablesIndexesAndWalSettings()
    {
        using var fixture = TempDatabase.Create();
        fixture.Database.InitializeSchema();
        fixture.Database.RunStartupChecks();

        using var conn = fixture.Database.CreateConnection();

        string[] requiredTables =
        [
            "media_assets",
            "editions",
            "works",
            "collections",
            "review_queue",
            "user_states",
            "profiles",
            "ingestion_batches",
            "ingestion_batch_artifacts",
            "search_index",
            "local_items",
            "local_files",
            "local_file_sources",
            "view_personal_spaces",
            "view_sources",
            "view_folder_pins",
            "view_folder_timeline_policies",
            "view_devices",
            "view_galleries",
            "view_gallery_items",
            "view_gallery_shares",
            "collection_view_sources",
            "profile_view_policies",
            "profile_view_preferences",
            "client_devices",
            "device_pairing_requests",
            "client_tokens",
            "households",
            "accounts",
            "account_profile_grants",
            "account_feature_grants",
            "account_library_grants",
            "grant_admin_protections",
            "grant_admin_unlocks",
            "applications",
            "application_credentials",
            "application_permission_grants",
            "application_client_bindings",
            "authorization_audit_events",
            "view_shared_library",
            "view_shared_assets",
        ];

        foreach (var table in requiredTables)
        {
            Assert.True(TableExists(conn, table), $"Required table '{table}' was not created.");
        }

        string[] requiredIndexes =
        [
            "idx_review_queue_status",
            "idx_review_queue_entity_id",
            "idx_media_assets_content_hash",
            "idx_media_assets_file_path_root",
            "idx_ingestion_batches_status",
            "idx_ingestion_batch_artifacts_batch",
            "ix_local_items_owner_timeline",
            "ix_local_items_space_timeline",
            "ix_local_items_library_favorite_timeline",
            "ix_local_item_metadata_location",
            "ix_view_galleries_owner_order",
            "ix_view_gallery_shares_profile",
            "ix_local_item_tags_tag",
            "ix_local_item_annotations_lookup",
            "ix_collection_view_sources_collection",
            "ix_collection_view_sources_owner",
            "ux_collection_view_sources_gallery",
        ];

        foreach (var index in requiredIndexes)
        {
            Assert.True(IndexExists(conn, index), $"Required index '{index}' was not created.");
        }

        Assert.Equal("wal", Scalar(conn, "PRAGMA journal_mode;").ToLowerInvariant());
        Assert.Equal("1", Scalar(conn, "PRAGMA synchronous;"));
        Assert.Equal("1", Scalar(conn, "PRAGMA foreign_keys;"));
        Assert.Equal("5000", Scalar(conn, "PRAGMA busy_timeout;"));
        Assert.Equal("-16384", Scalar(conn, "PRAGMA cache_size;"));
        Assert.Equal("ok", Scalar(conn, "PRAGMA integrity_check;"));

        foreach (var retiredTable in new[]
                 {
                     "photo_assets",
                     "photo_metadata",
                     "photo_sources",
                     "photo_albums",
                     "photo_album_items",
                     "local_collections",
                     "local_collection_items",
                 })
        {
            Assert.False(TableExists(conn, retiredTable),
                $"Retired Photos table '{retiredTable}' must not be recreated in the View storage epoch.");
        }
    }

    [Fact]
    public void FreshDatabase_UsesGuidBlobStorageEpoch()
    {
        using var fixture = TempDatabase.Create();
        fixture.Database.InitializeSchema();
        fixture.Database.RunStartupChecks();

        using var conn = fixture.Database.CreateConnection();
        Assert.Equal("guid-blob-v10-graph-timeline-lore", Scalar(conn, "SELECT value FROM storage_metadata WHERE key = 'storage_epoch';"));

        (string Table, string Column)[] internalGuidColumns =
        [
            ("metadata_providers", "id"),
            ("provider_config", "provider_id"),
            ("collections", "id"),
            ("collection_profile_audience", "collection_id"),
            ("collection_profile_audience", "profile_id"),
            ("works", "id"),
            ("editions", "id"),
            ("media_assets", "id"),
            ("media_artwork_writeback", "media_asset_id"),
            ("media_artwork_writeback", "desired_artwork_asset_id"),
            ("media_artwork_writeback", "embedded_artwork_asset_id"),
            ("media_editor_commits", "asset_id"),
            ("media_editor_commits", "source_work_id"),
            ("media_editor_commits", "target_work_id"),
            ("media_file_write_intents", "asset_id"),
            ("media_editor_commit_items", "asset_id"),
            ("media_editor_commit_items", "source_edition_id"),
            ("media_editor_commit_items", "source_work_id"),
            ("media_editor_commit_items", "target_work_id"),
            ("media_editor_commit_items", "source_season_work_id"),
            ("media_editor_commit_items", "target_season_work_id"),
            ("media_editor_music_pairing_commit_items", "asset_id"),
            ("media_editor_music_pairing_commit_items", "edition_id"),
            ("media_editor_music_pairing_commit_items", "work_id"),
            ("media_editor_commit_artwork", "owner_work_id"),
            ("media_editor_commit_artwork", "artwork_asset_id"),
            ("media_editor_preferred_artwork_commits", "owner_work_id"),
            ("media_editor_preferred_artwork_commits", "artwork_asset_id"),
            ("media_editor_edition_artwork_commits", "edition_id"),
            ("media_editor_edition_artwork_commits", "work_id"),
            ("media_editor_edition_artwork_commits", "artwork_asset_id"),
            ("persons", "id"),
            ("metadata_claims", "id"),
            ("metadata_claims", "entity_id"),
            ("metadata_claims", "observation_set_id"),
            ("metadata_claims", "provider_id"),
            ("metadata_claims", "decision_source_provider_id"),
            ("ai_feature_artifacts", "entity_id"),
            ("ai_feature_artifacts", "source_provider_id"),
            ("canonical_values", "entity_id"),
            ("canonical_values", "winning_provider_id"),
            ("canonical_value_arrays", "entity_id"),
            ("profiles", "id"),
            ("entity_assets", "id"),
            ("entity_assets", "entity_id"),
            ("artwork_assets", "id"),
            ("entity_artwork_links", "id"),
            ("entity_artwork_links", "entity_id"),
            ("entity_artwork_links", "artwork_asset_id"),
            ("artwork_asset_context", "artwork_asset_id"),
            ("artwork_asset_context", "entity_id"),
            ("pending_person_signals", "id"),
            ("pending_person_signals", "entity_id"),
            ("system_activity", "entity_id"),
            ("system_activity", "profile_id"),
            ("system_activity", "ingestion_run_id"),
            ("review_queue", "id"),
            ("review_queue", "entity_id"),
            ("review_queue", "source_operation_id"),
            ("ingestion_batches", "id"),
            ("ingestion_batch_artifacts", "id"),
            ("ingestion_batch_artifacts", "batch_id"),
            ("ingestion_batch_artifacts", "artifact_id"),
            ("ingestion_batch_artifacts", "parent_entity_id"),
            ("ingestion_log", "id"),
            ("ingestion_log", "media_asset_id"),
            ("ingestion_log", "ingestion_run_id"),
            ("bridge_ids", "id"),
            ("bridge_ids", "entity_id"),
            ("identity_jobs", "id"),
            ("identity_jobs", "entity_id"),
            ("identity_jobs", "ingestion_run_id"),
            ("identity_jobs", "selected_candidate_id"),
            ("retail_match_candidates", "id"),
            ("retail_match_candidates", "job_id"),
            ("wikidata_bridge_candidates", "id"),
            ("wikidata_bridge_candidates", "job_id"),
            ("person_aliases", "pseudonym_person_id"),
            ("person_aliases", "real_person_id"),
            ("person_media_links", "media_asset_id"),
            ("person_media_links", "person_id"),
            ("person_roles", "person_id"),
            ("person_group_members", "group_id"),
            ("person_group_members", "member_id"),
            ("character_performer_links", "person_id"),
            ("character_performer_links", "fictional_entity_id"),
            ("character_portraits", "id"),
            ("character_portraits", "person_id"),
            ("character_portraits", "fictional_entity_id"),
            ("client_devices", "id"),
            ("client_devices", "application_id"),
            ("client_devices", "account_id"),
            ("client_devices", "profile_id"),
            ("client_tokens", "id"),
            ("client_tokens", "application_id"),
            ("client_tokens", "account_id"),
            ("client_tokens", "device_id"),
            ("client_tokens", "profile_id"),
            ("client_tokens", "token_family_id"),
            ("media_operations", "id"),
            ("media_operations", "entity_id"),
            ("media_operations", "batch_id"),
            ("media_operation_events", "id"),
            ("media_operation_events", "operation_id"),
            ("media_operation_events", "entity_id"),
            ("media_operation_events", "batch_id"),
            ("entity_capability_states", "id"),
            ("entity_capability_states", "entity_id"),
            ("entity_capability_states", "last_operation_id"),
            ("entity_events", "id"),
            ("entity_events", "entity_id"),
            ("entity_events", "ingestion_run_id"),
            ("entity_field_changes", "id"),
            ("entity_field_changes", "event_id"),
            ("entity_field_changes", "entity_id"),
            ("alignment_jobs", "id"),
            ("alignment_jobs", "ebook_asset_id"),
            ("alignment_jobs", "audiobook_asset_id"),
            ("audio_fingerprints", "asset_id"),
            ("collection_items", "id"),
            ("collection_items", "collection_id"),
            ("collection_items", "work_id"),
            ("collection_placements", "id"),
            ("collection_placements", "collection_id"),
            ("collection_relationships", "id"),
            ("collection_relationships", "collection_id"),
            ("collections", "universe_id"),
            ("collections", "parent_collection_id"),
            ("collections", "profile_id"),
            ("deferred_enrichment_queue", "id"),
            ("deferred_enrichment_queue", "entity_id"),
            ("device_pairing_requests", "id"),
            ("device_pairing_requests", "application_id"),
            ("device_pairing_requests", "account_id"),
            ("device_pairing_requests", "profile_id"),
            ("device_pairing_requests", "approved_by_profile_id"),
            ("editions", "work_id"),
            ("encode_jobs", "id"),
            ("encode_jobs", "asset_id"),
            ("encode_jobs", "owner_profile_id"),
            ("encode_jobs", "owner_device_id"),
            ("enrichment_refresh_schedule", "entity_id"),
            ("enrichment_refresh_schedule", "operation_id"),
            ("entity_relationships", "id"),
            ("entity_relationship_qualifiers", "id"),
            ("entity_relationship_qualifiers", "relationship_id"),
            ("fictional_entities", "id"),
            ("fictional_entity_user_overrides", "entity_id"),
            ("fictional_entity_work_links", "id"),
            ("fictional_entity_work_links", "entity_id"),
            ("plugin_lore_sources", "id"),
            ("plugin_lore_entities", "id"),
            ("plugin_lore_entities", "source_id"),
            ("plugin_lore_relationships", "id"),
            ("plugin_lore_relationships", "source_id"),
            ("media_assets", "edition_id"),
            ("media_assets", "derived_from_asset_id"),
            ("local_items", "id"),
            ("local_items", "library_id"),
            ("local_items", "personal_space_id"),
            ("local_items", "owner_profile_id"),
            ("local_item_metadata", "item_id"),
            ("local_files", "id"),
            ("local_file_sources", "id"),
            ("local_file_sources", "file_id"),
            ("local_file_sources", "library_id"),
            ("local_file_sources", "source_id"),
            ("local_file_sources", "device_id"),
            ("local_item_files", "item_id"),
            ("local_item_files", "file_id"),
            ("local_item_tags", "item_id"),
            ("view_personal_spaces", "id"),
            ("view_personal_spaces", "owner_profile_id"),
            ("view_personal_spaces", "library_id"),
            ("view_storage_labels", "personal_space_id"),
            ("view_sources", "id"),
            ("view_sources", "library_id"),
            ("view_sources", "personal_space_id"),
            ("view_source_policies", "source_id"),
            ("view_folder_pins", "profile_id"),
            ("view_folder_pins", "source_id"),
            ("view_folder_timeline_policies", "source_id"),
            ("view_folder_timeline_policies", "updated_by_profile_id"),
            ("view_devices", "id"),
            ("view_devices", "personal_space_id"),
            ("view_devices", "source_id"),
            ("view_galleries", "id"),
            ("view_galleries", "owner_profile_id"),
            ("view_galleries", "personal_space_id"),
            ("view_galleries", "cover_item_id"),
            ("view_galleries", "soundtrack_playlist_id"),
            ("view_gallery_items", "gallery_id"),
            ("view_gallery_items", "item_id"),
            ("view_gallery_shares", "gallery_id"),
            ("view_gallery_shares", "profile_id"),
            ("view_shared_assets", "item_id"),
            ("view_shared_assets", "origin_item_id"),
            ("view_shared_assets", "original_profile_id"),
            ("view_shared_assets", "promoted_by_profile_id"),
            ("view_shared_contributions", "id"),
            ("view_shared_contributions", "submitted_by_profile_id"),
            ("view_shared_library", "library_id"),
            ("view_shared_contributions", "decided_by_profile_id"),
            ("view_shared_contribution_items", "id"),
            ("view_shared_contribution_items", "contribution_id"),
            ("view_shared_contribution_items", "item_id"),
            ("view_shared_contribution_items", "original_profile_id"),
            ("view_shared_contribution_events", "id"),
            ("view_shared_contribution_events", "contribution_id"),
            ("view_shared_contribution_events", "actor_profile_id"),
            ("view_shared_transfers", "id"),
            ("view_shared_transfers", "item_id"),
            ("view_shared_transfers", "contribution_item_id"),
            ("collection_view_sources", "id"),
            ("collection_view_sources", "collection_id"),
            ("collection_view_sources", "owner_profile_id"),
            ("collection_view_sources", "gallery_id"),
            ("profile_view_policies", "profile_id"),
            ("profile_view_preferences", "profile_id"),
            ("profile_view_preferences", "last_scope_profile_id"),
            ("local_item_annotations", "id"),
            ("local_item_annotations", "item_id"),
            ("local_item_search_keys", "item_id"),
            ("offline_variants", "id"),
            ("offline_variants", "asset_id"),
            ("offline_variants", "owner_profile_id"),
            ("offline_variants", "owner_device_id"),
            ("adaptive_hls_packages", "id"),
            ("adaptive_hls_packages", "asset_id"),
            ("onboarding_restore_operations", "id"),
            ("onboarding_sessions", "id"),
            ("setup_codes", "id"),
            ("onboarding_workflows", "administrator_profile_id"),
            ("playback_inspection_cache", "asset_id"),
            ("player_sessions", "profile_id"),
            ("player_sessions", "session_id"),
            ("player_sessions", "current_queue_item_id"),
            ("player_queue_items", "id"),
            ("player_queue_items", "profile_id"),
            ("player_queue_items", "work_id"),
            ("player_queue_items", "asset_id"),
            ("player_queue_items", "collection_id"),
            ("music_play_active_segments", "profile_id"),
            ("music_play_active_segments", "work_id"),
            ("music_play_active_segments", "asset_id"),
            ("music_play_active_segments", "queue_item_id"),
            ("music_play_stats", "profile_id"),
            ("music_play_stats", "work_id"),
            ("audiobook_listen_active_segments", "profile_id"),
            ("audiobook_listen_active_segments", "work_id"),
            ("audiobook_listen_active_segments", "asset_id"),
            ("audiobook_listen_active_segments", "queue_item_id"),
            ("audiobook_listen_history", "id"),
            ("audiobook_listen_history", "profile_id"),
            ("audiobook_listen_history", "work_id"),
            ("audiobook_listen_history", "asset_id"),
            ("accounts", "id"),
            ("accounts", "household_id"),
            ("households", "id"),
            ("profiles", "household_id"),
            ("account_profile_grants", "account_id"),
            ("account_profile_grants", "profile_id"),
            ("account_feature_grants", "account_id"),
            ("account_library_grants", "account_id"),
            ("account_library_grants", "library_id"),
            ("grant_admin_protections", "account_id"),
            ("grant_admin_protections", "profile_id"),
            ("account_invitations", "id"),
            ("account_invitations", "account_id"),
            ("account_passkeys", "account_id"),
            ("account_external_logins", "id"),
            ("account_external_logins", "account_id"),
            ("account_credentials", "id"),
            ("account_credentials", "account_id"),
            ("auth_sessions", "id"),
            ("auth_sessions", "account_id"),
            ("auth_sessions", "active_profile_id"),
            ("grant_admin_unlocks", "session_id"),
            ("grant_admin_unlocks", "account_id"),
            ("grant_admin_unlocks", "profile_id"),
            ("applications", "id"),
            ("application_credentials", "id"),
            ("application_credentials", "application_id"),
            ("application_permission_grants", "application_id"),
            ("application_client_bindings", "application_id"),
            ("authorization_audit_events", "actor_account_id"),
            ("authorization_audit_events", "actor_profile_id"),
            ("authorization_audit_events", "actor_application_id"),
            ("password_reset_challenges", "id"),
            ("password_reset_challenges", "account_id"),
            ("audiobook_bookmarks", "id"),
            ("audiobook_bookmarks", "profile_id"),
            ("audiobook_bookmarks", "work_id"),
            ("audiobook_bookmarks", "asset_id"),
            ("audiobook_chapter_title_overrides", "work_id"),
            ("audiobook_chapter_title_overrides", "asset_id"),
            ("playback_segments", "id"),
            ("playback_segments", "asset_id"),
            ("profile_credentials", "id"),
            ("profile_credentials", "profile_id"),
            ("profile_person_preferences", "person_id"),
            ("profile_person_preferences", "profile_id"),
            ("profile_reactions", "entity_id"),
            ("profile_reactions", "profile_id"),
            ("profile_saved_items", "entity_id"),
            ("profile_saved_items", "profile_id"),
            ("profile_sequence_preferences", "profile_id"),
            ("profile_work_preferences", "profile_id"),
            ("profile_work_preferences", "work_id"),
            ("consumption_history", "id"),
            ("consumption_history", "profile_id"),
            ("consumption_history", "asset_id"),
            ("tv_episode_credits", "work_id"),
            ("tv_episode_credits", "show_work_id"),
            ("personal_status_commands", "id"),
            ("personal_status_commands", "profile_id"),
            ("personal_status_commands", "target_id"),
            ("reader_bookmarks", "id"),
            ("reader_bookmarks", "asset_id"),
            ("reader_highlights", "id"),
            ("reader_highlights", "asset_id"),
            ("reader_statistics", "id"),
            ("reader_statistics", "asset_id"),
            ("password_recovery_codes", "id"),
            ("password_recovery_codes", "account_id"),
            ("retail_match_candidates", "provider_id"),
            ("search_results_cache", "entity_id"),
            ("series_manifest_hydrations", "collection_id"),
            ("series_manifest_items", "id"),
            ("series_manifest_items", "collection_id"),
            ("series_manifest_items", "linked_work_id"),
            ("service_credentials", "id"),
            ("identity_audit_events", "account_id"),
            ("application_events", "event_id"),
            ("application_events", "library_id"),
            ("application_events", "profile_id"),
            ("application_webhooks", "id"),
            ("application_webhooks", "application_id"),
            ("application_webhook_deliveries", "id"),
            ("application_webhook_deliveries", "webhook_id"),
            ("application_webhook_deliveries", "event_id"),
            ("playback_telemetry_sessions", "id"),
            ("playback_telemetry_sessions", "player_session_id"),
            ("playback_telemetry_sessions", "authority_session_id"),
            ("playback_telemetry_sessions", "account_id"),
            ("playback_telemetry_sessions", "profile_id"),
            ("playback_telemetry_sessions", "application_id"),
            ("playback_telemetry_sessions", "device_id"),
            ("playback_telemetry_sessions", "asset_id"),
            ("playback_telemetry_sessions", "library_id"),
            ("identity_audit_events", "profile_id"),
            ("identity_audit_events", "session_id"),
            ("text_tracks", "id"),
            ("text_tracks", "asset_id"),
            ("transaction_log", "entity_id"),
            ("user_playback_settings", "profile_id"),
            ("user_states", "user_id"),
            ("user_states", "asset_id"),
            ("user_taste_profiles", "user_id"),
            ("works", "collection_id"),
            ("works", "parent_work_id"),
        ];

        foreach (var (table, column) in internalGuidColumns)
        {
            Assert.Equal("BLOB", ColumnType(conn, table, column));
        }

        var expectedGuidColumns = internalGuidColumns
            .Select(column => $"{column.Table}.{column.Column}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var actualGuidColumns = DeclaredBlobColumns(conn)
            .Where(value => value is not "audio_fingerprints.fingerprint" and not "account_passkeys.credential_id")
            // FTS5 creates implementation-owned shadow tables with BLOB pages.
            // Those columns are not application GUID contracts.
            .Where(value => !value.StartsWith("artwork_asset_search_", StringComparison.Ordinal))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedGuidColumns, actualGuidColumns);

        Assert.Equal("TEXT", ColumnType(conn, "works", "wikidata_qid"));
        Assert.Equal("TEXT", ColumnType(conn, "media_assets", "content_hash"));
        Assert.Equal("TEXT", ColumnType(conn, "bridge_ids", "provider_id"));
        Assert.Equal("TEXT", ColumnType(conn, "provider_response_cache", "provider_id"));
    }

    [Fact]
    public void NativeApplicationSeed_RunsOnceAndPreservesAdministrativeChangesAcrossRestart()
    {
        using var fixture = TempDatabase.Create();
        fixture.Database.InitializeSchema();
        fixture.Database.RunStartupChecks();

        using (var conn = fixture.Database.CreateConnection())
        {
            using (var command = conn.CreateCommand())
            {
                command.CommandText = """
                    UPDATE applications
                    SET is_enabled=0
                    WHERE id=X'00000000000000000000000000000004';
                    DELETE FROM application_permission_grants
                    WHERE application_id=X'00000000000000000000000000000004'
                      AND permission_id='downloads.write';
                    DELETE FROM application_client_bindings WHERE client_id='tuvima-automotive';
                    """;
                command.ExecuteNonQuery();
            }
        }

        fixture.Database.InitializeSchema();
        fixture.Database.RunStartupChecks();
        using (var conn = fixture.Database.CreateConnection())
        {
            Assert.Equal("0", Scalar(conn, "SELECT is_enabled FROM applications WHERE id=X'00000000000000000000000000000004';"));
            Assert.Equal("0", Scalar(conn, "SELECT COUNT(*) FROM application_permission_grants WHERE application_id=X'00000000000000000000000000000004' AND permission_id='downloads.write';"));
            Assert.Equal("0", Scalar(conn, "SELECT COUNT(*) FROM application_client_bindings WHERE client_id='tuvima-automotive';"));
            using var delete = conn.CreateCommand();
            delete.CommandText = "DELETE FROM applications WHERE id=X'00000000000000000000000000000004';";
            delete.ExecuteNonQuery();
        }

        fixture.Database.InitializeSchema();
        fixture.Database.RunStartupChecks();
        using var restarted = fixture.Database.CreateConnection();
        Assert.Equal("0", Scalar(restarted, "SELECT COUNT(*) FROM applications WHERE id=X'00000000000000000000000000000004';"));
    }

    [Fact]
    public void FreshDatabase_ViewOwnershipAndGalleryForeignKeysTargetCurrentTables()
    {
        using var fixture = TempDatabase.Create();
        fixture.Database.InitializeSchema();
        using var conn = fixture.Database.CreateConnection();

        (string Table, string Column, string TargetTable, string TargetColumn)[] expected =
        [
            ("view_personal_spaces", "owner_profile_id", "profiles", "id"),
            ("view_sources", "personal_space_id", "view_personal_spaces", "id"),
            ("view_devices", "personal_space_id", "view_personal_spaces", "id"),
            ("view_devices", "source_id", "view_sources", "id"),
            ("local_items", "personal_space_id", "view_personal_spaces", "id"),
            ("local_items", "owner_profile_id", "profiles", "id"),
            ("local_file_sources", "source_id", "view_sources", "id"),
            ("local_file_sources", "device_id", "view_devices", "id"),
            ("view_galleries", "owner_profile_id", "profiles", "id"),
            ("view_galleries", "personal_space_id", "view_personal_spaces", "id"),
            ("view_galleries", "cover_item_id", "local_items", "id"),
            ("view_galleries", "soundtrack_playlist_id", "collections", "id"),
            ("view_gallery_items", "gallery_id", "view_galleries", "id"),
            ("view_gallery_items", "item_id", "local_items", "id"),
            ("view_gallery_shares", "gallery_id", "view_galleries", "id"),
            ("view_gallery_shares", "profile_id", "profiles", "id"),
            ("view_source_policies", "source_id", "view_sources", "id"),
            ("view_folder_pins", "profile_id", "profiles", "id"),
            ("view_folder_pins", "source_id", "view_sources", "id"),
            ("view_folder_timeline_policies", "source_id", "view_sources", "id"),
            ("view_folder_timeline_policies", "updated_by_profile_id", "profiles", "id"),
            ("view_shared_assets", "item_id", "local_items", "id"),
            ("view_shared_assets", "original_profile_id", "profiles", "id"),
            ("view_shared_assets", "promoted_by_profile_id", "profiles", "id"),
            ("view_shared_contributions", "submitted_by_profile_id", "profiles", "id"),
            ("view_shared_contributions", "decided_by_profile_id", "profiles", "id"),
            ("view_shared_contribution_items", "contribution_id", "view_shared_contributions", "id"),
            ("view_shared_contribution_items", "item_id", "local_items", "id"),
            ("view_shared_contribution_items", "original_profile_id", "profiles", "id"),
            ("view_shared_contribution_events", "contribution_id", "view_shared_contributions", "id"),
            ("view_shared_contribution_events", "actor_profile_id", "profiles", "id"),
            ("view_shared_transfers", "item_id", "local_items", "id"),
            ("view_shared_transfers", "contribution_item_id", "view_shared_contribution_items", "id"),
            ("collection_view_sources", "collection_id", "collections", "id"),
            ("collection_view_sources", "owner_profile_id", "profiles", "id"),
            ("collection_view_sources", "gallery_id", "view_galleries", "id"),
            ("profile_view_policies", "profile_id", "profiles", "id"),
            ("profile_view_preferences", "profile_id", "profiles", "id"),
            ("profile_view_preferences", "last_scope_profile_id", "profiles", "id"),
        ];

        foreach (var foreignKey in expected)
        {
            using var command = conn.CreateCommand();
            command.CommandText = $"""
                SELECT COUNT(1)
                  FROM pragma_foreign_key_list('{foreignKey.Table}')
                 WHERE "from" = $column AND "table" = $targetTable AND "to" = $targetColumn;
                """;
            command.Parameters.AddWithValue("$column", foreignKey.Column);
            command.Parameters.AddWithValue("$targetTable", foreignKey.TargetTable);
            command.Parameters.AddWithValue("$targetColumn", foreignKey.TargetColumn);
            Assert.True(Convert.ToInt64(command.ExecuteScalar()) == 1L,
                $"Missing foreign key {foreignKey.Table}.{foreignKey.Column} -> {foreignKey.TargetTable}.{foreignKey.TargetColumn}.");
        }
    }

    [Fact]
    public async Task CanonicalValueRepository_KeepsMultiValuedKeysOutOfScalarTable()
    {
        using var fixture = TempDatabase.Create();
        fixture.Database.InitializeSchema();
        fixture.Database.RunStartupChecks();

        var repo = new CanonicalValueRepository(fixture.Database);
        var entityId = Guid.NewGuid();
        await repo.UpsertBatchAsync([
            new CanonicalValue
            {
                EntityId = entityId,
                Key = MetadataFieldConstants.Title,
                Value = "Dune",
                LastScoredAt = DateTimeOffset.UtcNow,
            },
            new CanonicalValue
            {
                EntityId = entityId,
                Key = MetadataFieldConstants.Author,
                Value = "Frank Herbert",
                LastScoredAt = DateTimeOffset.UtcNow,
            },
        ]);

        using var conn = fixture.Database.CreateConnection();
        Assert.Equal(1, Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM canonical_values;")));
        Assert.Equal("Dune", Scalar(conn, "SELECT value FROM canonical_values WHERE key = 'title';"));
        Assert.Equal("0", Scalar(conn, "SELECT COUNT(*) FROM canonical_values WHERE key = 'author';"));
    }

    [Fact]
    public async Task CanonicalValueArrayRepository_RejectsScalarAndMalformedEntriesBeforeWriting()
    {
        using var fixture = TempDatabase.Create();
        fixture.Database.InitializeSchema();
        fixture.Database.RunStartupChecks();

        var repo = new CanonicalValueArrayRepository(fixture.Database);
        var entityId = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() => repo.SetValuesAsync(
            entityId,
            MetadataFieldConstants.Title,
            [new CanonicalArrayEntry { Ordinal = 0, Value = "Dune" }]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repo.SetValuesAsync(
            entityId,
            MetadataFieldConstants.Genre,
            [new CanonicalArrayEntry { Ordinal = -1, Value = "Science fiction" }]));
        await Assert.ThrowsAsync<ArgumentException>(() => repo.SetValuesAsync(
            entityId,
            MetadataFieldConstants.Genre,
            [
                new CanonicalArrayEntry { Ordinal = 0, Value = "Science fiction" },
                new CanonicalArrayEntry { Ordinal = 0, Value = "Space opera" },
            ]));
        await Assert.ThrowsAsync<ArgumentException>(() => repo.SetValuesAsync(
            entityId,
            MetadataFieldConstants.Genre,
            [new CanonicalArrayEntry { Ordinal = 0, Value = " " }]));

        using var conn = fixture.Database.CreateConnection();
        Assert.Equal("0", Scalar(conn, "SELECT COUNT(*) FROM canonical_value_arrays;"));
    }

    [Fact]
    public void MetadataFieldConstants_ExposeAnImmutableMultiValueCatalog()
    {
        var property = typeof(MetadataFieldConstants).GetProperty(nameof(MetadataFieldConstants.MultiValuedKeys));

        Assert.NotNull(property);
        Assert.Equal(typeof(IReadOnlySet<string>), property.PropertyType);
        Assert.True(MetadataFieldConstants.MultiValuedKeys.Contains("GENRE"));
    }

    [Fact]
    public void StartupInitialization_IsIdempotent()
    {
        using var fixture = TempDatabase.Create();

        fixture.Database.InitializeSchema();
        fixture.Database.RunStartupChecks();
        fixture.Database.InitializeSchema();
        fixture.Database.RunStartupChecks();

        using var conn = fixture.Database.CreateConnection();
        Assert.True(TableExists(conn, "review_queue"));
        Assert.Equal("ok", Scalar(conn, "PRAGMA integrity_check;"));
    }

    [Fact]
    public void OpenedEmptyDatabase_InitializesAsCurrentSchema()
    {
        using var fixture = TempDatabase.Create();

        var startup = fixture.Database.Open();
        Assert.Equal(ConnectionState.Open, startup.State);

        fixture.Database.InitializeSchema();
        fixture.Database.RunStartupChecks();

        using var conn = fixture.Database.CreateConnection();
        Assert.Equal("guid-blob-v10-graph-timeline-lore", Scalar(conn, "SELECT value FROM storage_metadata WHERE key = 'storage_epoch';"));
        Assert.True(TableExists(conn, "review_queue"));
    }

    [Fact]
    public void LegacyDatabaseWithoutStorageEpoch_FailsStartupByDefault()
    {
        using var fixture = TempDatabase.Create();
        CreateLegacyTextGuidDatabase(fixture.Path);

        var ex = Assert.Throws<InvalidOperationException>(() => fixture.Database.InitializeSchema());

        Assert.Contains("Legacy databases are not migrated in place", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TUVIMA_STORAGE_RESET", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviousGuidBlobEpoch_FailsFastInsteadOfPartiallyApplyingViewSchema()
    {
        using var fixture = TempDatabase.Create();
        using (var conn = new SqliteConnection($"Data Source={fixture.Path};Pooling=False"))
        {
            conn.Open();
            using var command = conn.CreateCommand();
            command.CommandText = """
                CREATE TABLE storage_metadata (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO storage_metadata (key, value) VALUES ('storage_epoch', 'guid-blob-v4-client-auth');
                CREATE TABLE local_items (id BLOB NOT NULL PRIMARY KEY, library_id BLOB NOT NULL);
                """;
            command.ExecuteNonQuery();
        }

        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Database.InitializeSchema());
        Assert.Contains("guid-blob-v10-graph-timeline-lore", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not migrated in place", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreviousGraphTripleEpoch_FailsFastInsteadOfAcceptingLossyFactsSchema()
    {
        using var fixture = TempDatabase.Create();
        using (var conn = new SqliteConnection($"Data Source={fixture.Path};Pooling=False"))
        {
            conn.Open();
            using var command = conn.CreateCommand();
            command.CommandText = """
                CREATE TABLE storage_metadata (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO storage_metadata (key, value) VALUES ('storage_epoch', 'guid-blob-v7-access-authority');
                CREATE TABLE entity_relationships (
                    id BLOB NOT NULL PRIMARY KEY,
                    subject_qid TEXT NOT NULL,
                    relationship_type TEXT NOT NULL,
                    object_qid TEXT NOT NULL,
                    UNIQUE (subject_qid, relationship_type, object_qid));
                """;
            command.ExecuteNonQuery();
        }

        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Database.InitializeSchema());

        Assert.Contains("guid-blob-v10-graph-timeline-lore", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not migrated in place", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyDatabaseWithoutStorageEpoch_IsBackedUpAndRecreatedWhenResetIsExplicit()
    {
        using var fixture = TempDatabase.Create();
        CreateLegacyTextGuidDatabase(fixture.Path);

        var previous = Environment.GetEnvironmentVariable("TUVIMA_STORAGE_RESET");
        try
        {
            Environment.SetEnvironmentVariable("TUVIMA_STORAGE_RESET", "1");

            fixture.Database.InitializeSchema();
            fixture.Database.RunStartupChecks();
        }
        finally
        {
            Environment.SetEnvironmentVariable("TUVIMA_STORAGE_RESET", previous);
        }

        using var conn = fixture.Database.CreateConnection();
        Assert.Equal("guid-blob-v10-graph-timeline-lore", Scalar(conn, "SELECT value FROM storage_metadata WHERE key = 'storage_epoch';"));
        Assert.Equal("BLOB", ColumnType(conn, "metadata_providers", "id"));
        Assert.True(TableExists(conn, "review_queue"));

        var backupPattern = $"{Path.GetFileName(fixture.Path)}.legacy-text-guid.*.bak";
        var backupDir = Path.GetDirectoryName(fixture.Path)!;
        Assert.NotEmpty(Directory.GetFiles(backupDir, backupPattern));
    }

    [Fact]
    public void OpenPreviousEpochDatabase_IsReleasedBeforeExplicitReset()
    {
        using var fixture = TempDatabase.Create();
        using (var conn = new SqliteConnection($"Data Source={fixture.Path};Pooling=False"))
        {
            conn.Open();
            using var command = conn.CreateCommand();
            command.CommandText = """
                CREATE TABLE storage_metadata (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO storage_metadata (key, value) VALUES ('storage_epoch', 'guid-blob-v4-client-auth');
                CREATE TABLE user_states (user_id BLOB NOT NULL, asset_id BLOB NOT NULL, PRIMARY KEY (user_id, asset_id));
                """;
            command.ExecuteNonQuery();
        }

        fixture.Database.Open();
        var previous = Environment.GetEnvironmentVariable("TUVIMA_STORAGE_RESET");
        try
        {
            Environment.SetEnvironmentVariable("TUVIMA_STORAGE_RESET", "1");
            fixture.Database.InitializeSchema();
            fixture.Database.RunStartupChecks();
        }
        finally
        {
            Environment.SetEnvironmentVariable("TUVIMA_STORAGE_RESET", previous);
        }

        using var current = fixture.Database.CreateConnection();
        Assert.Equal("guid-blob-v10-graph-timeline-lore", Scalar(current, "SELECT value FROM storage_metadata WHERE key = 'storage_epoch';"));
        Assert.Equal("INTEGER", ColumnType(current, "user_states", "revision"));
    }

    [Fact]
    public void SchemaResource_DoesNotContainRetiredLegacyStorageTokens()
    {
        var root = FindRepoRoot();
        var schema = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Storage/Schema/schema.sql"));

        Assert.DoesNotContain("provider_" + "reg" + "istry", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hub_" + "items", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE TABLE IF NOT EXISTS " + "hubs", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("idx_" + "hubs", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("packed delimiter", schema, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShortLivedConnections_DoNotDisposeSharedStartupConnection()
    {
        using var fixture = TempDatabase.Create();
        fixture.Database.InitializeSchema();
        fixture.Database.RunStartupChecks();

        var startup = fixture.Database.Open();
        using (var transient = fixture.Database.CreateConnection())
        {
            Assert.NotSame(startup, transient);
            Assert.Equal(System.Data.ConnectionState.Open, transient.State);
        }

        Assert.Equal(System.Data.ConnectionState.Open, startup.State);
        using var cmd = startup.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
        Assert.True(Convert.ToInt32(cmd.ExecuteScalar()) > 0);
    }

    [Fact]
    public void Open_ReusesSharedStartupConnection()
    {
        using var fixture = TempDatabase.Create();

        var first = fixture.Database.Open();
        var second = fixture.Database.Open();

        Assert.Same(first, second);
        Assert.Equal(ConnectionState.Open, first.State);
    }

    [Fact]
    public void Dispose_ClosesSharedStartupConnection()
    {
        var fixture = TempDatabase.Create();
        var startup = fixture.Database.Open();

        fixture.Dispose();

        Assert.Equal(ConnectionState.Closed, startup.State);
    }

    [Fact]
    public void Vacuum_RemainsCallableAfterStartup()
    {
        using var fixture = TempDatabase.Create();
        fixture.Database.InitializeSchema();
        fixture.Database.RunStartupChecks();

        fixture.Database.Vacuum();

        using var conn = fixture.Database.CreateConnection();
        Assert.Equal("ok", Scalar(conn, "PRAGMA integrity_check;"));
    }

    [Fact]
    public async Task WriteLock_SerializesAndCanBeReacquired()
    {
        using var fixture = TempDatabase.Create();

        await fixture.Database.AcquireWriteLockAsync();
        var waiterStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiterAcquired = false;

        var waiter = Task.Run(async () =>
        {
            waiterStarted.SetResult();
            await fixture.Database.AcquireWriteLockAsync();
            waiterAcquired = true;
            fixture.Database.ReleaseWriteLock();
        });

        await waiterStarted.Task;
        await Task.Delay(50);
        Assert.False(waiterAcquired);

        fixture.Database.ReleaseWriteLock();
        await waiter;

        await fixture.Database.AcquireWriteLockAsync();
        fixture.Database.ReleaseWriteLock();
    }

    private static bool TableExists(SqliteConnection conn, string name) =>
        Exists(conn, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;", name);

    private static bool IndexExists(SqliteConnection conn, string name) =>
        Exists(conn, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name;", name);

    private static bool Exists(SqliteConnection conn, string sql, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$name", name);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private static string Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? string.Empty;
    }

    private static string ColumnType(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info([{table}]);";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return reader.GetString(2);
            }
        }

        throw new InvalidOperationException($"Column {table}.{column} was not found.");
    }

    private static IReadOnlyList<string> DeclaredBlobColumns(SqliteConnection conn)
    {
        var tables = new List<string>();
        using (var tableCmd = conn.CreateCommand())
        {
            tableCmd.CommandText = """
                SELECT name
                FROM sqlite_master
                WHERE type = 'table'
                  AND name NOT LIKE 'sqlite_%'
                  AND name NOT LIKE 'search_index_%'
                  AND (name NOT LIKE 'local_item_search_%' OR name = 'local_item_search_keys')
                ORDER BY name;
                """;
            using var tableReader = tableCmd.ExecuteReader();
            while (tableReader.Read())
            {
                tables.Add(tableReader.GetString(0));
            }
        }

        var result = new List<string>();
        foreach (var table in tables)
        {
            using var columnCmd = conn.CreateCommand();
            columnCmd.CommandText = $"PRAGMA table_info([{table}]);";
            using var columnReader = columnCmd.ExecuteReader();
            while (columnReader.Read())
            {
                if (string.Equals(columnReader.GetString(2), "BLOB", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add($"{table}.{columnReader.GetString(1)}");
                }
            }
        }

        return result;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private static void CreateLegacyTextGuidDatabase(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE metadata_providers (
                id TEXT NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                version TEXT NOT NULL
            );
            INSERT INTO metadata_providers (id, name, version)
            VALUES ('00000000-0000-0000-0000-000000000001', 'legacy', '1.0');
            """;
        cmd.ExecuteNonQuery();
    }

    private sealed class TempDatabase : IDisposable
    {
        private TempDatabase(string path)
        {
            Path = path;
            Database = new DatabaseConnection(path);
        }

        public string Path { get; }

        public DatabaseConnection Database { get; }

        public static TempDatabase Create() =>
            new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tuvima_startup_{Guid.NewGuid():N}.db"));

        public void Dispose()
        {
            Database.Dispose();
            DatabaseStartupSafetyTests.TryDelete(Path);
            DatabaseStartupSafetyTests.TryDelete($"{Path}-wal");
            DatabaseStartupSafetyTests.TryDelete($"{Path}-shm");
            foreach (var backup in Directory.GetFiles(
                         System.IO.Path.GetDirectoryName(Path)!,
                         $"{System.IO.Path.GetFileName(Path)}.legacy-text-guid.*.bak"))
            {
                DatabaseStartupSafetyTests.TryDelete(backup);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Test cleanup is best effort; failure should not hide the test assertion result.
        }
    }
}
