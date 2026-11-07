using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Ui;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Deleting supporting files from the browser or from "Editing N files" (universal editor v5 D29): it asks once, naming the file or the count, and
/// then happens at once without waiting for Save. One flow for both places, so they ask and answer the same way.
/// </summary>
public static class EditorFileBrowserDeleteFlow
{
    /// <summary>Asks, deletes through the browser's bulk-delete, and says what happened. Returns true when anything was removed.</summary>
    public static async Task<bool> DeleteAsync(
        EditorFileBrowserState state,
        IReadOnlyList<SupportingFileRowDto> rows,
        IAppDialogService dialogs,
        IAppToastService toasts,
        IStringLocalizer<SharedStrings> l)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return false;
        }

        var question = rows.Count == 1 ? string.Format(l["EditorBrowser_DeleteOne"].Value, rows[0].Title) : string.Format(l["EditorBrowser_DeleteMany"].Value, rows.Count);
        if (await AppConfirmDialog.ShowAsync(dialogs, question, l["EditorBrowser_DeleteBody"].Value, l["EditorBrowser_Delete"].Value, l["EditorBrowser_Keep"].Value, primaryDestructive: true) != AppConfirmResult.Primary)
        {
            return false;
        }

        var result = await state.DeleteAsync(rows);
        if (result.Removed > 0)
        {
            toasts.Add(result.Removed == 1 ? l["EditorBrowser_DeletedOne"].Value : string.Format(l["EditorBrowser_DeletedMany"].Value, result.Removed), AppSeverity.Success);
        }

        if (result.Failed > 0)
        {
            toasts.Add(string.IsNullOrWhiteSpace(result.Message) ? string.Format(l["EditorBrowser_DeleteFailed"].Value, result.Failed) : result.Message, AppSeverity.Error);
        }

        return result.Removed > 0;
    }
}
