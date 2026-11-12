using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Enums;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Components.Details;

/// <summary>
/// The album page's track list on the one <see cref="SongTable"/> (spec music-table-and-reader-frame WP4). It owns the album's tracks and
/// what playing, queueing and favoriting them mean, the Show missing tracks switch, the ticks that feed the page's bulk bar, a track's own
/// Edit and Delete, and dragging a track out to a playlist; the table owns the columns, sort, rows and phone list.
/// </summary>
public partial class MusicTrackList
{
    private readonly HashSet<Guid> _favoriteIds = [];
    private readonly HashSet<Guid> _removed = [];
    private Guid? _favoritesProfileId;
    private Guid? _profileId;
    private IReadOnlySet<string> _favoriteRowIds = new HashSet<string>();
    private IReadOnlyList<SongRow> _rows = [];
    private Dictionary<string, MediaGroupingItemViewModel> _itemsByRowId = new(StringComparer.Ordinal);
    private IReadOnlySet<string> _selectedIds = new HashSet<string>();
    private SongSortState _sort = SongTableSort.DefaultFor(SongTablePlace.AlbumPage);
    private bool _showMissing;
    private string? _notice;
    private bool _deleting;
    private bool _disposed;
    private BulkEditSelection? _hookedSelection;

    [Inject] private PlaybackSessionController Playback { get; set; } = default!;

    [Inject] private SavedItemService SavedItems { get; set; } = default!;

    [Inject] private ListenAudioDragService AudioDrag { get; set; } = default!;

    [Inject] private UIOrchestratorService Orchestrator { get; set; } = default!;

    [Inject] private DeviceContextService DeviceContext { get; set; } = default!;

    [Inject] private EditorHost EditorHost { get; set; } = default!;

    [Inject] private IServiceProvider Services { get; set; } = default!;

    [Parameter] public IReadOnlyList<MediaGroupingViewModel> Groups { get; set; } = [];

    [Parameter] public string AlbumTitle { get; set; } = "Album";

    [Parameter] public string? AlbumId { get; set; }

    [Parameter] public string? ArtistId { get; set; }

    [Parameter] public string? ArtistName { get; set; }

    [Parameter] public string? AuthorName { get; set; }

    [Parameter] public string? NarratorName { get; set; }

    [Parameter] public string? Synopsis { get; set; }

    [Parameter] public IReadOnlyList<PlaybackContributorIdentity> Authors { get; set; } = [];

    [Parameter] public IReadOnlyList<PlaybackContributorIdentity> Narrators { get; set; } = [];

    [Parameter] public string? AudiobookId { get; set; }

    [Parameter] public string? AlbumArtworkUrl { get; set; }

    [Parameter] public bool IsAudiobook { get; set; }

    [Parameter] public RenderFragment? ToolbarLeadingContent { get; set; }

    /// <summary>Each row of a page the person may edit gets a tick box. Null outside a detail page.</summary>
    [CascadingParameter] public BulkEditSelection? BatchSelection { get; set; }

    /// <summary>The page this list sits on, for a row's own Edit (opens the pop-over editor over the page). Null outside a detail page.</summary>
    [CascadingParameter] public EditorBinding? Editor { get; set; }

    private IReadOnlyList<MediaGroupingItemViewModel> AllItems =>
        [.. Groups.SelectMany(group => group.Items)
            .Where(item => item.EntityType == DetailEntityType.Work && !_removed.Contains(ItemId(item)))];

    private string TrackCountSummary
    {
        get
        {
            var items = AllItems;
            return L["MusicTrackList_TracksOwned", items.Count(item => item.IsOwned), items.Count].Value;
        }
    }

    // What a ticked row knows about where it sits: its album, so Renumber, Match and the move step work from it.
    private Guid? RowParentId => Guid.TryParse(AlbumId, out var parent) && parent != Guid.Empty ? parent : null;

    private const string RowMediaType = "Music";

    // Ticks are for every album page, not only one the person may edit: ticked songs are played, queued and put in playlists by anyone
    // (Listen music spec D9). The bar then shows Edit only where the page and device allow it.
    private bool SelectionShown => BatchSelection is not null;

    private bool CanEditRows => Editor is { CanEdit: true, EditRowAsync: not null };

    // Delete from library is for an effective administrator on a device that may edit items, on a page the person may edit.
    private bool CanDeleteRows => Editor is { CanEdit: true } && LibraryItemDeletionFlow.CanDelete(DeviceContext, Services.GetService<DashboardSessionAccessor>());

