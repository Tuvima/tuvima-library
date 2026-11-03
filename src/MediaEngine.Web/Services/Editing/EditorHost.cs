using MediaEngine.Contracts.Details;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Theming;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// What a page tells the pop-over editor about the item it shows: whether the person may edit it, how to load its fields, where it sits
/// in its family, and what to refresh when something was saved. A page makes one and hands it to <see cref="EditorHost.OpenAsync(EditorBinding, Guid, EditTool?, IReadOnlyList{EditTrailItem}?, string?)"/>
/// when the person presses Edit; the page itself never enters an "editing" state.
/// </summary>
public sealed class EditorBinding
{
    private readonly DeviceContextService _device;
    private EditTrailCatalog _catalog = EditTrailCatalog.Empty;

    public EditorBinding(DeviceContextService device)
    {
        _device = device;
    }

    /// <summary>The page's own item (Guid.Empty for a Universe, which is addressed by its Wikidata id).</summary>
    public Guid EntityId { get; set; }

    /// <summary>The Engine's own answer: may this person edit this item (the page reads it from the item's "Edit" action).</summary>
    public bool HasPermission { get; set; }

    /// <summary>Why Edit is unavailable right now, for the disabled entry's tooltip.</summary>
    public string? UnavailableReason { get; set; }

    /// <summary>Permission and a device that is allowed to edit (desktop form factor, fine hover-capable pointer).</summary>
    public bool CanEdit => HasPermission && EditorDeviceRule.CanEdit(_device);

    /// <summary>True on pages whose items keep a change history (not the Universe pages): only then does the pop-over offer History.</summary>
    public bool ShowHistory { get; set; }

    /// <summary>True when the item has not been matched yet, so the pop-over opens on Match instead of Details.</summary>
    public bool IsUnmatched { get; set; }

    /// <summary>The editor media type used when rows are ticked (Renumber and Match need it).</summary>
    public string? BulkMediaType { get; set; }

    /// <summary>A show's video clips, for "Move to episode" in the Match tool.</summary>
    public IReadOnlyList<DetailExtraCardViewModel>? Extras { get; set; }

    /// <summary>Extra read-only content the page adds under the Details fields (a Universe lists its Series there).</summary>
    public RenderFragment? DetailsExtra { get; set; }

    /// <summary>The items the trail can switch between; set whenever the page's item changes.</summary>
    public EditTrailCatalog Catalog
    {
        get => _catalog;
        set
        {
            _catalog = value ?? EditTrailCatalog.Empty;
            CatalogChanged?.Invoke(this);
        }
    }

    internal event Action<EditorBinding>? CatalogChanged;

    /// <summary>Loads the fields of whatever the person is editing now (the page's item, or another item the trail switched to).</summary>
    public Func<Task>? LoadFields { get; set; }

    /// <summary>Loads the fields of an item the person switched to. True when it is ready.</summary>
    public Func<EditTrailItem, Task<bool>>? OpenItem { get; set; }

    /// <summary>The item being edited changed; the page moves anything of its own that follows it.</summary>
    public Func<Task>? ItemSwitched { get; set; }

    /// <summary>Save changes went through: the page refreshes what it shows.</summary>
    public Func<Task>? Saved { get; set; }

    /// <summary>A tool committed a change on its own (a match): the page refreshes what it shows.</summary>
    public Func<Task>? SectionCommitted { get; set; }

    /// <summary>A single field was saved on its own: the page refreshes what it shows.</summary>
    public Func<string, Task>? FieldSaved { get; set; }

    /// <summary>The editor media types of the page's items, which say which library's file setting applies.</summary>
    public Func<IReadOnlyList<string>>? MediaTypes { get; set; }

    /// <summary>Where the page's item sits when the pop-over opens on it.</summary>
    public Func<IReadOnlyList<EditTrailItem>>? Trail { get; set; }

    /// <summary>A row's own Edit: opens the pop-over on the page's item with that row ticked in the bulk panel.</summary>
    public Func<BulkEditRow, Task<bool>>? EditRowAsync { get; set; }
}

/// <summary>What a list needs to know when the pop-over it opened over itself closes (universal editor v3 WP3).</summary>
/// <param name="Changed">True when something was saved or committed (a match, a field) while the pop-over was open.</param>
/// <param name="ItemIds">Every item the pop-over was on or had ticked, so a list can refresh just those rows.</param>
public sealed record EditorClosedEventArgs(bool Changed, IReadOnlyList<Guid> ItemIds);

