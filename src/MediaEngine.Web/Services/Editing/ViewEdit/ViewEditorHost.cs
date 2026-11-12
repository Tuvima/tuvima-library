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

    /// <summary>True when this device may edit items at all (desktop, tablet or phone; never a TV).</summary>
    public bool CanEdit => EditorDeviceRule.CanEditItems(device);

    /// <summary>Opens the window on photos, videos or documents. Several items open the group editor. False when the device may not edit or nothing was given.</summary>
    public async Task<bool> OpenItemsAsync(IReadOnlyList<LocalAssetDto> items, ViewEditGalleryRail? rail = null, ViewEditTool tool = ViewEditTool.Details)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0 || !CanEdit)
        {
            return false;
        }

        // The window is already open (home, a pick in the dropdown, or Edit on another item): moving on would drop its staged changes, so the person is asked first.
        if (Session is { } open && !await open.ConfirmLeaveAsync())
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

        if (Session is { } open && !await open.ConfirmLeaveAsync())
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
    /// and the Engine carries them on the photo read of a gallery, which every View profile may make, so the Dashboard holds none. Read once per
    /// circuit; null when the Engine could not say.
    /// </summary>
    public async Task<ViewEditorLimits?> GetLimitsAsync(Guid galleryId)
    {
        if (_limits is null)
        {
            // One photo is enough: the numbers ride on any page of the gallery.
            await GalleryPhotosAsync(galleryId, null, null, 1);
        }

        return _limits;
    }

    private ViewEditorLimits? _limits;

    /// <summary>One page of a gallery's photos for the header's item dropdown, optionally narrowed by a search. Null when the Engine could not be reached.</summary>
    public async Task<ViewAssetTimelinePageDto?> GalleryPhotosAsync(Guid galleryId, string? cursor, string? search, int? limit)
    {
        var options = new ViewAssetQueryOptions(workspace.ScopeKind, workspace.ScopeProfileId, cursor, string.IsNullOrWhiteSpace(search) ? null : search.Trim(), GalleryId: galleryId);
        var page = await api.GetViewAssetsAsync(limit is { } size ? options with { Limit = size } : options);
        if (page?.EditorLimits is { InlineItems: > 0, PageSize: > 0 } numbers)
        {
            _limits = new ViewEditorLimits(numbers.InlineItems, numbers.PageSize);
        }

        return page;
    }

    /// <summary>The home button of a photo opened from a gallery: the window moves to the gallery's own editor. Asks first when something is staged.</summary>
    public async Task<bool> OpenGalleryByIdAsync(Guid galleryId)
    {
        var gallery = await api.GetViewGalleryAsync(galleryId);
        return gallery is not null && await OpenGalleryAsync(gallery);
    }

    /// <summary>A photo picked in the dropdown of a gallery's own editor: the window moves to that photo, with the gallery as its family.</summary>
    public async Task<bool> OpenPhotoInGalleryAsync(LocalAssetDto photo, Guid galleryId, string galleryName, int itemCount)
    {
        var page = await GalleryPhotosAsync(galleryId, null, null, (await GetLimitsAsync(galleryId))?.PageSize);
        IReadOnlyList<LocalAssetDto> members = page?.Items ?? [photo];
        if (members.All(member => member.Id != photo.Id))
        {
            members = [photo, .. members];
        }

        return await OpenItemsAsync([photo], new ViewEditGalleryRail(galleryId, galleryName, members, itemCount, page?.HasMore ?? false));
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
                session.SetRail(Shrunk(rail, remaining));
                session.SwitchTo([remaining[0]]);
                return;
            }

            session.SetRail(Shrunk(rail, remaining));
        }

        if (session.Active.Items.Count == 0 || session.Active.Items.All(item => ids.Contains(item.Id)) || session.IsGallery)
        {
            Close();
        }
    }

    // The gallery lost the deleted photos: the dropdown lists what is left and counts one fewer for each.
    private static ViewEditGalleryRail Shrunk(ViewEditGalleryRail rail, List<LocalAssetDto> remaining) =>
        rail with { Members = remaining, ItemCount = Math.Max(remaining.Count, rail.ItemCount - (rail.Members.Count - remaining.Count)) };

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

        var args = new ViewEditClosedArgs(_carriedChanged || session.ChangedWhileOpen || _deleted.Count > 0, [.. _carriedTouched.Concat(session.TouchedIds).Distinct()],
            [.. _deleted], session.LastSaved ?? _carriedSaved, session.CreatedGalleryId ?? _carriedCreated);
        session.Changed -= OnChanged;
        session.CloseRequested -= Close;
        Session = null;
        _deleted.Clear();
        ClearCarried();
        var closed = _closed;
        _closed = null;
        Changed?.Invoke();
        Closed?.Invoke(args);
        closed?.TrySetResult(args);
    }

    // What the sessions the window has already moved on from changed. The page that opened the window gets one answer, at the final close, that
    // covers every session the window showed.
    private bool _carriedChanged;
    private readonly HashSet<Guid> _carriedTouched = [];
    private string? _carriedSaved;
    private Guid? _carriedCreated;

    private void ClearCarried()
    {
        _carriedChanged = false;
        _carriedTouched.Clear();
        _carriedSaved = null;
        _carriedCreated = null;
    }

    private void Begin(ViewEditSession session)
    {
        if (Session is { } previous)
        {
            // In-window navigation: the old session stops reporting, its news is carried, and the opener's pending answer stays as it is.
            previous.Changed -= OnChanged;
            previous.CloseRequested -= Close;
            _carriedChanged |= previous.ChangedWhileOpen;
            _carriedTouched.UnionWith(previous.TouchedIds);
            _carriedSaved = previous.LastSaved ?? _carriedSaved;
            _carriedCreated = previous.CreatedGalleryId ?? _carriedCreated;
        }
        else
        {
            _closed = new TaskCompletionSource<ViewEditClosedArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

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

/// <summary>The item dropdown's numbers, from the Engine (<c>config/editor.json</c>): photos shown before "All photos", and the size of one page of the full list.</summary>
public sealed record ViewEditorLimits(int InlineItems, int PageSize);
