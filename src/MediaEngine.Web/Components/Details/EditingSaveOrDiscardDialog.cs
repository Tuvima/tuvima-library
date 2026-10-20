using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Components.Details;

/// <summary>
/// The "Save or discard?" question Editing Mode asks (details overlay spec D9) when Done, Esc, Back or a close finds unsaved
/// input: the shared confirmation dialog with two buttons, Save and Discard. Closing it (Esc or the close button) cancels, so
/// the person stays in Editing Mode with their input intact.
/// </summary>
public static class EditingSaveOrDiscardDialog
{
    public const string Title = "Save or discard?";

    public const string Message = "You have changes that are not saved yet.";

    public static async Task<SaveOrDiscardChoice> AskAsync(IAppDialogService dialogs) =>
        await AppConfirmDialog.ShowAsync(dialogs, Title, Message, primaryLabel: "Save", secondaryLabel: "Discard") switch
        {
            AppConfirmResult.Primary => SaveOrDiscardChoice.Save,
            AppConfirmResult.Secondary => SaveOrDiscardChoice.Discard,
            _ => SaveOrDiscardChoice.Cancel,
        };
}
