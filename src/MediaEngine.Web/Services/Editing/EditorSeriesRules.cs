using System.Globalization;
using MediaEngine.Contracts.Editing;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Services.Editing;

/// <summary>One line of a Series' member list: the member to open and what the line says ("1 · Dune").</summary>
/// <param name="Number">The place the Series gives the member, as written; null when it gives none.</param>
/// <param name="MediaType">The member's media type (<c>Books</c>, <c>Movies</c> ...); null when the Engine did not say.</param>
/// <param name="Year">The member's publication year; null when unknown.</param>
/// <param name="PlacedBy">The rule that placed the member (<see cref="EditorSeriesOrderRuleKeys"/>); null when the Engine did not say.</param>
public sealed record EditorSeriesMember(
    Guid Id, string Title, string Line, string? Number = null, string? MediaType = null, string? Year = null, string? PlacedBy = null);

/// <summary>One rule of the order and how many titles it placed (step 1 of the order block).</summary>
public sealed record EditorSeriesOrderRule(string Key, int Placed);

/// <summary>
/// What the order block draws: the rules that placed at least one title, in the order they are tried, and whether no order is known at all.
/// With no rule named by the Engine, <see cref="Rules"/> is empty and <see cref="OrderUnknown"/> is false, so only the result list shows.
/// </summary>
public sealed record EditorSeriesOrderView(IReadOnlyList<EditorSeriesOrderRule> Rules, bool OrderUnknown, int Total)
{
    /// <summary>The rule shown on a member's row: its own rule, or none when the order is unknown or the Engine did not say.</summary>
    public string? ChipRuleOf(EditorSeriesMember member) =>
        Rules.Any(rule => string.Equals(rule.Key, member.PlacedBy, StringComparison.Ordinal)) ? member.PlacedBy : null;

    /// <summary>True when a later rule than the first placed the member (a novella that has no number, say): its chip is amber.</summary>
    public bool IsLaterRule(EditorSeriesMember member) =>
        Rules.Count > 1 && ChipRuleOf(member) is { } rule && !string.Equals(rule, Rules[0].Key, StringComparison.Ordinal);
}

/// <summary>
/// Small rules for the Series editor (universal editor v5 D7, D24, screen H, amended by v8 D5). A Series is a grouping made from its members' own
/// matches: the editor shows its name and ordered members locked and explains why, while its pictures, tags and sort title are the owner's own.
/// A member is reached from the header's dropdown or from the list, and edited as any item is.
/// </summary>
public static class EditorSeriesRules
{
    /// <summary>
    /// True when the item being edited is a Series: a Series of the Library (the subject kind), or the root of a family of books, audiobooks,
    /// comics or films (the Engine's header says so with the kind <c>series</c>).
    /// </summary>
    public static bool IsSeries(string? subjectKind, string? headerKind) =>
        string.Equals(subjectKind, EditorSubjectKinds.Series, StringComparison.Ordinal)
        || (subjectKind is null && string.Equals(headerKind, EditorKindNames.Series, StringComparison.Ordinal));

    /// <summary>The members of the Series being edited, in order: from the header's first page of the item dropdown, or the Library Series' own list.</summary>
    public static IReadOnlyList<EditorSeriesMember> Members(ItemFieldEditSession fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.SubjectKind == EditorSubjectKinds.Series)
        {
            return [.. fields.SubjectMembers
                .Where(member => !string.IsNullOrWhiteSpace(member.Title))
                .Select(member => Line(
                    member.Id, member.Title, member.Number?.ToString(CultureInfo.InvariantCulture),
                    member.MediaType, member.Year, member.PlacedBy))];
        }

