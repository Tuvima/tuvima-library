using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="ICollectionRepository"/>.
/// Loads all collections with their child Works and each Work's CanonicalValues
/// using two sequential queries (no N+1) — same pattern as
/// <see cref="MediaAssetRepository"/>.
///
/// Uses Dapper for simple single-table queries; raw reader retained for the
/// complex multi-table JOIN in <see cref="GetAllAsync"/>.
/// </summary>
public sealed class CollectionRepository : ICollectionRepository
{
    private readonly IDatabaseConnection _db;
    private readonly IConfigurationLoader? _configLoader;

    private sealed class WorkLineageIdsRow
    {
        public Guid? LeafWorkId { get; init; }
        public Guid? ParentWorkId { get; init; }
        public Guid? RootWorkId { get; init; }
    }

    private sealed class WorkCanonicalValueRow
    {
        public Guid WorkId { get; init; }
        public Guid EntityId { get; init; }
        public string Key { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
        public DateTimeOffset LastScoredAt { get; init; }
        public int ScopeRank { get; init; }
    }

    private sealed class CollectionAudienceProfileRow
    {
        public Guid CollectionId { get; init; }
        public Guid ProfileId { get; init; }
    }

    // Reusable SELECT list for single-collection queries (no table prefix needed).
    private const string CollectionSelectColumns = """
        id                AS Id,
        universe_id       AS UniverseId,
        display_name      AS DisplayName,
        created_at        AS CreatedAt,
        universe_status   AS UniverseStatus,
        parent_collection_id     AS ParentCollectionId,
        wikidata_qid      AS WikidataQid,
        collection_type          AS CollectionType,
        description       AS Description,
        icon_name         AS IconName,
        cover_artwork_path       AS CoverArtworkPath,
        cover_artwork_mime_type  AS CoverArtworkMimeType,
        background_artwork_path       AS BackgroundArtworkPath,
        background_artwork_mime_type  AS BackgroundArtworkMimeType,
        banner_artwork_path       AS BannerArtworkPath,
        banner_artwork_mime_type  AS BannerArtworkMimeType,
        logo_artwork_path       AS LogoArtworkPath,
        logo_artwork_mime_type  AS LogoArtworkMimeType,
        scope             AS Scope,
        profile_id        AS ProfileId,
        membership_mode   AS MembershipMode,
        primary_area      AS PrimaryArea,
        owner_kind        AS OwnerKind,
        audience          AS Audience,
        is_enabled        AS IsEnabled,
        is_featured       AS IsFeatured,
        min_items         AS MinItems,
        rule_json         AS RuleJson,
        resolution        AS Resolution,
        rule_hash         AS RuleHash,
        group_by_field    AS GroupByField,
        match_mode        AS MatchMode,
        sort_field        AS SortField,
        sort_direction    AS SortDirection,
        secondary_sort_field     AS SecondarySortField,
        secondary_sort_direction AS SecondarySortDirection,
        refresh_schedule  AS RefreshSchedule,
        last_refreshed_at AS LastRefreshedAt,
        modified_at       AS ModifiedAt
        """;

    // Reusable SELECT list for collection_relationships rows.
    private const string RelSelectColumns = """
        id            AS Id,
        collection_id        AS CollectionId,
        rel_type      AS RelType,
        rel_qid       AS RelQid,
        rel_label     AS RelLabel,
        confidence    AS Confidence,
        discovered_at AS DiscoveredAt
        """;

    public CollectionRepository(IDatabaseConnection db, IConfigurationLoader? configLoader = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
        _configLoader = configLoader;
    }

    // -------------------------------------------------------------------------
    // Helpers — post-query fixup for Collection rows
    // -------------------------------------------------------------------------

    /// <summary>
    /// Dapper maps nullable Guid columns to Guid? via the registered type handler,
    /// but UniverseStatus defaults to null when the DB column is NULL.
    /// This helper normalises defaults after Dapper mapping.
    /// </summary>
    private static Collection NormalizeCollection(Collection h)
    {
        return h;
    }

    private static void HydrateAudienceProfiles(SqliteConnection conn, IReadOnlyCollection<Collection> collections)
    {
        if (collections.Count == 0)
        {
            return;
        }

        var byId = collections.ToDictionary(collection => collection.Id);
        foreach (var collection in collections)
        {
            collection.ReplaceAudienceProfiles([]);
        }

        var rows = conn.Query<CollectionAudienceProfileRow>("""
            SELECT collection_id AS CollectionId, profile_id AS ProfileId
            FROM collection_profile_audience
            WHERE collection_id IN @CollectionIds
            ORDER BY created_at, profile_id;
            """, new { CollectionIds = byId.Keys.Select(GuidSql.ToBlob).ToArray() });

        foreach (var group in rows.GroupBy(row => row.CollectionId))
        {
            if (byId.TryGetValue(group.Key, out var collection))
            {
                collection.ReplaceAudienceProfiles(group.Select(row => row.ProfileId));
            }
        }
    }

    private IReadOnlyList<string> GetCollectionRollupRelationshipTypes()
    {
        try
        {
            var configured = _configLoader?.LoadHydration().CollectionRollupRelationshipTypes
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (configured is { Count: > 0 })
            {
                return configured;
            }
        }
        catch
        {
            // Direct repository tests and first-run config fall back to defaults.
        }

        return new MediaEngine.Domain.Configuration.HydrationSettings()
            .CollectionRollupRelationshipTypes
            .ToList();
    }

    private static void AddIfPresent(List<Guid> ids, Guid? value)
    {
        if (value.HasValue)
        {
            ids.Add(value.Value);
        }
    }

    private static Guid ReadGuid(SqliteDataReader reader, int ordinal) =>
        GuidSql.FromDb(reader.GetValue(ordinal));

    private static Guid? ReadNullableGuid(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : GuidSql.FromDb(reader.GetValue(ordinal));

    private static Collection ReadJoinedCollection(SqliteDataReader reader)
    {
        var collection = new Collection
        {
            Id = ReadGuid(reader, 0),
            UniverseId = ReadNullableGuid(reader, 1),
            DisplayName = reader.IsDBNull(2) ? null : reader.GetString(2),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(3)),
            ParentCollectionId = ReadNullableGuid(reader, 5),
            WikidataQid = reader.IsDBNull(6) ? null : reader.GetString(6),
            Description = reader.IsDBNull(8) ? null : reader.GetString(8),
            IconName = reader.IsDBNull(9) ? null : reader.GetString(9),
            ProfileId = ReadNullableGuid(reader, 11),
            IsEnabled = !reader.IsDBNull(12) && reader.GetInt32(12) == 1,
            IsFeatured = !reader.IsDBNull(13) && reader.GetInt32(13) == 1,
            MinItems = reader.IsDBNull(14) ? 0 : reader.GetInt32(14),
            RuleJson = reader.IsDBNull(15) ? null : reader.GetString(15),
            RefreshSchedule = reader.IsDBNull(16) ? null : reader.GetString(16),
            LastRefreshedAt = reader.IsDBNull(17) ? null : DateTimeOffset.Parse(reader.GetString(17)),
            ModifiedAt = reader.IsDBNull(18) ? null : DateTimeOffset.Parse(reader.GetString(18)),
        };
        collection.RestoreDefinition(
            reader.IsDBNull(7)
                ? CollectionType.Universe
                : AggregateStateSerializer.ParseCollectionType(reader.GetString(7)),
            reader.IsDBNull(10)
                ? CollectionScope.Library
                : AggregateStateSerializer.ParseCollectionScope(reader.GetString(10)),
            CollectionResolution.Query,
            CollectionMatchMode.All,
            CollectionSortDirection.Desc,
            reader.IsDBNull(4)
                ? CollectionUniverseStatus.Unknown
                : AggregateStateSerializer.ParseCollectionUniverseStatus(reader.GetString(4)));
        return collection;
    }

