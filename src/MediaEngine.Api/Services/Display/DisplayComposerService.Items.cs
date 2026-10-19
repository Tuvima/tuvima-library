using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Display;

public sealed partial class DisplayComposerService
{
    /// <summary>
    /// Turns item ids into the same cards the lane browse pages return: a Work id gives that Work's card, a show's or album's
    /// root id gives the show or album card. Every row comes from the authorized projection the browse reads use, so the
    /// profile's content limit, library and lane grants apply exactly as they do on a lane page. An id the viewer cannot see,
    /// an id that is not a catalogue item (a Collection, a View document) and an id that does not exist all get the same answer:
    /// no card. Nothing in the reply says which of those it was.
    /// </summary>
    public async Task<DisplayPageDto> BuildItemsAsync(IReadOnlyList<Guid> ids, Guid? profileId = null, CancellationToken ct = default)
    {
        if (ids.Count > DisplayItemsRequest.MaxIds)
        {
            throw new ArgumentException($"Ask for at most {DisplayItemsRequest.MaxIds} items at a time.", nameof(ids));
        }

        var requested = ids.Where(id => id != Guid.Empty).Distinct().ToList();
        var cards = new List<DisplayCardDto>();
        if (requested.Count > 0)
        {
            var worksTask = _readService.LoadWorksAsync(ct);
            var statesTask = _readService.LoadStatesAsync(profileId, null, ct);
            var favoritesTask = _readService.LoadFavoriteWorkIdsAsync(profileId, ct);
            await Task.WhenAll(worksTask, statesTask, favoritesTask);

            var works = await worksTask;
            var progressByWork = LatestProgressByWork(await statesTask);
            var favoriteWorkIds = await favoritesTask;
            var byWork = works.GroupBy(work => work.WorkId).ToDictionary(group => group.Key, group => group.First());
            var wanted = requested.ToHashSet();

            // Shows and albums are found by their root id; only the groups someone asked about are built.
            var showWorks = works
                .Where(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "TV")
                .Where(work => wanted.Contains(TvGroupId(work)) && !byWork.ContainsKey(TvGroupId(work)))
                .ToList();
            var showCards = showWorks.Count == 0
                ? new Dictionary<Guid, DisplayCardDto>()
                : _cards.BuildTvShowCards(showWorks, progressByWork).ToDictionary(card => card.Id);

            var musicWorks = works
                .Where(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "Music")
                .ToList();
            var albumKeys = musicWorks
                .Where(work => work.RootWorkId != Guid.Empty && wanted.Contains(work.RootWorkId) && !byWork.ContainsKey(work.RootWorkId))
                .Select(AlbumGroupKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var albumCards = albumKeys.Count == 0
                ? new Dictionary<Guid, DisplayCardDto>()
                : BuildMusicAlbumCards(musicWorks.Where(work => albumKeys.Contains(AlbumGroupKey(work))).ToList())
                    .GroupBy(card => card.Id)
                    .ToDictionary(group => group.Key, group => group.First());

            foreach (var id in requested)
            {
                if (byWork.TryGetValue(id, out var row))
                {
                    cards.Add(MarkFavorite(
                        _cards.FromWork(row, LaneFor(row.MediaType), progressByWork.GetValueOrDefault(row.WorkId)),
                        favoriteWorkIds));
                }
                else if (showCards.TryGetValue(id, out var show))
                {
                    cards.Add(show);
                }
                else if (albumCards.TryGetValue(id, out var album))
                {
                    cards.Add(album);
                }
            }
        }

        return new DisplayPageDto(
            Key: "items",
            Title: "Items",
            Subtitle: null,
            Hero: null,
            Shelves: [],
            Catalog: cards);
    }

    private static Guid TvGroupId(DisplayWorkRow work) =>
        work.RootWorkId != Guid.Empty ? work.RootWorkId : work.CollectionId ?? work.WorkId;

    private static string LaneFor(string mediaType) =>
        DisplayMediaRules.IsWatchKind(mediaType) ? "watch"
        : DisplayMediaRules.IsReadKind(mediaType) ? "read"
        : DisplayMediaRules.IsListenKind(mediaType) ? "listen"
        : "browse";
}
