using System.Globalization;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Playback;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.Listen;

/// <summary>
/// The one song table (spec music-table-and-reader-frame). It owns the columns, header (sort, resize, Columns button), rows, now-playing
/// mark, heart, hover queue, "..." menu, phone list and in-place notice; the page that hosts it owns the songs and what each action does.
/// </summary>
public partial class SongTable
{
    // The "Columns" button's glyph (Material view_column, outlined): three columns in a frame.
    private const string ColumnsIcon = "M14.67 5v14H9.33V5h5.34zm1 14H21V5h-5.33v14zm-7.34 0V5H3v14h5.33z";

    private const int SkeletonRowCount = 8;
    private const int ActionsColumnWidth = 100;
    private const int ColumnGap = 8;
    private const int PhoneRowHeight = 64;
    private const int PhoneJumpOffset = 8;

    private SongTableChoice _choice = SongTableChoice.Default;
    private (Guid Profile, SongTablePlace Place)? _choiceKey;
    private SongTablePlace _placeShown;
    private SongSortState _sort = new(null, false);
    private SongSortState? _hostSort;
    private IReadOnlyList<SongColumnLayout> _layout = [];
    private string _gridVariables = string.Empty;
    private bool _multipleArtists;

    private List<SongRow> _shown = [];
    private Dictionary<string, int> _positions = [];
    private string? _firstUnnumberedKey;
    private (IReadOnlyList<SongRow>? Rows, int Count, SongTablePlace Place, SongSortState Sort, IReadOnlySet<string>? Favorites, int FavoriteCount)? _shownKey;

    private List<PhoneListItem> _phoneItems = [];
    private (List<SongRow> Shown, bool Letters, SongColumn? Column)? _phoneItemsKey;
    private HashSet<string> _presentLetters = [];
    private ElementReference _phoneList;

    private readonly HashSet<string> _selected = [];
    private IReadOnlySet<string>? _hostSelection;

    private string? _notice;
    private string? _hostNotice;
    private UndoOffer? _undo;
    private UndoOffer? _hostUndo;
    private string? _announcement;

    // Column resizing runs in the browser (wwwroot/js/song-table.js); .NET hears once when a drag ends.
    private ElementReference _tableRoot;
    private IJSObjectReference? _resizeModule;
    private DotNetObjectReference<SongTable>? _self;
    private string? _wiredRootId;

    private IReadOnlyList<SongRow>? _draggingOut;
    private AppSeverity _noticeSeverity = AppSeverity.Warning;
    private SongDragDropService? _dropService;
    private string? _dragKey;
    private string? _focusRowKey;
    private string? _dropBeforeKey;
    private bool _dropAtEnd;

    [Inject] private IServiceProvider Services { get; set; } = default!;

    /// <summary>Where the table is shown: decides its columns, defaults, sort rules and which actions apply.</summary>
    [Parameter, EditorRequired] public SongTablePlace Place { get; set; }

    /// <summary>The songs, in the host's own order (a playlist shows them exactly so). Rows with <see cref="SongRow.IsOwned"/> false are shown dimmed with no actions.</summary>
    [Parameter] public IReadOnlyList<SongRow> Rows { get; set; } = [];

    /// <summary>True while the songs are being fetched: the header shows with skeleton rows beneath it.</summary>
    [Parameter] public bool IsLoading { get; set; }

    /// <summary>The table's accessible name; "Songs" when not set.</summary>
    [Parameter] public string? AriaLabel { get; set; }

    /// <summary>The signed-in profile. With it, the person's chosen columns and widths are remembered for this place; without it they last for the visit.</summary>
    [Parameter] public Guid? ProfileId { get; set; }

    /// <summary>The <see cref="SongRow.Id"/> of the song the player is on; its row gets the equalizer mark and an accent title.</summary>
    [Parameter] public string? CurrentRowId { get; set; }

    /// <summary>True while the current song plays (the mark moves); false shows it still.</summary>
    [Parameter] public bool IsPlaying { get; set; }

    /// <summary>The <see cref="SongRow.Id"/> values saved to Favorites: their heart is filled.</summary>
    [Parameter] public IReadOnlySet<string> FavoriteIds { get; set; } = new HashSet<string>();

    /// <summary>A sort chosen outside the table (the browse header's sort menu). Each time it changes the table sorts that way; a header click afterwards changes it again.</summary>
    [Parameter] public SongSortState? Sort { get; set; }

    /// <summary>Raised with the new sort after a header click, so the host can remember it.</summary>
    [Parameter] public EventCallback<SongSortState> OnSortChanged { get; set; }

    /// <summary>False draws no cover art (the song cell keeps a play button). Phone: an album's own page never shows covers, whatever this says.</summary>
    [Parameter] public bool ShowCovers { get; set; } = true;

    /// <summary>The album's own artist, for the phone album page's rule that an artist shows only when it differs. Left empty, the most common artist is used.</summary>
    [Parameter] public string? AlbumArtist { get; set; }

    /// <summary>Draws the tick column and the host's bulk bar. Only a desktop that may edit shows it; a phone never does.</summary>
    [Parameter] public bool ShowSelection { get; set; }

    /// <summary>The ticked <see cref="SongRow.Id"/> values. Optional: when the host passes it, a new set replaces the table's own.</summary>
    [Parameter] public IReadOnlySet<string>? SelectedIds { get; set; }

