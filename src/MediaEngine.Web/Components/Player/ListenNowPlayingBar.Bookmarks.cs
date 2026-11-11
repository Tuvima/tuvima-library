using System.Text.Json;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.Player;

public partial class ListenNowPlayingBar
{
    // Bookmarks and the timeline: the Chapter or Book view, the markers on the rail, and adding, editing and deleting a bookmark.

    // Unified player D5: an audiobook's bar never draws chapter ticks (ticks are for bookmarks). It shows the current chapter by default,
    // or the whole book when the person picks "Book"; the Chapters list is the way to jump around.
    // The choice is a profile preference shared with the Chapters panel's footer control (PlaybackTimelineScope), so a change in either place shows in both.
    private PlaybackTimelineScope? _ownTimelineScope;
    private PlaybackTimelineScope TimelineScope => Services.GetService<PlaybackTimelineScope>() ?? (_ownTimelineScope ??= new PlaybackTimelineScope(JS, Playback));
    private bool _bookView => TimelineScope.WholeBook;
    private AudiobookChapterWindow? AudiobookWindow => Playback.IsAudiobookMode
        ? AudiobookChapterWindow.Find(CurrentChapters, Playback.CurrentTimeSeconds, Playback.DurationSeconds)
        : null;
    private AudiobookChapterWindow? ChapterScope => _bookView ? null : AudiobookWindow;

    // Unified player D10: "Show bookmarks on the bar" in the audiobook Settings menu is the profile's own setting, the same one the video player's
    // bookmark pane and Settings > Playback use. Off by default; while it is on the bar draws the book file's bookmarks (this chapter's, in the chapter view).
    private UserPlaybackSettingsDto? _playerSettings;
    private IReadOnlyList<AudiobookBookmarkDto> _barBookmarks = [];
    private (Guid Profile, Guid Work, Guid Asset)? _barMarkersKey;
    private int _barMarkersVersion;
    private bool ShowBookmarksOnBar => _playerSettings?.General.ShowBookmarksOnBar == true;
    // An audiobook with a file, or a music track with a file while a profile is active (unified player v2 D3, D21, D26): music Bookmarks are the track's
    // own, keyed by the track's work and file.
    private bool CanBookmarkCurrent => Playback.CurrentItem?.AssetId is { } assetId && assetId != Guid.Empty
        && (Playback.IsAudiobookMode || (Playback.IsMusicMode && PlaybackPreferences.ActiveProfileId is { } profile && profile != Guid.Empty));
    private string BookmarkChoice => _playerSettings?.General.BookmarkFilter ?? PlaybackPreferenceValues.All;
    private IReadOnlyList<PlaybackRailBookmark> BarRailBookmarks => PlaybackRailBookmark.From(_barBookmarks, Playback.DurationSeconds);

    private IReadOnlyList<PlaybackRailBookmark> BarMarkers(AudiobookChapterWindow? scope) =>
        CanBookmarkCurrent && _barMarkersKey is { } key && key.Asset == Playback.CurrentItem?.AssetId
            ? AudiobookBarMarkers.ForBar(_barBookmarks, ShowBookmarksOnBar, BookmarkChoice, Playback.DurationSeconds, scope)
            : [];

    /// <summary>Loads the profile's playback settings and, while the markers are on, the book file's bookmarks, once per book file (or when forced).</summary>
    private async Task RefreshBarMarkersAsync(bool force = false)
    {
        if (_disposed || !CanBookmarkCurrent || Playback.CurrentItem is not { AssetId: Guid assetId } item
            || PlaybackPreferences.ActiveProfileId is not Guid profileId)
        {
            return;
        }

        var workId = item.AudiobookWorkId ?? item.WorkId;
        var key = (profileId, workId, assetId);
        if (!force && _barMarkersKey == key)
        {
            return;
        }

        _barMarkersKey = key;
        var version = Interlocked.Increment(ref _barMarkersVersion);
        try
        {
            _playerSettings ??= await PlaybackPreferences.GetAsync();
            var saved = await AudiobookBarMarkers.LoadAsync(Services.GetService<IEngineApiClient>(), workId, profileId, assetId, CancellationToken.None);
            if (_disposed || version != _barMarkersVersion)
            {
                return;
            }

            _barBookmarks = saved;
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException)
        {
            Logger.LogInformation(ex, "The bookmark markers for the bar could not be loaded; the bar shows none.");
        }
    }

    /// <summary>
    /// Switches the audiobook bar between the current chapter and the whole book and remembers the choice for this profile. The label on the timeline
    /// and the Chapters panel's footer control (Chapter | Whole book) both call it, through the one shared preference.
    /// </summary>
    public Task SetBarScopeAsync(bool wholeBook) => TimelineScope.SetWholeBookAsync(wholeBook);

    private void TimelineScopeChanged() => _ = InvokeAsync(StateHasChanged);

