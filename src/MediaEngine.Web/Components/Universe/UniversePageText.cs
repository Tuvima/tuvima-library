using System.Globalization;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Universe;

namespace MediaEngine.Web.Components.Universe;

/// <summary>The sentences the Universe page and the item page's "Part of" line are built from, in one place.</summary>
public static class UniversePageText
{
    /// <summary>"Part of the Dune Universe"; a name that already ends in "Universe" is not given a second one.</summary>
    public static string PartOfLine(string name)
    {
        var trimmed = name.Trim();
        return trimmed.EndsWith("Universe", StringComparison.OrdinalIgnoreCase)
            ? $"Part of the {trimmed}"
            : $"Part of the {trimmed} Universe";
    }

    /// <summary>
    /// The hero's one line of totals, for example "3 Series · 14 Works · 2 formats". A Universe whose lines are all TV
    /// shows says "shows"; every other mix says "Series". Parts that are zero are left out.
    /// </summary>
    public static string TotalsLine(UniverseClientDetailDto detail)
    {
        var parts = new List<string>();
        if (detail.SeriesCount > 0)
        {
            var allShows = detail.Shelves.Count > 0
                && detail.Shelves.All(shelf => string.Equals(shelf.GroupType, CollectionGroupTypes.Show, StringComparison.OrdinalIgnoreCase));
            var noun = allShows
                ? (detail.SeriesCount == 1 ? "show" : "shows")
                : "Series";
            parts.Add(Count(detail.SeriesCount, noun));
        }

        if (detail.ItemCount > 0)
        {
            parts.Add(Count(detail.ItemCount, detail.ItemCount == 1 ? "Work" : "Works"));
        }

        var formats = detail.MediaTypes.Count;
        if (formats > 0)
        {
            parts.Add(Count(formats, formats == 1 ? "format" : "formats"));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>"as Hero, Villain" for a cast row; null when the credit names no character.</summary>
    public static string? CharactersLine(UniverseClientCastMemberDto member)
    {
        var labels = member.Characters
            .Select(character => character.Label)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .ToList();
        return labels.Count == 0 ? null : $"as {string.Join(", ", labels)}";
    }

    /// <summary>The wire relationship is lower case ("based on"); on screen it starts a sentence.</summary>
    public static string CapitaliseFirst(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0
            ? trimmed
            : string.Concat(trimmed[..1].ToUpperInvariant(), trimmed.AsSpan(1));
    }

    /// <summary>"Title — Based on Source".</summary>
    public static string AdaptationLine(UniverseClientAdaptationDto adaptation) =>
        string.IsNullOrWhiteSpace(adaptation.Relationship) || string.IsNullOrWhiteSpace(adaptation.SourceLabel)
            ? adaptation.Label
            : $"{adaptation.Label} — {CapitaliseFirst(adaptation.Relationship)} {adaptation.SourceLabel}";

    /// <summary>"Details found for 6 of 14 works".</summary>
    public static string EnrichmentNote(UniverseEnrichmentStateDto enrichment) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Details found for {enrichment.EnrichedWorkCount} of {enrichment.WorkCount} {(enrichment.WorkCount == 1 ? "work" : "works")}");

    private static string Count(int count, string noun) => string.Create(CultureInfo.InvariantCulture, $"{count} {noun}");
}
