using Dapper;
using MediaEngine.Contracts.Details;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.Details.Internals;

internal sealed partial class DetailCompositionOrchestrator
{
    /// <summary>
    /// Marks the title, and the child rows the page shows, that were added to their album, season, series or audiobook anyway
    /// (Details overlay D13), so the page can show "Not matched" and offer a later Match. One look-up for the whole page.
    /// </summary>
    private async Task StampUnmatchedAsync(DetailPageViewModel model, CancellationToken ct)
    {
        var sequenceItems = EnumerateSequenceItems(model).ToList();
        var groupItems = model.MediaGroups.SelectMany(group => group.Items).ToList();

        var workIds = new HashSet<Guid>();
        if (Guid.TryParse(model.Id, out var pageId))
        {
            workIds.Add(pageId);
        }

        foreach (var item in sequenceItems)
        {
            if (Guid.TryParse(item.Id, out var id))
            {
                workIds.Add(id);
            }
        }

        var assetIds = new HashSet<Guid>();
        foreach (var item in groupItems)
        {
            if (Guid.TryParse(item.Id, out var id))
            {
                workIds.Add(id);
            }

            if (Guid.TryParse(item.AssetId, out var assetId))
            {
                assetIds.Add(assetId);
            }
        }

        if (workIds.Count == 0 && assetIds.Count == 0)
        {
            return;
        }

        using var conn = _db.CreateConnection();
        var unmatchedWorks = workIds.Count == 0
            ? []
            : (await conn.QueryAsync<Guid>(new CommandDefinition(
                """
                -- readiness: marks the titles a page already shows, looked up by id
                SELECT id FROM works WHERE placement_unmatched = 1 AND id IN @ids;
                """,
                new { ids = workIds.Select(GuidSql.ToBlob).ToArray() },
                cancellationToken: ct))).ToHashSet();
        // An audiobook part is one file (one Edition): its mark is on the Edition.
        var unmatchedAssets = assetIds.Count == 0
            ? []
            : (await conn.QueryAsync<Guid>(new CommandDefinition(
                """
                SELECT ma.id FROM media_assets ma
                JOIN editions e ON e.id = ma.edition_id
                WHERE e.placement_unmatched = 1 AND ma.id IN @ids;
                """,
                new { ids = assetIds.Select(GuidSql.ToBlob).ToArray() },
                cancellationToken: ct))).ToHashSet();

        model.IsUnmatched = unmatchedWorks.Contains(pageId);
        foreach (var item in sequenceItems)
        {
            item.IsUnmatched = Guid.TryParse(item.Id, out var id) && unmatchedWorks.Contains(id);
        }

        foreach (var item in groupItems)
        {
            item.IsUnmatched = (Guid.TryParse(item.Id, out var id) && unmatchedWorks.Contains(id))
                || (Guid.TryParse(item.AssetId, out var assetId) && unmatchedAssets.Contains(assetId));
        }

        PresentUnmatchedWithoutNumbers(model, sequenceItems, groupItems);
    }

    /// <summary>
    /// A title added anyway is never shown with a number, however a number reached the page (a file tag read again, a provider, a
    /// series list position), and it is listed in an "Unnumbered" group at the end of its list (Details overlay D13).
    /// </summary>
    private static void PresentUnmatchedWithoutNumbers(
        DetailPageViewModel model,
        IReadOnlyList<SequenceItemViewModel> sequenceItems,
        IReadOnlyList<MediaGroupingItemViewModel> groupItems)
    {
        foreach (var item in sequenceItems.Where(item => item.IsUnmatched))
        {
            item.PositionNumber = null;
            item.PositionSort = null;
            item.PositionLabel = null;
            item.PositionText = null;
        }

        foreach (var item in groupItems.Where(item => item.IsUnmatched))
        {
            item.TrackNumber = null;
        }

        if (model.IsUnmatched && model.SequencePlacement is { } own)
        {
            own.PositionNumber = null;
            own.PositionSort = null;
            own.PositionLabel = null;
            own.PositionText = null;
            own.PositionSummary = null;
        }

        if (model.SequencePlacement is { } placement)
        {
            var unnumbered = new List<SequenceItemViewModel>();
            var groups = placement.Groups.ToList();
            foreach (var group in groups)
            {
                var gone = group.Items.Where(item => item.IsUnmatched).ToList();
                if (gone.Count == 0)
                {
                    continue;
                }

                unnumbered.AddRange(gone);
                group.Items = [.. group.Items.Where(item => !item.IsUnmatched)];
                group.OwnedCount = Math.Max(0, group.OwnedCount - gone.Count(item => item.IsOwned));
                group.ItemCount = group.ItemCount > 0 ? Math.Max(group.Items.Count, group.ItemCount - gone.Count) : 0;
            }

            if (placement.OrderedItems.Any(item => item.IsUnmatched))
            {
                placement.OrderedItems = [.. placement.OrderedItems.Where(item => !item.IsUnmatched), .. placement.OrderedItems.Where(item => item.IsUnmatched)];
            }

            if (unnumbered.Count > 0)
            {
                groups.Add(new SequenceGroupViewModel
                {
                    Key = UnnumberedGroupKey,
                    Title = UnnumberedGroupTitle,
                    OwnedCount = unnumbered.Count(item => item.IsOwned),
                    ItemCount = unnumbered.Count,
                    Items = unnumbered,
                });
                placement.Groups = groups;
            }
        }

        var mediaGroups = model.MediaGroups.ToList();
        var unnumberedRows = new List<MediaGroupingItemViewModel>();
        foreach (var group in mediaGroups)
        {
            var gone = group.Items.Where(item => item.IsUnmatched).ToList();
            if (gone.Count == 0)
            {
                continue;
            }

            unnumberedRows.AddRange(gone);
            var owned = gone.Count(item => item.IsOwned);
            group.Items = [.. group.Items.Where(item => !item.IsUnmatched)];
            group.OwnedCount = Math.Max(0, group.OwnedCount - owned);
            group.TotalCount = Math.Max(group.OwnedCount, group.TotalCount - owned);
            group.CompletionPercent = group.TotalCount > 0 ? Math.Round(100d * group.OwnedCount / group.TotalCount, 1) : 0;
        }

        if (unnumberedRows.Count > 0)
        {
            mediaGroups.Add(new MediaGroupingViewModel
            {
                Key = UnnumberedGroupKey,
                Title = UnnumberedGroupTitle,
                Items = unnumberedRows,
                OwnedCount = unnumberedRows.Count(item => item.IsOwned),
                TotalCount = unnumberedRows.Count,
                MissingCount = 0,
                CompletionPercent = 100,
            });
            model.MediaGroups = mediaGroups;
        }
    }

    private const string UnnumberedGroupKey = "unnumbered";
    private const string UnnumberedGroupTitle = "Unnumbered";

    private static IEnumerable<SequenceItemViewModel> EnumerateSequenceItems(DetailPageViewModel model)
    {
        var placement = model.SequencePlacement;
        if (placement is null)
        {
            yield break;
        }

        yield return placement.CurrentItem;
        if (placement.PreviousItem is not null)
        {
            yield return placement.PreviousItem;
        }

        if (placement.NextItem is not null)
        {
            yield return placement.NextItem;
        }

        foreach (var item in placement.OrderedItems.Concat(placement.AlsoInThisWorld).Concat(placement.Groups.SelectMany(group => group.Items)))
        {
            yield return item;
        }
    }
}
