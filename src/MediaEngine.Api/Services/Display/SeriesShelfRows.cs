using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using ManifestItem = MediaEngine.Domain.Entities.SeriesManifestItemRecord;

namespace MediaEngine.Api.Services.Display;

/// <summary>
/// One Series shelf's visible rows split the way every surface shows them: TV rows, the Series run in the shared Series
/// order, and the "Also in this world" members after it. <see cref="Kinds"/> lists the distinct display kinds in order of
/// first appearance (TV first, then the Series rows in Series order).
/// </summary>
public sealed record SeriesShelfRows(
    IReadOnlyList<DisplayWorkRow> TvRows,
    IReadOnlyList<DisplayWorkRow> Run,
    IReadOnlyList<DisplayWorkRow> Also,
    IReadOnlyList<string> Kinds);

/// <summary>
/// The one place that splits a Series shelf into its run and "Also in this world", so the Universe read and the shelf's
/// see-all list (<c>/api/v1/display/groups/{id}</c>) always agree on order and count.
/// </summary>
public static class SeriesShelfSplit
{
    /// <summary>The manifest row that places each owned work on each of these shelves, in one read.</summary>
    public static async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, ManifestItem>>> LoadPlacementsAsync(
        ISeriesManifestRepository? manifests,
        IReadOnlyCollection<Guid> shelfIds,
        CancellationToken ct)
    {
        if (manifests is null || shelfIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyDictionary<Guid, ManifestItem>>();
        }

        var rows = await manifests.GetLinkedItemsByCollectionIdsAsync(shelfIds, ct).ConfigureAwait(false);
        return rows
            .GroupBy(item => item.CollectionId)
            .ToDictionary(
                shelf => shelf.Key,
                shelf => (IReadOnlyDictionary<Guid, ManifestItem>)shelf
                    .GroupBy(item => item.LinkedWorkId!.Value)
                    .ToDictionary(
                        group => group.Key,
                        group => group
                            .OrderBy(item => item.IsExpandedFromCollection)
                            .ThenBy(item => SeriesPlacementRule.ScopeRank(item.MembershipScope))
                            .First()));
    }

    /// <summary>
    /// The (Series, position) pairs the listed works and their files store, keyed by entity, so a work in two Series is
    /// ordered by this shelf's own number on every surface that lists the shelf.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalArrayEntry>>> LoadSeriesPairsAsync(
        ICanonicalValueArrayRepository? arrays,
        IReadOnlyList<DisplayWorkRow> rows,
        CancellationToken ct)
    {
        if (arrays is null || rows.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<CanonicalArrayEntry>>();
        }

        var entityIds = rows.SelectMany(row => new[] { row.WorkId, row.AssetId }).Distinct().ToList();
        var byEntity = await arrays.GetAllByEntitiesAsync(entityIds, ct).ConfigureAwait(false);
        var pairs = new Dictionary<Guid, IReadOnlyList<CanonicalArrayEntry>>();
        foreach (var (entityId, keys) in byEntity)
        {
            if (keys.TryGetValue(MetadataFieldConstants.SeriesPlacement, out var entries) && entries.Count > 0)
            {
                pairs[entityId] = entries;
            }
        }

        return pairs;
    }

    /// <summary>
    /// Splits one shelf's rows. Non-TV rows are taken once per work and sorted with <see cref="SeriesOrdering"/>; a
    /// supplementary, collected or broader-context member of a Book, Comic or Movie Series goes to <see cref="SeriesShelfRows.Also"/>.
    /// TV rows are returned as given.
    /// </summary>
    /// <param name="rows">The shelf's visible rows.</param>
    /// <param name="manifestByWork">The manifest row placing each owned work on this shelf.</param>
    /// <param name="seriesQid">The shelf's Series QID, so a work's own position is the one it holds in this Series.</param>
    /// <param name="seriesPairs">The stored (Series, position) pairs from <see cref="LoadSeriesPairsAsync"/>.</param>
    public static SeriesShelfRows Split(
        IReadOnlyList<DisplayWorkRow> rows,
        IReadOnlyDictionary<Guid, ManifestItem> manifestByWork,
        string? seriesQid = null,
        IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalArrayEntry>>? seriesPairs = null)
    {
        var kinds = new List<string>();
        var tvRows = rows.Where(row => DisplayMediaRules.NormalizeDisplayKind(row.MediaType) == "TV").ToList();
        if (tvRows.Count > 0)
        {
            kinds.Add("TV");
        }

        // The same Series ordering as the shelf and the item page: the manifest's ordinal, then the work's own decimal
        // position, then publication date, then title, with "Also in this world" members after the Series run.
        var seriesRows = rows
            .Where(row => DisplayMediaRules.NormalizeDisplayKind(row.MediaType) != "TV")
            .GroupBy(row => row.WorkId)
            .Select(group => group.First())
            .ToList();
        var ordered = SeriesOrdering.Order(seriesRows, row =>
        {
            manifestByWork.TryGetValue(row.WorkId, out var placement);
            return SeriesOrdering.BuildInput(
                placement?.MembershipScope,
                placement?.ParsedOrdinal,
                placement?.OrdinalScopeQid,
                placement?.SeriesQid,
                SeriesPositionPairs.SortValueOf(OwnPosition(row, seriesQid, seriesPairs)),
                row.Year,
                row.SortTitle ?? row.Title);
        });

        var run = new List<DisplayWorkRow>();
        var also = new List<DisplayWorkRow>();
        foreach (var row in ordered)
        {
            var kind = DisplayMediaRules.NormalizeDisplayKind(row.MediaType);
            if (!kinds.Contains(kind, StringComparer.Ordinal))
            {
                kinds.Add(kind);
            }

            // The same split the item page uses: supplementary, collected and broader members are "Also in this world".
            manifestByWork.TryGetValue(row.WorkId, out var placement);
            if (SeriesOrdering.SplitsAlsoInThisWorld(kind) && SeriesOrdering.IsAlsoInThisWorld(placement?.MembershipScope))
            {
                also.Add(row);
            }
            else
            {
                run.Add(row);
            }
        }

        return new SeriesShelfRows(tvRows, run, also, kinds);
    }

    // The work's own position in this Series: the pair its file or its work stores for the Series, else today's single number.
    private static string? OwnPosition(
        DisplayWorkRow row,
        string? seriesQid,
        IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalArrayEntry>>? seriesPairs)
    {
        var fromAsset = SeriesPositionPairs.PositionFor(
            seriesQid, seriesPairs?.GetValueOrDefault(row.AssetId), fallbackPosition: null);
        return fromAsset ?? SeriesPositionPairs.PositionFor(
            seriesQid, seriesPairs?.GetValueOrDefault(row.WorkId), row.SeriesPosition);
    }
}
