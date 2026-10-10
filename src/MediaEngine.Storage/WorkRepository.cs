using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IWorkRepository"/>.
///
/// Phase 3 (M-082) replaced the legacy title+author dedup with parent/child
/// resolution. The repository now exposes:
///
/// <list type="bullet">
///   <item>Indexed find-or-create against the <c>parent_key</c> shadow column
///     for parent Works (albums, shows, series).</item>
///   <item>Ordinal/title lookups for child Works under a known parent.</item>
///   <item>Catalog row promotion when a previously unowned child gets a file.</item>
///   <item>Merging writes to the <c>external_identifiers</c> JSON blob.</item>
/// </list>
///
/// All inserts use parameterised SQL through <see cref="SqliteConnection"/>
/// directly (Dapper is used only for the lightweight queries).
/// </summary>
public sealed class WorkRepository : IWorkRepository
{
    private readonly IDatabaseConnection _db;
    private readonly ILogger<WorkRepository>? _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    public WorkRepository(IDatabaseConnection db, ILogger<WorkRepository>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
        _logger = logger;
    }

    // ── Lookups ────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<Guid?> FindParentByKeyAsync(
        MediaType mediaType,
        string parentKey,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(parentKey))
        {
            return Task.FromResult<Guid?>(null);
        }

        using var conn = _db.CreateConnection();
        const string sql = """
            SELECT id
            FROM   works
            WHERE  media_type = @mediaType
              AND  parent_key = @parentKey
              AND  work_kind  = 'parent'
            LIMIT  1;
            """;

        var id = conn.QueryFirstOrDefault<Guid?>(
            sql, new { mediaType = mediaType.ToString(), parentKey });

        return Task.FromResult(id);
    }

    /// <inheritdoc/>
    public Task<Guid?> FindChildByOrdinalAsync(
        Guid parentWorkId,
        int ordinal,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        return Task.FromResult(FindChildByOrdinal(conn, null, parentWorkId, ordinal));
    }

    /// <summary>Transaction-aware form of <see cref="FindChildByOrdinalAsync"/>.</summary>
    internal static Guid? FindChildByOrdinal(
        SqliteConnection conn, SqliteTransaction? tx, Guid parentWorkId, int ordinal)
    {
        const string sql = """
            SELECT id
            FROM   works
            WHERE  parent_work_id = @parentId
              AND  ordinal        = @ordinal
              AND (media_type <> 'TV' OR work_kind = CASE
                WHEN EXISTS (SELECT 1 FROM works parent WHERE parent.id=@parentId
                    AND parent.media_type='TV' AND parent.work_kind='parent' AND parent.parent_work_id IS NULL)
                THEN 'parent' ELSE 'child' END)
            LIMIT  1;
            """;

        return conn.QueryFirstOrDefault<Guid?>(
            sql, new { parentId = parentWorkId, ordinal }, tx);
    }

    /// <inheritdoc/>
    public Task<Guid?> FindChildByOrdinalSortAsync(
        Guid parentWorkId,
        double ordinalSort,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        return Task.FromResult(FindChildByOrdinalSort(conn, null, parentWorkId, ordinalSort));
    }

    /// <summary>Transaction-aware form of <see cref="FindChildByOrdinalSortAsync"/>.</summary>
    internal static Guid? FindChildByOrdinalSort(
        SqliteConnection conn, SqliteTransaction? tx, Guid parentWorkId, double ordinalSort)
    {
        const string sql = """
            SELECT id
            FROM   works
            WHERE  parent_work_id = @parentId
              AND  ordinal_sort   = @ordinalSort
            LIMIT  1;
            """;

        return conn.QueryFirstOrDefault<Guid?>(
            sql, new { parentId = parentWorkId, ordinalSort }, tx);
    }

    /// <inheritdoc/>
    public Task<Guid?> FindChildByTitleAsync(
        Guid parentWorkId,
        string title,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(title))
        {
            return Task.FromResult<Guid?>(null);
        }

        using var conn = _db.CreateConnection();
        return Task.FromResult(FindChildByTitle(conn, null, parentWorkId, title));
    }

    /// <summary>Transaction-aware form of <see cref="FindChildByTitleAsync"/>.</summary>
    internal static Guid? FindChildByTitle(
        SqliteConnection conn, SqliteTransaction? tx, Guid parentWorkId, string title)
    {
        // Title comparison goes through the canonical_values → media_assets →
        // editions chain. For catalog children that have no asset yet, we
        // also fall back to canonical values written directly with the
        // child's work id as entity_id (CatalogUpsertService).
        const string sql = """
            SELECT w.id
            FROM   works w
            LEFT   JOIN editions e        ON e.work_id      = w.id
            LEFT   JOIN media_assets ma   ON ma.edition_id  = e.id
            LEFT   JOIN canonical_values cv_asset
                    ON cv_asset.entity_id = ma.id AND cv_asset.key = 'title'
            LEFT   JOIN canonical_values cv_work
                    ON cv_work.entity_id  = w.id  AND cv_work.key  = 'title'
            WHERE  w.parent_work_id = @parentId
              AND  (cv_asset.value = @title COLLATE NOCASE
                 OR cv_work.value  = @title COLLATE NOCASE)
            LIMIT  1;
            """;

        return conn.QueryFirstOrDefault<Guid?>(
            sql, new { parentId = parentWorkId, title }, tx);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ChildWorkReference>> GetDirectChildrenAsync(
        Guid parentWorkId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var children = conn.Query<DirectChildRow>(
            """
            SELECT id AS WorkId,
                   ordinal AS Ordinal,
                   work_kind AS WorkKind,
                   is_catalog_only AS IsCatalogOnly
            FROM works
            WHERE parent_work_id = @parentWorkId
            ORDER BY COALESCE(ordinal_sort, ordinal, 2147483647), id;
            """,
            new { parentWorkId })
            .Select(row => new ChildWorkReference(
                row.WorkId,
                row.Ordinal,
                Enum.TryParse<WorkKind>(row.WorkKind, ignoreCase: true, out var kind)
                    ? kind
                    : WorkKind.Standalone,
                row.IsCatalogOnly))
            .ToList();

        return Task.FromResult<IReadOnlyList<ChildWorkReference>>(children);
    }

    /// <inheritdoc/>
    public Task<Guid?> FindByExternalIdentifierAsync(
        string scheme,
        string value,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(scheme) || string.IsNullOrWhiteSpace(value))
        {
            return Task.FromResult<Guid?>(null);
        }

        using var conn = _db.CreateConnection();

        // SQLite's json_extract is the cleanest way to read a single key from
        // the JSON blob. The schemes we use are well-known constants
        // (BridgeIdKeys.*) so the path is always "$.{scheme}".
        const string sql = """
            SELECT id
            FROM   works
            WHERE  external_identifiers IS NOT NULL
              AND  json_extract(external_identifiers, '$.' || @scheme) = @value
            LIMIT  1;
            """;

        var id = conn.QueryFirstOrDefault<Guid?>(
            sql, new { scheme, value });

        return Task.FromResult(id);
    }

    // ── Inserts ────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<ConfirmedSiblingWorkQid?> FindConfirmedSiblingQidAsync(
        MediaType sourceMediaType,
        IReadOnlyList<MediaType> candidateMediaTypes,
        string title,
        string? creator,
        Guid? excludeWorkId = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(title) || candidateMediaTypes.Count == 0)
        {
            return Task.FromResult<ConfirmedSiblingWorkQid?>(null);
        }

        var normalizedTitle = NormalizeIdentityText(title, stripEditionMarkers: true);
        if (normalizedTitle.Length == 0)
        {
            return Task.FromResult<ConfirmedSiblingWorkQid?>(null);
        }

        var normalizedCreator = NormalizeCreatorVariants(creator);

        using var conn = _db.CreateConnection();
        var parameters = new DynamicParameters();
        var mediaPlaceholders = new string[candidateMediaTypes.Count];
        for (var i = 0; i < candidateMediaTypes.Count; i++)
        {
            var name = $"mediaType{i}";
            mediaPlaceholders[i] = "@" + name;
            parameters.Add(name, candidateMediaTypes[i].ToString());
        }

        parameters.Add("wikidataQid", BridgeIdKeys.WikidataQid);

        var rows = conn.Query<SiblingQidRow>("""
            SELECT DISTINCT
                   w.id         AS WorkId,
                   w.media_type AS MediaType,
                   COALESCE(NULLIF(TRIM(cv_work_qid.value), ''),
                            NULLIF(TRIM(cv_asset_qid.value), ''),
                            NULLIF(TRIM(w.wikidata_qid), ''),
                            NULLIF(TRIM(CASE
                                WHEN json_valid(w.external_identifiers)
                                THEN json_extract(w.external_identifiers, '$.' || @wikidataQid)
                            END), '')) AS WikidataQid,
                   COALESCE(NULLIF(TRIM(cv_work_title.value), ''),
                            NULLIF(TRIM(cv_asset_title.value), '')) AS Title,
                   COALESCE(NULLIF(TRIM(cva_work_author.value), ''),
                            NULLIF(TRIM(cva_asset_author.value), ''),
                            NULLIF(TRIM(cva_work_artist.value), ''),
                            NULLIF(TRIM(cva_asset_artist.value), '')) AS Creator
            FROM   works w
            INNER JOIN editions e
                    ON e.work_id = w.id
            INNER JOIN media_assets ma
                    ON ma.edition_id = e.id
                   AND ma.file_path_root IS NOT NULL
                   AND TRIM(ma.file_path_root) <> ''
                   AND ma.status <> 'Orphaned'
            LEFT JOIN canonical_values cv_work_qid
                   ON cv_work_qid.entity_id = w.id AND cv_work_qid.key = 'wikidata_qid'
            LEFT JOIN canonical_values cv_asset_qid
                   ON cv_asset_qid.entity_id = ma.id AND cv_asset_qid.key = 'wikidata_qid'
            LEFT JOIN canonical_values cv_work_title
                   ON cv_work_title.entity_id = w.id AND cv_work_title.key = 'title'
            LEFT JOIN canonical_values cv_asset_title
                   ON cv_asset_title.entity_id = ma.id AND cv_asset_title.key = 'title'
            LEFT JOIN canonical_value_arrays cva_work_author
                   ON cva_work_author.entity_id = w.id AND cva_work_author.key = 'author' AND cva_work_author.ordinal = 0
            LEFT JOIN canonical_value_arrays cva_asset_author
                   ON cva_asset_author.entity_id = ma.id AND cva_asset_author.key = 'author' AND cva_asset_author.ordinal = 0
            LEFT JOIN canonical_value_arrays cva_work_artist
                   ON cva_work_artist.entity_id = w.id AND cva_work_artist.key = 'artist' AND cva_work_artist.ordinal = 0
            LEFT JOIN canonical_value_arrays cva_asset_artist
                   ON cva_asset_artist.entity_id = ma.id AND cva_asset_artist.key = 'artist' AND cva_asset_artist.ordinal = 0
            WHERE  w.media_type IN (
            """ + string.Join(", ", mediaPlaceholders) + """
            )
              AND  w.is_catalog_only = 0
              AND  w.ownership = 'Owned';
            """, parameters).AsList();

        if (excludeWorkId.HasValue)
        {
            rows.RemoveAll(row => row.WorkId == excludeWorkId.Value);
        }

        var titleMatches = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.WikidataQid)
                       && !string.IsNullOrWhiteSpace(row.Title)
                       && NormalizeIdentityText(row.Title!, stripEditionMarkers: true) == normalizedTitle)
            .ToList();

        if (titleMatches.Count == 0)
        {
            return Task.FromResult<ConfirmedSiblingWorkQid?>(null);
        }

        if (normalizedCreator.Count > 0)
        {
            titleMatches = titleMatches
                .Where(row =>
                {
                    var rowCreators = NormalizeCreatorVariants(row.Creator);
                    return rowCreators.Count > 0 && rowCreators.Overlaps(normalizedCreator);
                })
                .ToList();
        }

        var qidGroups = titleMatches
            .Where(row => !string.IsNullOrWhiteSpace(row.WikidataQid))
            .GroupBy(row => row.WikidataQid!, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (qidGroups.Count != 1)
        {
            return Task.FromResult<ConfirmedSiblingWorkQid?>(null);
        }

        var match = qidGroups[0]
            .OrderBy(row => row.MediaType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
            .First();

        var mediaType = Enum.TryParse<MediaType>(match.MediaType, ignoreCase: true, out var parsed)
            ? parsed
            : sourceMediaType;

        return Task.FromResult<ConfirmedSiblingWorkQid?>(new ConfirmedSiblingWorkQid(
            match.WorkId,
            mediaType,
            match.WikidataQid!,
            match.Title!,
            match.Creator));
    }

    /// <inheritdoc/>
    public Task<Guid> InsertParentAsync(
        MediaType mediaType,
        string parentKey,
        Guid? grandparentWorkId,
        int? ordinal,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var workId = Guid.NewGuid();

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO works
                (id, collection_id, media_type, work_kind, parent_work_id,
                 ordinal, is_catalog_only, parent_key, wikidata_status)
            VALUES
                (@id, NULL, @mediaType, 'parent', @parentId,
                 @ordinal, 0, @parentKey, 'pending');
            """;
        cmd.Parameters.AddWithValue("@id", GuidSql.ToBlob(workId));
        cmd.Parameters.AddWithValue("@mediaType", mediaType.ToString());
        cmd.Parameters.AddWithValue("@parentId", grandparentWorkId.HasValue ? GuidSql.ToBlob(grandparentWorkId.Value) : DBNull.Value);
        cmd.Parameters.AddWithValue("@ordinal", (object?)ordinal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@parentKey", parentKey);
        cmd.ExecuteNonQuery();

        _logger?.LogDebug(
            "Inserted parent Work {WorkId} ({MediaType}) parent_key='{ParentKey}' grandparent={Grandparent} ordinal={Ordinal}",
            workId, mediaType, parentKey, grandparentWorkId, ordinal);

        return Task.FromResult(workId);
    }

    /// <inheritdoc/>
    public Task<Guid> GetOrCreateAudiobookRecordingAsync(string recordingKey, Guid? seriesId, double? ordinalSort, CancellationToken ct = default)
        => _db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var found = conn.QueryFirstOrDefault<Guid?>("SELECT id FROM works WHERE media_type='Audiobooks' AND parent_key=@recordingKey AND work_kind!='parent' LIMIT 1", new { recordingKey }, tx);
            if (found.HasValue)
            {
                return found.Value;
            }
            var id = Guid.NewGuid();
            conn.Execute("INSERT INTO works (id,media_type,work_kind,parent_key,parent_work_id,ordinal_sort) VALUES (@id,'Audiobooks',@kind,@recordingKey,@seriesId,@ordinalSort)",
                new { id, recordingKey, seriesId, ordinalSort, kind = seriesId.HasValue ? "child" : "standalone" }, tx);
            return id;
        }, ct);

    public async Task<Guid> GetOrCreateParentAsync(
        MediaType mediaType,
        string parentKey,
        Guid? grandparentWorkId,
        int? ordinal,
        double? ordinalSort = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(parentKey))
        {
            throw new ArgumentException("Parent key is required.", nameof(parentKey));
        }

        var resolved = await _db.ExecuteWriteAsync(
            (conn, tx, innerCt) => GetOrCreateParent(
                conn, tx, mediaType, parentKey, grandparentWorkId, ordinal, ordinalSort),
            ct).ConfigureAwait(false);

        _logger?.LogDebug(
            "Resolved parent Work {WorkId} ({MediaType}) parent_key='{ParentKey}' grandparent={Grandparent} ordinal={Ordinal}",
            resolved, mediaType, parentKey, grandparentWorkId, ordinal);

        return resolved;
    }

    /// <summary>
    /// Transaction-aware find-or-create of a parent Work, for callers that already hold the
    /// write lock and an open transaction.
    /// </summary>
    internal static Guid GetOrCreateParent(
        SqliteConnection conn,
        SqliteTransaction tx,
        MediaType mediaType,
        string parentKey,
        Guid? grandparentWorkId,
        int? ordinal,
        double? ordinalSort)
    {
        Guid? existing = grandparentWorkId.HasValue && ordinal.HasValue
            ? conn.QueryFirstOrDefault<Guid?>(
                """
                SELECT id
                FROM   works
                WHERE  media_type      = @mediaType
                  AND  parent_work_id  = @parentId
                  AND  ordinal         = @ordinal
                  AND  work_kind       = 'parent'
                LIMIT  1;
                """,
                new { mediaType = mediaType.ToString(), parentId = grandparentWorkId.Value, ordinal },
                tx)
            : conn.QueryFirstOrDefault<Guid?>(
                """
                SELECT id
                FROM   works
                WHERE  media_type = @mediaType
                  AND  parent_key = @parentKey
                  AND  work_kind  = 'parent'
                LIMIT  1;
                """,
                new { mediaType = mediaType.ToString(), parentKey },
                tx);

        if (existing is { } found)
        {
            if (ordinalSort.HasValue)
            {
                conn.Execute(
                    "UPDATE works SET ordinal_sort = COALESCE(ordinal_sort, @ordinalSort) WHERE id = @id;",
                    new { id = found, ordinalSort },
                    tx);
            }

            return found;
        }

        var workId = Guid.NewGuid();
        conn.Execute(
            """
            INSERT OR IGNORE INTO works
                (id, collection_id, media_type, work_kind, parent_work_id,
                 ordinal, ordinal_sort, is_catalog_only, parent_key, wikidata_status)
            VALUES
                (@id, NULL, @mediaType, 'parent', @parentId,
                 @ordinal, @ordinalSort, 0, @parentKey, 'pending');
            """,
            new
            {
                id = workId,
                mediaType = mediaType.ToString(),
                parentId = grandparentWorkId,
                ordinal,
                ordinalSort,
                parentKey
            },
            tx);

        var resolvedId = grandparentWorkId.HasValue && ordinal.HasValue
            ? conn.QuerySingle<Guid>(
                """
                SELECT id
                FROM   works
                WHERE  media_type      = @mediaType
                  AND  parent_work_id  = @parentId
                  AND  ordinal         = @ordinal
                  AND  work_kind       = 'parent'
                LIMIT  1;
                """,
                new { mediaType = mediaType.ToString(), parentId = grandparentWorkId.Value, ordinal },
                tx)
            : conn.QuerySingle<Guid>(
                """
                SELECT id
                FROM   works
                WHERE  media_type = @mediaType
                  AND  parent_key = @parentKey
                  AND  work_kind  = 'parent'
                LIMIT  1;
                """,
                new { mediaType = mediaType.ToString(), parentKey },
                tx);

        return resolvedId;
    }

    /// <inheritdoc/>
    public Task<Guid> InsertChildAsync(
        MediaType mediaType,
        Guid parentWorkId,
        int? ordinal,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var workId = Guid.NewGuid();

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO works
                (id, collection_id, media_type, work_kind, parent_work_id,
                 ordinal, is_catalog_only, wikidata_status)
            VALUES
                (@id, NULL, @mediaType, 'child', @parentId,
                 @ordinal, 0, 'pending');
            """;
        cmd.Parameters.AddWithValue("@id", GuidSql.ToBlob(workId));
        cmd.Parameters.AddWithValue("@mediaType", mediaType.ToString());
        cmd.Parameters.AddWithValue("@parentId", GuidSql.ToBlob(parentWorkId));
        cmd.Parameters.AddWithValue("@ordinal", (object?)ordinal ?? DBNull.Value);
        cmd.ExecuteNonQuery();

        return Task.FromResult(workId);
    }

    /// <inheritdoc/>
    public Task<Guid> GetOrCreateChildAsync(
        MediaType mediaType,
        Guid parentWorkId,
        int? ordinal,
        double? ordinalSort = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return _db.ExecuteWriteAsync(
            (conn, tx, innerCt) => GetOrCreateChild(conn, tx, mediaType, parentWorkId, ordinal, ordinalSort),
            ct);
    }

    /// <summary>
    /// Transaction-aware find-or-create of a child Work, for callers that already hold the
    /// write lock and an open transaction.
    /// </summary>
    internal static Guid GetOrCreateChild(
        SqliteConnection conn,
        SqliteTransaction tx,
        MediaType mediaType,
        Guid parentWorkId,
        int? ordinal,
        double? ordinalSort)
    {
        Guid? existing = ordinalSort.HasValue
            ? conn.QueryFirstOrDefault<Guid?>(
                """
                SELECT id
                FROM   works
                WHERE  parent_work_id = @parentId
                  AND  ordinal_sort   = @ordinalSort
                  AND  work_kind IN ('child', 'catalog')
                LIMIT  1;
                """,
                new { parentId = parentWorkId, ordinalSort },
                tx)
            : null;

        existing ??= ordinal.HasValue
            ? conn.QueryFirstOrDefault<Guid?>(
                """
                SELECT id
                FROM   works
                WHERE  parent_work_id = @parentId
                  AND  ordinal        = @ordinal
                  AND  work_kind IN ('child', 'catalog')
                LIMIT  1;
                """,
                new { parentId = parentWorkId, ordinal },
                tx)
            : null;

        if (existing is { } found)
        {
            conn.Execute(
                """
                UPDATE works
                SET    work_kind       = CASE WHEN work_kind = 'catalog' THEN 'child' ELSE work_kind END,
                       is_catalog_only = CASE WHEN work_kind = 'catalog' THEN 0 ELSE is_catalog_only END,
                       ownership       = CASE WHEN work_kind = 'catalog' THEN 'Owned' ELSE ownership END,
                       ordinal_sort    = COALESCE(ordinal_sort, @ordinalSort)
                WHERE  id = @id;
                """,
                new { id = found, ordinalSort },
                tx);

            return found;
        }

        var workId = Guid.NewGuid();
        conn.Execute(
            """
            INSERT OR IGNORE INTO works
                (id, collection_id, media_type, work_kind, parent_work_id,
                 ordinal, ordinal_sort, is_catalog_only, wikidata_status)
            VALUES
                (@id, NULL, @mediaType, 'child', @parentId,
                 @ordinal, @ordinalSort, 0, 'pending');
            """,
            new
            {
                id = workId,
                mediaType = mediaType.ToString(),
                parentId = parentWorkId,
                ordinal,
                ordinalSort
            },
            tx);

        var resolved = ordinalSort.HasValue
            ? conn.QuerySingle<Guid>(
                """
                SELECT id
                FROM   works
                WHERE  parent_work_id = @parentId
                  AND  ordinal_sort   = @ordinalSort
                  AND  work_kind IN ('child', 'catalog')
                LIMIT  1;
                """,
                new { parentId = parentWorkId, ordinalSort },
                tx)
            : conn.QuerySingle<Guid>(
                """
                SELECT id
                FROM   works
                WHERE  id = @id
                LIMIT  1;
                """,
                new { id = workId },
                tx);

        return resolved;
    }

    /// <inheritdoc/>
    public Task UpdateOrdinalSortAsync(
        Guid workId,
        double? ordinalSort,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!ordinalSort.HasValue)
        {
            return Task.CompletedTask;
        }

        using var conn = _db.CreateConnection();
        UpdateOrdinalSort(conn, null, workId, ordinalSort.Value);

        return Task.CompletedTask;
    }

    /// <summary>Transaction-aware form of <see cref="UpdateOrdinalSortAsync"/>.</summary>
    internal static void UpdateOrdinalSort(
        SqliteConnection conn, SqliteTransaction? tx, Guid workId, double ordinalSort) =>
        conn.Execute(
            "UPDATE works SET ordinal_sort = @ordinalSort WHERE id = @workId;",
            new { workId, ordinalSort },
            tx);

    /// <inheritdoc/>
    public Task<Guid> InsertStandaloneAsync(
        MediaType mediaType,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var workId = Guid.NewGuid();

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO works
                (id, collection_id, media_type, work_kind, is_catalog_only, wikidata_status)
            VALUES
                (@id, NULL, @mediaType, 'standalone', 0, 'pending');
            """;
        cmd.Parameters.AddWithValue("@id", GuidSql.ToBlob(workId));
        cmd.Parameters.AddWithValue("@mediaType", mediaType.ToString());
        cmd.ExecuteNonQuery();

        return Task.FromResult(workId);
    }

    /// <inheritdoc/>
    public Task<Guid> InsertCatalogChildAsync(
        MediaType mediaType,
        Guid parentWorkId,
        int? ordinal,
        IReadOnlyDictionary<string, string>? externalIdentifiers,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var workId = Guid.NewGuid();

        var idsJson = externalIdentifiers is { Count: > 0 }
            ? JsonSerializer.Serialize(externalIdentifiers, JsonOptions)
            : null;

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO works
                (id, collection_id, media_type, work_kind, parent_work_id,
                 ordinal, is_catalog_only, external_identifiers, wikidata_status,
                 ownership)
            VALUES
                (@id, NULL, @mediaType, 'catalog', @parentId,
                 @ordinal, 1, @ids, 'pending',
                 'Unowned');
            """;
        cmd.Parameters.AddWithValue("@id", GuidSql.ToBlob(workId));
        cmd.Parameters.AddWithValue("@mediaType", mediaType.ToString());
        cmd.Parameters.AddWithValue("@parentId", GuidSql.ToBlob(parentWorkId));
        cmd.Parameters.AddWithValue("@ordinal", (object?)ordinal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ids", (object?)idsJson ?? DBNull.Value);
        cmd.ExecuteNonQuery();

        return Task.FromResult(workId);
    }

    // ── Mutations ──────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task PromoteCatalogToOwnedAsync(Guid workId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        if (PromoteCatalogToOwned(conn, null, workId) > 0)
        {
            _logger?.LogInformation("Promoted catalog Work {WorkId} to owned child", workId);
        }

        return Task.CompletedTask;
    }

    /// <summary>Transaction-aware form of <see cref="PromoteCatalogToOwnedAsync"/>; returns rows changed.</summary>
    internal static int PromoteCatalogToOwned(SqliteConnection conn, SqliteTransaction? tx, Guid workId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE works
            SET    work_kind       = 'child',
                   is_catalog_only = 0,
                   ownership       = 'Owned'
            WHERE  id              = @id
              AND  work_kind       = 'catalog';
            """;
        cmd.Parameters.AddWithValue("@id", GuidSql.ToBlob(workId));
        return cmd.ExecuteNonQuery();
    }

    /// <inheritdoc/>
    public Task WriteExternalIdentifiersAsync(
        Guid workId,
        IReadOnlyDictionary<string, string> identifiers,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (identifiers.Count == 0)
        {
            return Task.CompletedTask;
        }

        // Read existing blob, merge new keys (no overwrite), write back.
        // Done in a single transaction to avoid lost-update races between
        // RetailMatchWorker and WikidataBridgeWorker writing in parallel.
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            string? currentJson;
            using (var read = conn.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = "SELECT external_identifiers FROM works WHERE id = @id;";
                read.Parameters.AddWithValue("@id", GuidSql.ToBlob(workId));
                currentJson = read.ExecuteScalar() as string;
            }

            Dictionary<string, string> merged;
            if (string.IsNullOrWhiteSpace(currentJson))
            {
                merged = new Dictionary<string, string>(identifiers, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                try
                {
                    merged = JsonSerializer.Deserialize<Dictionary<string, string>>(currentJson)
                        ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                catch (JsonException)
                {
                    _logger?.LogWarning(
                        "Existing external_identifiers JSON for Work {WorkId} is malformed; resetting",
                        workId);
                    merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                foreach (var kv in identifiers)
                {
                    if (!merged.ContainsKey(kv.Key))
                    {
                        merged[kv.Key] = kv.Value;
                    }
                }
            }

            var newJson = JsonSerializer.Serialize(merged, JsonOptions);

            using (var write = conn.CreateCommand())
            {
                write.Transaction = tx;
                write.CommandText = """
                    UPDATE works
                    SET    external_identifiers = @json
                    WHERE  id                   = @id;
                    """;
                write.Parameters.AddWithValue("@id", GuidSql.ToBlob(workId));
                write.Parameters.AddWithValue("@json", newJson);
                write.ExecuteNonQuery();
            }

        }, ct);
    }

    /// <inheritdoc/>
    public Task<WorkLineage?> GetLineageByAssetAsync(
        Guid assetId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Single join walks asset → edition → work plus two LEFT JOINs up
        // the parent chain. COALESCE picks the topmost parent so a TV episode
        // resolves to its SHOW (gp.id) not its season (p.id), and a music
        // track resolves to its album (p.id), and a standalone movie falls
        // back to its own Work (w.id).
        const string sql = """
            SELECT a.id             AS AssetId,
                   e.id             AS EditionId,
                   w.id             AS WorkId,
                   w.parent_work_id AS ParentWorkId,
                   CASE WHEN w.media_type = 'Audiobooks' AND w.parent_key LIKE 'source-folder:%'
                        THEN w.id ELSE COALESCE(gp.id, p.id, w.id) END AS RootParentWorkId,
                   w.work_kind      AS WorkKind,
                   w.media_type     AS MediaType
            FROM   media_assets a
            JOIN   editions e  ON e.id  = a.edition_id
            JOIN   works    w  ON w.id  = e.work_id
            LEFT JOIN works p  ON p.id  = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            WHERE  a.id = @assetId
            LIMIT  1;
            """;

        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<LineageRow>(sql, new { assetId });
        if (row is null)
        {
            return Task.FromResult<WorkLineage?>(null);
        }

        var workKind = Enum.TryParse<WorkKind>(
            row.WorkKind, ignoreCase: true, out var wk)
            ? wk
            : WorkKind.Standalone;

        var mediaType = Enum.TryParse<MediaType>(row.MediaType, ignoreCase: true, out var mt)
            ? mt
            : MediaType.Unknown;

        var lineage = new WorkLineage(
            AssetId: row.AssetId,
            EditionId: row.EditionId,
            WorkId: row.WorkId,
            ParentWorkId: row.ParentWorkId,
            RootParentWorkId: row.RootParentWorkId,
            WorkKind: workKind,
            MediaType: mediaType);

        return Task.FromResult<WorkLineage?>(lineage);
    }

    private sealed class LineageRow
    {
        public Guid AssetId { get; set; }
        public Guid EditionId { get; set; }
        public Guid WorkId { get; set; }
        public Guid? ParentWorkId { get; set; }
        public Guid RootParentWorkId { get; set; }
        public string WorkKind { get; set; } = string.Empty;
        public string MediaType { get; set; } = string.Empty;
    }

    private sealed class DirectChildRow
    {
        public Guid WorkId { get; set; }
        public int? Ordinal { get; set; }
        public string WorkKind { get; set; } = string.Empty;
        public bool IsCatalogOnly { get; set; }
    }

    private sealed class SiblingQidRow
    {
        public Guid WorkId { get; set; }
        public string MediaType { get; set; } = string.Empty;
        public string? WikidataQid { get; set; }
        public string? Title { get; set; }
        public string? Creator { get; set; }
    }

    private static HashSet<string> NormalizeCreatorVariants(string? value)
    {
        var variants = new HashSet<string>(StringComparer.Ordinal);
        var normalized = NormalizeIdentityText(value, stripEditionMarkers: false);
        if (normalized.Length > 0)
        {
            variants.Add(normalized);
        }

        if (!string.IsNullOrWhiteSpace(value) && value.Contains(',', StringComparison.Ordinal))
        {
            var parts = value.Split(',', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                var inverted = NormalizeIdentityText($"{parts[1]} {parts[0]}", stripEditionMarkers: false);
                if (inverted.Length > 0)
                {
                    variants.Add(inverted);
                }
            }
        }

        return variants;
    }

    private static string NormalizeIdentityText(string? value, bool stripEditionMarkers)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var repaired = TextEncodingRepair.RepairMojibake(value).Trim();
        if (stripEditionMarkers)
        {
            repaired = Regex.Replace(repaired, @"\([^)]*\)|\[[^\]]*\]", " ");
            repaired = Regex.Replace(
                repaired,
                @"\b(unabridged|abridged|audiobook|audio\s+book|complete\s+edition|retail|digital)\b",
                " ",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        var decomposed = repaired.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var previousWasSpace = true;

        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return builder.ToString().Trim().Normalize(NormalizationForm.FormC);
    }
}
