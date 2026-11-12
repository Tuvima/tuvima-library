using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Components.Listen;

/// <summary>
/// The Songs and Albums lists on the one <see cref="SongTable"/> (spec music-table-and-reader-frame WP3). The page hands it its songs as
/// <see cref="WorkViewModel"/>s and keeps deciding what playing, queueing and favoriting mean; this component maps the songs to rows,
/// and keeps the tick boxes with the bulk bar and Edit panel, the delete flow (spec delete-from-library D13, D25), songs leaving the list
/// when they are deleted anywhere, and dragging a song out to a playlist.
/// </summary>
public partial class SongList
{
    private readonly HashSet<Guid> _removed = [];
    private IReadOnlyList<SongRow> _rows = [];
    private Dictionary<string, WorkViewModel> _byId = [];
    private (IReadOnlyList<WorkViewModel>? Tracks, int Count, int Removed)? _rowsKey;
    private IReadOnlySet<string> _favoriteIds = new HashSet<string>();
    private (IReadOnlySet<Guid>? Source, int Count)? _favoritesKey;
    private IReadOnlySet<string> _selectedIds = new HashSet<string>();
    private BulkListEntry? _bulkHost;
    private bool _openEditPanelAfterRender;
    private UIOrchestratorService? _orchestrator;
    private string? _notice;
    private bool _deleting;
    private bool _disposed;

    [Inject] private IServiceProvider Services { get; set; } = default!;

    // Edit is offered only to a profile the Engine says may edit item details; the table and each handler below check it again.
    private bool CanEditSongs => Services.GetService<DashboardSessionAccessor>()?.CanOfferMetadataEdit == true;

    /// <summary>Songs (the Songs list) or Albums (an album's songs inside the Listen page): each remembers its own columns.</summary>
    [Parameter, EditorRequired] public SongTablePlace Place { get; set; }

    [Parameter] public IReadOnlyList<WorkViewModel> Tracks { get; set; } = [];

    /// <summary>The songs saved to Favorites: their heart is filled.</summary>
    [Parameter] public IReadOnlySet<Guid> FavoriteIds { get; set; } = new HashSet<Guid>();

    /// <summary>The name the player shows as where the songs came from ("All Songs", an album's name).</summary>
    [Parameter] public string SourceLabel { get; set; } = "Music";

    [Parameter] public string? AriaLabel { get; set; }

    [Parameter] public bool ShowCovers { get; set; } = true;

    /// <summary>The signed-in profile, so its chosen columns and widths are remembered for this place.</summary>
    [Parameter] public Guid? ProfileId { get; set; }

    /// <summary>The song the player is on; its row gets the equalizer mark.</summary>
    [Parameter] public Guid? CurrentWorkId { get; set; }

    [Parameter] public bool IsPlaying { get; set; }

    [Parameter] public bool IsLoading { get; set; }

    /// <summary>A sort chosen outside the table (the browse header's sort, the phone header's). A header click afterwards changes it again.</summary>
    [Parameter] public SongSortState? Sort { get; set; }

    [Parameter] public EventCallback<SongSortState> OnSortChanged { get; set; }

    /// <summary>The phone Songs page: letter headings and the A to Z strip while sorted by Song or Artist.</summary>
    [Parameter] public bool PhoneLetterIndex { get; set; }

    [Parameter] public RenderFragment? EmptyContent { get; set; }

    /// <summary>The songs as sorted on screen, the one to start from (null for the first) and where they came from; true shuffles.</summary>
    [Parameter] public Func<IReadOnlyList<WorkViewModel>, Guid?, string?, bool, Task>? OnPlayRequested { get; set; }

    [Parameter] public Func<WorkViewModel, Task>? OnFavoriteToggled { get; set; }

    [Parameter] public Func<WorkViewModel, Task>? OnQueueRequested { get; set; }

    [Parameter] public Func<WorkViewModel, Task>? OnPlayNextRequested { get; set; }

    /// <summary>Go to album. Left unset, the table opens the album's page itself.</summary>
    [Parameter] public Func<WorkViewModel, Task>? OnAlbumRequested { get; set; }

    /// <summary>Asks the host to reload its songs after the bulk panel saved changes; the table keeps its sort and selection.</summary>
    [Parameter] public Func<Task>? OnTracksChanged { get; set; }

