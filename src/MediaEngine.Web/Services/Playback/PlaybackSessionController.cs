using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Services.Playback;

public sealed class PlaybackSessionController
{
    private readonly UIOrchestratorService _orchestrator;
    private readonly IEngineApiClient _apiClient;
    private readonly ILogger<PlaybackSessionController>? _logger;
    private readonly IUserPlaybackPreferencesAccessor? _preferences;
    private readonly ListenPlaybackClientSettings _clientSettings;
    private readonly PlaybackStateMachine _stateMachine = new();
    private readonly List<ListenQueueItem> _queue = [];
    private readonly List<ListenQueueItem> _history = [];
    private IReadOnlyList<ListenQueueItem> _upcomingQueue = [];
    private readonly List<AudiobookListenHistoryItemDto> _audiobookHistory = [];
    private readonly List<AudiobookBookmarkDto> _audiobookBookmarks = [];
    private readonly List<PlaybackTransportCommand> _pendingTransportCommands = [];
    private Guid _sessionId;
    private long _telemetrySequence;
    private long _nextTransportRequestId;
    private long _lastDispatchedTransportRequestId;
    private PlaybackClientContext _clientContext = PlaybackClientContext.WebDefault;
    private DateTimeOffset _lastHeartbeatAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTransportUiNotificationAt = DateTimeOffset.MinValue;
    private CancellationTokenSource? _sleepTimerCts;
    private bool _transportHostReady = true;
    private string? _currentAudiobookStartKind;
    private CancellationTokenSource? _startCancellation;
    public bool HasExplicitPlaybackRequest { get; private set; }
    public long PlaybackRequestVersion { get; private set; }

    public void ReservePlaybackRequest()
    {
        HasExplicitPlaybackRequest = true;
        PlaybackRequestVersion++;
        _startCancellation?.Cancel();
        _pendingTransportCommands.Clear();
    }

