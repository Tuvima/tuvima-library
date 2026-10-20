using System.Globalization;
using MediaEngine.Contracts.Display;
using MediaEngine.Web.Components.Browse;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.Pages;

public partial class ShelfGridPage
{
    private const int SkeletonTileCount = 12;
    private const string AnyValue = "__any";
    private const string ObserverModule = "./js/load-more-observer.js";

    private static readonly string[] FilterKeys =
    [
        DisplayGridValues.FilterType, DisplayGridValues.FilterGenre, DisplayGridValues.FilterFormat, DisplayGridValues.FilterStatus,
    ];

    [Parameter] public string Lane { get; set; } = string.Empty;
    [Parameter] public string ShelfKey { get; set; } = string.Empty;

    [SupplyParameterFromQuery(Name = "sort")] public string? SortQuery { get; set; }
    [SupplyParameterFromQuery(Name = "type")] public string? TypeQuery { get; set; }
    [SupplyParameterFromQuery(Name = "genre")] public string? GenreQuery { get; set; }
    [SupplyParameterFromQuery(Name = "format")] public string? FormatQuery { get; set; }
    [SupplyParameterFromQuery(Name = "status")] public string? StatusQuery { get; set; }

    private readonly List<MediaTileViewModel> _tiles = [];
    private readonly HashSet<Guid> _tileIds = [];
    private DisplayGridPageDto? _page;
    private string? _loadedKey;
    private string? _loadedGrid;
    private int _version;
    private int _nextOffset;
    private bool _loading = true;
    private bool _loadingMore;
    private bool _failed;
    private bool _notFound;
    private bool _moreFailed;
    private bool _hasMore;
    private bool _observeRequested;
    private bool _savingSort;
    private CancellationTokenSource? _cts;

    private ElementReference _sentinel;
    private IJSObjectReference? _module;
    private DotNetObjectReference<ShelfGridPage>? _self;

    private string PageTitleText => string.IsNullOrWhiteSpace(_page?.Title) ? "View all" : _page!.Title;

    private string CountText => _page is null || _loading || _failed ? string.Empty : CountLabel(_page.TotalCount);

    private string CurrentSort => !string.IsNullOrWhiteSpace(SortQuery) ? SortQuery : _page?.Sort ?? string.Empty;

    private IReadOnlyList<AppSelectOption> SortChoices =>
        (_page?.SortOptions ?? []).Select(option => new AppSelectOption(option.Value, option.Label)).ToList();

    private IReadOnlyList<ActiveFilterChip> ActiveFilters
    {
        get
        {
            var chips = new List<ActiveFilterChip>();
            foreach (var group in _page?.Filters ?? [])
            {
                if (QueryValue(group.Key) is { } value)
                {
                    var option = group.Options.FirstOrDefault(candidate => string.Equals(candidate.Value, value, StringComparison.OrdinalIgnoreCase));
                    chips.Add(new ActiveFilterChip(group.Key, option?.Label ?? value));
                }
            }

            // A filter in the address that the Engine did not return a group for can still be removed.
            foreach (var key in FilterKeys)
            {
                if (QueryValue(key) is { } value && chips.All(chip => chip.Key != key))
                {
                    chips.Add(new ActiveFilterChip(key, value));
                }
            }

            return chips;
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        var key = string.Join('\u001f', Lane, ShelfKey, Clean(SortQuery), Clean(TypeQuery), Clean(GenreQuery), Clean(FormatQuery), Clean(StatusQuery));
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
        await SyncObserverAsync();
    }

    private Task ReloadAsync() => LoadFirstPageAsync();

    private async Task LoadFirstPageAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();
        var version = ++_version;
        var grid = $"{Lane}/{ShelfKey}";
        if (grid != _loadedGrid)
        {
            // A different list: nothing of the old one (title, choices) may show while this one loads.
            _page = null;
            _loadedGrid = grid;
        }

        _tiles.Clear();
        _tileIds.Clear();
        _loading = true;
        _loadingMore = false;
        _failed = false;
        _notFound = false;
        _moreFailed = false;
        _hasMore = false;

        var result = await ApiClient.GetDisplayGridAsync(
            Lane, ShelfKey, Clean(SortQuery), Clean(TypeQuery), Clean(GenreQuery), Clean(FormatQuery), Clean(StatusQuery),
            0, DisplayGridValues.DefaultPageSize, cts.Token);
        if (version != _version)
        {
            return;
        }

        if (result.Page is not { } page)
        {
            if (result.Failure == DisplayGridLoadFailure.BadRequest && HasChoicesInAddress)
            {
                // A bookmarked address with a sort or filter the Engine no longer accepts: fall back to the defaults once.
                // The new address carries no choices, so a second refusal lands in the normal error state instead of looping.
                // _loading stays on; the address change starts the reload.
                Nav.NavigateTo(
                    Nav.GetUriWithQueryParameters(new Dictionary<string, object?>
                    {
                        ["sort"] = null,
                        [DisplayGridValues.FilterType] = null,
                        [DisplayGridValues.FilterGenre] = null,
                        [DisplayGridValues.FilterFormat] = null,
                        [DisplayGridValues.FilterStatus] = null,
                    }),
                    replace: true);
                return;
            }

            _loading = false;
            _notFound = result.Failure == DisplayGridLoadFailure.NotFound;
            _failed = !_notFound;
            return;
        }

        _loading = false;
        _page = page;
        Append(page);
        _observeRequested = true;
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

        var result = await ApiClient.GetDisplayGridAsync(
            Lane, ShelfKey, Clean(SortQuery), Clean(TypeQuery), Clean(GenreQuery), Clean(FormatQuery), Clean(StatusQuery),
            _nextOffset, DisplayGridValues.DefaultPageSize, token);
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
        var context = IsContinueGrid(page) ? MediaTileContext.ContinueWatching : MediaTileContext.Default;
        foreach (var card in page.Items)
        {
            if (_tileIds.Add(card.Id))
            {
                _tiles.Add(MediaTileComposerService.FromDisplayCard(card, context));
            }
        }

        _nextOffset = page.Offset + page.Items.Count;
        // A page that added nothing must not keep the button (and the observer) asking for the same offset.
        _hasMore = page.HasMore && page.Items.Count > 0;
    }