    [Parameter] public EventCallback<IReadOnlySet<string>> SelectedIdsChanged { get; set; }

    /// <summary>The host's bulk bar (Edit, Match, Move to album, Delete), drawn above the table with the ticked rows in the order shown.</summary>
    [Parameter] public RenderFragment<IReadOnlyList<SongRow>>? BulkContent { get; set; }

    /// <summary>The person may edit songs here (the Engine decides who really may). Edit also needs a desktop with a fine, hover-capable pointer.</summary>
    [Parameter] public bool CanEdit { get; set; }

    /// <summary>An administrator may delete songs from the library (the host works it out). Never offered on a phone.</summary>
    [Parameter] public bool CanDelete { get; set; }

    /// <summary>The person may reorder this playlist: drag, Alt+Up and Alt+Down, and Move up and Move down on a phone.</summary>
    [Parameter] public bool CanReorder { get; set; }

    /// <summary>A line to show in place above the table (a failed favorite, delete or status change); closing it raises <see cref="OnNoticeDismissed"/>.</summary>
    [Parameter] public string? Notice { get; set; }

    [Parameter] public EventCallback OnNoticeDismissed { get; set; }

    /// <summary>A change the person can take back, shown in place above the table with Undo; when it goes away <see cref="OnUndoCleared"/> is raised.</summary>
    [Parameter] public UndoOffer? Undo { get; set; }

    [Parameter] public EventCallback OnUndoCleared { get; set; }

    /// <summary>What shows instead of the table when there are no songs and it is not loading (the page's existing empty line).</summary>
    [Parameter] public RenderFragment? EmptyContent { get; set; }

    /// <summary>A song was chosen to play: the owned rows as shown, and where to start.</summary>
    [Parameter] public EventCallback<SongPlayRequest> OnPlay { get; set; }

    [Parameter] public EventCallback<SongRow> OnPlayNext { get; set; }

    [Parameter] public EventCallback<SongRow> OnQueue { get; set; }

    /// <summary>Go to album. Left unset, the table opens the album's page itself.</summary>
    [Parameter] public EventCallback<SongRow> OnOpenAlbum { get; set; }

    /// <summary>Go to artist. Left unset, the table opens the artist's page itself.</summary>
    [Parameter] public EventCallback<SongRow> OnOpenArtist { get; set; }

    [Parameter] public EventCallback<SongRow> OnToggleFavorite { get; set; }

    /// <summary>Edit this song (the host ticks it and opens its Edit panel).</summary>
    [Parameter] public EventCallback<SongRow> OnEdit { get; set; }

    /// <summary>Delete this song from the library (the host runs the confirmation and the delete).</summary>
    [Parameter] public EventCallback<SongRow> OnDelete { get; set; }

    /// <summary>Remove this song from the playlist (playlist only).</summary>
    [Parameter] public EventCallback<SongRow> OnRemoveFromPlaylist { get; set; }

    /// <summary>The words for the row action that takes a song out of the list; left empty it reads "Remove from playlist". Favorites says "Remove from Favorites".</summary>
    [Parameter] public string? RemoveLabel { get; set; }

    /// <summary>
    /// The list has an order of its own (Favorites) and its headers may also sort it. Reordering by hand is offered only while the list
    /// is in its own order, not while a column sorts it. Playlist only.
    /// </summary>
    [Parameter] public bool SortableOwnOrder { get; set; }

    /// <summary>A playlist row moved; the host saves the order and hands back the reordered rows.</summary>
    [Parameter] public EventCallback<SongReorderRequest> OnReorder { get; set; }

    /// <summary>
    /// Songs are being dragged out of the table: the dragged row, or every ticked row (in list order) when the dragged row is ticked. The
    /// table itself carries them to a playlist (<see cref="SongDragDropService"/>); this lets a host also use them, for example to drop them
    /// on the queue. A playlist's own rows still drag to reorder inside the table.
    /// </summary>
    [Parameter] public EventCallback<IReadOnlyList<SongRow>> OnDragOutStarted { get; set; }

    [Parameter] public EventCallback OnDragOutEnded { get; set; }

    /// <summary>
    /// On a phone, group the list under letter headings and show the A to Z jump strip while it is sorted by Song or Artist (the phone
    /// Songs page, spec music-table-and-reader-frame D21). Ignored everywhere else.
    /// </summary>
    [Parameter] public bool PhoneLetterIndex { get; set; }

    private bool IsPhoneList => DeviceContext.IsPhone;

    private string AriaLabelText => string.IsNullOrWhiteSpace(AriaLabel) ? L["Common_Songs"].Value : AriaLabel;

    // Tick boxes and the host's bar are for a desktop or tablet whose host wants them (ticked songs play, queue and go in playlists without
    // any right to edit; the host's bar offers Edit only where editing is allowed). A phone ticks only in select mode, with its own bar.
    private bool TickShown => IsPhoneList ? SelectModeOn : ShowSelection && (DeviceContext.IsDesktop || DeviceContext.IsTablet);

    // Select mode (Listen music spec D12): chosen from a song's "..." sheet on a phone; rows then tick when tapped and a bottom bar appears.
    private bool _selectMode;

