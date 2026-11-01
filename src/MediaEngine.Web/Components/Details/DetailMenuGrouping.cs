using MediaEngine.Contracts.Details;

namespace MediaEngine.Web.Components.Details;

/// <summary>One row of the detail "..." menu: an action, or the quiet device note that stands where Edit would be.</summary>
/// <param name="Action">The action, or null for the note.</param>
/// <param name="Note">The note text when this row is the note.</param>
/// <param name="DividerBefore">True for the first row of every group after the first.</param>
public readonly record struct DetailMenuEntry(DetailAction? Action, string? Note, bool DividerBefore);

/// <summary>
/// The order of the detail "..." menu (details overlay spec D5): the personal and playback group, other utilities,
/// the edit group, then a final destructive group that is always last. Groups 4 to 8 are left empty so actions that
/// later sit after Edit and before Delete (Videos: Identify, Change thumbnail, in the shortcut group) slot in without reshuffling.
/// </summary>
public static class DetailMenuGrouping
{
    public const int PlaybackGroup = 0;
    public const int VisibilityGroup = 1;
    public const int UtilityGroup = 2;
    public const int EditGroup = 3;

    /// <summary>Shortcuts into Editing Mode sections (Videos: Identify, Change thumbnail): straight after Edit, before Delete.</summary>
    public const int ShortcutGroup = 4;
    public const int DestructiveGroup = 9;

    /// <summary>
    /// The Delete from library entry (Delete spec D17; details overlay spec D18, D19). The page adds it to its "..." menu and to
    /// the editing banner's "..." for a person who may delete a single title; the Engine never sends it.
    /// </summary>
    public static DetailAction DeleteFromLibraryAction { get; } = new()
    {
        Key = DetailActionKeys.DeleteFromLibrary,
        Label = "Delete from library…",
        Icon = "delete",
        IsDestructive = true,
    };

    /// <summary>
    /// The "Add to collection" entry (Collections and lists spec 4a): the page adds it to a single title's "..." menu for a profile that
    /// may make collections; the Engine never sends it. It opens the list of collections the person may edit.
    /// </summary>
    public static DetailAction AddToCollectionAction { get; } = new()
    {
        Key = DetailActionKeys.AddToCollection,
        Label = "Add to collection…",
        Icon = "playlist_add",
    };

    /// <summary>
    /// The Delete entry for a whole show, season, album or Series ("Delete show…", "Delete Season 2…", "Delete album…", "Delete Series…"):
    /// the last red entry of the page's "..." menu, as for a single title (Delete a show, season, album or Series D2).
    /// </summary>
    public static DetailAction DeleteGroupAction(string label, bool season = false) => new()
    {
        Key = season ? DetailActionKeys.DeleteSeason : DetailActionKeys.DeleteFromLibrary,
        Label = label,
        Icon = "delete",
        IsDestructive = true,
    };

    public static bool IsEditAction(DetailAction action) =>
        action.Key is DetailActionKeys.Edit or DetailActionKeys.EditMedia;

    /// <summary>The "..." shortcuts that open an Editing Mode section; like Edit, they disappear where the device cannot edit.</summary>
    public static bool IsEditShortcut(DetailAction action) =>
        action.Key is DetailActionKeys.Identify or DetailActionKeys.ChangeThumbnail;

    /// <summary>Anything flagged destructive, and Delete by key (including Delete from library), is red and goes last.</summary>
    public static bool IsDestructive(DetailAction action) =>
        action.IsDestructive || action.Key is "delete" or DetailActionKeys.DeleteFromLibrary;

    public static int GroupOf(DetailAction action)
    {
        if (IsDestructive(action))
        {
            return DestructiveGroup;
        }

        return action.Key switch
        {
            "play-from-beginning" or "restart" or "status-complete" or "status-reset" => PlaybackGroup,
            "status-hide" or "status-show" or "status-history" => VisibilityGroup,
            DetailActionKeys.Edit or DetailActionKeys.EditMedia => EditGroup,
            DetailActionKeys.Identify or DetailActionKeys.ChangeThumbnail => ShortcutGroup,
            _ => UtilityGroup,
        };
    }

    /// <summary>
    /// Orders the actions by group (a stable sort, with "play from the beginning" first within its group) and marks the
    /// first row of each later group with a divider. A note, when given, takes the edit group's place.
    /// </summary>
    public static IReadOnlyList<DetailMenuEntry> Arrange(IEnumerable<DetailAction> actions, string? note = null)
    {
        var rows = actions
            .Select(action => (Group: GroupOf(action), Action: (DetailAction?)action, Note: (string?)null))
            .ToList();
        if (!string.IsNullOrWhiteSpace(note))
        {
            rows.Add((EditGroup, null, note));
        }

        var ordered = rows
            .OrderBy(row => row.Group)
            .ThenBy(row => row.Action?.Key == "play-from-beginning" ? 0 : 1)
            .ToList();

        var entries = new List<DetailMenuEntry>(ordered.Count);
        for (var index = 0; index < ordered.Count; index++)
        {
            var row = ordered[index];
            entries.Add(new DetailMenuEntry(row.Action, row.Note, index > 0 && row.Group != ordered[index - 1].Group));
        }

        return entries;
    }
}