    private CancellationTokenSource BeginPlaybackRequest(CancellationToken ct)
    {
        EndViewSession();
        ReservePlaybackRequest();
        return _startCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    public void RestoreInitialState(ListenPlaybackSnapshot snapshot)
    {
        if (!HasExplicitPlaybackRequest) RestoreState(snapshot);
    }

    public PlaybackSessionController(
        UIOrchestratorService orchestrator,
        IEngineApiClient apiClient,
        ILogger<PlaybackSessionController>? logger = null,
        IUserPlaybackPreferencesAccessor? preferences = null,
        ListenPlaybackClientSettings? clientSettings = null)
    {
        _orchestrator = orchestrator;
        _apiClient = apiClient;
        _logger = logger;
        _preferences = preferences;
        _clientSettings = (clientSettings ?? new ListenPlaybackClientSettings()).Normalize();
        Volume = _clientSettings.DefaultVolume;
        ApplyListeningSettings(UserPlaybackSettingsDto.CreateDefaults(Guid.Empty).Listening);
        RefreshUpcomingQueue();
    }

    public event Action<PlaybackChangeKind>? Changed;
    public event Func<PlaybackTransportCommand, Task>? TransportCommandRequested;

    public IReadOnlyList<ListenQueueItem> Queue => _queue;
    public IReadOnlyList<ListenQueueItem> History => _history;
    public IReadOnlyList<AudiobookListenHistoryItemDto> AudiobookHistory => _audiobookHistory;
    public IReadOnlyList<AudiobookBookmarkDto> AudiobookBookmarks => _audiobookBookmarks;
    public IReadOnlyList<ListenQueueItem> UpcomingQueue => _upcomingQueue;

    public int CurrentIndex { get; private set; } = -1;
    public string? SourceLabel { get; private set; }
    public bool IsPanelOpen { get; private set; }
    public string ActiveTab { get; private set; } = ListenPlaybackTabs.Queue;
    public bool IsDismissed { get; private set; }
    public double CurrentTimeSeconds { get; private set; }
    public double DurationSeconds { get; private set; }
    public double Volume { get; private set; }
    public bool IsMuted { get; private set; }
    public bool IsPlaying { get; private set; } = true;
    public double PlaybackRate { get; private set; } = 1d;
    public bool ShuffleEnabled { get; private set; }
    public string RepeatMode { get; private set; } = PlayerRepeatModes.Off;
    public long PlaybackStartVersion { get; private set; }
    public string Experience { get; private set; } = PlayerExperienceModes.Music;
    public bool NeedsUserGestureToStart { get; private set; }
    public bool IsPopupOpen { get; private set; }
    public bool IsVideoExpanded { get; private set; }
    public ViewPlaybackSessionState? ViewSession { get; private set; }
    public PlaybackPresentationSurface PresentationSurface { get; private set; } = PlaybackPresentationSurface.Docked;
    public string? CurrentError { get; private set; }
    public int SkipBackSeconds { get; private set; }
    public int SkipForwardSeconds { get; private set; }
    public int ResumeRewindSeconds { get; private set; }
    public int AudiobookNearStartGuardSeconds { get; private set; }
    public IReadOnlyList<int> SleepTimerOptionsMinutes { get; private set; } = [];
    public bool AllowEndOfChapterSleepTimer { get; private set; }
    public string SleepTimerMode { get; private set; } = ListenSleepTimerModes.Off;
    public DateTimeOffset? SleepTimerEndsAtUtc { get; private set; }
    public string SleepTimerLabel => SleepTimerMode switch
    {
        ListenSleepTimerModes.EndOfChapter => "End of chapter",
        ListenSleepTimerModes.Timer when SleepTimerEndsAtUtc.HasValue => FormatSleepTimerRemaining(SleepTimerEndsAtUtc.Value),
        _ => "Off",
    };

    public bool HasQueue => _queue.Count > 0 && !IsDismissed;

    public ListenQueueItem? CurrentItem =>
        CurrentIndex >= 0 && CurrentIndex < _queue.Count
            ? _queue[CurrentIndex]
            : null;

    public string? CurrentStreamUrl => CurrentItem?.StreamUrl;
    public string? CurrentBrowserStreamUrl => ToDashboardPlaybackUrl(CurrentStreamUrl) ?? CurrentStreamUrl;
    public PlaybackManifestDto? CurrentManifest => CurrentItem?.Manifest;
    public bool IsAudiobookMode => string.Equals(Experience, PlayerExperienceModes.Audiobook, StringComparison.OrdinalIgnoreCase);
    public bool IsMusicMode => string.Equals(Experience, PlayerExperienceModes.Music, StringComparison.OrdinalIgnoreCase);
    public bool IsVideoMode => string.Equals(Experience, PlayerExperienceModes.Video, StringComparison.OrdinalIgnoreCase);
    public PlaybackClientContext ClientContext => _clientContext;
    public ListenPlaybackClientSettings ClientSettings => _clientSettings;
    public PlaybackPhase Phase => _stateMachine.Phase;

    public PlaybackSessionState State => new()
    {
        ViewSession = ViewSession,
        Queue = Queue,
        History = History,
        UpcomingQueue = UpcomingQueue,
        AudiobookHistory = AudiobookHistory,
        AudiobookBookmarks = AudiobookBookmarks,
        CurrentIndex = CurrentIndex,
        SourceLabel = SourceLabel,
        IsPanelOpen = IsPanelOpen,
        ActiveTab = ActiveTab,
        IsDismissed = IsDismissed,
        CurrentTimeSeconds = CurrentTimeSeconds,
        DurationSeconds = DurationSeconds,
        Volume = Volume,
        IsMuted = IsMuted,
        IsPlaying = IsPlaying,
        PlaybackRate = PlaybackRate,
        ShuffleEnabled = ShuffleEnabled,
        RepeatMode = RepeatMode,
        PlaybackStartVersion = PlaybackStartVersion,
        Experience = MediaKindClassifier.FromPlayerExperienceString(Experience),
        Phase = Phase,
        NeedsUserGestureToStart = NeedsUserGestureToStart,
        IsPopupOpen = IsPopupOpen,
        IsVideoExpanded = IsVideoExpanded,
        PresentationSurface = PresentationSurface,
        CurrentError = CurrentError,
        SkipBackSeconds = SkipBackSeconds,
        SkipForwardSeconds = SkipForwardSeconds,
        ResumeRewindSeconds = ResumeRewindSeconds,
        AudiobookNearStartGuardSeconds = AudiobookNearStartGuardSeconds,
        SleepTimerOptionsMinutes = SleepTimerOptionsMinutes,
        AllowEndOfChapterSleepTimer = AllowEndOfChapterSleepTimer,
        SleepTimerMode = SleepTimerMode,
        SleepTimerEndsAtUtc = SleepTimerEndsAtUtc,
        CurrentItem = CurrentItem,
        CurrentStreamUrl = CurrentStreamUrl,
        CurrentBrowserStreamUrl = CurrentBrowserStreamUrl,
        CurrentChapter = CurrentChapter,
        ClientContext = ClientContext,
    };

    public void SetClientContext(PlaybackClientContext context)
    {
        _clientContext = context.Normalize();
        NotifyChanged(PlaybackChangeKind.Ui);
    }

    public async Task DispatchAsync(PlaybackCommand command, CancellationToken ct = default)
    {
        var cancellationToken = command.CancellationToken.CanBeCanceled ? command.CancellationToken : ct;
        _stateMachine.Transition(command.Kind);

        switch (command.Kind)
        {
            case PlaybackCommandKind.TogglePlay:
                await RequestTransportCommandAsync(new("toggle-play"));
                break;
            case PlaybackCommandKind.Pause:
                ReservePlaybackRequest();
                await RequestTransportCommandAsync(new("pause"));
                break;
            case PlaybackCommandKind.PlayNext:
                await RequestTransportCommandAsync(new("play-next"));
                break;
            case PlaybackCommandKind.PlayPrevious:
                await RequestTransportCommandAsync(new("play-previous"));
                break;
            case PlaybackCommandKind.PlayNextChapter:
                await PlayNextChapterAsync(cancellationToken);
                break;
            case PlaybackCommandKind.PlayPreviousChapter:
                await PlayPreviousChapterAsync(cancellationToken);
                break;
            case PlaybackCommandKind.SkipRelative when command.Value.HasValue:
                await SeekRelativeAsync(command.Value.Value, cancellationToken);
                break;
            case PlaybackCommandKind.Seek when command.Value.HasValue:
                await RequestTransportCommandAsync(new("seek", command.Value.Value));
                break;
            case PlaybackCommandKind.SetVolume when command.Value.HasValue:
                await RequestTransportCommandAsync(new("set-volume", Math.Clamp(command.Value.Value, 0d, 1d)));
                break;
            case PlaybackCommandKind.SetSpeed when command.Value.HasValue:
                await SetPlaybackRateAsync(command.Value.Value, cancellationToken);
                break;
            case PlaybackCommandKind.ToggleMute:
                await RequestTransportCommandAsync(new("toggle-mute"));
                break;
            case PlaybackCommandKind.TogglePanel:
                TogglePanel();
                break;
            case PlaybackCommandKind.ClosePanel:
                ClosePanel();
                break;
            case PlaybackCommandKind.SetActiveTab when !string.IsNullOrWhiteSpace(command.Text):
                SetActiveTab(command.Text);
                break;
            case PlaybackCommandKind.ClearUpcoming:
                ClearUpcoming();
                break;
            case PlaybackCommandKind.RemoveUpcoming when command.Index.HasValue:
                RemoveUpcomingAt(command.Index.Value);
                break;
            case PlaybackCommandKind.PlayIndex when command.Index.HasValue:
                await PlayIndexAsync(command.Index.Value, cancellationToken);
                break;
            case PlaybackCommandKind.PlayHistory when command.Item is not null:
                await PlayQueueItemAsync(command.Item, command.Item.Album ?? command.Item.Title, cancellationToken);
                break;
            case PlaybackCommandKind.PlayQueueItem when command.Item is not null:
                await PlayQueueItemAsync(command.Item, command.Text, cancellationToken);
                break;
            case PlaybackCommandKind.PlayAudiobookChapter when command.Index.HasValue:
                await PlayAudiobookChapterAsync(command.Index.Value, cancellationToken);
                break;
            case PlaybackCommandKind.PlayAudiobookHistory when command.AudiobookHistoryItem is not null:
                await PlayAudiobookHistoryAsync(command.AudiobookHistoryItem, cancellationToken);
                break;
            case PlaybackCommandKind.PlayAudiobookBookmark when command.AudiobookBookmark is not null:
                await PlayAudiobookBookmarkAsync(command.AudiobookBookmark, cancellationToken);
                break;
            case PlaybackCommandKind.AddAudiobookBookmark:
                await AddAudiobookBookmarkAsync(cancellationToken);
                break;
            case PlaybackCommandKind.DeleteAudiobookBookmark when command.Id.HasValue:
                await DeleteAudiobookBookmarkAsync(command.Id.Value, cancellationToken);
                break;
            case PlaybackCommandKind.SetSleepTimer when command.Duration.HasValue:
                await SetSleepTimerAsync(command.Duration.Value, cancellationToken);
                break;
            case PlaybackCommandKind.SetSleepTimerEndOfChapter:
                await SetSleepTimerEndOfChapterAsync();
                break;
            case PlaybackCommandKind.CancelSleepTimer:
                await CancelSleepTimerAsync();
                break;
            case PlaybackCommandKind.SetPopupOpen when command.Flag.HasValue:
                SetPopupOpen(command.Flag.Value);
                break;
            case PlaybackCommandKind.ClosePlayer:
                ClosePlayer();
                break;
            case PlaybackCommandKind.RestoreState when command.Snapshot is not null:
                RestoreState(command.Snapshot);
                break;
            case PlaybackCommandKind.UpdateTransportState when command.AudioState is not null:
                UpdateTransportState(
                    command.AudioState.CurrentTimeSeconds,
                    command.AudioState.DurationSeconds,
                    command.AudioState.IsPlaying,
                    command.AudioState.Volume,
                    command.AudioState.IsMuted,
                    command.AudioState.PlaybackRate,
                    command.AudioState.NeedsUserGestureToStart);
                break;
            case PlaybackCommandKind.MarkPlaybackStarted:
                MarkPlaybackStarted();
                break;
            case PlaybackCommandKind.MarkNeedsUserGestureToStart:
                MarkNeedsUserGestureToStart();
                break;
            case PlaybackCommandKind.ReportHeartbeat:
                await ReportHeartbeatAsync(force: command.Flag == true, cancellationToken);
                break;
        }
    }

    public async Task PlayWorkAsync(WorkViewModel work, string? sourceLabel = null, CancellationToken ct = default)
    {
        await ReplaceQueueAsync([work], 0, sourceLabel ?? work.Album ?? work.Title, false, ct);
    }

    public Task PlayQueueItemAsync(ListenQueueItem item, string? sourceLabel = null, CancellationToken ct = default)
        => MediaKindClassifier.Classify(item.MediaType) switch
        {
            PlaybackExperience.Audiobook => PlayAudiobookAsync(item, sourceLabel, ct),
            PlaybackExperience.Video => PlayVideoAsync(item, sourceLabel, ct),
            _ => PlayQueueItemCoreAsync(item, sourceLabel, ct),
        };

    public Task PlayAudiobookAsync(ListenQueueItem item, string? sourceLabel = null, CancellationToken ct = default)
        => StartAudiobookAsync(new AudiobookStartRequest(item, AudiobookStartKinds.Resume, item.InitialPositionSeconds, item.ChapterIndex, sourceLabel), ct);

    public async Task PlayVideoAsync(ListenQueueItem item, string? sourceLabel = null, CancellationToken ct = default)
    {
        if (!MediaKindClassifier.IsVideo(item.MediaType))
        {
            throw new ArgumentException("The queue item is not a video.", nameof(item));
        }

        await PlayQueueItemCoreAsync(item, sourceLabel, ct);
    }

    public Task StartAudiobookAsync(AudiobookStartRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startKind = NormalizeAudiobookStartKind(request.StartKind);
        var exact = !string.Equals(startKind, AudiobookStartKinds.Resume, StringComparison.Ordinal);
        var item = request.Item with
        {
            MediaType = "Audiobooks",
            InitialPositionSeconds = request.PositionSeconds ?? request.Item.InitialPositionSeconds,
            ChapterIndex = request.ChapterIndex ?? request.Item.ChapterIndex,
            StartAtExactPosition = exact || request.Item.StartAtExactPosition,
            AudiobookStartKind = startKind,
        };

        return PlayQueueItemCoreAsync(item, request.SourceLabel ?? item.Album ?? item.Title, ct);
    }

    public Task PlayAudiobookChapterAsync(ListenQueueItem item, PlaybackChapterDto chapter, string? sourceLabel = null, CancellationToken ct = default)
    {
        var normalized = NormalizeChapter(chapter, chapter.Index);
        var chapterItem = item with
        {
            AssetId = chapter.AssetId ?? item.AssetId,
            StreamUrl = chapter.AssetId.HasValue && chapter.AssetId != item.AssetId ? null : item.StreamUrl,
            Manifest = chapter.AssetId.HasValue && chapter.AssetId != item.AssetId ? null : item.Manifest,
            MediaType = "Audiobooks",
            Subtitle = normalized.Title,
            InitialPositionSeconds = normalized.StartSeconds,
            ChapterIndex = normalized.Index,
            StartAtExactPosition = true,
        };

        return StartAudiobookAsync(
            new AudiobookStartRequest(chapterItem, AudiobookStartKinds.Chapter, normalized.StartSeconds, normalized.Index, sourceLabel ?? item.Album ?? item.Title),
            ct);
    }

    public Task PlayAudiobookChapterAsync(int chapterIndex, CancellationToken ct = default)
    {
        var current = CurrentItem;
        if (current is null)
        {
            return Task.CompletedTask;
        }

        var chapter = current.Chapters.FirstOrDefault(item => item.Index == chapterIndex)
            ?? current.Chapters.ElementAtOrDefault(Math.Clamp(chapterIndex, 0, Math.Max(0, current.Chapters.Count - 1)));
        return chapter is null
            ? Task.CompletedTask
            : PlayAudiobookChapterAsync(current, chapter, SourceLabel, ct);
    }

    private async Task PlayQueueItemCoreAsync(ListenQueueItem item, string? sourceLabel = null, CancellationToken ct = default)
    {
        using var request = BeginPlaybackRequest(ct);
        ct = request.Token;
        try
        {
        ArgumentNullException.ThrowIfNull(item);

        item = BootstrapDirectStream(item);
        if (MediaKindClassifier.IsAudiobook(item.MediaType))
        {
            var startKind = NormalizeAudiobookStartKind(item.AudiobookStartKind);
            item = item with { AudiobookStartKind = startKind };
            _currentAudiobookStartKind = startKind;
        }
        else
        {
            _currentAudiobookStartKind = null;
        }
        RememberCurrentItem();
        _queue.Clear();
        _queue.Add(item);
        CurrentIndex = 0;
        SourceLabel = sourceLabel ?? item.Album ?? item.Title;
        Experience = MediaKindClassifier.ToPlayerExperienceString(MediaKindClassifier.Classify(item.MediaType));
        PresentationSurface = IsVideoMode ? PlaybackPresentationSurface.PrimaryVideo : PlaybackPresentationSurface.Docked;
        IsVideoExpanded = IsVideoMode;
        var startSettings = await PlaybackSettingsAsync(ct);
        ct.ThrowIfCancellationRequested();
        ApplyExperienceSettings(startSettings);
        IsDismissed = false;
        var startPosition = await InitialPositionForAsync(item, ct);
        ct.ThrowIfCancellationRequested();
        CurrentTimeSeconds = startPosition;
        DurationSeconds = 0;
        var startRate = await InitialPlaybackRateForAsync(item, ct);
        ct.ThrowIfCancellationRequested();
        PlaybackRate = startRate;
        IsPlaying = true;
        _stateMachine.SetLoading();
        NeedsUserGestureToStart = false;
        CurrentError = null;
        NotifyChanged();
        var startRequested = await TryStartCurrentAudioAsync();
        ct.ThrowIfCancellationRequested();
        await EnsurePlayableAsync(CurrentIndex, ct);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(CurrentBrowserStreamUrl))
        {
            NotifyChanged();
            return;
        }

        if (!startRequested)
        {
            await TryStartCurrentAudioAsync();
        ct.ThrowIfCancellationRequested();
        }
        else
        {
            NotifyChanged();
        }
        await RefreshAudiobookHistoryAsync(ct);
        ct.ThrowIfCancellationRequested();
        await SyncReplaceQueueAsync([_queue[CurrentIndex]], 0, SourceLabel, false, ct);
        ct.ThrowIfCancellationRequested();
    
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_startCancellation, request)) _startCancellation = null;
        }
    }

    public async Task ReplaceQueueAsync(
        IEnumerable<WorkViewModel> works,
        int startIndex,
        string? sourceLabel,
        bool shuffle,
        CancellationToken ct = default)
    {
        var items = works.Select(ListenQueueItemFactory.Create).ToList();
        await ReplaceQueueItemsAsync(items, startIndex, sourceLabel, shuffle, ct);
    }

    public async Task ReplaceQueueItemsAsync(
        IEnumerable<ListenQueueItem> queueItems,
        int startIndex,
        string? sourceLabel,
        bool shuffle,
        CancellationToken ct = default)
    {
        using var request = BeginPlaybackRequest(ct);
        ct = request.Token;
        try
        {
        var items = queueItems
            .Where(item => item.WorkId != Guid.Empty)
            .ToList();
        if (items.Count == 0)
        {
            ClosePlayer();
            return;
        }

        var queueExperiences = items
            .Select(item => MediaKindClassifier.Classify(item.MediaType))
            .Distinct()
            .ToList();
        if (queueExperiences.Count > 1)
        {
            items = [items[Math.Clamp(startIndex, 0, items.Count - 1)]];
            startIndex = 0;
            shuffle = false;
        }

        if (items.Any(item => MediaKindClassifier.IsAudiobook(item.MediaType)))
        {
            var selected = items[Math.Clamp(startIndex, 0, items.Count - 1)];
            items =
            [
                selected with
                {
                    MediaType = "Audiobooks",
                    AudiobookStartKind = AudiobookStartKinds.Resume,
                },
            ];
            startIndex = 0;
            shuffle = false;
        }

        RememberCurrentItem();

        items = items.Select(BootstrapDirectStream).ToList();
        if (shuffle)
        {
            var random = new Random();
            items = items.OrderBy(_ => random.Next()).ToList();
            startIndex = 0;
        }

        _queue.Clear();
        _queue.AddRange(items);
        CurrentIndex = Math.Clamp(startIndex, 0, _queue.Count - 1);
        SourceLabel = sourceLabel;
        var retainAudioNowPlaying = !IsVideoMode && PresentationSurface == PlaybackPresentationSurface.NowPlaying;
        Experience = MediaKindClassifier.ToPlayerExperienceString(MediaKindClassifier.Classify(_queue[CurrentIndex].MediaType));
        PresentationSurface = IsVideoMode
            ? PlaybackPresentationSurface.PrimaryVideo
            : retainAudioNowPlaying ? PlaybackPresentationSurface.NowPlaying : PlaybackPresentationSurface.Docked;
        IsVideoExpanded = IsVideoMode;
        var startSettings = await PlaybackSettingsAsync(ct);
        ct.ThrowIfCancellationRequested();
        ApplyExperienceSettings(startSettings);
        _currentAudiobookStartKind = MediaKindClassifier.IsAudiobook(_queue[CurrentIndex].MediaType)
            ? NormalizeAudiobookStartKind(_queue[CurrentIndex].AudiobookStartKind)
            : null;
        IsDismissed = false;
        var startPosition = await InitialPositionForAsync(_queue[CurrentIndex], ct);
        ct.ThrowIfCancellationRequested();
        CurrentTimeSeconds = startPosition;
        DurationSeconds = 0;
        var startRate = await InitialPlaybackRateForAsync(_queue[CurrentIndex], ct);
        ct.ThrowIfCancellationRequested();
        PlaybackRate = startRate;
        IsPlaying = true;
        _stateMachine.SetLoading();
        NeedsUserGestureToStart = false;
        CurrentError = null;
        NotifyChanged();
        var startRequested = await TryStartCurrentAudioAsync();
        ct.ThrowIfCancellationRequested();
        await EnsurePlayableAsync(CurrentIndex, ct);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(CurrentBrowserStreamUrl))
        {
            NotifyChanged();
            return;
        }

        if (!startRequested)
        {
            await TryStartCurrentAudioAsync();
        ct.ThrowIfCancellationRequested();
        }
        else
        {
            NotifyChanged();
        }
        await RefreshAudiobookHistoryAsync(ct);
        ct.ThrowIfCancellationRequested();
        await SyncReplaceQueueAsync(items, CurrentIndex, sourceLabel, shuffle, ct);
        ct.ThrowIfCancellationRequested();
    
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_startCancellation, request)) _startCancellation = null;
        }
    }

    public async Task InsertNextAsync(WorkViewModel work, CancellationToken ct = default)
    {
        var item = ListenQueueItemFactory.Create(work);
        if (_queue.Count == 0)
        {
            await PlayQueueItemAsync(item, item.Album ?? item.Title, ct);
            return;
        }

        if (MediaKindClassifier.IsAudiobook(item.MediaType))
        {
            await PlayAudiobookAsync(item, item.Album ?? item.Title, ct);
            return;
        }
        if (MediaKindClassifier.IsVideo(item.MediaType))
        {
            await PlayVideoAsync(item, item.Album ?? item.Title, ct);
            return;
        }

        var insertIndex = Math.Clamp(CurrentIndex + 1, 0, _queue.Count);
        _queue.Insert(insertIndex, item);
        NotifyChanged();
        await SyncAddQueueItemsAsync([item], PlayerQueueMutationModes.AddNext, ct);
    }

    public async Task AddToQueueAsync(WorkViewModel work, CancellationToken ct = default)
    {
        var item = ListenQueueItemFactory.Create(work);
        await AddQueueItemAsync(item, next: false, ct);
    }

    public async Task AddQueueItemAsync(ListenQueueItem item, bool next = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (_queue.Count == 0)
        {
            await PlayQueueItemAsync(item, item.Album ?? item.Title, ct);
            return;
        }

        if (MediaKindClassifier.IsAudiobook(item.MediaType))
        {
            await PlayAudiobookAsync(item, item.Album ?? item.Title, ct);
            return;
        }
        if (MediaKindClassifier.IsVideo(item.MediaType))
        {
            await PlayVideoAsync(item, item.Album ?? item.Title, ct);
            return;
        }

        var mutationMode = PlayerQueueMutationModes.AddEnd;
        if (next)
        {
            var insertIndex = Math.Clamp(CurrentIndex + 1, 0, _queue.Count);
            _queue.Insert(insertIndex, item);
            mutationMode = PlayerQueueMutationModes.AddNext;
        }
        else
        {
            _queue.Add(item);
        }

        NotifyChanged();
        await SyncAddQueueItemsAsync([item], mutationMode, ct);
    }

    public async Task BeginViewSessionAsync(Guid assetId, ViewPlaybackKind kind, CancellationToken ct = default)
    {
        if (assetId == Guid.Empty) throw new ArgumentException("A View asset identity is required.", nameof(assetId));
        if (ViewSession is { } current && current.AssetId == assetId && current.Kind == kind) return;

        // Cancel a catalogue start before the viewer's authorized browser stream begins.
        if (HasQueue && IsVideoMode)
        {
            await DispatchAsync(PlaybackCommand.Pause(), ct);
            ClosePlayer();
        }
        else if (HasQueue && IsPlaying)
            await DispatchAsync(PlaybackCommand.Pause(), ct);
        else
            ReservePlaybackRequest();

        ViewSession = new ViewPlaybackSessionState(assetId, kind,
            PresentationSurface: kind == ViewPlaybackKind.Video
                ? PlaybackPresentationSurface.PrimaryVideo
                : PlaybackPresentationSurface.Docked);
        NotifyChanged(PlaybackChangeKind.View);
    }

    public void UpdateViewSession(
        Guid assetId,
        AudioTransportState transport,
        PlaybackPresentationSurface? surface = null)
    {
        if (ViewSession is not { } current || current.AssetId != assetId) return;

        ViewSession = current with
        {
            PositionSeconds = ValidNonnegative(transport.CurrentTimeSeconds, current.PositionSeconds),
            DurationSeconds = ValidNonnegative(transport.DurationSeconds, current.DurationSeconds),
            IsPlaying = transport.IsPlaying ?? current.IsPlaying,
            Volume = transport.Volume is { } volume && double.IsFinite(volume) ? Math.Clamp(volume, 0, 1) : current.Volume,
            IsMuted = transport.IsMuted ?? current.IsMuted,
            PlaybackRate = transport.PlaybackRate is { } rate && double.IsFinite(rate) && rate > 0 ? rate : current.PlaybackRate,
            PresentationSurface = surface ?? current.PresentationSurface,
        };
        NotifyChanged(PlaybackChangeKind.View);
    }

    public void EndViewSession(Guid? assetId = null)
    {
        if (ViewSession is null || assetId.HasValue && ViewSession.AssetId != assetId.Value) return;
        ViewSession = null;
        NotifyChanged(PlaybackChangeKind.View);
    }

    private static double ValidNonnegative(double? value, double fallback) =>
        value is { } number && double.IsFinite(number) ? Math.Max(0, number) : fallback;

    public async Task<bool> AppendVideoNextUpAsync(
        ListenQueueItem item,
        Guid expectedCurrentWorkId,
        long expectedRequestVersion,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ct.ThrowIfCancellationRequested();
        if (!MediaKindClassifier.IsVideo(item.MediaType)
            || !IsVideoMode
            || CurrentItem?.WorkId != expectedCurrentWorkId
            || PlaybackRequestVersion != expectedRequestVersion
            || _queue.Any(queued => queued.WorkId == item.WorkId))
            return false;

        _queue.Add(BootstrapDirectStream(item));
        NotifyChanged();
        await SyncAddQueueItemsAsync([item], PlayerQueueMutationModes.AddEnd, ct);
        return true;
    }

    public async Task PlayIndexAsync(int index, CancellationToken ct = default)
    {
        using var request = BeginPlaybackRequest(ct);
        ct = request.Token;
        try
        {
        if (index < 0 || index >= _queue.Count)
        {
            return;
        }

        if (index != CurrentIndex)
        {
            RememberCurrentItem();
        }

        CurrentIndex = index;
        Experience = MediaKindClassifier.ToPlayerExperienceString(MediaKindClassifier.Classify(_queue[CurrentIndex].MediaType));
        var startSettings = await PlaybackSettingsAsync(ct);
        ct.ThrowIfCancellationRequested();
        ApplyExperienceSettings(startSettings);
        var startPosition = await InitialPositionForAsync(_queue[CurrentIndex], ct);
        ct.ThrowIfCancellationRequested();
        CurrentTimeSeconds = startPosition;
        DurationSeconds = 0;
        var startRate = await InitialPlaybackRateForAsync(_queue[CurrentIndex], ct);
        ct.ThrowIfCancellationRequested();
        PlaybackRate = startRate;
        IsDismissed = false;
        IsPlaying = true;
        _stateMachine.SetLoading();
        NeedsUserGestureToStart = false;
        CurrentError = null;
        await EnsurePlayableAsync(CurrentIndex, ct);
        ct.ThrowIfCancellationRequested();
        await RefreshAudiobookHistoryAsync(ct);
        ct.ThrowIfCancellationRequested();
        MarkPlaybackStart();
        NotifyChanged();
    
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_startCancellation, request)) _startCancellation = null;
        }
    }

    public async Task<bool> SkipNextAsync(CancellationToken ct = default)
    {
        var nextIndex = ResolveNextIndex(automaticAdvance: false);
        if (!nextIndex.HasValue)
        {
            CurrentTimeSeconds = DurationSeconds;
            IsPlaying = false;
            _stateMachine.SetEnded();
            NotifyChanged();
            return false;
        }

        await PlayIndexAsync(nextIndex.Value, ct);
        return true;
    }

    public async Task SkipPreviousAsync(CancellationToken ct = default)
    {
        using var request = BeginPlaybackRequest(ct);
        ct = request.Token;
        try
        {
        if (CurrentIndex <= 0)
        {
            CurrentTimeSeconds = 0;
            NotifyChanged();
            return;
        }

        CurrentIndex--;
        Experience = MediaKindClassifier.ToPlayerExperienceString(MediaKindClassifier.Classify(_queue[CurrentIndex].MediaType));
        var startSettings = await PlaybackSettingsAsync(ct);
        ct.ThrowIfCancellationRequested();
        ApplyExperienceSettings(startSettings);
        var startPosition = await InitialPositionForAsync(_queue[CurrentIndex], ct);
        ct.ThrowIfCancellationRequested();
        CurrentTimeSeconds = startPosition;
        DurationSeconds = 0;
        var startRate = await InitialPlaybackRateForAsync(_queue[CurrentIndex], ct);
        ct.ThrowIfCancellationRequested();
        PlaybackRate = startRate;
        IsPlaying = true;
        _stateMachine.SetLoading();
        NeedsUserGestureToStart = false;
        CurrentError = null;
        await EnsurePlayableAsync(CurrentIndex, ct);
        ct.ThrowIfCancellationRequested();
        await RefreshAudiobookHistoryAsync(ct);
        ct.ThrowIfCancellationRequested();
        MarkPlaybackStart();
        NotifyChanged();
    
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_startCancellation, request)) _startCancellation = null;
        }
    }

    public async Task CompleteCurrentAsync(CancellationToken ct = default)
    {
        await ReportHeartbeatAsync(force: true, ct, hasPlaybackEnded: true);
        if (IsAudiobookMode && CurrentItem is { } recording && recording.Chapters.Any(chapter => chapter.AssetId.HasValue))
        {
            var lastPartChapter = recording.Chapters.Where(chapter => chapter.AssetId == recording.AssetId)
                .Select(chapter => chapter.Index).DefaultIfEmpty(-1).Max();
            var nextPart = recording.Chapters.FirstOrDefault(chapter => chapter.Index > lastPartChapter && chapter.AssetId != recording.AssetId);
            if (nextPart is not null)
            {
                await PlayAudiobookChapterAsync(recording, nextPart, SourceLabel, ct);
                return;
            }
        }
        var nextIndex = ResolveNextIndex(automaticAdvance: true);
        if (CurrentItem is not null
            && (!nextIndex.HasValue || nextIndex.Value == CurrentIndex))
        {
            AddHistoryItem(CurrentItem with { PlayedAt = DateTimeOffset.UtcNow });
        }

        if (!nextIndex.HasValue)
        {
            IsPlaying = false;
            CurrentTimeSeconds = DurationSeconds;
            _stateMachine.SetEnded();
            NotifyChanged();
            return;
        }

        await PlayIndexAsync(nextIndex.Value, ct);
    }

    private int? ResolveNextIndex(bool automaticAdvance)
    {
        if (_queue.Count == 0 || CurrentIndex < 0)
        {
            return null;
        }

        if (automaticAdvance && RepeatMode == PlayerRepeatModes.One)
        {
            return CurrentIndex;
        }

        if (ShuffleEnabled && _queue.Count > 1)
        {
            var candidate = Random.Shared.Next(_queue.Count - 1);
            return candidate >= CurrentIndex ? candidate + 1 : candidate;
        }

        if (CurrentIndex + 1 < _queue.Count)
        {
            return CurrentIndex + 1;
        }

        return RepeatMode == PlayerRepeatModes.All ? 0 : null;
    }

    public void RemoveUpcomingAt(int absoluteIndex)
    {
        if (absoluteIndex < 0 || absoluteIndex >= _queue.Count || absoluteIndex <= CurrentIndex)
        {
            return;
        }

        _queue.RemoveAt(absoluteIndex);
        NotifyChanged();
    }

    public void ClearUpcoming()
    {
        if (_queue.Count == 0)
        {
            return;
        }

        if (CurrentIndex < 0)
        {
            _queue.Clear();
            CurrentIndex = -1;
        }
        else if (CurrentIndex + 1 < _queue.Count)
        {
            _queue.RemoveRange(CurrentIndex + 1, _queue.Count - (CurrentIndex + 1));
        }

        NotifyChanged();
    }

    public void TogglePanel()
    {
        IsPanelOpen = !IsPanelOpen;
        NotifyChanged();
    }

    public void ClosePanel()
    {
        if (!IsPanelOpen)
        {
            return;
        }

        IsPanelOpen = false;
        NotifyChanged();
    }

    public void SetActiveTab(string tab)
    {
        ActiveTab = tab.ToLowerInvariant() switch
        {
            ListenPlaybackTabs.History => ListenPlaybackTabs.History,
            ListenPlaybackTabs.Lyrics => ListenPlaybackTabs.Lyrics,
            _ => ListenPlaybackTabs.Queue,
        };
        NotifyChanged();
    }

    public void UpdateTransportState(
        double? currentTimeSeconds = null,
        double? durationSeconds = null,
        bool? isPlaying = null,
        double? volume = null,
        bool? isMuted = null,
        double? playbackRate = null,
        bool? needsUserGestureToStart = null)
    {
        _stateMachine.SetTransportState(isPlaying, needsUserGestureToStart, CurrentError);
        var now = DateTimeOffset.UtcNow;
        var positionChanged = false;
        var structuralChanged = false;

        if (currentTimeSeconds.HasValue)
        {
            var next = Math.Max(0, currentTimeSeconds.Value);
            if (Math.Abs(CurrentTimeSeconds - next) >= 1)
            {
                CurrentTimeSeconds = next;
                positionChanged = true;
            }
        }

        if (durationSeconds.HasValue)
        {
            var next = Math.Max(0, IsVideoMode && CurrentItem?.Manifest?.DurationSeconds is > 0
                ? CurrentItem.Manifest.DurationSeconds.Value : durationSeconds.Value);
            if (Math.Abs(DurationSeconds - next) >= 1)
            {
                DurationSeconds = next;
                structuralChanged = true;
            }
        }

        if (isPlaying.HasValue)
        {
            if (IsPlaying != isPlaying.Value)
            {
                IsPlaying = isPlaying.Value;
                structuralChanged = true;
            }
        }

        if (volume.HasValue)
        {
            var next = Math.Clamp(volume.Value, 0d, 1d);
            if (Math.Abs(Volume - next) >= 0.01d)
            {
                Volume = next;
                structuralChanged = true;
            }
        }

        if (isMuted.HasValue)
        {
            if (IsMuted != isMuted.Value)
            {
                IsMuted = isMuted.Value;
                structuralChanged = true;
            }
        }

        if (playbackRate.HasValue)
        {
            var next = Math.Clamp(playbackRate.Value, 0.5d, 32d);
            if (Math.Abs(PlaybackRate - next) >= 0.01d)
            {
                PlaybackRate = next;
                structuralChanged = true;
            }
        }

        if (needsUserGestureToStart.HasValue && NeedsUserGestureToStart != needsUserGestureToStart.Value)
        {
            NeedsUserGestureToStart = needsUserGestureToStart.Value;
            structuralChanged = true;
        }

        if (SleepTimerMode == ListenSleepTimerModes.EndOfChapter
            && IsPlaying
            && CurrentItem is { Chapters.Count: > 0 } current
            && ResolveCurrentChapter(current, CurrentTimeSeconds) is { EndSeconds: { } endSeconds }
            && CurrentTimeSeconds >= endSeconds - 0.5d)
        {
            _ = ExpireSleepTimerAsync();
        }

        if (structuralChanged || positionChanged && now - _lastTransportUiNotificationAt >= TimeSpan.FromMilliseconds(_clientSettings.TransportUiUpdateIntervalMilliseconds))
        {
            _lastTransportUiNotificationAt = now;
            NotifyChanged(structuralChanged ? PlaybackChangeKind.TransportState : PlaybackChangeKind.TransportTick);
        }
    }

    public async Task RequestTransportCommandAsync(PlaybackTransportCommand command)
    {
        command = EnsureTransportRequestId(command);
        if (!_transportHostReady || TransportCommandRequested is null)
        {
            QueuePendingTransportCommand(command);
            return;
        }

        await DispatchTransportCommandAsync(command);
    }

    public async Task SetTransportHostReadyAsync()
    {
        _transportHostReady = true;
        if (TransportCommandRequested is null || _pendingTransportCommands.Count == 0)
        {
            return;
        }

        var commands = _pendingTransportCommands.ToList();
        _pendingTransportCommands.Clear();
        foreach (var command in commands)
        {
            await DispatchTransportCommandAsync(command);
        }
    }

    public void SetTransportHostNotReady()
    {
        _transportHostReady = false;
    }

    private void QueuePendingTransportCommand(PlaybackTransportCommand command)
    {
        if (IsCoalescibleTransportAction(command.Action))
        {
            _pendingTransportCommands.RemoveAll(item => string.Equals(item.Action, command.Action, StringComparison.OrdinalIgnoreCase));
        }

        if (_pendingTransportCommands.Count >= _clientSettings.PendingTransportCommandLimit)
        {
            _pendingTransportCommands.RemoveAt(0);
        }

        _pendingTransportCommands.Add(command);
    }

    private PlaybackTransportCommand EnsureTransportRequestId(PlaybackTransportCommand command)
    {
        if (command.RequestId is { } explicitId)
        {
            AdvanceTransportRequestIdTo(explicitId);
            return command;
        }

        long next;
        long observed;
        do
        {
            observed = Interlocked.Read(ref _nextTransportRequestId);
            next = Math.Max(observed, Math.Max(Interlocked.Read(ref _lastDispatchedTransportRequestId), PlaybackStartVersion)) + 1;
        } while (Interlocked.CompareExchange(ref _nextTransportRequestId, next, observed) != observed);

        return command with { RequestId = next };
    }

    private void AdvanceTransportRequestIdTo(long minimum)
    {
        long observed;
        do
        {
            observed = Interlocked.Read(ref _nextTransportRequestId);
            if (observed >= minimum) return;
        } while (Interlocked.CompareExchange(ref _nextTransportRequestId, minimum, observed) != observed);
    }

    private async Task DispatchTransportCommandAsync(PlaybackTransportCommand command)
    {
        if (TransportCommandRequested is null)
        {
            QueuePendingTransportCommand(command);
            return;
        }

        if (command.RequestId is { } requestId && requestId <= Interlocked.Read(ref _lastDispatchedTransportRequestId))
        {
            return;
        }

        await TransportCommandRequested.Invoke(command);

        if (command.RequestId is { } completedRequestId)
        {
            Interlocked.Exchange(ref _lastDispatchedTransportRequestId, completedRequestId);
        }
    }

    private static bool IsCoalescibleTransportAction(string action) => action.ToLowerInvariant() switch
    {
        "start" or "pause" or "seek" or "set-volume" or "set-speed" => true,
        _ => false,
    };

    private PlaybackTransportCommand CreateStartCommand() => new(
        "start",
        Value: CurrentTimeSeconds,
        StreamUrl: CurrentBrowserStreamUrl,
        PositionSeconds: CurrentTimeSeconds,
        PlaybackRate: PlaybackRate,
        RequestId: PlaybackStartVersion,
        AudiobookStartKind: _currentAudiobookStartKind);

    private async Task<bool> TryStartCurrentAudioAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentBrowserStreamUrl))
        {
            return false;
        }

        MarkPlaybackStart();
        await RequestTransportCommandAsync(CreateStartCommand());
        NotifyChanged();
        return true;
    }

    public async Task SkipBackAsync(CancellationToken ct = default)
    {
        var settings = await PlaybackSettingsAsync(ct);
        var seconds = IsVideoMode ? settings.Watching.SkipBackSeconds : settings.Listening.SkipBackSeconds;
        await SeekRelativeAsync(-seconds, ct);
    }

    public async Task SkipForwardAsync(CancellationToken ct = default)
    {
        var settings = await PlaybackSettingsAsync(ct);
        var seconds = IsVideoMode ? settings.Watching.SkipForwardSeconds : settings.Listening.SkipForwardSeconds;
        await SeekRelativeAsync(seconds, ct);
    }

    public async Task SeekRelativeAsync(double deltaSeconds, CancellationToken ct = default)
    {
        var max = DurationSeconds > 0 ? DurationSeconds : double.MaxValue;
        var next = Math.Clamp(CurrentTimeSeconds + deltaSeconds, 0, max);
        CurrentTimeSeconds = next;
        NotifyChanged();
        await RequestTransportCommandAsync(new("seek", next));
        await ReportHeartbeatAsync(force: true, ct);
    }

    public async Task PlayNextChapterAsync(CancellationToken ct = default)
    {
        var current = CurrentItem;
        if (current?.Chapters.Count is not > 0)
        {
            await SkipForwardAsync(ct);
            return;
        }

        var chapter = ResolveCurrentChapter(current, CurrentTimeSeconds);
        var next = current.Chapters.FirstOrDefault(item => item.StartSeconds > CurrentTimeSeconds + 0.5d)
            ?? (chapter is null ? current.Chapters.FirstOrDefault() : current.Chapters.FirstOrDefault(item => item.Index > chapter.Index));
        if (next is null)
        {
            return;
        }

        await PlayAudiobookChapterAsync(current, next, SourceLabel, ct);
    }

    public async Task PlayPreviousChapterAsync(CancellationToken ct = default)
    {
        var current = CurrentItem;
        if (current?.Chapters.Count is not > 0)
        {
            await SkipBackAsync(ct);
            return;
        }

        var chapter = ResolveCurrentChapter(current, CurrentTimeSeconds);
        var previous = current.Chapters.LastOrDefault(item => item.StartSeconds < CurrentTimeSeconds - 3d)
            ?? (chapter is null ? current.Chapters.FirstOrDefault() : current.Chapters.LastOrDefault(item => item.Index < chapter.Index))
            ?? current.Chapters.FirstOrDefault();
        if (previous is null)
        {
            return;
        }

        await PlayAudiobookChapterAsync(current, previous, SourceLabel, ct);
    }

    public async Task CyclePlaybackRateAsync(CancellationToken ct = default)
    {
        var rates = await SupportedPlaybackRatesAsync(ct);
        var currentIndex = rates.FindIndex(rate => Math.Abs(rate - PlaybackRate) < 0.01d);
        var next = rates[(currentIndex + 1 + rates.Count) % rates.Count];
        await SetPlaybackRateAsync(next, ct);
    }

    public async Task SetPlaybackRateAsync(double rate, CancellationToken ct = default)
    {
        var next = Math.Clamp(Math.Round(rate, 1), 0.5d, 3d);
        PlaybackRate = next;
        NotifyChanged();
        await RequestTransportCommandAsync(new("set-speed", next));
        await ReportHeartbeatAsync(force: true, ct);
    }

    public async Task PlayAudiobookHistoryAsync(AudiobookListenHistoryItemDto history, CancellationToken ct = default)
    {
        var current = CurrentItem;
        var item = (current ?? new ListenQueueItem
        {
            WorkId = history.WorkId,
            AssetId = history.AssetId,
            MediaType = "Audiobooks",
            Title = history.Title,
        }) with
        {
            WorkId = history.WorkId,
            AssetId = history.AssetId,
            MediaType = "Audiobooks",
            Title = string.IsNullOrWhiteSpace(history.Title) ? current?.Title ?? "Audiobook" : history.Title,
            Subtitle = history.ChapterTitle ?? current?.Subtitle,
            InitialPositionSeconds = history.PositionSeconds,
            ChapterIndex = history.ChapterIndex,
            StartAtExactPosition = true,
            Duration = history.DurationSeconds.HasValue ? PlaybackTimeParser.FormatDuration(history.DurationSeconds.Value) : current?.Duration,
        };

        await StartAudiobookAsync(new AudiobookStartRequest(item, AudiobookStartKinds.History, history.PositionSeconds, history.ChapterIndex, item.Title), ct);
    }

    public async Task PlayAudiobookBookmarkAsync(AudiobookBookmarkDto bookmark, CancellationToken ct = default)
    {
        var current = CurrentItem;
        var item = (current ?? new ListenQueueItem
        {
            WorkId = bookmark.WorkId,
            AssetId = bookmark.AssetId,
            MediaType = "Audiobooks",
            Title = bookmark.Label ?? "Audiobook",
        }) with
        {
            WorkId = bookmark.WorkId,
            AssetId = bookmark.AssetId,
            MediaType = "Audiobooks",
            Subtitle = bookmark.ChapterTitle ?? current?.Subtitle,
            InitialPositionSeconds = bookmark.PositionSeconds,
            ChapterIndex = bookmark.ChapterIndex,
            StartAtExactPosition = true,
            Duration = bookmark.DurationSeconds.HasValue ? PlaybackTimeParser.FormatDuration(bookmark.DurationSeconds.Value) : current?.Duration,
        };

        await StartAudiobookAsync(new AudiobookStartRequest(item, AudiobookStartKinds.Bookmark, bookmark.PositionSeconds, bookmark.ChapterIndex, item.Title), ct);
    }

    public async Task<AudiobookBookmarkDto?> AddAudiobookBookmarkAsync(CancellationToken ct = default)
    {
        var current = CurrentItem;
        if (!IsAudiobookMode || current?.AssetId is not Guid assetId || current.WorkId == Guid.Empty || _apiClient is null)
        {
            return null;
        }

        var chapter = ResolveCurrentChapter(current, CurrentTimeSeconds);
        var bookmark = await _apiClient.CreateAudiobookBookmarkAsync(
            current.WorkId,
            new CreateAudiobookBookmarkRequestDto
            {
                AssetId = assetId,
                ChapterIndex = chapter?.Index ?? current.ChapterIndex,
                ChapterTitle = chapter?.Title ?? current.Subtitle,
                PositionSeconds = CurrentTimeSeconds,
                DurationSeconds = DurationSeconds > 0 ? DurationSeconds : PlaybackTimeParser.TryParseDurationSeconds(current.Duration),
                Label = chapter is null
                    ? $"Left off at {PlaybackTimeParser.FormatDuration(CurrentTimeSeconds)}"
                    : $"{chapter.Title} - {PlaybackTimeParser.FormatDuration(CurrentTimeSeconds)}",
            },
            ct: ct);

        if (bookmark is not null)
        {
            _audiobookBookmarks.Insert(0, bookmark);
            NotifyChanged();
        }

        return bookmark;
    }

    public async Task DeleteAudiobookBookmarkAsync(Guid bookmarkId, CancellationToken ct = default)
    {
        if (_apiClient is null)
        {
            return;
        }

        if (await _apiClient.DeleteAudiobookBookmarkAsync(bookmarkId, ct: ct))
        {
            _audiobookBookmarks.RemoveAll(item => item.Id == bookmarkId);
            NotifyChanged();
        }
    }

    public Task SetSleepTimerAsync(TimeSpan duration, CancellationToken ct = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            return CancelSleepTimerAsync();
        }

        _sleepTimerCts?.Cancel();
        _sleepTimerCts?.Dispose();
        _sleepTimerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        SleepTimerMode = ListenSleepTimerModes.Timer;
        SleepTimerEndsAtUtc = DateTimeOffset.UtcNow.Add(duration);
        NotifyChanged();
        _ = CompleteSleepTimerAfterDelayAsync(duration, _sleepTimerCts.Token);
        return Task.CompletedTask;
    }

    public Task SetSleepTimerEndOfChapterAsync()
    {
        if (!AllowEndOfChapterSleepTimer)
        {
            return CancelSleepTimerAsync();
        }

        _sleepTimerCts?.Cancel();
        _sleepTimerCts?.Dispose();
        _sleepTimerCts = null;
        SleepTimerMode = ListenSleepTimerModes.EndOfChapter;
        SleepTimerEndsAtUtc = null;
        NotifyChanged();
        return Task.CompletedTask;
    }

    public Task CancelSleepTimerAsync()
    {
        _sleepTimerCts?.Cancel();
        _sleepTimerCts?.Dispose();
        _sleepTimerCts = null;
        SleepTimerMode = ListenSleepTimerModes.Off;
        SleepTimerEndsAtUtc = null;
        NotifyChanged();
        return Task.CompletedTask;
    }

    public async Task ToggleShuffleAsync(CancellationToken ct = default)
    {
        var next = !ShuffleEnabled;
        ShuffleEnabled = next;
        NotifyChanged(PlaybackChangeKind.TransportState);
        await SyncPlayerModeAsync(
            PlayerCommands.Shuffle,
            shuffleEnabled: next,
            repeatMode: null,
            ct);
    }

    public async Task CycleRepeatModeAsync(CancellationToken ct = default)
    {
        var next = RepeatMode switch
        {
            PlayerRepeatModes.Off => PlayerRepeatModes.All,
            PlayerRepeatModes.All => PlayerRepeatModes.One,
            _ => PlayerRepeatModes.Off,
        };
        RepeatMode = next;
        NotifyChanged(PlaybackChangeKind.TransportState);
        await SyncPlayerModeAsync(
            PlayerCommands.Repeat,
            shuffleEnabled: null,
            repeatMode: next,
            ct);
    }

    public async Task ReportHeartbeatAsync(
        bool force = false,
        CancellationToken ct = default,
        bool hasPlaybackEnded = false)
    {
        if (_apiClient is null || CurrentItem?.AssetId is not Guid assetId)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastHeartbeatAt < TimeSpan.FromSeconds(_clientSettings.HeartbeatIntervalSeconds))
        {
            return;
        }

        _lastHeartbeatAt = now;
        try
        {
            var profile = await _orchestrator.GetActiveProfileAsync(ct);
            var current = CurrentItem;
            var chapter = current is null ? null : ResolveCurrentChapter(current, CurrentTimeSeconds);
            var state = await _apiClient.PostPlayerHeartbeatAsync(new PlayerHeartbeatDto
            {
                ProfileId = profile?.Id,
                SessionId = _sessionId == Guid.Empty ? null : _sessionId,
                Sequence = Interlocked.Increment(ref _telemetrySequence),
                DeviceId = _clientContext.DeviceId,
                Client = _clientContext.Client,
                AssetId = assetId,
                IsPlaying = IsPlaying,
                HasPlaybackEnded = hasPlaybackEnded,
                PositionSeconds = CurrentTimeSeconds,
                DurationSeconds = DurationSeconds > 0 ? DurationSeconds : null,
                ProgressPct = DurationSeconds > 0 ? Math.Clamp(CurrentTimeSeconds / DurationSeconds * 100d, 0d, 100d) : null,
                Volume = Volume,
                IsMuted = IsMuted,
                PlaybackRate = PlaybackRate,
                ChapterIndex = chapter?.Index ?? current?.ChapterIndex,
                ChapterTitle = chapter?.Title ?? current?.Subtitle,
                AudiobookStartKind = IsAudiobookMode
                    ? NormalizeAudiobookStartKind(_currentAudiobookStartKind ?? current?.AudiobookStartKind)
                    : null,
                Connection = ToConnectionContext(),
            }, ct);
            ApplyPlayerState(state);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not sync listen player heartbeat.");
        }
    }

    public void MarkCurrentFailed(string message)
    {
        CurrentError = string.IsNullOrWhiteSpace(message)
            ? "Playback failed for this item."
            : message;
        IsPlaying = false;
        NeedsUserGestureToStart = false;
        _stateMachine.SetTransportState(false, false, CurrentError);
        NotifyChanged();
    }

    public void MarkNeedsUserGestureToStart()
    {
        _stateMachine.Transition(PlaybackCommandKind.MarkNeedsUserGestureToStart);
        NeedsUserGestureToStart = true;
        IsPlaying = false;
        CurrentError = "Tap play to start this source.";
        NotifyChanged();
    }

    public void MarkPlaybackStarted()
    {
        NeedsUserGestureToStart = false;
        CurrentError = null;
        IsPlaying = true;
        _stateMachine.SetTransportState(true, false, null);
        NotifyChanged();
    }

    public void SetPopupOpen(bool isOpen)
    {
        if (IsPopupOpen == isOpen)
        {
            return;
        }

        IsPopupOpen = isOpen;
        NotifyChanged();
    }

    public void SetVideoExpanded(bool isExpanded)
    {
        if (IsVideoMode)
            SetPresentationSurface(isExpanded ? PlaybackPresentationSurface.PrimaryVideo : PlaybackPresentationSurface.PictureInPicture);
    }

    public void SetPresentationSurface(PlaybackPresentationSurface surface)
    {
        if (IsVideoMode && surface is PlaybackPresentationSurface.Docked or PlaybackPresentationSurface.NowPlaying) return;
        if (!IsVideoMode && surface is PlaybackPresentationSurface.PrimaryVideo or PlaybackPresentationSurface.PictureInPicture or PlaybackPresentationSurface.RestorableVideo or PlaybackPresentationSurface.Fullscreen) return;
        if (PresentationSurface == surface) return;
        PresentationSurface = surface;
        IsVideoExpanded = surface is PlaybackPresentationSurface.PrimaryVideo or PlaybackPresentationSurface.Fullscreen;
        NotifyChanged(PlaybackChangeKind.Ui);
    }

    public void ClosePlayer()
    {
        ReservePlaybackRequest();
        _queue.Clear();
        _history.Clear();
        _audiobookHistory.Clear();
        _audiobookBookmarks.Clear();
        _sleepTimerCts?.Cancel();
        _sleepTimerCts?.Dispose();
        _sleepTimerCts = null;
        CurrentIndex = -1;
        SourceLabel = null;
        IsPanelOpen = false;
        ActiveTab = ListenPlaybackTabs.Queue;
        IsDismissed = true;
        CurrentTimeSeconds = 0;
        DurationSeconds = 0;
        Volume = _clientSettings.DefaultVolume;
        IsMuted = false;
        PlaybackRate = 1d;
        ShuffleEnabled = false;
        RepeatMode = PlayerRepeatModes.Off;
        Experience = PlayerExperienceModes.Music;
        NeedsUserGestureToStart = false;
        IsPlaying = false;
        IsPopupOpen = false;
        IsVideoExpanded = false;
        PresentationSurface = PlaybackPresentationSurface.Docked;
        CurrentError = null;
        _stateMachine.SetIdle();
        PlaybackStartVersion++;
        SleepTimerMode = ListenSleepTimerModes.Off;
        SleepTimerEndsAtUtc = null;
        _currentAudiobookStartKind = null;
        NotifyChanged();
    }

    public void RestoreState(ListenPlaybackSnapshot snapshot)
    {
        _startCancellation?.Cancel();
        _queue.Clear();
        _queue.AddRange(snapshot.Queue ?? []);
        _history.Clear();
        _history.AddRange(snapshot.History ?? []);
        _audiobookHistory.Clear();
        _audiobookHistory.AddRange(snapshot.AudiobookHistory ?? []);
        _audiobookBookmarks.Clear();
        _audiobookBookmarks.AddRange(snapshot.AudiobookBookmarks ?? []);
        CurrentIndex = _queue.Count == 0
            ? -1
            : Math.Clamp(snapshot.CurrentIndex, 0, _queue.Count - 1);
        SourceLabel = snapshot.SourceLabel;
        IsPanelOpen = snapshot.IsPanelOpen;
        ActiveTab = snapshot.ActiveTab?.ToLowerInvariant() switch
        {
            ListenPlaybackTabs.History => ListenPlaybackTabs.History,
            ListenPlaybackTabs.Lyrics => ListenPlaybackTabs.Lyrics,
            _ => ListenPlaybackTabs.Queue,
        };
        IsDismissed = snapshot.IsDismissed || _queue.Count == 0;
        CurrentTimeSeconds = Math.Max(0, snapshot.CurrentTimeSeconds);
        DurationSeconds = Math.Max(0, snapshot.DurationSeconds);
        Volume = snapshot.Volume is > 0 and <= 1 ? snapshot.Volume : _clientSettings.DefaultVolume;
        IsMuted = snapshot.IsMuted;
        PlaybackRate = snapshot.PlaybackRate is >= 0.5d and <= 32d ? snapshot.PlaybackRate : 1d;
        ShuffleEnabled = snapshot.ShuffleEnabled;
        RepeatMode = NormalizeRepeatMode(snapshot.RepeatMode);
        Experience = MediaKindClassifier.ToPlayerExperienceString(
            !string.IsNullOrWhiteSpace(CurrentItem?.MediaType)
                ? MediaKindClassifier.Classify(CurrentItem.MediaType)
                : MediaKindClassifier.FromPlayerExperienceString(snapshot.Experience));
        _currentAudiobookStartKind = IsAudiobookMode
            ? NormalizeAudiobookStartKind(CurrentItem?.AudiobookStartKind)
            : null;
        NeedsUserGestureToStart = snapshot.NeedsUserGestureToStart;
        IsPlaying = snapshot.IsPlaying && _queue.Count > 0;
        IsPopupOpen = snapshot.IsPopupOpen;
        IsVideoExpanded = snapshot.IsVideoExpanded && IsVideoMode;
        PresentationSurface = IsVideoMode
            ? IsVideoExpanded ? PlaybackPresentationSurface.PrimaryVideo : PlaybackPresentationSurface.RestorableVideo
            : PlaybackPresentationSurface.Docked;
        if (IsVideoMode && !IsVideoExpanded) IsPlaying = false;
        CurrentError = snapshot.CurrentError;
        _stateMachine.SetTransportState(IsPlaying, NeedsUserGestureToStart, CurrentError);
        SkipBackSeconds = snapshot.SkipBackSeconds > 0 ? snapshot.SkipBackSeconds : 15;
        SkipForwardSeconds = snapshot.SkipForwardSeconds > 0 ? snapshot.SkipForwardSeconds : 15;
        SleepTimerOptionsMinutes = NormalizeSleepTimerOptions(snapshot.SleepTimerOptionsMinutes);
        AllowEndOfChapterSleepTimer = snapshot.AllowEndOfChapterSleepTimer;
        PlaybackStartVersion = Math.Max(PlaybackStartVersion, snapshot.PlaybackStartVersion);
        SleepTimerMode = snapshot.SleepTimerMode is ListenSleepTimerModes.Timer or ListenSleepTimerModes.EndOfChapter
            ? snapshot.SleepTimerMode
            : ListenSleepTimerModes.Off;
        SleepTimerEndsAtUtc = snapshot.SleepTimerEndsAtUtc > DateTimeOffset.UtcNow ? snapshot.SleepTimerEndsAtUtc : null;
        if (SleepTimerMode == ListenSleepTimerModes.Timer && !SleepTimerEndsAtUtc.HasValue)
        {
            SleepTimerMode = ListenSleepTimerModes.Off;
        }
        if (SleepTimerMode == ListenSleepTimerModes.Timer && SleepTimerEndsAtUtc.HasValue)
        {
            _ = SetSleepTimerAsync(SleepTimerEndsAtUtc.Value - DateTimeOffset.UtcNow);
        }
        NotifyChanged();
    }

    public ListenPlaybackSnapshot CreateSnapshot() => new()
    {
        Queue = _queue.ToList(),
        History = _history.ToList(),
        AudiobookHistory = _audiobookHistory.ToList(),
        AudiobookBookmarks = _audiobookBookmarks.ToList(),
        CurrentIndex = CurrentIndex,
        SourceLabel = SourceLabel,
        CurrentBrowserStreamUrl = CurrentBrowserStreamUrl,
        Experience = Experience,
        IsPanelOpen = IsPanelOpen,
        ActiveTab = ActiveTab,
        IsDismissed = IsDismissed,
        CurrentTimeSeconds = CurrentTimeSeconds,
        DurationSeconds = DurationSeconds,
        Volume = Volume,
        IsMuted = IsMuted,
        PlaybackRate = PlaybackRate,
        ShuffleEnabled = ShuffleEnabled,
        RepeatMode = RepeatMode,
        NeedsUserGestureToStart = NeedsUserGestureToStart,
        IsPlaying = IsPlaying,
        IsPopupOpen = IsPopupOpen,
        IsVideoExpanded = IsVideoExpanded,
        CurrentError = CurrentError,
        SkipBackSeconds = SkipBackSeconds,
        SkipForwardSeconds = SkipForwardSeconds,
        SleepTimerOptionsMinutes = SleepTimerOptionsMinutes.ToList(),
        AllowEndOfChapterSleepTimer = AllowEndOfChapterSleepTimer,
        PlaybackStartVersion = PlaybackStartVersion,
        SleepTimerMode = SleepTimerMode,
        SleepTimerEndsAtUtc = SleepTimerEndsAtUtc,
    };

    private void RememberCurrentItem()
    {
        if (CurrentItem is null)
        {
            return;
        }

        AddHistoryItem(CurrentItem with { PlayedAt = DateTimeOffset.UtcNow });
    }

    private void AddHistoryItem(ListenQueueItem item)
    {
        _history.Insert(0, item);
        if (_history.Count > 100)
        {
            _history.RemoveRange(100, _history.Count - 100);
        }
    }

    private async Task EnsurePlayableAsync(int index, CancellationToken ct)
    {
        if (index < 0 || index >= _queue.Count)
        {
            return;
        }

        var item = _queue[index];
        if (MediaKindClassifier.IsVideo(item.MediaType) && item.Manifest?.HlsStatus == "preparing" && item.AssetId is { } preparingAsset)
        {
            CurrentError = "Preparing video… Playback will begin automatically.";
            NotifyChanged();
            for (var attempt = 0; attempt < 120 && item.Manifest?.HlsStatus == "preparing"; attempt++)
            {
                await Task.Delay(1000, ct);
        ct.ThrowIfCancellationRequested();
                if (index >= _queue.Count || _queue[index].AssetId != preparingAsset) return;
                var refreshed = await _apiClient.GetPlaybackManifestAsync(preparingAsset, _clientContext.Client, null, ct, ToConnectionContext());
        ct.ThrowIfCancellationRequested();
                if (refreshed is null) continue;
                item = item with { Manifest = refreshed };
                _queue[index] = item;
            }
            CurrentError = null;
        }
        if (index >= _queue.Count || _queue[index].AssetId != item.AssetId) return;
        if (item.Manifest?.HlsStatus == "failed")
        {
            MarkCurrentFailed(item.Manifest.Warnings.FirstOrDefault() ?? "Compatible video preparation failed.");
            return;
        }
        if (MediaKindClassifier.IsVideo(item.MediaType) && item.Manifest is not null)
        {
            var manifestStream = SelectManifestStream(item.Manifest);
            if (string.IsNullOrWhiteSpace(manifestStream))
            {
                MarkCurrentFailed("The Engine has not finished preparing a compatible video stream.");
                return;
            }

            _queue[index] = item with { StreamUrl = NormalizeStreamUrl(manifestStream) };
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.StreamUrl))
        {
            var normalizedStreamUrl = NormalizeStreamUrl(item.StreamUrl);
            if (!string.Equals(item.StreamUrl, normalizedStreamUrl, StringComparison.Ordinal))
            {
                _queue[index] = item with { StreamUrl = normalizedStreamUrl };
                item = _queue[index];
            }

            if (MediaKindClassifier.IsMusic(item.MediaType)
                || (MediaKindClassifier.IsAudiobook(item.MediaType) && item.Chapters.Count > 0)
                || MediaKindClassifier.IsVideo(item.MediaType))
            {
                return;
            }

            if (_apiClient is null)
            {
                return;
            }
        }

        var assetId = item.AssetId;
        if (!assetId.HasValue)
        {
            assetId = await _orchestrator.ResolveWorkToAssetAsync(item.WorkId, ct);
        ct.ThrowIfCancellationRequested();
        }

        if (!assetId.HasValue)
        {
            MarkCurrentFailed("No playable file could be resolved for this item.");
            return;
        }

        var settings = _preferences is null ? null : await _preferences.GetAsync(ct);
        ct.ThrowIfCancellationRequested();
        var profileId = settings?.ProfileId == Guid.Empty ? null : settings?.ProfileId;
        var manifest = await _apiClient.GetPlaybackManifestAsync(
            assetId.Value,
            _clientContext.Client,
            profileId,
            ct,
            ToConnectionContext());
        ct.ThrowIfCancellationRequested();
        if (MediaKindClassifier.IsVideo(item.MediaType) && manifest is not null)
        {
            _queue[index] = item with { AssetId = assetId, Manifest = manifest };
            if (item.InitialPositionSeconds is null && !item.StartAtExactPosition && manifest.Resume?.PositionSeconds is { } resume)
                CurrentTimeSeconds = resume;
            DurationSeconds = manifest.DurationSeconds ?? 0;
            await EnsurePlayableAsync(index, ct);
            ct.ThrowIfCancellationRequested();
            return;
        }
        var streamUrl = manifest is null ? null : SelectManifestStream(manifest);
        streamUrl ??= manifest is null ? item.StreamUrl : null;
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            MarkCurrentFailed("The Engine did not return a playable stream for this item.");
            return;
        }

        _queue[index] = item with
        {
            AssetId = assetId,
            StreamUrl = NormalizeStreamUrl(streamUrl),
            Chapters = NormalizeChapters(manifest?.Chapters ?? []),
            Manifest = manifest,
            Quality = item.Quality ?? manifest?.Technical?.QualityLabel,
        };
        double? manifestDuration = manifest?.DurationSeconds ?? manifest?.Chapters
            .Where(chapter => chapter.EndSeconds.HasValue)
            .Select(chapter => chapter.EndSeconds!.Value)
            .DefaultIfEmpty()
            .Max();
        manifestDuration ??= PlaybackTimeParser.TryParseDurationSeconds(item.Duration);
        if (manifestDuration is > 0)
        {
            DurationSeconds = manifestDuration.Value;
        }
        CurrentError = null;
    }


    private PlaybackConnectionContextDto ToConnectionContext() => new()
    {
        ConnectionPath = _clientContext.ConnectionPath,
        RemoteConnectivityProvider = _clientContext.RemoteConnectivityProvider,
        EstimatedBandwidthMbps = _clientContext.EstimatedBandwidthMbps,
        LatencyMs = _clientContext.LatencyMs,
        RoomId = _clientContext.RoomId,
    };

    private static ListenQueueItem BootstrapDirectStream(ListenQueueItem item)
    {
        if (MediaKindClassifier.IsVideo(item.MediaType))
        {
            return item with { StreamUrl = item.Manifest is null ? null : SelectManifestStream(item.Manifest) };
        }

        if (!string.IsNullOrWhiteSpace(item.StreamUrl) || !item.AssetId.HasValue)
        {
            return item;
        }

        return item with { StreamUrl = $"/stream/{item.AssetId.Value:D}" };
    }

    private static string? SelectManifestStream(PlaybackManifestDto manifest) => manifest.RecommendedDelivery switch
    {
        PlaybackDeliveryModes.Hls => manifest.HlsUrl,
        PlaybackDeliveryModes.DirectStream => manifest.DirectStreamUrl,
        _ => manifest.DirectPlaySupported ? manifest.DirectStreamUrl : null,
    };

    private string? NormalizeStreamUrl(string? streamUrl)
    {
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            return null;
        }

        return _apiClient is null
            ? streamUrl
            : _apiClient.ToAbsoluteEngineUrl(streamUrl);
    }

    private static string? ToDashboardPlaybackUrl(string? streamUrl)
    {
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            return null;
        }

        var candidate = streamUrl.Trim();
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute))
        {
            candidate = absolute.PathAndQuery;
        }
        else if (candidate.StartsWith("stream/", StringComparison.OrdinalIgnoreCase))
        {
            candidate = "/" + candidate;
        }

        if (candidate.StartsWith("/api/v1/stream/", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate["/api/v1".Length..];
        }

        // Current catalogue manifests use the resource route; the legacy stream
        // route remains supported below. Both must use the authenticated proxy.
        var resourcePath = candidate.Split('?', '#')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (resourcePath is ["media", "assets", var id, "stream"] && Guid.TryParse(id, out var resourceAssetId))
        {
            return $"/engine-stream/{resourceAssetId:D}";
        }

        if (!candidate.StartsWith("/stream/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var remainder = candidate["/stream/".Length..];
        if (remainder.StartsWith("hls/", StringComparison.OrdinalIgnoreCase))
        {
            return "/engine-hls/" + remainder["hls/".Length..];
        }
        var pathEnd = remainder.IndexOfAny(['?', '#']);
        var path = (pathEnd >= 0 ? remainder[..pathEnd] : remainder).TrimEnd('/');
        return Guid.TryParse(path, out var assetId)
            ? $"/engine-stream/{assetId:D}"
            : null;
    }

    private async Task SyncReplaceQueueAsync(
        IReadOnlyList<ListenQueueItem> items,
        int startIndex,
        string? sourceLabel,
        bool shuffle,
        CancellationToken ct)
    {
        if (_apiClient is null || items.Count == 0)
        {
            return;
        }

        try
        {
            var profile = await _orchestrator.GetActiveProfileAsync(ct);
            var start = Math.Clamp(startIndex, 0, items.Count - 1);
            await _apiClient.ReplacePlayerQueueAsync(new PlayerQueueMutationDto
            {
                ProfileId = profile?.Id,
                DeviceId = _clientContext.DeviceId,
                Client = _clientContext.Client,
                Items = items.Select(ToPlayerQueueMutationItem).ToList(),
                WorkIds = items.Select(item => item.WorkId).Where(id => id != Guid.Empty).ToList(),
                StartIndex = start,
                StartWorkId = items[start].WorkId,
                SourceLabel = sourceLabel,
                Shuffle = shuffle,
                ClearExisting = true,
            }, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not sync listen player queue replacement.");
        }
    }

    private async Task SyncAddQueueItemsAsync(
        IReadOnlyList<ListenQueueItem> items,
        string mode,
        CancellationToken ct)
    {
        if (_apiClient is null || items.Count == 0)
        {
            return;
        }

        try
        {
            var profile = await _orchestrator.GetActiveProfileAsync(ct);
            await _apiClient.AddPlayerQueueItemsAsync(new PlayerQueueMutationDto
            {
                ProfileId = profile?.Id,
                DeviceId = _clientContext.DeviceId,
                Client = _clientContext.Client,
                Mode = mode,
                Items = items.Select(ToPlayerQueueMutationItem).ToList(),
                WorkIds = items.Select(item => item.WorkId).Where(id => id != Guid.Empty).ToList(),
                SourceLabel = SourceLabel,
            }, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not sync listen player queue addition.");
        }
    }

    private static PlayerQueueMutationItemDto ToPlayerQueueMutationItem(ListenQueueItem item)
        => new()
        {
            WorkId = item.WorkId,
            AssetId = item.AssetId,
            CollectionId = item.CollectionId,
            MediaType = item.MediaType,
            Title = item.Title,
            Subtitle = item.Subtitle,
            Album = item.Album,
            Artist = MediaKindClassifier.IsMusic(item.MediaType) ? item.Subtitle : null,
            Author = MediaKindClassifier.IsAudiobook(item.MediaType) ? null : item.Subtitle,
            CoverUrl = item.CoverUrl,
            DurationSeconds = ResolveQueueDurationSeconds(item),
            PositionSeconds = item.InitialPositionSeconds,
            StreamUrl = item.StreamUrl,
            Year = item.Year,
            ContentRating = item.ContentRating,
            SeasonNumber = item.SeasonNumber,
            EpisodeNumber = item.EpisodeNumber,
            EpisodeTitle = item.EpisodeTitle,
            Quality = item.Quality,
        };

    private static double? ResolveQueueDurationSeconds(ListenQueueItem item)
    {
        if (!MediaKindClassifier.IsAudiobook(item.MediaType) || item.Chapters.Count == 0)
        {
            return PlaybackTimeParser.TryParseDurationSeconds(item.Duration);
        }

        return item.Chapters
            .Where(chapter => chapter.EndSeconds.HasValue)
            .Select(chapter => chapter.EndSeconds!.Value)
            .DefaultIfEmpty(PlaybackTimeParser.TryParseDurationSeconds(item.Duration) ?? 0)
            .Max();
    }

    private static double InitialPositionFor(ListenQueueItem item) =>
        Math.Max(0, item.InitialPositionSeconds ?? 0);

    private async Task<double> InitialPositionForAsync(ListenQueueItem item, CancellationToken ct)
    {
        var position = InitialPositionFor(item);
        if (position <= 0 || item.StartAtExactPosition)
        {
            return position;
        }

        var settings = await PlaybackSettingsAsync(ct);
        if (!settings.General.ResumePlayback)
        {
            return 0;
        }

        if (!MediaKindClassifier.IsAudiobook(item.MediaType))
        {
            return position;
        }

        return Math.Max(0, position - settings.Listening.ResumeRewindSeconds);
    }

    private async Task<double> InitialPlaybackRateForAsync(ListenQueueItem item, CancellationToken ct)
    {
        if (MediaKindClassifier.IsMusic(item.MediaType))
        {
            return 1d;
        }

        var settings = await PlaybackSettingsAsync(ct);
        if (MediaKindClassifier.IsVideo(item.MediaType))
        {
            return Math.Clamp((double)settings.Watching.DefaultPlaybackSpeed, 0.5d, 3d);
        }

        return Math.Clamp((double)settings.Listening.AudiobookDefaultSpeed, 0.5d, 3d);
    }

    private async Task<UserPlaybackSettingsDto> PlaybackSettingsAsync(CancellationToken ct)
    {
        if (_preferences is null)
        {
            var defaults = UserPlaybackSettingsDto.CreateDefaults(Guid.Empty);
            ApplyExperienceSettings(defaults);
            return defaults;
        }

        var settings = await _preferences.GetAsync(ct) ?? UserPlaybackSettingsDto.CreateDefaults(Guid.Empty);
        ct.ThrowIfCancellationRequested();
        ApplyExperienceSettings(settings);
        return settings;
    }

    private void ApplyListeningSettings(ListeningSettingsDto settings)
    {
        SkipBackSeconds = settings.SkipBackSeconds;
        SkipForwardSeconds = settings.SkipForwardSeconds;
        ResumeRewindSeconds = settings.ResumeRewindSeconds;
        AudiobookNearStartGuardSeconds = settings.AudiobookNearStartGuardSeconds;
        SleepTimerOptionsMinutes = NormalizeSleepTimerOptions(settings.SleepTimerOptionsMinutes);
        AllowEndOfChapterSleepTimer = settings.AllowEndOfChapterSleepTimer;
    }

    private void ApplyExperienceSettings(UserPlaybackSettingsDto settings)
    {
        if (IsVideoMode)
        {
            SkipBackSeconds = settings.Watching.SkipBackSeconds;
            SkipForwardSeconds = settings.Watching.SkipForwardSeconds;
            return;
        }

        ApplyListeningSettings(settings.Listening);
    }

    private async Task<List<double>> SupportedPlaybackRatesAsync(CancellationToken ct)
    {
        if (!IsAudiobookMode)
        {
            return [1d];
        }

        var settings = await PlaybackSettingsAsync(ct);
        return Enumerable.Range(5, 26).Select(value => value / 10d)
            .Append((double)settings.Listening.AudiobookDefaultSpeed)
            .Distinct()
            .Order()
            .ToList();
    }

    private async Task RefreshAudiobookHistoryAsync(CancellationToken ct)
    {
        _audiobookHistory.Clear();
        _audiobookBookmarks.Clear();
        if (!IsAudiobookMode || CurrentItem is null || CurrentItem.WorkId == Guid.Empty || _apiClient is null)
        {
            return;
        }

        try
        {
            var settings = await PlaybackSettingsAsync(ct);
            var items = await _apiClient.GetAudiobookListenHistoryAsync(CurrentItem.WorkId, limit: settings.Listening.AudiobookHistoryLimit, ct: ct);
            _audiobookHistory.AddRange(CleanAudiobookHistory(items));
            var bookmarks = await _apiClient.GetAudiobookBookmarksAsync(CurrentItem.WorkId, ct: ct);
            _audiobookBookmarks.AddRange(bookmarks);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not load audiobook listen history or bookmarks.");
        }
    }

    private void ApplyPlayerState(PlayerStateDto? state)
    {
        if (state is null)
        {
            return;
        }

        if (state.SessionId != Guid.Empty && state.SessionId != _sessionId)
        {
            var hadSession = _sessionId != Guid.Empty;
            _sessionId = state.SessionId;
            if (hadSession)
            {
                Interlocked.Exchange(ref _telemetrySequence, 0);
            }
        }

        Experience = MediaKindClassifier.ToPlayerExperienceString(
            MediaKindClassifier.FromPlayerExperienceString(state.Experience));
        PlaybackRate = state.PlaybackRate is >= 0.5d and <= 32d ? state.PlaybackRate : PlaybackRate;
        ShuffleEnabled = state.ShuffleEnabled;
        RepeatMode = NormalizeRepeatMode(state.RepeatMode);
        _audiobookHistory.Clear();
        _audiobookHistory.AddRange(CleanAudiobookHistory(state.AudiobookHistory ?? []));
    }

    public PlaybackChapterDto? CurrentChapter =>
        CurrentItem is { Chapters.Count: > 0 } current
            ? ResolveCurrentChapter(current, CurrentTimeSeconds)
            : null;

    public static IReadOnlyList<PlaybackChapterDto> NormalizeChapters(IReadOnlyList<PlaybackChapterDto> chapters) =>
        chapters
            .OrderBy(chapter => chapters.Any(part => part.AssetId.HasValue) ? chapter.Index : chapter.StartSeconds)
            .Select((chapter, ordinal) => NormalizeChapter(chapter, ordinal))
            .ToList();

    public static PlaybackChapterDto NormalizeChapter(PlaybackChapterDto chapter, int ordinal) =>
        chapter with
        {
            Title = string.IsNullOrWhiteSpace(chapter.Title)
                ? $"Track {Math.Max(1, ordinal + 1)}"
                : chapter.Title.Trim(),
        };

    private static PlaybackChapterDto? ResolveCurrentChapter(ListenQueueItem item, double positionSeconds) =>
        item.Chapters.LastOrDefault(chapter =>
            (!chapter.AssetId.HasValue || chapter.AssetId == item.AssetId) && chapter.StartSeconds <= positionSeconds
            && (!chapter.EndSeconds.HasValue || positionSeconds < chapter.EndSeconds.Value))
        ?? item.Chapters.FirstOrDefault(chapter => !chapter.AssetId.HasValue || chapter.AssetId == item.AssetId);

    private static IReadOnlyList<AudiobookListenHistoryItemDto> CleanAudiobookHistory(IEnumerable<AudiobookListenHistoryItemDto> items) =>
        (items.Any(item => item.PositionSeconds > 0.5d)
            ? items.Where(item => item.PositionSeconds > 0.5d)
            : items)
            .OrderByDescending(item => item.EndedAt)
            .GroupBy(item => new
            {
                item.WorkId,
                item.AssetId,
                Chapter = item.ChapterIndex ?? -1,
                PositionBucket = (int)Math.Floor(item.PositionSeconds / 30d),
            })
            .Select(group => group.First())
            .ToList();

    private static IReadOnlyList<int> NormalizeSleepTimerOptions(IEnumerable<int>? options)
    {
        var defaults = new ListeningSettingsDto().SleepTimerOptionsMinutes;
        var values = (options ?? defaults)
            .Where(minutes => minutes is > 0 and <= 240)
            .Distinct()
            .Order()
            .ToList();
        return values.Count == 0 ? defaults : values;
    }

    private static string NormalizeAudiobookStartKind(string? value) =>
        value?.Trim() switch
        {
            AudiobookStartKinds.Chapter => AudiobookStartKinds.Chapter,
            AudiobookStartKinds.History => AudiobookStartKinds.History,
            AudiobookStartKinds.Bookmark => AudiobookStartKinds.Bookmark,
            _ => AudiobookStartKinds.Resume,
        };

    private static string NormalizeRepeatMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            PlayerRepeatModes.One => PlayerRepeatModes.One,
            PlayerRepeatModes.All => PlayerRepeatModes.All,
            _ => PlayerRepeatModes.Off,
        };

    private async Task SyncPlayerModeAsync(
        string command,
        bool? shuffleEnabled,
        string? repeatMode,
        CancellationToken ct)
    {
        try
        {
            var profile = await _orchestrator.GetActiveProfileAsync();
            var state = await _apiClient.SendPlayerCommandAsync(new PlayerCommandRequestDto
            {
                ProfileId = profile?.Id,
                DeviceId = _clientContext.DeviceId,
                Client = _clientContext.Client,
                Command = command,
                ShuffleEnabled = shuffleEnabled,
                RepeatMode = repeatMode,
            }, ct);
            ApplyPlayerState(state);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not sync player mode command {Command}.", command);
        }
    }

    private async Task CompleteSleepTimerAfterDelayAsync(TimeSpan duration, CancellationToken ct)
    {
        try
        {
            await Task.Delay(duration, ct);
            await ExpireSleepTimerAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ExpireSleepTimerAsync()
    {
        if (SleepTimerMode == ListenSleepTimerModes.Off)
        {
            return;
        }

        _sleepTimerCts?.Cancel();
        _sleepTimerCts?.Dispose();
        _sleepTimerCts = null;
        SleepTimerMode = ListenSleepTimerModes.Off;
        SleepTimerEndsAtUtc = null;
        IsPlaying = false;
        _stateMachine.SetTransportState(false, false, null);
        NotifyChanged();
        await RequestTransportCommandAsync(new("pause"));
        await ReportHeartbeatAsync(force: true);
    }

    private static string FormatSleepTimerRemaining(DateTimeOffset endsAtUtc)
    {
        var remaining = endsAtUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return "Off";
        }

        var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return $"{minutes} min";
    }

    private void MarkPlaybackStart() => PlaybackStartVersion =
        Math.Max(PlaybackStartVersion, Math.Max(
            Interlocked.Read(ref _nextTransportRequestId),
            Interlocked.Read(ref _lastDispatchedTransportRequestId))) + 1;

    private void NotifyChanged(PlaybackChangeKind kind = PlaybackChangeKind.State)
    {
        RefreshUpcomingQueue();
        Changed?.Invoke(kind);
    }

    private void RefreshUpcomingQueue()
    {
        _upcomingQueue = CurrentIndex < 0 || CurrentIndex >= _queue.Count
            ? _queue.ToList()
            : _queue.Skip(CurrentIndex + 1).ToList();
    }
}
