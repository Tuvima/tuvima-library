using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Formatting;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Components.Pages;

public partial class ListenPage
{
    // ---- A playlist's own page: the header, the "..." menu and Sort (spec listen-playlists WP4) ----------------------------------

    // The most songs the page reads for one playlist. The Engine answers up to this many in one read, so a long playlist (thousands of
    // songs) lists whole and still reorders through position moves.
    private const int PlaylistItemReadLimit = 5000;

    private string? _activePlaylistOwnerName;

    // The Sort choice staged on the page (null when none); it lives only while the page is in its reorder mode.
    private SongSortState? _playlistSort;

    // The page of a hand-made or Smart playlist, as opposed to Favorites and the other built-in lists.
    private bool IsCollectionPlaylistPage => ActivePlaylistCollection is not null && string.IsNullOrWhiteSpace(PlaylistKey);

    // Favorites and a playlist of this profile's own say "yours"; a saved playlist from someone else names its owner when it is known.
    private string? ActivePlaylistKindLine
    {
        get
        {
            if (IsFavoritesPlaylist)
            {
                return L["PlaylistPage_KindYours", L["PlaylistPage_Kind"].Value].Value;
            }

            if (ActivePlaylistCollection is not { } playlist || !IsCollectionPlaylistPage)
            {
                return null;
            }

            var kind = string.Equals(playlist.CollectionType, "Smart", StringComparison.OrdinalIgnoreCase)
                ? L["PlaylistPage_KindSmart"].Value
                : L["PlaylistPage_Kind"].Value;
            if (!CanSaveActivePlaylist)
            {
                return L["PlaylistPage_KindYours", kind].Value;
            }

            return string.IsNullOrWhiteSpace(_activePlaylistOwnerName) ? kind : L["PlaylistPage_KindOwner", kind, _activePlaylistOwnerName].Value;
        }
    }

    // Songs the library cannot find: the playlist's items that no music in the library answers to, and items the Engine holds that it
    // would not list. They are skipped when the playlist plays and counted in the header.
    private int ActivePlaylistUnavailableCount
    {
        get
        {
            if (!IsCollectionPlaylistPage || ActivePlaylistCollection is not { } playlist)
            {
                return 0;
            }

            var notInLibrary = _playlistItems.Count(item => !_workLookup.ContainsKey(item.WorkId));
            var notListed = _playlistItems.Count < PlaylistItemReadLimit ? Math.Max(0, playlist.ItemCount - _playlistItems.Count) : 0;
            return notInLibrary + notListed;
        }
    }

    // The hero's pills (mockup D): the kind of list in words, then "42 songs" (the songs that play) and, when the Engine knows it, the length
    // ("2h 55m"). A built-in list with no kind line of its own says "Built in".
    private IReadOnlyList<string> ActivePlaylistPills
    {
        get
        {
            var pills = new List<string> { ActivePlaylistKindLine ?? L["PlaylistTiles_TypeBuiltIn"].Value, SongCountText(ActivePlaylistTracks.Count) };
            if (IsCollectionPlaylistPage && DisplayFormat.FormatDurationHoursMinutesOrNull(ActivePlaylistCollection?.TotalDurationSeconds) is { } length)
            {
                pills.Add(length);
            }

            return pills;
        }
    }

    private string SongCountText(int count)
        => count == 1 ? L["ListenPage_FavoritesOneSong"].Value : L["ListenPage_FavoritesSongCount", DisplayFormat.FormatCount(count)].Value;

    private static readonly SongSortState PlaylistOwnOrder = new(null, false);

    // The line under the list ("48 songs, 3 h 12 min"): every song of the list, whatever the search shows, and their summed length when known.
    private string PlaylistTotalLine(IReadOnlyList<SongRow> rows)
    {
        var count = SongCountText(rows.Count);
        return DisplayFormat.FormatDurationHoursMinutesOrNull(rows.Sum(row => row.DurationSeconds ?? 0d)) is { } length
            ? L["PlaylistPage_TotalLine", count, length].Value
            : count;
    }

