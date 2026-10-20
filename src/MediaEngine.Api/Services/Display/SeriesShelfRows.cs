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
    /// Splits one shelf's rows. Non-TV rows are taken once per work and sorted with <see cref="SeriesOrdering"/>; a
    /// supplementary, collected or broader-context member of a Book, Comic or Movie Series goes to <see cref="SeriesShelfRows.Also"/>.
    /// TV rows are returned as given.
    /// </summary>
    public static SeriesShelfRows Split(
        IReadOnlyList<DisplayWorkRow> rows,
        IReadOnlyDictionary<Guid, ManifestItem> manifestByWork)
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
                DisplayMediaRules.ParseDouble(row.SeriesPosition),
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
}
