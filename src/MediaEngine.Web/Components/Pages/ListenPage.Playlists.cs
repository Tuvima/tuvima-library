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

    private int GetPlaylistOrderIndex(ManagedCollectionViewModel playlist)
    {
        var index = _playlistOrder.IndexOf(playlist.Id);
        return index < 0 ? int.MaxValue : index;
    }

    private async Task MovePlaylistAsync(Guid playlistId, int direction)
    {
        var orderedIds = PlaylistCollections.Select(playlist => playlist.Id).ToList();
        if (!ListenPlaylistOrderState.TryMoveByOffset(orderedIds, playlistId, direction, out var updatedOrder))
        {
            return;
        }

        _playlistOrder = updatedOrder;
        await SavePlaylistOrderAsync();
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
        _newPlaylistName = string.Empty;
    }

    private async Task CreatePlaylistOnKeyDownAsync(KeyboardEventArgs args)
    {
        if (string.Equals(args.Key, "Escape", StringComparison.Ordinal))
        {
            _creatingPlaylistInline = false;
            _newPlaylistName = string.Empty;
            _pendingPlaylistDrop = null;
            return;
        }
        if (!string.Equals(args.Key, "Enter", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(_newPlaylistName))
        {
            return;
        }

        _notice = null;
        if (_activeProfileId is null)
        {
            ShowNotice(L["ListenPage_ProfileRequiredToCreatePlaylists"].Value, AppUiTone.Warning);
            return;
        }
        var name = _newPlaylistName.Trim();
        if (_pendingPlaylistDrop is { } carried)
        {
            // Songs were dropped on New playlist: the playlist is made already holding them, and the sentence goes to their table.
            var made = await SongDrop.CreatePlaylistAsync(name, carried);
            if (made.Created)
            {
                _pendingPlaylistDrop = null;
                _creatingPlaylistInline = false;
                _newPlaylistName = string.Empty;
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
        _newPlaylistName = string.Empty;
        await LoadAsync();
    }

    private void BeginPlaylistRename(ManagedCollectionViewModel playlist)
    {
        _renamingPlaylistId = playlist.Id;
        _renamePlaylistName = playlist.Name;
    }

    private async Task RenamePlaylistOnKeyDownAsync(ManagedCollectionViewModel playlist, KeyboardEventArgs args)
    {
        if (string.Equals(args.Key, "Escape", StringComparison.Ordinal))
        {
            _renamingPlaylistId = null;
            return;
        }
        if (!string.Equals(args.Key, "Enter", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(_renamePlaylistName))
        {
            return;
        }

        _notice = null;
        if (!await ApiClient.UpdateCollectionAsync(
                playlist.Id,
                _renamePlaylistName.Trim(),
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                _activeProfileId))
        {
            ShowNotice(L["ListenPage_PlaylistRenameFailed"].Value, AppUiTone.Error);
            return;
        }
        _renamingPlaylistId = null;
        await LoadAsync();
    }

    // Delete is instant after one "are you sure" (the shared confirm).
    private async Task BeginPlaylistDeleteAsync(ManagedCollectionViewModel playlist)
    {
        _renamingPlaylistId = null;
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
            // Editing is desktop-only; the refusal is told in place rather than the button doing nothing.
            ShowNotice(EditorDeviceRule.Message, AppUiTone.Info);
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

    private async Task AddSelectedTracksToPlaylistAsync(Guid? collectionId = null)
    {
        if (SelectedTrackWorks.Count == 0)
        {
            return;
        }

        if (collectionId.HasValue)
        {
            var collection = PlaylistCollections.FirstOrDefault(item => item.Id == collectionId.Value);
            if (collection is null)
            {
                return;
            }

            await AddTracksToPlaylistAsync(SelectedTrackWorks, collection);
            return;
        }

        await CreatePlaylistAndAddTracksAsync(SelectedTrackWorks);
    }

    private async Task AddTracksToPlaylistAsync(IEnumerable<WorkViewModel> works, ManagedCollectionViewModel collection)
    {
        _notice = null;
        if (!_activeProfileId.HasValue)
        {
            ShowNotice(L["ListenPage_ProfileRequiredToSaveToPlaylists"].Value, AppUiTone.Warning);
            return;
        }

        var added = 0;
        foreach (var work in works)
        {
            if (await ApiClient.AddCollectionItemAsync(collection.Id, work.Id, _activeProfileId.Value))
            {
                added++;
            }
        }

        if (added == 0)
        {
            ShowNotice(L["ListenPage_AddSongsFailed", collection.Name].Value, AppUiTone.Error);
            return;
        }

        await LoadAsync();
    }

    private async Task CreatePlaylistAndAddTracksAsync(IEnumerable<WorkViewModel> works)
    {
        _notice = null;
        if (!_activeProfileId.HasValue)
        {
            ShowNotice(L["ListenPage_ChooseProfileBeforeCreatingPlaylists"].Value, AppUiTone.Warning);
            return;
        }

        var timestamp = DateTimeOffset.Now.ToString("M-d HHmm", CultureInfo.InvariantCulture);
        var created = await ApiClient.CreateCollectionAsync(
            name: $"New Playlist {timestamp}",
            description: "Created from the Listen grid.",
            iconName: AppMaterialIcons.Outlined.QueueMusic,
            collectionType: "Playlist",
            definition: new CollectionRuleDefinitionViewModel(),
            sortField: null,
            sortDirection: "asc",
            visibility: "private",
            profileId: _activeProfileId.Value);

        if (!created)
        {
            ShowNotice(L["ListenPage_DroppedSongsPlaylistFailed"].Value, AppUiTone.Error);
            return;
        }

        await LoadAsync();
        var createdPlaylist = PlaylistCollections.OrderByDescending(collection => collection.CreatedAt).FirstOrDefault();
        if (createdPlaylist is null)
        {
            ShowNotice(L["ListenPage_PlaylistCreatedNotLoaded"].Value, AppUiTone.Warning);
            return;
        }

        await AddTracksToPlaylistAsync(works, createdPlaylist);
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