/// <summary>
/// The one universal editor of the Dashboard: the field session, the staged changes, the saver, the artwork picker session and the
/// permission answer all live here, once per circuit, so the pop-over can open over any page (an item page, a Universe, a list) without
/// that page being put into an "editing" state first (universal editor v3, D1 and D6). A page binds itself with an
/// <see cref="EditorBinding"/> and presses <see cref="OpenAsync(Guid, EditTool?, IReadOnlyList{EditTrailItem}?, string?)"/>; the layout
/// draws the window from this service.
/// </summary>
/// <remarks>
/// While the editor is open it registers itself as a leave guard of the details overlay, so closing the overlay (Back, a link) first asks
/// "Unsaved changes" and only then lets the overlay go.
/// </remarks>
public sealed class EditorHost : IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ILogger<EditorHost> _logger;
    private EditorBinding? _binding;
    private IReadOnlyList<EditTrailItem>? _openTrail;
    private IDisposable? _overlayGuard;
    private DetailsOverlayState? _overlayWithGuard;
    private EditTool? _lastTool;
    private bool _bulkWasOpen;
    private bool _disposed;
    private bool _wasOpen;
    private bool _changedWhileOpen;
    private readonly HashSet<Guid> _touched = [];

    public EditorHost(
        IEngineApiClient api,
        DeviceContextService device,
        IServiceProvider services,
        ILogger<EditorHost> logger)
    {
        _services = services;
        _logger = logger;
        Mode = new EditingModeContext(device, new ItemFieldEditSession(api, device, logger))
        {
            Loader = () => _binding?.LoadFields?.Invoke() ?? Task.CompletedTask,
            AskSaveOrDiscard = () => AskSaveOrDiscard?.Invoke() ?? Task.FromResult(SaveOrDiscardChoice.Cancel),
        };
        Session = new EditSessionState(new PendingEditList());
        Flow = new EditSessionFlow(
            Session,
            Mode,
            new EditSessionSaver(api),
            () => _openTrail ?? _binding?.Trail?.Invoke() ?? [new EditTrailItem(Mode.Fields.EntityId, "This item", "Item")],
            () => ConfirmDiscard?.Invoke() ?? Task.FromResult(false))
        {
            Saved = OnSavedAsync,
            MediaTypes = () => _binding?.MediaTypes?.Invoke() ?? [],
            OpenItem = item => _binding?.OpenItem?.Invoke(item) ?? Task.FromResult(false),
            ItemSwitched = () => _binding?.ItemSwitched?.Invoke() ?? Task.CompletedTask,
        };
        Preview = new EditPendingPreview(Session, Mode.Fields);
        Picker = new ArtworkPickerSession(api, Session, Mode.Fields, Preview.Artwork, logger);
        Batch = new BulkEditSelection { RefreshAsync = () => _binding?.SectionCommitted?.Invoke() ?? Task.CompletedTask };

        Mode.Fields.Saved += OnFieldSavedAsync;
        Mode.Changed += OnModeChanged;
        Session.Changed += OnSessionChanged;
        Picker.Changed += RaiseChanged;
        Flow.Changed += RaiseChanged;
        Batch.Changed += OnBatchChanged;
    }

    /// <summary>The permission, device and unsaved-input rules of the editor; the window is open exactly while <see cref="IsOpen"/>.</summary>
    public EditingModeContext Mode { get; }

    /// <summary>The staged changes, the item being edited and the open tool.</summary>
    public EditSessionState Session { get; }

    /// <summary>What the person does with the staged changes: pick a tool, switch item, leave, review and save.</summary>
    public EditSessionFlow Flow { get; }

    /// <summary>What a page shows while changes are only staged.</summary>
    public EditPendingPreview Preview { get; }

    /// <summary>The Artwork tool's session.</summary>
    public ArtworkPickerSession Picker { get; }

    /// <summary>The rows ticked for the bulk panel; what it stages goes into the same pending list as one item's edits.</summary>
    public BulkEditSelection Batch { get; }

    /// <summary>How the window asks "Save or discard?" (set by the layout's pop-over, which owns the dialogs). Unset, unsaved input keeps the person where they are.</summary>
    public Func<Task<SaveOrDiscardChoice>>? AskSaveOrDiscard { get; set; }

    /// <summary>How the window asks "Unsaved changes"; true means discard them (set by the layout's pop-over). Unset, the person stays.</summary>
    public Func<Task<bool>>? ConfirmDiscard { get; set; }

    /// <summary>The page the editor currently belongs to; null when no page has bound itself.</summary>
    public EditorBinding? Binding => _binding;

    /// <summary>True while the pop-over is showing.</summary>
    public bool IsOpen => Mode.IsOn;

    /// <summary>Raised whenever the pop-over's content may need to be drawn again.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised once each time the pop-over closes, whatever opened it (a list row's Edit, Fix, a page). A list subscribes while it is on
    /// screen and refreshes the rows named in the event when something changed, so the person is never taken away from it.
    /// </summary>
    public event Action<EditorClosedEventArgs>? Closed;

    /// <summary>A tool committed a change on its own (a match went through): the page behind refreshes and the closing event says something changed.</summary>
    public async Task NotifyCommittedAsync()
    {
        _changedWhileOpen = true;
        if (_binding?.SectionCommitted is { } committed)
        {
            await committed();
        }
    }

    /// <summary>Makes <paramref name="binding"/> the page the editor serves. A page binds right before it opens the editor.</summary>
    public void Bind(EditorBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (ReferenceEquals(_binding, binding))
        {
            ApplyBinding(binding);
            return;
        }

        if (_binding is not null)
        {
            _binding.CatalogChanged -= OnCatalogChanged;
        }

        _binding = binding;
        binding.CatalogChanged += OnCatalogChanged;
        ApplyBinding(binding);
    }

    /// <summary>A page is going away. When the editor is open on it, the editor goes with it (the leave guards have already asked).</summary>
    public void Unbind(EditorBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!ReferenceEquals(_binding, binding))
        {
            return;
        }

        binding.CatalogChanged -= OnCatalogChanged;
        _binding = null;
        if (Mode.IsOn)
        {
            Mode.Leave();
        }

        // Rows ticked on a page or list that is going away must not wait for the next one.
        Batch.Clear();
        RaiseChanged();
    }

    /// <summary>Opens the pop-over for the bound page's item (see <see cref="OpenAsync(EditorBinding, Guid, EditTool?, IReadOnlyList{EditTrailItem}?, string?)"/>).</summary>
    public Task<bool> OpenAsync(Guid entityId, EditTool? tool = null, IReadOnlyList<EditTrailItem>? trail = null, string? artworkSlot = null) =>
        _binding is { } binding ? OpenAsync(binding, entityId, tool, trail, artworkSlot) : Task.FromResult(false);

    /// <summary>
    /// Opens the pop-over at once (its skeleton shows while the item's fields load) and then opens the right tool: the one asked for, else
    /// Match when the item is not matched yet, else Details. Refused (false) when the person or the device may not edit. Pressing Edit
    /// while the pop-over is already open on another item moves to that item and keeps every staged change. True when the pop-over is open
    /// afterwards, even if the Engine could not be reached (the window then shows the reason and Try again).
    /// </summary>
    /// <param name="entityId">The item to edit; Guid.Empty for a Universe, which the page's loader addresses by Wikidata id.</param>
    /// <param name="tool">The tool to open; null chooses Match for an unmatched item and Details otherwise.</param>
    /// <param name="trail">Where the item sits, outermost first; null asks the page's binding.</param>
    /// <param name="artworkSlot">For the Artwork tool, the picture to start on (CoverArt, Background, Logo).</param>
    public async Task<bool> OpenAsync(EditorBinding binding, Guid entityId, EditTool? tool = null, IReadOnlyList<EditTrailItem>? trail = null, string? artworkSlot = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        Bind(binding);
        if (!Mode.CanEdit)
        {
            return false;
        }

        if (Mode.IsOn)
        {
            return await ReopenAsync(entityId, tool, trail, artworkSlot);
        }

        _openTrail = trail;
        Task<bool> entering;
        try
        {
            // Turning the mode on raises its change synchronously, which starts the session: the window can draw before the fields arrive.
            entering = Mode.EnterAsync();
            if (Session.IsActive)
            {
                Session.SetTool(EditTool.Details);
            }

            if (!await entering)
            {
                return false;
            }
        }
        finally
        {
            _openTrail = null;
        }

        if (!Mode.IsOn || !Session.IsActive)
        {
            return false;
        }

        if (!Mode.Fields.Loaded)
        {
            // The Engine could not be reached: the window stays open on Details with its plain error line and Try again.
            return true;
        }

        await ShowToolAsync(tool, artworkSlot);
        return Mode.IsOn;
    }

    /// <summary>Loads the item again after the Engine could not be reached (the window's "Try again").</summary>
    public async Task RetryLoadAsync()
    {
        if (!Mode.IsOn || Mode.Fields.Loading || _binding?.LoadFields is not { } load)
        {
            return;
        }

        await load();
        if (Mode.Fields.Loaded && Session.IsActive && Session.ActiveTool == EditTool.Details && !Mode.SectionOpen)
        {
            Mode.OpenSection(EditingSection.Details);
        }

        RaiseChanged();
    }

    /// <summary>
    /// Esc, the backdrop or the window's X: closes an open field or section first, then the window itself, asking "Unsaved changes"
    /// when something is staged. False means the person stayed.
    /// </summary>
    public async Task<bool> RequestCloseAsync()
    {
        if (!Mode.IsOn)
        {
            return true;
        }

        if (await Flow.HandleEscapeAsync())
        {
            return !Mode.IsOn;
        }

        return await Flow.LeaveAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseOverlayGuard();
        Mode.Fields.Saved -= OnFieldSavedAsync;
        Mode.Changed -= OnModeChanged;
        Session.Changed -= OnSessionChanged;
        Picker.Changed -= RaiseChanged;
        Flow.Changed -= RaiseChanged;
        Batch.Changed -= OnBatchChanged;
        if (_binding is not null)
        {
            _binding.CatalogChanged -= OnCatalogChanged;
        }

        Flow.Dispose();
        Mode.Dispose();
    }

    private async Task<bool> ReopenAsync(Guid entityId, EditTool? tool, IReadOnlyList<EditTrailItem>? trail, string? artworkSlot)
    {
        if (entityId != Guid.Empty && Session.ActiveItem?.EntityId != entityId)
        {
            var moved = trail is { Count: > 0 } ? await Flow.SwitchToAsync(trail) : await Flow.SwitchToAsync(entityId);
            if (!moved)
            {
                return false;
            }
        }

        if (tool is { } requested && Session.ActiveTool != requested)
        {
            await ShowToolAsync(requested, artworkSlot);
        }

        return Mode.IsOn;
    }

    private async Task ShowToolAsync(EditTool? requested, string? artworkSlot)
    {
        var chosen = requested ?? (_binding is { IsUnmatched: true } && Mode.Fields.CanMatch ? EditTool.Match : EditTool.Details);
        switch (chosen)
        {
            case EditTool.Artwork when artworkSlot is not null && Mode.Fields.CanEditArtwork:
                if (await Mode.ShowSectionAsync(EditingSection.Artwork, artworkSlot) && Session.IsActive)
                {
                    Session.SetTool(EditTool.Artwork);
                }

                break;
            case EditTool.Details:
                Mode.OpenSection(EditingSection.Details);
                break;
            default:
                await Flow.SelectToolAsync(chosen);
                break;
        }
    }

    private void ApplyBinding(EditorBinding binding)
    {
        Mode.HasPermission = binding.HasPermission;
        Mode.UnavailableReason = binding.UnavailableReason;
        Batch.MediaType = binding.BulkMediaType;
        Flow.Catalog = binding.Catalog;
    }

    private void OnCatalogChanged(EditorBinding binding)
    {
        if (ReferenceEquals(binding, _binding))
        {
            Flow.Catalog = binding.Catalog;
        }
    }

    private Task OnFieldSavedAsync(string key)
    {
        _changedWhileOpen = true;
        return _binding?.FieldSaved?.Invoke(key) ?? Task.CompletedTask;
    }

    private Task OnSavedAsync()
    {
        _changedWhileOpen = true;
        return _binding?.Saved?.Invoke() ?? Task.CompletedTask;
    }

    private void OnModeChanged()
    {
        if (Mode.IsOn && !_wasOpen)
        {
            _wasOpen = true;
            _changedWhileOpen = false;
            _touched.Clear();
        }
        else if (!Mode.IsOn && _wasOpen)
        {
            // Leaving (Done, a device change, a removed title) ends the ticks and the bulk panel with them.
            _wasOpen = false;
            var closed = new EditorClosedEventArgs(_changedWhileOpen, [.. _touched]);
            _touched.Clear();
            Batch.Clear();
            _bulkWasOpen = false;
            _lastTool = null;
            SyncOverlayGuard();
            RaiseChanged();
            RaiseClosed(closed);
            return;
        }

        SyncOverlayGuard();
        RaiseChanged();
    }

    private void RaiseClosed(EditorClosedEventArgs closed)
    {
        if (Closed is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<EditorClosedEventArgs>>())
        {
            try
            {
                handler(closed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A list that cannot refresh must not break the editor closing; it keeps what it showed.
                _logger.LogWarning(ex, "A list could not refresh after the editor closed");
            }
        }
    }

    // The window asks "Unsaved changes" itself; the details overlay behind it asks through this guard when it is about to close
    // (browser Back, a link to another title), so the editor closes first and the overlay only goes if the person agreed.
    private void SyncOverlayGuard()
    {
        if (Mode.IsOn)
        {
            if (_overlayGuard is null && _services.GetService(typeof(DetailsOverlayState)) is DetailsOverlayState overlay)
            {
                _overlayWithGuard = overlay;
                _overlayGuard = overlay.RegisterDismissGuard(Flow);
            }

            return;
        }

        ReleaseOverlayGuard();
    }

    private void ReleaseOverlayGuard()
    {
        _overlayGuard?.Dispose();
        _overlayGuard = null;
        _overlayWithGuard = null;
    }

    /// <summary>True while the details overlay is open behind the editor, so it (not the window) asks about navigation.</summary>
    public bool OverlayGuardsNavigation => _overlayWithGuard?.IsOpen == true;

    // Leaving the Details tool (the window's other tool, or the panel closing) also closes the bulk panel; the ticks and staged changes stay.
    private void OnSessionChanged()
    {
        var tool = Session.ActiveTool;
        if (Mode.IsOn && Session.ActiveItem is { } active && active.EntityId != Guid.Empty)
        {
            _touched.Add(active.EntityId);
        }

        if (_lastTool == EditTool.Details && tool != EditTool.Details && Batch.Panel != BulkPanelKind.None)
        {
            _bulkWasOpen = false;
            Batch.ClosePanel();
        }

        _lastTool = tool;
        RaiseChanged();
    }

    // A bulk panel was asked for, or the ticks ended: open the Details tool for it (settling an open section first), or close the tool
    // again when the last ticked row went and the panel was only there for them.
    private void OnBatchChanged() => _ = SyncBulkPanelAsync();

    private async Task SyncBulkPanelAsync()
    {
        try
        {
            if (_disposed)
            {
                return;
            }

            if (Batch.Panel != BulkPanelKind.None)
            {
                if (!Mode.IsOn && _binding is { CanEdit: true } binding)
                {
                    // The bar's Edit, Match or Move on rows ticked on a page or a list: the pop-over opens for them, over that page or list.
                    await OpenAsync(binding, binding.EntityId, EditTool.Details);
                }

                if (Mode is not { IsOn: true, CanEdit: true } || !Session.IsActive)
                {
                    Batch.ClosePanel();
                    return;
                }

                if (Mode.SectionOpen && !await Mode.CloseSectionAsync())
                {
                    Batch.ClosePanel();
                    return;
                }

                foreach (var id in Batch.Ids)
                {
                    _touched.Add(id);
                }

                _bulkWasOpen = true;
                if (Session.ActiveTool != EditTool.Details)
                {
                    Session.SetTool(EditTool.Details);
                }
            }
            else if (_bulkWasOpen)
            {
                _bulkWasOpen = false;
                if (Session.IsActive && Session.ActiveTool == EditTool.Details && !Mode.SectionOpen)
                {
                    Session.SetTool(null);
                }
            }

            RaiseChanged();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The bulk panel could not follow the ticked rows");
        }
    }

    private void RaiseChanged() => Changed?.Invoke();
}
