using System.Globalization;
using MediaEngine.Contracts.Artwork;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// The Language and Size filters of the Artwork tool's provider list (spec D10): narrows what the provider returned without asking it again.
/// </summary>
public static class ArtworkCandidateFilter
{
    public const string All = "all";
    public const string NoWords = "none";
    public const string AnySize = "any";
    public const string LargeOnly = "large";

    /// <summary>A picture counts as large from this many pixels on its longer side.</summary>
    public const int LargeFromPixels = 1000;

    /// <summary>Languages the list holds, in the order first seen, each with the name a person would say. Pictures with no words are "No text".</summary>
    public static IReadOnlyList<(string Value, string Label)> Languages(IEnumerable<ProviderArtworkCandidateDto> items)
    {
        var seen = new List<(string, string)>();
        var hasNoWords = false;
        foreach (var item in items)
        {
            var key = Key(item.Language);
            if (key == NoWords)
            {
                hasNoWords = true;
            }
            else if (seen.All(entry => entry.Item1 != key))
            {
                seen.Add((key, NameOf(key)));
            }
        }

        if (hasNoWords)
        {
            seen.Add((NoWords, "No text"));
        }

        return seen;
    }

    public static IReadOnlyList<ProviderArtworkCandidateDto> Apply(IEnumerable<ProviderArtworkCandidateDto> items, string language, string size) =>
        items
            .Where(item => language == All || Key(item.Language) == language)
            .Where(item => size != LargeOnly || Math.Max(item.Width ?? 0, item.Height ?? 0) >= LargeFromPixels)
            .ToList();

    /// <summary>What a tile says about its language, or null for a picture with no words.</summary>
    public static string? LabelFor(string? language)
    {
        var key = Key(language);
        return key == NoWords ? null : NameOf(key);
    }

    private static string Key(string? code)
    {
        var value = code?.Trim().ToLowerInvariant() ?? string.Empty;
        return value.Length == 0 || value is "xx" or "zxx" or "und" or "null" ? NoWords : value;
    }

    private static string NameOf(string code)
    {
        try
        {
            var name = CultureInfo.GetCultureInfo(code).EnglishName;
            return name.Contains('(') ? name[..name.IndexOf('(')].Trim() : name;
        }
        catch (CultureNotFoundException)
        {
            return code.ToUpperInvariant();
        }
    }
}
