using System.Text.Json;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.Player;

public partial class ListenNowPlayingBar
{
    // The dock's panels and notices: the compact context panel, the inline Now playing tabs, the Settings menu state, the Undo line after a status change and the one-shot notice.

    private UndoOffer? _statusUndo;
    private Guid? _statusUndoWorkId;
    private Task ShowStatusUndo(UndoOffer offer) { _statusUndo = offer; _statusUndoWorkId = Playback.CurrentItem?.WorkId; return Task.CompletedTask; }
    private Task ClearStatusUndo() { _statusUndo = null; return Task.CompletedTask; }

    // The offer belongs to the item it was made on: when the playing item changes, it is gone.
    private UndoOffer? StatusUndo
    {
        get
        {
            if (_statusUndo is not null && _statusUndoWorkId != Playback.CurrentItem?.WorkId)
            {
                _statusUndo = null;
            }

            return _statusUndo;
        }
    }

    private PlaybackPanel? _compactContext;
    private ElementReference? _compactToolTrigger;
    private string _compactPanelKey = "queue";
    private bool _openCompactToolPending;
    private string ContextToolTitle => _compactPanelKey switch { "lyrics" => "Lyrics", "chapters" => L["Common_Chapters"].Value, "history" => Playback.IsMusicMode ? L["Common_Queue"].Value : "History", _ => L["Common_Queue"].Value };
    private bool CompactIsList => _compactPanelKey is "queue" or "chapters" || _compactPanelKey == "history" && Playback.IsMusicMode;

