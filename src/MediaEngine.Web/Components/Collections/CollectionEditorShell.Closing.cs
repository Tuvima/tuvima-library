using Microsoft.AspNetCore.Components.Routing;

namespace MediaEngine.Web.Components.Collections;

public partial class CollectionEditorShell
{
    private async Task ConfirmNavigationWithUnsavedChanges(LocationChangingContext context)
    {
        if (_hasUnsavedChanges && !await ConfirmDiscardAsync())
            context.PreventNavigation();
    }

    // One dialog style for every confirmation (details overlay spec D17): closing it any other way keeps the person editing.
    private async Task<bool> ConfirmDiscardAsync() =>
        await AppConfirmDialog.ShowAsync(
            Dialogs,
            "Discard your changes?",
            "You have changes that are not saved yet.",
            primaryLabel: "Discard",
            secondaryLabel: "Keep editing",
            primaryDestructive: true,
            tone: AppUiTone.Warning) == AppConfirmResult.Primary;

    private async Task<bool> ConfirmDeleteCollectionAsync(string name) =>
        await AppConfirmDialog.ShowAsync(
            Dialogs,
            $"Delete {name}?",
            "The titles in it stay in your library.",
            primaryLabel: "Delete",
            secondaryLabel: "Keep",
            primaryDestructive: true) == AppConfirmResult.Primary;

    private async Task Cancel()
    {
        if (!await CanCloseAsync()) return;
        await CloseEditorAsync(applied: _artworkChanged);
    }

    private async Task<bool> CanCloseAsync()
    {
        if (_saving) return false;
        if (_hasUnsavedChanges && !await ConfirmDiscardAsync())
            return false;
        _hasUnsavedChanges = false;
        return true;
    }

    private async Task CloseEditorAsync(bool applied)
    {
        if (Inline)
        {
            await Closed.InvokeAsync(applied);
            return;
        }

        // Save or discard has already been approved by this editor's own flow.
        _dialogCloseGuard?.Dispose();
        _dialogCloseGuard = null;

        if (applied)
            DialogContext?.Close(AppDialogResult.Ok(true));
        else
            DialogContext?.Cancel();
    }
}
