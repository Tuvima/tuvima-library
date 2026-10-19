using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// Read-only facts for the delete-from-library preview: the requested titles with all their files (including titles with
/// none), the hand-picked collections they sit in, the profiles with personal state on them, and whether processing is running.
/// Every query uses a short-lived connection and guid-blob parameters.
/// </summary>
public sealed class LibraryItemDeletionRepository(IDatabaseConnection db) : ILibraryItemDeletionRepository
{
    // Jobs in these states are finished or waiting for a person, so they are not "processing". The same set the active-job
    // index (ux_identity_jobs_entity_pass_active) and IdentityJobRepository treat as at rest.
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
        var works = connection.Query<WorkRow>(new CommandDefinition("""
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
            WHERE w.id IN @ids;
            """,
            new { ids },
            cancellationToken: ct)).ToList();

        // LEFT joins: a title with no edition or no file must still come back, with an empty file list.
        var assetRows = connection.Query<AssetRow>(new CommandDefinition("""
            -- readiness: lookup of the requested works by id; a title that is not ready can still be deleted
            SELECT w.id AS WorkId,
                   ma.id AS AssetId,
                   ma.file_path_root AS FilePath,
                   ma.content_hash AS ContentHash
            FROM works w
            LEFT JOIN editions e ON e.work_id = w.id
            LEFT JOIN media_assets ma ON ma.edition_id = e.id
            WHERE w.id IN @ids
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
                   ) AS TotalItems
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
                row.ItemsInRequest >= row.TotalItems))
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
    }
}
