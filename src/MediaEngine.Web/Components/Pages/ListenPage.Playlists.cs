using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Browse;
using MediaEngine.Web.Components.Library;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.Pages;

public partial class ListenPage
{
    /// <summary>The playlist header's My List button names what pressing it does (My List spec D14).</summary>
    private string ActivePlaylistSavedLabel => _activePlaylistSaved ? L["MediaTile_RemoveFromMyList"].Value : L["MediaTile_AddToMyList"].Value;

    private ManagedCollectionViewModel? ActivePlaylistCollection => CollectionId.HasValue
        ? PlaylistCollections.FirstOrDefault(collection => collection.Id == CollectionId.Value)
        : null;

    private bool CanSaveActivePlaylist => ActivePlaylistCollection is { } playlist
        && (!_activeProfileId.HasValue || playlist.ProfileId != _activeProfileId);

    private string ActivePlaylistTitle => !string.IsNullOrWhiteSpace(PlaylistKey)
        ? PlaylistKey.ToLowerInvariant() switch
        {
            "all-music" => "All Music",
            "favorite-songs" => L["Glossary_Favorites"].Value,
            "recently-added" => "Recently Added",
            _ => "Playlist",
        }
        : ActivePlaylistCollection?.Name ?? "Playlist";

    // The built-in Favorites list: the profile's hearted songs, kept in an order it can change by hand.
    private bool IsFavoritesPlaylist => string.Equals(PlaylistKey, "favorite-songs", StringComparison.OrdinalIgnoreCase);

    private string ActivePlaylistTypeLabel => !string.IsNullOrWhiteSpace(PlaylistKey)
        ? "System Playlist"
        : ActivePlaylistCollection?.TypeLabel is { Length: > 0 } label
            ? $"{label} Playlist"
            : "Playlist";

    private string? ActivePlaylistDescription => !string.IsNullOrWhiteSpace(PlaylistKey)
        ? PlaylistKey.ToLowerInvariant() switch
        {
            "all-music" => "Every song in your library in one utility view.",
            "favorite-songs" => "Songs this profile has marked as Favorites.",
            "recently-added" => "Fresh music sorted by arrival time.",
            _ => null,
        }
        : ActivePlaylistCollection?.Description;

    private string? ActivePlaylistMeta => !string.IsNullOrWhiteSpace(PlaylistKey)
        ? null
        : ActivePlaylistCollection is null
            ? null
            : $"{ActivePlaylistCollection.ItemCount} items";

    private async Task ToggleActivePlaylistSavedAsync()
    {
        if (ActivePlaylistCollection is null || !CanSaveActivePlaylist)
        {
            return;
        }

        _notice = null;
        var result = await SavedItems.ToggleAsync(ProfileEntityKind.Playlist, ActivePlaylistCollection.Id);
        if (result is null)
        {
            ShowNotice(L["ListenPage_PlaylistMyListFailed"].Value, AppUiTone.Error);
            return;
        }

        _activePlaylistSaved = result.IsSaved;
    }

    // The sidebar's "..." menu: Move up and Move down put the playlist one place earlier or later in the order the profile keeps (spec D3).
    private async Task MovePlaylistAsync(Guid playlistId, int direction)
    {
        var shown = PlaylistCollections;
        var orderedIds = shown.Select(playlist => playlist.Id).ToList();
        if (!ListenPlaylistOrderState.CanMoveWithinGroup(shown, playlistId, direction)
            || !ListenPlaylistOrderState.TryMoveByOffset(orderedIds, playlistId, direction, out var updatedOrder))
        {
            return;
        }

        _playlistOrder = updatedOrder;
        await SavePlaylistOrderAsync();
    }

    private Task MovePlaylistUpAsync(ManagedCollectionViewModel playlist) => MovePlaylistAsync(playlist.Id, -1);

    private Task MovePlaylistDownAsync(ManagedCollectionViewModel playlist) => MovePlaylistAsync(playlist.Id, 1);

    private bool CanMovePlaylist(ManagedCollectionViewModel playlist, int direction)
        => ListenPlaylistOrderState.CanMoveWithinGroup(PlaylistCollections, playlist.Id, direction);

    // ---- Reorder by finger (spec listen-playlists WP5, issue 1201) ----
    // The sidebar's playlists are dragged by their grip (playlist-reorder.js): a finger cannot start the browser's drag. A drop reports the playlist
    // that moved and the one it landed before or after; the order is worked out and saved exactly as for the mouse's drag.

    private ElementReference _playlistRailRoot;
    private string? _playlistRailWiredId;
    private IJSObjectReference? _playlistReorderModule;
    private DotNetObjectReference<ListenPage>? _playlistReorderSelf;