    private static void AddJoinedWork(
        SqliteDataReader reader,
        Collection collection,
        IDictionary<Guid, Work> works)
    {
        const int workIdOrdinal = 19;
        if (reader.IsDBNull(workIdOrdinal))
        {
            return;
        }

        var workId = ReadGuid(reader, workIdOrdinal);
        if (works.ContainsKey(workId))
        {
            return;
        }

        var universeMismatch = !reader.IsDBNull(22) && reader.GetInt32(22) == 1;
        DateTimeOffset? universeMismatchAt = reader.IsDBNull(23)
            ? null
            : DateTimeOffset.Parse(reader.GetString(23));
        var wikidataStatus = reader.IsDBNull(24)
            ? WikidataLinkStatus.Pending
            : AggregateStateSerializer.ParseWikidataLinkStatus(reader.GetString(24));
        DateTimeOffset? wikidataCheckedAt = reader.IsDBNull(25)
            ? null
            : DateTimeOffset.Parse(reader.GetString(25));
        var wikidataQid = reader.IsDBNull(26) ? null : reader.GetString(26);
        var work = new Work
        {
            Id = workId,
            CollectionId = collection.Id,
            MediaType = Enum.Parse<MediaType>(reader.GetString(20), ignoreCase: true),
            Ordinal = reader.IsDBNull(21) ? null : reader.GetInt32(21),
        };
        work.RestoreWikidataState(
            wikidataQid,
            wikidataStatus,
            WorkMatchLevel.Work,
            wikidataCheckedAt,
            universeMismatch,
            universeMismatchAt);
        works[workId] = work;
        collection.AddWork(work);
    }

    private static void LoadCanonicalValuesForLoadedWorks(
        SqliteConnection conn,
        Dictionary<Guid, Work> works,
        bool visibleAssetsOnly)
    {
        if (works.Count == 0)
        {
            return;
        }

        var visibleAssetPredicate = visibleAssetsOnly
            ? $"AND {HomeVisibilitySql.VisibleAssetPathPredicate("ma.file_path_root")}"
            : string.Empty;

        var rows = new List<WorkCanonicalValueRow>();
        foreach (var batch in works.Keys.Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            rows.AddRange(conn.Query<WorkCanonicalValueRow>($"""
                WITH work_lineage AS (
                    SELECT w.id AS WorkId,
                           w.id AS LeafWorkId,
                           p.id AS ParentWorkId,
                           COALESCE(gp.id, p.id, w.id) AS RootWorkId
                    FROM works w
                    LEFT JOIN works p ON p.id = w.parent_work_id
                    LEFT JOIN works gp ON gp.id = p.parent_work_id
                    WHERE w.id IN @workIds
                ),
                canonical_sources AS (
                    SELECT WorkId, LeafWorkId AS EntityId, 1 AS ScopeRank
                    FROM work_lineage
                    UNION ALL
                    SELECT WorkId, ParentWorkId AS EntityId, 2 AS ScopeRank
                    FROM work_lineage
                    WHERE ParentWorkId IS NOT NULL
                    UNION ALL
                    SELECT WorkId, RootWorkId AS EntityId, 3 AS ScopeRank
                    FROM work_lineage
                    WHERE RootWorkId IS NOT NULL
                    UNION ALL
                    SELECT wl.WorkId, ma.id AS EntityId, 0 AS ScopeRank
                    FROM work_lineage wl
                    JOIN editions e ON e.work_id = wl.WorkId
                    JOIN media_assets ma ON ma.edition_id = e.id
                    WHERE 1 = 1
                    {visibleAssetPredicate}
                )
                SELECT cs.WorkId,
                       cv.entity_id AS EntityId,
                       cv.key AS Key,
                       cv.value AS Value,
                       cv.last_scored_at AS LastScoredAt,
                       cs.ScopeRank
                FROM canonical_sources cs
                JOIN canonical_values cv ON cv.entity_id = cs.EntityId
                ORDER BY cs.WorkId, cs.ScopeRank, cv.key, cv.last_scored_at DESC;
                """, new { workIds = batch.Select(GuidSql.ToBlob).ToArray() }));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!works.TryGetValue(row.WorkId, out var work))
            {
                continue;
            }

            var key = $"{row.WorkId:N}:{row.EntityId:N}:{row.Key}";
            if (!seen.Add(key))
            {
                continue;
            }

            work.AddCanonicalValue(new CanonicalValue
            {
                EntityId = row.EntityId,
                Key = row.Key,
                Value = row.Value,
                LastScoredAt = row.LastScoredAt,
            });
        }
    }

    // -------------------------------------------------------------------------
    // GetAllAsync — kept as raw reader (complex 3-query grouping pattern)
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public Task<IReadOnlyList<Collection>> GetAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var collections = new Dictionary<Guid, Collection>();
        var works = new Dictionary<Guid, Work>();

