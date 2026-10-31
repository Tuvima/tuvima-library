using MediaEngine.Api.Models;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Display;

public sealed class DisplayHomeCollectionProjectionReader
{
    private const string HomeLocation = "home";

    private readonly ICollectionPlacementRepository _placements;
    private readonly CollectionCatalogReadService _catalog;
    private readonly IProfileRepository _profiles;
    private readonly CollectionLaneReadService? _lanes;

    public DisplayHomeCollectionProjectionReader(
        ICollectionPlacementRepository placements,
        CollectionCatalogReadService catalog,
        IProfileRepository profiles,
        CollectionLaneReadService? lanes = null)
    {
        _placements = placements;
        _catalog = catalog;
        _profiles = profiles;
        _lanes = lanes;
    }

    /// <summary>
    /// The collections shown as lanes at <paramref name="location"/> for one profile (Collections and lists spec D9 to D11).
    /// The profile's hides, the seasonal window, the minimum, the Kids levels (through <paramref name="allowedWorkIds"/>)
    /// and who owns a private collection are all settled by the lane read service; this only shapes its answer for the composer.
    /// </summary>
    public async Task<IReadOnlyList<DisplayCollectionLaneRow>> LoadLanesAsync(
        Guid? profileId,
        string location,
        CancellationToken ct,
        IReadOnlySet<Guid>? allowedWorkIds = null)
    {
        if (_lanes is null)
        {
            return [];
        }

        var activeProfile = await ResolveActiveProfileAsync(profileId, ct);
        var lanes = await _lanes.GetLanesAsync(activeProfile, location, allowedWorkIds, ct);
        return lanes
            .Select(lane => new DisplayCollectionLaneRow
            {
                CollectionId = lane.CollectionId,
                Title = lane.Name,
                Subtitle = null,
                WorkIds = lane.Items.Select(item => item.WorkId).ToList(),
            })
            .ToList();
    }

    public async Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadAsync(
        Guid? profileId,
        CancellationToken ct,
        IReadOnlySet<Guid>? allowedWorkIds = null)
    {
        var placements = await _placements.GetByLocationAsync(HomeLocation, ct);
        if (placements.Count == 0)
        {
            return [];
        }

        var activeProfile = await ResolveActiveProfileAsync(profileId, ct);
        var catalog = await _catalog.GetCatalogAsync(activeProfile, ct, allowedWorkIds);
        var catalogById = catalog.ToDictionary(item => item.Id);

        var rows = new List<DisplayHomeCollectionRow>();
        foreach (var placement in placements.OrderBy(item => item.Position))
        {
            if (!catalogById.TryGetValue(placement.CollectionId, out var collection)
                || collection.ItemCount <= 0)
            {
                continue;
            }

            rows.Add(ToRow(collection));
        }

        return rows;
    }

    private async Task<Profile?> ResolveActiveProfileAsync(Guid? profileId, CancellationToken ct)
    {
        if (!profileId.HasValue)
        {
            return null;
        }

        return await _profiles.GetByIdAsync(profileId.Value, ct);
    }

    private static DisplayHomeCollectionRow ToRow(CollectionManagementCatalogDto collection)
    {
        var artwork = collection.ArtworkItems.FirstOrDefault();
        var primaryArtwork = StringHelpers.FirstNonBlank(collection.CoverArtworkUrl, artwork?.CoverUrl);
        return new DisplayHomeCollectionRow
        {
            CollectionId = collection.Id,
            Title = collection.Name,
            Subtitle = collection.Description,
            CollectionType = collection.CollectionType,
            PrimaryLane = collection.PrimaryLane,
            ItemCount = collection.ItemCount,
            WatchCount = collection.WatchCount,
            ReadCount = collection.ReadCount,
            ListenCount = collection.ListenCount,
            OtherCount = collection.OtherCount,
            EarliestYear = collection.EarliestYear,
            LatestYear = collection.LatestYear,
            PreviewItems = collection.ArtworkItems
                .Where(item => !string.IsNullOrWhiteSpace(item.CoverUrl))
                .Select(item => new DisplayCardPreviewItemDto(
                    item.WorkId,
                    null,
                    item.Title,
                    item.CoverUrl!,
                    item.ArtworkShape,
                    null,
                    item.MediaType,
                    DisplayCardBuilder.PreviewWebUrlFor(item.WorkId, item.MediaType),
                    item.Description,
                    item.Facts))
                .ToList(),
            CreatedAt = collection.ModifiedAt ?? collection.CreatedAt,
            BackgroundUrl = primaryArtwork,
            BackgroundSmallUrl = primaryArtwork,
            BackgroundMediumUrl = primaryArtwork,
            BackgroundLargeUrl = primaryArtwork,
            AccentColor = collection.ArtworkPalette.AccentColor,
        };
    }

}
