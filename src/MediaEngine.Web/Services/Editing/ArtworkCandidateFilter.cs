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
    public const string MediumOnly = "medium";

    /// <summary>A picture counts as large from this many pixels on its longer side.</summary>
    public const int LargeFromPixels = 1000;

    /// <summary>A picture counts as medium from this many pixels on its longer side, up to the large size.</summary>
    public const int MediumFromPixels = 500;

    /// <summary>Languages the list holds, in the order first seen, each with the name a person would say. Pictures with no words are "No text".</summary>
    public static IReadOnlyList<(string Value, string Label)> Languages(IEnumerable<ProviderArtworkCandidateDto> items, string? appLanguage = null)
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

        // The app's own language comes first, so the list opens on what the person reads.
        var ordered = seen.OrderBy(entry => SameLanguage(entry.Item1, appLanguage) ? 0 : 1).ToList();
        if (hasNoWords)
        {
            ordered.Add((NoWords, "No text"));
        }

        return ordered;
    }

    /// <summary>The language the list opens on: the app's language when the provider has pictures in it, else everything.</summary>
    public static string DefaultLanguage(IEnumerable<ProviderArtworkCandidateDto> items, string? appLanguage)
    {
        var match = items.Select(item => Key(item.Language)).FirstOrDefault(key => SameLanguage(key, appLanguage));
        return match ?? All;
    }

    // Providers send two- or three-letter codes ("en", "eng"); the app's language is a culture name ("en", "en-US").
    private static bool SameLanguage(string providerKey, string? appLanguage)
    {
        if (providerKey == NoWords || string.IsNullOrWhiteSpace(appLanguage))
        {
            return false;
        }

        try
        {
            return string.Equals(
                CultureInfo.GetCultureInfo(providerKey).TwoLetterISOLanguageName,
                CultureInfo.GetCultureInfo(appLanguage.Trim()).TwoLetterISOLanguageName,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (CultureNotFoundException)
        {
            return string.Equals(providerKey, appLanguage.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Largest pictures first; equal sizes keep the provider's order.</summary>
    public static IReadOnlyList<ProviderArtworkCandidateDto> SortBySize(IEnumerable<ProviderArtworkCandidateDto> items) =>
        items.OrderByDescending(item => Math.Max(item.Width ?? 0, item.Height ?? 0)).ToList();

    public static IReadOnlyList<ProviderArtworkCandidateDto> Apply(IEnumerable<ProviderArtworkCandidateDto> items, string language, string size) =>
        items
            .Where(item => language == All || Key(item.Language) == language)
            .Where(item => size switch
            {
                LargeOnly => LongSide(item) >= LargeFromPixels,
                MediumOnly => LongSide(item) is >= MediumFromPixels and < LargeFromPixels,
                _ => true,
            })
            .ToList();

    /// <summary>What a tile says about its language, or null for a picture with no words.</summary>
    public static string? LabelFor(string? language)
    {
        var key = Key(language);
        return key == NoWords ? null : NameOf(key);
    }

    private static int LongSide(ProviderArtworkCandidateDto item) => Math.Max(item.Width ?? 0, item.Height ?? 0);

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