    private async Task WirePlaylistTouchReorderAsync()
    {
        var rootId = !DeviceContext.IsPhone && PlaylistCollections.Count > 1 ? _playlistRailRoot.Id : null;
        if (rootId == _playlistRailWiredId || _disposed)
        {
            return;
        }

        await UnwirePlaylistTouchReorderAsync();
        if (rootId is null)
        {
            return;
        }

        try
        {
            _playlistReorderModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/playlist-reorder.js");
            _playlistReorderSelf ??= DotNetObjectReference.Create(this);
            await _playlistReorderModule.InvokeVoidAsync("attach", _playlistRailRoot, _playlistReorderSelf);
            _playlistRailWiredId = rootId;
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException)
        {
            // The mouse's drag and the menu's Move up and Move down still reorder the playlists.
            Logger.LogWarning(exception, "Dragging a playlist by finger could not start in the Listen sidebar.");
        }
        catch (JSDisconnectedException exception)
        {
            Logger.LogDebug(exception, "The browser went away before the Listen sidebar could start dragging playlists by finger.");
        }
    }

    private async Task UnwirePlaylistTouchReorderAsync()
    {
        var wired = _playlistRailWiredId;
        _playlistRailWiredId = null;
        if (wired is null || _playlistReorderModule is null)
        {
            return;
        }

        try
        {
            await _playlistReorderModule.InvokeVoidAsync("detach", _playlistRailRoot);
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException or ObjectDisposedException or JSDisconnectedException)
        {
            // The sidebar may already be gone from the page; there is nothing left to let go of.
            Logger.LogDebug(exception, "Letting go of the sidebar's finger drag failed.");
        }
    }

    /// <summary>A finger let go of a playlist's grip over another playlist: the script names both and whether it was the lower half.</summary>
    [JSInvokable]
    public Task TouchReorderPlaylistAsync(string from, string to, bool after) => InvokeAsync(async () =>
    {
        if (!Guid.TryParse(from, out var playlistId) || !Guid.TryParse(to, out var targetId))
        {
            return;
        }

        var orderedIds = PlaylistCollections.Select(playlist => playlist.Id).ToList();
        var moved = after
            ? ListenPlaylistOrderState.TryMoveAfter(orderedIds, playlistId, targetId, out var updatedOrder)
            : ListenPlaylistOrderState.TryMoveBefore(orderedIds, playlistId, targetId, out updatedOrder);
        if (!moved)
        {
            return;
        }

        _playlistOrder = updatedOrder;
        await SavePlaylistOrderAsync();
        StateHasChanged();
    });

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await UnwirePlaylistTouchReorderAsync();
        if (_playlistReorderModule is not null)
        {
            try
            {
                await _playlistReorderModule.DisposeAsync();
            }
            catch (Exception exception) when (exception is JSException or InvalidOperationException or ObjectDisposedException or JSDisconnectedException)
            {
                Logger.LogDebug(exception, "The sidebar's script was already gone when the Listen page closed.");
            }

            _playlistReorderModule = null;
        }

