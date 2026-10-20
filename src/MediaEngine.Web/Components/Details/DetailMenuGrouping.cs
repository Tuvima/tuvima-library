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
/// later sit after Edit and before Delete (Videos: Identify, Change thumbnail) slot in without reshuffling.
/// </summary>
public static class DetailMenuGrouping
{
    public const int PlaybackGroup = 0;
    public const int VisibilityGroup = 1;
    public const int UtilityGroup = 2;
    public const int EditGroup = 3;
    public const int DestructiveGroup = 9;

    public static bool IsEditAction(DetailAction action) =>
        action.Key is DetailActionKeys.Edit or DetailActionKeys.EditMedia;

    /// <summary>Anything flagged destructive, and Delete by key, is red and goes last.</summary>
    public static bool IsDestructive(DetailAction action) =>
        action.IsDestructive || action.Key == "delete";

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
