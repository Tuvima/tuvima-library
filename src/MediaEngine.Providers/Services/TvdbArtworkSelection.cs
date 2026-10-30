using System.Globalization;
using System.Text.Json.Nodes;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Decides which of TheTVDB's artwork records belong to a show, season or episode and which of
/// them to show first. TheTVDB lists a show's season posters alongside its own posters, and
/// returns every language of a logo in no particular order, so both need an explicit rule.
/// </summary>
public static class TvdbArtworkSelection
{
    /// <summary>The kind of managed artwork a TheTVDB artwork type name stands for in a scope, or null when it is not used.</summary>
    public static AssetType? Classify(string scope, string typeName)
    {
        var name = typeName.ToLowerInvariant();
        if (scope == "series")
        {
            // "Season Poster" and "Season Background" belong to a season, never to the show itself.
            if (name.Contains("season") || name.Contains("episode"))
            {
                return null;
            }
            if (name.Contains("logo"))
            {
                return AssetType.Logo;
            }
            if (name.Contains("background") || name.Contains("fanart"))
            {
                return AssetType.Background;
            }
            if (name.Contains("poster"))
            {
                return AssetType.CoverArt;
            }
        }
        else if (scope == "season")
        {
            if (name.Contains("background"))
            {
                return AssetType.SeasonThumb;
            }
            if (name.Contains("poster"))
            {
                return AssetType.SeasonPoster;
            }
        }
        return null;
    }

    /// <summary>TheTVDB names languages with three letters ("eng"); the app's setting uses two ("en").</summary>
    public static string ThreeLetterLanguage(string? language)
    {
        var code = language?.Split('-', '_')[0].Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(code))
        {
            return "eng";
        }
        if (code.Length == 3)
        {
            return code;
        }
        try
        {
            return CultureInfo.GetCultureInfo(code).ThreeLetterISOLanguageName.ToLowerInvariant();
        }
        catch (CultureNotFoundException)
        {
            return "eng";
        }
    }

    /// <summary>
    /// Orders one kind of artwork best first: the configured language, then pictures with no text
    /// language, then the highest TheTVDB score. A logo in another language is dropped; a poster in
    /// another language is kept only when nothing better exists.
    /// </summary>
    public static IReadOnlyList<JsonNode> Rank(IEnumerable<JsonNode> artworks, AssetType type, string? metadataLanguage)
    {
        var preferred = ThreeLetterLanguage(metadataLanguage);
        var scored = artworks
            .Select(node => (Node: node, Rank: LanguageRank(node, preferred)))
            .ToList();
        var acceptable = scored.Where(item => item.Rank > 0).ToList();
        if (type == AssetType.Logo || (type is AssetType.CoverArt or AssetType.SeasonPoster && acceptable.Count > 0))
        {
            scored = acceptable;
        }
        return scored
            .OrderByDescending(item => item.Rank)
            .ThenByDescending(item => Number(item.Node["score"]))
            .ThenByDescending(item => Number(item.Node["width"]) * Number(item.Node["height"]))
            .Select(item => item.Node)
            .ToList();
    }

    private static int LanguageRank(JsonNode artwork, string preferred)
    {
        var language = artwork["language"]?.ToString()?.Trim().ToLowerInvariant();
        if (string.Equals(language, preferred, StringComparison.Ordinal))
        {
            return 3;
        }
        return string.IsNullOrEmpty(language) || language == "zxx" ? 2 : 0;
    }

    private static double Number(JsonNode? node) =>
        double.TryParse(node?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