    private bool SelectModeOn => _selectMode && IsPhoneList;

    private bool SelectModeAvailable => IsPhoneList && OnPlay.HasDelegate;

    private bool ShowEmpty => !IsLoading && _shown.Count == 0 && EmptyContent is not null;

    private bool OwnOrderSortable => Place == SongTablePlace.Playlist && SortableOwnOrder;

    private bool ReorderEnabled => Place == SongTablePlace.Playlist && CanReorder && OnReorder.HasDelegate && (!OwnOrderSortable || _sort.Column is null);

    // Drag is a mouse affordance; touch screens and phones move rows from the "..." menu or the keyboard.
    private bool CanDrag => ReorderEnabled && !DeviceContext.IsTouch && !DeviceContext.IsPhone;

    // Dragging songs out to somewhere else (a playlist in the rail or a playlist tile) is also a mouse affordance. A playlist's rows reorder
    // inside the table and are carried out when the drag leaves it: a drop on a row or the end zone reorders, a drop on a playlist adds.
    private bool CanDragOut => (OnDragOutStarted.HasDelegate || _dropService is not null) && !DeviceContext.IsTouch && !DeviceContext.IsPhone;

    // "3 songs" for the drag picture when a ticked row carries more than one song; the browser script reads it from the table.
    private string? DragCountLabel => CanDragOut && TickedPlayable.Count > 1 ? L["SongTable_DragCount", TickedPlayable.Count].Value : null;

    private bool LettersShown => PhoneLetterIndex && IsPhoneList && SongTableSort.ShowsLetters(Place, _sort);

    // Add to queue on hover or focus needs a pointer; a touch screen finds it in the "..." menu.
    private bool ShowHoverQueue => OnQueue.HasDelegate && !DeviceContext.IsTouch;

    private bool PhoneCovers => ShowCovers && Place != SongTablePlace.AlbumPage;

    private IReadOnlyList<SongRow> SelectedRows => _selected.Count == 0
        ? []
        : [.. _shown.Where(row => _selected.Contains(row.Id))];

    protected override void OnInitialized()
    {
        DeviceContext.OnChanged += DeviceChanged;
        _dropService = Services.GetService<SongDragDropService>();
        if (_dropService is not null)
        {
            _dropService.NoticeRaised += OnDropNotice;
        }

        _placeShown = Place;
        _sort = SongTableSort.DefaultFor(Place);
    }

    protected override void OnParametersSet()
    {
        if (_placeShown != Place)
        {
            // The same table now shows another place: its own columns and its own default sort.
            _placeShown = Place;
            _choice = SongTableChoice.Default;
            _choiceKey = null;
            _sort = SongTableSort.DefaultFor(Place);
            _shownKey = null;
        }

        if (_selectMode && !IsPhoneList)
        {
            // The window became wider than a phone: select mode belongs to the phone list only.
            _selectMode = false;
        }

        if (Sort is { } requested && requested != _hostSort)
        {
            _sort = requested;
            _hostSort = requested;
        }

        if (!string.Equals(Notice, _hostNotice, StringComparison.Ordinal))
        {
            _hostNotice = Notice;
            _notice = Notice;
            _noticeSeverity = AppSeverity.Warning;
        }

        if (!ReferenceEquals(Undo, _hostUndo))
        {
            _hostUndo = Undo;
            _undo = Undo;
        }

        if (SelectedIds is not null && !ReferenceEquals(SelectedIds, _hostSelection))
        {
            _hostSelection = SelectedIds;
            _selected.Clear();
            _selected.UnionWith(SelectedIds);
        }

        RebuildShown();

        if (_selected.Count > 0)
        {
            // A search or filter that hides ticked songs drops their ticks (as the bulk lists elsewhere do), so what the bar counts is what
            // Play, Add to queue and Add to playlist use. The host is told, so it does not bring them back when the search is cleared.
            var present = Rows.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
            if (_selected.RemoveWhere(id => !present.Contains(id)) > 0 && SelectedIdsChanged.HasDelegate)
            {
                var kept = _selected.ToHashSet(StringComparer.Ordinal);
                _ = InvokeAsync(() => SelectedIdsChanged.InvokeAsync(kept));
            }
        }

        UpdateLayout();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await LoadChoiceAsync();
        await FocusMovedRowAsync();
        await WireResizeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        DeviceContext.OnChanged -= DeviceChanged;
        if (_dropService is not null)
        {
            _dropService.NoticeRaised -= OnDropNotice;
            _dropService.End(this);
        }

        await UnwireResizeAsync();
        if (_resizeModule is not null)
        {
            await ReleaseAsync(() => _resizeModule.DisposeAsync().AsTask());
            _resizeModule = null;
        }

        _self?.Dispose();
        _self = null;
    }

    private void DeviceChanged() => _ = InvokeAsync(StateHasChanged);

    // What a drop of this table's songs said ("3 songs added to Road trip"): shown in place above the table, like the selection bar's sentence.
    private void OnDropNotice(SongDropNotice drop)
    {
        if (!ReferenceEquals(drop.Owner, this))
        {
            return;
        }

        _notice = drop.Notice.Message;
        _noticeSeverity = drop.Notice.Severity;
        _ = InvokeAsync(StateHasChanged);
    }