    // The tile on the right of the hero is the one the Playlists page draws (PlaylistTileComposer): the first four albums of the songs in a
    // 2 by 2, or the cover the person chose, whole.
    private IReadOnlyList<ArtworkStackItem> ActivePlaylistHeroCovers
    {
        get
        {
            var albumCovers = ActivePlaylistTracks.Select(track => track.CoverUrl).OfType<string>();
            return ActivePlaylistCollection is { } playlist && IsCollectionPlaylistPage
                ? PlaylistTileComposer.Compose(playlist, albumCovers).ArtworkStackItems
                : PlaylistTileComposer.ComposeBuiltIn(ActivePlaylistTitle, string.Empty, null, PlaylistWords, albumCovers).ArtworkStackItems;
        }
    }

    private PlaylistTileWords PlaylistWords => new(
        L["PlaylistTiles_TypePlaylist"].Value,
        L["PlaylistTiles_TypeSmartPlaylist"].Value,
        L["PlaylistTiles_TypeBuiltIn"].Value,
        L["PlaylistTiles_MetaShortOne"].Value,
        L["PlaylistTiles_MetaShortMany"].Value);

    private string ActivePlaylistUnavailableText => ActivePlaylistUnavailableCount == 1
        ? L["PlaylistPage_UnavailableOne"].Value
        : L["PlaylistPage_Unavailable", DisplayFormat.FormatCount(ActivePlaylistUnavailableCount)].Value;

    // The "..." menu is for a hand-made or Smart playlist this profile owns; Favorites and the built-in lists have none.
    private bool ShowActivePlaylistMenu => IsCollectionPlaylistPage
        && !CanSaveActivePlaylist
        && ActivePlaylistCollection is { } playlist
        && PlaylistMenuRules.ActionsFor(playlist).Count > 0;

    // A hand-made playlist this profile owns: the one list whose order can be saved (drag, Move up and down, Keep this order) and that takes songs.
    private bool IsOwnedHandMadePlaylist => IsCollectionPlaylistPage && CanReorderActivePlaylist;

    // Sort changes the saved order on a hand-made playlist (it is staged, then Keep this order saves it); on every other list it only
    // changes what the page shows. Either way it is offered once there is more than one song.
    private bool CanSortActivePlaylist => ActivePlaylistTracks.Count > 1;

    // ---- Search, sort, Cards and Add songs (spec group-pages-and-tiles D7, WP1b) --------------------------------------------------

    private string _playlistSearch = string.Empty;
    private PlaylistSongView _playlistSongView = PlaylistSongView.List;

    // The sort of a list whose order the person cannot change (Smart, Favorites, built-in, someone else's): a view only, never saved.
    private SongSortState _playlistViewSort = PlaylistOwnOrder;

    private bool ShowPlaylistSearchBar => ActivePlaylistTracks.Count > 0;

    // The sort the table draws a list in: null leaves a hand-made playlist to its staged order, the others take the view sort.
    private SongSortState? PlaylistTableSort => IsOwnedHandMadePlaylist ? null : _playlistViewSort;

    private SongSortState? CurrentPlaylistSort
        => IsOwnedHandMadePlaylist ? _playlistSort : _playlistViewSort.Column is null ? null : _playlistViewSort;

    // "Sort: Playlist order" or "Sort: Title".
    private string PlaylistSortButtonLabel
        => L["PlaylistPage_SortLabel",
            CurrentPlaylistSort is { Column: { } column } && PlaylistSortRules.KeyFor(column) is { } key
                ? PlaylistSortLabel(key)
                : L["PlaylistPage_SortOrder"].Value].Value;

    private void SetPlaylistViewSort(SongSortState state) => _playlistViewSort = state;

    // The playlist's own order again: a staged sort is cancelled (the saved order comes back), a view sort is cleared.
    private async Task ChoosePlaylistOwnOrderAsync()
    {
        if (IsOwnedHandMadePlaylist)
        {
            await CancelTrackReorder();
            return;
        }

        _playlistViewSort = PlaylistOwnOrder;
    }

    // The songs the search leaves: every word typed has to be in the title, the artist or the album.
    private IReadOnlyList<SongRow> PlaylistRowsMatchingSearch(IReadOnlyList<SongRow> rows)
    {
        var words = _playlistSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length == 0
            ? rows
            : [.. rows.Where(row => words.All(word => ContainsWord(row.Title, word) || ContainsWord(row.Artist, word) || ContainsWord(row.Album, word)))];
    }

