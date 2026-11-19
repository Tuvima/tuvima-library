using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Components.Pages;

// The playlist actions of the Listen lane (spec listen-playlists WP3): the Playlists page's New playlist and its tiles' "..." menu, and the same
// menu on the sidebar's rows. One set of handlers serves both, so a playlist behaves the same wherever its menu is opened.
public partial class ListenBrowsePage
{
    // Hand-picked: the playlist is made empty with the name typed and the page shows it as a new tile.
    private async Task CreateHandPickedAsync(string name)
    {
        if (!await MakeHandPickedPlaylistAsync(name))
        {
            return;
        }

        _notice = null;
        await ReloadPlaylistsAsync();
    }

    private async Task<bool> MakeHandPickedPlaylistAsync(string name)
    {
        if (_activeProfileId is null)
        {
            ShowNotice(L["ListenPage_ProfileRequiredToCreatePlaylists"].Value, AppUiTone.Warning);
            return false;
        }

        var playlistId = await ApiClient.CreateCollectionWithItemsAsync(
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
        if (playlistId is null)
        {
            ShowNotice(L["ListenBrowsePage_ThePlaylistCouldNotBe"].Value, AppUiTone.Error);
            return false;
        }

        return true;
    }

    // Smart: the rule editor opens with the name filled in (a phone opens it as its full-screen step, like a computer's window).
    private Task CreateSmartAsync(string name) => OpenSmartEditorAsync(name);

    private async Task OpenSmartEditorAsync(string name)
    {
        if (_activeProfileId is null)
        {
            ShowNotice(L["ListenPage_ProfileRequiredToCreatePlaylists"].Value, AppUiTone.Warning);
            return;
        }

        // What the editor saved is told by one quiet line; there is no toast.
        _savedCollectionCount = 0;
        var entry = CollectionEditorEntryPoints.For(CollectionEditorEntryPoints.Listen, _activeProfileId);
        var request = new CollectionEditorLaunchRequest
        {
            ActiveProfileId = entry.ActiveProfileId,
            Kind = entry.Kind,
            InitialPrimaryArea = entry.InitialPrimaryArea,
            InitialMediaTypes = entry.InitialMediaTypes,
            InitialMembershipMode = "Smart",
            InitialTitle = name,
        };
        await LaunchEditorAsync(request);
    }

    private async Task EditPlaylistRulesAsync(ManagedCollectionViewModel playlist)
    {
        _notice = null;
        var entry = CollectionEditorEntryPoints.For(CollectionEditorEntryPoints.Listen, _activeProfileId);
        await LaunchEditorAsync(new CollectionEditorLaunchRequest
        {
            EditingCollection = PlaylistMenuRules.EditorItemFor(playlist),
            ActiveProfileId = entry.ActiveProfileId,
            Kind = entry.Kind,
            InitialPrimaryArea = entry.InitialPrimaryArea,
            InitialMediaTypes = entry.InitialMediaTypes,
        });
    }

    private async Task LaunchEditorAsync(CollectionEditorLaunchRequest request)
    {
        var launch = await CollectionEditorLauncher.TryOpenAsync(request);
        if (launch == EditorLaunchResult.NotAvailableOnThisDevice)
        {
            // A device that may not edit items (a TV) is told in place rather than the button doing nothing.
            ShowNotice(EditorDeviceRule.ItemMessage, AppUiTone.Info);
            return;
        }

        _savedCollectionCount = request.SavedCollectionIds.Count;
        if (launch == EditorLaunchResult.Applied || _savedCollectionCount > 0)
        {
            await ReloadPlaylistsAsync();
        }
    }

    // Rename and Edit description and cover are one small window (a bottom sheet on a phone and a touch tablet); the page reads the playlists
    // again when something was saved.
    private Task RenamePlaylistWindowAsync(ManagedCollectionViewModel playlist) => ShowPlaylistEditWindowAsync(playlist, PlaylistEditMode.Rename);

    private Task EditPlaylistDetailsAsync(ManagedCollectionViewModel playlist) => ShowPlaylistEditWindowAsync(playlist, PlaylistEditMode.Details);

    private async Task ShowPlaylistEditWindowAsync(ManagedCollectionViewModel playlist, PlaylistEditMode mode)
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
            await ReloadPlaylistsAsync();
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

        Nav.NavigateTo($"/listen/music/playlists/{copy.Id}");
    }

    // Pin to top and Unpin belong to this profile only; the playlist moves to the first place (or back) at once.
    private async Task TogglePlaylistPinAsync(ManagedCollectionViewModel playlist)
    {
        _notice = null;
        if (!await ApiClient.SetCollectionPinnedAsync(playlist.Id, playlist.IsPinned != true, _activeProfileId))
        {
            ShowNotice(L["PlaylistPage_PinFailed"].Value, AppUiTone.Error);
            return;
        }

        await ReloadPlaylistsAsync();
    }

    // Delete asks once, in the shared confirm, then happens at once.
    private async Task DeletePlaylistWithAskAsync(ManagedCollectionViewModel playlist)
    {
        var confirmed = await Dialogs.ShowMessageBoxAsync(
            L["Common_Delete2", playlist.Name].Value,
            string.Empty,
            yesText: L["Common_Delete"].Value,
            cancelText: L["Search_Cancel"].Value,
            options: AppDialogOptions.DestructiveConfirm);
        if (confirmed != true)
        {
            return;
        }

        _notice = null;
        if (!await ApiClient.DeleteCollectionAsync(playlist.Id, _activeProfileId))
        {
            ShowNotice(L["ListenBrowsePage_ThePlaylistCouldNotBe3"].Value, AppUiTone.Error);
            return;
        }

        await ReloadPlaylistsAsync();
        if (new Uri(RoutedPageAddress.AbsoluteFor(Nav, Overlay)).AbsolutePath.Contains(playlist.Id.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            Nav.NavigateTo(ListenNavigation.PlaylistsRoute);
        }
    }

    // The rail's Reorder row (the rail keeps its own reorder mode) only when that row can be reordered.
    private EventCallback<ManagedCollectionViewModel> ReorderCallback(PlaylistRailMenu menu)
        => menu.Reorder is { } reorder
            ? EventCallback.Factory.Create<ManagedCollectionViewModel>(this, _ => reorder())
            : default;
}