    private bool IsContinueGrid(DisplayGridPageDto page) =>
        ShelfKey.StartsWith("continue", StringComparison.OrdinalIgnoreCase)
        || string.Equals(page.DefaultSort, DisplayGridValues.SortPlayed, StringComparison.OrdinalIgnoreCase);

    private async Task SyncObserverAsync()
    {
        try
        {
            if (_hasMore && !_loading)
            {
                _module ??= await JS.InvokeAsync<IJSObjectReference>("import", ObserverModule);
                _self ??= DotNetObjectReference.Create(this);
                await _module.InvokeVoidAsync("observe", _sentinel, _self, new { rootMargin = "800px 0px" });
            }
            else if (_module is not null)
            {
                await _module.InvokeVoidAsync("disconnect", _sentinel);
            }
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; nothing is left to observe.
        }
        catch (JSException ex)
        {
            // Load more stays under the grid, so the list still works without the scroll trigger.
            Logger.LogWarning(ex, "The scroll trigger for {Lane}/{ShelfKey} could not start.", Lane, ShelfKey);
        }
    }

    private async Task OnSortChangedAsync(string? sort)
    {
        // A second choice while the first is still being saved is ignored; the page settles on the first.
        if (_savingSort || string.IsNullOrWhiteSpace(sort) || string.Equals(sort, CurrentSort, StringComparison.Ordinal))
        {
            return;
        }

        _savingSort = true;
        try
        {
            if (_page?.RememberKey is { } rememberKey && !await ApiClient.SetDisplayGridSortAsync(rememberKey, sort))
            {
                // The new sort still applies for this visit; only next time is affected.
                Toasts.Add("Your sort could not be remembered. It still applies for now.", AppSeverity.Warning);
            }

            Nav.NavigateTo(Nav.GetUriWithQueryParameter("sort", sort), replace: true);
        }
        finally
        {
            _savingSort = false;
        }
    }

    private Task OnFilterChangedAsync(string key, string? value)
    {
        Nav.NavigateTo(
            Nav.GetUriWithQueryParameter(key, string.IsNullOrWhiteSpace(value) || value == AnyValue ? null : value),
            replace: true);
        return Task.CompletedTask;
    }

    private Task RemoveFilterAsync(string key) => OnFilterChangedAsync(key, null);

    private Task ClearFiltersAsync()
    {
        // Sort stays; only the filters go.
        Nav.NavigateTo(
            Nav.GetUriWithQueryParameters(FilterKeys.ToDictionary(key => key, _ => (object?)null)),
            replace: true);
        return Task.CompletedTask;
    }

    private string SelectedValue(DisplayGridFilterGroupDto group) => QueryValue(group.Key) ?? AnyValue;

    private static IReadOnlyList<AppSelectOption> FilterChoices(DisplayGridFilterGroupDto group) =>
        [
            new AppSelectOption(AnyValue, "Any"),
            .. group.Options.Select(option => new AppSelectOption(option.Value, $"{option.Label} ({option.Count.ToString("N0", CultureInfo.CurrentCulture)})")),
        ];

    private bool HasChoicesInAddress => Clean(SortQuery) is not null || FilterKeys.Any(key => QueryValue(key) is not null);

    private string? QueryValue(string key) => Clean(key switch
    {
        DisplayGridValues.FilterType => TypeQuery,
        DisplayGridValues.FilterGenre => GenreQuery,
        DisplayGridValues.FilterFormat => FormatQuery,
        DisplayGridValues.FilterStatus => StatusQuery,
        _ => null,
    });

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string CountLabel(int count) =>
        count == 1 ? "1 title" : $"{count.ToString("N0", CultureInfo.CurrentCulture)} titles";

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("disconnect", _sentinel);
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The circuit closed first; the browser has already released the observer.
            }
        }

        _self?.Dispose();
    }
}