    /// <summary>
    /// The panel kind the compact tool panel is drawn as. A music "history" request is the Queue panel opened on its History tab, so it gets the Queue's
    /// 440 x 440 size (mockup screen 8); an audiobook's history is the ordinary listening-history list.
    /// </summary>
    public static string CompactPanelKind(string key, bool isMusic) => key switch
    {
        "lyrics" => "lyrics",
        "queue" => "queue",
        "chapters" => "chapters",
        "history" when isMusic => "queue",
        _ => "ordinary",
    };
    private bool HasUsableChapters => CurrentChapters.Count > 1;
    private bool IsCurrentDockSnapshot(ListenPlaybackSnapshot snapshot) => !_disposed && snapshot.ProfileId == Playback.ActiveProfileId
        && snapshot.PlaybackRequestVersion == Playback.PlaybackRequestVersion && snapshot.CurrentIndex >= 0 && snapshot.CurrentIndex < snapshot.Queue.Count
        && snapshot.Queue[snapshot.CurrentIndex].QueueEntryId == Playback.CurrentItem?.QueueEntryId;
    private string DockItemKey => $"{Playback.ActiveProfileId}:{Playback.CurrentItem?.AssetId}:{Playback.PlaybackRequestVersion}";
    private PlaybackTransientToolCoordinator? Tools => Services.GetService<PlaybackTransientToolCoordinator>();
    private IPlaybackCommandSink PanelCommands => Services.GetRequiredService<IPlaybackCommandSink>();
    // Now playing on a desktop is the routed page (unified player v2 D24); its tabs are chosen by the bar's Lyrics, Queue and Chapters tools too.
    private bool IsNowPlayingPage => NowPlayingRoute.IsNowPlaying(RoutedPageAddress.AbsoluteFor(Nav, Overlay));
    private bool IsDesktopNowPlaying => IsNowPlayingPage && !IsPhonePresentation;
    // While the Now playing page is open the bar follows the page's tab: a tool is pressed only for the tab the page shows, and none is pressed when the
    // page opens on its default tab or shows About (mockup screen 13 presses only Now playing).
    private string? PressedInlineKey => IsDesktopNowPlaying ? AudioSidebar.NowPlayingTab : InlinePanelKey;
    private string InlinePanelKey => Playback.IsAudiobookMode && !HasUsableChapters ? "history" : AudioSidebar.For(Playback).ActivePanelKey ?? (Playback.IsAudiobookMode ? "chapters" : "queue");
    private void ObserveLyricsPresentation()
    {
        if (Services.GetService<PlaybackLyricsPresenter>() is { } presenter)
        {
            presenter.Observe(Playback.CreateSnapshot(), PanelCommands);
        }
    }
    private void HandleAudioPanelChanged() => _ = InvokeAsync(() => { ObserveLyricsPresentation(); StateHasChanged(); });
    private async Task SelectInlinePanelAsync(string key)
    {
        Tools?.Close(); CloseSettingsMenu();
        await SelectDockTabAsync(key);
        await InvokeAsync(StateHasChanged);
    }
    private Task SelectDockTabAsync(string key) => AudioSidebar.OpenPanelAsync(Playback.IsAudiobookMode ? "desktop:audiobook" : "desktop:music", key);
    private async Task DockToolChangedAsync(string key, bool open)
    {
        if (open) { CloseSettingsMenu(); await SelectDockTabAsync(key); }
        else if (Tools?.OpenToolId is null)
        {
            await AudioSidebar.CloseAsync(Playback);
        }
    }
    private async Task OpenDockPanelAsync(string key, ElementReference? trigger = null)
    {
        if (IsDesktopNowPlaying) { await SelectInlinePanelAsync(key); return; }
        Tools?.Close();
        _compactPanelKey = key;
        _compactToolTrigger = trigger ?? _shell?.ArtworkElement;
        await SelectDockTabAsync(key);
        _openCompactToolPending = _compactToolTrigger is not null;
        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleMusicPanelControl(PlaybackControlDefinition control)
    {
        if (control.IsDisabled)
        {
            return;
        }

        CloseSettingsMenu();

        switch (control.Key)
        {
            case PlaybackControlKey.Queue:
                await OpenDockPanelAsync("queue");
                break;
            case PlaybackControlKey.History:
                await OpenDockPanelAsync("history");
                break;
            case PlaybackControlKey.Lyrics:
                await OpenDockPanelAsync("lyrics");
                break;
            case PlaybackControlKey.Shuffle:
                await Playback.ToggleShuffleAsync();
                break;
            case PlaybackControlKey.Repeat:
                await Playback.CycleRepeatModeAsync();
                break;
        }
    }

    private void HandlePlayerKeyDown(KeyboardEventArgs args)
    {
        if (string.Equals(args.Key, "Escape", StringComparison.Ordinal) && Tools?.IsPinnedFor("audio-lyrics") != true
            && Tools?.IsPinnedFor("audio-queue") != true && Tools?.IsPinnedFor("audio-chapters") != true)
        {
            Tools?.Close();
        }
    }

    private async Task SetAudiobookSpeedAsync(double rate)
    {
        await Playback.SetPlaybackRateAsync(rate);
    }

    // Playback settings (unified player v2 D14): a new skip length is saved on the profile and the bar's skip buttons take it at once.
    private async Task ChangeSkipLengthAsync(PlaybackSkipChange change)
    {
        if (Services.GetService<PlaybackSettingsActions>() is { } actions && await actions.SetSkipLengthAsync(change))
        {
            return;
        }

        _barNotice = L["PlaybackSettings_NotSaved"].Value;
    }

    // "Reset playback preferences": the skip lengths go back to the defaults and audiobooks play at normal speed.
    private async Task ResetPlaybackPreferencesAsync()
    {
        if (Playback.IsAudiobookMode)
        {
            await Playback.SetPlaybackRateAsync(PlaybackSpeedChoices.Normal);
        }

        if (Services.GetService<PlaybackSettingsActions>() is { } actions && await actions.ResetSkipLengthsAsync())
        {
            return;
        }

        _barNotice = L["PlaybackSettings_NotSaved"].Value;
    }

    private async Task HandleSettingsMenuOpenChangedAsync(PlaybackToolOpenState state)
    {
        if (!state.IsOpen)
        {
            if (_settingsMenuSurface == state.Surface)
            {
                CloseSettingsMenu();
            }

            return;
        }

        _settingsMenuOpen = true;
        _settingsMenuSurface = state.Surface;
        _transientPlaybackRequestVersion = Playback.PlaybackRequestVersion;
    }

    private void CloseSettingsMenu()
    {
        _settingsMenuOpen = false;
        _settingsMenuSurface = null;
        _settingsMenuTool = null;
    }

    // The bar's Speed panel and Sleep timer panel share this state, so only the pressed one is open.
    private async Task HandleBarSettingsOpenChangedAsync(string tool, PlaybackToolOpenState state)
    {
        if (!state.IsOpen && _settingsMenuTool != tool)
        {
            return;
        }

        await HandleSettingsMenuOpenChangedAsync(state);
        if (state.IsOpen)
        {
            _settingsMenuTool = tool;
        }
    }

    private async Task BookmarksPanelOpenChangedAsync(bool open)
    {
        if (!open)
        {
            return;
        }

        CloseSettingsMenu();
        await RefreshBarMarkersAsync(force: true);
    }

    private async Task QueueDraggedItemsAsync()
    {
        if (!AudioDrag.HasItems)
        {
            return;
        }

        if (Playback.IsAudiobookMode)
        {
            _barNotice = L["ListenNowPlayingBar_FinishOrCloseTheAudiobook"].Value;
            AudioDrag.Clear();
            return;
        }

        var items = AudioDrag.Items.ToList();
        foreach (var item in items)
        {
            await Playback.AddQueueItemAsync(item);
        }

        // The queue shows the dropped songs; that is the confirmation.
        AudioDrag.Clear();
    }

    // Why the last favorite, setting or drop did not work; shown beside the restore line until the next try or the close button.
    private string? _barNotice;

    private void DismissBarNotice() => _barNotice = null;
}