    private string? CurrentRowId
    {
        get
        {
            var workId = Playback.CurrentItem?.WorkId;
            if (workId is null || workId == Guid.Empty)
            {
                return null;
            }

            return _rows.FirstOrDefault(row => row.WorkId == workId)?.Id;
        }
    }

    private EventCallback<IReadOnlyList<SongRow>> DragStartedCallback => EventCallback.Factory.Create<IReadOnlyList<SongRow>>(this, StartDrag);

    private EventCallback DragEndedCallback => EventCallback.Factory.Create(this, EndDrag);

    protected override void OnInitialized() => Playback.Changed += OnPlaybackChanged;

    protected override async Task OnParametersSetAsync()
    {
        HookBatchState();
        RebuildRows();
        RebuildSelected();

        if (IsAudiobook)
        {
            return;
        }

        var profile = await Orchestrator.GetActiveProfileAsync();
        _profileId = profile?.Id;

        // Favorites are the profile's saved songs: one profile-wide read, so fetch it once per profile for this list;
        // toggles keep the set current locally.
        if (_favoritesProfileId is { } loadedFor && loadedFor == profile?.Id)
        {
            return;
        }

        _favoriteIds.Clear();
        if (profile is not null)
        {
            _favoriteIds.UnionWith((await SavedItems.GetListAsync())
                .Where(saved => saved.EntityKind == ProfileEntityKind.Song)
                .Select(saved => saved.EntityId));
        }

        _favoritesProfileId = profile?.Id;
        RebuildFavoriteRowIds();
    }

    public void Dispose()
    {
        _disposed = true;
        Playback.Changed -= OnPlaybackChanged;
        if (_hookedSelection is not null)
        {
            _hookedSelection.ListOrder = null;
            _hookedSelection.Changed -= OnBatchStateChanged;
        }
    }

    // ---- Rows ------------------------------------------------------------------------------------------------------

    private void RebuildRows()
    {
        var shown = AllItems.Where(item => _showMissing || item.IsOwned).ToList();
        var byId = new Dictionary<string, MediaGroupingItemViewModel>(shown.Count, StringComparer.Ordinal);
        var rows = new List<SongRow>(shown.Count);
        foreach (var item in shown)
        {
            if (!byId.TryAdd(item.Id, item))
            {
                continue;
            }

            rows.Add(ToRow(item));
        }

        _rows = rows;
        _itemsByRowId = byId;
        RebuildFavoriteRowIds();
    }

    private SongRow ToRow(MediaGroupingItemViewModel item)
    {
        var row = SongRowMapper.FromGroupingItem(item);
        return row with
        {
            // A track without its own picture shows the album's.
            CoverUrl = row.CoverUrl ?? (string.IsNullOrWhiteSpace(AlbumArtworkUrl) ? null : AlbumArtworkUrl),
            // Go to artist opens the album's artist, but only for a track that is theirs (a compilation names other artists).
            ArtistPersonId = SharesAlbumArtist(row) && Guid.TryParse(ArtistId, out var artistId) && artistId != Guid.Empty ? artistId : null,
        };
    }

    private bool SharesAlbumArtist(SongRow row)
        => string.IsNullOrWhiteSpace(row.Artist)
           || string.IsNullOrWhiteSpace(ArtistName)
           || string.Equals(row.Artist.Trim(), ArtistName.Trim(), StringComparison.OrdinalIgnoreCase);

    private void RebuildFavoriteRowIds()
        => _favoriteRowIds = _rows
            .Where(row => row.WorkId is { } id && _favoriteIds.Contains(id))
            .Select(row => row.Id)
            .ToHashSet(StringComparer.Ordinal);

    private MediaGroupingItemViewModel? ItemFor(SongRow row) => _itemsByRowId.GetValueOrDefault(row.Id);

