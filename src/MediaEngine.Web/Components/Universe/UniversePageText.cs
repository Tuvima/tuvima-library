using System.Globalization;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Universe;

namespace MediaEngine.Web.Components.Universe;

/// <summary>The sentences the Universe page and the item page's "Part of" line are built from, in one place.</summary>
public static class UniversePageText
{
    /// <summary>
    /// "Dune Universe"; a name that already ends in "Universe" is not given a second one, and a leading "The" is dropped
    /// ("The Expanse" becomes "Expanse Universe" in "Open the Expanse Universe").
    /// </summary>
    public static string UniverseName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.StartsWith("The ", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 4)
        {
            trimmed = trimmed[4..].TrimStart();
        }

        return trimmed.EndsWith("Universe", StringComparison.OrdinalIgnoreCase) ? trimmed : $"{trimmed} Universe";
    }

    /// <summary>The Universe icon's hover line, for example "3 Series · 14 Works". Zero parts are left out.</summary>
    public static string CardCounts(int seriesCount, int workCount)
    {
        var parts = new List<string>();
        if (seriesCount > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{seriesCount} Series"));
        }

        if (workCount > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{workCount} {(workCount == 1 ? "Work" : "Works")} in your Library"));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>A QID reduced to letters and digits, for an element id.</summary>
    public static string SafeId(string? value) =>
        new(string.IsNullOrEmpty(value) ? ['x'] : value.Where(char.IsAsciiLetterOrDigit).ToArray());

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

    /// <summary>"Source: Wikidata" for one source, "Sources: Wikidata, Dune Wiki" for several; null when none is recorded.</summary>
    public static string? SourcesLine(IReadOnlyList<UniverseSourceDto> sources)
    {
        var labels = sources
            .Select(source => source.Label)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .ToList();
        return labels.Count switch
        {
            0 => null,
            1 => $"Source: {labels[0]}",
            _ => $"Sources: {string.Join(", ", labels)}",
        };
    }

    /// <summary>"Details found for 6 of 14 works".</summary>
    public static string EnrichmentNote(UniverseEnrichmentStateDto enrichment) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Details found for {enrichment.EnrichedWorkCount} of {enrichment.WorkCount} {(enrichment.WorkCount == 1 ? "work" : "works")}");

    private static string Count(int count, string noun) => string.Create(CultureInfo.InvariantCulture, $"{count} {noun}");
}
