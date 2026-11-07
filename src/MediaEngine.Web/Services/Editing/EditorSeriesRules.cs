using System.Globalization;
using MediaEngine.Contracts.Editing;

namespace MediaEngine.Web.Services.Editing;

/// <summary>One line of a Series' member list: the member to open and what the line says ("1 · Dune").</summary>
public sealed record EditorSeriesMember(Guid Id, string Title, string Line);

/// <summary>
/// Small rules for the read-only Series editor (universal editor v5 D7, D24, screen H). A Series is a grouping made from its members' own
/// matches: the editor shows its name and ordered members locked, explains why, and offers Close instead of Save. A member is reached from
/// the header's dropdown or from the list, and edited as any item is.
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

    /// <summary>The resx key of the heading over the list, named for what the members are (books, films, comics, audiobooks).</summary>
    public static string MembersHeadingKey(ItemFieldEditSession fields) =>
        (fields.Header?.Level.Items.FirstOrDefault()?.Kind, fields.MediaType) switch
        {
            ("movie", _) or (_, "Movies") => "EditorSeries_FilmsInOrder",
            ("comic", _) or (_, "Comics") => "EditorSeries_ComicsInOrder",
            ("audiobook", _) or (_, "Audiobooks") => "EditorSeries_AudiobooksInOrder",
            _ => "EditorSeries_BooksInOrder",
        };

    private static EditorSeriesMember Line(Guid id, string title, string? number) =>
        new(id, title.Trim(), string.IsNullOrWhiteSpace(number) ? title.Trim() : $"{number.Trim()} · {title.Trim()}");
}
