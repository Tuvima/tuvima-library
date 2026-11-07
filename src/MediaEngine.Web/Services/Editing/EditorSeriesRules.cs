using MediaEngine.Contracts.Editing;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Small rules for the read-only Series editor (universal editor v5 D7, D24, screen H). A Series is a canonical grouping that comes from its
/// members' own matches: the editor shows its name and ordered members locked, explains why, and offers Close instead of Save. A member is
/// reached from the header's dropdown and edited as any item is.
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

    /// <summary>The members of a Series in order, as the lines of the "in order" list: "1 · Dune" or just the title when it has no number.</summary>
    public static IReadOnlyList<string> MemberLines(IEnumerable<EditorNavItemDto> members) =>
        [.. members
            .Where(member => !string.IsNullOrWhiteSpace(member.Title))
            .Select(member => NumberOf(member) is { } number ? $"{number} · {member.Title.Trim()}" : member.Title.Trim())];

    /// <summary>The heading over the list: "Books in order", "Films in order", "Comics in order" or "Audiobooks in order", named for what the members are.</summary>
    public static string MembersHeading(IEnumerable<EditorNavItemDto> members) => members.FirstOrDefault()?.Kind switch
    {
        "movie" => "Films in order",
        "comic" => "Comics in order",
        "audiobook" => "Audiobooks in order",
        _ => "Books in order",
    };

    private static string? NumberOf(EditorNavItemDto member) =>
        !string.IsNullOrWhiteSpace(member.Code) ? member.Code.Trim() : member.Number?.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
