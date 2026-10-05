using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Services.Integration;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Services.Playback;

public sealed class ListenPlaybackCommandOwner(IServiceProvider services, PlaybackSessionController playback)
{
    public Guid RecipientId { get; } = Guid.NewGuid();
    public Func<ListenPlaybackCommandDto, string, Task<bool>>? NavigateIdentityAsync { get; set; }
    public Func<Guid, Task<bool>>? ValidatePopupWindowAsync { get; set; }
    private (Guid Sender, Guid Window, long Generation)? _popupRegistration;
    private long _lastPopupRegistrationGeneration;
    private readonly SemaphoreSlim _transportGate = new(1, 1);
    private readonly SemaphoreSlim _presentationGate = new(1, 1);
    private readonly Dictionary<(Guid Sender, Guid Command), ListenPlaybackCommandReplyDto> _presentationReplies = [];
    private readonly Dictionary<(Guid Sender, Guid Command), ListenPlaybackCommandReplyDto?> _completed = [];
    private readonly Queue<(Guid Sender, Guid Command)> _completedOrder = [];

    public async Task<ListenPlaybackCommandReplyDto?> HandleAsync(ListenPlaybackCommandDto command,
        CancellationToken ct = default)
    {
        if (command.RecipientId != RecipientId || command.CommandId == Guid.Empty || command.SenderId == Guid.Empty)
            return null;
        if (IsBookmarkAction(command.Action))
            return await services.GetRequiredService<AudiobookBookmarkCommandDispatcher>().HandleAsync(command, ct).ConfigureAwait(false);
        if (command.Action is ListenPlaybackPresentationActions.SelectLyrics or ListenPlaybackPresentationActions.NavigateIdentity
            or ListenPlaybackPresentationActions.RegisterPopup or ListenPlaybackCommandActions.PopupClosed)
        {
            await _presentationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var key = (command.SenderId, command.CommandId);
                if (_presentationReplies.TryGetValue(key, out var previous)) return previous;
                var accepted = false;
                if (command.Action == ListenPlaybackPresentationActions.SelectLyrics)
                    accepted = await services.GetRequiredService<PlaybackLyricsSelectionOwner>()
                        .SelectAsync(RecipientId, command, ct).ConfigureAwait(false);
                else if (command.Action == ListenPlaybackPresentationActions.NavigateIdentity && NavigateIdentityAsync is { } navigate)
                {
                    var route = await services.GetRequiredService<PlaybackIdentityNavigationOwner>().ResolveAsync(command, ct).ConfigureAwait(false);
                    if (route is not null) accepted = await navigate(command, route).ConfigureAwait(false);
                }
                else if (command.Action == ListenPlaybackPresentationActions.RegisterPopup && command.PopupWindowId is { } window
                    && window != Guid.Empty && command.OwnerGeneration > _lastPopupRegistrationGeneration
                    && ValidatePopupWindowAsync is { } validate && await validate(window).ConfigureAwait(false))
                {
                    _lastPopupRegistrationGeneration = command.OwnerGeneration;
                    _popupRegistration = (command.SenderId, window, command.OwnerGeneration);
                    playback.SetPopupOpen(true);
                    accepted = true;
                }
                else if (command.Action == ListenPlaybackCommandActions.PopupClosed && command.PopupWindowId is { } closed
                    && _popupRegistration == (command.SenderId, closed, command.OwnerGeneration))
                {
                    _popupRegistration = null;
                    playback.SetPopupOpen(false);
                    accepted = true;
                }
                var reply = new ListenPlaybackCommandReplyDto { CommandId = command.CommandId,
                    RecipientId = command.SenderId, BooleanResult = accepted,
                    Outcome = accepted ? AudiobookBookmarkOperationOutcomes.Success : AudiobookBookmarkOperationOutcomes.DefiniteFailure };
                if (_presentationReplies.Count >= 256) _presentationReplies.Remove(_presentationReplies.Keys.First());
                _presentationReplies[key] = reply;
                return reply;
            }
            finally { _presentationGate.Release(); }
        }
        await _transportGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var key = (command.SenderId, command.CommandId);
            if (_completed.TryGetValue(key, out var previous)) return previous;
            var reply = await DispatchTransportAsync(command, ct).ConfigureAwait(false);
            _completed[key] = reply;
            _completedOrder.Enqueue(key);
            while (_completedOrder.Count > 256) _completed.Remove(_completedOrder.Dequeue());
            return reply;
        }
        finally { _transportGate.Release(); }
    }

    private async Task<ListenPlaybackCommandReplyDto?> DispatchTransportAsync(ListenPlaybackCommandDto command, CancellationToken ct)
    {
        // Sleep and bookmark handling retain their authoritative owner-captured protocols.
        if (command.Action != ListenPlaybackCommandActions.SetSleepTimer)
        {
            var activeProfile = services.GetService<IUserPlaybackPreferencesAccessor>()?.ActiveProfileId;
            if (activeProfile is not Guid profileId || profileId == Guid.Empty || command.ProfileId != profileId
                || command.WorkId is not Guid workId
                || command.ExpectedAssetId is not Guid assetId
                || command.ExpectedPlaybackRequestVersion is not long version
                || playback.CurrentItem is not { } current
                || current.WorkId != workId || current.AssetId != assetId
                || playback.PlaybackRequestVersion != version || playback.IsDismissed)
                return Reply(command, AudiobookBookmarkOperationOutcomes.DefiniteFailure,
                    "Playback changed before this action arrived. Try again for the current item.");

            if (command.Action is ListenPlaybackCommandActions.PlayIndex or ListenPlaybackCommandActions.RemoveUpcoming)
            {
                var target = command.QueueEntryId is { } entryId
                    ? playback.Queue.Select((item, index) => (item, index)).FirstOrDefault(row => row.item.QueueEntryId == entryId)
                    : default;
                if (target.item is null || command.Action == ListenPlaybackCommandActions.RemoveUpcoming && target.index <= playback.CurrentIndex)
                    return Reply(command, AudiobookBookmarkOperationOutcomes.DefiniteFailure,
                        "That queue item is no longer available as displayed.");
                command = command with { Index = target.index };
            }
        }
        if (command.Action == ListenPlaybackCommandActions.SetSleepTimer)
        {
            var preferences = services.GetService<IUserPlaybackPreferencesAccessor>();
            if (preferences?.ActiveProfileId is not Guid profileId
                || command.ProfileId != profileId
                || command.WorkId is not Guid workId
                || command.ExpectedAssetId is not Guid expectedAssetId
                || command.ExpectedPlaybackRequestVersion is not long expectedRequestVersion
                || command.SleepTimer is not { } selection)
            {
                return Reply(command, AudiobookBookmarkOperationOutcomes.DefiniteFailure,
                    "The sleep timer request is missing current playback identity.");
            }

            try
            {
                var state = await playback.SetAudiobookSleepTimerAsync(profileId, workId, expectedAssetId,
                    expectedRequestVersion, selection, ct).ConfigureAwait(false);
                return Reply(command, AudiobookBookmarkOperationOutcomes.Success, sleepTimerState: state,
                    sleepTimerAvailability: playback.SleepTimerAvailability);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Reply(command, AudiobookBookmarkOperationOutcomes.DefiniteFailure, exception.Message);
            }
        }

        if (command.Action == ListenPlaybackCommandActions.SetSpeed)
        {
            var preferences = services.GetService<IUserPlaybackPreferencesAccessor>();
            if (command.Value is not double requestedRate || !PlaybackRatePolicy.IsValid(requestedRate))
                return Reply(command, AudiobookBookmarkOperationOutcomes.DefiniteFailure, "The requested playback speed is invalid.");

            if (preferences?.ActiveProfileId is not Guid activeProfile
                || command.ProfileId != activeProfile
                || command.WorkId is not Guid expectedWorkId
                || command.ExpectedAssetId is not Guid expectedAssetId
                || command.ExpectedPlaybackRequestVersion is not long expectedVersion
                || playback.CurrentItem is not { } current
                || current.WorkId != expectedWorkId
                || current.AssetId != expectedAssetId
                || playback.PlaybackRequestVersion != expectedVersion)
            {
                return Reply(command, AudiobookBookmarkOperationOutcomes.DefiniteFailure,
                    "Playback changed before the speed request arrived. Choose the speed again for the current item.");
            }
        }

        var kind = command.Action switch
        {
            ListenPlaybackCommandActions.TogglePlay => PlaybackCommandKind.TogglePlay,
            ListenPlaybackCommandActions.Pause => PlaybackCommandKind.Pause,
            ListenPlaybackCommandActions.PlayNext => PlaybackCommandKind.PlayNext,
            ListenPlaybackCommandActions.PlayPrevious => PlaybackCommandKind.PlayPrevious,
            ListenPlaybackCommandActions.PlayNextChapter => PlaybackCommandKind.PlayNextChapter,
            ListenPlaybackCommandActions.PlayPreviousChapter => PlaybackCommandKind.PlayPreviousChapter,
            ListenPlaybackCommandActions.SkipBack => PlaybackCommandKind.SkipRelative,
            ListenPlaybackCommandActions.SkipForward => PlaybackCommandKind.SkipRelative,
            ListenPlaybackCommandActions.Seek => PlaybackCommandKind.Seek,
            ListenPlaybackCommandActions.SetVolume => PlaybackCommandKind.SetVolume,
            ListenPlaybackCommandActions.SetSpeed => PlaybackCommandKind.SetSpeed,
            ListenPlaybackCommandActions.ToggleMute => PlaybackCommandKind.ToggleMute,
            ListenPlaybackCommandActions.TogglePanel => PlaybackCommandKind.TogglePanel,
            ListenPlaybackCommandActions.SetTab => PlaybackCommandKind.SetActiveTab,
            ListenPlaybackCommandActions.ClearUpcoming => PlaybackCommandKind.ClearUpcoming,
            ListenPlaybackCommandActions.RemoveUpcoming => PlaybackCommandKind.RemoveUpcoming,
            ListenPlaybackCommandActions.PlayIndex => PlaybackCommandKind.PlayIndex,
            ListenPlaybackCommandActions.PlayAudiobookHistory => PlaybackCommandKind.PlayAudiobookHistory,
            ListenPlaybackCommandActions.ClosePlayer => PlaybackCommandKind.ClosePlayer,
            _ => (PlaybackCommandKind?)null,
        };

        try
        {
            switch (command.Action)
            {
                case ListenPlaybackCommandActions.ListOutputs:
                case ListenPlaybackCommandActions.SetOutputDevice:
                    if (!playback.OutputSupported) return Reply(command, AudiobookBookmarkOperationOutcomes.DefiniteFailure, "Output selection is unavailable in this browser.");
                    var js = services.GetRequiredService<IJSRuntime>();
                    var output = command.Action == ListenPlaybackCommandActions.ListOutputs
                        ? await js.InvokeAsync<AudioOutputStateDto>("listenOutput.list", ct)
                        : await js.InvokeAsync<AudioOutputStateDto>("listenOutput.select", ct, command.OutputDeviceId, command.ExpectedAssetId, command.ExpectedPlaybackRequestVersion);
                    if (command.ProfileId != services.GetService<IUserPlaybackPreferencesAccessor>()?.ActiveProfileId
                        || command.WorkId != playback.CurrentItem?.WorkId || command.ExpectedAssetId != playback.CurrentItem?.AssetId
                        || command.ExpectedPlaybackRequestVersion != playback.PlaybackRequestVersion || playback.IsDismissed)
                        return Reply(command, AudiobookBookmarkOperationOutcomes.DefiniteFailure, "Playback changed before output selection completed.");
                    return new() { CommandId = command.CommandId, RecipientId = command.SenderId, AudioOutput = output,
                        Outcome = output.Message is null ? AudiobookBookmarkOperationOutcomes.Success : AudiobookBookmarkOperationOutcomes.DefiniteFailure,
                        Message = output.Message };
                case ListenPlaybackCommandActions.ReorderUpcoming when command.QueueEntryId is Guid occurrence
                    && command.Index is int destination && command.ExpectedQueueRevision is long revision:
                    await playback.MoveUpcomingAsync(occurrence, destination, revision, ct).ConfigureAwait(false);
                    break;
                case ListenPlaybackCommandActions.ClearHistory:
                    playback.ClearMusicHistory();
                    break;
                case ListenPlaybackCommandActions.Play:
                    if (!playback.IsPlaying || playback.NeedsUserGestureToStart) await playback.DispatchAsync(PlaybackCommand.TogglePlay(), ct).ConfigureAwait(false);
                    break;
                case ListenPlaybackCommandActions.PlayChapter when command.ChapterIndex is int chapterIndex:
                    await playback.PlayAudiobookChapterAsync(chapterIndex, ct).ConfigureAwait(false);
                    break;
                case ListenPlaybackCommandActions.ShowQueue:
                    playback.SetActiveTab(ListenPlaybackTabs.Queue);
                    playback.TogglePanel();
                    break;
                case ListenPlaybackCommandActions.PlayHistory when command.QueueItem is { } queueItem:
                    var historyItem = playback.History.FirstOrDefault(item =>
                        item.WorkId == queueItem.WorkId && item.AssetId == queueItem.AssetId
                        && (command.QueueEntryId is null || item.QueueEntryId == command.QueueEntryId));
                    if (historyItem is null)
                        return Reply(command, AudiobookBookmarkOperationOutcomes.DefiniteFailure, "That played track is no longer available in this session.");
                    await playback.PlayQueueItemAsync(historyItem, historyItem.Album ?? historyItem.Title, ct).ConfigureAwait(false);
                    break;
                case ListenPlaybackCommandActions.ToggleShuffle:
                    await playback.ToggleShuffleAsync(ct).ConfigureAwait(false);
                    break;
                case ListenPlaybackCommandActions.CycleRepeat:
                    await playback.CycleRepeatModeAsync(ct).ConfigureAwait(false);
                    break;
                case var _ when kind is PlaybackCommandKind value:
                    if (command.Action == ListenPlaybackCommandActions.Seek && command.Value is null
                        || command.Action == ListenPlaybackCommandActions.SetVolume && command.Value is null
                        || command.Action == ListenPlaybackCommandActions.SetSpeed && command.Value is null
                        || command.Action == ListenPlaybackCommandActions.RemoveUpcoming && command.Index is null
                        || command.Action == ListenPlaybackCommandActions.PlayIndex && command.Index is null
                        || command.Action == ListenPlaybackCommandActions.PlayAudiobookHistory && command.AudiobookHistoryItem is null
                        || command.Action == ListenPlaybackCommandActions.SetTab && string.IsNullOrWhiteSpace(command.Tab))
                    {
                        return Reply(command, AudiobookBookmarkOperationOutcomes.DefiniteFailure, "The playback command payload is incomplete.");
                    }
                    else
                    {
                        var playbackCommand = new PlaybackCommand(value,
                            Value: command.Action == ListenPlaybackCommandActions.SkipBack ? -playback.SkipBackSeconds
                                : command.Action == ListenPlaybackCommandActions.SkipForward ? playback.SkipForwardSeconds
                                : command.Value,
                            Index: command.Index,
                            Text: command.Tab,
                            Item: command.QueueItem is { } item ? ToQueueItem(item) : null,
                            AudiobookHistoryItem: command.AudiobookHistoryItem,
                            CancellationToken: ct);
                        await playback.DispatchAsync(playbackCommand, ct).ConfigureAwait(false);
                    }
                    break;
                default:
                    return Reply(command, AudiobookBookmarkOperationOutcomes.DefiniteFailure, "The playback command is unsupported.");
            }

            return Reply(command, AudiobookBookmarkOperationOutcomes.Success);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Reply(command, AudiobookBookmarkOperationOutcomes.DefiniteFailure, "The playback command was cancelled.");
        }
        catch
        {
            return Reply(command, AudiobookBookmarkOperationOutcomes.DefiniteFailure, "The playback command could not be completed.");
        }
    }

    private static bool IsBookmarkAction(string action) => action is
        ListenPlaybackCommandActions.OpenBookmarkDialog or ListenPlaybackCommandActions.BookmarkDialogState
        or ListenPlaybackCommandActions.PreviewBookmarkDraft or ListenPlaybackCommandActions.LoadBookmarks
        or ListenPlaybackCommandActions.SaveBookmarkDraft or ListenPlaybackCommandActions.ReplayBookmark
        or ListenPlaybackCommandActions.RequestDeleteBookmark or ListenPlaybackCommandActions.ConfirmDeleteBookmark
        or ListenPlaybackCommandActions.CancelDeleteBookmark or ListenPlaybackCommandActions.CloseBookmarkDialog;

    private static ListenQueueItem ToQueueItem(PlayerQueueItemDto item) => new()
    {
        WorkId = item.WorkId,
        AssetId = item.AssetId,
        CollectionId = item.CollectionId,
        MediaType = item.MediaType,
        Title = item.Title,
        Subtitle = item.Subtitle,
        Album = item.Album,
        AuthorName = item.Author,
        NarratorName = item.Narrator,
        Year = item.Year,
        ContentRating = item.ContentRating,
        SeasonNumber = item.SeasonNumber,
        EpisodeNumber = item.EpisodeNumber,
        EpisodeTitle = item.EpisodeTitle,
        Quality = item.Quality,
        CoverUrl = item.CoverUrl,
        Duration = item.DurationSeconds is double duration ? PlaybackTimeParser.FormatDuration(duration) : null,
        InitialPositionSeconds = item.PositionSeconds,
        StreamUrl = item.StreamUrl,
        Chapters = item.Chapters,
        Manifest = item.Manifest,
    };

    private ListenPlaybackCommandReplyDto Reply(ListenPlaybackCommandDto command, string outcome, string? message = null,
        AudiobookSleepTimerStateDto? sleepTimerState = null,
        AudiobookSleepTimerAvailabilityDto? sleepTimerAvailability = null) => new()
    {
        CommandId = command.CommandId,
        RecipientId = command.SenderId,
        Outcome = outcome,
        Message = message,
        SleepTimerState = sleepTimerState,
        SleepTimerAvailability = sleepTimerAvailability,
    };
}

