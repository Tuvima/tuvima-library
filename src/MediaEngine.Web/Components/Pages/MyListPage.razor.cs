using System.Globalization;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.ProfileState;
using MediaEngine.Domain.Enums;
using MediaEngine.Web.Components.Browse;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Navigation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.Pages;

/// <summary>
/// My List (spec my-list): the header and Continue carried over from My Library, then the saved titles as a paged cover grid read
/// from the Engine's <c>personal/my-list</c> grid. This part holds the page's loading, kinds and sort; removing, Select and Undo are in
/// <c>MyListPage.Removal.cs</c>.
/// </summary>
public partial class MyListPage
{
    private const string Lane = "personal";
    private const string ShelfKey = "my-list";
    private const int SkeletonTileCount = 12;
    private const int ContinueLimit = 24;

    private static readonly string[] SortValues = [DisplayGridValues.SortAdded, DisplayGridValues.SortTitle, DisplayGridValues.SortYear];

    /// <summary>What Downloads does on this client; the card shows only where downloads work (the Tuvima app), never in the browser.</summary>
    [Parameter] public DownloadsTabMode DownloadsMode { get; set; } = ShellDock.DownloadsMode;

    // The address this page was routed with (see RoutedPageQuery): the page under the details overlay keeps its own query.
    [CascadingParameter(Name = RoutedPageQuery.CascadingName)] public string? RoutedUri { get; set; }

    private RoutedPageQuery Routed => RoutedPageQuery.From(RoutedUri ?? Nav.Uri);
    public string? SortQuery => Clean(Routed.String("sort"));
    public string? TypeQuery => Clean(Routed.String(DisplayGridValues.FilterType));

    // -- header and Continue ------------------------------------------------------------------------------------------

    private ProfileViewModel? _profile;
    private int _profileCount;
    private bool _headerFailed;
    private bool _continueLoading = true;
    private MediaTileShelfViewModel _continueShelf = new() { Key = "continue", Title = string.Empty };
    private int _chromeVersion;

    // -- the list -----------------------------------------------------------------------------------------------------

    private readonly List<MediaTileViewModel> _tiles = [];
    private readonly HashSet<Guid> _tileIds = [];
    private DisplayGridPageDto? _page;
    private string? _loadedKey;
    private bool _started;
    private int _version;
    private int _nextOffset;
    private bool _loading = true;
    private bool _loadingMore;
    private bool _failed;
    private bool _moreFailed;
    private bool _hasMore;
    private bool _observeRequested;
    private bool _savingSort;
    private string? _sortNote;
    private int? _finishedCount;
    private CancellationTokenSource? _cts;

    private ElementReference _sentinel;
    private GridLoadMoreObserver<MyListPage>? _observer;

    private string ProfileName => string.IsNullOrWhiteSpace(_profile?.DisplayName) ? "Profile" : _profile!.DisplayName.Trim();
    private bool IsKids => _profile?.IsRestricted == true;
    private bool ShowsSettings => ShellNavigation.ShowsSettings(IsKids);
    private bool ShowsSwitchProfile => ShellNavigation.ShowsSwitchProfile(_profileCount);
    private bool ShowsDownloads => ShellDock.ShowsDownloadsInTopBar(DownloadsMode);

    // A Kids profile has no Settings, so its Activity page is the one outside Settings.
    private string ActivityHref => IsKids ? SettingsNav.MyActivityRoute : SettingsNav.ActivityRoute;
    private const string LikedHref = "/all/personal/liked";

    // Nothing is saved at all (not just nothing of the picked kind).
    private bool IsEmpty => _page is { TotalCount: 0 } && TypeQuery is null && !_loading && !_failed;

    // Sort, Select and the page menu (and the count) exist only while there is a list to work with.
    private bool ShowsListControls => _page is not null && !_failed && !IsEmpty;

    protected override void OnInitialized()
    {
        Orchestrator.OnProfileChanged += OnProfileChanged;
        Orchestrator.OnLibraryItemsRemoved += OnLibraryItemsRemoved;
        Saved.Changed += OnSavedChanged;
        Device.OnChanged += OnDeviceChanged;
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!_started)
        {
            _started = true;
            _ = LoadChromeAsync();
        }

        var key = string.Join('\u001f', SortQuery, TypeQuery);
        if (key == _loadedKey)
        {
            return;
        }

