using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain.Enums;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Library;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Branding;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Components.Browse;

public partial class MediaBrowseShell
{
    private async Task LoadBrowseContentAsync(bool append = false)
    {
        _loadFailed = false;
        _loadError = null;
        _loading = true;
        var loadVersion = ++_browseLoadVersion;
        if (!_hasLoadedOnce)
        {
            StateHasChanged();
        }

        try
        {
            if (IsTimelineGrouping)
            {
                if (!await LoadDisplayCardsAsync(append))
                {
                    throw new InvalidOperationException("The timeline could not be loaded.");
                }
            }
            else if (IsPlaylistGrouping)
            {
                await LoadPlaylistsAsync();
            }
            else if (IsContainerGrouping || UsesAlbumTimeline || UsesTvShowTimeline)
            {
                if (IsTvShowsGrouping && !UseListLayout)
                {
                    if (!await LoadDisplayCardsAsync(append))
                    {
                        throw new InvalidOperationException("Display API did not return a TV browse page.");
                    }
                }
                else
                {
                    await LoadGroupsAsync();
                    _timelineIndex = null;
                    _timelineOffset = 0;
                    _cards.Clear();
                    _cards.AddRange(_groups.Select(ToGroupCard));
                }
            }
            else
            {
                if (!UseListLayout)
                {
                    if (!await LoadDisplayCardsAsync(append))
                    {
                        throw new InvalidOperationException("Display API did not return a browse page.");
                    }
                }
                else
                {
                    await LoadItemsAsync(append);
                }
            }
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            _loadError = ex.Message;
        }
        finally
        {
            // A superseded load must not clear the flag for the newer one.
            if (loadVersion == _browseLoadVersion)
            {
                _loading = false;
                DropTicksOfHiddenAlbums();
            }

            _hasLoadedOnce = true;
        }
    }

    private int _browseLoadVersion;

    private Task ReloadAsync() => LoadBrowseContentAsync();

    // Rows a reload asks for instead of one page, so a reload after a bulk save keeps the rows "Load more" added (and the ticks and
    // scroll on them) instead of falling back to the first page. Null for every other load.
    private int? _reloadRowCount;

    private async Task ReloadAfterBulkSaveAsync()
    {
        _reloadRowCount = Math.Clamp(_items.Count, PageSize, MediaEngine.Contracts.Paging.PagedRequest.MaxLimit);
        try
        {
            await LoadBrowseContentAsync();
        }
        finally
        {
            _reloadRowCount = null;
        }
    }

    private void OnMediaAdded(MediaAddedEvent ev)
    {
        Interlocked.Increment(ref _pendingMediaAddedCount);
        _mediaAddedReloadCts?.Cancel();
        _mediaAddedReloadCts?.Dispose();
        _mediaAddedReloadCts = new CancellationTokenSource();
        var token = _mediaAddedReloadCts.Token;

        _ = DebounceMediaAddedReloadAsync(ev.Title, token);
    }

    // Titles deleted from the library: the same silent, debounced reload a new title gets; the next answer no longer holds them.
    private void OnLibraryItemsRemoved(LibraryItemsRemovedEvent ev)
    {
        _mediaAddedReloadCts?.Cancel();
        _mediaAddedReloadCts?.Dispose();
        _mediaAddedReloadCts = new CancellationTokenSource();
        _ = DebounceMediaAddedReloadAsync(string.Empty, _mediaAddedReloadCts.Token);
    }