    // ---- Rows, sort and layout ---------------------------------------------------------------------------------

    private static string RowKey(SongRow row) => row.PlaylistItemId?.ToString("D") ?? row.Id;

    private static string RowDomId(SongRow row) => $"song-table-row-{RowKey(row)}";

    private void RebuildShown()
    {
        // A playlist is shown exactly as the host gave it, so it is read again each time; sorting a long list twice per render would be wasteful.
        var key = (Rows, Rows.Count, Place, _sort, FavoriteIds, FavoriteIds?.Count ?? 0);
        if (Place != SongTablePlace.Playlist && _shownKey is { } known && known.Equals(key))
        {
            return;
        }

        _shown = Place == SongTablePlace.Playlist && !OwnOrderSortable
            ? [.. Rows]
            : [.. SongTableSort.Sort(Rows, Place, _sort, FavoriteIds, OwnOrderSortable)];
        _shownKey = key;

        _positions = new Dictionary<string, int>(_shown.Count, StringComparer.Ordinal);
        for (var index = 0; index < _shown.Count; index++)
        {
            _positions.TryAdd(RowKey(_shown[index]), index + 1);
        }

        _firstUnnumberedKey = Place == SongTablePlace.AlbumPage
            ? _shown.Where(row => row.IsUnnumbered).Select(RowKey).FirstOrDefault()
            : null;
        _multipleArtists = Place == SongTablePlace.AlbumPage && SongTableColumns.HasMultipleArtists(Rows);
    }

    /// <summary>One entry of the phone list: a song, or the letter heading above a run of songs.</summary>
    private sealed record PhoneListItem(SongRow? Row, string? Letter);

    private List<PhoneListItem> PhoneItems
    {
        get
        {
            var letters = LettersShown;
            var key = (_shown, letters, _sort.Column);
            if (_phoneItemsKey is { } known && ReferenceEquals(known.Shown, _shown) && known.Letters == letters && known.Column == _sort.Column)
            {
                return _phoneItems;
            }

            var items = new List<PhoneListItem>(_shown.Count + (letters ? 27 : 0));
            string? previous = null;
            foreach (var row in _shown)
            {
                if (letters)
                {
                    var letter = SongTableSort.LetterFor(_sort.Column == SongColumn.Artist ? row.Artist : row.Title);
                    if (!string.Equals(letter, previous, StringComparison.Ordinal))
                    {
                        items.Add(new PhoneListItem(null, letter));
                        previous = letter;
                    }
                }

                items.Add(new PhoneListItem(row, null));
            }

            _phoneItems = items;
            _presentLetters = [.. items.Where(item => item.Letter is not null).Select(item => item.Letter!)];
            _phoneItemsKey = key;
            return items;
        }
    }

    // The letters that have songs under them. Reading the items first makes sure they are worked out for the current rows and sort.
    private HashSet<string> PresentLetters
    {
        get
        {
            _ = PhoneItems;
            return _presentLetters;
        }
    }

    // The strip's letters: A to Z then #. A letter with no songs under it is shown dimmed and does nothing.
    private static readonly string[] StripLetters = [.. Enumerable.Range('A', 26).Select(code => ((char)code).ToString()), "#"];

    private async Task JumpToLetterAsync(string letter)
    {
        var index = PhoneItems.ToList().FindIndex(item => item.Letter == letter);
        if (index < 0)
        {
            return;
        }

        try
        {
            await using var module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/song-table.js");
            await module.InvokeVoidAsync("scrollToIndex", _phoneList, index, PhoneRowHeight, PhoneJumpOffset);
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException)
        {
            Logger.LogWarning(exception, "Could not jump to a letter in the song list.");
        }
        catch (JSDisconnectedException exception)
        {
            Logger.LogDebug(exception, "The browser went away before the song list could jump to a letter.");
        }
    }

    private int RowIndex(SongRow row) => _shown.FindIndex(candidate => RowKey(candidate) == RowKey(row));

    private void UpdateLayout()
    {
        _layout = SongTableColumns.Resolve(Place, _choice, _multipleArtists, TickShown);

        // Columns give up width down to their minimum before the table scrolls sideways; the Song column takes what is left over.
        var template = new List<string>();
        var minimum = ActionsColumnWidth + ColumnGap * (_layout.Count + 1) + 16;
        foreach (var layout in _layout)
        {
            switch (layout.Column)
            {
                case SongColumn.Tick:
                    template.Add($"{SongTableColumns.DefaultWidth(SongColumn.Tick)}px");
                    minimum += SongTableColumns.DefaultWidth(SongColumn.Tick);
                    break;
                case SongColumn.Song:
                    // Song soaks up the spare width until the person sets its width; then it keeps exactly that width and the spare
                    // width falls to the right of the last column.
                    template.Add(_choice.Widths.ContainsKey(SongColumn.Song) ? $"{layout.Width}px" : $"minmax({layout.Width}px, 1fr)");
                    minimum += layout.Width;
                    break;
                case var resized when _choice.Widths.ContainsKey(resized):
                    // A column the person resized keeps exactly that width, even when the table is too narrow for every column:
                    // the other columns give up width first, then the table scrolls sideways.
                    template.Add($"{layout.Width}px");
                    minimum += layout.Width;
                    break;
                default:
                    template.Add($"minmax({SongTableColumns.MinWidth(layout.Column)}px, {layout.Width}px)");
                    minimum += SongTableColumns.MinWidth(layout.Column);
                    break;
            }
        }

        // With Song at a fixed width the spare width goes to an empty filler track (and its empty cells), so the actions column stays at the
        // right edge and the header and row backgrounds still span the whole table.
        var filler = _choice.Widths.ContainsKey(SongColumn.Song);
        if (filler)
        {
            template.Add("minmax(0, 1fr)");
            minimum += ColumnGap;
        }

        template.Add($"{ActionsColumnWidth}px");
        _gridVariables = $"--song-table-columns: {string.Join(' ', template)}; --song-table-min-width: {minimum}px; --song-table-filler: {(filler ? "block" : "none")}";
    }