public sealed class PlaybackAudiobookBookmarkAuthoritySource(
    PlaybackSessionController playback,
    IUserPlaybackPreferencesAccessor preferences,
    IEngineApiClient api) : IAudiobookBookmarkAuthoritySource
{
    public async Task<IReadOnlySet<Guid>> GetAuthorizedAssetIdsAsync(AudiobookBookmarkActionContext context,
        CancellationToken ct = default)
    {
        if (preferences.ActiveProfileId != context.ProfileId || !playback.IsAudiobookMode
            || playback.AudiobookBookSessionLeaseId != context.SessionLeaseId
            || (playback.CurrentItem?.AudiobookWorkId ?? playback.CurrentItem?.WorkId) != context.WorkId)
            return new HashSet<Guid>();

        var authorized = new HashSet<Guid>();
        foreach (var item in playback.Queue.Where(item => (item.AudiobookWorkId ?? item.WorkId) == context.WorkId
                     && MediaKindClassifier.IsAudiobook(item.MediaType)))
        {
            if (item.AssetId is Guid assetId && assetId != Guid.Empty) authorized.Add(assetId);
            if (item.Manifest is { } manifest && item.AssetId == manifest.AssetId) authorized.Add(manifest.AssetId);
            foreach (var chapter in item.Chapters)
            {
                if (chapter.AssetId is Guid chapterAsset && chapterAsset != Guid.Empty) authorized.Add(chapterAsset);
            }
        }

        var history = await api.GetAudiobookListenHistoryAsync(context.WorkId, context.ProfileId, 250, ct)
            .ConfigureAwait(false);
        foreach (var row in history.Where(row => row.ProfileId == context.ProfileId && row.WorkId == context.WorkId))
            if (row.AssetId != Guid.Empty) authorized.Add(row.AssetId);

        var saved = await api.GetAudiobookBookmarksWithOutcomeAsync(context.WorkId, context.ProfileId, ct)
            .ConfigureAwait(false);
        if (saved.Outcome == AudiobookBookmarkOperationOutcome.Success && saved.Value is not null)
        {
            foreach (var row in saved.Value.Where(row => row.ProfileId == context.ProfileId && row.WorkId == context.WorkId))
                if (row.AssetId != Guid.Empty) authorized.Add(row.AssetId);
        }

        if (playback.CurrentItem?.AssetId is Guid currentAsset && currentAsset != Guid.Empty)
            authorized.Add(currentAsset);
        return authorized;
    }
}

