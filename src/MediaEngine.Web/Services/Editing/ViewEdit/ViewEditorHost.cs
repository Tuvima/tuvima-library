using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Services.Editing;

/// <summary>What a list needs to know when the View editor closes: whether anything changed and which items it touched, so it refreshes only those rows.</summary>
public sealed record ViewEditClosedArgs(bool Changed, IReadOnlyList<Guid> ItemIds, IReadOnlyList<Guid> DeletedIds, string? Saved, Guid? CreatedGalleryId);

/// <summary>
/// The one editor window for photos, videos, documents and galleries (spec photos-documents-editor). It lives once per circuit, like the library
/// editor's host, so Edit opens the same pop-over over any page: a photo's card menu, the viewer, ticked rows, a gallery. The page behind is never
/// put into an "editing" state. It checks the device rule itself, so hiding a button is never the only protection (v4 D6).
/// </summary>
public sealed class ViewEditorHost(IEngineApiClient api, DeviceContextService device, ViewWorkspaceService workspace, ILogger<ViewEditorHost> logger)
{
    private TaskCompletionSource<ViewEditClosedArgs>? _closed;
    private readonly HashSet<Guid> _deleted = [];

    public ViewEditSession? Session { get; private set; }

    public bool IsOpen => Session is not null;

    /// <summary>True while a delete started from the window is running, so the page behind leaves "removed" news to the window.</summary>
    public bool IsDeleting { get; set; }

    /// <summary>"Unsaved changes" (set by the layout's pop-over, which owns the dialogs).</summary>
    public Func<Task<bool>>? ConfirmDiscard { get; set; }

    public event Action? Changed;

    public event Action<ViewEditClosedArgs>? Closed;

    /// <summary>True when this device may edit at all (desktop form factor, fine hover-capable pointer).</summary>
    public bool CanEdit => EditorDeviceRule.CanEdit(device);

