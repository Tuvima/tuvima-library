using MediaEngine.Contracts.Details;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Components.Shared;

/// <summary>How a run of the Delete from library flow ended.</summary>
public enum LibraryItemDeletionFlowOutcome
{
    /// <summary>The person cancelled, or the dialog was dismissed. Nothing was deleted.</summary>
    Canceled,

    /// <summary>The titles were deleted; the success toast (and any warning toast) has been shown.</summary>
    Deleted,

    /// <summary>The titles were already gone; the "no longer in your library" toast has been shown.</summary>
    NoLongerInLibrary,
}

/// <summary>
/// The caller's side of Delete from library, shared by the detail page and the Listen song table: who may start it, opening the
/// confirmation dialog, and the toasts afterwards. The Engine still decides on every call; this only keeps the button honest.
/// </summary>
public static class LibraryItemDeletionFlow
{
    /// <summary>
    /// True for a detail page that is one Work (a film, an episode, a book, an audiobook, a comic issue). Groups (a show, season, album or
    /// series) are not deleted in one go (spec D14), so they get no Delete entry.
    /// </summary>
    public static bool IsSingleTitle(DetailEntityType entityType) =>
        entityType is DetailEntityType.Work or DetailEntityType.Movie or DetailEntityType.TvEpisode or DetailEntityType.Book
            or DetailEntityType.Audiobook or DetailEntityType.ComicIssue;

    /// <summary>Delete is for an effective administrator on a desktop with a fine, hover-capable pointer (spec D2).</summary>
    public static bool CanDelete(DeviceContextService? device, DashboardSessionAccessor? session) =>
        EditorDeviceRule.CanEdit(device) && session?.Authority?.EffectiveAdministrator == true;

    /// <summary>
    /// Returns true (and says why in a toast) when this device or person may not delete. Handlers call it before opening the dialog,
    /// so a hidden button is never the only protection.
    /// </summary>
    public static bool TryRefuse(DeviceContextService? device, DashboardSessionAccessor? session, IAppToastService toasts)
    {
        if (EditorDeviceRule.TryRefuse(device, toasts))
        {
            return true;
        }

        if (session?.Authority?.EffectiveAdministrator == true)
        {
            return false;
        }

        toasts.Add(LibraryItemDeletionText.Forbidden, AppSeverity.Info);
        return true;
    }

    /// <summary>True for a page that is a show, an album or a Series (the group menu entries; a season is a part of a show page).</summary>
    public static bool IsGroupPage(DetailEntityType entityType) =>
        entityType is DetailEntityType.TvShow or DetailEntityType.MusicAlbum or DetailEntityType.BookSeries or DetailEntityType.ComicSeries;

    /// <summary>
    /// Opens the confirmation for a whole show, season, album or Series and shows the toasts that follow. The Engine finds the titles
    /// inside it, so nothing is sent but the group.
    /// </summary>
    public static async Task<LibraryItemDeletionDialogResult?> RunGroupAsync(
        IAppDialogService dialogs,
        IAppToastService toasts,
        LibraryGroupTarget target)
    {
        var dialog = await dialogs.ShowAsync<DeleteFromLibraryDialog>(
            string.Empty,
            new AppDialogParameters<DeleteFromLibraryDialog>
            {
                { component => component.WorkIds, (IReadOnlyList<Guid>)[] },
                { component => component.Group, target },
            },
            new AppDialogOptions
            {
                MaxWidth = AppMaxWidth.Medium,
                FullWidth = true,
                CloseButton = false,
                NoHeader = true,
                CloseOnEscapeKey = true,
            });

        if (await dialog.Result is not { Canceled: false, Data: LibraryItemDeletionDialogResult answer })
        {
            return null;
        }

        if (answer.NoLongerInLibrary || answer.Deleted is null)
        {
            toasts.Add(LibraryItemDeletionText.NoLongerInLibrary, AppSeverity.Info);
            return answer;
        }

        toasts.Add(LibraryGroupDeletionText.Success(answer.Deleted, answer.GroupName ?? $"\"{target.Title}\""), AppSeverity.Success);
        if (LibraryItemDeletionText.FailedFiles(answer.Deleted) is { } warning)
        {
            toasts.Add(warning, AppSeverity.Warning);
        }

        return answer;
    }

    /// <summary>Opens the confirmation for these Works and shows the toasts that follow. <paramref name="title"/> names a single title.</summary>
    public static async Task<LibraryItemDeletionFlowOutcome> RunAsync(
        IAppDialogService dialogs,
        IAppToastService toasts,
        IReadOnlyList<Guid> workIds,
        string? title = null)
    {
        var dialog = await dialogs.ShowAsync<DeleteFromLibraryDialog>(
            string.Empty,
            new AppDialogParameters<DeleteFromLibraryDialog>
            {
                { component => component.WorkIds, workIds },
                { component => component.ItemTitle, title },
            },
            new AppDialogOptions
            {
                MaxWidth = AppMaxWidth.Medium,
                FullWidth = true,
                CloseButton = false,
                NoHeader = true,
                CloseOnEscapeKey = true,
            });

        if (await dialog.Result is not { Canceled: false, Data: LibraryItemDeletionDialogResult answer })
        {
            return LibraryItemDeletionFlowOutcome.Canceled;
        }

        if (answer.NoLongerInLibrary || answer.Deleted is null)
        {
            toasts.Add(LibraryItemDeletionText.NoLongerInLibrary, AppSeverity.Info);
            return LibraryItemDeletionFlowOutcome.NoLongerInLibrary;
        }

        toasts.Add(LibraryItemDeletionText.Success(answer.Deleted, title, answer.Noun), AppSeverity.Success);
        if (LibraryItemDeletionText.FailedFiles(answer.Deleted) is { } warning)
        {
            toasts.Add(warning, AppSeverity.Warning);
        }

        return LibraryItemDeletionFlowOutcome.Deleted;
    }
}
