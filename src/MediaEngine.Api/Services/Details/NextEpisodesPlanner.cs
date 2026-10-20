using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Details;

/// <summary>
/// Picks the owned episodes that follow a given episode file in a show's watch order. It uses the same ordering as
/// the show page's "next episode" choice (<see cref="TvEpisodeContextResolver.Order"/>), over works the caller has
/// already been authorized to see (library grants and the profile's content limit are applied before this runs).
/// </summary>
public static class NextEpisodesPlanner
{
    public const int DefaultCount = 3;
    public const int MaxCount = 10;

    public static int ClampCount(int? requested) => Math.Clamp(requested ?? DefaultCount, 1, MaxCount);

    /// <summary>
    /// Null when the show has no owned episodes in <paramref name="works"/> or none of them is the file
    /// <paramref name="afterAssetId"/>: unknown and not-allowed look the same. A combined file that covers several
    /// episodes counts once, and the episodes it covers are skipped, so the list starts after the whole file.
    /// </summary>
    public static NextEpisodesDto? Plan(Guid showId, Guid afterAssetId, int count, IReadOnlyList<DisplayWorkRow> works)
    {
        var episodes = works
            .Where(work => work.AssetId != Guid.Empty
                && DisplayMediaRules.NormalizeDisplayKind(work.MediaType) is "TV" or "Video"
                && (work.RootWorkId == Guid.Empty ? work.WorkId : work.RootWorkId) == showId)
            .DistinctBy(work => work.WorkId)
            .ToDictionary(work => work.WorkId.ToString("D"));
        if (episodes.Count == 0)
        {
            return null;
        }

        var ordered = TvEpisodeContextResolver.Order(episodes.Values.Select(work => new TvEpisodePlaybackCandidate(
                work.WorkId.ToString("D"), Number(work.SeasonNumber), Number(work.EpisodeNumber), 0, null)))
            .Select(candidate => episodes[candidate.Id])
            .ToList();
        var lastOfCurrentFile = ordered.FindLastIndex(work => work.AssetId == afterAssetId);
        if (lastOfCurrentFile < 0)
        {
            return null;
        }

        var next = ordered.Skip(lastOfCurrentFile + 1)
            .Where(work => work.AssetId != afterAssetId)
            .DistinctBy(work => work.AssetId)
            .Take(Math.Clamp(count, 1, MaxCount))
            .Select(work => new NextEpisodeDto
            {
                WorkId = work.WorkId,
                AssetId = work.AssetId,
                Title = work.Title,
                SeasonNumber = Number(work.SeasonNumber),
                EpisodeNumber = Number(work.EpisodeNumber),
                StillUrl = work.EpisodeStillUrl,
            })
            .ToList();
        return new NextEpisodesDto { ShowId = showId, AfterAssetId = afterAssetId, Episodes = next };
    }

    private static int? Number(string? number) => int.TryParse(number, out var value) ? value : null;
}
