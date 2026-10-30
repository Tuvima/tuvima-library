using System.Globalization;
using System.Text.Json.Nodes;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Matching;

/// <summary>
/// Turns TheTVDB answers into the rows of the Match tool's Show › Season › Episode browser (universal editor v2, D4). Pure functions
/// with no calls out, so the rules that keep a show-only result from ever being offered as an episode are testable on their own.
/// </summary>
internal static class TvMatchBrowserMapper
{
    /// <summary>Show rows from TheTVDB's series search. A row with no usable numeric show id is dropped: it could not be opened.</summary>
    public static IReadOnlyList<TvMatchBrowserShowDto> MapShows(JsonNode? searchResults, string? currentSeriesId, int limit = 20)
    {
        var shows = new List<TvMatchBrowserShowDto>();
        foreach (var node in searchResults?.AsArray() ?? [])
        {
            var id = ReadSeriesId(node);
            if (id is null || shows.Any(show => show.SeriesId == id))
            {
                continue;
            }

            var name = Text(node?["translations"]?["eng"]) ?? Text(node, "name");
            if (name is null)
            {
                continue;
            }

            shows.Add(new TvMatchBrowserShowDto(
                id, name, Text(node, "year"),
                Text(node?["overviews"]?["eng"]) ?? Text(node, "overview"),
                Text(node, "image_url") ?? Text(node, "thumbnail"),
                OutsideLinks.TvdbSeries(id) ?? "https://thetvdb.com",
                string.Equals(id, currentSeriesId, StringComparison.Ordinal)));
            if (shows.Count >= limit)
            {
                break;
            }
        }

        return shows;
    }

    /// <summary>The seasons of a show in TheTVDB's default order, numbered seasons only, lowest first.</summary>
    public static IReadOnlyList<TvMatchBrowserSeasonDto> MapSeasons(JsonNode? show, string seriesId, string? currentSeasonId)
    {
        var seasons = new List<TvMatchBrowserSeasonDto>();
        foreach (var node in show?["seasons"]?.AsArray() ?? [])
        {
            if (node is null || !Api.Endpoints.MetadataEndpoints.IsTvdbSeasonInOrder(node, show, "default"))
            {
                continue;
            }

            var id = Text(node, "id");
            if (id is null || !int.TryParse(Text(node, "number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                continue;
            }

            seasons.Add(new TvMatchBrowserSeasonDto(
                id, seriesId, number,
                Text(node, "name") ?? (number == 0 ? "Specials" : $"Season {number}"),
                Text(node, "image"),
                string.Equals(id, currentSeasonId, StringComparison.Ordinal)));
        }

        return seasons.OrderBy(season => season.SeasonNumber).ToList();
    }

    /// <summary>
    /// The episodes of one season, each a complete record (show, season and episode ids and numbers). Anything without an id, a
    /// season number matching the requested season, or an episode number of at least 1 is left out, so no row can be half an identity.
    /// </summary>
    public static IReadOnlyList<TvMatchBrowserEpisodeDto> MapEpisodes(
        IEnumerable<JsonNode> orderedEpisodes,
        string seriesId,
        string showName,
        TvMatchBrowserSeasonDto season,
        string? currentEpisodeId,
        IReadOnlyDictionary<string, Guid> filesByEpisodeId)
    {
        var rows = new List<TvMatchBrowserEpisodeDto>();
        foreach (var node in orderedEpisodes)
        {
            var id = Text(node, "id");
            if (id is null
                || !int.TryParse(Text(node, "number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < 1)
            {
                continue;
            }

            filesByEpisodeId.TryGetValue(id, out var fileId);
            rows.Add(new TvMatchBrowserEpisodeDto(
                id, seriesId, showName, season.SeasonId, season.SeasonNumber, number,
                Text(node, "name") ?? $"Episode {number}",
                Text(node, "aired"), Text(node, "overview"), Text(node, "image"),
                OutsideLinks.TvdbEpisode(id) ?? "https://thetvdb.com",
                string.Equals(id, currentEpisodeId, StringComparison.Ordinal),
                filesByEpisodeId.ContainsKey(id),
                filesByEpisodeId.ContainsKey(id) ? fileId : null));
        }

        return rows.OrderBy(row => row.EpisodeNumber).ToList();
    }

    /// <summary>The episodes of the ordered list that belong to the season (by season id, else by season number).</summary>
    public static IReadOnlyList<JsonNode> SelectSeasonEpisodes(IEnumerable<JsonNode> orderedEpisodes, TvMatchBrowserSeasonDto season)
    {
        var seasonNode = SeasonNode(season);
        return orderedEpisodes.Where(node => Api.Endpoints.MetadataEndpoints.IsTvdbEpisodeInSeason(node, seasonNode)).ToList();
    }

    private static JsonNode SeasonNode(TvMatchBrowserSeasonDto season) => new JsonObject
    {
        ["id"] = season.SeasonId,
        ["number"] = season.SeasonNumber.ToString(CultureInfo.InvariantCulture),
    };

    private static string? ReadSeriesId(JsonNode? node)
    {
        var raw = Text(node, "tvdb_id") ?? Text(node, "id");
        if (raw is null)
        {
            return null;
        }

        var digits = raw.StartsWith("series-", StringComparison.OrdinalIgnoreCase) ? raw["series-".Length..] : raw;
        return digits.Length > 0 && digits.All(char.IsAsciiDigit) ? digits : null;
    }

    private static string? Text(JsonNode? node, string key) => Text(node?[key]);

    private static string? Text(JsonNode? node) =>
        node is JsonValue && node.ToString().Trim() is { Length: > 0 } text ? text : null;
}
