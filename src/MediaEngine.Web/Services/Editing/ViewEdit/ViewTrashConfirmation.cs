using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// The one "Are you sure?" step in front of Trash and Archive anywhere in View (spec view-section D7 and WP1): the selection bar, a tile's menu, the viewer,
/// a Gallery's viewer, Places and Documents all ask here, once, and the step happens at once after a yes. Closing the dialog is a No.
/// </summary>
public static class ViewTrashConfirmation
{
    /// <summary>Title for moving <paramref name="count"/> items to Trash.</summary>
    public static string TrashTitle(int count) => count == 1 ? "Move this item to Trash?" : $"Move {count} items to Trash?";

    /// <summary>Title for archiving <paramref name="count"/> items.</summary>
    public static string ArchiveTitle(int count) => count == 1 ? "Archive this item?" : $"Archive {count} items?";

    /// <summary>Asks before Trash. True only when the person chose Move to Trash.</summary>
    public static async Task<bool> AskTrashAsync(IAppDialogService dialogs, int count = 1) =>
        await AppConfirmDialog.ShowAsync(
            dialogs, TrashTitle(count), "You can bring it back from Trash until Trash is emptied.", "Move to Trash", "Cancel", primaryDestructive: true) == AppConfirmResult.Primary;

    /// <summary>Asks before Archive. True only when the person chose Archive.</summary>
    public static async Task<bool> AskArchiveAsync(IAppDialogService dialogs, int count = 1) =>
        await AppConfirmDialog.ShowAsync(
            dialogs, ArchiveTitle(count), "It leaves Photos and stays in Archive, where you can restore it.", "Archive", "Cancel") == AppConfirmResult.Primary;
}
