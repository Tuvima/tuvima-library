using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Components.Details;

/// <summary>
/// The "Unsaved changes" question the universal editor asks (spec D20, D22) when Cancel, Esc or leaving the page finds staged changes:
/// the shared confirmation dialog in its warning tone, with "Keep editing" as the safe primary answer and "Discard changes" beside it.
/// Closing it any other way keeps the person editing.
/// </summary>
public static class EditingUnsavedChangesDialog
{
    public const string Title = "Unsaved changes";

    public const string Message = "You have changes that have not been saved. If you leave now they will be lost.";

    public const string DiscardLabel = "Discard changes";

    public const string KeepLabel = "Keep editing";

    /// <summary>True when the person chose to discard their changes.</summary>
    public static async Task<bool> AskDiscardAsync(IAppDialogService dialogs) =>
        await AppConfirmDialog.ShowAsync(dialogs, Title, Message, primaryLabel: KeepLabel, secondaryLabel: DiscardLabel, tone: AppUiTone.Warning)
            == AppConfirmResult.Secondary;
}
