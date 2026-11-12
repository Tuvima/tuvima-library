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

    /// <summary>The titles were deleted. The titles leaving the screen is the confirmation; <see cref="LibraryItemDeletionFlowResult.Notice"/> says so when files stayed on disk.</summary>
    Deleted,

    /// <summary>The titles were already gone; <see cref="LibraryItemDeletionFlowResult.Notice"/> says so.</summary>
    NoLongerInLibrary,
}

/// <summary>How a run ended, and the line the caller shows in place (an alert) when there is one: files left on disk, or titles already gone.</summary>
public readonly record struct LibraryItemDeletionFlowResult(LibraryItemDeletionFlowOutcome Outcome, string? Notice = null);

/// <summary>The answer of <see cref="LibraryItemDeletionFlow.EnsureMayDeleteAsync"/>: whether Delete may go on to its question, and the line to show when it may not (null when the PIN prompt was simply closed).</summary>
public readonly record struct LibraryDeleteGate(bool Allowed, string? Message);

/// <summary>
/// The caller's side of Delete from library, shared by the detail page and the Listen song table: who may start it, opening the
/// confirmation dialog, and the notice afterwards (handed back to the caller to show in place; nothing is drawn over the page). The Engine still decides on every call; this only keeps the button honest.
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

    /// <summary>Delete is for an effective administrator on a desktop, tablet or phone (spec D2; editor v6 D19).</summary>
    public static bool CanDelete(DeviceContextService? device, DashboardSessionAccessor? session) =>
        EditorDeviceRule.CanEditItems(device) && session?.Authority?.EffectiveAdministrator == true;

    /// <summary>
    /// Says why this device or person may not delete, or null when they may. Handlers call it before opening the dialog, so a hidden
    /// button is never the only protection, and show the line in place when it is not null.
    /// </summary>
    public static string? Refusal(DeviceContextService? device, DashboardSessionAccessor? session)
    {
        if (EditorDeviceRule.TryRefuseItems(device))
        {
            return EditorDeviceRule.ItemMessage;
        }

        return session?.Authority?.EffectiveAdministrator == true ? null : LibraryItemDeletionText.Forbidden;
    }

    /// <summary>
    /// The check every Delete handler runs before it asks "are you sure". The device and the person must be allowed
    /// (<see cref="Refusal"/>), and the administrator settings must be unlocked, because the Engine deletes only for an administrator
    /// whose PIN (where one is set) has been entered. A locked administrator is asked for the PIN first, with the same prompt as Edit;
    /// closing it stops quietly (<see cref="LibraryDeleteGate.Message"/> is null). Nothing here allows a delete: the Engine decides again.
    /// </summary>
    public static async Task<LibraryDeleteGate> EnsureMayDeleteAsync(
        DeviceContextService? device,
        DashboardSessionAccessor? session,
        IAdministratorSurfaceAccessService? administratorAccess,
        CancellationToken ct = default)
    {
        if (Refusal(device, session) is { } refusal)
        {
            return new(false, refusal);
        }

        if (IsUnlocked(session))
        {
            return new(true, null);
        }

        if (administratorAccess is null)
        {
            return new(false, LibraryItemDeletionText.Forbidden);
        }

        return await administratorAccess.EnsureUnlockedAsync(ct) && IsUnlocked(session)
            ? new(true, null)
            : new(false, null);
    }

    /// <summary>
    /// For a delete whose place already decided who may use it (the supporting-files browser inside the editor): only an administrator
    /// whose settings are locked, for instance because a timed unlock ran out while the editor was open, is asked for the PIN before
    /// the question. Everyone else passes straight through to the existing checks. False means stop quietly (the prompt was closed).
    /// </summary>
    public static async Task<bool> EnsureUnlockedForDeleteAsync(
        DashboardSessionAccessor? session,
        IAdministratorSurfaceAccessService? administratorAccess,
        CancellationToken ct = default)
    {
        if (session?.Authority?.EffectiveAdministrator != true || IsUnlocked(session))
        {
            return true;
        }

        return administratorAccess is not null && await administratorAccess.EnsureUnlockedAsync(ct) && IsUnlocked(session);
    }

    private static bool IsUnlocked(DashboardSessionAccessor? session) =>
        session?.Authority is { EffectiveAdministrator: true, AdministratorSurfaceUnlocked: true } authority
        && (authority.AdministratorUnlockExpiresAt is null || authority.AdministratorUnlockExpiresAt > DateTimeOffset.UtcNow);

    /// <summary>
    /// The line to show in place after a delete, or null when there is nothing to add: the titles already being gone, or files that
    /// could not be removed from disk. A plain success needs none; the titles leaving the screen says it.
    /// </summary>
    public static string? NoticeFor(LibraryItemDeletionDialogResult? answer)
    {
        if (answer is null)
        {
            return null;
        }

        if (answer.NoLongerInLibrary || answer.Deleted is null)
        {
            return LibraryItemDeletionText.NoLongerInLibrary;
        }

        return LibraryItemDeletionText.FailedFiles(answer.Deleted);
    }

    /// <summary>True for a page that is a show, an album or a Series (the group menu entries; a season is a part of a show page).</summary>
    public static bool IsGroupPage(DetailEntityType entityType) =>
        entityType is DetailEntityType.TvShow or DetailEntityType.MusicAlbum or DetailEntityType.BookSeries or DetailEntityType.ComicSeries;

    /// <summary>
    /// Opens the confirmation for a whole show, season, album or Series. The Engine finds the titles inside it, so nothing is sent but
    /// the group. Null means the person cancelled; the caller shows <see cref="NoticeFor"/> of the answer in place.
    /// </summary>
    public static async Task<LibraryItemDeletionDialogResult?> RunGroupAsync(
        IAppDialogService dialogs,
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

        return answer;
    }

    /// <summary>Opens the confirmation for these Works. <paramref name="title"/> names a single title.</summary>
    public static async Task<LibraryItemDeletionFlowResult> RunAsync(
        IAppDialogService dialogs,
        IReadOnlyList<Guid> workIds,
        string? title = null)
    {
        var answer = await RunForResultAsync(dialogs, workIds, title);
        if (answer is null)
        {
            return new LibraryItemDeletionFlowResult(LibraryItemDeletionFlowOutcome.Canceled);
        }

        var outcome = answer.NoLongerInLibrary || answer.Deleted is null
            ? LibraryItemDeletionFlowOutcome.NoLongerInLibrary
            : LibraryItemDeletionFlowOutcome.Deleted;
        return new LibraryItemDeletionFlowResult(outcome, NoticeFor(answer));
    }

    /// <summary>
    /// <see cref="RunAsync"/> that also hands back what the Engine answered (which titles went), for a caller that has to take them off
    /// its own screen. Null means the person cancelled; nothing was deleted. The caller shows <see cref="NoticeFor"/> of the answer in place.
    /// </summary>
    public static async Task<LibraryItemDeletionDialogResult?> RunForResultAsync(
        IAppDialogService dialogs,
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
            DialogOptions);

        if (await dialog.Result is not { Canceled: false, Data: LibraryItemDeletionDialogResult answer })
        {
            return null;
        }

        return answer;
    }

    private static AppDialogOptions DialogOptions => new()
    {
        MaxWidth = AppMaxWidth.Medium,
        FullWidth = true,
        CloseButton = false,
        NoHeader = true,
        CloseOnEscapeKey = true,
    };
}