    /// <summary>Songs are being dragged out: the dragged song, or every ticked song (in list order) when a ticked one is dragged.</summary>
    [Parameter] public Func<IReadOnlyList<Guid>, Task>? OnDragStarted { get; set; }

    [Parameter] public Func<Task>? OnDragEnded { get; set; }

    private string? CurrentRowId => CurrentWorkId?.ToString("D");

    // Worked out at each render: a person's sign-in or the device can change under a mounted list.
    private bool CanDeleteNow => LibraryItemDeletionFlow.CanDelete(DeviceContext, Services.GetService<DashboardSessionAccessor>());

    private EventCallback<SongRow> OpenAlbumCallback => OnAlbumRequested is null
        ? default
        : EventCallback.Factory.Create<SongRow>(this, OpenAlbumAsync);

    private EventCallback<IReadOnlyList<SongRow>> DragStartedCallback => OnDragStarted is null
        ? default
        : EventCallback.Factory.Create<IReadOnlyList<SongRow>>(this, StartDragAsync);

    private EventCallback DragEndedCallback => OnDragEnded is null
        ? default
        : EventCallback.Factory.Create(this, EndDragAsync);

    protected override void OnInitialized()
    {
        DeviceContext.OnChanged += DeviceChanged;
        _orchestrator = Services.GetService<UIOrchestratorService>();
        if (_orchestrator is not null)
        {
            _orchestrator.OnLibraryItemsRemoved += OnLibraryItemsRemoved;
        }
    }

    protected override void OnParametersSet()
    {
        RebuildRows();

        var favoritesKey = (FavoriteIds, FavoriteIds.Count);
        if (_favoritesKey is not { } knownFavorites || !ReferenceEquals(knownFavorites.Source, favoritesKey.FavoriteIds) || knownFavorites.Count != favoritesKey.Count)
        {
            _favoriteIds = FavoriteIds.Select(id => id.ToString("D")).ToHashSet(StringComparer.Ordinal);
            _favoritesKey = favoritesKey;
        }
    }

    protected override void OnAfterRender(bool firstRender)
    {
        // The table hands the bulk bar the ticked rows when it renders; only then can its panel open for the song just ticked.
        if (_openEditPanelAfterRender && _bulkHost is not null)
        {
            _openEditPanelAfterRender = false;
            _bulkHost.OpenEditPanel();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        DeviceContext.OnChanged -= DeviceChanged;
        if (_orchestrator is not null)
        {
            _orchestrator.OnLibraryItemsRemoved -= OnLibraryItemsRemoved;
        }
    }

    /// <summary>
    /// The songs in the order a Songs or Albums list shows them for a sort, so Play and Shuffle in a page header play what is on screen.
    /// Uses the table's own sort rules.
    /// </summary>
    public static IReadOnlyList<WorkViewModel> Ordered(
        IReadOnlyList<WorkViewModel> tracks,
        SongTablePlace place,
        SongSortState sort,
        IReadOnlySet<Guid> favorites)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(sort);
        ArgumentNullException.ThrowIfNull(favorites);

        var byId = tracks.GroupBy(track => track.Id.ToString("D")).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var favoriteIds = favorites.Select(id => id.ToString("D")).ToHashSet(StringComparer.Ordinal);
        return [.. SongTableSort.Sort(tracks.Select(SongRowMapper.FromWork), place, sort, favoriteIds).Select(row => byId[row.Id])];
    }

    // The rows are kept until the songs, or the ones deleted here or elsewhere, change.
    private void RebuildRows()
    {
        var rowsKey = (Tracks, Tracks.Count, _removed.Count);
        if (_rowsKey is { } known && known.Equals(rowsKey))
        {
            return;
        }

        var visible = _removed.Count == 0 ? Tracks : [.. Tracks.Where(track => !_removed.Contains(track.Id))];
        _rows = [.. visible.Select(SongRowMapper.FromWork)];
        _byId = visible.GroupBy(track => track.Id.ToString("D")).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        _rowsKey = rowsKey;
    }

    private void DeviceChanged() => _ = InvokeAsync(StateHasChanged);

