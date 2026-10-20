using MediaEngine.Contracts.Items;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Formatting;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Components.Shared;

/// <summary>
/// What Delete from library says: the confirmation dialog's headings and states, and the toasts the caller shows afterwards.
/// One place so the dialog, the detail page and the Listen song table agree word for word.
/// </summary>
public static class LibraryItemDeletionText
{
    public const string Checking = "Checking what will be deleted…";
    public const string Changed = "Something changed since you opened this. Here's the updated list.";
    public const string Unavailable = "Couldn't reach the Engine. Nothing was deleted.";
    public const string Unconfirmed = "The Engine didn't answer in time. Check your library before trying again.";
    public const string Forbidden = "You need administrator access to delete items.";
    public const string NoLongerInLibrary = "This item is no longer in your library.";
    public const string InvalidFallback = "The Engine couldn't delete these items. Nothing was deleted.";

    /// <summary>The words for what is being deleted: "songs" when every title is music, otherwise "titles".</summary>
    public static string Noun(IReadOnlyCollection<LibraryItemDeletionItemDto> items) =>
        items.Count > 0 && items.All(IsMusic) ? "songs" : "titles";

    // "Books" and an empty type also classify as music by default; only a type that really names music counts.
    private static bool IsMusic(LibraryItemDeletionItemDto item) =>
        MediaKindClassifier.TryClassifyKnown(item.MediaType, out var experience) && experience == PlaybackExperience.Music;

    /// <summary>
    /// The dialog's question. One title names it; several say how many. <paramref name="requested"/> is how many the caller asked about,
    /// used until the Engine's preview names the items.
    /// </summary>
    public static string Heading(string? title, IReadOnlyCollection<LibraryItemDeletionItemDto> items, int requested)
    {
        var count = items.Count > 0 ? items.Count : requested;
        if (count > 1)
        {
            return $"Delete {count} {Noun(items)} from your library?";
        }

        var name = !string.IsNullOrWhiteSpace(title) ? title : items.FirstOrDefault()?.Title;
        return string.IsNullOrWhiteSpace(name)
            ? "Delete this title from your library?"
            : $"Delete \"{name}\" from your library?";
    }

    /// <summary>The toast after a deletion: what went, and how much space came back (left out when nothing did).</summary>
    public static string Success(LibraryItemDeletionResultDto result, string? singleTitle, string noun)
    {
        var count = Math.Max(result.DeletedWorkIds.Count, result.Titles.Count);
        var message = count <= 1
            ? $"Deleted \"{(result.Titles.FirstOrDefault() ?? singleTitle)}\"."
            : $"Deleted {count} {noun}.";
        var freed = DisplayFormat.FormatFileSize(result.BytesFreed);
        return freed is null ? message : $"{message} {freed} freed.";
    }

    /// <summary>The warning toast when files could not be removed from disk; null when every file went.</summary>
    public static string? FailedFiles(LibraryItemDeletionResultDto result) => result.FilesFailed.Count switch
    {
        0 => null,
        1 => "1 file couldn't be removed and was left on disk.",
        var many => $"{many} files couldn't be removed and were left on disk.",
    };
}

/// <summary>
/// How the confirmation dialog ended when it did not simply cancel: the titles were deleted, or they were already gone.
/// <see cref="Noun"/> is "songs" or "titles", for the toast.
/// </summary>
public sealed record LibraryItemDeletionDialogResult(LibraryItemDeletionResultDto? Deleted, bool NoLongerInLibrary, string Noun);