public sealed class PlaybackAudiobookBookmarkNativeOwner(
    PlaybackSessionController playback,
    IUserPlaybackPreferencesAccessor preferences,
    IJSRuntime js,
    IAudiobookBookmarkAuthoritySource authority) : IAudiobookBookmarkNativeOwner
{
    public Task<bool> IsCurrentSessionAsync(AudiobookBookmarkActionContext context, CancellationToken ct = default) =>
        Task.FromResult(preferences.ActiveProfileId == context.ProfileId
            && playback.IsAudiobookMode
            && (playback.CurrentItem?.AudiobookWorkId ?? playback.CurrentItem?.WorkId) == context.WorkId
            && playback.AudiobookBookSessionLeaseId == context.SessionLeaseId
            && playback.AudiobookBookSessionGeneration == context.OwnerGeneration);

    public async Task<bool> IsCurrentSourceAsync(AudiobookBookmarkActionContext context, Guid expectedAssetId,
        CancellationToken ct = default) => expectedAssetId != Guid.Empty
        && context.ExpectedAssetId == expectedAssetId
        && (playback.CurrentItem?.AudiobookWorkId ?? playback.CurrentItem?.WorkId) == context.WorkId
        && playback.CurrentItem?.AssetId == expectedAssetId
        && await IsCurrentSessionAsync(context, ct).ConfigureAwait(false);

    public async Task<AudiobookBookmarkCaptureObservation?> CaptureCurrentAsync(AudiobookBookmarkActionContext context,
        CancellationToken ct = default)
    {
        if (context.ExpectedAssetId is not Guid assetId || !await IsCurrentSourceAsync(context, assetId, ct).ConfigureAwait(false))
            return null;

        var expectedRequestVersion = playback.PlaybackRequestVersion;
        var expectedSourceUrl = playback.CurrentBrowserStreamUrl;
        if (string.IsNullOrWhiteSpace(expectedSourceUrl)) return null;

        AudiobookBookmarkNativeCaptureDto? metrics;
        try
        {
            metrics = await js.InvokeAsync<AudiobookBookmarkNativeCaptureDto?>("listenPlayback.captureAudiobookBookmarkPosition", ct,
                assetId.ToString("D"), expectedRequestVersion, expectedSourceUrl).ConfigureAwait(false);
        }
        catch (JSException) { return null; }
        catch (InvalidOperationException) { return null; }

        if (metrics is null || metrics.AssetId != assetId
            || metrics.PlaybackRequestVersion != expectedRequestVersion || !metrics.SourceVerified
            || playback.PlaybackRequestVersion != expectedRequestVersion
            || playback.CurrentBrowserStreamUrl != expectedSourceUrl
            || !await IsCurrentSourceAsync(context, assetId, ct).ConfigureAwait(false)
            || !double.IsFinite(metrics.PositionSeconds) || metrics.PositionSeconds < 0
            || metrics.DurationSeconds is double duration && (!double.IsFinite(duration) || duration <= 0))
            return null;

        var item = playback.CurrentItem;
        PlaybackChapterDto? chapter = null;
        if (item is not null)
        {
            var matching = item.Chapters.Where(candidate => candidate.AssetId == assetId
                && double.IsFinite(candidate.StartSeconds) && candidate.StartSeconds >= 0
                && candidate.EndSeconds is double end && double.IsFinite(end) && end > candidate.StartSeconds
                && metrics.PositionSeconds >= candidate.StartSeconds && metrics.PositionSeconds < end).ToArray();
            if (matching.Length == 1) chapter = matching[0];
        }

        return new AudiobookBookmarkCaptureObservation(assetId, metrics.PositionSeconds, metrics.DurationSeconds, chapter);
    }

    public async Task<AudiobookBookmarkOperationResult<bool>> ReplayBookmarkAsync(AudiobookBookmarkActionContext context,
        AudiobookBookmarkDto bookmark, CancellationToken ct = default)
    {
        if (bookmark.ProfileId != context.ProfileId || bookmark.WorkId != context.WorkId || bookmark.AssetId == Guid.Empty
            || !await IsCurrentSessionAsync(context, ct).ConfigureAwait(false))
            return AudiobookBookmarkOperationResult<bool>.Failed("The bookmark does not belong to this audiobook session.");
        var authorizedAssets = await authority.GetAuthorizedAssetIdsAsync(context, ct).ConfigureAwait(false);
        if (!authorizedAssets.Contains(bookmark.AssetId)
            || !await IsCurrentSessionAsync(context, ct).ConfigureAwait(false))
            return AudiobookBookmarkOperationResult<bool>.Failed("The saved audio source is no longer authorized.");
        var previousRequestVersion = playback.PlaybackRequestVersion;
        await playback.PlayAudiobookBookmarkAsync(bookmark, ct).ConfigureAwait(false);
        var requestVersion = playback.PlaybackRequestVersion;
        var streamUrl = playback.CurrentBrowserStreamUrl;
        var nativePosition = string.IsNullOrWhiteSpace(streamUrl) ? null
            : await ReadVerifiedNativePositionAsync(bookmark.AssetId, requestVersion, streamUrl, ct).ConfigureAwait(false);
        return requestVersion != previousRequestVersion
            && preferences.ActiveProfileId == context.ProfileId
            && playback.IsAudiobookMode
            && (playback.CurrentItem?.AudiobookWorkId ?? playback.CurrentItem?.WorkId) == context.WorkId
            && playback.CurrentItem?.AssetId == bookmark.AssetId
            && playback.PlaybackRequestVersion == requestVersion
            && playback.AudiobookBookSessionLeaseId != context.SessionLeaseId
            && playback.AudiobookBookSessionGeneration > context.OwnerGeneration
            && playback.CurrentBrowserStreamUrl == streamUrl
            && playback.CurrentError is null
            && nativePosition.HasValue
            ? AudiobookBookmarkOperationResult<bool>.Succeeded(true)
            : AudiobookBookmarkOperationResult<bool>.Failed("The exact saved audio source could not be confirmed after playback changed.");
    }

    public Task<AudiobookBookmarkOperationResult<bool>> PreviewCapturedDraftAsync(AudiobookBookmarkActionContext context,
        CapturedAudiobookBookmarkDraft draft, CancellationToken ct = default)
    {
        return PreviewCapturedDraftCoreAsync(context, draft, ct);
    }

    private async Task<AudiobookBookmarkOperationResult<bool>> PreviewCapturedDraftCoreAsync(
        AudiobookBookmarkActionContext context, CapturedAudiobookBookmarkDraft draft, CancellationToken ct)
    {
        if (draft.ProfileId != context.ProfileId || draft.WorkId != context.WorkId
            || draft.SessionLeaseId != context.SessionLeaseId || !double.IsFinite(draft.PositionSeconds)
            || draft.PositionSeconds < 0 || draft.AssetId == Guid.Empty
            || !await IsCurrentSessionAsync(context, ct).ConfigureAwait(false))
            return AudiobookBookmarkOperationResult<bool>.Failed("The captured position is invalid.");

        var authorizedAssets = await authority.GetAuthorizedAssetIdsAsync(context, ct).ConfigureAwait(false);
        if (!authorizedAssets.Contains(draft.AssetId)
            || !await IsCurrentSessionAsync(context, ct).ConfigureAwait(false))
            return AudiobookBookmarkOperationResult<bool>.Failed("The captured audio source is no longer authorized.");

        var previousRequestVersion = playback.PlaybackRequestVersion;
        if (!await playback.PreviewCapturedAudiobookDraftAsync(context, draft, ct).ConfigureAwait(false))
            return AudiobookBookmarkOperationResult<bool>.Failed("The captured source could not be previewed.");

        var requestVersion = playback.PlaybackRequestVersion;
        var streamUrl = playback.CurrentBrowserStreamUrl;
        var nativePosition = string.IsNullOrWhiteSpace(streamUrl) ? null
            : await ReadVerifiedNativePositionAsync(draft.AssetId, requestVersion, streamUrl, ct).ConfigureAwait(false);
        return requestVersion != previousRequestVersion
            && await IsCurrentSessionAsync(context, ct).ConfigureAwait(false)
            && playback.CurrentItem?.AssetId == draft.AssetId
            && playback.PlaybackRequestVersion == requestVersion
            && playback.CurrentBrowserStreamUrl == streamUrl
            && playback.CurrentError is null
            && nativePosition is double position && double.IsFinite(position)
            ? AudiobookBookmarkOperationResult<bool>.Succeeded(true)
            : AudiobookBookmarkOperationResult<bool>.Failed("The captured source changed before preview could be confirmed.");
    }

    private async Task<double?> ReadVerifiedNativePositionAsync(Guid assetId, long requestVersion, string sourceUrl,
        CancellationToken ct)
    {
        try
        {
            return await js.InvokeAsync<double?>("listenPlayback.readCurrentAudiobookPosition", ct,
                assetId.ToString("D"), requestVersion, sourceUrl).ConfigureAwait(false);
        }
        catch (JSException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}