        return fields.Header is { } header
            ? [.. header.Level.Items
                .Where(member => !string.IsNullOrWhiteSpace(member.Title))
                .Select(member => Line(member.Id, member.Title, !string.IsNullOrWhiteSpace(member.Code) ? member.Code : member.Number?.ToString(CultureInfo.InvariantCulture)))]
            : [];
    }

    /// <summary>
    /// The resx key of the heading over the list, named for how the members are taken in (universal editor v8 D7): "Reading order" for books and
    /// comics, "Watch order" for films and shows, "Listening order" for audiobooks and music, and "In order" for a Series that mixes them.
    /// </summary>
    public static string MembersHeadingKey(ItemFieldEditSession fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var kinds = fields.SeriesMediaTypes.Count > 0
            ? fields.SeriesMediaTypes.Select(type => type.Key)
            : (fields.Header?.Level.Items.Select(item => item.Kind) ?? []).Append(fields.MediaType);
        return OrderHeadingKey(kinds);
    }

    /// <summary>The heading key for members of these kinds (media type names such as <c>Books</c> or item kinds such as <c>book</c>).</summary>
    public static string OrderHeadingKey(IEnumerable<string?> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        var words = kinds.Select(Word).Where(word => word is not null).Distinct(StringComparer.Ordinal).ToList();
        return words switch
        {
            [] => "EditorSeries_ReadingOrder",
            [var only] => only!,
            _ => "EditorSeries_MixedOrder",
        };
    }

    private static string? Word(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "books" or "book" or "comics" or "comic" => "EditorSeries_ReadingOrder",
        "movies" or "movie" or "film" or "tv" or "show" or "episode" => "EditorSeries_WatchOrder",
        "audiobooks" or "audiobook" or "music" or "track" or "song" or "album" => "EditorSeries_ListeningOrder",
        _ => null,
    };

    /// <summary>
    /// Step 1 of the order block, worked out from what the Engine said about each member: the rules that placed at least one title, in the order they
    /// are tried, with how many each placed. When the Engine names a rule for every member and none of them is a number or a date, no order is known.
    /// </summary>
    public static EditorSeriesOrderView OrderView(IReadOnlyList<EditorSeriesMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var counts = EditorSeriesOrderRuleKeys.InOrder
            .Select(key => new EditorSeriesOrderRule(key, members.Count(member => string.Equals(member.PlacedBy, key, StringComparison.Ordinal))))
            .Where(rule => rule.Placed > 0)
            .ToList();
        var named = counts.Sum(rule => rule.Placed);
        var unknown = named > 0 && counts.All(rule => rule.Key == EditorSeriesOrderRuleKeys.Title);
        return new EditorSeriesOrderView(unknown ? [] : counts, unknown, members.Count);
    }

    /// <summary>The words for a rule, on its pill and its chip.</summary>
    public static string RuleWord(string rule, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return rule switch
        {
            EditorSeriesOrderRuleKeys.Number => localizer["EditorSeriesOrder_Rule_number"].Value,
            EditorSeriesOrderRuleKeys.Date => localizer["EditorSeriesOrder_Rule_date"].Value,
            _ => localizer["EditorSeriesOrder_Rule_title"].Value,
        };
    }

    /// <summary>The detail line under a title ("Book · 2011"): the kind of title and its year, whichever of them is known; empty when neither is.</summary>
    public static string DetailLine(EditorSeriesMember member, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(localizer);
        var kind = member.MediaType?.Trim() switch
        {
            "Books" => localizer["EditorSeriesOrder_Kind_Books"].Value,
            "Audiobooks" => localizer["EditorSeriesOrder_Kind_Audiobooks"].Value,
            "Comics" => localizer["EditorSeriesOrder_Kind_Comics"].Value,
            "Movies" => localizer["EditorSeriesOrder_Kind_Movies"].Value,
            "TV" => localizer["EditorSeriesOrder_Kind_TV"].Value,
            "Music" => localizer["EditorSeriesOrder_Kind_Music"].Value,
            _ => null,
        };
        return string.Join(" · ", new[] { kind, member.Year }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static EditorSeriesMember Line(
        Guid id, string title, string? number, string? mediaType = null, string? year = null, string? placedBy = null) =>
        new(
            id, title.Trim(), string.IsNullOrWhiteSpace(number) ? title.Trim() : $"{number.Trim()} · {title.Trim()}",
            string.IsNullOrWhiteSpace(number) ? null : number.Trim(), mediaType, year, placedBy);
}
