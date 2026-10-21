using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Storage;

/// <summary>
/// The facts for the delete-from-library preview: the requested titles with all their files (including titles with none), the
/// hand-picked collections they sit in, the profiles with personal state on them, and whether processing is running. Also the
/// one transaction that removes the titles' records (<see cref="DeleteWorksAsync"/>). Reads use a short-lived connection and
/// guid-blob parameters.
/// </summary>
/// <param name="leaseWaitLimit">How long <see cref="CancelProcessingAsync"/> waits for a running job to let go (default 30 seconds).</param>
/// <param name="leasePollInterval">How often it looks (default 400 ms).</param>
public sealed class LibraryItemDeletionRepository(
    IDatabaseConnection db,
    ILogger<LibraryItemDeletionRepository>? logger = null,
    TimeSpan? leaseWaitLimit = null,
    TimeSpan? leasePollInterval = null) : ILibraryItemDeletionRepository
{
    // Jobs in these states are finished or waiting for a person, so they are not "processing". The same set the active-job
    // index (ux_identity_jobs_entity_pass_active) and IdentityJobRepository treat as at rest.
    private const string CancelledReason = "Removed from the library.";

    private static readonly string[] RestingJobStates =
    [
        nameof(IdentityJobState.Ready),
        nameof(IdentityJobState.ReadyWithoutUniverse),
        nameof(IdentityJobState.Failed),
        nameof(IdentityJobState.RetailNoMatch),
        nameof(IdentityJobState.QidNoMatch),
        nameof(IdentityJobState.QidNeedsReview),
    ];

    public Task<IReadOnlyList<LibraryItemDeletionWork>> GetWorksAsync(
        IReadOnlyList<Guid> workIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workIds);
        ct.ThrowIfCancellationRequested();
        if (workIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<LibraryItemDeletionWork>>([]);
        }

        using var connection = db.CreateConnection();
        var ids = ToBlobs(workIds);

        // The title is the canonical one the other read paths show: the work's own value first, then its files'.
        // A work with any file in a Vault library is not a title here (Videos spec D17): asking about one answers "unknown id".
        var works = connection.Query<WorkRow>(new CommandDefinition($"""
            -- readiness: lookup of the requested works by id; a title that is not ready can still be deleted
            SELECT w.id AS WorkId,
                   w.work_kind AS WorkKind,
                   w.is_catalog_only AS IsCatalogOnly,
                   w.ownership AS Ownership,
                   w.media_type AS MediaType,
                   COALESCE(
                       (
                           SELECT cv.value
                           FROM canonical_values cv
                           WHERE cv.entity_id = w.id
                             AND cv.key IN ('episode_title', 'title', 'track_title', 'show_name', 'album')
                             AND NULLIF(cv.value, '') IS NOT NULL
                           ORDER BY CASE cv.key
                               WHEN 'episode_title' THEN 0
                               WHEN 'track_title' THEN 1
                               WHEN 'title' THEN 2
                               WHEN 'show_name' THEN 3
                               ELSE 4
                           END
                           LIMIT 1
                       ),
                       (
                           SELECT cv.value
                           FROM canonical_values cv
                           WHERE cv.entity_id IN (
                                     SELECT ma_title.id
                                     FROM editions e_title
                                     INNER JOIN media_assets ma_title ON ma_title.edition_id = e_title.id
                                     WHERE e_title.work_id = w.id)
                             AND cv.key IN ('episode_title', 'title', 'track_title', 'show_name', 'album')
                             AND NULLIF(cv.value, '') IS NOT NULL
                           ORDER BY CASE cv.key
                               WHEN 'episode_title' THEN 0
                               WHEN 'track_title' THEN 1
                               WHEN 'title' THEN 2
                               WHEN 'show_name' THEN 3
                               ELSE 4
                           END
                           LIMIT 1
                       )
                   ) AS Title
            FROM works w
            WHERE w.id IN @ids
              AND {VaultSql.NotVaultEntity("w.id")};
            """,
            new { ids },
            cancellationToken: ct)).ToList();

        // LEFT joins: a title with no edition or no file must still come back, with an empty file list.
        var assetRows = connection.Query<AssetRow>(new CommandDefinition($"""
            -- readiness: lookup of the requested works by id; a title that is not ready can still be deleted
            SELECT w.id AS WorkId,
                   ma.id AS AssetId,
                   ma.file_path_root AS FilePath,
                   ma.content_hash AS ContentHash
            FROM works w
            LEFT JOIN editions e ON e.work_id = w.id
            LEFT JOIN media_assets ma ON ma.edition_id = e.id
            WHERE w.id IN @ids
              AND {VaultSql.NotVaultEntity("w.id")}
            ORDER BY w.id, ma.file_path_root;
            """,
            new { ids },
            cancellationToken: ct)).ToList();

        var assetsByWork = assetRows
            .Where(row => row.AssetId.HasValue)
            .GroupBy(row => row.WorkId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<LibraryItemDeletionAsset>)group
                    .Select(row => new LibraryItemDeletionAsset(row.AssetId!.Value, row.FilePath ?? string.Empty, row.ContentHash ?? string.Empty))
                    .ToList());

        IReadOnlyList<LibraryItemDeletionWork> result = works
            .Select(row => new LibraryItemDeletionWork(
                row.WorkId,
                row.WorkKind ?? string.Empty,
                row.IsCatalogOnly != 0
                    || string.Equals(row.Ownership, "Unowned", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(row.WorkKind, "catalog", StringComparison.OrdinalIgnoreCase),
                row.MediaType ?? string.Empty,
                string.IsNullOrWhiteSpace(row.Title) ? string.Empty : row.Title.Trim(),
                assetsByWork.TryGetValue(row.WorkId, out var assets) ? assets : []))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<LibraryItemDeletionRelatedFacts> GetRelatedFactsAsync(
        IReadOnlyList<Guid> workIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workIds);
        ct.ThrowIfCancellationRequested();
        if (workIds.Count == 0)
        {
            return Task.FromResult(new LibraryItemDeletionRelatedFacts([], 0, false));
        }

        using var connection = db.CreateConnection();
        var ids = ToBlobs(workIds);

        // Only hand-picked collections count; Series and Universe links (works.collection_id) and Smart collections are not memberships.
        var collectionRows = connection.Query<CollectionRow>(new CommandDefinition("""
            SELECT c.id AS CollectionId,
                   COALESCE(NULLIF(TRIM(c.display_name), ''), 'Untitled collection') AS Name,
                   (
                       SELECT COUNT(DISTINCT ci.work_id)
                       FROM collection_items ci
                       WHERE ci.collection_id = c.id AND ci.work_id IN @ids
                   ) AS ItemsInRequest,
                   (
                       SELECT COUNT(DISTINCT ci.work_id)
                       FROM collection_items ci
                       WHERE ci.collection_id = c.id
                   ) AS TotalItems,
                   c.collection_type AS CollectionType,
                   c.resolution AS Resolution,
                   EXISTS (SELECT 1 FROM collection_view_sources s WHERE s.collection_id = c.id) AS HasViewSources
            FROM collections c
            WHERE c.membership_mode = 'Manual'
              AND EXISTS (
                  SELECT 1 FROM collection_items ci
                  WHERE ci.collection_id = c.id AND ci.work_id IN @ids);
            """,
            new { ids },
            cancellationToken: ct)).ToList();

        IReadOnlyList<LibraryItemDeletionCollection> collections = collectionRows
            .Select(row => new LibraryItemDeletionCollection(
                row.CollectionId,
                row.Name,
                row.ItemsInRequest,
                // The same rule CuratedCollectionCleanup.DeleteEmpty applies once the works are gone: only a curated collection
                // (Custom, stored members) with no personal-media source and no member left. Playlists, playlist folders and system
                // groupings are hand-picked too, so they are listed, but they are never deleted by this.
                row.ItemsInRequest >= row.TotalItems
                    && string.Equals(row.CollectionType, "Custom", StringComparison.Ordinal)
                    && string.Equals(row.Resolution, "materialized", StringComparison.Ordinal)
                    && row.HasViewSources == 0))
            .OrderBy(collection => collection.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(collection => collection.Name, StringComparer.Ordinal)
            .ThenBy(collection => collection.CollectionId)
            .ToList();

        // Every table that holds one person's state about a title or one of its files. Binary profile ids are projected to
        // the same lowercase 32-character form as the text ids the reader tables store, so one DISTINCT counts each person once.
        // personal_activity is deliberately not counted: it is the person's own record, not progress on the title.
        var profiles = connection.ExecuteScalar<int>(new CommandDefinition("""
            WITH scoped_assets AS (
                SELECT ma.id AS id
                FROM editions e
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                WHERE e.work_id IN @ids
            ),
            holders AS (
                SELECT lower(hex(user_id)) AS profile FROM user_states WHERE asset_id IN (SELECT id FROM scoped_assets)
                UNION
                SELECT lower(hex(profile_id)) FROM consumption_history WHERE asset_id IN (SELECT id FROM scoped_assets)
                UNION
                SELECT lower(hex(profile_id)) FROM profile_work_preferences WHERE work_id IN @ids
                UNION
                SELECT lower(hex(profile_id)) FROM audiobook_listen_history
                    WHERE work_id IN @ids OR asset_id IN (SELECT id FROM scoped_assets)
                UNION
                SELECT lower(hex(profile_id)) FROM audiobook_bookmarks
                    WHERE work_id IN @ids OR asset_id IN (SELECT id FROM scoped_assets)
                UNION
                SELECT lower(hex(profile_id)) FROM bookmark_packs
                    WHERE work_id IN @ids OR asset_id IN (SELECT id FROM scoped_assets)
                UNION
                SELECT lower(hex(profile_id)) FROM music_play_stats WHERE work_id IN @ids
                UNION
                SELECT lower(replace(user_id, '-', '')) FROM reader_bookmarks WHERE asset_id IN (SELECT id FROM scoped_assets)
                UNION
                SELECT lower(replace(user_id, '-', '')) FROM reader_highlights WHERE asset_id IN (SELECT id FROM scoped_assets)
                UNION
                SELECT lower(replace(user_id, '-', '')) FROM reader_statistics WHERE asset_id IN (SELECT id FROM scoped_assets)
                UNION
                SELECT lower(hex(profile_id)) FROM player_queue_items
                    WHERE work_id IN @ids OR asset_id IN (SELECT id FROM scoped_assets)
                UNION
                SELECT lower(hex(profile_id)) FROM profile_saved_items WHERE entity_id IN @ids
                UNION
                SELECT lower(hex(profile_id)) FROM profile_reactions WHERE entity_id IN @ids
            )
            SELECT COUNT(*) FROM holders WHERE profile IS NOT NULL AND profile <> '';
            """,
            new { ids },
            cancellationToken: ct));

        var processing = connection.ExecuteScalar<int>(new CommandDefinition("""
            SELECT EXISTS (
                SELECT 1
                FROM identity_jobs j
                WHERE j.state NOT IN @resting
                  AND (j.entity_id IN @ids
                       OR j.entity_id IN (
                           SELECT ma.id
                           FROM editions e
                           INNER JOIN media_assets ma ON ma.edition_id = e.id
                           WHERE e.work_id IN @ids)));
            """,
            new { ids, resting = RestingJobStates },
            cancellationToken: ct)) != 0;

        return Task.FromResult(new LibraryItemDeletionRelatedFacts(collections, profiles, processing));
    }

    public Task<IReadOnlySet<string>> FindPathsUsedOutsideRequestAsync(
        IReadOnlyList<Guid> workIds,
        IReadOnlyCollection<string> paths,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workIds);
        ArgumentNullException.ThrowIfNull(paths);
        ct.ThrowIfCancellationRequested();

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0)
        {
            return Task.FromResult<IReadOnlySet<string>>(found);
        }

        using var connection = db.CreateConnection();
        var ids = ToBlobs(workIds);
        foreach (var batch in paths.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            var stored = connection.Query<string>(new CommandDefinition("""
                SELECT ma.file_path_root
                FROM media_assets ma
                INNER JOIN editions e ON e.id = ma.edition_id
                WHERE ma.file_path_root COLLATE NOCASE IN @paths
                  AND e.work_id NOT IN @ids;
                """,
                new { paths = batch, ids },
                cancellationToken: ct));
            foreach (var path in stored)
            {
                found.Add(path);
            }
        }

        return Task.FromResult<IReadOnlySet<string>>(found);
    }

    public async Task CancelProcessingAsync(
        IReadOnlyList<Guid> workIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workIds);
        ct.ThrowIfCancellationRequested();
        if (workIds.Count == 0)
        {
            return;
        }

        var ids = ToBlobs(workIds);
        var limit = leaseWaitLimit ?? TimeSpan.FromSeconds(30);
        var interval = leasePollInterval ?? TimeSpan.FromMilliseconds(400);
        var deadline = DateTimeOffset.UtcNow + limit;

        // Mark the jobs failed so nothing new can lease them. A job a worker is running right now keeps its lease, so the lease still
        // shows who is on it; everything else is released. A running job cannot be interrupted from here, so it is waited for. The
        // marking is repeated while waiting, because a worker that finishes moves its job on to the next state, and it reports how
        // many live leases it found in the same write that marked them. The wait ends only when a marking finds none: that marking
        // is the last word, because the marked jobs are failed and cannot be leased again.
        while (true)
        {
            var leased = await MarkJobsFailedAsync(ids, ct).ConfigureAwait(false);
            if (leased == 0)
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                var running = FindRunningJobs(ids);
                logger?.LogWarning(
                    "Deleting from the library without waiting any longer for {Count} running job(s) to finish: {JobIds}",
                    running.Count,
                    string.Join(", ", running.Select(id => id.ToString("D"))));
                return;
            }

            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Marks the jobs failed and returns how many of them had a live lease at that moment (counted in the same write).</summary>
    private Task<int> MarkJobsFailedAsync(byte[][] ids, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        // A short write each time, so a worker can take the write lock between looks and let go of its job.
        return db.ExecuteWriteAsync((connection, transaction, _) =>
        {
            var leased = connection.ExecuteScalar<int>("""
                SELECT COUNT(*)
                FROM identity_jobs
                WHERE lease_owner IS NOT NULL AND lease_expires_at > @now
                  AND (entity_id IN @ids
                       OR entity_id IN (
                           SELECT ma.id
                           FROM editions e
                           INNER JOIN media_assets ma ON ma.edition_id = e.id
                           WHERE e.work_id IN @ids));
                """,
                new { now, ids },
                transaction);
            connection.Execute("""
                UPDATE identity_jobs
                SET state = @failed,
                    last_error = @reason,
                    lease_owner = CASE WHEN lease_owner IS NOT NULL AND lease_expires_at > @now THEN lease_owner ELSE NULL END,
                    lease_expires_at = CASE WHEN lease_owner IS NOT NULL AND lease_expires_at > @now THEN lease_expires_at ELSE NULL END,
                    next_retry_at = NULL,
                    updated_at = @now
                WHERE (state NOT IN @resting OR (lease_owner IS NOT NULL AND lease_expires_at > @now))
                  AND (entity_id IN @ids
                       OR entity_id IN (
                           SELECT ma.id
                           FROM editions e
                           INNER JOIN media_assets ma ON ma.edition_id = e.id
                           WHERE e.work_id IN @ids));
                """,
                new { failed = nameof(IdentityJobState.Failed), reason = CancelledReason, now, resting = RestingJobStates, ids },
                transaction);
            return leased;
        }, ct);
    }

    private List<Guid> FindRunningJobs(byte[][] ids)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = db.CreateConnection();
        return connection.Query<Guid>("""
            SELECT j.id
            FROM identity_jobs j
            WHERE j.lease_owner IS NOT NULL
              AND j.lease_expires_at > @now
              AND (j.entity_id IN @ids
                   OR j.entity_id IN (
                       SELECT ma.id
                       FROM editions e
                       INNER JOIN media_assets ma ON ma.edition_id = e.id
                       WHERE e.work_id IN @ids));
            """,
            new { now, ids }).ToList();
    }

    public async Task<LibraryItemDeletionDataResult> DeleteWorksAsync(
        IReadOnlyList<Guid> workIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workIds);
        ct.ThrowIfCancellationRequested();
        var distinct = workIds.Distinct().ToList();
        if (distinct.Count == 0)
        {
            return new LibraryItemDeletionDataResult([], [], [], [], []);
        }

        return await db.ExecuteWriteAsync(
            (connection, transaction, _) => DeleteWorksCore(connection, transaction, distinct),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The removal itself, on the caller's connection and transaction, so another write (a move that merges a title into another)
    /// can remove titles inside its own transaction. <paramref name="distinct"/> must be non-empty.
    /// </summary>
    internal static LibraryItemDeletionDataResult DeleteWorksCore(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        IReadOnlyList<Guid> distinct)
    {
        // The ids of everything that goes live in connection-private scratch tables for the length of this transaction, so no
        // statement carries a long parameter list. They are emptied before the commit (and a rollback takes the rows away too).
        connection.Execute("""
            CREATE TEMP TABLE IF NOT EXISTS deletion_works (id BLOB NOT NULL PRIMARY KEY);
            CREATE TEMP TABLE IF NOT EXISTS deletion_editions (id BLOB NOT NULL PRIMARY KEY);
            CREATE TEMP TABLE IF NOT EXISTS deletion_assets (id BLOB NOT NULL PRIMARY KEY);
            CREATE TEMP TABLE IF NOT EXISTS deletion_ids (id BLOB NOT NULL PRIMARY KEY);
            CREATE TEMP TABLE IF NOT EXISTS deletion_artwork (id BLOB NOT NULL PRIMARY KEY);
            """, transaction: transaction);
        ClearScratchTables(connection, transaction);
        connection.Execute(
            "INSERT OR IGNORE INTO deletion_works (id) VALUES (@id);",
            distinct.Select(id => new { id }),
            transaction);
        connection.Execute("""
            INSERT OR IGNORE INTO deletion_editions (id) SELECT e.id FROM editions e WHERE e.work_id IN (SELECT id FROM deletion_works);
            INSERT OR IGNORE INTO deletion_assets (id) SELECT ma.id FROM media_assets ma WHERE ma.edition_id IN (SELECT id FROM deletion_editions);
            INSERT OR IGNORE INTO deletion_ids (id) SELECT id FROM deletion_works;
            INSERT OR IGNORE INTO deletion_ids (id) SELECT id FROM deletion_editions;
            INSERT OR IGNORE INTO deletion_ids (id) SELECT id FROM deletion_assets;
            """, transaction: transaction);

        var editionIds = connection.Query<Guid>("SELECT id FROM deletion_editions;", transaction: transaction).ToList();
        var assetIds = connection.Query<Guid>("SELECT id FROM deletion_assets;", transaction: transaction).ToList();

        // Artwork only these titles used: every link to the picture comes from something being deleted. Its row cannot go until
        // its links do (RESTRICT), so the list is made first.
        connection.Execute("""
            INSERT OR IGNORE INTO deletion_artwork (id)
            SELECT DISTINCT link.artwork_asset_id
            FROM entity_artwork_links link
            WHERE link.entity_id IN (SELECT id FROM deletion_ids)
              AND NOT EXISTS (
                  SELECT 1 FROM entity_artwork_links other
                  WHERE other.artwork_asset_id = link.artwork_asset_id
                    AND other.entity_id NOT IN (SELECT id FROM deletion_ids));
            """, transaction: transaction);

        // Files Tuvima wrote that nothing will point at once the rows are gone. The caller removes them after the commit.
        var ownedFiles = new List<string>();
        ownedFiles.AddRange(connection.Query<string>("""
            SELECT p FROM (
                SELECT original_path AS p FROM artwork_assets WHERE id IN (SELECT id FROM deletion_artwork)
                UNION SELECT small_path FROM artwork_assets WHERE id IN (SELECT id FROM deletion_artwork)
                UNION SELECT medium_path FROM artwork_assets WHERE id IN (SELECT id FROM deletion_artwork)
                UNION SELECT large_path FROM artwork_assets WHERE id IN (SELECT id FROM deletion_artwork)
                UNION SELECT local_image_path FROM entity_assets WHERE entity_id IN (SELECT id FROM deletion_ids)
                UNION SELECT local_image_path_s FROM entity_assets WHERE entity_id IN (SELECT id FROM deletion_ids)
                UNION SELECT local_image_path_m FROM entity_assets WHERE entity_id IN (SELECT id FROM deletion_ids)
                UNION SELECT local_image_path_l FROM entity_assets WHERE entity_id IN (SELECT id FROM deletion_ids)
                UNION SELECT local_path FROM text_tracks WHERE asset_id IN (SELECT id FROM deletion_assets)
                UNION SELECT sidecar_path FROM text_tracks WHERE asset_id IN (SELECT id FROM deletion_assets)
            ) WHERE p IS NOT NULL AND p <> '';
            """, transaction: transaction));

        // Records that point at a title, edition or file by id with no foreign key to cascade from.
        foreach (var sql in IdKeyedDeletes)
        {
            connection.Execute(sql, transaction: transaction);
        }

        // Pictures only these titles used, and their search entries.
        connection.Execute("""
            DELETE FROM artwork_asset_search WHERE artwork_asset_id IN (SELECT id FROM deletion_artwork);
            DELETE FROM artwork_assets WHERE id IN (SELECT id FROM deletion_artwork);
            """, transaction: transaction);

        // A chosen series entry that pointed at a deleted title keeps its place, unlinked.
        connection.Execute(
            "UPDATE series_manifest_items SET linked_work_id = NULL WHERE linked_work_id IN (SELECT id FROM deletion_works);",
            transaction: transaction);

        // Everything else that belongs to the titles (progress, bookmarks, highlights, queue entries, play counts, telemetry
        // links, text tracks, derived files, coverage) hangs off the asset or the Work with ON DELETE CASCADE or SET NULL.
        connection.Execute("""
            -- readiness: removes the requested works by id; a title that is not ready can still be deleted
            DELETE FROM media_assets WHERE id IN (SELECT id FROM deletion_assets);
            DELETE FROM editions WHERE id IN (SELECT id FROM deletion_editions);
            DELETE FROM works WHERE id IN (SELECT id FROM deletion_works);
            """, transaction: transaction);

        // A hand-picked collection whose last title just left is deleted too (it writes its own activity entry).
        var collections = CuratedCollectionCleanup.DeleteEmpty(connection, transaction);

        // A file is Tuvima's to remove only when no remaining row still points at it: artwork rows are content-addressed, so another
        // title's picture can be served by the very file a deleted title's entity asset names.
        var stillReferenced = AssetFileReferences.LoadReferencedPaths(connection, transaction);
        var deletable = ownedFiles
            .Where(path => !stillReferenced.Contains(NormalizeForComparison(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        ClearScratchTables(connection, transaction);
        return new LibraryItemDeletionDataResult(editionIds, assetIds, collections, deletable, stillReferenced);
    }

    private static string NormalizeForComparison(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Compared as written, the same way AssetFileReferences keeps a path it cannot normalise.
            return path;
        }
    }

    private static void ClearScratchTables(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) =>
        connection.Execute("""
            DELETE FROM deletion_works;
            DELETE FROM deletion_editions;
            DELETE FROM deletion_assets;
            DELETE FROM deletion_ids;
            DELETE FROM deletion_artwork;
            """, transaction: transaction);

    // personal_activity and personal_activity_artwork are deliberately absent: a person's own history keeps its snapshot of the
    // title and survives deletion. deletion_ids holds the Work, edition and asset ids; deletion_works and deletion_assets the subsets.
    private static readonly string[] IdKeyedDeletes =
    [
        "DELETE FROM canonical_values WHERE entity_id IN (SELECT id FROM deletion_ids);",
        "DELETE FROM canonical_value_arrays WHERE entity_id IN (SELECT id FROM deletion_ids);",
        "DELETE FROM metadata_claims WHERE entity_id IN (SELECT id FROM deletion_ids);",
        "DELETE FROM bridge_ids WHERE entity_id IN (SELECT id FROM deletion_ids);",
        "DELETE FROM ai_feature_artifacts WHERE entity_id IN (SELECT id FROM deletion_ids);",
        "DELETE FROM enrichment_refresh_schedule WHERE entity_id IN (SELECT id FROM deletion_ids);",
        "DELETE FROM entity_capability_states WHERE entity_id IN (SELECT id FROM deletion_ids);",
        "DELETE FROM entity_field_changes WHERE entity_id IN (SELECT id FROM deletion_ids);",
        "DELETE FROM entity_events WHERE entity_id IN (SELECT id FROM deletion_ids);",
        "DELETE FROM pending_person_signals WHERE entity_id IN (SELECT id FROM deletion_ids);",
        "DELETE FROM search_results_cache WHERE entity_id IN (SELECT id FROM deletion_ids);",
        "DELETE FROM transaction_log WHERE entity_id IN (SELECT id FROM deletion_ids);",
        "DELETE FROM media_operation_events WHERE entity_id IN (SELECT id FROM deletion_ids);",
        "DELETE FROM media_operations WHERE entity_id IN (SELECT id FROM deletion_ids);",
        "DELETE FROM deferred_enrichment_queue WHERE entity_id IN (SELECT id FROM deletion_ids);",
        "DELETE FROM artwork_asset_context WHERE entity_id IN (SELECT id FROM deletion_ids);",
        "DELETE FROM review_queue WHERE entity_id IN (SELECT id FROM deletion_ids);",
        "DELETE FROM identity_jobs WHERE entity_id IN (SELECT id FROM deletion_ids);",
        "DELETE FROM entity_assets WHERE entity_id IN (SELECT id FROM deletion_ids);",
        "DELETE FROM entity_artwork_links WHERE entity_id IN (SELECT id FROM deletion_ids);",
        "DELETE FROM ingestion_log WHERE media_asset_id IN (SELECT id FROM deletion_assets);",
        "DELETE FROM search_index WHERE entity_id IN (SELECT id FROM deletion_works);",
        "DELETE FROM profile_saved_items WHERE entity_id IN (SELECT id FROM deletion_ids);",
        "DELETE FROM profile_reactions WHERE entity_id IN (SELECT id FROM deletion_ids);",
        "DELETE FROM personal_status_commands WHERE target_id IN (SELECT id FROM deletion_ids);",
        // The title's own History (D9); the single kept entry is written after this.
        "DELETE FROM system_activity WHERE entity_id IN (SELECT id FROM deletion_ids);",
        // Declared without ON DELETE, so it would block the asset delete.
        "DELETE FROM audio_fingerprints WHERE asset_id IN (SELECT id FROM deletion_assets);",
        // Media editor receipts for moves and artwork choices that involve a deleted title or file. A commit's items go with it.
        "DELETE FROM media_editor_commits WHERE asset_id IN (SELECT id FROM deletion_assets) OR source_work_id IN (SELECT id FROM deletion_works) OR target_work_id IN (SELECT id FROM deletion_works);",
        """
        DELETE FROM media_editor_commit_items
        WHERE asset_id IN (SELECT id FROM deletion_assets)
           OR source_edition_id IN (SELECT id FROM deletion_editions)
           OR source_work_id IN (SELECT id FROM deletion_works)
           OR target_work_id IN (SELECT id FROM deletion_works)
           OR source_season_work_id IN (SELECT id FROM deletion_works)
           OR target_season_work_id IN (SELECT id FROM deletion_works);
        """,
        "DELETE FROM media_editor_commit_artwork WHERE owner_work_id IN (SELECT id FROM deletion_works) OR artwork_asset_id IN (SELECT id FROM deletion_artwork);",
        "DELETE FROM media_editor_edition_artwork_commits WHERE edition_id IN (SELECT id FROM deletion_editions) OR work_id IN (SELECT id FROM deletion_works) OR artwork_asset_id IN (SELECT id FROM deletion_artwork);",
        "DELETE FROM media_editor_preferred_artwork_commits WHERE owner_work_id IN (SELECT id FROM deletion_works) OR artwork_asset_id IN (SELECT id FROM deletion_artwork);",
        "DELETE FROM media_editor_music_pairing_commit_items WHERE asset_id IN (SELECT id FROM deletion_assets) OR edition_id IN (SELECT id FROM deletion_editions) OR work_id IN (SELECT id FROM deletion_works);",
    ];

    private static byte[][] ToBlobs(IReadOnlyList<Guid> ids) => [.. ids.Distinct().Select(GuidSql.ToBlob)];

    private sealed class WorkRow
    {
        public Guid WorkId { get; init; }
        public string? WorkKind { get; init; }
        public long IsCatalogOnly { get; init; }
        public string? Ownership { get; init; }
        public string? MediaType { get; init; }
        public string? Title { get; init; }
    }

    private sealed class AssetRow
    {
        public Guid WorkId { get; init; }
        public Guid? AssetId { get; init; }
        public string? FilePath { get; init; }
        public string? ContentHash { get; init; }
    }

    private sealed class CollectionRow
    {
        public Guid CollectionId { get; init; }
        public string Name { get; init; } = string.Empty;
        public int ItemsInRequest { get; init; }
        public int TotalItems { get; init; }
        public string? CollectionType { get; init; }
        public string? Resolution { get; init; }
        public long HasViewSources { get; init; }
    }
}
