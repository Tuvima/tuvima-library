using System.Globalization;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Formatting;

namespace MediaEngine.Web.Components.Details;

/// <summary>One word-pill at the top of a group page: the kind of group is the purple one.</summary>
/// <param name="Text">What the pill says, in words ("Series", "Books", "2011 – 2021", "11 books").</param>
/// <param name="IsType">True for the kind of group ("Series", "Collection"); it is drawn in the accent color.</param>
public sealed record GroupPagePill(string Text, bool IsType);

/// <summary>
/// The rules of a group page (the page of a Series or a collection, spec group-pages-and-tiles WP2 and WP3): which pages they are,
/// the path above the title, the pills in words, and the one "You own 10 of 11 books." line under the members.
/// </summary>
public static class GroupPageRules
{
    /// <summary>A Series (books, comics, films) or a collection: the pages that open from a group tile.</summary>
    public static bool IsGroupPage(DetailEntityType entityType) => entityType is DetailEntityType.Collection or DetailEntityType.BookSeries
        or DetailEntityType.ComicSeries or DetailEntityType.MovieSeries;

    /// <summary>A Series of one kind of title; a collection can mix kinds.</summary>
    public static bool IsSeries(DetailEntityType entityType) => entityType is DetailEntityType.BookSeries or DetailEntityType.ComicSeries
        or DetailEntityType.MovieSeries;

    /// <summary>The shape a member's cover keeps in the grid: albums and audiobooks are square, episodes wide, everything else portrait.</summary>
    public static ArtworkShape MemberShape(DetailEntityType entityType) => entityType switch
    {
        DetailEntityType.MusicAlbum or DetailEntityType.Audiobook => ArtworkShape.Square,
        DetailEntityType.TvEpisode => ArtworkShape.Wide,
        _ => ArtworkShape.Portrait,
    };

    /// <summary>The first step of the path above the title ("Read", "Watch", "Collections") and where it goes.</summary>
    public static (string Label, string Href) PathRoot(DetailEntityType entityType) => entityType switch
    {
        DetailEntityType.MovieSeries => ("Watch", "/watch"),
        DetailEntityType.BookSeries or DetailEntityType.ComicSeries => ("Read", "/read"),
        _ => ("Collections", "/collections"),
    };

    /// <summary>The second step of the path, the kind of group in words; none for a collection (its first step already names it).</summary>
    public static string? PathKind(DetailEntityType entityType) => IsSeries(entityType) ? "Series" : null;

    /// <summary>The pills under the title, in words: Series, Books, years, count. A collection says Collection, its years and its count.</summary>
    public static IReadOnlyList<GroupPagePill> Pills(DetailPageViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var pills = new List<GroupPagePill>();
        if (!IsGroupPage(model.EntityType))
        {
            return pills;
        }

        if (IsSeries(model.EntityType))
        {
            pills.Add(new GroupPagePill("Series", true));
            pills.Add(new GroupPagePill(model.EntityType switch
            {
                DetailEntityType.ComicSeries => "Comics",
                DetailEntityType.MovieSeries => "Movies",
                _ => "Books",
            }, false));

            var placement = model.SequencePlacement;
            if (YearRange(placement?.OrderedItems.Select(item => item.PublicationDate)) is { } years)
            {
                pills.Add(new GroupPagePill(years, false));
            }

            if (placement is not null)
            {
                pills.Add(new GroupPagePill(CountWords(SeriesOwnedCount.Of(placement), placement), false));
            }
            else if (model.Metadata.FirstOrDefault(item => string.Equals(item.Kind, "count", StringComparison.OrdinalIgnoreCase)) is { Label.Length: > 0 } count)
            {
                pills.Add(new GroupPagePill(count.Label, false));
            }

            return pills;
        }

        pills.Add(new GroupPagePill("Collection", true));
        // An imported list says "You own 2 of 4" in place of the plain item count (Collections and lists 4g).
        var hasListCount = model.Metadata.Any(item => string.Equals(item.Kind, "list_owned", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.Label));
        foreach (var kind in new[] { "year", hasListCount ? "list_owned" : "item_count" })
        {
            if (model.Metadata.FirstOrDefault(item => string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase)) is { Label.Length: > 0 } pill)
            {
                pills.Add(new GroupPagePill(pill.Label.Replace("–", " – ", StringComparison.Ordinal), false));
            }
        }

        return pills;
    }

    /// <summary>"2011 – 2021" (or "2011" for one year) from the members' publication dates; null when none has a year.</summary>
    public static string? YearRange(IEnumerable<string?>? dates)
    {
        var years = (dates ?? [])
            .Select(TryYear)
            .Where(year => year.HasValue)
            .Select(year => year!.Value)
            .Distinct()
            .Order()
            .ToList();
        return years.Count == 0
            ? null
            : years.Count == 1
                ? years[0].ToString(CultureInfo.InvariantCulture)
                : $"{years[0].ToString(CultureInfo.InvariantCulture)} – {years[^1].ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// The one line under the members: "You own 10 of 11 books.", and "You own none of the 11 books." when nothing is owned. The total
    /// is never less than what is owned.
    /// </summary>
    public static string OwnedLine(int owned, int total, string singularNoun, string? pluralNoun = null)
    {
        owned = Math.Max(0, owned);
        total = Math.Max(total, owned);
        return owned == 0
            ? $"You own none of the {DisplayFormat.FormatCountNoun(total, singularNoun, pluralNoun)}."
            : $"You own {DisplayFormat.FormatCount(owned)} of {DisplayFormat.FormatCountNoun(total, singularNoun, pluralNoun)}.";
    }

    /// <summary>The owned line for a Series' own placement (novellas and short stories count, as everywhere else).</summary>
    public static string OwnedLine(SequencePlacementViewModel placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return OwnedLine(
            SeriesOwnedCount.Of(placement),
            SeriesOwnedCount.KnownTotal(placement),
            Noun(placement.ItemLabel, "item"),
            Noun(placement.ItemPluralLabel, "items"));
    }

    private static string CountWords(int total, SequencePlacementViewModel placement) =>
        DisplayFormat.FormatCountNoun(total, Noun(placement.ItemLabel, "item"), Noun(placement.ItemPluralLabel, "items"));

    private static string Noun(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();

    private static int? TryYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date))
        {
            return null;
        }

        var text = date.AsSpan().Trim();
        return text.Length >= 4
            && int.TryParse(text[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            && year is >= 1000 and <= 2999
                ? year
                : null;
    }
}
