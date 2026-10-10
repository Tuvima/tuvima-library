namespace MediaEngine.Providers.Services;

public enum EpisodeRangeKind
{
    /// <summary>The file names a single episode; nothing to link.</summary>
    NotARange,

    /// <summary>An unbroken run of episodes that all exist at the TV provider.</summary>
    Clean,

    /// <summary>A range that cannot be linked safely; the file goes to Review.</summary>
    NeedsReview,
}

public sealed record EpisodeRangeDecision(EpisodeRangeKind Kind, IReadOnlyList<int> Episodes, string? Reason)
{
    public static EpisodeRangeDecision NotARange { get; } = new(EpisodeRangeKind.NotARange, [], null);

    public static EpisodeRangeDecision Review(string reason) => new(EpisodeRangeKind.NeedsReview, [], reason);
}

/// <summary>
/// Decides whether a multi-episode file (S01E01E02, 1x01-02, ...) may be linked to every
/// episode it names. Only unbroken, in-limit runs where every episode exists in the TV
/// provider's season list link automatically; anything else is a Review case.
/// </summary>
public static class TvEpisodeRangeCoverage
{
    /// <summary>Most episodes one file may cover; stops typos like E01E200 from linking.</summary>
    public const int MaxEpisodesPerFile = 6;

    /// <param name="firstEpisode">The file's first episode number.</param>
    /// <param name="episodeEnd">The raw <c>episode_end</c> hint, or null/blank for a single-episode file.</param>
    /// <param name="providerEpisodes">Episode numbers the provider lists for the season, or null when unavailable.</param>
    public static EpisodeRangeDecision Evaluate(
        int? firstEpisode,
        string? episodeEnd,
        IReadOnlySet<int>? providerEpisodes)
    {
        if (string.IsNullOrWhiteSpace(episodeEnd))
        {
            return EpisodeRangeDecision.NotARange;
        }

        if (!int.TryParse(episodeEnd, out var last) || firstEpisode is not { } first || first < 0)
        {
            return EpisodeRangeDecision.Review("The filename's episode range could not be read.");
        }

        if (last <= first)
        {
            return EpisodeRangeDecision.Review("The filename's episode range is not an ascending run.");
        }

        if (last - first + 1 > MaxEpisodesPerFile)
        {
            return EpisodeRangeDecision.Review(
                $"The filename covers more than {MaxEpisodesPerFile} episodes.");
        }

        if (providerEpisodes is null)
        {
            return EpisodeRangeDecision.Review("The provider's episode list for this season is unavailable.");
        }

        var episodes = Enumerable.Range(first, last - first + 1).ToArray();
        var missing = episodes.Where(episode => !providerEpisodes.Contains(episode)).ToArray();
        if (missing.Length > 0)
        {
            return EpisodeRangeDecision.Review(
                $"Episode(s) {string.Join(", ", missing)} are not in the provider's season list.");
        }

        return new EpisodeRangeDecision(EpisodeRangeKind.Clean, episodes, null);
    }
}
