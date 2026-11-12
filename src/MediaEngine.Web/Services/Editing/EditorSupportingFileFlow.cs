using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// What a supporting file's editor does besides the form (universal editor v5 WP7): Delete, which asks once and happens at once like every
/// other Delete in the editor (D29), and the words it uses. The file's own fields are staged by <see cref="SupportingFileStaging"/> and saved
/// with the rest of the session.
/// </summary>
public static class EditorSupportingFileFlow
{
    /// <summary>The confirmation's heading names the file as it reads in the Library.</summary>
    public static string Question(EditorSupportingFileDto file) => $"Delete {file.File.FileName}?";

    /// <summary>What the confirmation says goes: the record always; the file on disk only where Library may touch it.</summary>
    public static string Body(EditorSupportingFileDto file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return file.ReadOnly
            ? "It will be removed from the Library. The file stays where it is: it is in a Read-only folder, which Library never changes."
            : "It will be removed from the Library, and the file is removed from its folder as your library's file settings say.";
    }

    /// <summary>The line after a delete: what went, and where the file stayed when it did.</summary>
    public static string Done(string fileName, SupportingFileDeleteResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Disk switch
        {
            SupportingFileDiskOutcomes.Kept => $"Deleted {fileName} from the Library. The file stays on disk{(string.IsNullOrWhiteSpace(result.Reason) ? "." : $": {result.Reason.TrimEnd('.')}.")}",
            SupportingFileDiskOutcomes.Recycled => $"Deleted {fileName}. The file went to the Recycle bin.",
            _ => $"Deleted {fileName}.",
        };
    }

    /// <summary>True when the footer offers Delete for the supporting file being edited: an administrator on a device that may edit items, with the file loaded and no several-items panel open.</summary>
    public static bool CanDelete(EditorHost editor, DeviceContextService? device, DashboardSessionAccessor? session)
    {
        ArgumentNullException.ThrowIfNull(editor);
        return editor.Batch.Panel == BulkPanelKind.None
            && editor.Mode.Fields.Loaded
            && editor.Mode.Fields.SupportingFile is not null
            && editor.Session.ActiveItem is { Kind: EditTrailKinds.SupportingFile }
            && LibraryItemDeletionFlow.CanDelete(device, session);
    }

    /// <summary>
    /// Asks once, then deletes the supporting file being edited at once, without waiting for Save. False means nothing was deleted (the person
    /// said no, this device or person may not delete, or the Engine refused; the notice says which). On success the editor has moved back to
    /// the title the file belonged to, and the notice says so when the file stayed on disk.
    /// </summary>
    public static async Task<EditorDeleteResult> DeleteAsync(
        EditorHost editor,
        IAppDialogService dialogs,
        DeviceContextService? device,
        DashboardSessionAccessor? session,
        IAdministratorSurfaceAccessService? administratorAccess = null)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(dialogs);
        if (editor.Mode.Fields.SupportingFile is not { } file)
        {
            return EditorDeleteResult.Nothing;
        }

        // A locked administrator is asked for the PIN before the question; closing the prompt stops quietly.
        var deleteGate = await LibraryItemDeletionFlow.EnsureMayDeleteAsync(device, session, administratorAccess);
        if (!deleteGate.Allowed)
        {
            return deleteGate.Message is { } refusal ? EditorDeleteResult.Failed(refusal) : EditorDeleteResult.Nothing;
        }

        using var deleting = editor.MarkDeleting();
        var answer = await AppConfirmDialog.ShowAsync(dialogs, Question(file), Body(file), "Delete", "Keep", primaryDestructive: true);
        if (answer != AppConfirmResult.Primary)
        {
            return EditorDeleteResult.Nothing;
        }

        var result = await editor.DeleteSupportingFileAsync(file.TitleId, Guid.Parse(file.File.Id));
        if (result is not { Deleted: true })
        {
            return EditorDeleteResult.Failed(
                string.IsNullOrWhiteSpace(result?.Message)
                    ? $"{file.File.FileName} could not be deleted. Nothing was changed."
                    : $"{file.File.FileName} could not be deleted. {result.Message}");
        }

        // The file leaving the editor is the confirmation; only a file that stayed on disk is worth a line.
        return new EditorDeleteResult(true, result.Disk == SupportingFileDiskOutcomes.Kept ? Done(file.File.FileName, result) : null);
    }
}
