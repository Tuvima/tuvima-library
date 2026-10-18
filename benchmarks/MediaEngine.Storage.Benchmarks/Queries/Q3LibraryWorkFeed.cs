using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Paging;
using MediaEngine.Storage;
using MediaEngine.Storage.Benchmarks.Ef;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaEngine.Storage.Benchmarks.Queries;

using Result = PagedResponse<LibraryWorkListItemDto>;

/// <summary>
/// Q3: <c>LibraryWorkFeedReadService.GetWorksAsync</c> (src/MediaEngine.Api/Services/ReadServices/LibraryWorkFeedReadService.cs:18)
/// for a first page and a deep page. A window-function paging query (MIN OVER, ROW_NUMBER OVER) followed by three
/// batched follow-up loads (canonical values for assets, for roots, and the author array) and in-memory composition.
///
/// The EF variants share the production composition code (copied below, because it is private to the service).
/// </summary>
internal static class Q3LibraryWorkFeed
{
    // Same constant as the service (private there).
    private const int MaxParametersPerQuery = 400;

    /// <summary>Production code, called as the Engine calls it.</summary>
    public static Task<Result> Dapper(BenchContext context, PagedRequest page) =>
        new LibraryWorkFeedReadService(context.Database).GetWorksAsync(page);

    /// <summary>
    /// The production paging SQL through EF raw SQL, then the three follow-up loads also as raw SQL, then the
    /// production composition.
    /// </summary>
    public static async Task<Result> EfRaw(BenchContext context, PagedRequest page)
    {
        using var lease = context.Ef.Rent();
        var workRows = await LoadPageAsync(lease.Context, page);
        if (workRows.Count == 0)
        {
            return new Result([], page.Offset, page.Limit, false);
        }

        var assetValues = Compose.GroupValues(await RawCanonicalValuesAsync(lease.Context, workRows.Select(row => row.AssetId)));
        var rootValues = Compose.GroupValues(await RawCanonicalValuesAsync(lease.Context, workRows.Select(row => row.RootWorkId)));
        var authors = Compose.GroupArrays(await RawAuthorArraysAsync(lease.Context, workRows.Select(row => row.RootWorkId)));
        return Result.FromPage(workRows.Select(row => Compose.Item(row, assetValues, rootValues, authors)).ToList(), page);
    }

    /// <summary>
    /// The paging query stays raw SQL (EF Core cannot translate window functions; see README, "Not practical"),
    /// while the three follow-up loads are idiomatic LINQ. This is the part of Q3 that LINQ can express.
    /// </summary>
    public static async Task<Result> EfLinq(BenchContext context, PagedRequest page)
    {
        using var lease = context.Ef.Rent();
        var workRows = await LoadPageAsync(lease.Context, page);
        if (workRows.Count == 0)
        {
            return new Result([], page.Offset, page.Limit, false);
        }

        var assetValues = Compose.GroupValues(await LinqCanonicalValuesAsync(lease.Context, workRows.Select(row => row.AssetId)));
        var rootValues = Compose.GroupValues(await LinqCanonicalValuesAsync(lease.Context, workRows.Select(row => row.RootWorkId)));
        var authors = Compose.GroupArrays(await LinqAuthorArraysAsync(lease.Context, workRows.Select(row => row.RootWorkId)));
        return Result.FromPage(workRows.Select(row => Compose.Item(row, assetValues, rootValues, authors)).ToList(), page);
    }

