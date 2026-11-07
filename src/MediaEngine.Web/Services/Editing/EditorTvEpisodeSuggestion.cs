using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Web.Services.Editing;

/// <summary>Where a pre-selected episode came from, so the Match tab can say so ("Suggested from filename: S02E03").</summary>
public static class EditorEpisodeSuggestionSources
{
    public const string FileName = "filename";
    public const string Absolute = "absolute";
    public const string Runtime = "runtime";
    public const string AirOrder = "air_order";
}

/// <summary>An episode picked for the person, with the reason it was picked.</summary>
public sealed record EditorEpisodeSuggestion(TvMatchBrowserEpisodeDto Episode, string Source);

/// <summary>
/// Which episode of a season the Match tab pre-selects (universal editor v5, 3c "Choose an episode"). The number in the file name comes
/// first; with none, the episode whose length is nearest the measured length; with neither, the first to air. The pick is advice: the
/// verdict says how well it fits and the person can choose another row.
/// </summary>
public static class EditorTvEpisodeSuggestion
{
    /// <summary>The episode with the file name's number, in the file name's season when it names one.</summary>
    public static EditorEpisodeSuggestion? FromFileName(IReadOnlyList<TvMatchBrowserEpisodeDto> episodes, EditorMatchContextDto? context)
    {
        if (context?.ParsedEpisode is not { } number)
        {
            return null;
        }

        var found = episodes.FirstOrDefault(episode => episode.EpisodeNumber == number && (context.ParsedSeason is null || episode.SeasonNumber == context.ParsedSeason));
        return found is null ? null : new EditorEpisodeSuggestion(found, EditorEpisodeSuggestionSources.FileName);
    }

    /// <summary>The episode a provider numbers as the file's absolute number (anime); the provider's own season and episode are then shown.</summary>
    public static EditorEpisodeSuggestion? FromAbsolute(TvMatchBrowserEpisodeDto? episode) =>
        episode is null ? null : new EditorEpisodeSuggestion(episode, EditorEpisodeSuggestionSources.Absolute);

    /// <summary>The episode whose runtime is nearest the measured length, else the first to air; null for an empty season.</summary>
    public static EditorEpisodeSuggestion? Fallback(IReadOnlyList<TvMatchBrowserEpisodeDto> episodes, double? measuredSeconds)
    {
        if (episodes.Count == 0)
        {
            return null;
        }

        if (measuredSeconds is > 0)
        {
            var minutes = measuredSeconds.Value / 60d;
            var nearest = episodes
                .Where(episode => episode.RuntimeMinutes is > 0)
                .OrderBy(episode => Math.Abs(episode.RuntimeMinutes!.Value - minutes))
                .ThenBy(episode => episode.EpisodeNumber)
                .FirstOrDefault();
            if (nearest is not null)
            {
                return new EditorEpisodeSuggestion(nearest, EditorEpisodeSuggestionSources.Runtime);
            }
        }

        var first = episodes
            .OrderBy(episode => string.IsNullOrWhiteSpace(episode.Aired) ? 1 : 0)
            .ThenBy(episode => episode.Aired, StringComparer.Ordinal)
            .ThenBy(episode => episode.EpisodeNumber)
            .First();
        return new EditorEpisodeSuggestion(first, EditorEpisodeSuggestionSources.AirOrder);
    }

    /// <summary>The season the list opens on: the file name's, else the item's own, else the first with episodes (never Specials by default).</summary>
    public static int? OpeningSeason(IReadOnlyList<TvMatchBrowserSeasonDto> seasons, EditorMatchContextDto? context, bool useFileName)
    {
        if (useFileName && context?.ParsedSeason is { } parsed && seasons.Any(season => season.SeasonNumber == parsed))
        {
            return parsed;
        }

        return seasons.FirstOrDefault(season => season.IsCurrent)?.SeasonNumber
               ?? seasons.Where(season => season.SeasonNumber > 0).OrderBy(season => season.SeasonNumber).FirstOrDefault()?.SeasonNumber
               ?? seasons.FirstOrDefault()?.SeasonNumber;
    }
}
