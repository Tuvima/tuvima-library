using System.Data;
using System.Text.Json;
using Dapper;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Storage;

public sealed class SeriesManifestRepository : ISeriesManifestRepository
{
    private readonly IDatabaseConnection _db;
    private readonly ILogger<SeriesManifestRepository>? _logger;

    private static readonly JsonSerializerOptions JsonOptions = MediaEngineJson.Web;

    public SeriesManifestRepository(IDatabaseConnection db, ILogger<SeriesManifestRepository>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
        _logger = logger;
    }

    public Task<SeriesManifestHydration?> GetHydrationAsync(string seriesQid, string mediaType, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<HydrationRow>(
            """
            SELECT h.series_qid AS SeriesQid, h.collection_id AS CollectionId, h.series_label AS SeriesLabel,
                   h.manifest_source AS ManifestSource, h.manifest_version AS ManifestVersion,
                   h.manifest_hash AS ManifestHash, h.known_item_qids_hash AS KnownItemQidsHash,
                   h.warnings_json AS WarningsJson, h.api_metadata_json AS ApiMetadataJson,
                   h.last_hydrated_at AS LastHydratedAt, h.created_at AS CreatedAt, h.updated_at AS UpdatedAt
            FROM series_manifest_hydrations h
            INNER JOIN collections c ON c.id = h.collection_id
            WHERE h.series_qid = @seriesQid
              AND LOWER(c.media_type) = LOWER(@mediaType)
            ORDER BY h.last_hydrated_at DESC
            LIMIT 1;
            """,
            new { seriesQid, mediaType });

        return Task.FromResult(row?.ToEntity());
    }

    public Task<IReadOnlyList<SeriesManifestItemRecord>> GetItemsBySeriesQidAsync(
        string seriesQid,
        CancellationToken ct = default)
        => GetItemsBySeriesQidCoreAsync(seriesQid, mediaType: null, ct);

    public Task<IReadOnlyList<SeriesManifestItemRecord>> GetItemsBySeriesQidAsync(
        string seriesQid,
        string mediaType,
        CancellationToken ct = default)
        => GetItemsBySeriesQidCoreAsync(seriesQid, mediaType, ct);

    private Task<IReadOnlyList<SeriesManifestItemRecord>> GetItemsBySeriesQidCoreAsync(
        string seriesQid,
        string? mediaType,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<ItemRow>(
            """
            SELECT id AS Id, collection_id AS CollectionId, series_qid AS SeriesQid,
                   item_qid AS ItemQid, item_label AS ItemLabel, item_description AS ItemDescription,
                   media_type AS MediaType, media_kind AS MediaKind,
                   instance_of_qids_json AS InstanceOfQidsJson,
                   raw_ordinal AS RawOrdinal, parsed_ordinal AS ParsedOrdinal,
                   ordinal_scope_qid AS OrdinalScopeQid,
                   sort_order AS SortOrder, publication_date AS PublicationDate, duration AS Duration,
                   previous_qid AS PreviousQid, next_qid AS NextQid,
                   parent_collection_qid AS ParentCollectionQid,
                   parent_collection_label AS ParentCollectionLabel,
                   is_collection AS IsCollection, is_expanded_from_collection AS IsExpandedFromCollection,
                   membership_scope AS MembershipScope,
                   source_properties_json AS SourcePropertiesJson,
                   relationships_json AS RelationshipsJson, order_source AS OrderSource,
                   ownership_state AS OwnershipState, linked_work_id AS LinkedWorkId,
                   last_hydrated_at AS LastHydratedAt, created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM series_manifest_items
            WHERE series_qid = @seriesQid
              AND (@mediaType IS NULL OR LOWER(media_type) = LOWER(@mediaType))
            ORDER BY COALESCE(sort_order, 999999), COALESCE(item_label, item_qid), item_qid;
            """,
            new { seriesQid, mediaType }).AsList();

        return Task.FromResult<IReadOnlyList<SeriesManifestItemRecord>>(rows.Select(r => r.ToEntity()).ToList());
    }

    public Task<IReadOnlyDictionary<string, IReadOnlyList<Guid>>> FindWorkIdsByQidsAsync(
        IReadOnlyCollection<string> qids,
        CancellationToken ct = default)
        => FindWorkIdsByQidsCoreAsync(qids, mediaType: null, ct);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<Guid>>> FindWorkIdsByQidsAsync(
        IReadOnlyCollection<string> qids,
        string mediaType,
        CancellationToken ct = default)
        => FindWorkIdsByQidsCoreAsync(qids, mediaType, ct);

    private Task<IReadOnlyDictionary<string, IReadOnlyList<Guid>>> FindWorkIdsByQidsCoreAsync(
        IReadOnlyCollection<string> qids,
        string? mediaType,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (qids.Count == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<Guid>>>(
                new Dictionary<string, IReadOnlyList<Guid>>(StringComparer.OrdinalIgnoreCase));
        }

        using var conn = _db.CreateConnection();
        var rows = conn.Query<WorkQidRow>(
            """
            -- readiness: lookup of works by their Wikidata ids, to link manifest rows
            SELECT id AS WorkId,
                   CAST(COALESCE(NULLIF(wikidata_qid, ''), json_extract(external_identifiers, '$.wikidata_qid')) AS TEXT) AS Qid
            FROM works
            WHERE COALESCE(NULLIF(wikidata_qid, ''), json_extract(external_identifiers, '$.wikidata_qid')) IN @qids
              AND (@mediaType IS NULL OR LOWER(media_type) = LOWER(@mediaType));
            """,
            new { qids = qids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), mediaType }).AsList();

