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
    /// Asks once, then deletes the hand-added thing being edited at once, without waiting for Save. False means nothing was deleted.
    /// </summary>
    public static async Task<bool> DeleteThingAsync(
        EditorHost editor,
        IAppDialogService dialogs,
        IAppToastService toasts,
        DeviceContextService? device,
        DashboardSessionAccessor? session)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(toasts);
        if (editor.Session.ActiveItem is not { } active || LibraryItemDeletionFlow.TryRefuse(device, session, toasts))
        {
            return false;
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
            return false;
        }

        if (!await editor.DeleteUniverseThingAsync(active.EntityId))
        {
            toasts.Add($"{active.Title} could not be deleted. Nothing was changed.", AppSeverity.Error);
            return false;
        }

        toasts.Add($"Deleted {active.Title}.", AppSeverity.Success);
        return true;
    }

    /// <summary>The whole show, season, album or Series the editor is on; null for a single title.</summary>
    public static LibraryGroupTarget? ActiveGroup(EditorHost editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        return editor.Session.ActiveItem is { } active
            ? EditorDeleteRules.GroupFor(active.EntityId, active.Title, editor.Mode.Fields.ScopeId, editor.Mode.Fields.MediaType)
            : null;
    }

    /// <summary>
    /// Deletes the item being edited, including a whole show, season, album or Series. False means nothing was deleted (the person said no,
    /// or this device or person may not delete). On success the editor has moved to the item's parent, or closed when it has none.
    /// </summary>
    public static async Task<bool> DeleteActiveAsync(
        EditorHost editor,
        IAppDialogService dialogs,
        IAppToastService toasts,
        DeviceContextService? device,
        DashboardSessionAccessor? session)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(toasts);
        if (editor.Session.ActiveItem is not { } active || LibraryItemDeletionFlow.TryRefuse(device, session, toasts))
        {
            return false;
        }

        using var deleting = editor.MarkDeleting();
        var group = ActiveGroup(editor);
        var answer = group is null
            ? await LibraryItemDeletionFlow.RunForResultAsync(dialogs, toasts, [active.EntityId], active.Title)
            : await LibraryItemDeletionFlow.RunGroupAsync(dialogs, toasts, group);
        if (answer is null)
        {
            return false;
        }

        await editor.ItemsDeletedAsync(EditorDeleteRules.Removed(answer, [active.EntityId], group, editor.Family.ParentOf));
        return true;
    }
}