    private void ToggleMissing(ChangeEventArgs args)
    {
        _showMissing = args.Value is true
            || string.Equals(args.Value?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        RebuildRows();
    }

    private Task OnSortChangedAsync(SongSortState sort)
    {
        _sort = sort;
        return Task.CompletedTask;
    }

    private void OnPlaybackChanged(PlaybackChangeKind kind)
    {
        if (kind != PlaybackChangeKind.TransportTick && !_disposed)
        {
            _ = InvokeAsync(StateHasChanged);
        }
    }

    // ---- Ticks and the page's bulk bar ------------------------------------------------------------------------------

    private void HookBatchState()
    {
        if (ReferenceEquals(_hookedSelection, BatchSelection))
        {
            return;
        }

        if (_hookedSelection is not null)
        {
            _hookedSelection.ListOrder = null;
            _hookedSelection.Changed -= OnBatchStateChanged;
        }

        _hookedSelection = BatchSelection;
        if (_hookedSelection is not null)
        {
            _hookedSelection.Changed += OnBatchStateChanged;
            // "Renumber sequentially" follows the order this list shows now, whatever it is sorted by.
            _hookedSelection.ListOrder = ShownOrder;
        }
    }

    private IReadOnlyList<Guid> ShownOrder() =>
        [.. SongTableSort.Sort(_rows, SongTablePlace.AlbumPage, _sort, _favoriteRowIds)
            .Select(row => row.WorkId ?? Guid.Empty)
            .Where(id => id != Guid.Empty)];

    private void OnBatchStateChanged() => _ = InvokeAsync(() =>
    {
        if (_disposed)
        {
            return;
        }

        RebuildSelected();
        StateHasChanged();
    });

    // The ticked rows the table shows are the page's selection, so the bar above the rows and the ticks always agree.
    private void RebuildSelected()
    {
        if (BatchSelection is null)
        {
            _selectedIds = new HashSet<string>();
            return;
        }

        var ticked = BatchSelection.Ids.ToHashSet();
        var next = _rows.Where(row => row.WorkId is { } id && ticked.Contains(id)).Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        if (!next.SetEquals(_selectedIds))
        {
            _selectedIds = next;
        }
    }

    private Task OnSelectedIdsChangedAsync(IReadOnlySet<string> selected)
    {
        if (BatchSelection is null)
        {
            return Task.CompletedTask;
        }

        // The ticks belong to this page's editor binding, so the bar's Edit opens the pop-over for this page (only a page that may be edited has one).
        if (Editor is { CanEdit: true })
        {
            EditorHost.Bind(Editor);
        }

        var ticked = BatchSelection.Ids.ToHashSet();
        foreach (var row in _rows)
        {
            if (row.WorkId is not { } id || ItemFor(row) is not { } item || !BulkEditRules.IsSelectableKind(item.EntityType))
            {
                continue;
            }

            if (selected.Contains(row.Id) != ticked.Contains(id))
            {
                // A full selection is told by the bar above the rows; the tick simply stays off.
                BatchSelection.Toggle(ToBulkRow(item));
            }
        }

        // Make the table show what the selection really holds (a tick that could not be added is dropped again).
        RebuildSelected();
        _selectedIds = new HashSet<string>(_selectedIds, StringComparer.Ordinal);
        return Task.CompletedTask;
    }

    private BulkEditRow ToBulkRow(MediaGroupingItemViewModel item)
        => new(ItemId(item), item.Title, item.EntityType, RowParentId, RowMediaType);

    // ---- Actions ---------------------------------------------------------------------------------------------------

    private async Task PlayAsync(SongPlayRequest request)
    {
        var playable = request.Rows.Select(ItemFor).OfType<MediaGroupingItemViewModel>().Where(item => item.IsOwned).ToList();
        if (playable.Count == 0 || ItemFor(request.Start) is not { } start)
        {
            return;
        }

        var index = Math.Max(0, playable.FindIndex(item => ReferenceEquals(item, start)));
        await Playback.ReplaceQueueItemsAsync(playable.Select(ToQueueItem), index, AlbumTitle, shuffle: false);
    }

    // The bar's Play and Add to queue for ticked tracks: the same calls the rows make, with the tracks in the order the table shows them.
    private EventCallback<IReadOnlyList<BulkEditRow>> PlayTickedCallback(IReadOnlyList<SongRow> ticked)
        => EventCallback.Factory.Create<IReadOnlyList<BulkEditRow>>(this, async _ =>
        {
            var owned = ticked.Where(row => row.IsOwned).ToList();
            if (owned.Count > 0)
            {
                await PlayAsync(new SongPlayRequest(owned, owned[0]));
            }
        });

    private EventCallback<IReadOnlyList<BulkEditRow>> QueueTickedCallback(IReadOnlyList<SongRow> ticked)
        => EventCallback.Factory.Create<IReadOnlyList<BulkEditRow>>(this, async _ =>
        {
            foreach (var row in ticked)
            {
                await QueueAsync(row);
            }
        });

    private async Task PlayNextAsync(SongRow row)
    {
        if (ItemFor(row) is { IsOwned: true } item)
        {
            await Playback.AddQueueItemAsync(ToQueueItem(item), next: true);
        }
    }

    private async Task QueueAsync(SongRow row)
    {
        if (ItemFor(row) is { IsOwned: true } item)
        {
            await Playback.AddQueueItemAsync(ToQueueItem(item));
        }
    }

    private async Task ToggleFavoriteAsync(SongRow row)
    {
        if (!row.IsOwned || row.WorkId is not { } id || id == Guid.Empty)
        {
            return;
        }

        var profile = await Orchestrator.GetActiveProfileAsync();
        if (profile is null)
        {
            _notice = L["Common_FavoritesRequireAnActiveProfile"].Value;
            return;
        }

        _notice = null;
        // Favorite is saved-item membership; it never sets or clears Like or Dislike.
        var membership = await SavedItems.ToggleAsync(ProfileEntityKind.Song, id);
        if (membership is null)
        {
            return;
        }

        if (membership.IsSaved)
        {
            _favoriteIds.Add(id);
        }
        else
        {
            _favoriteIds.Remove(id);
        }

        RebuildFavoriteRowIds();
    }

    private Task ClearNoticeAsync()
    {
        _notice = null;
        return Task.CompletedTask;
    }

    // A track's Edit opens the pop-over over this page with this track ticked and the Edit panel open (universal editor spec D19).
    private async Task EditAsync(SongRow row)
    {
        if (CanEditRows && ItemFor(row) is { } item && BulkEditRules.IsSelectableKind(item.EntityType))
        {
            await Editor!.EditRowAsync!(ToBulkRow(item));
        }
    }

    private async Task DeleteRowAsync(SongRow row)
    {
        if (row.WorkId is not { } workId || _deleting)
        {
            return;
        }

        // The menu item only shows to an administrator on a device that may edit items; a handler never trusts that.
        // A locked administrator is asked for the PIN before the question; closing the prompt stops quietly.
        var deleteGate = await LibraryItemDeletionFlow.EnsureMayDeleteAsync(DeviceContext, Services.GetService<DashboardSessionAccessor>(), Services.GetService<IAdministratorSurfaceAccessService>());
        if (!deleteGate.Allowed)
        {
            if (deleteGate.Message is { } refusal)
            {
                _notice = refusal;
            }

            return;
        }

        _notice = null;
        _deleting = true;
        try
        {
            var result = await LibraryItemDeletionFlow.RunAsync(Services.GetRequiredService<IAppDialogService>(), [workId], row.Title);
            if (result.Outcome != LibraryItemDeletionFlowOutcome.Canceled)
            {
                _notice = result.Notice;
                // The track leaves the list at once; the page reloads behind this when the Engine tells it the title is gone.
                _removed.Add(workId);
                RebuildRows();
                RebuildSelected();
            }
        }
        finally
        {
            _deleting = false;
        }
    }

    // The songs being dragged out (every ticked track when a ticked one is dragged) are also offered to the queue panel.
    private void StartDrag(IReadOnlyList<SongRow> rows)
    {
        var items = rows
            .Select(ItemFor)
            .OfType<MediaGroupingItemViewModel>()
            .Where(item => item.IsOwned)
            .Select(ToQueueItem)
            .ToList();
        if (items.Count > 0)
        {
            AudioDrag.BeginDrag(items);
        }
    }

    private void EndDrag() => AudioDrag.Clear();

    private ListenQueueItem ToQueueItem(MediaGroupingItemViewModel item)
        => new()
        {
            WorkId = ItemId(item),
            AlbumWorkId = Guid.TryParse(AlbumId, out var albumId) ? albumId : null,
            ArtistPersonId = Guid.TryParse(ArtistId, out var artistId) ? artistId : null,
            AssetId = Guid.TryParse(item.AssetId, out var assetId) ? assetId : null,
            MediaType = RowMediaType,
            Title = item.Title,
            Subtitle = string.IsNullOrWhiteSpace(ArtistName) ? item.Artist : ArtistName,
            Album = AlbumTitle,
            CoverUrl = item.ArtworkUrl ?? AlbumArtworkUrl,
            Duration = SongRowMapper.FromGroupingItem(item).DurationText,
            InitialPositionSeconds = AudioItemTable.ResumePositionFor(item),
            Chapters = [],
        };

    private static Guid ItemId(MediaGroupingItemViewModel item)
        => Guid.TryParse(item.Id, out var id) ? id : Guid.Empty;
}