    public static IEnumerable<string> Lines(Result result)
    {
        yield return $"offset={result.Offset} limit={result.Limit} hasMore={result.HasMore} count={result.Items.Count} next={result.NextCursor}";
        foreach (var item in result.Items)
        {
            yield return Describe.Row(item);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Paging query. Copied from LibraryWorkFeedReadService.GetWorksAsync at commit 2210bc763. The only change: the
    // production text ends its filter with "(@RestrictWorks = 0 OR w.id IN @AllowedWorkIds)", where Dapper expands the
    // empty list itself; the equivalent expansion is written out here, and RestrictWorks is always 0.
    // ---------------------------------------------------------------------------------------------------------
    private static async Task<List<FeedRow>> LoadPageAsync(BenchDbContext context, PagedRequest page)
    {
        var visibleWorkPredicate = HomeVisibilitySql.VisibleWorkPredicate("w.id", "w.curator_state", "w.is_catalog_only");
        var visibleAssetPredicate = HomeVisibilitySql.VisibleAssetPathPredicate("ad.file_path_root");
        var sql = $"""
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
                  AND (@RestrictWorks = 0 OR w.id IN (SELECT NULL WHERE 1 = 0))
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
            LIMIT @LimitPlusOne OFFSET @Offset
            """;

#pragma warning disable EF1002 // the interpolated text is only the library's own visibility predicates, no caller input
        // Boxed on purpose: a literal 0 would bind to the SqliteParameter(string, SqliteType) overload and carry no value.
        object restrictWorks = 0L;
        return await context.Set<FeedRow>()
            .FromSqlRaw(
                sql,
                new SqliteParameter("@LimitPlusOne", page.Limit + 1),
                new SqliteParameter("@Offset", page.Offset),
                new SqliteParameter("@RestrictWorks", restrictWorks))
            .ToListAsync();
#pragma warning restore EF1002
    }

    // ---------------------------------------------------------------------------------------------------------
    // Follow-up loads, raw SQL (production text with Dapper's "IN @ids" expansion written out)
    // ---------------------------------------------------------------------------------------------------------
    private static async Task<List<ValueRow>> RawCanonicalValuesAsync(BenchDbContext context, IEnumerable<Guid> entityIds)
    {
        var rows = new List<ValueRow>();
        foreach (var batch in entityIds.Where(id => id != Guid.Empty).Distinct().Chunk(MaxParametersPerQuery))
        {
            var (placeholders, parameters) = InList(batch);
#pragma warning disable EF1003 // placeholders only: generated '@id<n>' names
            rows.AddRange(await context.Set<ValueRow>()
                .FromSqlRaw(
                    "SELECT entity_id AS EntityId, key AS [Key], value AS Value FROM canonical_values WHERE entity_id IN (" + placeholders + ")",
                    parameters)
                .ToListAsync());
#pragma warning restore EF1003
        }

        return rows;
    }

    private static async Task<List<ArrayRow>> RawAuthorArraysAsync(BenchDbContext context, IEnumerable<Guid> entityIds)
    {
        var rows = new List<ArrayRow>();
        foreach (var batch in entityIds.Where(id => id != Guid.Empty).Distinct().Chunk(MaxParametersPerQuery))
        {
            var (placeholders, parameters) = InList(batch);
            var all = new List<object>(parameters) { new SqliteParameter("@key", "author") };
#pragma warning disable EF1003 // placeholders only: generated '@id<n>' names
            rows.AddRange(await context.Set<ArrayRow>()
                .FromSqlRaw(
                    "SELECT entity_id AS EntityId, value AS Value, ordinal AS Ordinal FROM canonical_value_arrays WHERE entity_id IN ("
                        + placeholders + ") AND key = @key ORDER BY entity_id, ordinal",
                    all.ToArray())
                .ToListAsync());
#pragma warning restore EF1003
        }

        return rows;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Follow-up loads, LINQ
    // ---------------------------------------------------------------------------------------------------------
    private static async Task<List<ValueRow>> LinqCanonicalValuesAsync(BenchDbContext context, IEnumerable<Guid> entityIds)
    {
        var rows = new List<ValueRow>();
        foreach (var batch in entityIds.Where(id => id != Guid.Empty).Distinct().Chunk(MaxParametersPerQuery))
        {
            var ids = batch;
            rows.AddRange(await context.CanonicalValues
                .AsNoTracking()
                .Where(value => ids.Contains(value.EntityId))
                .Select(value => new ValueRow { EntityId = value.EntityId, Key = value.Key, Value = value.Value })
                .ToListAsync());
        }

        return rows;
    }

    private static async Task<List<ArrayRow>> LinqAuthorArraysAsync(BenchDbContext context, IEnumerable<Guid> entityIds)
    {
        var rows = new List<ArrayRow>();
        foreach (var batch in entityIds.Where(id => id != Guid.Empty).Distinct().Chunk(MaxParametersPerQuery))
        {
            var ids = batch;
            rows.AddRange(await context.CanonicalValueArrays
                .AsNoTracking()
                .Where(row => ids.Contains(row.EntityId) && row.Key == "author")
                .OrderBy(row => row.EntityId)
                .ThenBy(row => row.Ordinal)
                .Select(row => new ArrayRow { EntityId = row.EntityId, Value = row.Value, Ordinal = row.Ordinal })
                .ToListAsync());
        }

        return rows;
    }

    private static (string Placeholders, object[] Parameters) InList(Guid[] batch)
    {
        var placeholders = new string[batch.Length];
        var parameters = new object[batch.Length];
        for (var i = 0; i < batch.Length; i++)
        {
            placeholders[i] = "@id" + i;
            parameters[i] = new SqliteParameter("@id" + i, GuidSql.ToBlob(batch[i]));
        }

        return (string.Join(", ", placeholders), parameters);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Row shapes (private nested classes in the production service)
    // ---------------------------------------------------------------------------------------------------------
    internal sealed class FeedRow
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

    internal sealed class ValueRow
    {
        public Guid EntityId { get; init; }

        public string Key { get; init; } = string.Empty;

        public string Value { get; init; } = string.Empty;
    }

    internal sealed class ArrayRow
    {
        public Guid EntityId { get; init; }

        public string Value { get; init; } = string.Empty;

        public int Ordinal { get; init; }
    }

    /// <summary>
    /// The production composition code, copied from LibraryWorkFeedReadService (commit 2210bc763) because it is
    /// private there. Behaviour is unchanged; only the row inputs differ in type.
    /// </summary>
    private static class Compose
    {
        public static Dictionary<Guid, Dictionary<string, string>> GroupValues(List<ValueRow> rows) =>
            rows
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

        public static Dictionary<Guid, List<string>> GroupArrays(List<ArrayRow> rows) =>
            rows
                .GroupBy(row => row.EntityId)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Where(row => !string.IsNullOrWhiteSpace(row.Value))
                        .OrderBy(row => row.Ordinal)
                        .Select(row => row.Value)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList());

        public static LibraryWorkListItemDto Item(
            FeedRow row,
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
    }
}