    private static bool ContainsWord(string? text, string word) => text?.Contains(word, StringComparison.CurrentCultureIgnoreCase) == true;

    // The same rows for the Cards view: a list that only sorts as a view is put in that order here, as the table does for itself.
    private IReadOnlyList<SongRow> PlaylistCardRows(IReadOnlyList<SongRow> rows)
        => IsOwnedHandMadePlaylist || _playlistViewSort.Column is null
            ? rows
            : SongTableSort.Sort(rows, SongTablePlace.Playlist, _playlistViewSort, PlaylistFavoriteIds, ownOrderSortable: true);

    private Task PlayPlaylistCardAsync(SongRow row, IReadOnlyList<SongRow> rows) => PlayPlaylistRowAsync(new SongPlayRequest(rows, row));

    // Add songs is only for a hand-made playlist this profile owns; Smart and built-in lists fill themselves.
    private bool CanAddSongsToActivePlaylist => IsOwnedHandMadePlaylist;

    // Add songs opens the search window over the page. Songs added there go to the end of the playlist; the page reads the playlist again when
    // the window closes with something added.
    private async Task AddSongsToActivePlaylistAsync()
    {
        if (ActivePlaylistCollection is not { } playlist)
        {
            return;
        }

        _notice = null;
        var dialog = await Dialogs.ShowAsync<PlaylistAddSongsDialog>(
            string.Empty,
            new AppDialogParameters<PlaylistAddSongsDialog>
            {
                { component => component.Playlist, playlist },
                { component => component.ProfileId, _activeProfileId },
                { component => component.Songs, (IReadOnlyList<WorkViewModel>)_musicWorks },
                { component => component.AlreadyIn, (IReadOnlySet<Guid>)_playlistItems.Select(item => item.WorkId).ToHashSet() },
            },
            new AppDialogOptions { NoHeader = true, FullWidth = true, MaxWidth = AppMaxWidth.Small, Sheet = AppDialogSheetMode.PhoneAndTablet });
        if ((await dialog.Result) is { Canceled: false })
        {
            await LoadAsync();
        }
    }

    private Task RenameActivePlaylistAsync(ManagedCollectionViewModel playlist) => ShowPlaylistEditDialogAsync(playlist, PlaylistEditMode.Rename);

    private Task EditActivePlaylistDetailsAsync(ManagedCollectionViewModel playlist) => ShowPlaylistEditDialogAsync(playlist, PlaylistEditMode.Details);

    // Rename and Edit description and cover are one small window over the page; the page reads the playlist again when something was saved.
    private async Task ShowPlaylistEditDialogAsync(ManagedCollectionViewModel playlist, PlaylistEditMode mode)
    {
        _notice = null;
        var dialog = await Dialogs.ShowAsync<PlaylistEditDialog>(
            string.Empty,
            new AppDialogParameters<PlaylistEditDialog>
            {
                { component => component.Playlist, playlist },
                { component => component.ProfileId, _activeProfileId },
                { component => component.Mode, mode },
            },
            new AppDialogOptions { NoHeader = true, FullWidth = true, MaxWidth = AppMaxWidth.Small, Sheet = AppDialogSheetMode.PhoneAndTablet });
        if ((await dialog.Result) is { Canceled: false })
        {
            await LoadAsync();
        }
    }

    // The copy opens at once, which is the answer to "did it work".
    private async Task DuplicatePlaylistAsync(ManagedCollectionViewModel playlist)
    {
        _notice = null;
        var copy = await ApiClient.DuplicateCollectionAsync(playlist.Id, null, _activeProfileId);
        if (copy is null)
        {
            ShowNotice(L["PlaylistPage_DuplicateFailed"].Value, AppUiTone.Error);
            return;
        }

        NavigateTo($"/listen/music/playlists/{copy.Id}");
    }