        var result = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Qid))
            .GroupBy(r => r.Qid!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<Guid>)g.Select(r => r.WorkId).Distinct().ToList(),
                StringComparer.OrdinalIgnoreCase);

        return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<Guid>>>(result);
    }

    public Task<IReadOnlyDictionary<string, IReadOnlyList<Guid>>> FindWorkIdsByExternalIdsAsync(
        string externalIdKey,
        IReadOnlyCollection<string> externalIds,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(externalIdKey);
        if (externalIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<Guid>>>(
                new Dictionary<string, IReadOnlyList<Guid>>(StringComparer.OrdinalIgnoreCase));
        }

        using var conn = _db.CreateConnection();
        var rows = conn.Query<WorkExternalIdRow>(
            """
            WITH candidates AS (
                SELECT w.id AS WorkId,
                       CAST(json_extract(w.external_identifiers, @jsonPath) AS TEXT) AS ExternalId
                FROM works w
                WHERE CAST(json_extract(w.external_identifiers, @jsonPath) AS TEXT) IN @externalIds
                  AND COALESCE(w.ownership, 'Owned') <> 'Unowned'
                  AND COALESCE(w.is_catalog_only, 0) = 0

                UNION ALL

                SELECT w.id AS WorkId,
                       cv.value AS ExternalId
                FROM canonical_values cv
                INNER JOIN works w ON w.id = cv.entity_id
                WHERE cv.key = @externalIdKey
                  AND cv.value IN @externalIds
                  AND COALESCE(w.ownership, 'Owned') <> 'Unowned'
                  AND COALESCE(w.is_catalog_only, 0) = 0

                UNION ALL

                SELECT w.id AS WorkId,
                       cv.value AS ExternalId
                FROM canonical_values cv
                INNER JOIN media_assets a ON a.id = cv.entity_id
                INNER JOIN editions e ON e.id = a.edition_id
                INNER JOIN works w ON w.id = e.work_id
                WHERE cv.key = @externalIdKey
                  AND cv.value IN @externalIds
                  AND COALESCE(w.ownership, 'Owned') <> 'Unowned'
                  AND COALESCE(w.is_catalog_only, 0) = 0
            )
            SELECT DISTINCT WorkId, ExternalId
            FROM candidates;
            """,
            new
            {
                jsonPath = $"$.{externalIdKey}",
                externalIdKey,
                externalIds = externalIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            }).AsList();

        var result = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.ExternalId))
            .GroupBy(row => row.ExternalId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<Guid>)group.Select(row => row.WorkId).Distinct().ToList(),
                StringComparer.OrdinalIgnoreCase);

        return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<Guid>>>(result);
    }

    public Task UpsertManifestAsync(
        SeriesManifestHydration hydration,
        IReadOnlyList<SeriesManifestItemRecord> items,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            conn.Execute(
                """
                INSERT INTO series_manifest_hydrations
                    (series_qid, collection_id, series_label, manifest_source, manifest_version,
                     manifest_hash, known_item_qids_hash, warnings_json, api_metadata_json,
                     last_hydrated_at, created_at, updated_at)
                VALUES
                    (@SeriesQid, @CollectionId, @SeriesLabel, @ManifestSource, @ManifestVersion,
                     @ManifestHash, @KnownItemQidsHash, @WarningsJson, @ApiMetadataJson,
                     @LastHydratedAt, @CreatedAt, @UpdatedAt)
                ON CONFLICT(collection_id, series_qid) DO UPDATE SET
                    series_label = excluded.series_label,
                    manifest_source = excluded.manifest_source,
                    manifest_version = excluded.manifest_version,
                    manifest_hash = excluded.manifest_hash,
                    known_item_qids_hash = excluded.known_item_qids_hash,
                    warnings_json = excluded.warnings_json,
                    api_metadata_json = excluded.api_metadata_json,
                    last_hydrated_at = excluded.last_hydrated_at,
                    updated_at = excluded.updated_at;
                """,
                ToHydrationParams(hydration),
                tx);

            var retainedItemQids = items.Select(item => item.ItemQid).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (retainedItemQids.Length > 0)
            {
                conn.Execute(
                    """
                    DELETE FROM series_manifest_items
                    WHERE collection_id = @CollectionId
                      AND series_qid = @SeriesQid
                      AND item_qid NOT IN @RetainedItemQids;
                    """,
                    new { hydration.CollectionId, hydration.SeriesQid, RetainedItemQids = retainedItemQids },
                    tx);
            }

            foreach (var item in items)
            {
                conn.Execute(
                    """
                    INSERT INTO series_manifest_items
                        (id, collection_id, series_qid, item_qid, item_label, item_description,
                         media_type, media_kind, instance_of_qids_json,
                          raw_ordinal, parsed_ordinal, ordinal_scope_qid, sort_order, publication_date, duration,
                         previous_qid, next_qid, parent_collection_qid, parent_collection_label,
                         is_collection, is_expanded_from_collection, membership_scope, source_properties_json,
                         relationships_json, order_source, ownership_state, linked_work_id,
                         last_hydrated_at, created_at, updated_at)
                    VALUES
                        (@Id, @CollectionId, @SeriesQid, @ItemQid, @ItemLabel, @ItemDescription,
                         @MediaType, @MediaKind, @InstanceOfQidsJson,
                         @RawOrdinal, @ParsedOrdinal, @OrdinalScopeQid, @SortOrder, @PublicationDate, @Duration,
                         @PreviousQid, @NextQid, @ParentCollectionQid, @ParentCollectionLabel,
                         @IsCollection, @IsExpandedFromCollection, @MembershipScope, @SourcePropertiesJson,
                         @RelationshipsJson, @OrderSource, @OwnershipState, @LinkedWorkId,
                         @LastHydratedAt, @CreatedAt, @UpdatedAt)
                    ON CONFLICT(collection_id, item_qid) DO UPDATE SET
                        series_qid = excluded.series_qid,
                        item_label = excluded.item_label,
                        item_description = excluded.item_description,
                        media_type = excluded.media_type,
                        media_kind = excluded.media_kind,
                        instance_of_qids_json = excluded.instance_of_qids_json,
                        raw_ordinal = excluded.raw_ordinal,
                        parsed_ordinal = excluded.parsed_ordinal,
                        ordinal_scope_qid = excluded.ordinal_scope_qid,
                        sort_order = excluded.sort_order,
                        publication_date = excluded.publication_date,
                        duration = excluded.duration,
                        previous_qid = excluded.previous_qid,
                        next_qid = excluded.next_qid,
                        parent_collection_qid = excluded.parent_collection_qid,
                        parent_collection_label = excluded.parent_collection_label,
                        is_collection = excluded.is_collection,
                        is_expanded_from_collection = excluded.is_expanded_from_collection,
                        membership_scope = excluded.membership_scope,
                        source_properties_json = excluded.source_properties_json,
                        relationships_json = excluded.relationships_json,
                        order_source = excluded.order_source,
                        ownership_state = excluded.ownership_state,
                        linked_work_id = excluded.linked_work_id,
                        last_hydrated_at = excluded.last_hydrated_at,
                        updated_at = excluded.updated_at;
                    """,
                    ToItemParams(item),
                    tx);
            }

        }, ct);
    }

    public async Task<IReadOnlyList<AmbiguousSeriesPlacement>> LinkOwnedWorksAsync(
        Guid collectionId,
        IReadOnlyList<SeriesManifestItemRecord> items,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var ambiguous = new List<AmbiguousSeriesPlacement>();
        await _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            // The one Series placement rule decides where every work these rows touch sits, so the order in which
            // manifests are hydrated (or this method is called) never decides it. A work is touched when a row names
            // it, or when it is listed in a parent collection that one of these rows lists (so placement through a
            // parent collection needs no later assignment run). Several rows may name the same work; the row that is
            // not an expansion of a collection supplies its position.
            var sortOrders = new Dictionary<Guid, double>();
            foreach (var group in items
                         .Where(i => i.OwnershipState == "Owned" && !i.IsCollection && i.LinkedWorkId.HasValue)
                         .GroupBy(i => i.LinkedWorkId!.Value))
            {
                var row = group.OrderBy(i => i.IsExpandedFromCollection).First();
                sortOrders[group.Key] = row.ParsedOrdinal ?? row.SortOrder ?? 0d;
            }

            var shelfMediaType = conn.ExecuteScalar<string?>(
                "SELECT media_type FROM collections WHERE id = @collectionId;",
                new { collectionId },
                tx);
            foreach (var listed in items.Where(i => i.IsCollection))
            {
                var mediaType = !string.IsNullOrWhiteSpace(shelfMediaType) ? shelfMediaType : listed.MediaType;
                var parentWorkIds = conn.Query<Guid>(
                    """
                    SELECT DISTINCT linked_work_id
                    FROM series_manifest_items
                    WHERE series_qid = @parentQid
                      AND linked_work_id IS NOT NULL
                      AND is_collection = 0
                      AND LOWER(COALESCE(media_type, '')) = LOWER(COALESCE(@mediaType, ''));
                    """,
                    new { parentQid = listed.ItemQid, mediaType },
                    tx);
                foreach (var parentWorkId in parentWorkIds)
                {
                    sortOrders.TryAdd(parentWorkId, 0d);
                }
            }

            foreach (var (workId, sortOrder) in sortOrders)
            {
                innerCt.ThrowIfCancellationRequested();
                var workMediaType = conn.ExecuteScalar<string?>(
                    "-- readiness: media type of one work, looked up by id\nSELECT media_type FROM works WHERE id = @workId;",
                    new { workId },
                    tx) ?? string.Empty;
                var decision = SeriesPlacementRule.Decide(new SeriesPlacementInput(
                    workMediaType,
                    LoadMemberships(conn, tx, workId),
                    Retail: null,
                    ClaimedSeriesQid: LoadClaimedSeriesQid(conn, tx, workId)));

                if (decision.Kind == SeriesPlacementKind.Ambiguous)
                {
                    // Two Series claim the work equally: it sits on neither until a person decides.
                    var candidateIds = decision.Candidates.Select(c => c.CollectionId).Distinct().Select(GuidSql.ToBlob).ToArray();
                    var cleared = conn.Execute(
                        "UPDATE works SET collection_id = NULL WHERE id = @workId AND collection_id IN @candidateIds;",
                        new { workId, candidateIds },
                        tx);
                    conn.Execute(
                        "DELETE FROM collection_items WHERE work_id = @workId AND collection_id IN @candidateIds;",
                        new { workId, candidateIds },
                        tx);
                    if (cleared > 0)
                    {
                        _logger?.LogInformation(
                            "Series linking: took work {WorkId} off its shelf: {Reason}",
                            workId,
                            decision.Reason);
                    }

                    ambiguous.Add(new AmbiguousSeriesPlacement(workId, decision.Candidates));
                    continue;
                }

                if (decision.Kind != SeriesPlacementKind.Placed || decision.ShelfCollectionId is not { } target)
                {
                    // No rule-chosen shelf: this manifest does not hold the work on a shelf.
                    conn.Execute(
                        "DELETE FROM collection_items WHERE collection_id = @collectionId AND work_id = @workId;",
                        new { collectionId, workId },
                        tx);
                    continue;
                }

                var current = conn.ExecuteScalar<Guid?>(
                    "-- readiness: current shelf of one work, looked up by id\nSELECT collection_id FROM works WHERE id = @workId;",
                    new { workId },
                    tx);

                // Decimal positions stay decimals: collection_items.sort_order has integer affinity, which keeps a
                // fractional value as a real number.
                conn.Execute(
                    """
                    INSERT INTO collection_items
                        (id, collection_id, work_id, sort_order, progress_state, added_at)
                    SELECT @id, @target, @workId, @sortOrder, 'not_started', @addedAt
                    WHERE NOT EXISTS (
                        SELECT 1 FROM collection_items
                        WHERE collection_id = @target AND work_id = @workId
                    );

                    UPDATE collection_items
                    SET sort_order = @sortOrder
                    WHERE collection_id = @target AND work_id = @workId
                      AND @target = @collectionId;

                    DELETE FROM collection_items
                    WHERE work_id = @workId
                      AND collection_id <> @target
                      AND collection_id IN (SELECT id FROM collections WHERE collection_type = 'ContentGroup');
                    """,
                    new
                    {
                        id = Guid.NewGuid(),
                        target,
                        collectionId,
                        workId,
                        sortOrder,
                        addedAt = DateTimeOffset.UtcNow.ToString("O"),
                    },
                    tx);

                if (current != target)
                {
                    conn.Execute(
                        "UPDATE works SET collection_id = @target WHERE id = @workId;",
                        new { target, workId },
                        tx);
                    _logger?.LogInformation(
                        "Series linking: moved work {WorkId} from shelf {From} to shelf {To}: {Reason}",
                        workId,
                        current,
                        target,
                        decision.Reason);
                }
            }
        }, ct).ConfigureAwait(false);

        return ambiguous;
    }

    private const string LinkedItemColumns = """
        id AS Id, collection_id AS CollectionId, series_qid AS SeriesQid,
        item_qid AS ItemQid, item_label AS ItemLabel, item_description AS ItemDescription,
        media_type AS MediaType, media_kind AS MediaKind,
        instance_of_qids_json AS InstanceOfQidsJson,
        raw_ordinal AS RawOrdinal, parsed_ordinal AS ParsedOrdinal,
        ordinal_scope_qid AS OrdinalScopeQid,
        sort_order AS SortOrder, publication_date AS PublicationDate, duration AS Duration,
        previous_qid AS PreviousQid, next_qid AS NextQid,
        parent_collection_qid AS ParentCollectionQid,
        parent_collection_label AS ParentCollectionLabel,
        is_collection AS IsCollection, is_expanded_from_collection AS IsExpandedFromCollection,
        membership_scope AS MembershipScope,
        source_properties_json AS SourcePropertiesJson,
        relationships_json AS RelationshipsJson, order_source AS OrderSource,
        ownership_state AS OwnershipState, linked_work_id AS LinkedWorkId,
        last_hydrated_at AS LastHydratedAt, created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    public Task<IReadOnlyList<SeriesManifestItemRecord>> GetLinkedItemsByCollectionIdsAsync(
        IReadOnlyCollection<Guid> collectionIds,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (collectionIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<SeriesManifestItemRecord>>([]);
        }

        using var conn = _db.CreateConnection();
        var rows = new List<SeriesManifestItemRecord>();
        foreach (var chunk in collectionIds.Distinct().Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            rows.AddRange(conn.Query<ItemRow>(
                $"""
                SELECT {LinkedItemColumns}
                FROM series_manifest_items
                WHERE collection_id IN @collectionIds
                  AND linked_work_id IS NOT NULL
                  AND is_collection = 0;
                """,
                new { collectionIds = chunk.Select(GuidSql.ToBlob).ToArray() }).Select(r => r.ToEntity()));
        }

        return Task.FromResult<IReadOnlyList<SeriesManifestItemRecord>>(rows);
    }

    public Task<IReadOnlyList<SeriesManifestMembership>> GetMembershipsForWorkAsync(
        Guid workId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        return Task.FromResult(LoadMemberships(conn, transaction: null, workId));
    }

    /// <summary>The Series QID the work's own <c>series_qid</c> claim names, used only to break a tie that is not at main sequence.</summary>
    private static string? LoadClaimedSeriesQid(IDbConnection conn, IDbTransaction? transaction, Guid workId)
    {
        var raw = conn.ExecuteScalar<string?>(
            "SELECT CAST(value AS TEXT) FROM canonical_values WHERE entity_id = @workId AND key = 'series_qid' LIMIT 1;",
            new { workId },
            transaction);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var qid = raw.Contains('/') ? raw.Split('/')[^1] : raw;
        return qid.Contains("::") ? qid.Split("::", 2)[0].Trim() : qid.Trim();
    }

    /// <summary>
    /// Every stored manifest row that names the work, as input for <see cref="SeriesPlacementRule"/>. A collection is a
    /// parent collection (for example Memory's Legion) when another Series manifest of the same media type lists its QID
    /// as a collection item; that is read from the data every time, never from a flag a later hydration could overwrite.
    /// A parent collection's membership reports the Series that list it, and is not itself a Series.
    /// </summary>
    private static IReadOnlyList<SeriesManifestMembership> LoadMemberships(
        IDbConnection conn,
        IDbTransaction? transaction,
        Guid workId)
    {
        var rows = conn.Query<MembershipRow>(
            """
            SELECT smi.collection_id AS CollectionId,
                   smi.series_qid AS SeriesQid,
                   COALESCE(NULLIF(c.media_type, ''), NULLIF(smi.media_type, ''), '') AS MediaType,
                   smi.membership_scope AS Scope,
                   CASE WHEN COALESCE(h.manifest_source, 'Tuvima.Wikidata') = 'Tuvima.Wikidata' THEN 1 ELSE 0 END AS IsWikidata
            FROM series_manifest_items smi
            LEFT JOIN collections c ON c.id = smi.collection_id
            LEFT JOIN series_manifest_hydrations h
                   ON h.collection_id = smi.collection_id AND h.series_qid = smi.series_qid
            WHERE smi.linked_work_id = @workId
              AND smi.is_collection = 0;
            """,
            new { workId },
            transaction).AsList();

        var memberships = new List<SeriesManifestMembership>(rows.Count);
        var listedCache = new Dictionary<string, IReadOnlyList<SeriesParentListing>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.IsWikidata != 1)
            {
                memberships.Add(new SeriesManifestMembership(
                    row.CollectionId, row.SeriesQid, row.MediaType, row.Scope, SeriesMembershipOrigin.ProviderSequence));
                continue;
            }

            var cacheKey = $"{row.SeriesQid}|{row.MediaType}";
            if (!listedCache.TryGetValue(cacheKey, out var listed))
            {
                listed = conn.Query<ParentListingRow>(
                    """
                    SELECT s.collection_id AS CollectionId,
                           s.series_qid AS SeriesQid,
                           COALESCE(NULLIF(sc.media_type, ''), NULLIF(s.media_type, ''), '') AS MediaType
                    FROM series_manifest_items s
                    LEFT JOIN collections sc ON sc.id = s.collection_id
                    WHERE s.is_collection = 1
                      AND s.item_qid = @parentQid
                      AND s.series_qid <> @parentQid
                    GROUP BY s.collection_id, s.series_qid;
                    """,
                    new { parentQid = row.SeriesQid },
                    transaction)
                    .Where(r => SeriesPlacementRule.MediaMatches(r.MediaType, row.MediaType))
                    .Select(r => new SeriesParentListing(r.SeriesQid, r.MediaType, r.CollectionId))
                    .ToList();
                listedCache[cacheKey] = listed;
            }

            memberships.Add(listed.Count > 0
                ? new SeriesManifestMembership(
                    row.CollectionId, row.SeriesQid, row.MediaType, row.Scope,
                    SeriesMembershipOrigin.WikidataParentCollection, listed)
                : new SeriesManifestMembership(
                    row.CollectionId, row.SeriesQid, row.MediaType, row.Scope, SeriesMembershipOrigin.WikidataSeries));
        }

        return memberships;
    }

    public Task<SeriesManifestViewDto?> GetViewByCollectionIdAsync(Guid collectionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var hydration = conn.QueryFirstOrDefault<HydrationRow>(
            """
            SELECT series_qid AS SeriesQid, collection_id AS CollectionId, series_label AS SeriesLabel,
                   manifest_source AS ManifestSource, manifest_version AS ManifestVersion,
                   manifest_hash AS ManifestHash, known_item_qids_hash AS KnownItemQidsHash,
                   warnings_json AS WarningsJson, api_metadata_json AS ApiMetadataJson,
                   last_hydrated_at AS LastHydratedAt, created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM series_manifest_hydrations
            WHERE collection_id = @collectionId
            LIMIT 1;
            """,
            new { collectionId });

        if (hydration is null)
        {
            return Task.FromResult<SeriesManifestViewDto?>(null);
        }

        var rows = conn.Query<ItemRow>(
            """
            SELECT id AS Id, collection_id AS CollectionId, series_qid AS SeriesQid,
                   item_qid AS ItemQid, item_label AS ItemLabel, item_description AS ItemDescription,
                   media_type AS MediaType, media_kind AS MediaKind,
                   instance_of_qids_json AS InstanceOfQidsJson,
                   raw_ordinal AS RawOrdinal, parsed_ordinal AS ParsedOrdinal,
                   ordinal_scope_qid AS OrdinalScopeQid,
                   sort_order AS SortOrder, publication_date AS PublicationDate, duration AS Duration,
                   previous_qid AS PreviousQid, next_qid AS NextQid,
                   parent_collection_qid AS ParentCollectionQid,
                   parent_collection_label AS ParentCollectionLabel,
                   is_collection AS IsCollection, is_expanded_from_collection AS IsExpandedFromCollection,
                   membership_scope AS MembershipScope,
                   source_properties_json AS SourcePropertiesJson,
                   relationships_json AS RelationshipsJson, order_source AS OrderSource,
                   ownership_state AS OwnershipState, linked_work_id AS LinkedWorkId,
                   last_hydrated_at AS LastHydratedAt, created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM series_manifest_items
            WHERE collection_id = @collectionId
            ORDER BY COALESCE(sort_order, 999999), COALESCE(item_label, item_qid), item_qid;
            """,
            new { collectionId }).AsList();

        var items = rows.Select(r => r.ToDto()).ToList();
        var primaryItems = items
            .Where(i => !i.IsCollection
                && string.Equals(i.MembershipScope, MediaEngine.Domain.Constants.SeriesMembershipScopeNames.MainSequence, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var warnings = DeserializeWarnings(hydration.WarningsJson);
        var metadata = DeserializeApiMetadata(hydration.ApiMetadataJson);
        var authoritativeTotals = conn.Query<HydrationRow>(
                """
                SELECT series_qid AS SeriesQid, collection_id AS CollectionId, series_label AS SeriesLabel,
                       manifest_source AS ManifestSource, manifest_version AS ManifestVersion,
                       manifest_hash AS ManifestHash, known_item_qids_hash AS KnownItemQidsHash,
                       warnings_json AS WarningsJson, api_metadata_json AS ApiMetadataJson,
                       last_hydrated_at AS LastHydratedAt, created_at AS CreatedAt, updated_at AS UpdatedAt
                FROM series_manifest_hydrations
                WHERE collection_id = @collectionId;
                """,
                new { collectionId })
            .Select(row => (row.SeriesQid, Metadata: DeserializeApiMetadata(row.ApiMetadataJson)))
            .Where(entry => string.Equals(entry.Metadata.Completeness, "Complete", StringComparison.OrdinalIgnoreCase)
                && entry.Metadata.ExpectedTotal is > 0)
            .ToDictionary(
                entry => entry.SeriesQid,
                entry => entry.Metadata.ExpectedTotal!.Value,
                StringComparer.OrdinalIgnoreCase);
        var ownedCount = primaryItems.Count(i => i.OwnershipState == "Owned");
        var provisionalCount = primaryItems.Count(i => i.OwnershipState == "Provisional");
        var ambiguousCount = primaryItems.Count(i => i.OwnershipState == "Ambiguous");
        var rowMissingCount = primaryItems.Count(i => i.OwnershipState == "Missing");
        var totalCount = Math.Max(primaryItems.Count, metadata.ExpectedTotal ?? 0);
        var expectedMissingCount = Math.Max(0, totalCount - ownedCount - provisionalCount - ambiguousCount);
        var missingCount = Math.Max(rowMissingCount, expectedMissingCount);

        return Task.FromResult<SeriesManifestViewDto?>(new SeriesManifestViewDto
        {
            CollectionId = hydration.CollectionId,
            SeriesQid = hydration.SeriesQid,
            SeriesLabel = hydration.SeriesLabel,
            LastHydratedAt = DateTimeOffset.Parse(hydration.LastHydratedAt),
            ContainerKind = metadata.ContainerKind,
            ExpectedTotal = metadata.ExpectedTotal,
            ExpectedTotalKind = metadata.ExpectedTotalKind,
            ExpectedTotalSource = metadata.ExpectedTotalSource,
            ExpectedTotalConfidence = metadata.ExpectedTotalConfidence,
            TotalCount = totalCount,
            OwnedCount = ownedCount,
            MissingCount = missingCount,
            ProvisionalCount = provisionalCount,
            AmbiguousCount = ambiguousCount,
            SupplementaryCount = items.Count(i => string.Equals(i.MembershipScope, MediaEngine.Domain.Constants.SeriesMembershipScopeNames.Supplementary, StringComparison.OrdinalIgnoreCase)),
            CollectedContentCount = items.Count(i => string.Equals(i.MembershipScope, MediaEngine.Domain.Constants.SeriesMembershipScopeNames.CollectedContent, StringComparison.OrdinalIgnoreCase)),
            UnpositionedCount = items.Count(i => string.Equals(i.MembershipScope, MediaEngine.Domain.Constants.SeriesMembershipScopeNames.Unpositioned, StringComparison.OrdinalIgnoreCase)),
            AuthoritativeTotalsByContainer = authoritativeTotals,
            Warnings = warnings,
            Items = items,
        });
    }

    private static object ToHydrationParams(SeriesManifestHydration hydration)
        => new
        {
            hydration.SeriesQid,
            hydration.CollectionId,
            hydration.SeriesLabel,
            hydration.ManifestSource,
            hydration.ManifestVersion,
            hydration.ManifestHash,
            hydration.KnownItemQidsHash,
            hydration.WarningsJson,
            hydration.ApiMetadataJson,
            LastHydratedAt = hydration.LastHydratedAt.ToString("O"),
            CreatedAt = hydration.CreatedAt.ToString("O"),
            UpdatedAt = hydration.UpdatedAt.ToString("O"),
        };

    private static object ToItemParams(SeriesManifestItemRecord item)
        => new
        {
            Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id,
            item.CollectionId,
            item.SeriesQid,
            item.ItemQid,
            item.ItemLabel,
            item.ItemDescription,
            item.MediaType,
            item.MediaKind,
            item.InstanceOfQidsJson,
            item.RawOrdinal,
            item.ParsedOrdinal,
            item.OrdinalScopeQid,
            item.SortOrder,
            item.PublicationDate,
            item.Duration,
            item.PreviousQid,
            item.NextQid,
            item.ParentCollectionQid,
            item.ParentCollectionLabel,
            IsCollection = item.IsCollection ? 1 : 0,
            IsExpandedFromCollection = item.IsExpandedFromCollection ? 1 : 0,
            item.MembershipScope,
            item.SourcePropertiesJson,
            item.RelationshipsJson,
            item.OrderSource,
            item.OwnershipState,
            item.LinkedWorkId,
            LastHydratedAt = item.LastHydratedAt.ToString("O"),
            CreatedAt = item.CreatedAt.ToString("O"),
            UpdatedAt = item.UpdatedAt.ToString("O"),
        };

    private static IReadOnlyList<SeriesManifestWarningDto> DeserializeWarnings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<SeriesManifestWarningDto>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<string> DeserializeStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<string>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ManifestApiMetadata DeserializeApiMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ManifestApiMetadata();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new ManifestApiMetadata
            {
                ContainerKind = ReadString(root, "containerKind", "container_kind"),
                ExpectedTotal = ReadInt(root, "expectedTotal", "expected_total"),
                ExpectedTotalKind = ReadString(root, "expectedTotalKind", "expected_total_kind"),
                ExpectedTotalSource = ReadString(root, "expectedTotalSource", "expected_total_source"),
                ExpectedTotalConfidence = ReadDouble(root, "expectedTotalConfidence", "expected_total_confidence"),
                Completeness = ReadString(root, "completeness"),
            };
        }
        catch (JsonException)
        {
            // Metadata is diagnostic/cache state; row-level manifest data remains valid.
            return new ManifestApiMetadata();
        }
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static int? ReadInt(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
            {
                return parsed;
            }

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static double? ReadDouble(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed))
            {
                return parsed;
            }

            if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private sealed class ManifestApiMetadata
    {
        public string? ContainerKind { get; init; }
        public int? ExpectedTotal { get; init; }
        public string? ExpectedTotalKind { get; init; }
        public string? ExpectedTotalSource { get; init; }
        public double? ExpectedTotalConfidence { get; init; }
        public string? Completeness { get; init; }
    }

    private sealed class HydrationRow
    {
        public required string SeriesQid { get; init; }
        public required Guid CollectionId { get; init; }
        public string? SeriesLabel { get; init; }
        public string ManifestSource { get; init; } = "Tuvima.Wikidata";
        public string? ManifestVersion { get; init; }
        public string? ManifestHash { get; init; }
        public string? KnownItemQidsHash { get; init; }
        public string WarningsJson { get; init; } = "[]";
        public string ApiMetadataJson { get; init; } = "{}";
        public required string LastHydratedAt { get; init; }
        public required string CreatedAt { get; init; }
        public required string UpdatedAt { get; init; }

        public SeriesManifestHydration ToEntity() => new()
        {
            SeriesQid = SeriesQid,
            CollectionId = CollectionId,
            SeriesLabel = SeriesLabel,
            ManifestSource = ManifestSource,
            ManifestVersion = ManifestVersion,
            ManifestHash = ManifestHash,
            KnownItemQidsHash = KnownItemQidsHash,
            WarningsJson = WarningsJson,
            ApiMetadataJson = ApiMetadataJson,
            LastHydratedAt = DateTimeOffset.Parse(LastHydratedAt),
            CreatedAt = DateTimeOffset.Parse(CreatedAt),
            UpdatedAt = DateTimeOffset.Parse(UpdatedAt),
        };
    }

    private sealed class ItemRow
    {
        public required Guid Id { get; init; }
        public required Guid CollectionId { get; init; }
        public required string SeriesQid { get; init; }
        public required string ItemQid { get; init; }
        public string? ItemLabel { get; init; }
        public string? ItemDescription { get; init; }
        public string? MediaType { get; init; }
        public string? MediaKind { get; init; }
        public string InstanceOfQidsJson { get; init; } = "[]";
        public string? RawOrdinal { get; init; }
        public double? ParsedOrdinal { get; init; }
        public string? OrdinalScopeQid { get; init; }
        public double? SortOrder { get; init; }
        public string? PublicationDate { get; init; }
        public string? Duration { get; init; }
        public string? PreviousQid { get; init; }
        public string? NextQid { get; init; }
        public string? ParentCollectionQid { get; init; }
        public string? ParentCollectionLabel { get; init; }
        public int IsCollection { get; init; }
        public int IsExpandedFromCollection { get; init; }
        public string MembershipScope { get; init; } = MediaEngine.Domain.Constants.SeriesMembershipScopeNames.MainSequence;
        public string SourcePropertiesJson { get; init; } = "[]";
        public string RelationshipsJson { get; init; } = "[]";
        public string OrderSource { get; init; } = "Unknown";
        public string OwnershipState { get; init; } = "Missing";
        public Guid? LinkedWorkId { get; init; }
        public required string LastHydratedAt { get; init; }
        public required string CreatedAt { get; init; }
        public required string UpdatedAt { get; init; }

        public SeriesManifestItemRecord ToEntity() => new()
        {
            Id = Id,
            CollectionId = CollectionId,
            SeriesQid = SeriesQid,
            ItemQid = ItemQid,
            ItemLabel = ItemLabel,
            ItemDescription = ItemDescription,
            MediaType = MediaType,
            MediaKind = MediaKind,
            InstanceOfQidsJson = InstanceOfQidsJson,
            RawOrdinal = RawOrdinal,
            ParsedOrdinal = ParsedOrdinal,
            OrdinalScopeQid = OrdinalScopeQid,
            SortOrder = SortOrder,
            PublicationDate = PublicationDate,
            Duration = Duration,
            PreviousQid = PreviousQid,
            NextQid = NextQid,
            ParentCollectionQid = ParentCollectionQid,
            ParentCollectionLabel = ParentCollectionLabel,
            IsCollection = IsCollection == 1,
            IsExpandedFromCollection = IsExpandedFromCollection == 1,
            MembershipScope = MembershipScope,
            SourcePropertiesJson = SourcePropertiesJson,
            RelationshipsJson = RelationshipsJson,
            OrderSource = OrderSource,
            OwnershipState = OwnershipState,
            LinkedWorkId = LinkedWorkId,
            LastHydratedAt = DateTimeOffset.Parse(LastHydratedAt),
            CreatedAt = DateTimeOffset.Parse(CreatedAt),
            UpdatedAt = DateTimeOffset.Parse(UpdatedAt),
        };

        public SeriesManifestItemDto ToDto() => new()
        {
            Id = Id,
            ItemQid = ItemQid,
            SeriesQid = SeriesQid,
            ItemLabel = ItemLabel,
            ItemDescription = ItemDescription,
            MediaType = MediaType,
            MediaKind = MediaKind,
            InstanceOfQids = DeserializeStringArray(InstanceOfQidsJson),
            RawOrdinal = RawOrdinal,
            ParsedOrdinal = ParsedOrdinal,
            OrdinalScopeQid = OrdinalScopeQid,
            SortOrder = SortOrder,
            PublicationDate = PublicationDate,
            Duration = Duration,
            ParentCollectionQid = ParentCollectionQid,
            ParentCollectionLabel = ParentCollectionLabel,
            IsCollection = IsCollection == 1,
            IsExpandedFromCollection = IsExpandedFromCollection == 1,
            MembershipScope = MembershipScope,
            OrderSource = OrderSource,
            OwnershipState = OwnershipState,
            LinkedWorkId = LinkedWorkId,
        };
    }

    private sealed class MembershipRow
    {
        public Guid CollectionId { get; init; }
        public string SeriesQid { get; init; } = string.Empty;
        public string MediaType { get; init; } = string.Empty;
        public string Scope { get; init; } = SeriesMembershipScopeNames.MainSequence;
        public int IsWikidata { get; init; }
    }

    private sealed class ParentListingRow
    {
        public Guid CollectionId { get; init; }
        public string SeriesQid { get; init; } = string.Empty;
        public string MediaType { get; init; } = string.Empty;
    }

    private sealed class WorkQidRow
    {
        public Guid WorkId { get; init; }
        public string? Qid { get; init; }
    }

    private sealed class WorkExternalIdRow
    {
        public Guid WorkId { get; init; }
        public string? ExternalId { get; init; }
    }
}