    private bool IsColumnVisible(SongColumn column) => _layout.Any(layout => layout.Column == column);

    private string ColumnLabel(SongColumn column) => column switch
    {
        SongColumn.Number => "#",
        SongColumn.Song => L["Common_Song"].Value,
        SongColumn.Artist => L["ListenPage_Artist"].Value,
        SongColumn.Album => L["ListenPage_Album"].Value,
        SongColumn.DateAdded => L["SongTable_DateAdded"].Value,
        SongColumn.Plays => L["SongTable_Plays"].Value,
        SongColumn.Genre => L["SongTable_Genre"].Value,
        SongColumn.Status => L["SongTable_Status"].Value,
        SongColumn.Favorite => L["Glossary_Favorite"].Value,
        SongColumn.Time => L["Common_Time"].Value,
        _ => string.Empty,
    };

    private string HeadCellClass(SongColumn column) => column switch
    {
        SongColumn.Favorite => "song-table__head-cell song-table__head-cell--favorite",
        SongColumn.Number => "song-table__head-cell song-table__head-cell--number",
        SongColumn.Time or SongColumn.Plays => "song-table__head-cell song-table__head-cell--end",
        _ => "song-table__head-cell",
    };

    private string AriaSort(SongColumn column) => _sort.Column == column ? (_sort.Descending ? "descending" : "ascending") : "none";

    private async Task ToggleSortAsync(SongColumn column)
    {
        var next = SongTableSort.Click(Place, _sort, column, OwnOrderSortable);
        if (next == _sort)
        {
            return;
        }

        _sort = next;
        RebuildShown();
        await OnSortChanged.InvokeAsync(next);
    }

    private string RowClass(SongRow row)
    {
        var key = RowKey(row);
        var classes = new List<string> { "song-table__row" };
        if (IsCurrent(row))
        {
            classes.Add("is-playing");
        }

        if (!row.IsOwned)
        {
            classes.Add("is-missing");
        }

        if (_selected.Contains(row.Id))
        {
            classes.Add("is-selected");
        }

        if (CanDrag)
        {
            classes.Add("is-reorderable");
        }

        if (_dragKey == key)
        {
            classes.Add("is-dragging");
        }

        return string.Join(' ', classes);
    }

    private string PhoneRowClass(SongRow row)
    {
        var classes = new List<string> { "song-table__prow" };
        if (IsCurrent(row))
        {
            classes.Add("is-playing");
        }

        if (SelectModeOn && _selected.Contains(row.Id))
        {
            classes.Add("is-selected");
        }

        if (!row.IsOwned)
        {
            classes.Add("is-missing");
        }

        return string.Join(' ', classes);
    }

    private bool IsCurrent(SongRow row) => CurrentRowId is not null && string.Equals(CurrentRowId, row.Id, StringComparison.Ordinal);

    private bool IsFavorite(SongRow row) => FavoriteIds?.Contains(row.Id) == true;

    // The # column: the track number on an album page, the place in the list everywhere else.
    private string PositionText(SongRow row) => Place == SongTablePlace.AlbumPage
        ? row.IsUnnumbered ? "–" : row.Number?.ToString(CultureInfo.InvariantCulture) ?? string.Empty
        : _positions.TryGetValue(RowKey(row), out var position) ? position.ToString(CultureInfo.InvariantCulture) : string.Empty;

