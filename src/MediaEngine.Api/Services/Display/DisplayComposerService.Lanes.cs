using MediaEngine.Api.Services.Collections;
using MediaEngine.Contracts.Display;

namespace MediaEngine.Api.Services.Display;

public sealed partial class DisplayComposerService
{
    /// <summary>The shelf key of a collection lane is this prefix and the collection id; the Dashboard recognises a lane by it.</summary>
    public const string CollectionLaneShelfKeyPrefix = "lane-";

    /// <summary>
    /// Turns the collections shown as lanes (Collections and lists spec D9) into shelves of the same tiles every other row
    /// uses: up to 20 tiles and a View all tile that opens the collection page. A lane whose items have no tile for this
    /// profile (a title that left the library since the lane was read) is left out, never shown empty. The works are read
    /// once, narrowed to the lane items, and only when there is a lane to draw.
    /// </summary>
    private async Task<IReadOnlyList<DisplayShelfDto>> BuildCollectionLaneShelvesAsync(
        IReadOnlyList<DisplayCollectionLaneRow> lanes,
        string surface,
        IReadOnlyDictionary<Guid, DisplayJourneyRow> progressByWork,
        CancellationToken ct)
    {
        if (lanes.Count == 0)
        {
            return [];
        }

        var wanted = lanes.SelectMany(lane => lane.WorkIds).ToHashSet();
        var works = (await _readService.LoadWorksAsync(ct))
            .Where(work => wanted.Contains(work.WorkId)
                           || wanted.Contains(work.RootWorkId)
                           || work.CollectionId is { } collectionId && wanted.Contains(collectionId))
            .ToList();

        var pool = new Dictionary<Guid, DisplayCardDto>();
        foreach (var card in BuildLaneCardPool(works, surface, progressByWork))
        {
            pool.TryAdd(card.Id, card);
            if (card.WorkId is { } workId)
            {
                pool.TryAdd(workId, card);
            }
        }

        var shelves = new List<DisplayShelfDto>();
        foreach (var lane in lanes)
        {
            var cards = lane.WorkIds
                .Select(id => pool.GetValueOrDefault(id))
                .Where(card => card is not null)
                .Cast<DisplayCardDto>()
                .DistinctBy(card => card.Id)
                .Take(CollectionLanePolicy.LaneItemLimit)
                .ToList();
            DisplayShelfBuilder.AddShelf(
                shelves,
                $"{CollectionLaneShelfKeyPrefix}{lane.CollectionId:N}",
                lane.Title,
                lane.Subtitle ?? string.Empty,
                cards,
                $"/details/collection/{lane.CollectionId:D}?context=default");
        }

        return shelves.Select(shelf => string.IsNullOrEmpty(shelf.Subtitle) ? shelf with { Subtitle = null } : shelf).ToList();
    }

    // One tile per title the way each page already draws it: shows, videos and albums collapse their episodes and tracks.
    private IReadOnlyList<DisplayCardDto> BuildLaneCardPool(
        IReadOnlyList<DisplayWorkRow> works,
        string surface,
        IReadOnlyDictionary<Guid, DisplayJourneyRow> progressByWork) =>
        works
            .Where(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) is not ("TV" or "Music" or "Video"))
            .Select(work => _cards.FromWork(work, surface, progressByWork.GetValueOrDefault(work.WorkId)))
            .Concat(_cards.BuildTvShowCards(works, progressByWork).Select(card => card with { TileTextMode = "coverOnly" }))
            .Concat(_cards.BuildVideoCards(works, progressByWork))
            .Concat(BuildMusicAlbumCards(works
                .Where(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "Music")
                .ToList()))
            .ToList();
}
