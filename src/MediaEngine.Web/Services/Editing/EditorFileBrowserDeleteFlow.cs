using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Deleting supporting files from the browser or from "Editing N files" (universal editor v5 D29): it asks once, naming the file or the count, and
/// then happens at once without waiting for Save. One flow for both places, so they ask and answer the same way.
/// </summary>
public static class EditorFileBrowserDeleteFlow
{
    /// <summary>
    /// Asks, deletes through the browser's bulk-delete, and hands back what happened. Deleted is true when anything was removed (the rows leaving
    /// the list is the confirmation); the notice is the failure the caller shows in place, when some or all of the files could not be deleted.
    /// </summary>
    public static async Task<EditorDeleteResult> DeleteAsync(
        EditorFileBrowserState state,
        IReadOnlyList<SupportingFileRowDto> rows,
        IAppDialogService dialogs,
        IStringLocalizer<SharedStrings> l,
        DashboardSessionAccessor? session = null,
        IAdministratorSurfaceAccessService? administratorAccess = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return EditorDeleteResult.Nothing;
        }

        // A locked administrator is asked for the PIN before the question; closing the prompt stops quietly.
        if (!await LibraryItemDeletionFlow.EnsureUnlockedForDeleteAsync(session, administratorAccess))
        {
            return EditorDeleteResult.Nothing;
        }

        var question = rows.Count == 1 ? string.Format(l["EditorBrowser_DeleteOne"].Value, rows[0].Title) : string.Format(l["EditorBrowser_DeleteMany"].Value, rows.Count);
        if (await AppConfirmDialog.ShowAsync(dialogs, question, l["EditorBrowser_DeleteBody"].Value, l["EditorBrowser_Delete"].Value, l["EditorBrowser_Keep"].Value, primaryDestructive: true) != AppConfirmResult.Primary)
        {
            return EditorDeleteResult.Nothing;
        }

        var result = await state.DeleteAsync(rows);
        var notice = result.Failed > 0
            ? string.IsNullOrWhiteSpace(result.Message) ? string.Format(l["EditorBrowser_DeleteFailed"].Value, result.Failed) : result.Message
            : null;
        return new EditorDeleteResult(result.Removed > 0, notice, notice is not null);
    }
}
