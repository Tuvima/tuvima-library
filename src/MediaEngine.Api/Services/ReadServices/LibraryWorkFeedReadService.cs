using Dapper;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Paging;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Owns the library work-feed projection, including hierarchy-aware metadata
/// fallback and managed artwork URLs. The endpoint remains an HTTP boundary.
/// </summary>
public sealed class LibraryWorkFeedReadService(IDatabaseConnection db) : ILibraryWorkFeedReadService
{
    private const int MaxParametersPerQuery = 400;

    public async Task<PagedResponse<LibraryWorkListItemDto>> GetWorksAsync(
        PagedRequest page,
        CancellationToken ct = default,
        IReadOnlySet<Guid>? allowedWorkIds = null)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();

        var visibleWorkPredicate = WorkReadinessSql.ReadyWorkPredicate("w");
        var visibleAssetPredicate = HomeVisibilitySql.VisibleAssetPathPredicate("ad.file_path_root");
        var workRows = (await conn.QueryAsync<LibraryWorkFeedRow>(new CommandDefinition(
            $"""
            WITH asset_dates AS (
                SELECT
                    ma.id AS asset_id,
                    e.work_id AS work_id,
                    MIN(mc.claimed_at) AS created_at,
                    COALESCE(ma.file_path_root, '') AS file_path_root
                FROM media_assets ma
                INNER JOIN editions e ON e.id = ma.edition_id
                LEFT JOIN metadata_claims mc ON mc.entity_id = ma.id
                GROUP BY ma.id, e.work_id, ma.file_path_root
            ),
            ranked_assets AS (
                SELECT
                    w.id AS work_id,
                    w.collection_id AS collection_id,
                    w.media_type AS media_type,
                    w.work_kind AS work_kind,
                    w.ordinal AS ordinal,
                    COALESCE(gp.id, p.id, w.id) AS root_work_id,
                    ad.asset_id AS asset_id,
                    MIN(ad.created_at) OVER (PARTITION BY w.id) AS first_claimed_at,
                    ROW_NUMBER() OVER (
                        PARTITION BY w.id
                        ORDER BY
                            CASE WHEN ad.created_at IS NULL THEN 1 ELSE 0 END,
                            ad.created_at ASC,
                            ad.asset_id
                    ) AS asset_rank
                FROM works w
                INNER JOIN asset_dates ad ON ad.work_id = w.id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                WHERE w.work_kind != 'parent'
                  AND {visibleWorkPredicate}
                  AND {visibleAssetPredicate}
                  AND {VaultSql.NotVaultAsset("ad.asset_id")}
                  AND (@RestrictWorks = 0 OR w.id IN (SELECT unhex(value) FROM json_each(@AllowedWorkIds)))
            )
            SELECT
                work_id AS WorkId,
                collection_id AS CollectionId,
                media_type AS MediaType,
                work_kind AS WorkKind,
                ordinal AS Ordinal,
                root_work_id AS RootWorkId,
                asset_id AS AssetId,
                first_claimed_at AS FirstClaimedAt
            FROM ranked_assets
            WHERE asset_rank = 1
            ORDER BY
                CASE WHEN first_claimed_at IS NULL THEN 1 ELSE 0 END,
                first_claimed_at DESC,
                work_id
            LIMIT @LimitPlusOne OFFSET @Offset;
            """,
            new
            {
                LimitPlusOne = page.Limit + 1,
                page.Offset,
                RestrictWorks = allowedWorkIds is null ? 0 : 1,
                AllowedWorkIds = GuidSql.ToJsonHexArray(allowedWorkIds ?? new HashSet<Guid>()),
            },
            cancellationToken: ct)).ConfigureAwait(false)).ToList();

        if (workRows.Count == 0)
        {
            return new PagedResponse<LibraryWorkListItemDto>([], page.Offset, page.Limit, false);
        }