        // ── Query A: all collections LEFT JOIN their works ───────────────────────────
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT h.id, h.universe_id, h.display_name, h.created_at,
                       h.universe_status, h.parent_collection_id, h.wikidata_qid,
                       h.collection_type, h.description, h.icon_name, h.scope,
                       h.profile_id, h.is_enabled, h.is_featured, h.min_items,
                       h.rule_json, h.refresh_schedule, h.last_refreshed_at, h.modified_at,
                       w.id, w.media_type, w.ordinal,
                       w.universe_mismatch, w.universe_mismatch_at,
                       w.wikidata_status, w.wikidata_checked_at, w.wikidata_qid
                FROM   collections h
                LEFT JOIN works w ON w.collection_id = h.id
                ORDER  BY h.created_at, w.id;
                """;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var collectionId = ReadGuid(reader, 0);
                if (!collections.TryGetValue(collectionId, out var collection))
                {
                    collection = ReadJoinedCollection(reader);
                    collections[collectionId] = collection;
                }

                AddJoinedWork(reader, collection, works);
            }
        }

        // ── Query B: canonical values for all loaded works ────────────────────
        LoadCanonicalValuesForLoadedWorks(conn, works, visibleAssetsOnly: false);

        // ── Query C: collection relationships ────────────────────────────────────────
        if (collections.Count > 0)
        {
            foreach (var collectionIds in collections.Keys.Chunk(SqliteBatching.MaxParametersPerQuery))
            {
                ct.ThrowIfCancellationRequested();
                var paramNames = collectionIds.Select((_, i) => $"@h{i}").ToList();

                using var cmd3 = conn.CreateCommand();
                cmd3.CommandText = $"""
                    SELECT id, collection_id, rel_type, rel_qid, rel_label, confidence, discovered_at
                    FROM   collection_relationships
                    WHERE  collection_id IN ({string.Join(", ", paramNames)});
                    """;

                for (var i = 0; i < collectionIds.Length; i++)
                {
                    cmd3.Parameters.Add($"@h{i}", SqliteType.Blob).Value = GuidSql.ToBlob(collectionIds[i]);
                }

                using var reader3 = cmd3.ExecuteReader();
                while (reader3.Read())
                {
                    var collectionId = ReadGuid(reader3, 1);
                    if (collections.TryGetValue(collectionId, out var collection))
                    {
                        collection.AddRelationship(new CollectionRelationship
                        {
                            Id = ReadGuid(reader3, 0),
                            CollectionId = collectionId,
                            RelType = reader3.GetString(2),
                            RelQid = reader3.GetString(3),
                            RelLabel = reader3.IsDBNull(4) ? null : reader3.GetString(4),
                            Confidence = reader3.GetDouble(5),
                            DiscoveredAt = DateTimeOffset.Parse(reader3.GetString(6)),
                        });
                    }
                }
            }
        }

        IReadOnlyList<Collection> result = collections.Values.ToList();
        return Task.FromResult(result);
    }

    // -------------------------------------------------------------------------
    // Single-collection read methods — converted to Dapper
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public Task<Collection?> FindByRelationshipQidAsync(string relType, string qid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();

        // Find the collection that owns a relationship matching (rel_type, rel_qid).
        var collectionId = conn.ExecuteScalar<Guid?>("""
            SELECT collection_id FROM collection_relationships
            WHERE  rel_type = @relType AND rel_qid = @qid
            LIMIT  1;
            """, new { relType, qid });

        if (collectionId is null)
        {
            return Task.FromResult<Collection?>(null);
        }

        var collection = conn.QueryFirstOrDefault<Collection>($"""
            SELECT {CollectionSelectColumns}
            FROM   collections WHERE id = @id;
            """, new { id = collectionId.Value });

        if (collection is null)
        {
            return Task.FromResult<Collection?>(null);
        }

        NormalizeCollection(collection);

        // Load relationships for this collection.
        var rels = conn.Query<CollectionRelationship>($"""
            SELECT {RelSelectColumns}
            FROM   collection_relationships WHERE collection_id = @hid;
            """, new { hid = collectionId.Value }).AsList();

        collection.AddRelationships(rels);

        return Task.FromResult<Collection?>(collection);
    }

    /// <inheritdoc/>
    public Task InsertRelationshipsAsync(IReadOnlyList<CollectionRelationship> relationships, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (relationships.Count == 0)
        {
            return Task.CompletedTask;
        }

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            const string sql = """
                INSERT OR IGNORE INTO collection_relationships (id, collection_id, rel_type, rel_qid, rel_label, confidence, discovered_at)
                VALUES (@Id, @CollectionId, @RelType, @RelQid, @RelLabel, @Confidence, @DiscoveredAt);
                """;

            foreach (var relationship in relationships)
            {
                innerCt.ThrowIfCancellationRequested();
                // Use the entity's CLR property names directly so SQLite receives
                // the exact parameter names Dapper binds for Guid/DateTimeOffset handlers.
                conn.Execute(new CommandDefinition(
                    sql,
                    relationship,
                    transaction: tx));
            }
        }, ct);
    }

    /// <inheritdoc/>
    public Task<Guid?> GetWorkIdByMediaAssetAsync(Guid mediaAssetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var result = conn.ExecuteScalar<Guid?>("""
            SELECT e.work_id
            FROM   media_assets ma
            JOIN   editions e ON e.id = ma.edition_id
            WHERE  ma.id = @assetId
            LIMIT  1;
            """, new { assetId = mediaAssetId });

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Guid>> GetWorkLineageIdsByMediaAssetAsync(Guid mediaAssetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<WorkLineageIdsRow>("""
            SELECT leaf.id   AS LeafWorkId,
                   parent.id AS ParentWorkId,
                   root.id   AS RootWorkId
            FROM   media_assets ma
            JOIN   editions e ON e.id = ma.edition_id
            JOIN   works leaf ON leaf.id = e.work_id
            LEFT JOIN works parent ON parent.id = leaf.parent_work_id
            LEFT JOIN works root ON root.id = parent.parent_work_id
            WHERE  ma.id = @assetId
            LIMIT  1;
            """, new { assetId = mediaAssetId });

        if (row is null)
        {
            return Task.FromResult<IReadOnlyList<Guid>>([]);
        }

        var ids = new List<Guid>();
        AddIfPresent(ids, row.LeafWorkId);
        AddIfPresent(ids, row.ParentWorkId);
        AddIfPresent(ids, row.RootWorkId);

        return Task.FromResult<IReadOnlyList<Guid>>(ids
            .Distinct()
            .ToList());
    }

    /// <inheritdoc/>
    public Task<string?> FindCollectionNameByWorkIdAsync(Guid workId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var result = conn.ExecuteScalar<string>("""
            SELECT h.display_name
            FROM   works w
            JOIN   collections h ON h.id = w.collection_id
            WHERE  w.id = @workId
            LIMIT  1;
            """, new { workId });

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task AssignWorkToCollectionAsync(Guid workId, Guid collectionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            conn.Execute(
                "UPDATE works SET collection_id = @collectionId WHERE id = @workId;",
                new { collectionId, workId },
                transaction: tx);
        }, ct);
    }

    /// <inheritdoc/>
    public Task MergeCollectionsAsync(Guid keepCollectionId, Guid mergeCollectionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var keep = keepCollectionId;
        var merge = mergeCollectionId;

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            // Re-assign all Works from mergeCollection to keepCollection.
            conn.Execute(
                "UPDATE works SET collection_id = @keep WHERE collection_id = @merge;",
                new { keep, merge }, transaction: tx);

            // Move all relationships from mergeCollection to keepCollection (ignore duplicates).
            conn.Execute(
                "UPDATE OR IGNORE collection_relationships SET collection_id = @keep WHERE collection_id = @merge;",
                new { keep, merge }, transaction: tx);

            // Delete any remaining relationships on the merged collection (duplicates that couldn't move).
            conn.Execute(
                "DELETE FROM collection_relationships WHERE collection_id = @merge;",
                new { merge }, transaction: tx);

            // Delete the merged collection.
            conn.Execute(
                "DELETE FROM collections WHERE id = @merge;",
                new { merge }, transaction: tx);

        }, ct);
    }

    /// <inheritdoc/>
    public Task<Collection?> FindByDisplayNameAsync(string displayName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var collection = conn.QueryFirstOrDefault<Collection>($"""
            SELECT {CollectionSelectColumns}
            FROM   collections
            WHERE  LOWER(display_name) = LOWER(@name)
            LIMIT  1;
            """, new { name = displayName });

        return Task.FromResult(collection is null ? null : (Collection)NormalizeCollection(collection));
    }

    /// <inheritdoc/>
    public Task<Guid> UpsertAsync(Collection collection, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT OR IGNORE INTO collections(id, universe_id, parent_collection_id, display_name, created_at,
                universe_status, wikidata_qid, collection_type, description, icon_name,
                cover_artwork_path, cover_artwork_mime_type, background_artwork_path, background_artwork_mime_type,
                banner_artwork_path, banner_artwork_mime_type, logo_artwork_path, logo_artwork_mime_type, scope, profile_id,
                membership_mode, primary_area, owner_kind, audience,
                is_enabled, is_featured, min_items, rule_json, resolution, rule_hash,
                group_by_field, match_mode, sort_field, sort_direction, secondary_sort_field, secondary_sort_direction)
                VALUES (@id, @uid, @phid, @dn, @ca, @us, @wqid, @ht, @desc, @icon,
                    @coverArtworkPath, @coverArtworkMimeType, @backgroundArtworkPath, @backgroundArtworkMimeType,
                    @bannerArtworkPath, @bannerArtworkMimeType, @logoArtworkPath, @logoArtworkMimeType, @scope, @pid,
                    @membershipMode, @primaryArea, @ownerKind, @audience,
                    @enabled, @featured, @minItems, @ruleJson, @resolution, @ruleHash,
                    @groupByField, @matchMode, @sortField, @sortDirection, @secondarySortField, @secondarySortDirection);
            UPDATE collections SET display_name = @dn, universe_status = @us, parent_collection_id = @phid,
                            wikidata_qid = @wqid, collection_type = @ht, description = @desc,
                            icon_name = @icon,
                            cover_artwork_path = @coverArtworkPath,
                            cover_artwork_mime_type = @coverArtworkMimeType,
                            background_artwork_path = @backgroundArtworkPath,
                            background_artwork_mime_type = @backgroundArtworkMimeType,
                            banner_artwork_path = @bannerArtworkPath,
                            banner_artwork_mime_type = @bannerArtworkMimeType,
                            logo_artwork_path = @logoArtworkPath,
                            logo_artwork_mime_type = @logoArtworkMimeType,
                            scope = @scope, profile_id = @pid,
                            membership_mode = @membershipMode, primary_area = @primaryArea,
                            owner_kind = @ownerKind, audience = @audience,
                            is_enabled = @enabled, is_featured = @featured, min_items = @minItems,
                            rule_json = @ruleJson, resolution = @resolution, rule_hash = @ruleHash,
                            group_by_field = @groupByField, match_mode = @matchMode,
                            sort_field = @sortField, sort_direction = @sortDirection,
                            secondary_sort_field = @secondarySortField, secondary_sort_direction = @secondarySortDirection
                    WHERE id = @id;
            """,
            new
            {
                id = collection.Id,
                uid = collection.UniverseId,
                phid = collection.ParentCollectionId,
                dn = collection.DisplayName,
                ca = collection.CreatedAt.ToString("O"),
                us = collection.UniverseStatus.ToStorageValue(),
                wqid = collection.WikidataQid,
                ht = collection.CollectionType.ToStorageValue(),
                desc = collection.Description,
                icon = collection.IconName,
                coverArtworkPath = collection.CoverArtworkPath,
                coverArtworkMimeType = collection.CoverArtworkMimeType,
                backgroundArtworkPath = collection.BackgroundArtworkPath,
                backgroundArtworkMimeType = collection.BackgroundArtworkMimeType,
                bannerArtworkPath = collection.BannerArtworkPath,
                bannerArtworkMimeType = collection.BannerArtworkMimeType,
                logoArtworkPath = collection.LogoArtworkPath,
                logoArtworkMimeType = collection.LogoArtworkMimeType,
                scope = collection.Scope.ToStorageValue(),
                pid = collection.ProfileId,
                membershipMode = collection.MembershipMode.ToStorageValue(),
                primaryArea = collection.PrimaryArea.ToStorageValue(),
                ownerKind = collection.OwnerKind.ToStorageValue(),
                audience = collection.Audience.ToStorageValue(),
                enabled = collection.IsEnabled ? 1 : 0,
                featured = collection.IsFeatured ? 1 : 0,
                minItems = collection.MinItems,
                ruleJson = collection.RuleJson,
                resolution = collection.Resolution.ToStorageValue(),
                ruleHash = collection.RuleHash,
                groupByField = collection.GroupByField,
                matchMode = collection.MatchMode.ToStorageValue(),
                sortField = collection.SortField,
                sortDirection = collection.SortDirection.ToStorageValue(),
                secondarySortField = collection.SecondarySortField,
                secondarySortDirection = collection.SecondarySortDirection?.ToStorageValue(),
            });

        return Task.FromResult(collection.Id);
    }

    /// <inheritdoc/>
    public Task<Guid> CreateManagedCollectionAsync(
        Collection collection,
        IReadOnlyList<CollectionItem> items,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            conn.Execute("""
                INSERT INTO collections(id, universe_id, parent_collection_id, display_name, created_at,
                    universe_status, wikidata_qid, collection_type, description, icon_name,
                    cover_artwork_path, cover_artwork_mime_type, background_artwork_path, background_artwork_mime_type,
                    banner_artwork_path, banner_artwork_mime_type, logo_artwork_path, logo_artwork_mime_type, scope, profile_id,
                    membership_mode, primary_area, owner_kind, audience,
                    is_enabled, is_featured, min_items, rule_json, resolution, rule_hash,
                    group_by_field, match_mode, sort_field, sort_direction, secondary_sort_field, secondary_sort_direction)
                VALUES (@id, @uid, @phid, @dn, @ca, @us, @wqid, @ht, @desc, @icon,
                    @coverArtworkPath, @coverArtworkMimeType, @backgroundArtworkPath, @backgroundArtworkMimeType,
                    @bannerArtworkPath, @bannerArtworkMimeType, @logoArtworkPath, @logoArtworkMimeType, @scope, @pid,
                    @membershipMode, @primaryArea, @ownerKind, @audience,
                    @enabled, @featured, @minItems, @ruleJson, @resolution, @ruleHash,
                    @groupByField, @matchMode, @sortField, @sortDirection, @secondarySortField, @secondarySortDirection)
                """,
                new
                {
                    id = collection.Id,
                    uid = collection.UniverseId,
                    phid = collection.ParentCollectionId,
                    dn = collection.DisplayName,
                    ca = collection.CreatedAt.ToString("O"),
                    us = collection.UniverseStatus.ToStorageValue(),
                    wqid = collection.WikidataQid,
                    ht = collection.CollectionType.ToStorageValue(),
                    desc = collection.Description,
                    icon = collection.IconName,
                    coverArtworkPath = collection.CoverArtworkPath,
                    coverArtworkMimeType = collection.CoverArtworkMimeType,
                    backgroundArtworkPath = collection.BackgroundArtworkPath,
                    backgroundArtworkMimeType = collection.BackgroundArtworkMimeType,
                    bannerArtworkPath = collection.BannerArtworkPath,
                    bannerArtworkMimeType = collection.BannerArtworkMimeType,
                    logoArtworkPath = collection.LogoArtworkPath,
                    logoArtworkMimeType = collection.LogoArtworkMimeType,
                    scope = collection.Scope.ToStorageValue(),
                    pid = collection.ProfileId,
                    membershipMode = collection.MembershipMode.ToStorageValue(),
                    primaryArea = collection.PrimaryArea.ToStorageValue(),
                    ownerKind = collection.OwnerKind.ToStorageValue(),
                    audience = collection.Audience.ToStorageValue(),
                    enabled = collection.IsEnabled ? 1 : 0,
                    featured = collection.IsFeatured ? 1 : 0,
                    minItems = collection.MinItems,
                    ruleJson = collection.RuleJson,
                    resolution = collection.Resolution.ToStorageValue(),
                    ruleHash = collection.RuleHash,
                    groupByField = collection.GroupByField,
                    matchMode = collection.MatchMode.ToStorageValue(),
                    sortField = collection.SortField,
                    sortDirection = collection.SortDirection.ToStorageValue(),
                    secondarySortField = collection.SecondarySortField,
                    secondarySortDirection = collection.SecondarySortDirection?.ToStorageValue(),
                },
                transaction: tx);

            foreach (var item in items)
            {
                innerCt.ThrowIfCancellationRequested();
                conn.Execute("""
                    INSERT INTO collection_items (id, collection_id, work_id, sort_order, progress_state, progress_position, added_at)
                    VALUES (@Id, @CollectionId, @WorkId, @SortOrder, @ProgressState, @ProgressPosition, @AddedAt)
                    """,
                    new
                    {
                        Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id,
                        item.CollectionId,
                        item.WorkId,
                        item.SortOrder,
                        item.ProgressState,
                        item.ProgressPosition,
                        AddedAt = item.AddedAt == default ? DateTimeOffset.UtcNow.ToString("o") : item.AddedAt.ToString("o"),
                    },
                    transaction: tx);
            }

            return collection.Id;
        }, ct);
    }

    /// <inheritdoc/>
    public Task ReplaceAudienceProfileIdsAsync(
        Guid collectionId,
        IReadOnlyCollection<Guid> profileIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profileIds);
        ct.ThrowIfCancellationRequested();

        var normalizedIds = profileIds.Where(id => id != Guid.Empty).Distinct().ToList();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            conn.Execute(
                "DELETE FROM collection_profile_audience WHERE collection_id = @CollectionId;",
                new { CollectionId = collectionId },
                transaction: tx);

            foreach (var profileId in normalizedIds)
            {
                innerCt.ThrowIfCancellationRequested();
                conn.Execute("""
                    INSERT INTO collection_profile_audience(collection_id, profile_id, created_at)
                    VALUES (@CollectionId, @ProfileId, @CreatedAt);
                    """,
                    new
                    {
                        CollectionId = collectionId,
                        ProfileId = profileId,
                        CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                    },
                    transaction: tx);
            }
        }, ct);
    }

    /// <inheritdoc/>
    public Task SetUniverseMismatchAsync(Guid workId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE works
            SET    universe_mismatch    = 1,
                   universe_mismatch_at = @now
            WHERE  id = @id;
            """,
            new
            {
                id = workId,
                now = DateTimeOffset.UtcNow.ToString("O"),
            });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateWorkWikidataStatusAsync(Guid workId, string status, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE works
            SET    wikidata_status     = @status,
                   wikidata_checked_at = @now
            WHERE  id = @id;
            """,
            new
            {
                id = workId,
                status,
                now = DateTimeOffset.UtcNow.ToString("O"),
            });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateWorkWikidataMatchStateAsync(
        Guid workId,
        string status,
        string? source = null,
        bool? locked = null,
        string? wikidataQid = null,
        string? rejectedQidsJson = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE works
            SET    wikidata_status             = @status,
                   wikidata_checked_at         = @now,
                   wikidata_match_source       = COALESCE(@source, wikidata_match_source),
                   wikidata_match_locked       = COALESCE(@locked, wikidata_match_locked),
                   wikidata_qid                = CASE WHEN @hasQid = 1 THEN @qid ELSE wikidata_qid END,
                   wikidata_rejected_qids_json = COALESCE(@rejectedQidsJson, wikidata_rejected_qids_json)
            WHERE  id = @id;
            """,
            new
            {
                id = workId,
                status,
                now = DateTimeOffset.UtcNow.ToString("O"),
                source,
                locked = locked.HasValue ? (locked.Value ? 1 : 0) : (int?)null,
                hasQid = wikidataQid is null ? 0 : 1,
                qid = wikidataQid,
                rejectedQidsJson,
            });

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<int> PruneOrphanedHierarchyAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        int total = 0;

        // Pass 1: Remove editions that have no media assets.
        total += conn.Execute("""
            DELETE FROM editions
            WHERE id NOT IN (
                SELECT DISTINCT edition_id FROM media_assets
            );
            """);

        // Pass 2: Remove non-parent works that have no editions (after pass 1).
        // Parent works are real containers for albums, TV shows, seasons, and
        // series; they are cleaned up only after their child graph is empty.
        total += conn.Execute("""
            DELETE FROM works
            WHERE work_kind != 'parent'
              AND id NOT IN (
                SELECT DISTINCT work_id FROM editions
            );
            """);

        // Pass 3: Remove empty auto-created parents from the bottom up. This
        // removes empty seasons before their shows, and empty albums after their
        // last track disappears. User-retained parents are preserved.
        while (true)
        {
            var emptyParentIds = conn.Query<Guid>("""
                SELECT p.id
                FROM works p
                WHERE p.work_kind = 'parent'
                  AND COALESCE(p.is_catalog_only, 0) = 0
                  AND NOT EXISTS (SELECT 1 FROM editions e WHERE e.work_id = p.id)
                  AND NOT EXISTS (SELECT 1 FROM works child WHERE child.parent_work_id = p.id)
                  AND NOT EXISTS (SELECT 1 FROM collection_items ci WHERE ci.work_id = p.id)
                  AND NOT EXISTS (SELECT 1 FROM entity_assets ea WHERE ea.entity_id = p.id AND COALESCE(ea.is_user_override, 0) = 1)
                  AND COALESCE(NULLIF(p.display_overrides_json, ''), '') = '';
                """).ToList();

            if (emptyParentIds.Count == 0)
            {
                break;
            }

            foreach (var parentId in emptyParentIds)
            {
                conn.Execute("DELETE FROM entity_assets WHERE entity_id = @parentId AND COALESCE(is_user_override, 0) = 0;", new { parentId });
                conn.Execute("DELETE FROM canonical_values WHERE entity_id = @parentId;", new { parentId });
                conn.Execute("DELETE FROM metadata_claims WHERE entity_id = @parentId;", new { parentId });
                conn.Execute("DELETE FROM review_queue WHERE entity_id = @parentId;", new { parentId });
                conn.Execute("UPDATE series_manifest_items SET linked_work_id = NULL WHERE linked_work_id = @parentId;", new { parentId });
                total += conn.Execute("DELETE FROM works WHERE id = @parentId;", new { parentId });
            }
        }

        // Pass 4: Remove collections that have no works (after pass 2/3),
        // including their child collection_relationships rows.
        conn.Execute("""
            DELETE FROM collection_relationships
            WHERE collection_id NOT IN (
                SELECT DISTINCT collection_id FROM works
            );
            """); // relationships are not counted in the total

        // Only prune ContentGroup / Universe collections — these are the ones whose
        // identity is bound to the works they contain. System view collections (e.g.
        // "Music by Album", "TV by Show"), Smart collections, Mix, Playlist, and Custom
        // collections are query-resolved and intentionally have no work rows; they must
        // never be pruned.
        total += conn.Execute("""
            DELETE FROM collections
            WHERE collection_type IN ('ContentGroup', 'Universe')
              AND id NOT IN (
                SELECT DISTINCT collection_id FROM works WHERE collection_id IS NOT NULL
            );
            """);

        return Task.FromResult(total);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Collection>> GetChildCollectionsAsync(Guid parentCollectionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var results = conn.Query<Collection>($"""
            SELECT {CollectionSelectColumns}
            FROM   collections
            WHERE  parent_collection_id = @parentCollectionId
            ORDER  BY display_name;
            """, new { parentCollectionId })
            .Select(NormalizeCollection)
            .ToList();

        return Task.FromResult<IReadOnlyList<Collection>>(results);
    }

    /// <inheritdoc/>
    public Task SetParentCollectionAsync(Guid collectionId, Guid? parentCollectionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (parentCollectionId == collectionId)
        {
            return Task.CompletedTask;
        }

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            conn.Execute(
                "UPDATE collections SET parent_collection_id = @parentCollectionId WHERE id = @id;",
                new
                {
                    parentCollectionId,
                    id = collectionId,
                },
                transaction: tx);
        }, ct);
    }

    /// <inheritdoc/>
    public Task<Collection?> FindParentCollectionByRelationshipAsync(string qid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var rollupTypes = GetCollectionRollupRelationshipTypes();
        var collection = conn.QueryFirstOrDefault<Collection>("""
            SELECT h.id                AS Id,
                   h.universe_id       AS UniverseId,
                   h.display_name      AS DisplayName,
                   h.created_at        AS CreatedAt,
                   h.universe_status   AS UniverseStatus,
                   h.parent_collection_id     AS ParentCollectionId,
                   h.wikidata_qid      AS WikidataQid,
                   h.collection_type          AS CollectionType,
                   h.description       AS Description,
                   h.icon_name         AS IconName,
                   h.scope             AS Scope,
                   h.profile_id        AS ProfileId,
                   h.is_enabled        AS IsEnabled,
                   h.is_featured       AS IsFeatured,
                   h.min_items         AS MinItems,
                   h.rule_json         AS RuleJson,
                   h.refresh_schedule  AS RefreshSchedule,
                   h.last_refreshed_at AS LastRefreshedAt,
                   h.modified_at       AS ModifiedAt
            FROM   collections h
            INNER JOIN collection_relationships hr ON hr.collection_id = h.id
            WHERE  hr.rel_qid = @qid
              AND  hr.rel_type IN @rollupTypes
              AND  h.parent_collection_id IS NULL
              AND  COALESCE(h.collection_type, 'Universe') <> 'ContentGroup'
            LIMIT  1;
            """, new { qid, rollupTypes });

        return Task.FromResult(collection is null ? null : (Collection)NormalizeCollection(collection));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Guid>> FindCollectionIdsByFranchiseQidAsync(string qid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var rollupTypes = GetCollectionRollupRelationshipTypes();
        var results = conn.Query<Guid>("""
            SELECT DISTINCT hr.collection_id
            FROM   collection_relationships hr
            INNER JOIN collections h ON h.id = hr.collection_id
            WHERE  hr.rel_qid  = @qid
              AND  hr.rel_type IN @rollupTypes
              AND  COALESCE(h.collection_type, 'ContentGroup') IN ('ContentGroup', 'Series');
            """, new { qid, rollupTypes })
            .ToList();

        return Task.FromResult<IReadOnlyList<Guid>>(results);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<CollectionRelationship>> GetRelationshipsAsync(Guid collectionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var results = conn.Query<CollectionRelationship>($"""
            SELECT {RelSelectColumns}
            FROM   collection_relationships
            WHERE  collection_id = @collectionId;
            """, new { collectionId }).AsList();

        return Task.FromResult<IReadOnlyList<CollectionRelationship>>(results);
    }

    /// <inheritdoc/>
    public Task<Collection?> GetByIdAsync(Guid collectionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var collection = conn.QueryFirstOrDefault<Collection>($"""
            SELECT {CollectionSelectColumns}
            FROM   collections
            WHERE  id = @id
            LIMIT  1;
            """, new { id = collectionId });

        if (collection is null)
        {
            return Task.FromResult<Collection?>(null);
        }

        NormalizeCollection(collection);
        HydrateAudienceProfiles(conn, [collection]);
        return Task.FromResult<Collection?>(collection);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Collection>> GetByIdsAsync(
        IEnumerable<Guid> collectionIds,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(collectionIds);

        var orderedIds = collectionIds.Distinct().ToList();
        if (orderedIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<Collection>>([]);
        }

        using var conn = _db.CreateConnection();
        var collectionsById = new Dictionary<Guid, Collection>();
        foreach (var batch in orderedIds.Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            var rows = conn.Query<Collection>($"""
                SELECT {CollectionSelectColumns}
                FROM collections
                WHERE id IN @Ids;
                """, new { Ids = batch.Select(GuidSql.ToBlob).ToArray() });

            foreach (var collection in rows)
            {
                collectionsById[collection.Id] = NormalizeCollection(collection);
            }
        }

        IReadOnlyList<Collection> result = orderedIds
            .Where(collectionsById.ContainsKey)
            .Select(id => collectionsById[id])
            .ToList();
        HydrateAudienceProfiles(conn, result);
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<Collection?> FindByQidAsync(string qid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var collection = conn.QueryFirstOrDefault<Collection>($"""
            SELECT {CollectionSelectColumns}
            FROM   collections
            WHERE  wikidata_qid = @qid
            LIMIT  1;
            """, new { qid });

        return Task.FromResult(collection is null ? null : (Collection)NormalizeCollection(collection));
    }

    /// <inheritdoc/>
    public Task<Edition?> FindEditionByQidAsync(string wikidataQid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, work_id, format_label, wikidata_qid
            FROM   editions
            WHERE  wikidata_qid = @qid
            LIMIT  1;
            """;
        cmd.Parameters.AddWithValue("@qid", wikidataQid);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return Task.FromResult<Edition?>(null);
        }

        var edition = new Edition
        {
            Id = ReadGuid(reader, 0),
            WorkId = ReadGuid(reader, 1),
            FormatLabel = reader.IsDBNull(2) ? null : reader.GetString(2),
            WikidataQid = reader.IsDBNull(3) ? null : reader.GetString(3),
        };

        return Task.FromResult<Edition?>(edition);
    }

    /// <inheritdoc/>
    public Task<Edition> CreateEditionAsync(Guid workId, string? formatLabel, string? wikidataQid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var edition = new Edition
        {
            Id = Guid.NewGuid(),
            WorkId = workId,
            FormatLabel = formatLabel,
            WikidataQid = wikidataQid,
        };

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            conn.Execute("""
                INSERT INTO editions (id, work_id, format_label, wikidata_qid)
                VALUES (@id, @workId, @formatLabel, @wikidataQid);
                """,
                new
                {
                    id = edition.Id,
                    workId = edition.WorkId,
                    formatLabel = edition.FormatLabel,
                    wikidataQid = edition.WikidataQid,
                },
                transaction: tx);
            return edition;
        }, ct);
    }

    /// <inheritdoc/>
    public Task UpdateMatchLevelAsync(Guid workId, string matchLevel, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            conn.Execute(
                "UPDATE works SET match_level = @matchLevel WHERE id = @workId;",
                new { matchLevel, workId },
                transaction: tx);
        }, ct);
    }

    // ── Managed Collection methods ─────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<IReadOnlyList<Collection>> GetByTypeAsync(string collectionType, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var collections = conn.Query<Collection>(
            $"SELECT {CollectionSelectColumns} FROM collections WHERE collection_type = @CollectionType ORDER BY display_name",
            new { CollectionType = collectionType }).ToList();
        collections.ForEach(h => NormalizeCollection(h));
        HydrateAudienceProfiles(conn, collections);
        return Task.FromResult<IReadOnlyList<Collection>>(collections);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Collection>> GetManagedCollectionsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var collections = conn.Query<Collection>(
            $"SELECT {CollectionSelectColumns} FROM collections WHERE collection_type IN ('Custom', 'Playlist') ORDER BY collection_type, display_name").ToList();
        collections.ForEach(h => NormalizeCollection(h));
        HydrateAudienceProfiles(conn, collections);
        return Task.FromResult<IReadOnlyList<Collection>>(collections);
    }

    /// <inheritdoc/>
    public Task<Dictionary<string, int>> GetCountsByTypeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<(string CollectionType, int Count)>(
            "SELECT collection_type AS CollectionType, COUNT(*) AS Count FROM collections WHERE collection_type IN ('Custom', 'Playlist') GROUP BY collection_type");
        return Task.FromResult(rows.ToDictionary(r => r.CollectionType, r => r.Count));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<CollectionItem>> GetCollectionItemsAsync(Guid collectionId, int limit = 20, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var items = conn.Query<CollectionItem>(
            """
            SELECT id AS Id, collection_id AS CollectionId, work_id AS WorkId,
                   sort_order AS SortOrder, progress_state AS ProgressState,
                   progress_position AS ProgressPosition, added_at AS AddedAt
            FROM collection_items WHERE collection_id = @CollectionId
            ORDER BY sort_order LIMIT @Limit
            """,
            new { CollectionId = collectionId, Limit = limit });
        return Task.FromResult<IReadOnlyList<CollectionItem>>(items.ToList());
    }

    /// <inheritdoc/>
    public Task<int> GetCollectionItemCountAsync(Guid collectionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var count = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM collection_items WHERE collection_id = @CollectionId",
            new { CollectionId = collectionId });
        return Task.FromResult(count);
    }

    /// <inheritdoc/>
    public Task<Dictionary<Guid, int>> GetCollectionItemCountsAsync(IEnumerable<Guid> collectionIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var ids = collectionIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return Task.FromResult<Dictionary<Guid, int>>([]);
        }

        using var conn = _db.CreateConnection();
        var counts = ids.ToDictionary(id => id, _ => 0);
        foreach (var batch in ids.Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            var parameters = batch.Select((_, index) => $"@p{index}").ToArray();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT collection_id AS CollectionId, COUNT(*) AS Count
                FROM collection_items
                WHERE collection_id IN ({string.Join(", ", parameters)})
                GROUP BY collection_id;
                """;

            for (var i = 0; i < batch.Length; i++)
            {
                cmd.Parameters.AddWithValue(parameters[i], GuidSql.ToBlob(batch[i]));
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                counts[GuidSql.FromDb(reader.GetValue(0))] = reader.GetInt32(1);
            }
        }

        return Task.FromResult(counts);
    }

    /// <inheritdoc/>
    public Task UpdateCollectionEnabledAsync(Guid collectionId, bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            conn.Execute(
                "UPDATE collections SET is_enabled = @Enabled, modified_at = datetime('now') WHERE id = @Id",
                new { Id = collectionId, Enabled = enabled ? 1 : 0 },
                transaction: tx);
        }, ct);
    }

    /// <inheritdoc/>
    public Task UpdateCollectionFeaturedAsync(Guid collectionId, bool featured, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            conn.Execute(
                "UPDATE collections SET is_featured = @Featured, modified_at = datetime('now') WHERE id = @Id",
                new { Id = collectionId, Featured = featured ? 1 : 0 },
                transaction: tx);
        }, ct);
    }

    /// <inheritdoc/>
    public Task UpdateCollectionArtworkAsync(Guid collectionId, string slot, string? localPath, string? mimeType, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var (pathColumn, mimeColumn) = slot.ToLowerInvariant() switch
        {
            "poster" => ("cover_artwork_path", "cover_artwork_mime_type"),
            "background" => ("background_artwork_path", "background_artwork_mime_type"),
            "logo" => ("logo_artwork_path", "logo_artwork_mime_type"),
            _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "Unknown collection artwork slot."),
        };
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            conn.Execute(
                $"""
                UPDATE collections
                SET {pathColumn} = @LocalPath,
                    {mimeColumn} = @MimeType,
                    modified_at = datetime('now')
                WHERE id = @Id
                """,
                new { Id = collectionId, LocalPath = localPath, MimeType = mimeType },
                transaction: tx);
        }, ct);
    }

    /// <inheritdoc/>
    public Task AddCollectionItemAsync(CollectionItem item, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            conn.Execute(
                """
                INSERT INTO collection_items (id, collection_id, work_id, sort_order, progress_state, progress_position, added_at)
                VALUES (@Id, @CollectionId, @WorkId, @SortOrder, @ProgressState, @ProgressPosition, @AddedAt)
                """,
                new
                {
                    Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id,
                    item.CollectionId,
                    item.WorkId,
                    item.SortOrder,
                    item.ProgressState,
                    item.ProgressPosition,
                    AddedAt = item.AddedAt == default ? DateTimeOffset.UtcNow.ToString("o") : item.AddedAt.ToString("o")
                },
                transaction: tx);
        }, ct);
    }

    /// <inheritdoc/>
    public Task RemoveCollectionItemAsync(Guid itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            conn.Execute(
                "DELETE FROM collection_items WHERE id = @Id",
                new { Id = itemId },
                transaction: tx);
        }, ct);
    }

    /// <inheritdoc/>
    public Task ReorderCollectionItemsAsync(Guid collectionId, IReadOnlyList<Guid> itemIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            for (var index = 0; index < itemIds.Count; index++)
            {
                innerCt.ThrowIfCancellationRequested();
                conn.Execute(
                    """
                    UPDATE collection_items
                    SET sort_order = @SortOrder
                    WHERE id = @Id AND collection_id = @CollectionId
                    """,
                    new
                    {
                        Id = itemIds[index],
                        CollectionId = collectionId,
                        SortOrder = index + 1,
                    },
                    tx);
            }
        }, ct);
    }

    // -------------------------------------------------------------------------
    // Content Groups — structural collections that contain works (albums, series, etc.)
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public Task<IReadOnlyList<Collection>> GetContentGroupsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var collections = new Dictionary<Guid, Collection>();
        var works = new Dictionary<Guid, Work>();

        // Structural collection types that have at least one work assigned.
        using (var cmd = conn.CreateCommand())
        {
            var visibleWorkPredicate = HomeVisibilitySql.VisibleWorkPredicate("w.id", "w.curator_state", "w.is_catalog_only");
            cmd.CommandText = $"""
                SELECT h.id, h.universe_id, h.display_name, h.created_at,
                       h.universe_status, h.parent_collection_id, h.wikidata_qid,
                       h.collection_type, h.description, h.icon_name, h.scope,
                       h.profile_id, h.is_enabled, h.is_featured, h.min_items,
                       h.rule_json, h.refresh_schedule, h.last_refreshed_at, h.modified_at,
                       w.id, w.media_type, w.ordinal,
                       w.universe_mismatch, w.universe_mismatch_at,
                       w.wikidata_status, w.wikidata_checked_at, w.wikidata_qid
                FROM   collections h
                INNER JOIN works w ON w.collection_id = h.id
                               AND {visibleWorkPredicate}
                WHERE  h.collection_type IN ('ContentGroup', 'Series', 'Universe')
                ORDER  BY h.display_name, h.created_at, w.ordinal, w.id;
                """;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var collectionId = ReadGuid(reader, 0);
                if (!collections.TryGetValue(collectionId, out var collection))
                {
                    collection = ReadJoinedCollection(reader);
                    collections[collectionId] = collection;
                }

                AddJoinedWork(reader, collection, works);
            }
        }

        // Canonical values for file, leaf, parent, and root work rows.
        LoadCanonicalValuesForLoadedWorks(conn, works, visibleAssetsOnly: true);

        IReadOnlyList<Collection> result = collections.Values.ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<Collection?> GetCollectionWithWorksAsync(Guid collectionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var collections = LoadCollectionsWithWorks(conn, [collectionId], ct);
        return Task.FromResult<Collection?>(collections.Count == 0 ? null : collections[0]);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Collection>> GetCollectionsWithWorksAsync(
        IEnumerable<Guid> collectionIds,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(collectionIds);

        var orderedIds = collectionIds.Distinct().ToList();
        if (orderedIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<Collection>>([]);
        }

        using var conn = _db.CreateConnection();
        var collections = LoadCollectionsWithWorks(conn, orderedIds, ct);
        return Task.FromResult<IReadOnlyList<Collection>>(collections);
    }

    private static List<Collection> LoadCollectionsWithWorks(
        SqliteConnection conn,
        IReadOnlyList<Guid> orderedIds,
        CancellationToken ct)
    {
        var collectionsById = new Dictionary<Guid, Collection>();
        var works = new Dictionary<Guid, Work>();

        foreach (var batch in orderedIds.Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            var parameterNames = batch.Select((_, index) => $"@p{index}").ToArray();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT h.id, h.universe_id, h.display_name, h.created_at,
                       h.universe_status, h.parent_collection_id, h.wikidata_qid,
                       h.collection_type, h.description, h.icon_name, h.scope,
                       h.profile_id, h.is_enabled, h.is_featured, h.min_items,
                       h.rule_json, h.refresh_schedule, h.last_refreshed_at, h.modified_at,
                       w.id, w.media_type, w.ordinal,
                       w.universe_mismatch, w.universe_mismatch_at,
                       w.wikidata_status, w.wikidata_checked_at, w.wikidata_qid
                FROM   collections h
                LEFT JOIN works w ON w.collection_id = h.id
                WHERE  h.id IN ({string.Join(", ", parameterNames)})
                ORDER  BY h.id, w.ordinal, w.id;
                """;

            for (var index = 0; index < batch.Length; index++)
            {
                cmd.Parameters.Add(parameterNames[index], SqliteType.Blob).Value = GuidSql.ToBlob(batch[index]);
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var collectionId = ReadGuid(reader, 0);
                if (!collectionsById.TryGetValue(collectionId, out var collection))
                {
                    collection = ReadJoinedCollection(reader);
                    collectionsById[collectionId] = collection;
                }

                AddJoinedWork(reader, collection, works);
            }
        }

        // Canonical values for file, leaf, parent, and root work rows.
        LoadCanonicalValuesForLoadedWorks(conn, works, visibleAssetsOnly: false);

        return orderedIds
            .Where(collectionsById.ContainsKey)
            .Select(id => collectionsById[id])
            .ToList();
    }

    /// <inheritdoc/>
    public Task<Guid?> GetCollectionIdByWorkIdAsync(Guid workId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var collectionId = conn.QueryFirstOrDefault<Guid?>(
            "SELECT collection_id FROM works WHERE id = @workId AND collection_id IS NOT NULL;",
            new { workId });

        return Task.FromResult(collectionId);
    }

    /// <inheritdoc/>
    public Task<Collection?> FindByRuleHashAsync(string ruleHash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var collection = conn.QueryFirstOrDefault<Collection>(
            $"SELECT {CollectionSelectColumns} FROM collections WHERE rule_hash = @Hash LIMIT 1",
            new { Hash = ruleHash });
        return Task.FromResult(collection is null ? null : NormalizeCollection(collection));
    }

    /// <inheritdoc/>
    public Task<int> CountCollectionBackfillCandidatesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var visibleAssetPredicate = HomeVisibilitySql.VisibleAssetPathPredicate("ma.file_path_root");
        var count = conn.ExecuteScalar<int>(
            $"""
            SELECT COUNT(DISTINCT w.id)
            FROM works w
            INNER JOIN editions e ON e.work_id = w.id
            INNER JOIN media_assets ma ON ma.edition_id = e.id
            WHERE w.collection_id IS NULL
              AND COALESCE(w.is_catalog_only, 0) = 0
              AND COALESCE(w.work_kind, '') <> 'parent'
              AND ma.status = 'Normal'
              AND COALESCE(ma.is_orphaned, 0) = 0
              AND {visibleAssetPredicate};
            """);
        return Task.FromResult(count);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<CollectionBackfillCandidate>> GetCollectionBackfillCandidatesAsync(
        int limit,
        Guid? afterWorkId = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (limit <= 0)
        {
            return Task.FromResult<IReadOnlyList<CollectionBackfillCandidate>>([]);
        }

        using var conn = _db.CreateConnection();
        var visibleAssetPredicate = HomeVisibilitySql.VisibleAssetPathPredicate("ma.file_path_root");
        var cursorPredicate = afterWorkId.HasValue ? "AND hex(w.id) > @AfterWorkIdHex" : string.Empty;
        var rows = conn.Query<CollectionBackfillCandidate>(
            $"""
            SELECT w.id AS WorkId,
                   MIN(ma.id) AS MediaAssetId
            FROM works w
            INNER JOIN editions e ON e.work_id = w.id
            INNER JOIN media_assets ma ON ma.edition_id = e.id
            WHERE w.collection_id IS NULL
              AND COALESCE(w.is_catalog_only, 0) = 0
              AND COALESCE(w.work_kind, '') <> 'parent'
              AND ma.status = 'Normal'
              AND COALESCE(ma.is_orphaned, 0) = 0
              AND {visibleAssetPredicate}
              {cursorPredicate}
            GROUP BY w.id
            ORDER BY hex(w.id)
            LIMIT @Limit;
            """,
            new { Limit = limit, AfterWorkIdHex = afterWorkId?.ToString("N").ToUpperInvariant() });

        return Task.FromResult<IReadOnlyList<CollectionBackfillCandidate>>(rows.AsList());
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Collection>> GetAllCollectionsForLocationAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var collections = conn.Query<Collection>(
            $"SELECT {CollectionSelectColumns} FROM collections WHERE is_enabled = 1 ORDER BY display_name");
        return Task.FromResult<IReadOnlyList<Collection>>(collections.Select(NormalizeCollection).ToList());
    }
}