    /// <summary>
    /// Saves a bookmark at the moment the button was pressed, with an optional note (unified player v2 D21). The press asks the playback owner for the
    /// position through a fresh lease, saves it, and releases the lease, so a stale press (the book, profile or session changed) saves nothing.
    /// </summary>
    private async Task<bool> AddAudiobookBookmarkAsync(double seconds, string? note)
    {
        if (Playback.IsMusicMode)
        {
            return await AddMusicBookmarkAsync(seconds, note);
        }

        if (!Playback.IsAudiobookMode || Playback.CurrentItem is not { AssetId: Guid assetId } item
            || assetId == Guid.Empty || PlaybackPreferences.ActiveProfileId is not Guid profileId
            || Playback.AudiobookBookSessionLeaseId is not Guid sessionLeaseId)
        {
            return false;
        }

        var context = new AudiobookBookmarkActionContext(Guid.NewGuid(), profileId,
            item.AudiobookWorkId ?? item.WorkId, sessionLeaseId, Playback.AudiobookBookSessionGeneration, assetId);
        try
        {
            await BookmarkActions.OpenAsync(context);
            var snapshot = await BookmarkActions.GetSnapshotAsync(context);
            var authorized = BookmarkActions is ListenPlaybackCommandActionsClient client
                ? client.GetAuthorizedAssetIds(context)
                : new HashSet<Guid>();
            if (snapshot.Draft is not { } draft || !authorized.Contains(assetId))
            {
                return false;
            }

            var result = await BookmarkActions.SaveWithFieldsAsync(context, draft.Generation, authorized,
                new AudiobookBookmarkSaveFields(note, null, seconds));
            await RefreshBarMarkersAsync(force: true);
            return result.Outcome == AudiobookBookmarkOperationOutcome.Success;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "The audiobook position could not be bookmarked by the active playback owner.");
            return false;
        }
        finally
        {
            try { await BookmarkActions.CloseAsync(context); }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "The bookmark lease could not be released; it was probably released already.");
            }
        }
    }

    /// <summary>
    /// Saves a bookmark on the playing music track at the moment the button was pressed. A song has no chapters and no owner lease, so the press goes
    /// straight to the Engine, which checks the profile and the file.
    /// </summary>
    private async Task<bool> AddMusicBookmarkAsync(double seconds, string? note)
    {
        if (!CanBookmarkCurrent || Playback.CurrentItem is not { AssetId: Guid assetId } item
            || PlaybackPreferences.ActiveProfileId is not Guid profileId
            || Services.GetService<IEngineApiClient>() is not { } api
            || !double.IsFinite(seconds) || seconds < 0)
        {
            return false;
        }

        var duration = Playback.DurationSeconds;
        var request = new CreateAudiobookBookmarkRequestDto
        {
            ProfileId = profileId,
            AssetId = assetId,
            PositionSeconds = duration > 0 ? Math.Min(seconds, duration) : seconds,
            DurationSeconds = duration > 0 ? duration : null,
            Note = note,
        };
        var result = await api.CreateAudiobookBookmarkWithOutcomeAsync(item.WorkId, request, profileId);
        if (result.Outcome != AudiobookBookmarkOperationOutcome.Success)
        {
            return false;
        }

        await RefreshBarMarkersAsync(force: true);
        return true;
    }

    /// <summary>Renames the person's own bookmark and changes its note; the Engine refuses another profile's row.</summary>
    private async Task<bool> EditAudiobookBookmarkAsync(PlaybackRailBookmark bookmark, string? name, string? note)
    {
        if (PlaybackPreferences.ActiveProfileId is not Guid profileId || Services.GetService<IEngineApiClient>() is not { } api)
        {
            return false;
        }

        var result = await api.UpdateAudiobookBookmarkWithOutcomeAsync(bookmark.Id, new UpdateAudiobookBookmarkRequestDto { Label = name, Note = note }, profileId);
        if (result.Outcome != AudiobookBookmarkOperationOutcome.Success)
        {
            return false;
        }

        await RefreshBarMarkersAsync(force: true);
        return true;
    }

    private async Task<bool> DeleteAudiobookBookmarkAsync(PlaybackRailBookmark bookmark)
    {
        if (PlaybackPreferences.ActiveProfileId is not Guid profileId || Services.GetService<IEngineApiClient>() is not { } api)
        {
            return false;
        }

        var result = await api.DeleteAudiobookBookmarkWithOutcomeAsync(bookmark.Id, profileId);
        if (result.Outcome != AudiobookBookmarkOperationOutcome.Success)
        {
            return false;
        }

        await RefreshBarMarkersAsync(force: true);
        return true;
    }

    private async Task ChangeBookmarkChoiceAsync(string choice)
    {
        _playerSettings ??= new UserPlaybackSettingsDto();
        _playerSettings.General.BookmarkFilter = choice;
        await SaveBookmarkPreferenceAsync(settings => settings.General.BookmarkFilter = choice);
    }

    private async Task ChangeShowBookmarksOnBarAsync(bool show)
    {
        _playerSettings ??= new UserPlaybackSettingsDto();
        _playerSettings.General.ShowBookmarksOnBar = show;
        await SaveBookmarkPreferenceAsync(settings => settings.General.ShowBookmarksOnBar = show);
    }

    private async Task SaveBookmarkPreferenceAsync(Action<UserPlaybackSettingsDto> change)
    {
        await InvokeAsync(StateHasChanged);
        if (Services.GetService<PlayerPreferenceService>() is not { } preferences)
        {
            return;
        }

        try { await preferences.UpdateAsync(change); }
        catch (OperationCanceledException) { Logger.LogDebug("Saving the bookmark choice was cancelled."); }
        catch (Exception ex) { Logger.LogWarning(ex, "The bookmark choice could not be saved."); }
    }
}