        var assetCanonicalValues = await LoadCanonicalValuesAsync(
            conn,
            workRows.Select(row => row.AssetId),
            ct).ConfigureAwait(false);
        var rootCanonicalValues = await LoadCanonicalValuesAsync(
            conn,
            workRows.Select(row => row.RootWorkId),
            ct).ConfigureAwait(false);
        var authorArrays = await LoadCanonicalArraysAsync(
            conn,
            workRows.Select(row => row.RootWorkId),
            "author",
            ct).ConfigureAwait(false);

        var items = workRows.Select(row => ComposeItem(
            row,
            assetCanonicalValues,
            rootCanonicalValues,
            authorArrays)).ToList();
        return PagedResponse<LibraryWorkListItemDto>.FromPage(items, page);
    }

    private static LibraryWorkListItemDto ComposeItem(
        LibraryWorkFeedRow row,
        IReadOnlyDictionary<Guid, Dictionary<string, string>> assetCanonicalValues,
        IReadOnlyDictionary<Guid, Dictionary<string, string>> rootCanonicalValues,
        IReadOnlyDictionary<Guid, List<string>> authorArrays)
    {
        assetCanonicalValues.TryGetValue(row.AssetId, out var assetValues);
        rootCanonicalValues.TryGetValue(row.RootWorkId, out var rootValues);

        var canonicalValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        MergeCanonicalValues(canonicalValues, assetValues, overwriteExisting: true);
        MergeCanonicalValues(canonicalValues, rootValues, overwriteExisting: false);

        if (!canonicalValues.ContainsKey("author")
            && authorArrays.TryGetValue(row.RootWorkId, out var authors)
            && authors.Count > 0)
        {
            canonicalValues["author"] = string.Join("; ", authors);
        }

        AddManagedArtworkFallback(canonicalValues, assetValues, rootValues, row.AssetId, "cover");
        AddManagedArtworkFallback(canonicalValues, assetValues, rootValues, row.AssetId, "background");
        AddManagedArtworkFallback(canonicalValues, assetValues, rootValues, row.AssetId, "banner");
        AddManagedArtworkFallback(canonicalValues, assetValues, rootValues, row.AssetId, "logo");

        return new LibraryWorkListItemDto
        {
            Id = row.WorkId,
            CollectionId = row.CollectionId,
            RootWorkId = row.RootWorkId,
            MediaType = row.MediaType,
            WorkKind = row.WorkKind,
            Ordinal = row.Ordinal,
            WikidataQid = GetCanonicalValue(canonicalValues, "wikidata_qid"),
            AssetId = row.AssetId,
            CreatedAt = row.FirstClaimedAt,
            CoverUrl = ResolveArtworkUrl(canonicalValues, assetValues, rootValues, row.AssetId, "cover"),
            BackgroundUrl = ResolveArtworkUrl(canonicalValues, assetValues, rootValues, row.AssetId, "background"),
            BannerUrl = ResolveArtworkUrl(canonicalValues, assetValues, rootValues, row.AssetId, "banner"),
            HeroUrl = null,
            LogoUrl = ResolveArtworkUrl(canonicalValues, assetValues, rootValues, row.AssetId, "logo"),
            CanonicalValues = canonicalValues,
        };
    }

    private static async Task<Dictionary<Guid, Dictionary<string, string>>> LoadCanonicalValuesAsync(
        System.Data.IDbConnection conn,
        IEnumerable<Guid> entityIds,
        CancellationToken ct)
    {
        var rows = new List<LibraryCanonicalValueRow>();
        foreach (var batch in entityIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Chunk(MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            rows.AddRange(await conn.QueryAsync<LibraryCanonicalValueRow>(new CommandDefinition(
                """
                SELECT entity_id AS EntityId, key AS [Key], value AS Value
                FROM canonical_values
                WHERE entity_id IN @ids;
                """,
                new { ids = batch.Select(GuidSql.ToBlob).ToArray() },
                cancellationToken: ct)).ConfigureAwait(false));
        }

        return rows
            .GroupBy(row => row.EntityId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .GroupBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        keyGroup => keyGroup.Key,
                        keyGroup => keyGroup.Select(item => item.Value)
                            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty,
                        StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<Dictionary<Guid, List<string>>> LoadCanonicalArraysAsync(
        System.Data.IDbConnection conn,
        IEnumerable<Guid> entityIds,
        string key,
        CancellationToken ct)
    {
        var rows = new List<LibraryCanonicalArrayRow>();
        foreach (var batch in entityIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Chunk(MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            rows.AddRange(await conn.QueryAsync<LibraryCanonicalArrayRow>(new CommandDefinition(
                """
                SELECT entity_id AS EntityId, value AS Value, ordinal AS Ordinal
                FROM canonical_value_arrays
                WHERE entity_id IN @ids
                  AND key = @key
                ORDER BY entity_id, ordinal;
                """,
                new { ids = batch.Select(GuidSql.ToBlob).ToArray(), key },
                cancellationToken: ct)).ConfigureAwait(false));
        }

        return rows
            .GroupBy(row => row.EntityId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Where(row => !string.IsNullOrWhiteSpace(row.Value))
                    .OrderBy(row => row.Ordinal)
                    .Select(row => row.Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList());
    }

    private static void MergeCanonicalValues(
        IDictionary<string, string> target,
        IReadOnlyDictionary<string, string>? source,
        bool overwriteExisting)
    {
        if (source is null)
        {
            return;
        }

        foreach (var (key, value) in source)
        {
            if (!string.IsNullOrWhiteSpace(value) && (overwriteExisting || !target.ContainsKey(key)))
            {
                target[key] = value;
            }
        }
    }

    private static void AddManagedArtworkFallback(
        IDictionary<string, string> canonicalValues,
        IReadOnlyDictionary<string, string>? assetValues,
        IReadOnlyDictionary<string, string>? rootValues,
        Guid assetId,
        string routeSegment)
    {
        if (!canonicalValues.ContainsKey(routeSegment)
            && !canonicalValues.ContainsKey($"{routeSegment}_url")
            && HasPresentArtwork(assetValues, rootValues, $"{routeSegment}_state"))
        {
            canonicalValues[routeSegment] = $"/stream/{assetId}/{routeSegment}";
        }
    }

    private static bool HasPresentArtwork(
        IReadOnlyDictionary<string, string>? assetValues,
        IReadOnlyDictionary<string, string>? rootValues,
        string key) =>
        string.Equals(GetCanonicalValue(assetValues, key), "present", StringComparison.OrdinalIgnoreCase)
        || string.Equals(GetCanonicalValue(rootValues, key), "present", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveArtworkUrl(
        IReadOnlyDictionary<string, string> canonicalValues,
        IReadOnlyDictionary<string, string>? assetValues,
        IReadOnlyDictionary<string, string>? rootValues,
        Guid assetId,
        string routeSegment)
    {
        var canonical = GetCanonicalValue(canonicalValues, $"{routeSegment}_url")
            ?? GetCanonicalValue(canonicalValues, routeSegment);
        return !string.IsNullOrWhiteSpace(canonical)
            ? canonical
            : HasPresentArtwork(assetValues, rootValues, $"{routeSegment}_state")
                ? $"/stream/{assetId}/{routeSegment}"
                : null;
    }

    private static string? GetCanonicalValue(
        IReadOnlyDictionary<string, string>? values,
        string key) =>
        values is not null
        && values.TryGetValue(key, out var value)
        && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private sealed class LibraryWorkFeedRow
    {
        public Guid WorkId { get; init; }
        public Guid? CollectionId { get; init; }
        public string MediaType { get; init; } = string.Empty;
        public string? WorkKind { get; init; }
        public int? Ordinal { get; init; }
        public Guid RootWorkId { get; init; }
        public Guid AssetId { get; init; }
        public string? FirstClaimedAt { get; init; }
    }

    private sealed class LibraryCanonicalValueRow
    {
        public Guid EntityId { get; init; }
        public string Key { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
    }

    private sealed class LibraryCanonicalArrayRow
    {
        public Guid EntityId { get; init; }
        public string Value { get; init; } = string.Empty;
        public int Ordinal { get; init; }
    }
}
