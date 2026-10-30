using System.Globalization;
using MediaEngine.Contracts.Matching;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Turns what the person picks in the Match tool's Show › Season › Episode browser into the same candidate a search result would be, so
/// the "What will happen" step and the staging after it work the same for TV as for every other kind of item (universal editor v2, D4, D5).
/// An episode result always carries the exact show, season and episode, so an episode can be matched into another show but a show can
/// never be staged on an episode.
/// </summary>
public static class EditorTvMatchCandidates
{
    private const string ProviderName = "tvdb";

    /// <summary>The scope ids the browser serves: a show page and an episode page. Seasons and every other type keep the search.</summary>
    public static bool UsesBrowser(string mediaType, string scopeId) =>
        string.Equals(mediaType, "TV", StringComparison.OrdinalIgnoreCase)
        && (string.Equals(scopeId, "series", StringComparison.OrdinalIgnoreCase)
            || string.Equals(scopeId, "episode", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the browser must go down to an episode before "Use this match" is possible.</summary>
    public static bool NeedsEpisode(string scopeId) => string.Equals(scopeId, "episode", StringComparison.OrdinalIgnoreCase);

    public static ItemCanonicalRetailCandidateDto ForShow(TvMatchBrowserShowDto show) => new()
    {
        CandidateId = $"{ProviderName}:{show.SeriesId}",
        ProviderId = WellKnownProviders.Tvdb.ToString("D"),
        ProviderName = ProviderName,
        ProviderItemId = show.SeriesId,
        Title = show.Name,
        Year = show.Year,
        Description = show.Overview,
        CoverUrl = show.ImageUrl,
        IsApplicable = true,
        RequiredFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataFieldConstants.ShowName] = show.Name,
        },
        BridgeIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BridgeIdKeys.TvdbId] = show.SeriesId,
        },
    };

    public static ItemCanonicalRetailCandidateDto ForEpisode(TvMatchBrowserEpisodeDto episode)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataFieldConstants.ShowName] = episode.ShowName,
            [MetadataFieldConstants.SeasonNumber] = episode.SeasonNumber.ToString(CultureInfo.InvariantCulture),
            [MetadataFieldConstants.EpisodeNumber] = episode.EpisodeNumber.ToString(CultureInfo.InvariantCulture),
            [MetadataFieldConstants.EpisodeTitle] = episode.Title,
        };
        return new ItemCanonicalRetailCandidateDto
        {
            CandidateId = $"{ProviderName}:{episode.EpisodeId}",
            ProviderId = WellKnownProviders.Tvdb.ToString("D"),
            ProviderName = ProviderName,
            ProviderItemId = episode.EpisodeId,
            Title = episode.Title,
            Year = episode.Aired is { Length: >= 4 } aired ? aired[..4] : null,
            Description = episode.Overview,
            CoverUrl = episode.ImageUrl,
            IsApplicable = true,
            RequiredFields = fields,
            SuggestedFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            BridgeIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BridgeIdKeys.TvdbId] = episode.SeriesId,
                [BridgeIdKeys.TvdbEpisodeId] = episode.EpisodeId,
            },
        };
    }

    /// <summary>"S02 E05" for an episode row; "Specials" season 0 reads "S00 E03".</summary>
    public static string SlotLabel(int seasonNumber, int episodeNumber) =>
        $"S{seasonNumber.ToString("00", CultureInfo.InvariantCulture)} E{episodeNumber.ToString("00", CultureInfo.InvariantCulture)}";
}
