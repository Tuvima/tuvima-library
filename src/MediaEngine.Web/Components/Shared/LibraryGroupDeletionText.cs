using MediaEngine.Contracts.Items;
using MediaEngine.Web.Services.Formatting;

namespace MediaEngine.Web.Components.Shared;

/// <summary>
/// What Delete show, season, album or Series says: the heading, the totals broken out by kind, the counted button, the typed-name rule
/// and the toast. One place so the dialog and its tests agree word for word (spec Delete a show, season, album or Series, D3, D4, D8).
/// </summary>
public static class LibraryGroupDeletionText
{
    /// <summary>Titles are listed by name up to this many; beyond it only the totals are shown (D3).</summary>
    public const int NamedTitlesLimit = 12;

    public const string LastSeasonLine = "This is the last season, so the show will be removed too.";

    /// <summary>The question: a season names its show ("The Expanse", Season 2).</summary>
    public static string Heading(string requestedTitle, LibraryItemDeletionGroupDto? group)
    {
        var title = group?.Title is { Length: > 0 } known ? known : requestedTitle;
        return group is { Kind: "season", ParentTitle: { Length: > 0 } show }
            ? $"Delete \"{show}\", {title} from your library?"
            : $"Delete \"{title}\" from your library?";
    }

    /// <summary>The word for one title of this group: "episode", "track", "book".</summary>
    public static string Noun(IReadOnlyCollection<LibraryItemDeletionItemDto> items, int count)
    {
        var type = items.Select(item => item.MediaType).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        var singular = type.ToLowerInvariant() switch
        {
            "tv" or "videos" => "episode",
            "music" => "track",
            "books" => "book",
            "audiobooks" => "audiobook",
            "comics" => "comic",
            _ => "title",
        };
        return count == 1 ? singular : singular + "s";
    }

    /// <summary>The totals line: "2 seasons · 38 episodes", "18 tracks", "14 books".</summary>
    public static string Totals(LibraryItemDeletionGroupDto group, IReadOnlyCollection<LibraryItemDeletionItemDto> items)
    {
        var count = group.TitlesOwned;
        var titles = $"{DisplayFormat.FormatCount(count)} {Noun(items, count)}";
        return group.Kind == "show" && group.SubGroups.Count > 0
            ? $"{DisplayFormat.FormatCount(group.SubGroups.Count)} {(group.SubGroups.Count == 1 ? "season" : "seasons")} · {titles}"
            : titles;
    }

    /// <summary>"Season 3: 6 of 10 episodes owned" for every part that is only partly owned; empty when all of it is.</summary>
    public static IReadOnlyList<string> PartialLines(LibraryItemDeletionGroupDto group, IReadOnlyCollection<LibraryItemDeletionItemDto> items) =>
        [.. group.SubGroups
            .Where(part => part.Owned < part.Total)
            .Select(part => $"{part.Title}: {DisplayFormat.FormatCount(part.Owned)} of {DisplayFormat.FormatCount(part.Total)} {Noun(items, part.Total)} owned")];

    /// <summary>The Series warning (D11): the books go with it.</summary>
    public static string? SeriesLine(LibraryItemDeletionGroupDto group, IReadOnlyCollection<LibraryItemDeletionItemDto> items) =>
        group.Kind == "series"
            ? $"This deletes the Series and all {DisplayFormat.FormatCount(group.TitlesOwned)} {Noun(items, group.TitlesOwned)} in it."
            : null;

    /// <summary>The red button always counts: "Delete 10 episodes and 10 files", or "Delete 14 books" when every file stays on disk.</summary>
    public static string ConfirmLabel(LibraryItemDeletionGroupDto group, IReadOnlyCollection<LibraryItemDeletionItemDto> items, int filesDeleted)
    {
        var titles = $"{DisplayFormat.FormatCount(group.TitlesOwned)} {Noun(items, group.TitlesOwned)}";
        return filesDeleted > 0
            ? $"Delete {titles} and {DisplayFormat.FormatCount(filesDeleted)} {(filesDeleted == 1 ? "file" : "files")}"
            : $"Delete {titles}";
    }

    /// <summary>The button while the Engine works: "Deleting 10 episodes…".</summary>
    public static string DeletingLabel(LibraryItemDeletionGroupDto group, IReadOnlyCollection<LibraryItemDeletionItemDto> items) =>
        $"Deleting {DisplayFormat.FormatCount(group.TitlesOwned)} {Noun(items, group.TitlesOwned)}…";

    /// <summary>The typed name matches ignoring capital letters and extra spaces (D4).</summary>
    public static bool NameMatches(string? typed, string confirmName) =>
        !string.IsNullOrWhiteSpace(confirmName)
        && string.Equals(Normalise(typed), Normalise(confirmName), StringComparison.CurrentCultureIgnoreCase);

    private static string Normalise(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The toast: <c>Deleted "The Expanse", Season 2. 18.4 GB freed.</c> (D8).</summary>
    public static string Success(LibraryItemDeletionResultDto result, string groupName)
    {
        var freed = DisplayFormat.FormatFileSize(result.BytesFreed);
        var message = $"Deleted {groupName}.";
        return freed is null ? message : $"{message} {freed} freed.";
    }

    /// <summary>How the group is named in the toast: <c>"The Expanse", Season 2</c> for a season, <c>"The Expanse"</c> otherwise.</summary>
    public static string DisplayName(LibraryItemDeletionGroupDto? group, string fallbackTitle) =>
        group is { Kind: "season", ParentTitle: { Length: > 0 } show }
            ? $"\"{show}\", {group.Title}"
            : $"\"{(group?.Title is { Length: > 0 } title ? title : fallbackTitle)}\"";

}