    private static string Dash(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string DateAddedText(SongRow row)
        => row.DateAdded is { } added && added != default ? added.LocalDateTime.ToString("d", CultureInfo.CurrentCulture) : "—";

    private static string? StartUrl(SongRow row) => string.IsNullOrWhiteSpace(row.AssetId) ? null : $"/engine-stream/{row.AssetId}";

    private static string ArtistHref(Guid artistId) => $"/details/person/{artistId:D}";

    private static string AlbumHref(Guid albumId) => $"/details/musicalbum/{albumId:D}?context=listen";

    // The phone list's line under the title: no album name, so a row never wraps. An album's own page names an artist only when it differs.
    private string? PhoneSubtitle(SongRow row)
    {
        if (Place != SongTablePlace.AlbumPage)
        {
            return string.IsNullOrWhiteSpace(row.Artist) ? null : row.Artist;
        }

        if (!row.IsOwned)
        {
            return L["SongTable_Missing"].Value;
        }

        var albumArtist = string.IsNullOrWhiteSpace(AlbumArtist) ? MostCommonArtist() : AlbumArtist;
        return !string.IsNullOrWhiteSpace(row.Artist) && !string.Equals(row.Artist.Trim(), albumArtist?.Trim(), StringComparison.OrdinalIgnoreCase)
            ? row.Artist
            : null;
    }

    private string? MostCommonArtist() => Rows
        .Select(row => row.Artist)
        .Where(artist => !string.IsNullOrWhiteSpace(artist))
        .GroupBy(artist => artist!.Trim(), StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(group => group.Count())
        .Select(group => group.Key)
        .FirstOrDefault();

    // ---- Remembered columns ------------------------------------------------------------------------------------

    private async Task LoadChoiceAsync()
    {
        if (ProfileId is not { } profile || Services.GetService(typeof(ISongTablePreferenceStore)) is not ISongTablePreferenceStore store)
        {
            return;
        }

        var key = (profile, Place);
        if (_choiceKey == key)
        {
            return;
        }

        _choiceKey = key;
        var choice = await store.LoadAsync(profile, Place);
        if (_choiceKey != key)
        {
            return;
        }

        if (choice.IsDefault)
        {
            return;
        }

        _choice = choice;
        UpdateLayout();
        StateHasChanged();
    }

    private async Task SaveChoiceAsync()
    {
        if (ProfileId is { } profile && Services.GetService(typeof(ISongTablePreferenceStore)) is ISongTablePreferenceStore store)
        {
            await store.SaveAsync(profile, Place, _choice);
        }
    }

    private async Task SetColumnVisibleAsync(SongColumn column, bool visible)
    {
        _choice = SongTablePreferences.WithColumnVisible(_choice, Place, column, visible, _multipleArtists);
        UpdateLayout();
        await SaveChoiceAsync();
    }

    private async Task ResetColumnsAsync()
    {
        _choice = SongTableChoice.Default;
        UpdateLayout();
        await SaveChoiceAsync();
    }

    // ---- Resize ------------------------------------------------------------------------------------------------

    // Hands the table to the script once it is on the page. The script listens on the table itself and finds handles as they are pressed,
    // so showing or hiding columns needs no new wiring; a different table element (the phone list and back, an empty list and back) does.
    private async Task WireResizeAsync()
    {
        var wanted = !IsPhoneList && !ShowEmpty && !DeviceContext.IsTouch && _tableRoot.Id is not null;
        var rootId = wanted ? _tableRoot.Id : null;
        if (rootId == _wiredRootId)
        {
            return;
        }

        await UnwireResizeAsync();
        if (rootId is null)
        {
            return;
        }

        try
        {
            _resizeModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/song-table.js");
            _self ??= DotNetObjectReference.Create(this);
            await _resizeModule.InvokeVoidAsync("wireResize", _tableRoot, _self);
            _wiredRootId = rootId;
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException)
        {
            // Keyboard resizing still works without the drag, so the table stays usable.
            Logger.LogWarning(exception, "Column drag-resize could not start for the song table.");
        }
        catch (JSDisconnectedException exception)
        {
            Logger.LogDebug(exception, "The browser went away before the song table could start column drag-resize.");
        }
    }

    private async Task UnwireResizeAsync()
    {
        if (_wiredRootId is null || _resizeModule is null)
        {
            _wiredRootId = null;
            return;
        }

        _wiredRootId = null;
        await ReleaseAsync(() => _resizeModule.InvokeVoidAsync("unwireResize", _tableRoot).AsTask());
    }

    private async Task ReleaseAsync(Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (JSDisconnectedException exception)
        {
            Logger.LogDebug(exception, "The browser went away while the song table was letting go of column drag-resize.");
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException or ObjectDisposedException)
        {
            // The table element may already be gone from the page; there is nothing left to let go of.
            Logger.LogDebug(exception, "Song table column drag-resize clean-up failed.");
        }
    }

    /// <summary>
    /// A drag (or a Left or Right press) on a column's handle ended in the browser: the final width, already kept between the column's
    /// limits there. Returns the grid variables now drawn, which the browser writes back so the page always matches this component.
    /// </summary>
    [JSInvokable]
    public async Task<string> ColumnResized(string column, int width)
    {
        if (SongTableColumns.TryParseKey(column, out var parsed) && SongTableColumns.IsResizable(parsed))
        {
            _choice = SongTablePreferences.WithWidth(_choice, parsed, width);
            UpdateLayout();
            StateHasChanged();
            await TrySaveChoiceAsync();
        }

        return _gridVariables;
    }

    /// <summary>Double-click on a handle: that one column goes back to its default width (the Song column back to flexible).</summary>
    [JSInvokable]
    public async Task<string> ColumnResetWidth(string column)
    {
        if (SongTableColumns.TryParseKey(column, out var parsed) && SongTableColumns.IsResizable(parsed))
        {
            _choice = SongTablePreferences.WithDefaultWidth(_choice, parsed);
            UpdateLayout();
            StateHasChanged();
            await TrySaveChoiceAsync();
        }

        return _gridVariables;
    }

    // A column width that could not be saved to the profile still stays on screen for this visit; the failure is logged, never swallowed.
    private async Task TrySaveChoiceAsync()
    {
        try
        {
            await SaveChoiceAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogWarning(exception, "Could not save the song table's column widths for this profile; the widths stay for this visit.");
        }
    }

    // ---- Selection and notices -----------------------------------------------------------------------------------

    // "Select" in the phone sheet: this song is ticked and the list goes into select mode.
    private async Task BeginSelectAsync(SongRow row)
    {
        if (!SelectModeAvailable || !row.IsOwned)
        {
            return;
        }

        _selectMode = true;
        await SetSelectedAsync(row.Id, true);
    }

    // Done: the ticks are dropped and the bar goes away.
    private async Task EndSelectAsync()
    {
        _selectMode = false;
        if (_selected.Count > 0)
        {
            _selected.Clear();
            await SelectedIdsChanged.InvokeAsync(new HashSet<string>(StringComparer.Ordinal));
        }
    }

    // A tap on a phone row plays it, or ticks it while the list is in select mode.
    private Task ActivateRowAsync(SongRow row)
    {
        if (!SelectModeOn)
        {
            return PlayAsync(row);
        }

        return row.IsOwned ? SetSelectedAsync(row.Id, !_selected.Contains(row.Id)) : Task.CompletedTask;
    }

    // The ticked songs that can play, in the order the list shows them.
    private IReadOnlyList<SongRow> TickedPlayable => [.. SelectedRows.Where(row => row.IsOwned)];

    private IReadOnlyList<Guid> TickedWorkIds => [.. TickedPlayable.Select(row => row.WorkId).OfType<Guid>().Where(id => id != Guid.Empty)];

    private async Task PlayTickedAsync()
    {
        var rows = TickedPlayable;
        if (rows.Count > 0)
        {
            await OnPlay.InvokeAsync(new SongPlayRequest(rows, rows[0]));
        }
    }

    private async Task QueueTickedAsync()
    {
        foreach (var row in TickedPlayable)
        {
            await OnQueue.InvokeAsync(row);
        }
    }

    private async Task SetSelectedAsync(string id, bool selected)
    {
        if (selected)
        {
            _selected.Add(id);
        }
        else
        {
            _selected.Remove(id);
        }

        await SelectedIdsChanged.InvokeAsync(_selected.ToHashSet(StringComparer.Ordinal));
    }

    private Task ShowNotice(string message)
    {
        _notice = message;
        _noticeSeverity = AppSeverity.Warning;
        return Task.CompletedTask;
    }

    private Task ShowUndo(UndoOffer offer)
    {
        _undo = offer;
        return Task.CompletedTask;
    }

    private async Task DismissNoticeAsync()
    {
        _notice = null;
        _hostNotice = null;
        await OnNoticeDismissed.InvokeAsync();
    }

    private async Task ClearUndoAsync()
    {
        _undo = null;
        _hostUndo = null;
        await OnUndoCleared.InvokeAsync();
    }

    // ---- Actions -------------------------------------------------------------------------------------------------

    private async Task PlayAsync(SongRow row)
    {
        if (!row.IsOwned)
        {
            return;
        }

        await OnPlay.InvokeAsync(new SongPlayRequest([.. _shown.Where(candidate => candidate.IsOwned)], row));
    }

    private Task PlayNextAsync(SongRow row) => OnPlayNext.InvokeAsync(row);

    private Task QueueAsync(SongRow row) => OnQueue.InvokeAsync(row);

    private Task ToggleFavoriteAsync(SongRow row) => row.IsOwned ? OnToggleFavorite.InvokeAsync(row) : Task.CompletedTask;

    private async Task OpenAlbumAsync(SongRow row)
    {
        if (row.CollectionId is not { } albumId || albumId == Guid.Empty)
        {
            return;
        }

        if (OnOpenAlbum.HasDelegate)
        {
            await OnOpenAlbum.InvokeAsync(row);
        }
        else
        {
            Navigation.NavigateTo(AlbumHref(albumId));
        }
    }

    private async Task OpenArtistAsync(SongRow row)
    {
        if (row.ArtistPersonId is not { } artistId || artistId == Guid.Empty)
        {
            return;
        }

        if (OnOpenArtist.HasDelegate)
        {
            await OnOpenArtist.InvokeAsync(row);
        }
        else
        {
            Navigation.NavigateTo(ArtistHref(artistId));
        }
    }

    // The menu items only show to a desktop that may edit; a handler never trusts that.
    private bool EditAllowed(SongRow row) => CanEdit && row.IsOwned && row.WorkId.HasValue && !DeviceContext.IsPhone && DeviceContext.CanEdit;

    private bool DeleteAllowed(SongRow row) => CanDelete && row.IsOwned && row.WorkId.HasValue && !DeviceContext.IsPhone;

    private bool RemoveAllowed(SongRow row) => Place == SongTablePlace.Playlist && row.PlaylistItemId.HasValue && OnRemoveFromPlaylist.HasDelegate;

    private async Task EditAsync(SongRow row)
    {
        if (!EditAllowed(row))
        {
            if (!DeviceContext.CanEdit)
            {
                _notice = EditorDeviceRule.Message;
            }

            return;
        }

        if (!row.IsUpdatingDetails)
        {
            await OnEdit.InvokeAsync(row);
        }
    }

    private async Task DeleteAsync(SongRow row)
    {
        if (DeleteAllowed(row))
        {
            await OnDelete.InvokeAsync(row);
        }
    }

    private Task RemoveAsync(SongRow row) => RemoveAllowed(row) ? OnRemoveFromPlaylist.InvokeAsync(row) : Task.CompletedTask;

    private async Task RowKeyDownAsync(KeyboardEventArgs args, SongRow row)
    {
        if (args.AltKey && args.Key is "ArrowUp" or "ArrowDown")
        {
            if (ReorderEnabled)
            {
                // The moved row is drawn again in its new place; keyboard focus follows it so the next Alt+Arrow moves the same song.
                _focusRowKey = RowKey(row);
                await MoveByAsync(row, args.Key == "ArrowUp" ? -1 : 1);
            }

            return;
        }

        if (args.Key is "Enter" or " ")
        {
            await ActivateRowAsync(row);
        }
    }

    private async Task FocusMovedRowAsync()
    {
        if (_focusRowKey is not { } key)
        {
            return;
        }

        _focusRowKey = null;
        try
        {
            await JS.InvokeVoidAsync("tuvimaFocusById", $"song-table-row-{key}");
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException)
        {
            Logger.LogWarning(exception, "Could not return focus to the song row that was moved.");
        }
        catch (JSDisconnectedException exception)
        {
            Logger.LogDebug(exception, "The browser went away before focus could return to the song row that was moved.");
        }
    }

    // ---- Phone: press and hold ---------------------------------------------------------------------------------------

    // A long press raises the browser's context-menu event. It opens the same bottom sheet as the "..." button by pressing that button
    // (the sheet belongs to the shared menu component), so there is one sheet and one item list.
    private async Task OpenRowMenuAsync(SongRow row)
    {
        if (!row.IsOwned)
        {
            return;
        }

        try
        {
            await using var module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/song-table.js");
            await module.InvokeVoidAsync("openRowMenu", RowKey(row));
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException)
        {
            Logger.LogWarning(exception, "Could not open the actions for a song row.");
        }
        catch (JSDisconnectedException exception)
        {
            Logger.LogDebug(exception, "The browser went away before the actions for a song row could open.");
        }
    }

    // ---- Playlist order ----------------------------------------------------------------------------------------------

    private async Task DragStartAsync(SongRow row)
    {
        if (CanDrag)
        {
            _dragKey = RowKey(row);
            _dropBeforeKey = null;
            _dropAtEnd = false;
        }

        if (CanDragOut && row.IsOwned)
        {
            // A ticked song carries every ticked song, in the order the list shows them; an unticked song carries just itself.
            IReadOnlyList<SongRow> carried = _selected.Contains(row.Id) ? TickedPlayable : [row];
            _draggingOut = carried;
            _dropService?.Begin(this, [.. carried.Select(song => song.WorkId).OfType<Guid>()]);
            await OnDragOutStarted.InvokeAsync(carried);
        }
    }

    private void DragOver(SongRow row)
    {
        if (_dragKey is null)
        {
            return;
        }

        var key = RowKey(row);
        _dropBeforeKey = key == _dragKey ? null : key;
        _dropAtEnd = false;
    }

    private void DragOverEnd()
    {
        if (_dragKey is null)
        {
            return;
        }

        _dropBeforeKey = null;
        _dropAtEnd = true;
    }

    private void DragEnd()
    {
        _dragKey = null;
        _dropBeforeKey = null;
        _dropAtEnd = false;
    }

    private async Task DragEndAsync()
    {
        DragEnd();
        if (_draggingOut is not null)
        {
            _draggingOut = null;
            _dropService?.End(this);
            await OnDragOutEnded.InvokeAsync();
        }
    }

    /// <summary>The browser script reports that a drag has ended, for the case where the row's own dragend never arrived (a recycled row).</summary>
    [JSInvokable]
    public Task DragOutFinished() => InvokeAsync(DragEndAsync);

    private async Task DropAsync(SongRow target)
    {
        var moved = _dragKey;
        var targetKey = RowKey(target);
        DragEnd();
        if (moved is null || moved == targetKey)
        {
            return;
        }

        var from = _shown.FindIndex(candidate => RowKey(candidate) == moved);
        var before = _shown.FindIndex(candidate => RowKey(candidate) == targetKey);
        if (from < 0 || before < 0)
        {
            return;
        }

        // Dropping before a later row leaves a gap where the dragged row was, so it lands one place earlier than the row it was dropped on.
        await MoveAsync(_shown[from], from, before > from ? before - 1 : before);
    }

    private async Task DropAtEndAsync()
    {
        var moved = _dragKey;
        DragEnd();
        if (moved is null)
        {
            return;
        }

        var from = _shown.FindIndex(candidate => RowKey(candidate) == moved);
        if (from >= 0)
        {
            await MoveAsync(_shown[from], from, _shown.Count - 1);
        }
    }

    private async Task MoveByAsync(SongRow row, int delta)
    {
        var from = RowIndex(row);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= _shown.Count)
        {
            return;
        }

        await MoveAsync(row, from, to);
    }

    private async Task MoveAsync(SongRow row, int from, int to)
    {
        if (from == to || !ReorderEnabled)
        {
            return;
        }

        _announcement = L["SongTable_MovedTo", row.Title, to + 1, _shown.Count].Value;
        await OnReorder.InvokeAsync(new SongReorderRequest(row, from, to));
    }
}
