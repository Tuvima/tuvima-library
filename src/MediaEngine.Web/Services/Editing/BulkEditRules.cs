using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Who may be ticked, which bulk actions the selection bar offers for the ticked rows, and the plain words around them (universal
/// editor spec D14). The bar hides what does not make sense; the panel and the Engine check again.
/// </summary>
public static class BulkEditRules
{
    /// <summary>Shown on a field whose ticked items disagree; nothing is staged until the field is changed.</summary>
    public const string MixedLeaveUnchanged = "Mixed, leave unchanged";

    /// <summary>The most titles the Engine deletes in one request (its own cap, <c>LibraryItemDeletionService.MaxWorks</c>); more than this asks to delete fewer.</summary>
    public const int MaxDeleteItems = 200;

    /// <summary>Why Delete is off above the cap.</summary>
    public const string DeleteLimitReason = "Delete up to 200 at a time";

    /// <summary>A row can be ticked when it is one title (a track, episode, audiobook part, book, issue or movie), never a season, album or series.</summary>
    public static bool IsSelectableKind(DetailEntityType kind) => LibraryItemDeletionFlow.IsSingleTitle(kind);

    /// <summary>
    /// Move is offered when every ticked row is the same kind of single title: the destination picker only offers places that accept
    /// that kind (D15), so a mixed selection has no one right answer.
    /// </summary>
    public static bool CanMove(IReadOnlyList<BulkEditRow> rows) =>
        rows.Count > 0
        && rows.All(row => IsSelectableKind(row.Kind))
        && rows.Select(row => row.Kind).Distinct().Count() == 1;

    /// <summary>
    /// Match is offered for songs of one album and episodes of one season only (spec D14): the Engine pairs each ticked row with the
    /// entry of that album or season it fits. Anything else has no list the ticked rows share.
    /// </summary>
    public static bool CanMatch(IReadOnlyList<BulkEditRow> rows, string? pageMediaType = null)
    {
        if (rows.Count == 0 || rows[0].ParentId is not { } parent || parent == Guid.Empty || rows.Any(row => row.ParentId != parent))
        {
            return false;
        }

        return AllEpisodes(rows) || AllSongs(rows, pageMediaType);
    }

    /// <summary>Renumber sequentially is for music: songs of one album, numbered in the order the list shows them.</summary>
    public static bool CanRenumber(IReadOnlyList<BulkEditRow> rows, string? pageMediaType = null) =>
        rows.Count > 0 && rows[0].ParentId is { } parent && parent != Guid.Empty && rows.All(row => row.ParentId == parent) && AllSongs(rows, pageMediaType);

    /// <summary>Change album is for music rows (any album); the destination picker only offers albums.</summary>
    public static bool CanChangeAlbum(IReadOnlyList<BulkEditRow> rows, string? pageMediaType = null) =>
        rows.Count > 0 && AllSongs(rows, pageMediaType);

    /// <summary>
    /// Every ticked row must be one title (a whole show or album is never deleted in one go). Who may delete is the single Delete's own gate
    /// (<see cref="LibraryItemDeletionFlow.CanDelete"/>), which the caller checks as well.
    /// </summary>
    public static bool AreDeletable(IReadOnlyList<BulkEditRow> rows) =>
        rows.Count > 0 && rows.All(row => IsSelectableKind(row.Kind));

    /// <summary>The bar's "..." menu: Delete from library, last and red, only for the person who may delete, and off with its reason above the Engine's cap.</summary>
    public static IReadOnlyList<DetailAction> BarMenuActions(IReadOnlyList<BulkEditRow> rows, bool canDelete, bool busy)
    {
        if (!canDelete || !AreDeletable(rows))
        {
            return [];
        }

        var overLimit = rows.Count > MaxDeleteItems;
        return
        [
            new DetailAction
            {
                Key = DetailActionKeys.DeleteFromLibrary,
                Label = DetailMenuGrouping.DeleteFromLibraryAction.Label,
                Icon = DetailMenuGrouping.DeleteFromLibraryAction.Icon,
                IsDestructive = true,
                IsDisabled = busy || overLimit,
                Tooltip = overLimit ? DeleteLimitReason : null,
            },
        ];
    }

    /// <summary>"song", "episode" or "item": the plain noun for what is ticked.</summary>
    public static string Noun(IReadOnlyList<BulkEditRow> rows, string? pageMediaType = null)
    {
        var plural = rows.Count != 1;
        if (AllEpisodes(rows))
        {
            return plural ? "episodes" : "episode";
        }

        if (AllSongs(rows, pageMediaType))
        {
            return plural ? "songs" : "song";
        }

        return plural ? "items" : "item";
    }

    /// <summary>The panel's title: "Edit 4 songs".</summary>
    public static string EditTitle(IReadOnlyList<BulkEditRow> rows, string? pageMediaType = null) =>
        $"Edit {rows.Count} {Noun(rows, pageMediaType)}";

    /// <summary>The Move entry's label for these rows: "Move to album" or "Move to season" for music and TV, else the single Move's own wording.</summary>
    public static string MoveLabel(IReadOnlyList<BulkEditRow> rows, string? pageMediaType = null)
    {
        if (AllEpisodes(rows))
        {
            return "Move to season";
        }

        return AllSongs(rows, pageMediaType) ? "Move to album" : LibraryMoveText.MoveLabel(MediaTypeOf(rows, pageMediaType));
    }

    public static string SelectedLabel(int count) => $"{count} selected";

    public static string SelectLabel(string title) => $"Select {title}";

    private static bool AllEpisodes(IReadOnlyList<BulkEditRow> rows) =>
        rows.Count > 0 && rows.All(row => row.Kind == DetailEntityType.TvEpisode);

    private static bool AllSongs(IReadOnlyList<BulkEditRow> rows, string? pageMediaType) =>
        rows.Count > 0
        && rows.All(row => row.Kind == DetailEntityType.Work && string.Equals(row.MediaType ?? pageMediaType, "Music", StringComparison.OrdinalIgnoreCase));

    private static string? MediaTypeOf(IReadOnlyList<BulkEditRow> rows, string? pageMediaType) =>
        rows.Select(row => row.MediaType).FirstOrDefault(type => !string.IsNullOrWhiteSpace(type)) ?? pageMediaType;
}
