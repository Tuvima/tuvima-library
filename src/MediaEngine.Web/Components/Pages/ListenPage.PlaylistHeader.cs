using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Formatting;
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

    // "42 songs · 2h 55m": the count of songs that play, then the length when the Engine knows it (a list with no known length shows the count only).
    private string? ActivePlaylistMetaLine
    {
        get
        {
            if (!IsFavoritesPlaylist && !IsCollectionPlaylistPage)
            {
                return null;
            }

            var count = ActivePlaylistTracks.Count;
            var parts = new List<string>
            {
                count == 1 ? L["ListenPage_FavoritesOneSong"].Value : L["ListenPage_FavoritesSongCount", DisplayFormat.FormatCount(count)].Value,
            };
            if (IsCollectionPlaylistPage && DisplayFormat.FormatDurationHoursMinutesOrNull(ActivePlaylistCollection?.TotalDurationSeconds) is { } length)
            {
                parts.Add(length);
            }

            return string.Join(" · ", parts);
        }
    }

    private string ActivePlaylistUnavailableText => ActivePlaylistUnavailableCount == 1
        ? L["PlaylistPage_UnavailableOne"].Value
        : L["PlaylistPage_Unavailable", DisplayFormat.FormatCount(ActivePlaylistUnavailableCount)].Value;

    // The "..." menu is for a hand-made or Smart playlist this profile owns; Favorites and the built-in lists have none.
    private bool ShowActivePlaylistMenu => IsCollectionPlaylistPage
        && !CanSaveActivePlaylist
        && ActivePlaylistCollection is { } playlist
        && PlaylistMenuRules.ActionsFor(playlist).Count > 0;

    // Sort changes the saved order, so it is offered where Reorder is, on a hand-made playlist only.
    private bool CanSortActivePlaylist => IsCollectionPlaylistPage
        && CanReorderActivePlaylist
        && ActivePlaylistCollection is { } playlist
        && CanAcceptPlaylistItems(playlist)
        && ActivePlaylistTracks.Count > 1;

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
            EditingCollection = EditorItemFor(playlist),
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

    private static CollectionListItemViewModel EditorItemFor(ManagedCollectionViewModel playlist) => new()
    {
        Id = playlist.Id,
        Name = playlist.Name,
        Description = playlist.Description,
        IconName = playlist.IconName,
        CollectionType = playlist.CollectionType,
        Resolution = playlist.Resolution,
        Scope = playlist.Scope,
        ProfileId = playlist.ProfileId,
        Visibility = playlist.Visibility,
        MembershipMode = playlist.MembershipMode,
        PrimaryArea = playlist.PrimaryArea,
        OwnerKind = playlist.OwnerKind,
        Audience = playlist.Audience,
        SelectedProfileIds = playlist.SelectedProfileIds,
        IsEnabled = playlist.IsEnabled,
        IsFeatured = playlist.IsFeatured,
        MinItems = playlist.MinItems,
        MaxItems = playlist.MaxItems,
        SeasonFrom = playlist.SeasonFrom,
        SeasonTo = playlist.SeasonTo,
        HiddenUntil = playlist.HiddenUntil,
        ItemCount = playlist.ItemCount,
        RuleJson = playlist.RuleJson,
        RuleHash = playlist.RuleHash,
        MatchMode = playlist.MatchMode,
        SortField = playlist.SortField,
        SortDirection = playlist.SortDirection,
        SecondarySortField = playlist.SecondarySortField,
        SecondarySortDirection = playlist.SecondarySortDirection,
        CoverUrl = playlist.CoverArtworkUrl,
        BackgroundUrl = playlist.BackgroundArtworkUrl,
        BannerUrl = playlist.BannerArtworkUrl,
        LogoUrl = playlist.LogoArtworkUrl,
        CreatedAt = playlist.CreatedAt,
        CanEdit = playlist.CanEdit,
        CanShare = playlist.CanShare,
    };

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
        => _playlistSort is { Column: { } column } && PlaylistSortRules.KeyFor(column) == key;

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