    // Songs deleted here or in another window leave the list and the selection at once; the host page reloads behind this.
    private void OnLibraryItemsRemoved(LibraryItemsRemovedEvent removed) => _ = InvokeAsync(() =>
    {
        if (_disposed)
        {
            return;
        }

        MarkRemoved(removed.WorkIds);
        StateHasChanged();
    });

    private void MarkRemoved(IEnumerable<Guid> workIds)
    {
        var gone = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in workIds)
        {
            _removed.Add(id);
            gone.Add(id.ToString("D"));
        }

        if (_selectedIds.Overlaps(gone))
        {
            _selectedIds = _selectedIds.Where(id => !gone.Contains(id)).ToHashSet(StringComparer.Ordinal);
        }

        RebuildRows();
    }

    private WorkViewModel? WorkFor(SongRow row) => _byId.GetValueOrDefault(row.Id);

    // The ticked songs in the order the list shows them, with the album each sits in, for the bulk panel.
    private IReadOnlyList<BulkEditRow> BulkRowsFor(IReadOnlyList<SongRow> selected) => selected.Count == 0
        ? []
        : [.. selected
            .Select(row => (Row: row, Work: WorkFor(row)))
            .Where(pair => pair.Work is not null)
            .Select(pair => new BulkEditRow(
                pair.Work!.Id,
                pair.Row.Title,
                DetailEntityType.Work,
                pair.Work.RootWorkId is { } album && album != pair.Work.Id && album != Guid.Empty ? album : null,
                "Music",
                pair.Row.CoverUrl is { Length: > 0 } cover ? MediaTileArtworkUrl.ForDrawnSize(cover, 56) : null))];

    private Task OnSelectedIdsChangedAsync(IReadOnlySet<string> selected)
    {
        _selectedIds = selected;
        return Task.CompletedTask;
    }

    private Task ClearSelectionAsync()
    {
        _selectedIds = new HashSet<string>();
        return Task.CompletedTask;
    }

    // Songs the bar deleted leave the list and the selection at once, as songs deleted from the row menu do.
    private Task OnDeletedAsync(IReadOnlyList<Guid> workIds)
    {
        MarkRemoved(workIds);
        return Task.CompletedTask;
    }

    // A save reached the library: the host page reloads its songs. The table's own sort, column widths and ticks stay.
    private async Task OnEditedAsync()
    {
        if (OnTracksChanged is { } changed)
        {
            await changed();
        }
    }

    private Task ClearNoticeAsync()
    {
        _notice = null;
        return Task.CompletedTask;
    }

    // A song's Edit on a desktop or tablet ticks the song and opens the Edit panel beside the list (universal editor spec D2, D19): the list's
    // filters, sort and scroll stay where they are. A phone has no side panel and no bulk bar, so there the editor opens over the list on
    // just this song, the same way as for ticked songs.
    private async Task EditAsync(SongRow row)
    {
        // The table only offers Edit to a device and a profile that may edit items; a handler never trusts that.
        if (EditorDeviceRule.ItemRefusal(DeviceContext, Services.GetService<DashboardSessionAccessor>()) is { } refusal)
        {
            _notice = refusal;
            return;
        }

        if (DeviceContext.IsPhone)
        {
            await EditTickedAsync([row]);
        }
        else if (WorkFor(row) is not null)
        {
            // An administrator whose settings are locked is asked for the PIN before the panel opens (the phone path asks in the launcher).
            var gate = await EditorDeviceRule.EnsureMayEditItemsAsync(
                DeviceContext, Services.GetService<DashboardSessionAccessor>(), Services.GetService<IAdministratorSurfaceAccessService>());
            if (!gate.Allowed)
            {
                _notice = gate.Message;
                return;
            }

            _selectedIds = new HashSet<string>(_selectedIds, StringComparer.Ordinal) { row.Id };
            _openEditPanelAfterRender = true;
        }
    }

    // A phone opens the editor over the list on exactly these songs (one from a song's "..." menu, or the ticked ones from the bar's "..."),
    // through the same launcher the other lists use. The table and the launcher both check the device; the Engine checks every save.
    private async Task EditTickedAsync(IReadOnlyList<SongRow> selected)
    {
        if (EditorDeviceRule.ItemRefusal(DeviceContext, Services.GetService<DashboardSessionAccessor>()) is { } refusal)
        {
            _notice = refusal;
            return;
        }

        var rows = BulkRowsFor(selected);
        if (rows.Count == 0)
        {
            return;
        }

        var launcher = Services.GetRequiredService<ListEditLauncher>();
        if (!await launcher.OpenRowsAsync(rows, "Music", OnEditedAsync))
        {
            _notice = launcher.LastNotice ?? EditorDeviceRule.ItemMessage;
        }
    }

    internal Task DeleteRowAsync(SongRow row)
        => row.WorkId is { } workId ? DeleteAsync([workId], row.Title) : Task.CompletedTask;

    private async Task DeleteAsync(IReadOnlyList<Guid> workIds, string? title)
    {
        if (workIds.Count == 0 || _deleting)
        {
            return;
        }

        // The menu item only shows to an administrator on a device that may edit items; a handler never trusts that.
        if (LibraryItemDeletionFlow.Refusal(DeviceContext, Services.GetService<DashboardSessionAccessor>()) is { } refusal)
        {
            _notice = refusal;
            return;
        }

        _notice = null;
        _deleting = true;
        try
        {
            var result = await LibraryItemDeletionFlow.RunAsync(Services.GetRequiredService<IAppDialogService>(), workIds, title);
            if (result.Outcome != LibraryItemDeletionFlowOutcome.Canceled)
            {
                _notice = result.Notice;
                MarkRemoved(workIds);
            }
        }
        finally
        {
            _deleting = false;
        }
    }

    private async Task PlayAsync(SongPlayRequest request)
    {
        if (OnPlayRequested is null)
        {
            return;
        }

        var works = request.Rows.Select(WorkFor).OfType<WorkViewModel>().ToList();
        await OnPlayRequested(works, Guid.TryParse(request.Start.Id, out var start) ? start : null, SourceLabel, false);
    }

    // The bar's Play and Add to queue for ticked songs: the same calls the rows make, with the songs in the order the list shows them.
    private EventCallback<IReadOnlyList<BulkEditRow>> PlayTickedCallback => OnPlayRequested is null
        ? default
        : EventCallback.Factory.Create<IReadOnlyList<BulkEditRow>>(this, PlayTickedAsync);

    private EventCallback<IReadOnlyList<BulkEditRow>> QueueTickedCallback => OnQueueRequested is null
        ? default
        : EventCallback.Factory.Create<IReadOnlyList<BulkEditRow>>(this, QueueTickedAsync);

    // The bar's rows already come in the order the table shows them.
    private IReadOnlyList<WorkViewModel> TickedWorks(IReadOnlyList<BulkEditRow> rows)
        => [.. rows.Select(row => _byId.GetValueOrDefault(row.Id.ToString("D"))).OfType<WorkViewModel>()];

    private async Task PlayTickedAsync(IReadOnlyList<BulkEditRow> rows)
    {
        var works = TickedWorks(rows);
        if (works.Count > 0 && OnPlayRequested is not null)
        {
            await OnPlayRequested(works, null, SourceLabel, false);
        }
    }

    private async Task QueueTickedAsync(IReadOnlyList<BulkEditRow> rows)
    {
        if (OnQueueRequested is null)
        {
            return;
        }

        foreach (var work in TickedWorks(rows))
        {
            await OnQueueRequested(work);
        }
    }

    private Task PlayNextAsync(SongRow row) => WorkFor(row) is { } work && OnPlayNextRequested is not null ? OnPlayNextRequested(work) : Task.CompletedTask;

    private Task QueueAsync(SongRow row) => WorkFor(row) is { } work && OnQueueRequested is not null ? OnQueueRequested(work) : Task.CompletedTask;

    private Task ToggleFavoriteAsync(SongRow row) => WorkFor(row) is { } work && OnFavoriteToggled is not null ? OnFavoriteToggled(work) : Task.CompletedTask;

    private Task OpenAlbumAsync(SongRow row) => WorkFor(row) is { } work && OnAlbumRequested is not null ? OnAlbumRequested(work) : Task.CompletedTask;

    private Task StartDragAsync(IReadOnlyList<SongRow> rows) =>
        OnDragStarted is null
            ? Task.CompletedTask
            : OnDragStarted([.. rows.Select(row => Guid.TryParse(row.Id, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty)]);

    private Task EndDragAsync() => OnDragEnded is not null ? OnDragEnded() : Task.CompletedTask;
}