        _playlistReorderSelf?.Dispose();
        _playlistReorderSelf = null;
    }

    private void BeginPlaylistReorder(Guid playlistId)
    {
        _draggingPlaylistId = playlistId;
    }

    private void EndPlaylistReorder()
    {
        _draggingPlaylistId = null;
    }

    private async Task DropPlaylistReorderAsync(Guid targetPlaylistId)
    {
        if (!_draggingPlaylistId.HasValue || _draggingPlaylistId.Value == targetPlaylistId)
        {
            return;
        }

        var orderedIds = PlaylistCollections.Select(playlist => playlist.Id).ToList();
        if (!ListenPlaylistOrderState.TryMoveBefore(
                orderedIds,
                _draggingPlaylistId.Value,
                targetPlaylistId,
                out var updatedOrder))
        {
            return;
        }

        _playlistOrder = updatedOrder;
        _draggingPlaylistId = null;
        await SavePlaylistOrderAsync();
    }

    private void BeginPlaylistInlineCreate()
    {
        _creatingPlaylistInline = true;
    }

    // Escape, or moving away from the name box, closes it; songs dropped on New playlist are let go with it.
    private void CancelPlaylistInlineCreate()
    {
        _creatingPlaylistInline = false;
        _pendingPlaylistDrop = null;
    }

    // Enter or the Create button in the name box.
    private async Task CreatePlaylistNamedAsync(string name)
    {
        _notice = null;
        if (_activeProfileId is null)
        {
            ShowNotice(L["ListenPage_ProfileRequiredToCreatePlaylists"].Value, AppUiTone.Warning);
            return;
        }

        if (_pendingPlaylistDrop is { } carried)
        {
            // Songs were dropped on New playlist: the playlist is made already holding them, and the sentence goes to their table.
            var made = await SongDrop.CreatePlaylistAsync(name, carried);
            if (made.Created)
            {
                _pendingPlaylistDrop = null;
                _creatingPlaylistInline = false;
                await LoadAsync();
            }

            return;
        }

        var createdId = await ApiClient.CreateCollectionWithItemsAsync(
            name,
            null,
            AppMaterialIcons.Outlined.QueueMusic,
            "Playlist",
            new CollectionRuleDefinitionViewModel(),
            "title",
            "asc",
            "private",
            [],
            _activeProfileId);
        if (createdId is null)
        {
            ShowNotice(L["ListenPage_PlaylistCreateFailed"].Value, AppUiTone.Error);
            return;
        }

        _creatingPlaylistInline = false;
        await LoadAsync();
    }

    // Delete is instant after one "are you sure" (the shared confirm).
    private async Task BeginPlaylistDeleteAsync(ManagedCollectionViewModel playlist)
    {
        var confirmed = await Dialogs.ShowMessageBoxAsync(
            L["Common_Delete2", playlist.Name].Value,
            string.Empty,
            yesText: L["Common_Delete"].Value,
            cancelText: L["Search_Cancel"].Value,
            options: AppDialogOptions.DestructiveConfirm);
        if (confirmed == true)
        {
            await DeletePlaylistAsync(playlist);
        }
    }

    private async Task DeletePlaylistAsync(ManagedCollectionViewModel playlist)
    {
        _notice = null;
        if (!await ApiClient.DeleteCollectionAsync(playlist.Id, _activeProfileId))
        {
            ShowNotice(L["ListenPage_PlaylistDeleteFailed"].Value, AppUiTone.Error);
            return;
        }
        if (CollectionId == playlist.Id)
        {
            Nav.NavigateTo("/listen/playlists");
            return;
        }
        await LoadAsync();
    }

    private async Task OpenPlaylistCreateDialog(string kind)
    {
        if (_activeProfileId is null)
        {
            ShowNotice(L["ListenPage_ProfileRequiredToCreatePlaylists"].Value, AppUiTone.Warning);
            return;
        }

        _notice = null;
        var isSmart = string.Equals(kind, "Smart", StringComparison.OrdinalIgnoreCase);
        var entry = CollectionEditorEntryPoints.For(CollectionEditorEntryPoints.Listen, _activeProfileId);
        // What the editor saved is told by one quiet line; there is no toast.
        _savedCollectionCount = 0;
        var request = new CollectionEditorLaunchRequest
        {
            ActiveProfileId = entry.ActiveProfileId,
            Kind = entry.Kind,
            InitialPrimaryArea = entry.InitialPrimaryArea,
            InitialMediaTypes = entry.InitialMediaTypes,
            InitialMembershipMode = isSmart ? "Smart" : "Manual",
            InitialTitle = string.Empty,
        };
        var launch = await CollectionEditorLauncher.TryOpenAsync(request);
        if (launch == EditorLaunchResult.NotAvailableOnThisDevice)
        {
            // A device that may not edit items (a TV) is told in place rather than the button doing nothing.
            ShowNotice(EditorDeviceRule.ItemMessage, AppUiTone.Info);
            return;
        }

        var changed = launch == EditorLaunchResult.Applied;
        _savedCollectionCount = request.SavedCollectionIds.Count;

        if (changed || _savedCollectionCount > 0)
        {
            await LoadAsync();
        }
    }

    private int _savedCollectionCount;
    private async Task SavePlaylistOrderAsync()
    {
        if (_activeProfile is null)
        {
            return;
        }

        _notice = null;
        var updatedConfig = ListenPlaylistOrderState.Write(_playlistNavigationConfig, _playlistOrder);
        var saved = await Orchestrator.UpdateProfileAsync(
            _activeProfile.Id,
            _activeProfile.DisplayName,
            _activeProfile.AvatarColor,
            updatedConfig);

        if (saved)
        {
            _playlistNavigationConfig = updatedConfig;
        }
        else
        {
            ShowNotice(L["ListenPage_PlaylistOrderSaveFailed"].Value, AppUiTone.Warning, urgent: true);
        }
    }

    // Dragged songs dropped on a hand-made playlist in the rail: the add, the skip of songs already there and the sentence come from the one
    // drop service; the table the songs came from shows the sentence in place.
    private async Task DropSongsOnPlaylistAsync(ManagedCollectionViewModel playlist)
    {
        if (AcceptsSongDrops(playlist))
        {
            await SongDrop.DropOnPlaylistAsync(playlist.Id, playlist.Name);
        }
    }

    // Songs dropped on the Playlists heading (New playlist): they wait while the rail asks for a name, then go into the new playlist.
    private void DropSongsOnNewPlaylist()
    {
        if (SongDrop.Take() is { } carried)
        {
            _pendingPlaylistDrop = carried;
            BeginPlaylistInlineCreate();
        }
    }

    private static string PlaylistIconFor(ManagedCollectionViewModel collection)
        => collection.CollectionType switch
        {
            "Smart" => AppMaterialIcons.Outlined.AutoAwesomeMotion,
            "PlaylistFolder" => AppMaterialIcons.Outlined.Folder,
            "Mix" => AppMaterialIcons.Outlined.GraphicEq,
            "System" => AppMaterialIcons.Outlined.SettingsSuggest,
            _ => AppMaterialIcons.Outlined.QueueMusic,
        };

    private static bool CanAcceptPlaylistItems(ManagedCollectionViewModel collection)
        => string.Equals(collection.CollectionType, "Playlist", StringComparison.OrdinalIgnoreCase);

    // The playlists dragged songs can be dropped on: exactly the ones the Add to playlist list offers (a rule-based or system list never).
    private static bool AcceptsSongDrops(ManagedCollectionViewModel collection)
        => CollectionAuthoringRule.IsAddTarget(collection, playlist: true);
}
