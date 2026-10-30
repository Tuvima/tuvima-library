using System.Globalization;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Puts the pictures in the person's own language first (spec D10; F38): a logo with the title in their language is what they want to
/// see at the top, then pictures with no text in them, then every other language. The providers' own order is kept inside each group.
/// </summary>
internal static class ProviderArtworkLanguageOrder
{
    public static IReadOnlyList<ProviderArtworkCandidate> Apply(IReadOnlyList<ProviderArtworkCandidate> items, string? metadataLanguage)
    {
        var preferred = Normalise(metadataLanguage);
        return items
            .Select((item, index) => (item, index, rank: Rank(Normalise(item.Language), preferred)))
            .OrderBy(entry => entry.rank)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.item)
            .ToList();
    }

    private static int Rank(string language, string preferred) =>
        language.Length == 0 ? 1 : language == preferred ? 0 : 2;

    // The providers say "en" or "eng"; "xx", "zxx" and "und" mean the picture has no words in it.
    private static string Normalise(string? code)
    {
        var value = code?.Trim().ToLowerInvariant() ?? string.Empty;
        if (value.Length == 0 || value is "xx" or "zxx" or "und" or "null")
        {
            return string.Empty;
        }

        if (value.Length == 3)
        {
            return value;
        }

        try
        {
            return CultureInfo.GetCultureInfo(value.Split('-', '_')[0]).ThreeLetterISOLanguageName.ToLowerInvariant();
        }
        catch (CultureNotFoundException)
        {
            return value;
        }
    }
}