        _loadedKey = key;
        await LoadFirstPageAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_observeRequested)
        {
            return;
        }

        _observeRequested = false;
        await (_observer ??= new GridLoadMoreObserver<MyListPage>(JS, Logger, this)).SyncAsync(_sentinel, _hasMore && !_loading);
    }

    // -- header and Continue (from My Library) --------------------------------------------------------------------------

    private async Task LoadChromeAsync()
    {
        var version = ++_chromeVersion;
        _profile = null;
        _headerFailed = false;
        _continueLoading = true;
        _continueShelf = EmptyContinue();
        await InvokeAsync(StateHasChanged);

        // The saved list is read now, so a title taken off by a cover's own menu can later be told from the ones that stayed.
        var saved = ReadSavedAsync();
        await Task.WhenAll(LoadHeaderAsync(version), LoadContinueAsync(version));
        _knownSaved = await saved;
    }

    private async Task LoadHeaderAsync(int version)
    {
        try
        {
            var profile = await Orchestrator.GetActiveProfileAsync();
            var profiles = await Orchestrator.GetProfilesAsync();
            if (version != _chromeVersion)
            {
                return;
            }

            _profile = profile;
            _profileCount = profiles.Count;
            _headerFailed = profile is null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load the My List header.");
            if (version == _chromeVersion)
            {
                _profile = null;
                _headerFailed = true;
            }
        }
        finally
        {
            if (version == _chromeVersion)
            {
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private MediaTileShelfViewModel EmptyContinue() => new() { Key = "continue", Title = L["Common_Continue"].Value };

    private async Task LoadContinueAsync(int version)
    {
        var shelf = EmptyContinue();
        try
        {
            var documentsAllowed = LoadDocumentsAllowedAsync();
            var page = await ApiClient.GetDisplayContinueAsync(limit: ContinueLimit);
            var started = PhoneContinueLogic.Select(PersonalTileIndex.FlattenCards(page).Select(MediaTileComposerService.FromDisplayCard));
            shelf = new()
            {
                Key = "continue",
                Title = L["Common_Continue"].Value,
                Items = PersonalTileIndex.Visible(started, await documentsAllowed).Take(ContinueLimit).ToList(),
            };
        }
        catch (Exception ex)
        {
            // The list below still works; Continue simply stays out of sight until the next load.
            Logger.LogWarning(ex, "Failed to load Continue on My List.");
        }
        finally
        {
            if (version == _chromeVersion)
            {
                _continueShelf = shelf;
                _continueLoading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    // Documents are never part of the lane reads, but a profile that may not see them must not meet one in Continue either.
    // Until the View settings say Documents are allowed they stay hidden.
    private async Task<bool> LoadDocumentsAllowedAsync()
    {
        try
        {
            await Workspace.InitializeAsync();
            return Workspace.Scopes?.CanViewDocuments == true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not read whether this profile may see Documents; Document items stay hidden.");
            return false;
        }
    }

    private Task ReloadAllAsync()
    {
        _ = LoadChromeAsync();
        return LoadFirstPageAsync();
    }

    // -- reading the grid -----------------------------------------------------------------------------------------------

    private Task ReloadAsync() => LoadFirstPageAsync();

    private Task<DisplayGridLoadResult> ReadAsync(int offset, int limit, CancellationToken ct) =>
        ApiClient.GetDisplayGridAsync(Lane, ShelfKey, SortQuery, TypeQuery, null, null, null, offset, limit, ct);

    /// <summary>
    /// Reads the first page. A silent read (after a removal or Undo) keeps the covers on screen until the new ones arrive and asks for as many
    /// as are already shown (up to the page limit), so the list does not jump to the top.
    /// </summary>
    private async Task LoadFirstPageAsync(bool silent = false)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();
        var version = ++_version;
        var limit = silent
            ? Math.Clamp(_tiles.Count, DisplayGridValues.DefaultPageSize, DisplayGridValues.MaxPageSize)
            : DisplayGridValues.DefaultPageSize;

        if (!silent)
        {
            _tiles.Clear();
            _tileIds.Clear();
            _loading = true;
        }

        _loadingMore = false;
        _failed = false;
        _moreFailed = false;
        if (!silent)
        {
            _hasMore = false;
        }

        var result = await ReadAsync(0, limit, cts.Token);
        if (version != _version)
        {
            return;
        }

        if (result.Page is not { } page)
        {
            if (result.Failure == DisplayGridLoadFailure.BadRequest && (SortQuery is not null || TypeQuery is not null))
            {
                // A bookmarked address with a sort or kind the Engine no longer accepts: fall back to the defaults once.
                // The new address carries no choices, so a second refusal lands in the normal error state instead of looping.
                Nav.NavigateTo(
                    Nav.GetUriWithQueryParameters(new Dictionary<string, object?> { ["sort"] = null, [DisplayGridValues.FilterType] = null }),
                    replace: true);
                return;
            }

            _loading = false;
            _failed = true;
            return;
        }

        _page = page;
        if (silent)
        {
            _tiles.Clear();
            _tileIds.Clear();
        }

        if (TypeQuery is not null && page.TotalCount == 0)
        {
            // The picked kind has nothing left: back to All. The address change starts the reload.
            _loading = true;
            Nav.NavigateTo(Nav.GetUriWithQueryParameter(DisplayGridValues.FilterType, (string?)null), replace: true);
            return;
        }

        _loading = false;
        Append(page);
        PruneSelection();
        _observeRequested = true;
        if (page.TotalCount > 0)
        {
            _ = RefreshFinishedAsync(version);
        }
    }

    private async Task LoadMoreAsync()
    {
        if (_loading || _loadingMore || !_hasMore || _page is null)
        {
            return;
        }

        var version = _version;
        var token = _cts?.Token ?? CancellationToken.None;
        _loadingMore = true;
        _moreFailed = false;
        StateHasChanged();

        var result = await ReadAsync(_nextOffset, DisplayGridValues.DefaultPageSize, token);
        if (version != _version)
        {
            // The list was reloaded while this page was on its way; only the reload may change the state.
            return;
        }

        _loadingMore = false;
        if (result.Page is not { } page)
        {
            _moreFailed = true;
        }
        else
        {
            _page = _page with { TotalCount = page.TotalCount };
            Append(page);
            _observeRequested = true;
        }

        StateHasChanged();
    }

    [JSInvokable]
    public Task LoadMoreFromObserverAsync() => InvokeAsync(async () =>
    {
        // After a failure only the person's own "Try again" asks again, so a sentinel that stays on screen cannot loop.
        if (_moreFailed)
        {
            return;
        }

        await LoadMoreAsync();
    });

    private void Append(DisplayGridPageDto page)
    {
        foreach (var card in page.Items)
        {
            if (_tileIds.Add(card.Id))
            {
                _tiles.Add(MediaTileComposerService.FromDisplayCard(card, MediaTileContext.Default));
            }
        }

        _nextOffset = page.Offset + page.Items.Count;
        // A page that added nothing must not keep the button (and the observer) asking for the same offset.
        _hasMore = page.HasMore && page.Items.Count > 0;
    }

    // What "Remove finished" would take; read after each list read so the page menu already says "Nothing finished yet" when it is true.
    private async Task RefreshFinishedAsync(int version)
    {
        try
        {
            var finished = await ApiClient.GetFinishedSavedItemsAsync();
            if (version == _version)
            {
                _finishedCount = finished.Count;
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Could not read which titles on My List are finished.");
        }
    }

    // -- kinds (chips) ----------------------------------------------------------------------------------------------------

    private DisplayGridFilterGroupDto? TypeGroup =>
        _page?.Filters.FirstOrDefault(group => string.Equals(group.Key, DisplayGridValues.FilterType, StringComparison.Ordinal));

    /// <summary>The kinds on the list, in the Engine's order; the picked one stays even when its count is gone.</summary>
    private IReadOnlyList<DisplayGridFilterOptionDto> KindOptions =>
        TypeGroup?.Options.Where(option => option.Count > 0 || option.Selected).ToList() ?? [];

    // With one kind saved there is nothing to narrow, so the row is hidden (D4).
    private bool ShowChips => KindOptions.Count >= 2;

    private string? SelectedType => TypeQuery;

    private bool IsKindPicked(string value) => string.Equals(SelectedType, value, StringComparison.OrdinalIgnoreCase);

    // The kinds' counts ignore the picked kind, so together they are the whole list.
    private int AllCount => TypeGroup is { } group ? group.Options.Sum(option => option.Count) : _page?.TotalCount ?? 0;

    private Task PickKindAsync(string? type)
    {
        if (string.Equals(type, SelectedType, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        Nav.NavigateTo(Nav.GetUriWithQueryParameter(DisplayGridValues.FilterType, type), replace: true);
        return Task.CompletedTask;
    }

    private string CountText
    {
        get
        {
            if (_page is null || _loading)
            {
                return string.Empty;
            }

            var count = _page.TotalCount;
            var number = count.ToString("N0", CultureInfo.CurrentCulture);
            return SelectedType is null
                ? (count == 1 ? L["MyList_CountTitle", number] : L["MyList_CountTitles", number]).Value
                : KindCount(SelectedType.ToLowerInvariant(), count, number);
        }
    }

    // "9 movies", "4 TV shows", "1 album": the picked kind named in lower case, singular for one.
    private string KindCount(string type, int count, string number) => (count == 1
        ? type switch
        {
            DisplayGridValues.TypeMovie => L["MyList_Count_movie_one", number],
            DisplayGridValues.TypeTv => L["MyList_Count_tv_one", number],
            DisplayGridValues.TypeVideo => L["MyList_Count_video_one", number],
            DisplayGridValues.TypeBook => L["MyList_Count_book_one", number],
            DisplayGridValues.TypeComic => L["MyList_Count_comic_one", number],
            DisplayGridValues.TypeAudiobook => L["MyList_Count_audiobook_one", number],
            DisplayGridValues.TypeMusic => L["MyList_Count_music_one", number],
            DisplayGridValues.TypePlaylist => L["MyList_Count_playlist_one", number],
            _ => L["MyList_Count_collection_one", number],
        }
        : type switch
        {
            DisplayGridValues.TypeMovie => L["MyList_Count_movie", number],
            DisplayGridValues.TypeTv => L["MyList_Count_tv", number],
            DisplayGridValues.TypeVideo => L["MyList_Count_video", number],
            DisplayGridValues.TypeBook => L["MyList_Count_book", number],
            DisplayGridValues.TypeComic => L["MyList_Count_comic", number],
            DisplayGridValues.TypeAudiobook => L["MyList_Count_audiobook", number],
            DisplayGridValues.TypeMusic => L["MyList_Count_music", number],
            DisplayGridValues.TypePlaylist => L["MyList_Count_playlist", number],
            _ => L["MyList_Count_collection", number],
        }).Value;

    // -- sort ---------------------------------------------------------------------------------------------------------------

    private IReadOnlyList<AppSelectOption> SortChoices =>
    [
        new(DisplayGridValues.SortAdded, L["MyList_SortAdded"].Value),
        new(DisplayGridValues.SortTitle, L["MyList_SortTitle"].Value),
        new(DisplayGridValues.SortYear, L["MyList_SortYear"].Value),
    ];

    private string CurrentSort =>
        SortQuery is { } query && SortValues.Contains(query) ? query
        : _page?.Sort is { } remembered && SortValues.Contains(remembered) ? remembered
        : DisplayGridValues.SortAdded;

    private async Task OnSortChangedAsync(string? sort)
    {
        // A second choice while the first is still being saved is ignored; the page settles on the first.
        if (_savingSort || string.IsNullOrWhiteSpace(sort) || string.Equals(sort, CurrentSort, StringComparison.Ordinal))
        {
            return;
        }

        _savingSort = true;
        _sortNote = null;
        try
        {
            if (!await ApiClient.SetDisplayGridSortAsync(_page?.RememberKey ?? DisplayGridValues.RememberMyList, sort))
            {
                // The new sort still applies for this visit; only next time is affected.
                _sortNote = L["ShelfGridPage_SortNotRemembered"].Value;
            }

            Nav.NavigateTo(Nav.GetUriWithQueryParameter("sort", sort), replace: true);
        }
        finally
        {
            _savingSort = false;
        }
    }

    // -- events -------------------------------------------------------------------------------------------------------------------

    private void OnProfileChanged() => _ = InvokeAsync(ReloadAllAsync);

    // Continue and the list drop titles an administrator deleted from the library.
    private void OnLibraryItemsRemoved(MediaEngine.Contracts.Realtime.LibraryItemsRemovedEvent ev) =>
        _ = InvokeAsync(async () =>
        {
            _ = LoadContinueAsync(_chromeVersion);
            await LoadFirstPageAsync(silent: true);
        });

    private void OnDeviceChanged() => _ = InvokeAsync(StateHasChanged);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public async ValueTask DisposeAsync()
    {
        Orchestrator.OnProfileChanged -= OnProfileChanged;
        Orchestrator.OnLibraryItemsRemoved -= OnLibraryItemsRemoved;
        Saved.Changed -= OnSavedChanged;
        Device.OnChanged -= OnDeviceChanged;
        _chromeVersion++;
        _version++;
        _cts?.Cancel();
        _cts?.Dispose();
        if (_observer is not null)
        {
            await _observer.DisposeAsync();
        }
    }
}