    // Pin to top and Unpin belong to this profile only.
    private async Task TogglePlaylistPinAsync(ManagedCollectionViewModel playlist)
    {
        _notice = null;
        if (!await ApiClient.SetCollectionPinnedAsync(playlist.Id, playlist.IsPinned != true, _activeProfileId))
        {
            ShowNotice(L["PlaylistPage_PinFailed"].Value, AppUiTone.Error);
            return;
        }

        await LoadAsync();
    }

    // Edit rules opens the rule editor over the page for a Smart list.
    private async Task EditPlaylistRulesAsync(ManagedCollectionViewModel playlist)
    {
        _notice = null;
        var entry = CollectionEditorEntryPoints.For(CollectionEditorEntryPoints.Listen, _activeProfileId);
        var request = new CollectionEditorLaunchRequest
        {
            EditingCollection = PlaylistMenuRules.EditorItemFor(playlist),
            ActiveProfileId = entry.ActiveProfileId,
            Kind = entry.Kind,
            InitialPrimaryArea = entry.InitialPrimaryArea,
            InitialMediaTypes = entry.InitialMediaTypes,
        };
        var launch = await CollectionEditorLauncher.TryOpenAsync(request);
        if (launch == EditorLaunchResult.NotAvailableOnThisDevice)
        {
            ShowNotice(EditorDeviceRule.ItemMessage, AppUiTone.Info);
            return;
        }

        if (launch == EditorLaunchResult.Applied || request.SavedCollectionIds.Count > 0)
        {
            await LoadAsync();
        }
    }

    // ---- Sort, then Keep this order -----------------------------------------------------------------------------------------------

    private string PlaylistSortLabel(PlaylistSortKey key) => key switch
    {
        PlaylistSortKey.Artist => L["ListenPage_Artist"].Value,
        PlaylistSortKey.Album => L["ListenPage_Album"].Value,
        PlaylistSortKey.DateAdded => L["SongTable_DateAdded"].Value,
        PlaylistSortKey.Length => L["Common_Length"].Value,
        _ => L["Common_Title"].Value,
    };

    private bool IsPlaylistSortApplied(PlaylistSortKey key)
        => CurrentPlaylistSort is { Column: { } column } && PlaylistSortRules.KeyFor(column) == key;

    // The words on the left of the reorder bar while a sort is staged ("Sorted by Title"); empty falls back to the bar's own hint.
    private string? PlaylistSortMessage
        => _playlistSort is { Column: { } column } && PlaylistSortRules.KeyFor(column) is { } key
            ? L["PlaylistPage_SortedBy", PlaylistSortLabel(key)].Value
            : null;

    private string? PlaylistSortSaveLabel => _playlistSort is not null ? L["PlaylistPage_KeepThisOrder"].Value : null;

    // Sort only changes the page's copy of the order, in the same staged mode as Reorder, until Keep this order saves it as the manual order
    // (Cancel puts the saved order back).
    private Task ChoosePlaylistSortAsync(PlaylistSortKey key)
    {
        if (!IsOwnedHandMadePlaylist)
        {
            // A list the person cannot reorder is only shown in that order; nothing is staged and there is nothing to keep.
            _playlistViewSort = PlaylistSortRules.Choose(_playlistViewSort.Column is null ? null : _playlistViewSort, key);
            return Task.CompletedTask;
        }

        if (!TrackReorderActive)
        {
            BeginTrackReorder();
        }

        _playlistSort = PlaylistSortRules.Choose(_playlistSort, key);
        var rows = ActivePlaylistTrackRows
            .Where(row => row.Item is not null)
            .Select(row => SongRowMapper.FromPlaylistItem(row.Item, row.Work, row.Index))
            .ToList();
        var sorted = PlaylistSortRules.Order(rows, _playlistSort);

        // Songs the library cannot find have no row to sort; they keep their relative order after the rest.
        var byId = _playlistItems.ToDictionary(item => item.Id);
        var placed = sorted.ToHashSet();
        var unplaced = _playlistItems.OrderBy(item => item.SortOrder).Where(item => !placed.Contains(item.Id)).Select(item => item.Id);
        var position = 1;
        foreach (var id in sorted.Concat(unplaced))
        {
            byId[id].SortOrder = position++;
        }

        return Task.CompletedTask;
    }
}