    private async Task DebounceMediaAddedReloadAsync(string latestTitle, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(750), token);
            await InvokeAsync(async () =>
            {
                var count = Interlocked.Exchange(ref _pendingMediaAddedCount, 0);
                // Item events refresh content silently; activity notifications are run-scoped.
                await LoadBrowseContentAsync();
                StateHasChanged();
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private string BuildCurrentQueryKey() => string.Join('|',
        Preset.Title,
        _activeTabId,
        _grouping,
        _searchText,
        string.Join(',', _selectedGenres.Order()),
        string.Join(',', _selectedCreators.Order()),
        string.Join(',', _selectedYears.Order()),
        string.Join(',', _selectedVibes.Order()),
        string.Join(',', _selectedThemes.Order()),
        _status,
        _unmatched,
        _libraryChip,
        _folder,
        _sortBy,
        _layout,
        _activeProfileId?.ToString("N") ?? string.Empty);

    private async Task<bool> LoadDisplayCardsAsync(bool append, int? timelineOffset = null)
    {
        var requestVersion = ++_displayRequestVersion;
        var ct = BeginBrowseRequest();
        var queryKey = BuildCurrentQueryKey();
        var requestedOffset = timelineOffset ?? (append ? _timelineOffset + _cards.Count : 0);
        var page = await ApiClient.GetDisplayBrowseAsync(
            mediaType: CurrentTab.MediaType,
            grouping: BrowseGrouping,
            search: string.IsNullOrWhiteSpace(_searchText) ? null : _searchText,
            offset: requestedOffset,
            limit: PageSize,
            genres: _selectedGenres.Count == 0 ? null : string.Join(',', _selectedGenres),
            creator: _selectedCreators.Count == 0 ? null : string.Join(',', _selectedCreators),
            status: string.IsNullOrWhiteSpace(_status) ? null : _status,
            year: _selectedYears.Count == 0 ? null : string.Join(',', _selectedYears),
            vibes: _selectedVibes.Count == 0 ? null : string.Join(',', _selectedVibes),
            themes: _selectedThemes.Count == 0 ? null : string.Join(',', _selectedThemes),
            profileId: _activeProfileId,
            sort: _sortBy,
            libraryId: ActiveLibraryId,
            unmatched: _unmatched ? true : null,
            folder: ActiveFolderId,
            ct: ct);

        if (ct.IsCancellationRequested)
        {
            return true;
        }

        if (requestVersion != _displayRequestVersion || queryKey != BuildCurrentQueryKey())
        {
            return true;
        }

        if (page is null)
        {
            return false;
        }

        var incoming = SortCards(page.Catalog.Select(MediaTileComposerService.FromDisplayCard)).ToList();
        if (append)
        {
            _cards.AddRange(incoming);
            var sorted = SortCards(_cards).ToList();
            _cards.Clear();
            _cards.AddRange(sorted);
        }
        else
        {
            _timelineOffset = requestedOffset;
            _timelineIndex = page.Timeline;
            _cards.Clear();
            _cards.AddRange(incoming);
            _facets = page.Facets;
            _totalCount = page.TotalCount > 0 ? page.TotalCount : page.Catalog.Count;
            if (timelineOffset is null)
            {
                await LoadContinueAsync();
            }
        }

        _items = [];
        _groups = [];
        _hasMore = requestedOffset + page.Catalog.Count < page.TotalCount;
        return true;
    }

    /// <summary>Starts a browse request, cancelling the one still in flight (stale filters, tab or page change).</summary>
    private CancellationToken BeginBrowseRequest()
    {
        var previous = _browseRequestCts;
        _browseRequestCts = new CancellationTokenSource();
        previous?.Cancel();
        previous?.Dispose();
        return _browseRequestCts.Token;
    }

    private async Task LoadItemsAsync(bool append)
    {
        var requestedOffset = append ? _items.Count : 0;
        var ct = BeginBrowseRequest();
        var response = await ApiClient.GetDisplayBrowseAsync(
            mediaType: CurrentTab.MediaType,
            grouping: BrowseGrouping,
            search: string.IsNullOrWhiteSpace(_searchText) ? null : _searchText,
            offset: requestedOffset,
            limit: append ? PageSize : _reloadRowCount ?? PageSize,
            genres: _selectedGenres.Count == 0 ? null : string.Join(',', _selectedGenres),
            creator: _selectedCreators.Count == 0 ? null : string.Join(',', _selectedCreators),
            status: string.IsNullOrWhiteSpace(_status) ? null : _status,
            year: _selectedYears.Count == 0 ? null : string.Join(',', _selectedYears),
            vibes: _selectedVibes.Count == 0 ? null : string.Join(',', _selectedVibes),
            themes: _selectedThemes.Count == 0 ? null : string.Join(',', _selectedThemes),
            profileId: _activeProfileId,
            sort: _sortBy,
            libraryId: ActiveLibraryId,
            unmatched: _unmatched ? true : null,
            folder: ActiveFolderId,
            ct: ct);

        // A newer browse request (or leaving the page) cancelled this one; the newer load owns the state.
        if (ct.IsCancellationRequested)
        {
            return;
        }

        var incoming = response?.Catalog.Select(ToLibraryItem).ToList() ?? [];
        if (response is null && !string.IsNullOrWhiteSpace(ApiClient.LastError))
        {
            _loadFailed = true;
            _loadError = ApiClient.LastError;
            return;
        }

        if (append)
        {
            _items.AddRange(incoming);
        }
        else
        {
            _items = incoming;
            _totalCount = response?.TotalCount ?? incoming.Count;
            _facets = response?.Facets;
            await LoadContinueAsync(ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }
        }

        _cards.Clear();
        _groups.Clear();
        _hasMore = response is not null
                   && requestedOffset + incoming.Count < response.TotalCount;

        if (!append)
        {
            await PlayRequestedSongAsync();
        }
    }

    private async Task LoadContinueAsync(CancellationToken ct = default)
    {
        DisplayPageDto? page;
        try
        {
            page = await ApiClient.GetDisplayContinueAsync(
                lane: Preset.Title.ToLowerInvariant(),
                mediaType: CurrentTab.MediaType,
                limit: CompactContinueStrip.MaxItems,
                ct: ct);
        }
        finally
        {
            _continueLoading = false;
        }
        if (ct.IsCancellationRequested)
        {
            return;
        }

        _continueCards.Clear();
        if (page is not null)
        {
            _continueCards.AddRange(page.Catalog
                .Select(card => MediaTileComposerService.FromDisplayCard(card, MediaTileContext.ContinueWatching))
                .Where(item => item.ProgressPct is > 0 and < 100)
                .Take(CompactContinueStrip.MaxItems));
        }
    }

    private async Task LoadGroupsAsync()
    {
        var systemGroupField = GetSystemViewGroupField();
        var mediaType = CurrentTab.MediaType;

        var groups = systemGroupField is not null
            ? await ApiClient.GetSystemViewGroupsAsync(mediaType, systemGroupField, libraryId: ActiveLibraryId, unmatched: _unmatched ? true : null, folder: ActiveFolderId)
            : await ApiClient.GetContentGroupsAsync(libraryId: ActiveLibraryId, unmatched: _unmatched ? true : null, folder: ActiveFolderId);
        if (systemGroupField is null)
        {
            groups = groups
                .Where(group => MediaTypeMatches(group.PrimaryMediaType, mediaType))
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(_searchText)
            || _selectedGenres.Count > 0
            || _selectedCreators.Count > 0
            || _selectedYears.Count > 0
            || _selectedVibes.Count > 0
            || _selectedThemes.Count > 0
            || !string.IsNullOrWhiteSpace(_status)
            || _unmatched)
        {
            var qualifyingPage = await ApiClient.GetDisplayBrowseAsync(
                mediaType: CurrentTab.MediaType,
                grouping: "all",
                search: string.IsNullOrWhiteSpace(_searchText) ? null : _searchText,
                offset: 0,
                limit: 200,
                genres: _selectedGenres.Count == 0 ? null : string.Join(',', _selectedGenres),
                creator: _selectedCreators.Count == 0 ? null : string.Join(',', _selectedCreators),
                status: string.IsNullOrWhiteSpace(_status) ? null : _status,
                year: _selectedYears.Count == 0 ? null : string.Join(',', _selectedYears),
                vibes: _selectedVibes.Count == 0 ? null : string.Join(',', _selectedVibes),
                themes: _selectedThemes.Count == 0 ? null : string.Join(',', _selectedThemes),
                profileId: _activeProfileId,
                sort: _sortBy,
                libraryId: ActiveLibraryId,
                unmatched: _unmatched ? true : null,
                folder: ActiveFolderId);

            if (qualifyingPage is not null)
            {
                _facets = qualifyingPage.Facets;
                groups = FilterGroupsToQualifyingCards(groups, qualifyingPage.Catalog, systemGroupField);
            }
        }

        if (_selectedCreators.Count > 0)
        {
            groups = groups.Where(group => _selectedCreators.Any(selected =>
                    string.Equals(group.Creator, selected, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(group.Network, selected, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        if (_selectedYears.Count > 0)
        {
            groups = groups.Where(group => _selectedYears.Contains(group.Year ?? string.Empty))
                .ToList();
        }

        _groups = await SortGroupsAsync(groups);
        _totalCount = _groups.Count;
        await EnsureFacetOptionsAsync();
        await LoadContinueAsync();
        _hasMore = false;
    }

    private async Task LoadPlaylistsAsync()
    {
        var playlists = (await ApiClient.GetManagedCollectionsAsync(_activeProfileId))
            .Where(MediaTileComposerService.IsUserVisiblePlaylist)
            .Where(playlist => string.IsNullOrWhiteSpace(_searchText)
                               || playlist.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
                               || (playlist.Description?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        playlists = _sortBy switch
        {
            "title" => playlists.OrderBy(playlist => playlist.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            "oldest" => playlists.OrderBy(playlist => playlist.CreatedAt).ToList(),
            _ => playlists.OrderByDescending(playlist => playlist.ModifiedAt ?? playlist.CreatedAt)
                .ThenBy(playlist => playlist.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };

        _cards.Clear();
        _cards.AddRange(playlists.Select(MediaTileComposerService.FromPlaylist));
        _items = playlists.Select(playlist => new LibraryItemViewModel
        {
            EntityId = playlist.Id,
            Title = playlist.Name,
            MediaType = "Music",
            CoverUrl = playlist.CoverArtworkUrl,
            Album = playlist.TypeLabel,
            CreatedAt = playlist.CreatedAt,
        }).ToList();
        _groups = [];
        _totalCount = playlists.Count;
        _facets = null;
        _continueCards.Clear();
        _hasMore = false;
    }

    private static List<ContentGroupViewModel> FilterGroupsToQualifyingCards(
        IEnumerable<ContentGroupViewModel> groups,
        IReadOnlyList<DisplayCardDto> cards,
        string? groupField)
    {
        var workIds = cards
            .Select(card => card.WorkId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();
        var collectionIds = cards
            .Select(card => card.CollectionId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();
        var people = cards
            .Select(card => card.Subtitle)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return groups.Where(group =>
        {
            if (groupField is "author" or "artist" or "director" or "narrator" or "network")
            {
                return people.Contains(group.DisplayName);
            }

            return collectionIds.Contains(group.CollectionId)
                   || group.PreviewItems.Any(item => workIds.Contains(item.WorkId));
        }).ToList();
    }

    private async Task EnsureFacetOptionsAsync()
    {
        if (!SupportsFacetFilters || _facets is not null)
        {
            return;
        }

        var facetPage = await ApiClient.GetDisplayBrowseAsync(
            mediaType: CurrentTab.MediaType,
            grouping: "all",
            offset: 0,
            limit: 1,
            profileId: _activeProfileId,
            libraryId: ActiveLibraryId,
            unmatched: _unmatched ? true : null,
            folder: ActiveFolderId);
        _facets = facetPage?.Facets;
    }

    private List<LibraryItemViewModel> SortItems(IEnumerable<LibraryItemViewModel> source)
    {
        return _sortBy switch
        {
            "title" => source.OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            "oldest" => source.OrderBy(item => item.CreatedAt).ToList(),
            "creator" => source.OrderBy(item => item.Creator ?? item.ContextLabel ?? item.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            "year" => source.OrderByDescending(item => ParseYear(item.Year)).ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => source.OrderByDescending(item => item.CreatedAt).ToList(),
        };
    }

    private async Task<List<ContentGroupViewModel>> SortGroupsAsync(IEnumerable<ContentGroupViewModel> source)
    {
        if (IsMusicTab
            && string.Equals(_grouping, "albums", StringComparison.OrdinalIgnoreCase)
            && string.Equals(_sortBy, "recently-played", StringComparison.OrdinalIgnoreCase))
        {
            var recent = await ApiClient.GetDisplayContinueAsync(
                lane: "listen",
                mediaType: "Music",
                limit: 200);
            var recentOrder = (recent?.Catalog ?? [])
                .Where(card => card.CollectionId.HasValue)
                .GroupBy(card => card.CollectionId!.Value)
                .ToDictionary(
                    group => group.Key,
                    group => group.Max(card => card.SortTimestamp));
            return source
                .Where(group => recentOrder.ContainsKey(group.CollectionId))
                .OrderByDescending(group => recentOrder[group.CollectionId])
                .ThenBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return _sortBy switch
        {
            "title" => source.OrderBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
            "newest" => source.OrderByDescending(group => group.CreatedAt).ThenBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
            "creator" => source.OrderBy(group => group.Creator ?? group.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
            "year" => source.OrderByDescending(group => ParseYear(group.Year))
                .ThenBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => source.OrderByDescending(group => group.WorkCount).ThenBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    private IEnumerable<MediaTileViewModel> SortCards(IEnumerable<MediaTileViewModel> source)
    {
        return _sortBy switch
        {
            "title" => source.OrderBy(card => card.Title, StringComparer.OrdinalIgnoreCase),
            "oldest" => source.OrderBy(card => card.SortTimestamp),
            "creator" => source.OrderBy(card => card.Creator ?? card.Subtitle ?? card.Title, StringComparer.OrdinalIgnoreCase),
            "year" => source.OrderByDescending(card => card.SortYear).ThenBy(card => card.Title, StringComparer.OrdinalIgnoreCase),
            _ => source.OrderByDescending(card => card.SortTimestamp),
        };
    }

    private async Task LoadMoreAsync()
    {
        await LoadBrowseContentAsync(append: true);
    }
}
