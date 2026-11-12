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
    // The ticked rows in the order the table shows them. Only single titles can be edited together.
    private IReadOnlyList<BulkEditRow> BulkRows => _selectedItems.Count == 0
        ? []
        : [.. ActiveTableItems
            .Where(item => _selectedItems.Contains(item.EntityId))
            .Select(item => new BulkEditRow(item.EntityId, item.Title, BulkKindFor(item.MediaType), null, item.MediaType))
            .Where(row => BulkEditRules.IsSelectableKind(row.Kind))];

    private static DetailEntityType BulkKindFor(string? mediaType) => mediaType switch
    {
        "Movies" => DetailEntityType.Movie,
        "Books" => DetailEntityType.Book,
        "TV" => DetailEntityType.TvEpisode,
        "Comics" => DetailEntityType.ComicIssue,
        "Audiobooks" => DetailEntityType.Audiobook,
        "Music" => DetailEntityType.Work,
        _ => DetailEntityType.Collection,
    };

    private Task ClearBulkSelectionAsync()
    {
        _selectedItems.Clear();
        return Task.CompletedTask;
    }

    private Task OnBulkDeletedAsync(IReadOnlyList<Guid> ids)
    {
        _selectedItems.ExceptWith(ids);
        return ReloadAsync();
    }

    private Task OnSelectionChanged(HashSet<Guid> selected)
    {
        _selectedItems.Clear();
        foreach (var item in selected)
        {
            _selectedItems.Add(item);
        }
        return Task.CompletedTask;
    }

    // ---- Albums in the Listen Albums list (Listen music spec D15) ---------------------------------------------------
    // Albums are ticked only in the cover grid of that list, so a whole album can go into a playlist. The table (List) layout offers no
    // ticking, exactly as before. Albums are never edited, moved, matched or deleted from here: every one of those rules still asks
    // BulkEditRules.IsSelectableKind, which refuses albums. A cover is ticked under its group's id, which is unique per album (name and
    // artist), so two albums with the same title by different artists are two ticks.

    private bool IsMusicAlbumsGrouping => IsMusicTab && IsContainerGrouping && string.Equals(_grouping, "albums", StringComparison.OrdinalIgnoreCase);

    // The one place albums can be ticked: the Albums cover grid.
    private bool IsAlbumSelectionList => IsMusicAlbumsGrouping && !UseListLayout;

    // Select mode on the cover grid: covers tick instead of opening.
    private bool _albumSelecting;

    // True while the ticks in _selectedItems are album ticks, so leaving the list drops them and no other list's ticks.
    private bool _albumTicksHeld;

    private bool AlbumSelectionOn => _albumSelecting && IsAlbumSelectionList;

    // A desktop or tablet has the Select button; a phone starts select mode from a cover's "..." sheet (MediaTileActions).
    private bool ShowAlbumSelectButton => IsAlbumSelectionList && !DeviceContext.IsPhone;

    private static Guid? AlbumSelectionKey(ContentGroupViewModel group) =>
        group.CollectionId == Guid.Empty ? null : group.CollectionId;

    private static Guid? AlbumTileKey(MediaTileViewModel item) =>
        item.CollectionId is { } id && id != Guid.Empty ? id : null;

    // A cover can be ticked when it is an album's and has a group id to tick under.
    private bool IsAlbumTile(MediaTileViewModel item) =>
        IsAlbumSelectionList
        && item.Presentation == MediaTilePresentation.Album
        && MediaTileActionTarget.For(item) is { } target
        && BulkEditRules.IsPlaylistSelectableKind(target.EntityType)
        && AlbumTileKey(item) is not null;

    // The ticked albums in the order the list shows them. An album is named by its page's id (its root work, else its group id) and its
    // title and artist, which is how its songs are found when it goes into a playlist; only its root work can stand for it in a collection.
    private IReadOnlyList<BulkEditRow> AlbumBulkRows => !IsAlbumSelectionList || _selectedItems.Count == 0
        ? []
        : [.. _groups
            .Where(group => AlbumSelectionKey(group) is { } key && _selectedItems.Contains(key))
            .Select(group => new BulkEditRow(
                group.RootWorkId ?? group.CollectionId,
                group.DisplayName,
                DetailEntityType.MusicAlbum,
                null,
                "Music",
                group.CoverUrl,
                group.Creator,
                FromAlbumsList: true,
                RootWorkId: group.RootWorkId))];

    private MediaTileSelectionOffer? _albumSelectionOffer;

    private MediaTileSelectionOffer AlbumSelectionOffer => _albumSelectionOffer ??= new(IsAlbumTile, BeginAlbumSelectAsync);

    private bool IsAlbumTileSelected(MediaTileViewModel item) =>
        AlbumTileKey(item) is { } key && _selectedItems.Contains(key);

    private Task ToggleAlbumTileAsync(MediaTileViewModel item)
    {
        if (!IsAlbumTile(item) || AlbumTileKey(item) is not { } key)
        {
            return Task.CompletedTask;
        }

        if (!_selectedItems.Remove(key))
        {
            _selectedItems.Add(key);
            _albumTicksHeld = true;
        }

        return Task.CompletedTask;
    }

    private Task BeginAlbumSelectAsync(MediaTileViewModel item)
    {
        if (!IsAlbumTile(item) || AlbumTileKey(item) is not { } key)
        {
            return Task.CompletedTask;
        }

        _albumSelecting = true;
        _albumTicksHeld = true;
        _selectedItems.Add(key);
        StateHasChanged();
        return Task.CompletedTask;
    }

    private Task ToggleAlbumSelectAsync()
    {
        if (AlbumSelectionOn)
        {
            return EndAlbumSelectAsync();
        }

        _albumSelecting = true;
        return Task.CompletedTask;
    }

    // Clear on the bar: the ticks go, the cover grid stays in select mode.
    private Task ClearAlbumSelectionAsync()
    {
        _selectedItems.Clear();
        _albumTicksHeld = false;
        return Task.CompletedTask;
    }

    // Done: the ticks go and the covers open again.
    private Task EndAlbumSelectAsync()
    {
        _albumSelecting = false;
        return ClearAlbumSelectionAsync();
    }

    // Leaving the Albums cover grid by any route (tab, grouping, layout, Unmatched, an address change, back or forward) ends select mode and
    // drops the album ticks. Called wherever the list's state is read or changed.
    private void DropAlbumSelectionWhenLeft()
    {
        if (IsAlbumSelectionList)
        {
            return;
        }

        _albumSelecting = false;
        if (_albumTicksHeld)
        {
            _selectedItems.Clear();
            _albumTicksHeld = false;
        }
    }

    // After the albums are loaded again (a filter or search changed them), an album that is no longer in the list is no longer ticked.
    private void DropTicksOfHiddenAlbums()
    {
        if (!_albumTicksHeld)
        {
            return;
        }

        if (!IsAlbumSelectionList)
        {
            DropAlbumSelectionWhenLeft();
            return;
        }

        _selectedItems.IntersectWith(_groups.Select(AlbumSelectionKey).OfType<Guid>());
    }

    private void HandleTableRowClicked(Guid entityId)
    {
        if (IsContainerGrouping)
        {
            var displayItem = ActiveTableItems.FirstOrDefault(item => item.EntityId == entityId);
            if (displayItem is null)
            {
                return;
            }

            var matchingGroup = _groups.FirstOrDefault(group =>
                string.Equals(group.DisplayName, displayItem.Title, StringComparison.OrdinalIgnoreCase));

            if (matchingGroup is not null)
            {
                _navigatingInternally = true;
                Nav.NavigateTo(BuildGroupNavigationUrl(matchingGroup));
            }

            return;
        }

        var selectedItem = ActiveTableItems.FirstOrDefault(item => item.EntityId == entityId);
        Nav.NavigateTo(selectedItem is not null
            ? MediaNavigation.ForLibraryItem(selectedItem)
            : $"/details/work/{entityId:D}");
    }

    private Task OpenItem(Guid entityId)
    {
        var displayItem = _items.FirstOrDefault(item => item.EntityId == entityId);
        Nav.NavigateTo(displayItem is not null
            ? MediaNavigation.ForLibraryItem(displayItem)
            : $"/details/work/{entityId:D}");
        return Task.CompletedTask;
    }

    // Edit opens the pop-over editor over this list (universal editor v3 D6): nothing navigates away, the list keeps its filters, sort and
    // scroll, and it reloads its rows when the pop-over closes with a change (the Engine's own answer says whether this item may be edited).
    // The row's Edit is offered only to a profile the Engine says may edit item details (on a device that may edit); the handler asks again.
    private bool CanEditItems => EditorDeviceRule.CanEditItems(DeviceContext, Services.GetService<DashboardSessionAccessor>());

    private async Task OpenItemEditorAsync(Guid entityId)
    {
        var item = ActiveTableItems.FirstOrDefault(candidate => candidate.EntityId == entityId)
                   ?? _items.FirstOrDefault(candidate => candidate.EntityId == entityId);
        if (entityId == Guid.Empty)
        {
            return;
        }

        if (EditorDeviceRule.ItemRefusal(DeviceContext, Services.GetService<DashboardSessionAccessor>()) is { } refusal)
        {
            ShowNotice(refusal, AppUiTone.Info);
            return;
        }

        ClearNotice();
        _awaitingSingleEdit = true;
        // A delete that closes the editor has its line (files left on disk, say) shown here, in the notice above the list.
        if (!await EditLauncher.OpenAsync(
                ListEditTarget.ForLibraryRow(entityId, item?.Title, item?.MediaType),
                deletedNotice: notice => InvokeAsync(() =>
                {
                    ShowNotice(notice, AppUiTone.Warning);
                    StateHasChanged();
                })))
        {
            _awaitingSingleEdit = false;
            if (EditLauncher.LastNotice is { } why)
            {
                ShowNotice(why, AppUiTone.Warning);
            }
        }
    }

    // True from the moment a row's Edit opened the pop-over until it closes, so a bulk edit (which reloads through its own save) is not reloaded twice.
    private bool _awaitingSingleEdit;

    private void OnEditorClosed(EditorClosedEventArgs closed)
    {
        if (!_awaitingSingleEdit)
        {
            return;
        }

        _awaitingSingleEdit = false;
        if (closed.Changed)
        {
            _ = InvokeAsync(ReloadAfterBulkSaveAsync);
        }
    }

    private static string NormalizeEditorMediaType(string? mediaType)
    {
        var value = mediaType ?? string.Empty;
        if (value.Contains("movie", StringComparison.OrdinalIgnoreCase))
        {
            return "Movies";
        }

        if (value.Contains("tv", StringComparison.OrdinalIgnoreCase))
        {
            return "TV";
        }

        if (value.Contains("music", StringComparison.OrdinalIgnoreCase))
        {
            return "Music";
        }

        if (value.Contains("audio", StringComparison.OrdinalIgnoreCase))
        {
            return "Audiobooks";
        }

        if (value.Contains("comic", StringComparison.OrdinalIgnoreCase))
        {
            return "Comics";
        }

        return "Books";
    }

    private static bool MediaTypeMatches(string? candidate, string? requested) =>
        string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase)
        || string.Equals(NormalizeEditorMediaType(candidate), NormalizeEditorMediaType(requested), StringComparison.OrdinalIgnoreCase);
}
