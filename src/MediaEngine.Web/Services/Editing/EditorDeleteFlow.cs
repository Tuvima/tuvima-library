using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Delete from the universal editor's header "..." (v4 D13): asks once with the shared Delete from library box, deletes at once (it does
/// not wait for Save), and then lets the editor drop what went and move to the parent. Contents and Files run their own deletes through the
/// same box and the same <see cref="EditorHost.ItemsDeletedAsync"/>.
/// </summary>
public static class EditorDeleteFlow
{
    /// <summary>
    /// True when the header offers Delete: a library item (not a person, collection or Universe) with its fields loaded, no several-items panel
    /// open, for an administrator on a desktop. The Engine still decides on every call.
    /// </summary>
    public static bool CanDeleteActive(EditorHost editor, DeviceContextService? device, DashboardSessionAccessor? session)
    {
        ArgumentNullException.ThrowIfNull(editor);
        return editor.Batch.Panel == BulkPanelKind.None
            && editor.Binding?.ShowHistory == true
            && editor.Mode.Fields.Loaded
            && editor.Mode.Fields.SubjectKind is null
            && editor.Mode.Fields.SupportingFile is null
            && editor.Session.ActiveItem is not null
            && LibraryItemDeletionFlow.CanDelete(device, session);
    }

    /// <summary>
    /// True when the header offers Delete for a character, place or faction that someone added by hand to a Universe (universal editor v4,
    /// WP11). A thing that came from Wikidata is hidden on its page instead, never deleted.
    /// </summary>
    public static bool CanDeleteThing(EditorHost editor, DeviceContextService? device, DashboardSessionAccessor? session)
    {
        ArgumentNullException.ThrowIfNull(editor);
        return editor.Batch.Panel == BulkPanelKind.None
            && editor.Mode.Fields.Loaded
            && editor.Mode.Fields.SubjectKind == MediaEngine.Contracts.Editing.EditorSubjectKinds.FictionalEntity
            && EditorUniverseRules.IsAddedByUser(editor.Mode.Fields.Find("wikidata")?.DisplayValue ?? editor.Mode.Fields.Find("wikidata")?.Value)
            && editor.Session.ActiveItem is { } active
            && active.Kind == EditTrailKinds.Thing
            && LibraryItemDeletionFlow.CanDelete(device, session);
    }

    /// <summary>
    /// Asks once, then deletes the hand-added thing being edited at once, without waiting for Save. Deleted false means nothing was
    /// deleted; the result's notice (a refusal or a failure) is shown in the editor window.
    /// </summary>
    public static async Task<EditorDeleteResult> DeleteThingAsync(
        EditorHost editor,
        IAppDialogService dialogs,
        DeviceContextService? device,
        DashboardSessionAccessor? session)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(dialogs);
        if (editor.Session.ActiveItem is not { } active)
        {
            return EditorDeleteResult.Nothing;
        }

        if (LibraryItemDeletionFlow.Refusal(device, session) is { } refusal)
        {
            return EditorDeleteResult.Failed(refusal);
        }

        var answer = await AppConfirmDialog.ShowAsync(
            dialogs,
            $"Delete {active.Title}?",
            "This removes it from the Universe for good. Anything you changed on it goes too. It was added by you, so nothing on Wikidata is touched.",
            "Delete",
            "Keep",
            primaryDestructive: true);
        if (answer != AppConfirmResult.Primary)
        {
            return EditorDeleteResult.Nothing;
        }

        return await editor.DeleteUniverseThingAsync(active.EntityId)
            ? new EditorDeleteResult(true)
            : EditorDeleteResult.Failed($"{active.Title} could not be deleted. Nothing was changed.");
    }

    /// <summary>The whole show, season, album or Series the editor is on; null for a single title.</summary>
    public static LibraryGroupTarget? ActiveGroup(EditorHost editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (editor.Session.ActiveItem is not { } active)
        {
            return null;
        }

        var group = EditorDeleteRules.GroupFor(active.EntityId, active.Title, editor.Mode.Fields.ScopeId, editor.Mode.Fields.MediaType);
        return group is null ? null : group with { SupportingFileCount = EditorDeleteRules.SupportingFilesIn(group, editor.Mode.Fields.Header) };
    }

    /// <summary>
    /// Deletes the item being edited, including a whole show, season, album or Series. Deleted false means nothing was deleted (the person
    /// said no, or this device or person may not delete; the notice says which). On success the editor has moved to the item's parent, or
    /// closed when it has none; the notice then says when files stayed on disk.
    /// </summary>
    public static async Task<EditorDeleteResult> DeleteActiveAsync(
        EditorHost editor,
        IAppDialogService dialogs,
        DeviceContextService? device,
        DashboardSessionAccessor? session)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(dialogs);
        if (editor.Session.ActiveItem is not { } active)
        {
            return EditorDeleteResult.Nothing;
        }

        if (LibraryItemDeletionFlow.Refusal(device, session) is { } refusal)
        {
            return EditorDeleteResult.Failed(refusal);
        }

        using var deleting = editor.MarkDeleting();
        var group = ActiveGroup(editor);
        var answer = group is null
            ? await LibraryItemDeletionFlow.RunForResultAsync(dialogs, [active.EntityId], active.Title)
            : await LibraryItemDeletionFlow.RunGroupAsync(dialogs, group);
        if (answer is null)
        {
            return EditorDeleteResult.Nothing;
        }

        await editor.ItemsDeletedAsync(EditorDeleteRules.Removed(answer, [active.EntityId], group, editor.Family.ParentOf));
        return new EditorDeleteResult(true, LibraryItemDeletionFlow.NoticeFor(answer));
    }
}