    /// <summary>Opens the window on photos, videos or documents. Several items open the group editor. False when the device may not edit or nothing was given.</summary>
    public async Task<bool> OpenItemsAsync(IReadOnlyList<LocalAssetDto> items, ViewEditGalleryRail? rail = null, ViewEditTool tool = ViewEditTool.Details)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0 || !CanEdit)
        {
            return false;
        }

        // Another editor window is already open: opening a second would drop its staged changes, so the person has to leave it first.
        if (Session is { HasPendingChanges: true } open && !await open.LeaveAsync())
        {
            return false;
        }

        var shown = items.Count == 1 ? [await FreshAsync(items[0])] : items;
        Begin(new ViewEditSession(api, shown, rail));
        Session!.SelectTool(tool);
        return true;
    }

    /// <summary>
    /// Opens a gallery in the same window. <paramref name="gallery"/> null with <paramref name="vault"/> null makes a new gallery.
    /// <paramref name="startingMembers"/> are photos already chosen for a new gallery (New gallery from the selection bar).
    /// </summary>
    public async Task<bool> OpenGalleryAsync(ViewGalleryDto? gallery, VaultImageGalleryDto? vault = null, bool newSmart = false, bool newVault = false,
        IReadOnlyList<LocalAssetDto>? startingMembers = null)
    {
        if (!CanEdit)
        {
            return false;
        }

        if (Session is { HasPendingChanges: true } open && !await open.LeaveAsync())
        {
            return false;
        }

        var stage = new ViewGalleryEditStage(gallery, vault, newSmart);
        if (newVault)
        {
            stage.AsNewVault();
        }

        foreach (var member in startingMembers ?? [])
        {
            stage.StartingMembers.Add(member);
        }

        Begin(new ViewEditSession(api, stage));
        await Task.CompletedTask;
        return true;
    }

    /// <summary>
    /// The numbers the header's item dropdown follows (how many photos show inline, how many a page holds). They live in <c>config/editor.json</c>
    /// and arrive from the Engine's editor-config read; that read is open to administrators and applications only, so for anyone else the dropdown
    /// uses the shipped defaults below. Read once per circuit.
    /// </summary>
    public async Task<ViewEditorLimits> GetLimitsAsync()
    {
        if (_limits is not null)
        {
            return _limits;
        }

        try
        {
            var config = await api.GetEditorKindConfigAsync(MediaEngine.Contracts.Editing.EditorKindNames.ViewItem);
            _limits = config is { InlineSupportingFiles: > 0, ItemListPageSize: > 0 }
                ? new ViewEditorLimits(config.InlineSupportingFiles, config.ItemListPageSize)
                : ViewEditorLimits.Shipped;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogWarning(ex, "The editor limits could not be read; the shipped defaults are used.");
            _limits = ViewEditorLimits.Shipped;
        }

        return _limits;
    }

    private ViewEditorLimits? _limits;

    /// <summary>One page of a gallery's photos for the header's item dropdown, optionally narrowed by a search. Null when the Engine could not be reached.</summary>
    public Task<ViewAssetTimelinePageDto?> GalleryPhotosAsync(Guid galleryId, string? cursor, string? search, int limit) =>
        api.GetViewAssetsAsync(new ViewAssetQueryOptions(workspace.ScopeKind, workspace.ScopeProfileId, cursor, string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            GalleryId: galleryId, Limit: limit));

    /// <summary>The home button of a photo opened from a gallery: the window moves to the gallery's own editor. Asks first when something is staged.</summary>
    public async Task<bool> OpenGalleryByIdAsync(Guid galleryId)
    {
        var gallery = await api.GetViewGalleryAsync(galleryId);
        return gallery is not null && await OpenGalleryAsync(gallery);
    }

    /// <summary>A photo picked in the dropdown of a gallery's own editor: the window moves to that photo, with the gallery as its family.</summary>
    public async Task<bool> OpenPhotoInGalleryAsync(LocalAssetDto photo, Guid galleryId, string galleryName)
    {
        var page = await GalleryPhotosAsync(galleryId, null, null, (await GetLimitsAsync()).PageSize);
        IReadOnlyList<LocalAssetDto> members = page?.Items ?? [photo];
        if (members.All(member => member.Id != photo.Id))
        {
            members = [photo, .. members];
        }

        return await OpenItemsAsync([photo], new ViewEditGalleryRail(galleryId, galleryName, members, page?.HasMore ?? false));
    }

    /// <summary>Opens the window and completes when it closes, with what changed (the old launcher's "applied" answer).</summary>
    public Task<ViewEditClosedArgs> WhenClosedAsync() => _closed?.Task ?? Task.FromResult(new ViewEditClosedArgs(false, [], [], null, null));

    /// <summary>Steps to another photo of the gallery the window was opened from; what is staged on the other photos stays.</summary>
    public async Task SwitchItemAsync(LocalAssetDto item)
    {
        if (Session is not { Switching: false } session)
        {
            return;
        }

        session.SwitchTo([await FreshAsync(item)]);
    }

    /// <summary>Items were deleted from the window (the Engine already did it): their staged changes go, and the window moves on or closes.</summary>
    public void ItemsDeleted(IReadOnlyCollection<Guid> ids)
    {
        if (Session is not { } session)
        {
            return;
        }

        foreach (var id in ids)
        {
            _deleted.Add(id);
        }

        session.Forget(ids);
        if (session.Rail is { } rail)
        {
            var remaining = rail.Members.Where(member => !ids.Contains(member.Id)).ToList();
            if (session.Active.Items.Any(item => ids.Contains(item.Id)) && remaining.Count > 0)
            {
                session.SetRail(rail with { Members = remaining });
                session.SwitchTo([remaining[0]]);
                return;
            }

            session.SetRail(rail with { Members = remaining });
        }

        if (session.Active.Items.Count == 0 || session.Active.Items.All(item => ids.Contains(item.Id)) || session.IsGallery)
        {
            Close();
        }
    }

    /// <summary>A gallery was deleted from the window: the window closes at once.</summary>
    public void GalleryDeleted(Guid id)
    {
        _deleted.Add(id);
        Close();
    }

    /// <summary>Closes the window. Pending changes are dropped (the caller has already asked, or Save has already written them).</summary>
    public void Close()
    {
        if (Session is not { } session)
        {
            return;
        }

        var args = new ViewEditClosedArgs(session.ChangedWhileOpen || _deleted.Count > 0, [.. session.TouchedIds],
            [.. _deleted], session.LastSaved, session.CreatedGalleryId);
        session.Changed -= OnChanged;
        session.CloseRequested -= Close;
        Session = null;
        _deleted.Clear();
        var closed = _closed;
        _closed = null;
        Changed?.Invoke();
        Closed?.Invoke(args);
        closed?.TrySetResult(args);
    }

    private void Begin(ViewEditSession session)
    {
        _closed = new TaskCompletionSource<ViewEditClosedArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ConfirmDiscard = () => ConfirmDiscard?.Invoke() ?? Task.FromResult(false);
        session.Changed += OnChanged;
        session.CloseRequested += Close;
        Session = session;
        Changed?.Invoke();
    }

    private void OnChanged() => Changed?.Invoke();

    // A single item is read again so the window shows what is true now (and, for a document, its own fields), never a list's older copy.
    private async Task<LocalAssetDto> FreshAsync(LocalAssetDto item)
    {
        try
        {
            var fresh = await api.GetViewItemAsync(item.Id, workspace.ScopeKind, workspace.ScopeProfileId);
            if (fresh is null)
            {
                return item;
            }

            if (ViewEditStage.IsDocument(fresh) && fresh.DocumentInfo?.FieldValues is null
                && await api.GetViewDocumentDetailsAsync(item.Id, workspace.ScopeKind, workspace.ScopeProfileId) is { } details)
            {
                return fresh with { DocumentInfo = details };
            }

            return fresh;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogWarning(ex, "The item could not be read again before editing; the copy the list had is shown.");
            return item;
        }
    }
}

/// <summary>The item dropdown's numbers: photos shown before "All photos", and the size of one page of the full list.</summary>
public sealed record ViewEditorLimits(int InlineItems, int PageSize)
{
    /// <summary>The values <c>config/editor.json</c> ships with, used only when the Engine's editor-config read is not open to the person.</summary>
    public static ViewEditorLimits Shipped { get; } = new(8, 50);
}
