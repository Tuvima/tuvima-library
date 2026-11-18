using System.Globalization;
using MediaEngine.Contracts.Editing;

namespace MediaEngine.Web.Services.Editing;

/// <summary>One line of a Series' member list: the member to open and what the line says ("1 · Dune").</summary>
public sealed record EditorSeriesMember(Guid Id, string Title, string Line);

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
                .Select(member => Line(member.Id, member.Title, member.Number?.ToString(CultureInfo.InvariantCulture)))];
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

    private static EditorSeriesMember Line(Guid id, string title, string? number) =>
        new(id, title.Trim(), string.IsNullOrWhiteSpace(number) ? title.Trim() : $"{number.Trim()} · {title.Trim()}");
}
