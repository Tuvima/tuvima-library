using System.Diagnostics;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Services;
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
    private double _lastAudibleVolume;
    private readonly PlaybackStateMachine _stateMachine = new();
    private readonly List<ListenQueueItem> _queue = [];
    private readonly List<ListenQueueItem> _history = [];
    private IReadOnlyList<ListenQueueItem> _upcomingQueue = [];
    private readonly List<AudiobookListenHistoryItemDto> _audiobookHistory = [];
    private readonly List<PlaybackTransportCommand> _pendingTransportCommands = [];
    private Guid _sessionId;
    private long _telemetrySequence;
    private long _nextTransportRequestId;
    private long _lastDispatchedTransportRequestId;
    private long _playbackRateSelectionVersion;
    private bool _savedPlaybackRateInvalid;
    private PlaybackClientContext _clientContext = PlaybackClientContext.WebDefault;
    private DateTimeOffset _lastHeartbeatAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTransportUiNotificationAt = DateTimeOffset.MinValue;
    private CancellationTokenSource? _sleepTimerCts;
    private readonly SemaphoreSlim _sleepTimerSelectionGate = new(1, 1);
    private int _sleepTimerRegistrationCount;
    private long _sleepTimerGeneration;
    private long _sleepTimerMonotonicGeneration;
    private long _sleepTimerMonotonicDeadlineTimestamp;
    private AudiobookSleepTimerStateDto? _nativeSleepTimerCandidateBeingBound;
    private Guid? _audiobookTimerSessionId;
    private Guid? _audiobookTimerSessionWorkId;
    private Guid? _audiobookTimerSessionProfileId;
    private bool _allowEndOfChapterSleepTimer;
    private bool _transportHostReady = true;
    private string? _currentAudiobookStartKind;
    private Guid? _audiobookBookSessionLeaseId;
    private Guid? _audiobookBookSessionWorkId;
    private long _audiobookBookSessionGeneration;
    private CancellationTokenSource? _startCancellation;
    public bool HasExplicitPlaybackRequest { get; private set; }
    public long PlaybackRequestVersion { get; private set; }
    public long PlaybackRateSelectionVersion => _playbackRateSelectionVersion;
    public bool SleepTimerRegistrationInProgress => Volatile.Read(ref _sleepTimerRegistrationCount) > 0;

    public bool CanBindNativeSleepTimerState(AudiobookSleepTimerStateDto state) =>
        _nativeSleepTimerCandidateBeingBound is { } candidate
            ? ReferenceEquals(candidate, state)
            : SleepTimerState == state;

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
        _lastAudibleVolume = PositiveVolumeOrFallback(Volume);
        ApplyListeningSettings(UserPlaybackSettingsDto.CreateDefaults(Guid.Empty).Listening);
        RefreshUpcomingQueue();
    }

    public event Action<PlaybackChangeKind>? Changed;
    public event Func<PlaybackTransportCommand, Task>? TransportCommandRequested;
    public event Func<AudiobookSleepTimerStateDto, Task<bool>>? SleepTimerNativeArmRequested;
    public event Func<AudiobookSleepTimerStateDto, Task<bool>>? SleepTimerPauseRequested;
    public event Func<AudiobookSleepTimerStateDto, string, Task<bool>>? SleepTimerNativeSourcePrepareRequested;
    public event Func<Guid, long, Task<double?>>? SleepTimerNativePositionRequested;

    public IReadOnlyList<ListenQueueItem> Queue => _queue;
    public IReadOnlyList<ListenQueueItem> History => _history;
    public IReadOnlyList<ListenQueueItem> MusicHistory => _history
        .Where(item => MediaKindClassifier.TryClassifyKnown(item.MediaType, out var experience)
            && experience == PlaybackExperience.Music)
        .ToArray();
    public IReadOnlyList<AudiobookListenHistoryItemDto> AudiobookHistory => _audiobookHistory;
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
    public double LastAudibleVolume => _lastAudibleVolume;

    public double GetMuteToggleTargetVolume() => IsMuted || Volume <= 0
        ? _lastAudibleVolume
        : 0d;
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
    public AudiobookSleepTimerStateDto SleepTimerState { get; private set; } = new();
    public AudiobookSleepTimerAvailabilityDto SleepTimerAvailability => GetSleepTimerAvailability();
    public string SleepTimerLabel => SleepTimerState.Mode switch
    {
        AudiobookSleepTimerModes.EndCurrent => "End of chapter",
        AudiobookSleepTimerModes.EndNext => $"End of {SleepTimerState.TargetChapterTitle}",
        AudiobookSleepTimerModes.Timer when SleepTimerState.DeadlineUtc.HasValue => FormatSleepTimerRemaining(SleepTimerState.DeadlineUtc.Value),
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
    public Guid? AudiobookBookSessionLeaseId => IsAudiobookMode ? _audiobookBookSessionLeaseId : null;
    public long AudiobookBookSessionGeneration => _audiobookBookSessionGeneration;
    public bool IsMusicMode => string.Equals(Experience, PlayerExperienceModes.Music, StringComparison.OrdinalIgnoreCase);
    public bool IsVideoMode => string.Equals(Experience, PlayerExperienceModes.Video, StringComparison.OrdinalIgnoreCase);
    public PlaybackClientContext ClientContext => _clientContext;
    public ListenPlaybackClientSettings ClientSettings => _clientSettings;
    public PlaybackPhase Phase => _stateMachine.Phase;

    public PlaybackSessionState State => new()
    {
        AudiobookBookSessionLeaseId = AudiobookBookSessionLeaseId,
        AudiobookBookSessionGeneration = AudiobookBookSessionGeneration,
        ViewSession = ViewSession,
        Queue = Queue,
        History = History,
        UpcomingQueue = UpcomingQueue,
        AudiobookHistory = AudiobookHistory,
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
        SleepTimerState = SleepTimerState,
        SleepTimerAvailability = SleepTimerAvailability,
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

        var requestedBookId = item.AudiobookWorkId ?? item.WorkId;
        var preserveBookSession = request.Intent is AudiobookStartIntent.Natural
            or AudiobookStartIntent.CapturedPreview;
        var continuesCurrentBook = preserveBookSession || startKind == AudiobookStartKinds.Chapter;
        PrepareSleepTimerForAudiobookStart(item, startKind, request.Intent);
        var preservedRate = continuesCurrentBook
            && IsAudiobookMode
            && CurrentItem is { } previous
            && (previous.AudiobookWorkId ?? previous.WorkId) == requestedBookId
                ? PlaybackRate
                : (double?)null;
        EstablishAudiobookBookSession(requestedBookId, preserveBookSession);

        return PlayQueueItemCoreAsync(item, request.SourceLabel ?? item.Album ?? item.Title, ct, preservedRate);
    }

    public Task PlayAudiobookChapterAsync(ListenQueueItem item, PlaybackChapterDto chapter, string? sourceLabel = null,
        CancellationToken ct = default, AudiobookStartIntent intent = AudiobookStartIntent.Explicit)
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
            new AudiobookStartRequest(chapterItem, AudiobookStartKinds.Chapter, normalized.StartSeconds, normalized.Index,
                sourceLabel ?? item.Album ?? item.Title, intent),
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

    private async Task PlayQueueItemCoreAsync(ListenQueueItem item, string? sourceLabel = null, CancellationToken ct = default,
        double? preservedRate = null)
    {
        using var request = BeginPlaybackRequest(ct);
        ct = request.Token;
        var rateSelectionVersion = _playbackRateSelectionVersion;
        try
        {
            ArgumentNullException.ThrowIfNull(item);

            item = BootstrapDirectStream(item);
            var requestVersion = PlaybackRequestVersion;
            if (MediaKindClassifier.IsAudiobook(item.MediaType))
            {
                var startKind = NormalizeAudiobookStartKind(item.AudiobookStartKind);
                item = item with { AudiobookStartKind = startKind };
                _currentAudiobookStartKind = startKind;
            }
            else
            {
                _currentAudiobookStartKind = null;
                EndAudiobookBookSession();
            }
            RememberCurrentItem();
            _queue.Clear();
            _queue.Add(item);
            CurrentIndex = 0;
            SourceLabel = sourceLabel ?? item.Album ?? item.Title;
            Experience = MediaKindClassifier.ToPlayerExperienceString(MediaKindClassifier.Classify(item.MediaType));
            PresentationSurface = IsVideoMode ? PlaybackPresentationSurface.PrimaryVideo : PlaybackPresentationSurface.Docked;
            IsVideoExpanded = IsVideoMode;
            PublishNewSubjectProjection(item, preservedRate);
            if (!IsCurrentRequest(request, requestVersion, item)) return;
            var startSettings = await PlaybackSettingsAsync(ct);
            if (!IsCurrentRequest(request, requestVersion, item)) return;
            ApplyExperienceSettings(startSettings);
            IsDismissed = false;
            var startPosition = await InitialPositionForAsync(item, ct);
            if (!IsCurrentRequest(request, requestVersion, item)) return;
            CurrentTimeSeconds = startPosition;
            DurationSeconds = 0;
            var startRate = preservedRate is { } preservedStartRate && PlaybackRatePolicy.IsValid(preservedStartRate)
                ? preservedStartRate
                : await InitialPlaybackRateForAsync(item, ct, rateSelectionVersion, new(request, requestVersion, item));
            if (!IsCurrentRequest(request, requestVersion, item)) return;
            if (_playbackRateSelectionVersion == rateSelectionVersion)
                PlaybackRate = preservedRate is { } exact && PlaybackRatePolicy.IsValid(exact) ? exact : startRate;
            IsPlaying = !_savedPlaybackRateInvalid;
            _stateMachine.SetLoading();
            NeedsUserGestureToStart = false;
            CurrentError = _savedPlaybackRateInvalid
                ? "The saved playback speed is unsupported. Choose a valid speed before playback can start."
                : null;
            NotifyChanged();
            var timerNeedsSourcePreparation = SleepTimerState.Mode != AudiobookSleepTimerModes.Off;
            var startRequested = !timerNeedsSourcePreparation && !_savedPlaybackRateInvalid
                && await TryStartCurrentAudioAsync(new(request, requestVersion, item));
            if (!IsCurrentRequest(request, requestVersion, item)) return;
            await EnsurePlayableAsync(CurrentIndex, ct, new(request, requestVersion, item));
            if (!IsCurrentRequest(request, requestVersion, item)) return;
            if (_savedPlaybackRateInvalid)
            {
                IsPlaying = false;
                _stateMachine.SetTransportState(false, false, null);
                NotifyChanged();
                return;
            }
            if (string.IsNullOrWhiteSpace(CurrentBrowserStreamUrl))
            {
                NotifyChanged();
                return;
            }

            if (!startRequested)
            {
                if (SleepTimerState.Mode != AudiobookSleepTimerModes.Off)
                {
                    var guard = new PlaybackRequestGuard(request, requestVersion, item);
                    if (!await PrepareCurrentSleepTimerSourceAsync(guard)
                        || !await BindCurrentSleepTimerBeforeStartAsync(guard)) return;
                }
                await TryStartCurrentAudioAsync(new(request, requestVersion, item));
                ct.ThrowIfCancellationRequested();
            }
            else
            {
                if (SleepTimerState.Mode != AudiobookSleepTimerModes.Off
                    && !await BindCurrentSleepTimerBeforeStartAsync(new(request, requestVersion, item))) return;
                NotifyChanged();
            }
            await RefreshAudiobookHistoryAsync(ct, new(request, requestVersion, item));
            if (!IsCurrentRequest(request, requestVersion, item)) return;
            await SyncReplaceQueueAsync([_queue[CurrentIndex]], 0, SourceLabel, false, ct, new(request, requestVersion, item));
            if (!IsCurrentRequest(request, requestVersion, item)) return;

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
        var rateSelectionVersion = _playbackRateSelectionVersion;
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
            var subject = _queue[CurrentIndex];
            var requestVersion = PlaybackRequestVersion;
            PublishNewSubjectProjection(subject);
            if (!IsCurrentRequest(request, requestVersion, subject)) return;
            var startSettings = await PlaybackSettingsAsync(ct);
            if (!IsCurrentRequest(request, requestVersion, subject)) return;
            ApplyExperienceSettings(startSettings);
            _currentAudiobookStartKind = MediaKindClassifier.IsAudiobook(_queue[CurrentIndex].MediaType)
                ? NormalizeAudiobookStartKind(_queue[CurrentIndex].AudiobookStartKind)
                : null;
            IsDismissed = false;
            var startPosition = await InitialPositionForAsync(_queue[CurrentIndex], ct);
            if (!IsCurrentRequest(request, requestVersion, subject)) return;
            CurrentTimeSeconds = startPosition;
            DurationSeconds = 0;
            var startRate = await InitialPlaybackRateForAsync(_queue[CurrentIndex], ct, rateSelectionVersion, new(request, requestVersion, subject));
            if (!IsCurrentRequest(request, requestVersion, subject)) return;
            if (_playbackRateSelectionVersion == rateSelectionVersion) PlaybackRate = startRate;
            IsPlaying = !_savedPlaybackRateInvalid;
            _stateMachine.SetLoading();
            NeedsUserGestureToStart = false;
            if (!_savedPlaybackRateInvalid) CurrentError = null;
            NotifyChanged();
            var startRequested = await TryStartCurrentAudioAsync(new(request, requestVersion, subject));
            if (!IsCurrentRequest(request, requestVersion, subject)) return;
            await EnsurePlayableAsync(CurrentIndex, ct, new(request, requestVersion, subject));
            if (!IsCurrentRequest(request, requestVersion, subject)) return;
            if (_savedPlaybackRateInvalid)
            {
                IsPlaying = false;
                _stateMachine.SetTransportState(false, false, null);
                NotifyChanged();
                return;
            }
            if (string.IsNullOrWhiteSpace(CurrentBrowserStreamUrl))
            {
                NotifyChanged();
                return;
            }

            if (!startRequested)
            {
                await TryStartCurrentAudioAsync(new(request, requestVersion, subject));
                ct.ThrowIfCancellationRequested();
            }
            else
            {
                NotifyChanged();
            }
            await RefreshAudiobookHistoryAsync(ct, new(request, requestVersion, subject));
            if (!IsCurrentRequest(request, requestVersion, subject)) return;
            await SyncReplaceQueueAsync(items, CurrentIndex, sourceLabel, shuffle, ct, new(request, requestVersion, subject));
            if (!IsCurrentRequest(request, requestVersion, subject)) return;

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
            PlaybackRate = transport.PlaybackRate is { } rate && PlaybackRatePolicy.IsValid(rate) ? rate : current.PlaybackRate,
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

    private double PositiveVolumeOrFallback(double value) =>
        double.IsFinite(value) && value > 0d
            ? Math.Clamp(value, 0d, 1d)
            : _clientSettings.DefaultVolume > 0d
                ? _clientSettings.DefaultVolume
                : 0.8d;

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

    internal bool TrySetCurrentVideoEpisodeTitle(Guid workId, long expectedRequestVersion, string? title)
    {
        if (string.IsNullOrWhiteSpace(title)
            || !IsVideoMode
            || PlaybackRequestVersion != expectedRequestVersion
            || CurrentItem is not { } current
            || current.WorkId != workId)
            return false;

        var updated = current with { EpisodeTitle = title.Trim(), Title = title.Trim() };
        var index = _queue.FindIndex(item => item.WorkId == workId);
        if (index < 0) return false;
        _queue[index] = updated;
        NotifyChanged();
        return true;
    }

    public async Task PlayIndexAsync(int index, CancellationToken ct = default)
    {
        using var request = BeginPlaybackRequest(ct);
        ct = request.Token;
        var rateSelectionVersion = _playbackRateSelectionVersion;
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
            var item = _queue[CurrentIndex];
            var requestVersion = PlaybackRequestVersion;
            PublishNewSubjectProjection(item);
            if (!IsCurrentRequest(request, requestVersion, item)) return;
            var startSettings = await PlaybackSettingsAsync(ct);
            if (!IsCurrentRequest(request, requestVersion, item)) return;
            ApplyExperienceSettings(startSettings);
            var startPosition = await InitialPositionForAsync(item, ct);
            if (!IsCurrentRequest(request, requestVersion, item)) return;
            CurrentTimeSeconds = startPosition;
            DurationSeconds = 0;
            var startRate = await InitialPlaybackRateForAsync(item, ct, rateSelectionVersion, new(request, requestVersion, item));
            if (!IsCurrentRequest(request, requestVersion, item)) return;
            if (_playbackRateSelectionVersion == rateSelectionVersion) PlaybackRate = startRate;
            IsDismissed = false;
            IsPlaying = !_savedPlaybackRateInvalid;
            _stateMachine.SetLoading();
            NeedsUserGestureToStart = false;
            if (!_savedPlaybackRateInvalid) CurrentError = null;
            await EnsurePlayableAsync(CurrentIndex, ct, new(request, requestVersion, item));
            if (!IsCurrentRequest(request, requestVersion, item)) return;
            if (_savedPlaybackRateInvalid)
            {
                IsPlaying = false;
                _stateMachine.SetTransportState(false, false, null);
                NotifyChanged();
                return;
            }
            await RefreshAudiobookHistoryAsync(ct, new(request, requestVersion, item));
            if (!IsCurrentRequest(request, requestVersion, item)) return;
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
        var rateSelectionVersion = _playbackRateSelectionVersion;
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
            var item = _queue[CurrentIndex];
            var requestVersion = PlaybackRequestVersion;
            PublishNewSubjectProjection(item);
            if (!IsCurrentRequest(request, requestVersion, item)) return;
            var startSettings = await PlaybackSettingsAsync(ct);
            if (!IsCurrentRequest(request, requestVersion, item)) return;
            ApplyExperienceSettings(startSettings);
            var startPosition = await InitialPositionForAsync(item, ct);
            if (!IsCurrentRequest(request, requestVersion, item)) return;
            CurrentTimeSeconds = startPosition;
            DurationSeconds = 0;
            var startRate = await InitialPlaybackRateForAsync(item, ct, rateSelectionVersion, new(request, requestVersion, item));
            if (!IsCurrentRequest(request, requestVersion, item)) return;
            if (_playbackRateSelectionVersion == rateSelectionVersion) PlaybackRate = startRate;
            IsPlaying = !_savedPlaybackRateInvalid;
            _stateMachine.SetLoading();
            NeedsUserGestureToStart = false;
            if (!_savedPlaybackRateInvalid) CurrentError = null;
            await EnsurePlayableAsync(CurrentIndex, ct, new(request, requestVersion, item));
            if (!IsCurrentRequest(request, requestVersion, item)) return;
            if (_savedPlaybackRateInvalid)
            {
                IsPlaying = false;
                _stateMachine.SetTransportState(false, false, null);
                NotifyChanged();
                return;
            }
            await RefreshAudiobookHistoryAsync(ct, new(request, requestVersion, item));
            if (!IsCurrentRequest(request, requestVersion, item)) return;
            MarkPlaybackStart();
            NotifyChanged();

        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_startCancellation, request)) _startCancellation = null;
        }
    }

    public Task CompleteCurrentAsync(CancellationToken ct = default) =>
        CompleteCurrentCoreAsync(null, null, ct);

    public async Task HandleNativeAudioEndedAsync(
        Guid assetId,
        long requestVersion,
        long timerGeneration,
        double actualCurrentTimeSeconds,
        CancellationToken ct = default)
    {
        if (IsVideoMode || !double.IsFinite(actualCurrentTimeSeconds) || actualCurrentTimeSeconds < 0
            || !IsCurrentNativeEndedSubject(assetId, requestVersion)
            || (SleepTimerState.Mode != AudiobookSleepTimerModes.Off
                && SleepTimerState.TimerGeneration != timerGeneration))
            return;

        if (!ProjectNativeEndOfFilePosition(assetId, requestVersion, actualCurrentTimeSeconds))
            return;

        var timer = SleepTimerState;
        if (timer.Mode is AudiobookSleepTimerModes.EndCurrent or AudiobookSleepTimerModes.EndNext
            && timer.TargetAssetId == assetId)
        {
            await ExpireSleepTimerAsync(timer.TimerGeneration, assetId, requestVersion, nativeAlreadyPaused: true);
            return;
        }

        await CompleteCurrentCoreAsync(assetId, requestVersion, ct, actualCurrentTimeSeconds);
    }

    private bool ProjectNativeEndOfFilePosition(Guid assetId, long requestVersion, double actualCurrentTimeSeconds)
    {
        if (!double.IsFinite(actualCurrentTimeSeconds) || actualCurrentTimeSeconds < 0
            || !IsCurrentNativeEndedSubject(assetId, requestVersion))
            return false;

        CurrentTimeSeconds = actualCurrentTimeSeconds;
        IsPlaying = false;
        NeedsUserGestureToStart = false;
        _stateMachine.SetTransportState(false, false, CurrentError);
        NotifyChanged(PlaybackChangeKind.TransportState);
        return true;
    }

    private async Task CompleteCurrentCoreAsync(Guid? expectedAssetId, long? expectedRequestVersion, CancellationToken ct,
        double? nativeEndPositionSeconds = null)
    {
        bool IsExpectedSubjectCurrent() => expectedAssetId is null
            || IsCurrentNativeEndedSubject(expectedAssetId.Value, expectedRequestVersion!.Value);

        if (!IsExpectedSubjectCurrent()) return;
        var atCapturedBoundary = IsAtCapturedSleepBoundary();
        if (SleepTimerState.Mode is AudiobookSleepTimerModes.EndCurrent or AudiobookSleepTimerModes.EndNext
            && atCapturedBoundary)
        {
            await ExpireSleepTimerAsync(SleepTimerState.TimerGeneration, SleepTimerState.BoundAssetId, PlaybackRequestVersion, nativeAlreadyPaused: false);
            return;
        }
        await ReportHeartbeatAsync(force: true, ct, hasPlaybackEnded: true);
        if (!IsExpectedSubjectCurrent()) return;
        if (IsAudiobookMode && CurrentItem is { } recording && recording.Chapters.Any(chapter => chapter.AssetId.HasValue))
        {
            var lastPartChapter = recording.Chapters.Where(chapter => chapter.AssetId == recording.AssetId)
                .Select(chapter => chapter.Index).DefaultIfEmpty(-1).Max();
            var nextPart = recording.Chapters.FirstOrDefault(chapter => chapter.Index > lastPartChapter && chapter.AssetId != recording.AssetId);
            if (nextPart is not null)
            {
                if (!IsExpectedSubjectCurrent()) return;
                await PlayAudiobookChapterAsync(recording, nextPart, SourceLabel, ct, AudiobookStartIntent.Natural);
                return;
            }
        }
        if (!IsExpectedSubjectCurrent()) return;
        var nextIndex = ResolveNextIndex(automaticAdvance: true);
        if (CurrentItem is not null
            && (!nextIndex.HasValue || nextIndex.Value == CurrentIndex))
        {
            AddHistoryItem(CurrentItem with { PlayedAt = DateTimeOffset.UtcNow });
        }

        if (!nextIndex.HasValue)
        {
            IsPlaying = false;
            if (nativeEndPositionSeconds is null)
                CurrentTimeSeconds = DurationSeconds;
            _stateMachine.SetEnded();
            NotifyChanged();
            return;
        }

        if (!IsExpectedSubjectCurrent()) return;
        await PlayIndexAsync(nextIndex.Value, ct);
    }

    private bool IsCurrentNativeEndedSubject(Guid assetId, long requestVersion) =>
        assetId != Guid.Empty
        && CurrentItem?.AssetId == assetId
        && PlaybackRequestVersion == requestVersion;

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
        bool? needsUserGestureToStart = null,
        long? expectedPlaybackRateSelectionVersion = null)
    {
        var acceptedPlaying = _savedPlaybackRateInvalid ? false : isPlaying;
        _stateMachine.SetTransportState(acceptedPlaying, needsUserGestureToStart,
            _savedPlaybackRateInvalid ? null : CurrentError);
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
            var nextPlaying = isPlaying.Value && !_savedPlaybackRateInvalid;
            if (IsPlaying != nextPlaying)
            {
                IsPlaying = nextPlaying;
                structuralChanged = true;
            }
        }

        if (volume.HasValue)
        {
            var next = Math.Clamp(volume.Value, 0d, 1d);
            if (double.IsFinite(next) && next > 0d)
            {
                _lastAudibleVolume = next;
            }
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
            var next = playbackRate.Value;
            if (!_savedPlaybackRateInvalid
                && expectedPlaybackRateSelectionVersion == _playbackRateSelectionVersion
                && PlaybackRatePolicy.IsValid(next) && PlaybackRate != next)
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

        if (SleepTimerState.Mode == AudiobookSleepTimerModes.Timer
            && IsSleepTimerDeadlineElapsed(SleepTimerState))
        {
            _ = ExpireSleepTimerAsync(SleepTimerState.TimerGeneration, SleepTimerState.BoundAssetId, PlaybackRequestVersion, nativeAlreadyPaused: false);
        }
        else if (SleepTimerState.Mode is AudiobookSleepTimerModes.EndCurrent or AudiobookSleepTimerModes.EndNext
            && CurrentItem?.AssetId == SleepTimerState.TargetAssetId
            && CurrentTimeSeconds >= SleepTimerState.TargetEndSeconds)
        {
            _ = ExpireSleepTimerAsync(SleepTimerState.TimerGeneration, SleepTimerState.BoundAssetId, PlaybackRequestVersion, nativeAlreadyPaused: false);
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

    private async Task<bool> TryStartCurrentAudioAsync(PlaybackRequestGuard? guard = null)
    {
        if (_savedPlaybackRateInvalid
            || (guard is not null && !IsCurrentRequest(guard))
            || string.IsNullOrWhiteSpace(CurrentBrowserStreamUrl))
        {
            return false;
        }

        MarkPlaybackStart();
        await RequestTransportCommandAsync(CreateStartCommand());
        if (guard is not null && !IsCurrentRequest(guard)) return false;
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
        var currentIndex = rates.FindIndex(rate => rate == PlaybackRate);
        var next = rates[(currentIndex + 1 + rates.Count) % rates.Count];
        await SetPlaybackRateAsync(next, ct);
    }

    public async Task SetPlaybackRateAsync(double rate, CancellationToken ct = default)
    {
        if (!PlaybackRatePolicy.IsValid(rate))
        {
            CurrentError = $"Playback speed must be between {PlaybackRatePolicy.Minimum:0.0}x and {PlaybackRatePolicy.Maximum:0.0}x.";
            NotifyChanged();
            return;
        }

        var next = rate;
        _savedPlaybackRateInvalid = false;
        ClearPlaybackRateError();
        _playbackRateSelectionVersion++;
        PlaybackRate = next;
        NotifyChanged();
        await RequestTransportCommandAsync(new("set-speed", next));
        await ReportHeartbeatAsync(force: true, ct);
    }

    public bool TrySetPlaybackRateFromHost(double rate, Guid expectedAssetId, long expectedPlaybackRequestVersion)
    {
        if (!PlaybackRatePolicy.IsValid(rate)
            || expectedAssetId == Guid.Empty
            || PlaybackRequestVersion != expectedPlaybackRequestVersion
            || CurrentItem?.AssetId != expectedAssetId)
            return false;

        _savedPlaybackRateInvalid = false;
        ClearPlaybackRateError();
        _playbackRateSelectionVersion++;
        PlaybackRate = rate;
        NotifyChanged(PlaybackChangeKind.TransportState);
        return true;
    }

    public async Task PlayAudiobookHistoryAsync(AudiobookListenHistoryItemDto history, CancellationToken ct = default)
    {
        var current = CurrentItem;
        if (current is null
            || (current.AudiobookWorkId ?? current.WorkId) != history.WorkId
            || _preferences is not null && _preferences.ActiveProfileId != history.ProfileId
            || history.AssetId == Guid.Empty || !IsKnownAudiobookAsset(history.WorkId, history.AssetId)
            || !double.IsFinite(history.PositionSeconds) || history.PositionSeconds < 0)
            return;
        var sameAsset = current.AssetId == history.AssetId;
        var chapter = ResolveAudiobookChapter(history.AssetId, history.ChapterIndex, history.PositionSeconds);
        var item = current with
        {
            WorkId = history.WorkId,
            AssetId = history.AssetId,
            MediaType = "Audiobooks",
            Title = current.Title,
            Subtitle = chapter?.Title,
            InitialPositionSeconds = history.PositionSeconds,
            ChapterIndex = chapter?.Index,
            StartAtExactPosition = true,
            Duration = history.DurationSeconds.HasValue ? PlaybackTimeParser.FormatDuration(history.DurationSeconds.Value) : current.Duration,
            StreamUrl = sameAsset ? current.StreamUrl : null,
            Manifest = sameAsset ? current.Manifest : null,
            Chapters = current.Chapters,
        };

        await StartAudiobookAsync(new AudiobookStartRequest(item, AudiobookStartKinds.History, history.PositionSeconds, chapter?.Index, item.Title), ct);
    }

    public async Task PlayAudiobookBookmarkAsync(AudiobookBookmarkDto bookmark, CancellationToken ct = default)
    {
        var current = CurrentItem;
        if (current is null
            || (current.AudiobookWorkId ?? current.WorkId) != bookmark.WorkId
            || _preferences is not null && _preferences.ActiveProfileId != bookmark.ProfileId
            || bookmark.AssetId == Guid.Empty || !IsKnownAudiobookAsset(bookmark.WorkId, bookmark.AssetId)
            || !double.IsFinite(bookmark.PositionSeconds) || bookmark.PositionSeconds < 0)
            return;
        var sameAsset = current.AssetId == bookmark.AssetId;
        var chapter = ResolveAudiobookChapter(bookmark.AssetId, bookmark.ChapterIndex, bookmark.PositionSeconds);
        var item = current with
        {
            WorkId = bookmark.WorkId,
            AssetId = bookmark.AssetId,
            MediaType = "Audiobooks",
            Subtitle = chapter?.Title,
            InitialPositionSeconds = bookmark.PositionSeconds,
            ChapterIndex = chapter?.Index,
            StartAtExactPosition = true,
            Duration = bookmark.DurationSeconds.HasValue ? PlaybackTimeParser.FormatDuration(bookmark.DurationSeconds.Value) : current.Duration,
            StreamUrl = sameAsset ? current.StreamUrl : null,
            Manifest = sameAsset ? current.Manifest : null,
            Chapters = current.Chapters,
        };

        await StartAudiobookAsync(new AudiobookStartRequest(item, AudiobookStartKinds.Bookmark,
            bookmark.PositionSeconds, chapter?.Index, item.Title, AudiobookStartIntent.BookmarkReplay), ct);
    }

    private bool IsKnownAudiobookAsset(Guid workId, Guid assetId) => _queue.Any(item =>
        (item.AudiobookWorkId ?? item.WorkId) == workId && MediaKindClassifier.IsAudiobook(item.MediaType)
        && (item.AssetId == assetId || item.Chapters.Any(chapter => chapter.AssetId == assetId)));

    private PlaybackChapterDto? ResolveAudiobookChapter(Guid assetId, int? chapterIndex, double positionSeconds)
    {
        var chapters = _queue.SelectMany(item => item.Chapters)
            .Where(chapter => chapter.AssetId == assetId
                && double.IsFinite(chapter.StartSeconds) && chapter.StartSeconds >= 0
                && chapter.EndSeconds is double end && double.IsFinite(end) && end > chapter.StartSeconds
                && positionSeconds >= chapter.StartSeconds && positionSeconds < end)
            .DistinctBy(chapter => (chapter.AssetId, chapter.Index))
            .ToArray();
        if (chapterIndex is int index && index >= 0)
        {
            var indexed = chapters.Where(chapter => chapter.Index == index).ToArray();
            return indexed.Length == 1 ? indexed[0] : null;
        }

        return chapters.Length == 1 ? chapters[0] : null;
    }

    public async Task<bool> PreviewCapturedAudiobookDraftAsync(
        AudiobookBookmarkActionContext context,
        CapturedAudiobookBookmarkDraft draft,
        CancellationToken ct = default)
    {
        var current = CurrentItem;
        if (draft.ProfileId != context.ProfileId || draft.WorkId != context.WorkId
            || draft.SessionLeaseId != context.SessionLeaseId || draft.AssetId == Guid.Empty
            || _preferences?.ActiveProfileId != context.ProfileId
            || !IsAudiobookMode || (current?.AudiobookWorkId ?? current?.WorkId) != context.WorkId
            || AudiobookBookSessionLeaseId != context.SessionLeaseId
            || AudiobookBookSessionGeneration != context.OwnerGeneration)
            return false;

        var source = current!;
        var sameAsset = source.AssetId == draft.AssetId;
        var item = source with
        {
            AssetId = draft.AssetId,
            StreamUrl = sameAsset ? source.StreamUrl : null,
            Manifest = sameAsset ? source.Manifest : null,
            Chapters = source.Chapters,
            Subtitle = draft.ChapterTitle ?? source.Subtitle,
            InitialPositionSeconds = draft.PositionSeconds,
            ChapterIndex = draft.ChapterIndex,
            StartAtExactPosition = true,
        };
        var sourceRequestVersion = PlaybackRequestVersion;
        await StartAudiobookAsync(new AudiobookStartRequest(item, AudiobookStartKinds.Bookmark,
            draft.PositionSeconds, draft.ChapterIndex, source.Title, AudiobookStartIntent.CapturedPreview), ct);

        return sourceRequestVersion != PlaybackRequestVersion
            && _preferences?.ActiveProfileId == context.ProfileId
            && AudiobookBookSessionLeaseId == context.SessionLeaseId
            && AudiobookBookSessionGeneration == context.OwnerGeneration
            && IsAudiobookMode
            && (CurrentItem?.AudiobookWorkId ?? CurrentItem?.WorkId) == context.WorkId
            && CurrentItem?.AssetId == draft.AssetId
            && CurrentItem?.InitialPositionSeconds == draft.PositionSeconds
            && !string.IsNullOrWhiteSpace(CurrentBrowserStreamUrl);
    }

    public async Task<AudiobookSleepTimerStateDto> SetAudiobookSleepTimerAsync(
        Guid profileId,
        Guid workId,
        Guid expectedAssetId,
        long expectedPlaybackRequestVersion,
        AudiobookSleepTimerSelectionDto selection,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _sleepTimerRegistrationCount);
        try
        {
            await _sleepTimerSelectionGate.WaitAsync(ct);
            try
            {
                return await SetAudiobookSleepTimerCoreAsync(profileId, workId, expectedAssetId,
                    expectedPlaybackRequestVersion, selection, ct);
            }
            finally
            {
                _sleepTimerSelectionGate.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _sleepTimerRegistrationCount);
        }
    }

    /// <summary>
    /// Invalidates an active audiobook timer as soon as its profile or playback authority changes.
    /// The native clear is scoped to the binding that was active before invalidation.
    /// </summary>
    public async Task InvalidateAudiobookSleepTimerAsync()
    {
        var active = SleepTimerState;
        var hasPendingRegistration = SleepTimerRegistrationInProgress;
        if (active.Mode == AudiobookSleepTimerModes.Off && !hasPendingRegistration) return;


        CancelSleepTimerWait();
        // Advance past any selection that may currently be waiting for native acknowledgement.
        // That pending selection will fail its post-await generation check and cannot resurrect
        // its arm after this context invalidation.
        var generation = _sleepTimerGeneration + 2;
        _sleepTimerGeneration = generation;
        SleepTimerState = new AudiobookSleepTimerStateDto { TimerGeneration = generation };
        _sleepTimerMonotonicGeneration = 0;
        _sleepTimerMonotonicDeadlineTimestamp = 0;
        _audiobookTimerSessionId = null;
        _audiobookTimerSessionWorkId = null;
        _audiobookTimerSessionProfileId = null;
        NotifyChanged();

        if (active.BoundAssetId != Guid.Empty || hasPendingRegistration)
        {
            var nativeCleared = await BindNativeSleepTimerAsync(new AudiobookSleepTimerStateDto
            {
                TimerGeneration = generation,
                ProfileId = active.ProfileId,
                WorkId = active.WorkId,
                TimerSessionId = active.TimerSessionId,
                BoundAssetId = active.BoundAssetId,
                PlaybackRequestVersion = active.PlaybackRequestVersion,
            });
        }
    }

    private async Task<AudiobookSleepTimerStateDto> SetAudiobookSleepTimerCoreAsync(
        Guid profileId,
        Guid workId,
        Guid expectedAssetId,
        long expectedPlaybackRequestVersion,
        AudiobookSleepTimerSelectionDto selection,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (profileId == Guid.Empty || _preferences?.ActiveProfileId != profileId
            || expectedPlaybackRequestVersion != PlaybackRequestVersion
            || CurrentItem is not { } current || !IsAudiobookMode
            || (current.AudiobookWorkId ?? current.WorkId) != workId
            || current.AssetId != expectedAssetId || expectedAssetId == Guid.Empty)
        {
            throw new InvalidOperationException("The audiobook playback session changed before the sleep timer could be set.");
        }

        var timerSessionId = _audiobookTimerSessionId is { } existingSession
            && _audiobookTimerSessionProfileId == profileId
            && _audiobookTimerSessionWorkId == workId
                ? existingSession
                : Guid.NewGuid();

        AudiobookSleepBoundaryTarget? capturedTarget = null;
        if (selection.Mode == AudiobookSleepTimerModes.Timer
            && (selection.Minutes is not int requestedMinutes || !SleepTimerOptionsMinutes.Contains(requestedMinutes)))
            throw new ArgumentOutOfRangeException(nameof(selection), "Choose one of the available sleep timer durations.");
        if (selection.Mode is AudiobookSleepTimerModes.EndCurrent or AudiobookSleepTimerModes.EndNext)
        {
            if (!_allowEndOfChapterSleepTimer)
                throw new InvalidOperationException("Chapter boundary timers are not enabled for this profile.");
            var position = await ReadSleepTimerNativePositionAsync(expectedAssetId, expectedPlaybackRequestVersion);
            if (position is not double nativePosition || !double.IsFinite(nativePosition) || nativePosition < 0
                || PlaybackRequestVersion != expectedPlaybackRequestVersion
                || _preferences?.ActiveProfileId != profileId
                || CurrentItem?.AssetId != expectedAssetId
                || CurrentItem is not { } currentAfterRead
                || (currentAfterRead.AudiobookWorkId ?? currentAfterRead.WorkId) != workId)
                throw new InvalidOperationException("The current audiobook position could not be verified for this timer.");
            var kind = selection.Mode == AudiobookSleepTimerModes.EndCurrent
                ? AudiobookSleepBoundaryKind.EndOfChapter
                : AudiobookSleepBoundaryKind.EndOfNextChapter;
            if (!AudiobookSleepBoundaryResolver.TryCapture(workId, timerSessionId,
                    expectedAssetId, nativePosition, GetTimerChapters(currentAfterRead), CurrentAuthorizedAudiobookAssetIds(currentAfterRead), kind, out capturedTarget)
                || capturedTarget is null)
                throw new InvalidOperationException(selection.Mode == AudiobookSleepTimerModes.EndCurrent
                    ? SleepTimerAvailability.CurrentUnavailableReason ?? "A timed current chapter is not available."
                    : SleepTimerAvailability.NextUnavailableReason ?? "A timed next chapter is not available.");
        }
        else if (selection.Mode is not AudiobookSleepTimerModes.Off and not AudiobookSleepTimerModes.Timer)
        {
            throw new ArgumentOutOfRangeException(nameof(selection), "Choose a supported sleep timer option.");
        }

        var previousState = SleepTimerState;
        var previousGeneration = _sleepTimerGeneration;
        var generation = previousGeneration + 1;
        AudiobookSleepTimerStateDto candidate;
        if (selection.Mode == AudiobookSleepTimerModes.Off)
        {
            candidate = new AudiobookSleepTimerStateDto { TimerGeneration = generation };
        }
        else if (selection.Mode == AudiobookSleepTimerModes.Timer)
        {
            var minutes = selection.Minutes!.Value;
            var now = DateTimeOffset.UtcNow;
            candidate = new AudiobookSleepTimerStateDto
            {
                Mode = AudiobookSleepTimerModes.Timer,
                ChosenMinutes = minutes,
                DeadlineUtc = now.AddMinutes(minutes),
                TimerGeneration = generation,
                ProfileId = profileId,
                WorkId = workId,
                TimerSessionId = timerSessionId,
                OriginAssetId = expectedAssetId,
                TargetAssetId = expectedAssetId,
                BoundAssetId = expectedAssetId,
                PlaybackRequestVersion = expectedPlaybackRequestVersion,
            };
        }
        else if (selection.Mode is AudiobookSleepTimerModes.EndCurrent or AudiobookSleepTimerModes.EndNext)
        {
            var target = capturedTarget!;

            candidate = new AudiobookSleepTimerStateDto
            {
                Mode = selection.Mode,
                TimerGeneration = generation,
                ProfileId = profileId,
                WorkId = workId,
                TimerSessionId = target.SessionLeaseId,
                OriginAssetId = target.OriginAssetId,
                OriginChapterIndex = target.OriginChapterIndex,
                TargetAssetId = target.TargetAssetId,
                TargetChapterIndex = target.TargetChapterIndex,
                TargetChapterTitle = target.TargetChapterTitle,
                TargetEndSeconds = target.TargetEndSeconds,
                BoundAssetId = expectedAssetId,
                PlaybackRequestVersion = expectedPlaybackRequestVersion,
            };
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(selection), "Choose a supported sleep timer option.");
        }
        var candidateMonotonicDeadline = candidate.Mode == AudiobookSleepTimerModes.Timer
            ? MonotonicDeadlineFromUtc(candidate.DeadlineUtc!.Value)
            : 0;
        var nativeCandidate = candidate.Mode == AudiobookSleepTimerModes.Off
            ? candidate with
            {
                ProfileId = profileId,
                WorkId = workId,
                TimerSessionId = timerSessionId,
                BoundAssetId = expectedAssetId,
                PlaybackRequestVersion = expectedPlaybackRequestVersion,
            }
            : candidate;

        if (!await BindNativeSleepTimerAsync(nativeCandidate))
        {
            throw new InvalidOperationException("The audiobook sleep timer could not be registered with the active player.");
        }

        if (_sleepTimerGeneration != previousGeneration || SleepTimerState != previousState
            || PlaybackRequestVersion != expectedPlaybackRequestVersion
            || _preferences?.ActiveProfileId != profileId
            || CurrentItem?.AssetId != expectedAssetId
            || CurrentItem is not { } live || (live.AudiobookWorkId ?? live.WorkId) != workId)
        {
            await BindNativeSleepTimerAsync(new AudiobookSleepTimerStateDto
            {
                TimerGeneration = generation,
                ProfileId = profileId,
                WorkId = workId,
                TimerSessionId = timerSessionId,
                BoundAssetId = expectedAssetId,
                PlaybackRequestVersion = expectedPlaybackRequestVersion,
            });
            throw new InvalidOperationException("Playback changed before the sleep timer could be confirmed.");
        }

        CancelSleepTimerWait();
        _sleepTimerGeneration = generation;
        SleepTimerState = candidate;
        if (candidate.Mode != AudiobookSleepTimerModes.Off)
        {
            _audiobookTimerSessionId = candidate.TimerSessionId;
            _audiobookTimerSessionWorkId = workId;
            _audiobookTimerSessionProfileId = profileId;
        }
        _sleepTimerMonotonicGeneration = candidate.Mode == AudiobookSleepTimerModes.Timer ? generation : 0;
        _sleepTimerMonotonicDeadlineTimestamp = candidateMonotonicDeadline;
        if (candidate.Mode == AudiobookSleepTimerModes.Timer)
        {
            _sleepTimerCts = new CancellationTokenSource();
            _ = CompleteSleepTimerAtDeadlineAsync(candidate, _sleepTimerCts.Token);
        }
        NotifyChanged();
        return SleepTimerState;
    }

    private async Task<bool> BindNativeSleepTimerAsync(AudiobookSleepTimerStateDto state)
    {
        var handlers = SleepTimerNativeArmRequested;
        if (handlers is null) return false;
        _nativeSleepTimerCandidateBeingBound = state;
        try
        {
            foreach (Func<AudiobookSleepTimerStateDto, Task<bool>> handler in handlers.GetInvocationList())
                if (!await handler(state)) return false;
            return true;
        }
        finally
        {
            if (ReferenceEquals(_nativeSleepTimerCandidateBeingBound, state))
                _nativeSleepTimerCandidateBeingBound = null;
        }
    }

    private async Task<double?> ReadSleepTimerNativePositionAsync(Guid expectedAssetId, long expectedPlaybackRequestVersion)
    {
        var handlers = SleepTimerNativePositionRequested;
        if (handlers is null || !double.IsFinite(CurrentTimeSeconds)) return null;
        double? position = null;
        foreach (Func<Guid, long, Task<double?>> handler in handlers.GetInvocationList())
        {
            position = await handler(expectedAssetId, expectedPlaybackRequestVersion);
            if (position is null || PlaybackRequestVersion != expectedPlaybackRequestVersion
                || CurrentItem?.AssetId != expectedAssetId) return null;
        }
        return position;
    }

    private async Task<bool> PrepareCurrentSleepTimerSourceAsync(PlaybackRequestGuard guard)
    {
        var state = SleepTimerState;
        var streamUrl = CurrentBrowserStreamUrl;
        if (state.Mode == AudiobookSleepTimerModes.Off) return true;
        if (!IsCurrentRequest(guard)
            || !IsCurrentSleepTimerArm(state.TimerGeneration, state.BoundAssetId, state.PlaybackRequestVersion)
            || CurrentItem?.AssetId != state.BoundAssetId
            || string.IsNullOrWhiteSpace(streamUrl)) return false;

        var handlers = SleepTimerNativeSourcePrepareRequested;
        if (handlers is null) return true;
        foreach (Func<AudiobookSleepTimerStateDto, string, Task<bool>> handler in handlers.GetInvocationList())
        {
            if (!await handler(state, streamUrl)) return false;
            if (!IsCurrentRequest(guard)
                || !IsCurrentSleepTimerArm(state.TimerGeneration, state.BoundAssetId, state.PlaybackRequestVersion)
                || CurrentBrowserStreamUrl != streamUrl) return false;
        }
        return true;
    }

    private async Task<bool> BindCurrentSleepTimerBeforeStartAsync(PlaybackRequestGuard guard)
    {
        if (SleepTimerState.Mode == AudiobookSleepTimerModes.Off) return true;
        if (!IsCurrentRequest(guard)) return false;
        var state = SleepTimerState;
        if (CurrentItem?.AssetId != state.BoundAssetId || PlaybackRequestVersion != state.PlaybackRequestVersion)
            return false;

        var bound = await BindNativeSleepTimerAsync(state);
        if (!IsCurrentRequest(guard)) return false;
        if (bound && IsCurrentSleepTimerArm(state.TimerGeneration, state.BoundAssetId, state.PlaybackRequestVersion)) return true;

        CancelSleepTimerWait();
        SleepTimerState = new AudiobookSleepTimerStateDto { TimerGeneration = ++_sleepTimerGeneration };
        IsPlaying = false;
        CurrentError = "The sleep timer could not be connected to this audiobook source. Choose the timer again after playback is ready.";
        _stateMachine.SetTransportState(false, false, CurrentError);
        NotifyChanged(PlaybackChangeKind.Error);
        return false;
    }

    public bool IsCurrentSleepTimerArm(long timerGeneration, Guid assetId, long requestVersion) =>
        SleepTimerState.Mode != AudiobookSleepTimerModes.Off
        && SleepTimerState.TimerGeneration == timerGeneration
        && SleepTimerState.BoundAssetId == assetId
        && SleepTimerState.PlaybackRequestVersion == requestVersion
        && PlaybackRequestVersion == requestVersion
        && _preferences?.ActiveProfileId == SleepTimerState.ProfileId
        && _audiobookTimerSessionId == SleepTimerState.TimerSessionId
        && CurrentItem?.AssetId == assetId
        && IsAudiobookMode
        && CurrentItem is { } item
        && (item.AudiobookWorkId ?? item.WorkId) == SleepTimerState.WorkId;

    public async Task HandleNativeSleepTimerExpiredAsync(
        long timerGeneration,
        Guid assetId,
        long requestVersion,
        double actualCurrentTimeSeconds,
        bool nativeEndOfFileConfirmed = false)
    {
        var state = SleepTimerState;
        var reachedTarget = state.Mode is AudiobookSleepTimerModes.EndCurrent or AudiobookSleepTimerModes.EndNext
            && assetId == state.TargetAssetId
            && double.IsFinite(actualCurrentTimeSeconds) && actualCurrentTimeSeconds >= 0
            && (actualCurrentTimeSeconds >= state.TargetEndSeconds
                || nativeEndOfFileConfirmed);
        var deadlineReached = state.Mode == AudiobookSleepTimerModes.Timer
            && IsSleepTimerDeadlineElapsed(state);
        var isCurrent = IsCurrentSleepTimerArm(timerGeneration, assetId, requestVersion);
        if (isCurrent && (reachedTarget || deadlineReached))
        {
            if (nativeEndOfFileConfirmed && !ProjectNativeEndOfFilePosition(assetId, requestVersion, actualCurrentTimeSeconds))
                return;
            await ExpireSleepTimerAsync(timerGeneration, assetId, requestVersion, nativeAlreadyPaused: true);
        }
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
        var subject = CurrentItem;
        var requestVersion = PlaybackRequestVersion;
        var rateSelectionVersion = _playbackRateSelectionVersion;
        if (_apiClient is null || subject is null || subject.AssetId is not Guid assetId)
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
            if (!IsCurrentProjection(requestVersion, subject)) return;
            var current = subject;
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
            if (IsCurrentProjection(requestVersion, subject)) ApplyPlayerState(state, rateSelectionVersion);
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
        if (_savedPlaybackRateInvalid)
        {
            IsPlaying = false;
            CurrentError = "The saved playback speed is unsupported. Choose a valid speed before playback can start.";
            _stateMachine.SetTransportState(false, false, CurrentError);
            NotifyChanged();
            return;
        }

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
        EndAudiobookBookSession();
        _queue.Clear();
        _history.Clear();
        _audiobookHistory.Clear();
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
        _lastAudibleVolume = PositiveVolumeOrFallback(Volume);
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
        _savedPlaybackRateInvalid = false;
        _stateMachine.SetIdle();
        PlaybackStartVersion++;
        CancelSleepTimerWait();
        SleepTimerState = new AudiobookSleepTimerStateDto { TimerGeneration = ++_sleepTimerGeneration };
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
        CurrentIndex = _queue.Count == 0
            ? -1
            : Math.Clamp(snapshot.CurrentIndex, 0, _queue.Count - 1);
        if (CurrentItem is { } restoredItem && MediaKindClassifier.IsAudiobook(restoredItem.MediaType))
            EstablishAudiobookBookSession(restoredItem.WorkId);
        else
            EndAudiobookBookSession();
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
        var restoredVolume = double.IsFinite(snapshot.Volume) && snapshot.Volume is >= 0 and <= 1
            ? snapshot.Volume
            : _clientSettings.DefaultVolume;
        _lastAudibleVolume = PositiveVolumeOrFallback(restoredVolume);
        IsMuted = snapshot.IsMuted;
        Volume = IsMuted && restoredVolume > 0d ? 0d : restoredVolume;
        var invalidSavedRate = !PlaybackRatePolicy.IsValid(snapshot.PlaybackRate);
        if (!invalidSavedRate) PlaybackRate = snapshot.PlaybackRate;
        _savedPlaybackRateInvalid = invalidSavedRate;
        _playbackRateSelectionVersion++;
        ShuffleEnabled = snapshot.ShuffleEnabled;
        RepeatMode = NormalizeRepeatMode(snapshot.RepeatMode);
        Experience = MediaKindClassifier.ToPlayerExperienceString(
            !string.IsNullOrWhiteSpace(CurrentItem?.MediaType)
                ? MediaKindClassifier.Classify(CurrentItem.MediaType)
                : MediaKindClassifier.FromPlayerExperienceString(snapshot.Experience));
        _currentAudiobookStartKind = IsAudiobookMode
            ? NormalizeAudiobookStartKind(CurrentItem?.AudiobookStartKind)
            : null;
        _audiobookHistory.Clear();
        _audiobookHistory.AddRange(ScopeAudiobookHistory(snapshot.AudiobookHistory ?? [], CurrentItem, _queue));
        NeedsUserGestureToStart = snapshot.NeedsUserGestureToStart;
        PlaybackRequestVersion = Math.Max(PlaybackRequestVersion, snapshot.PlaybackRequestVersion);
        IsPlaying = snapshot.IsPlaying && _queue.Count > 0 && !invalidSavedRate;
        IsPopupOpen = snapshot.IsPopupOpen;
        IsVideoExpanded = snapshot.IsVideoExpanded && IsVideoMode;
        PresentationSurface = IsVideoMode
            ? IsVideoExpanded ? PlaybackPresentationSurface.PrimaryVideo : PlaybackPresentationSurface.RestorableVideo
            : PlaybackPresentationSurface.Docked;
        if (IsVideoMode && !IsVideoExpanded) IsPlaying = false;
        CurrentError = invalidSavedRate
            ? "The saved playback speed is unsupported. Choose a valid speed before playback can start."
            : snapshot.CurrentError;
        _stateMachine.SetTransportState(IsPlaying, NeedsUserGestureToStart, invalidSavedRate ? null : CurrentError);
        SkipBackSeconds = snapshot.SkipBackSeconds > 0 ? snapshot.SkipBackSeconds : 15;
        SkipForwardSeconds = snapshot.SkipForwardSeconds > 0 ? snapshot.SkipForwardSeconds : 15;
        SleepTimerOptionsMinutes = NormalizeSleepTimerOptions(snapshot.SleepTimerOptionsMinutes);
        PlaybackStartVersion = Math.Max(PlaybackStartVersion, snapshot.PlaybackStartVersion);
        RestoreSleepTimerState(snapshot.SleepTimerState);
        NotifyChanged();
    }

    public ListenPlaybackSnapshot CreateSnapshot() => new()
    {
        AudiobookBookSessionLeaseId = AudiobookBookSessionLeaseId,
        AudiobookBookSessionGeneration = AudiobookBookSessionGeneration,
        Queue = _queue.ToList(),
        History = _history.ToList(),
        AudiobookHistory = _audiobookHistory.ToList(),
        CurrentIndex = CurrentIndex,
        SourceLabel = SourceLabel,
        CurrentBrowserStreamUrl = CurrentBrowserStreamUrl,
        Experience = Experience,
        PlaybackRequestVersion = this.PlaybackRequestVersion,
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
        PlaybackStartVersion = PlaybackStartVersion,
        SleepTimerState = SleepTimerState,
        SleepTimerAvailability = SleepTimerAvailability,
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

    private async Task EnsurePlayableAsync(int index, CancellationToken ct, PlaybackRequestGuard? guard = null)
    {
        if (guard is not null && !IsCurrentRequest(guard)) return;
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
                if ((guard is not null && !IsCurrentRequest(guard)) || index >= _queue.Count || _queue[index].AssetId != preparingAsset) return;
                var refreshed = await _apiClient.GetPlaybackManifestAsync(preparingAsset, _clientContext.Client, null, ct, ToConnectionContext());
                ct.ThrowIfCancellationRequested();
                if ((guard is not null && !IsCurrentRequest(guard)) || index >= _queue.Count || _queue[index].AssetId != preparingAsset) return;
                if (refreshed is null) continue;
                item = item with { Manifest = refreshed };
                _queue[index] = item;
            }
            CurrentError = _savedPlaybackRateInvalid
                ? "The saved playback speed is unsupported. Choose a valid speed before playback can start."
                : null;
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
            if ((guard is not null && !IsCurrentRequest(guard)) || index >= _queue.Count || _queue[index].WorkId != item.WorkId) return;
        }

        if (!assetId.HasValue)
        {
            MarkCurrentFailed("No playable file could be resolved for this item.");
            return;
        }

        var settings = _preferences is null ? null : await _preferences.GetAsync(ct);
        ct.ThrowIfCancellationRequested();
        if ((guard is not null && !IsCurrentRequest(guard)) || index >= _queue.Count || _queue[index].WorkId != item.WorkId) return;
        var profileId = settings?.ProfileId == Guid.Empty ? null : settings?.ProfileId;
        var manifest = await _apiClient.GetPlaybackManifestAsync(
            assetId.Value,
            _clientContext.Client,
            profileId,
            ct,
            ToConnectionContext());
        ct.ThrowIfCancellationRequested();
        if ((guard is not null && !IsCurrentRequest(guard)) || index >= _queue.Count || _queue[index].WorkId != item.WorkId) return;
        if (MediaKindClassifier.IsVideo(item.MediaType) && manifest is not null)
        {
            _queue[index] = item with { AssetId = assetId, Manifest = manifest };
            if (item.InitialPositionSeconds is null && !item.StartAtExactPosition && manifest.Resume?.PositionSeconds is { } resume)
                CurrentTimeSeconds = resume;
            DurationSeconds = manifest.DurationSeconds ?? 0;
            await EnsurePlayableAsync(index, ct, guard);
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

        var chapters = MediaKindClassifier.IsAudiobook(item.MediaType) && item.Chapters.Count > 0
            ? item.Chapters
            : NormalizeChapters(manifest?.Chapters ?? []);
        _queue[index] = item with
        {
            AssetId = assetId,
            StreamUrl = NormalizeStreamUrl(streamUrl),
            Chapters = chapters,
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
        CurrentError = _savedPlaybackRateInvalid
            ? "The saved playback speed is unsupported. Choose a valid speed before playback can start."
            : null;
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
        CancellationToken ct,
        PlaybackRequestGuard? guard = null)
    {
        if ((guard is not null && !IsCurrentRequest(guard)) || _apiClient is null || items.Count == 0)
        {
            return;
        }

        try
        {
            var profile = await _orchestrator.GetActiveProfileAsync(ct);
            if (guard is not null && !IsCurrentRequest(guard)) return;
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

    private async Task<double> InitialPlaybackRateForAsync(ListenQueueItem item, CancellationToken ct,
        long rateSelectionVersion, PlaybackRequestGuard guard)
    {
        if (!IsCurrentRequest(guard) || rateSelectionVersion != _playbackRateSelectionVersion)
            return PlaybackRate;

        if (MediaKindClassifier.IsMusic(item.MediaType))
        {
            _savedPlaybackRateInvalid = false;
            return 1d;
        }

        var settings = await PlaybackSettingsAsync(ct);
        if (!IsCurrentRequest(guard) || rateSelectionVersion != _playbackRateSelectionVersion)
            return PlaybackRate;

        var configured = MediaKindClassifier.IsVideo(item.MediaType)
            ? (double)settings.Watching.DefaultPlaybackSpeed
            : (double)settings.Listening.AudiobookDefaultSpeed;
        if (PlaybackRatePolicy.IsValid(configured))
        {
            _savedPlaybackRateInvalid = false;
            return configured;
        }

        _savedPlaybackRateInvalid = true;
        CurrentError = "The saved playback speed is unsupported. Choose a valid speed before playback can start.";
        return PlaybackRate;
    }

    private void ClearPlaybackRateError()
    {
        if (CurrentError?.Contains("playback speed is unsupported", StringComparison.OrdinalIgnoreCase) == true
            || CurrentError?.StartsWith("Playback speed must be between", StringComparison.OrdinalIgnoreCase) == true)
        {
            CurrentError = null;
        }
    }

    private async Task<UserPlaybackSettingsDto> PlaybackSettingsAsync(CancellationToken ct)
    {
        if (_preferences is null)
        {
            return UserPlaybackSettingsDto.CreateDefaults(Guid.Empty);
        }

        var settings = await _preferences.GetAsync(ct) ?? UserPlaybackSettingsDto.CreateDefaults(Guid.Empty);
        ct.ThrowIfCancellationRequested();
        return settings;
    }

    private void ApplyListeningSettings(ListeningSettingsDto settings)
    {
        SkipBackSeconds = settings.SkipBackSeconds;
        SkipForwardSeconds = settings.SkipForwardSeconds;
        ResumeRewindSeconds = settings.ResumeRewindSeconds;
        AudiobookNearStartGuardSeconds = settings.AudiobookNearStartGuardSeconds;
        SleepTimerOptionsMinutes = NormalizeSleepTimerOptions(settings.SleepTimerOptionsMinutes);
        _allowEndOfChapterSleepTimer = settings.AllowEndOfChapterSleepTimer;
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
        return PlaybackRateOptions.BuildChoices(PlaybackRate)
            .Select(option => option.Rate)
            .Append((double)settings.Listening.AudiobookDefaultSpeed)
            .Where(PlaybackRatePolicy.IsValid)
            .Distinct()
            .Order()
            .ToList();
    }

    private async Task RefreshAudiobookHistoryAsync(CancellationToken ct, PlaybackRequestGuard? guard = null)
    {
        _audiobookHistory.Clear();
        var subject = CurrentItem;
        var requestVersion = PlaybackRequestVersion;
        var workId = AudiobookIdentityId(subject);
        if (!IsAudiobookMode || subject is null || workId == Guid.Empty || _apiClient is null)
        {
            return;
        }

        bool IsStillCurrent() => guard is not null
            ? IsCurrentRequest(guard)
            : IsCurrentProjection(requestVersion, subject);

        try
        {
            var settings = await PlaybackSettingsAsync(ct);
            if (!IsStillCurrent()) return;
            var items = await _apiClient.GetAudiobookListenHistoryAsync(workId, limit: settings.Listening.AudiobookHistoryLimit, ct: ct);
            if (!IsStillCurrent()) return;
            _audiobookHistory.AddRange(ScopeAudiobookHistory(items, subject, _queue));
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not load audiobook listen history.");
        }
    }

    internal void ApplyPlayerState(PlayerStateDto? state, long? expectedRateSelectionVersion = null, bool applyPlaybackRate = true)
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

        var reportedExperience = MediaKindClassifier.FromPlayerExperienceString(state.Experience);
        Experience = CurrentItem is { } current
            && MediaKindClassifier.TryClassifyKnown(current.MediaType, out var subjectExperience)
                ? MediaKindClassifier.ToPlayerExperienceString(subjectExperience)
                : MediaKindClassifier.ToPlayerExperienceString(reportedExperience);
        if (applyPlaybackRate
            && !_savedPlaybackRateInvalid
            && (expectedRateSelectionVersion is null || expectedRateSelectionVersion == _playbackRateSelectionVersion)
            && PlaybackRatePolicy.IsValid(state.PlaybackRate) && PlaybackRate != state.PlaybackRate)
        {
            _playbackRateSelectionVersion++;
            PlaybackRate = state.PlaybackRate;
        }
        ShuffleEnabled = state.ShuffleEnabled;
        RepeatMode = NormalizeRepeatMode(state.RepeatMode);
        _audiobookHistory.Clear();
        _audiobookHistory.AddRange(ScopeAudiobookHistory(state.AudiobookHistory ?? [], CurrentItem, _queue));
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

    public static IReadOnlyList<AudiobookListenHistoryItemDto> ScopeAudiobookHistory(
        IEnumerable<AudiobookListenHistoryItemDto> items,
        ListenQueueItem? current,
        IReadOnlyList<ListenQueueItem> queue)
    {
        if (current is null || !MediaKindClassifier.IsAudiobook(current.MediaType)) return [];

        var bookId = AudiobookIdentityId(current);
        if (bookId == Guid.Empty) return [];

        var relatedItems = queue
            .Where(item => MediaKindClassifier.IsAudiobook(item.MediaType)
                && (item.WorkId == bookId
                    || AudiobookIdentityId(item) == bookId))
            .Append(current)
            .DistinctBy(item => item.WorkId)
            .ToList();
        var workIds = relatedItems.Select(item => item.WorkId).Append(bookId).ToHashSet();
        var assetIds = relatedItems
            .Select(item => item.AssetId)
            .Concat(relatedItems.SelectMany(item => item.Chapters).Select(chapter => chapter.AssetId))
            .Where(assetId => assetId.HasValue && assetId.Value != Guid.Empty)
            .Select(assetId => assetId!.Value)
            .ToHashSet();

        if (assetIds.Count == 0) return [];
        var inScope = items
            .Where(item => workIds.Contains(item.WorkId) && assetIds.Contains(item.AssetId))
            .Select(item => NormalizeAudiobookHistoryIdentity(item, relatedItems));
        return CleanAudiobookHistory(inScope);
    }

    private static AudiobookListenHistoryItemDto NormalizeAudiobookHistoryIdentity(
        AudiobookListenHistoryItemDto history,
        IReadOnlyList<ListenQueueItem> relatedItems)
    {
        var source = relatedItems.FirstOrDefault(item => item.AssetId == history.AssetId
            || item.Chapters.Any(chapter => chapter.AssetId == history.AssetId));
        if (source is null) return history;

        var chapter = source.Chapters.FirstOrDefault(item => item.AssetId == history.AssetId
            && HistoryPositionIsWithinChapter(history.PositionSeconds, item));
        if (chapter is null && history.ChapterIndex is >= 0)
        {
            chapter = source.Chapters.FirstOrDefault(item => item.Index == history.ChapterIndex.Value
                && (item.AssetId == history.AssetId
                    || (source.AssetId == history.AssetId && !item.AssetId.HasValue))
                && HistoryPositionIsWithinChapter(history.PositionSeconds, item));
        }

        if (chapter is null)
        {
            var positionMatches = source.Chapters
                .Where(item => (item.AssetId == history.AssetId
                    || (source.AssetId == history.AssetId && !item.AssetId.HasValue))
                    && HistoryPositionIsWithinChapter(history.PositionSeconds, item))
                .Take(2)
                .ToList();
            if (positionMatches.Count == 1) chapter = positionMatches[0];
        }

        return history with
        {
            Title = source.Title,
            ChapterTitle = chapter?.Title,
            ChapterIndex = chapter?.Index,
        };
    }

    private static bool HistoryPositionIsWithinChapter(double positionSeconds, PlaybackChapterDto chapter) =>
        double.IsFinite(positionSeconds)
        && positionSeconds >= chapter.StartSeconds
        && (!chapter.EndSeconds.HasValue || positionSeconds < chapter.EndSeconds.Value);

    private static Guid AudiobookIdentityId(ListenQueueItem? item)
    {
        if (item is null) return Guid.Empty;
        if (item.AudiobookWorkId is { } audiobookWorkId && audiobookWorkId != Guid.Empty) return audiobookWorkId;
        if (MediaKindClassifier.IsAudiobook(item.MediaType)
            && item.AlbumWorkId is { } legacyBookId
            && legacyBookId != Guid.Empty) return legacyBookId;
        return item.WorkId;
    }

    private static IReadOnlyList<int> NormalizeSleepTimerOptions(IEnumerable<int>? options)
    {
        var defaults = new ListeningSettingsDto().SleepTimerOptionsMinutes;
        var values = (options ?? defaults)
            .Concat([15, 30, 45, 60])
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
        var subject = CurrentItem;
        var requestVersion = PlaybackRequestVersion;
        var rateSelectionVersion = _playbackRateSelectionVersion;
        try
        {
            var profile = await _orchestrator.GetActiveProfileAsync();
            if (!IsCurrentProjectionOrEmpty(requestVersion, subject)) return;
            var state = await _apiClient.SendPlayerCommandAsync(new PlayerCommandRequestDto
            {
                ProfileId = profile?.Id,
                DeviceId = _clientContext.DeviceId,
                Client = _clientContext.Client,
                Command = command,
                ShuffleEnabled = shuffleEnabled,
                RepeatMode = repeatMode,
            }, ct);
            if (IsCurrentProjectionOrEmpty(requestVersion, subject)) ApplyPlayerState(state, rateSelectionVersion, applyPlaybackRate: false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not sync player mode command {Command}.", command);
        }
    }

    private async Task CompleteSleepTimerAtDeadlineAsync(AudiobookSleepTimerStateDto capturedState, CancellationToken ct)
    {
        try
        {
            var remainingTicks = _sleepTimerMonotonicGeneration == capturedState.TimerGeneration
                ? _sleepTimerMonotonicDeadlineTimestamp - Stopwatch.GetTimestamp()
                : (long)Math.Ceiling(Math.Max(0, (capturedState.DeadlineUtc.GetValueOrDefault() - DateTimeOffset.UtcNow).TotalSeconds) * Stopwatch.Frequency);
            if (remainingTicks > 0)
                await Task.Delay(TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency), ct);

            var currentArm = SleepTimerState;
            if (currentArm.Mode == AudiobookSleepTimerModes.Timer
                && currentArm.TimerSessionId == capturedState.TimerSessionId
                && currentArm.DeadlineUtc == capturedState.DeadlineUtc
                && currentArm.BoundAssetId != Guid.Empty
                && IsSleepTimerDeadlineElapsed(currentArm))
                await ExpireSleepTimerAsync(currentArm.TimerGeneration, currentArm.BoundAssetId, currentArm.PlaybackRequestVersion, nativeAlreadyPaused: false);
        }
        catch (OperationCanceledException) { }
    }

    private static long MonotonicDeadlineFromUtc(DateTimeOffset deadlineUtc)
    {
        var remainingSeconds = Math.Max(0, (deadlineUtc - DateTimeOffset.UtcNow).TotalSeconds);
        var now = Stopwatch.GetTimestamp();
        var delta = remainingSeconds * Stopwatch.Frequency;
        return delta >= long.MaxValue - now ? long.MaxValue : now + (long)Math.Ceiling(delta);
    }

    private bool IsSleepTimerDeadlineElapsed(AudiobookSleepTimerStateDto state) =>
        state.Mode == AudiobookSleepTimerModes.Timer
        && _sleepTimerMonotonicGeneration == state.TimerGeneration
        && Stopwatch.GetTimestamp() >= _sleepTimerMonotonicDeadlineTimestamp;

    private async Task ExpireSleepTimerAsync(long timerGeneration, Guid assetId, long requestVersion, bool nativeAlreadyPaused)
    {
        if (!IsCurrentSleepTimerArm(timerGeneration, assetId, requestVersion))
        {
            return;
        }
        var subject = CurrentItem;

        if (!nativeAlreadyPaused)
        {
            var handlers = SleepTimerPauseRequested;
            var paused = handlers is not null;
            if (handlers is not null)
            {
                foreach (Func<AudiobookSleepTimerStateDto, Task<bool>> handler in handlers.GetInvocationList())
                {
                    if (!await handler(SleepTimerState))
                    {
                        paused = false;
                        break;
                    }
                }
            }
            if (!paused)
            {
                if (IsCurrentSleepTimerArm(timerGeneration, assetId, requestVersion))
                {
                    CurrentError = "The sleep timer reached its target, but playback could not be paused.";
                    NotifyChanged(PlaybackChangeKind.Error);
                }
                return;
            }
            if (!IsCurrentSleepTimerArm(timerGeneration, assetId, requestVersion)) return;
        }

        CancelSleepTimerWait();
        SleepTimerState = new AudiobookSleepTimerStateDto { TimerGeneration = ++_sleepTimerGeneration };
        IsPlaying = false;
        _stateMachine.SetTransportState(false, false, null);
        NotifyChanged();
        if (subject is not null && IsCurrentProjection(requestVersion, subject))
            await ReportHeartbeatAsync(force: true);
    }

    private AudiobookSleepTimerAvailabilityDto GetSleepTimerAvailability()
    {
        if (!IsAudiobookMode || CurrentItem is not { AssetId: Guid assetId } current || assetId == Guid.Empty)
            return new() { CurrentUnavailableReason = "Audiobook chapter timing is unavailable.", NextUnavailableReason = "Audiobook chapter timing is unavailable." };
        if (!_allowEndOfChapterSleepTimer)
            return new() { CurrentUnavailableReason = "Chapter boundary timers are not enabled for this profile.", NextUnavailableReason = "Chapter boundary timers are not enabled for this profile." };

        var chapters = GetTimerChapters(current);
        var authorized = CurrentAuthorizedAudiobookAssetIds(current);
        var currentWorkId = current.AudiobookWorkId ?? current.WorkId;
        var currentTimerSession = _audiobookTimerSessionId is { } session
            && _audiobookTimerSessionProfileId == _preferences?.ActiveProfileId
            && _audiobookTimerSessionWorkId == currentWorkId
                ? session
                : currentWorkId;
        AudiobookSleepBoundaryTarget? currentTarget = null;
        AudiobookSleepBoundaryTarget? nextTarget = null;
        var canCurrent = AudiobookSleepBoundaryResolver.TryCapture(currentWorkId,
            currentTimerSession, assetId, CurrentTimeSeconds, chapters, authorized,
            AudiobookSleepBoundaryKind.EndOfChapter, out currentTarget);
        var canNext = AudiobookSleepBoundaryResolver.TryCapture(currentWorkId,
            currentTimerSession, assetId, CurrentTimeSeconds, chapters, authorized,
            AudiobookSleepBoundaryKind.EndOfNextChapter, out nextTarget);
        return new()
        {
            CanEndCurrent = canCurrent,
            CanEndNext = canNext,
            CurrentChapterTitle = currentTarget?.TargetChapterTitle,
            NextChapterTitle = nextTarget?.TargetChapterTitle,
            CurrentUnavailableReason = canCurrent ? null : "A uniquely timed current chapter is not available at this position.",
            NextUnavailableReason = canNext ? null : "The immediate next chapter does not have an authorized finite end.",
        };
    }

    private IReadOnlyList<PlaybackChapterDto> GetTimerChapters(ListenQueueItem item)
    {
        var chapters = item.Chapters;
        if (chapters.Count == 0) return [];
        if (chapters.All(chapter => chapter.AssetId.HasValue)) return chapters;

        // A manifest is asset-scoped. It can bind embedded null-asset chapters only when
        // both the manifest and requested item identify this exact current asset.
        if (item.AssetId is not Guid assetId || item.Manifest?.AssetId != assetId) return chapters;
        var manifest = item.Manifest.Chapters.ToDictionary(chapter => chapter.Index);
        return chapters.Select(chapter => chapter.AssetId.HasValue
            ? chapter
            : manifest.TryGetValue(chapter.Index, out var source)
                && source.StartSeconds == chapter.StartSeconds
                && source.EndSeconds == chapter.EndSeconds
                ? chapter with { AssetId = assetId }
                : chapter).ToArray();
    }

    private IReadOnlySet<Guid> CurrentAuthorizedAudiobookAssetIds(ListenQueueItem current)
    {
        var workId = current.AudiobookWorkId ?? current.WorkId;
        return _queue.Where(item => MediaKindClassifier.IsAudiobook(item.MediaType)
                && (item.WorkId == workId || (item.AudiobookWorkId ?? item.WorkId) == workId))
            .Append(current)
            .SelectMany(item => item.Chapters.Select(chapter => chapter.AssetId).Append(item.AssetId))
            .Where(assetId => assetId.HasValue && assetId.Value != Guid.Empty)
            .Select(assetId => assetId!.Value)
            .ToHashSet();
    }

    private void CancelSleepTimerWait()
    {
        _sleepTimerCts?.Cancel();
        _sleepTimerCts?.Dispose();
        _sleepTimerCts = null;
    }

    private bool IsAtCapturedSleepBoundary() => CurrentItem?.AssetId == SleepTimerState.TargetAssetId
        && SleepTimerState.BoundAssetId == SleepTimerState.TargetAssetId
        && CurrentTimeSeconds >= SleepTimerState.TargetEndSeconds;

    private void PrepareSleepTimerForAudiobookStart(ListenQueueItem item, string startKind, AudiobookStartIntent intent)
    {
        if (SleepTimerState.Mode == AudiobookSleepTimerModes.Off) return;
        var workId = item.AudiobookWorkId ?? item.WorkId;
        var sameTimerSession = SleepTimerState.WorkId == workId
            && SleepTimerState.ProfileId == _preferences?.ActiveProfileId
            && _audiobookTimerSessionId == SleepTimerState.TimerSessionId;
        var exactCapturedSuccessor = item.AssetId == SleepTimerState.TargetAssetId
            && item.ChapterIndex == SleepTimerState.TargetChapterIndex;
        var sameBookChapterNavigation = string.Equals(startKind, AudiobookStartKinds.Chapter, StringComparison.Ordinal)
            && IsAudiobookMode
            && CurrentItem is { } current
            && (current.AudiobookWorkId ?? current.WorkId) == workId
            && item.AssetId.HasValue
            && (item.AssetId == current.AssetId || exactCapturedSuccessor
                || CurrentAuthorizedAudiobookAssetIds(current).Contains(item.AssetId.Value));
        var capturedPreviewSameBookAsset = intent == AudiobookStartIntent.CapturedPreview
            && IsAudiobookMode
            && CurrentItem is { } previewCurrent
            && (previewCurrent.AudiobookWorkId ?? previewCurrent.WorkId) == workId
            && item.AssetId.HasValue
            && CurrentAuthorizedAudiobookAssetIds(previewCurrent).Contains(item.AssetId.Value);
        var capturedPreviewBoundaryMovement = capturedPreviewSameBookAsset
            && (item.AssetId == SleepTimerState.OriginAssetId || item.AssetId == SleepTimerState.TargetAssetId);
        var boundaryTimer = SleepTimerState.Mode is AudiobookSleepTimerModes.EndCurrent or AudiobookSleepTimerModes.EndNext;
        var sameAssetChapterProgression = boundaryTimer
            && sameBookChapterNavigation
            && item.AssetId == SleepTimerState.BoundAssetId;
        var capturedTargetAssetNavigation = boundaryTimer
            && sameBookChapterNavigation
            && item.AssetId == SleepTimerState.TargetAssetId;
        var timerProgression = SleepTimerState.Mode == AudiobookSleepTimerModes.Timer
            ? sameBookChapterNavigation || capturedPreviewSameBookAsset
            : (exactCapturedSuccessor && SleepTimerState.Mode == AudiobookSleepTimerModes.EndNext)
                || sameAssetChapterProgression || capturedTargetAssetNavigation || capturedPreviewBoundaryMovement;
        if (!sameTimerSession || !timerProgression)
        {
            CancelSleepTimerWait();
            SleepTimerState = new AudiobookSleepTimerStateDto { TimerGeneration = ++_sleepTimerGeneration };
            if (!sameTimerSession)
            {
                _audiobookTimerSessionId = null;
                _audiobookTimerSessionWorkId = null;
                _audiobookTimerSessionProfileId = null;
            }
            return;
        }

        // Keep a boundary arm when a same-asset chapter selection starts at or beyond the
        // captured end. The native seek/scheduler observes that exact selected position and
        // expires the original target; changing the target or clearing the arm here would let
        // playback run past the user's chosen boundary.
        // Rebinding preserves the arm generation and exact target while refreshing its
        // captured native request binding.
        SleepTimerState = SleepTimerState with
        {
            BoundAssetId = item.AssetId ?? Guid.Empty,
            PlaybackRequestVersion = PlaybackRequestVersion + 1,
        };
    }

    private void RestoreSleepTimerState(AudiobookSleepTimerStateDto? state)
    {
        CancelSleepTimerWait();
        var item = CurrentItem;
        var profile = _preferences?.ActiveProfileId;
        var supportedMode = state?.Mode is AudiobookSleepTimerModes.Timer
            or AudiobookSleepTimerModes.EndCurrent or AudiobookSleepTimerModes.EndNext;
        var validBoundary = state?.Mode is not AudiobookSleepTimerModes.EndCurrent and not AudiobookSleepTimerModes.EndNext;
        if (state is { Mode: AudiobookSleepTimerModes.EndCurrent or AudiobookSleepTimerModes.EndNext } && item is not null)
        {
            var sourceChapters = GetTimerChapters(item);
            var authorizedAssets = CurrentAuthorizedAudiobookAssetIds(item);
            var origin = sourceChapters.SingleOrDefault(chapter => chapter.AssetId == state.OriginAssetId
                && chapter.Index == state.OriginChapterIndex);
            var target = sourceChapters.SingleOrDefault(chapter => chapter.AssetId == state.TargetAssetId
                && chapter.Index == state.TargetChapterIndex);
            validBoundary = origin is not null && target is not null
                && authorizedAssets.Contains(state.OriginAssetId) && authorizedAssets.Contains(state.TargetAssetId)
                && origin.EndSeconds is double originEnd && double.IsFinite(originEnd) && originEnd > origin.StartSeconds
                && target.EndSeconds is double targetEnd && double.IsFinite(targetEnd)
                && targetEnd == state.TargetEndSeconds
                && string.Equals(target.Title, state.TargetChapterTitle, StringComparison.Ordinal);
        }
        if (state is null || state.Mode == AudiobookSleepTimerModes.Off || item is null || !IsAudiobookMode
            || !supportedMode || !validBoundary
            || state.ProfileId == Guid.Empty || state.ProfileId != profile
            || state.WorkId != (item.AudiobookWorkId ?? item.WorkId)
            || state.BoundAssetId != item.AssetId || state.TimerSessionId == Guid.Empty
            || (state.Mode == AudiobookSleepTimerModes.Timer
                && (state.DeadlineUtc is null || state.ChosenMinutes is not int minutes || !SleepTimerOptionsMinutes.Contains(minutes))))
        {
            SleepTimerState = new AudiobookSleepTimerStateDto { TimerGeneration = ++_sleepTimerGeneration };
            return;
        }

        _sleepTimerGeneration = Math.Max(_sleepTimerGeneration, state.TimerGeneration);
        SleepTimerState = state with { PlaybackRequestVersion = PlaybackRequestVersion };
        _audiobookTimerSessionId = state.TimerSessionId;
        _audiobookTimerSessionWorkId = state.WorkId;
        _audiobookTimerSessionProfileId = state.ProfileId;
        if (state.Mode == AudiobookSleepTimerModes.Timer)
        {
            _sleepTimerMonotonicGeneration = state.TimerGeneration;
            _sleepTimerMonotonicDeadlineTimestamp = MonotonicDeadlineFromUtc(state.DeadlineUtc!.Value);
            if (IsSleepTimerDeadlineElapsed(SleepTimerState))
            {
                _ = ExpireSleepTimerAsync(state.TimerGeneration, state.BoundAssetId, PlaybackRequestVersion, nativeAlreadyPaused: false);
                return;
            }
            _sleepTimerCts = new CancellationTokenSource();
            _ = CompleteSleepTimerAtDeadlineAsync(SleepTimerState, _sleepTimerCts.Token);
        }
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

    private void EstablishAudiobookBookSession(Guid workId, bool preserveExisting = false)
    {
        if (workId == Guid.Empty)
        {
            EndAudiobookBookSession();
            return;
        }
        if (preserveExisting && _audiobookBookSessionWorkId == workId && _audiobookBookSessionLeaseId.HasValue) return;
        _audiobookBookSessionWorkId = workId;
        _audiobookBookSessionLeaseId = Guid.NewGuid();
        _audiobookBookSessionGeneration++;
    }

    private void EndAudiobookBookSession()
    {
        if (_audiobookBookSessionLeaseId is null && _audiobookBookSessionWorkId is null) return;
        _audiobookBookSessionLeaseId = null;
        _audiobookBookSessionWorkId = null;
        _audiobookBookSessionGeneration++;
    }

    private void NotifyChanged(PlaybackChangeKind kind = PlaybackChangeKind.State)
    {
        RefreshUpcomingQueue();
        Changed?.Invoke(kind);
    }

    private void PublishNewSubjectProjection(ListenQueueItem item, double? continuationRate = null)
    {
        CurrentTimeSeconds = 0;
        DurationSeconds = 0;
        PlaybackRate = continuationRate is { } exactRate && PlaybackRatePolicy.IsValid(exactRate)
            ? exactRate
            : 1d;
        CurrentError = null;
        NeedsUserGestureToStart = false;
        IsDismissed = false;
        IsPlaying = true;
        _audiobookHistory.Clear();
        if (!IsAudiobookMode && SleepTimerState.Mode != AudiobookSleepTimerModes.Off)
        {
            CancelSleepTimerWait();
            SleepTimerState = new AudiobookSleepTimerStateDto { TimerGeneration = ++_sleepTimerGeneration };
            _audiobookTimerSessionId = null;
            _audiobookTimerSessionWorkId = null;
            _audiobookTimerSessionProfileId = null;
        }
        else if (IsAudiobookMode && SleepTimerState.Mode != AudiobookSleepTimerModes.Off
            && CurrentItem is { AssetId: Guid boundAsset } timerItem
            && SleepTimerState.WorkId == (timerItem.AudiobookWorkId ?? timerItem.WorkId)
            && SleepTimerState.ProfileId == _preferences?.ActiveProfileId
            && boundAsset == SleepTimerState.BoundAssetId
            && PlaybackRequestVersion == SleepTimerState.PlaybackRequestVersion)
        {
            // The arm is already rebound by the pre-start session check; this branch
            // keeps a valid transition visible while the new stream is resolving.
        }
        else if (IsAudiobookMode && SleepTimerState.Mode != AudiobookSleepTimerModes.Off
            && PlaybackRequestVersion != SleepTimerState.PlaybackRequestVersion)
        {
            CancelSleepTimerWait();
            SleepTimerState = new AudiobookSleepTimerStateDto { TimerGeneration = ++_sleepTimerGeneration };
        }
        _stateMachine.SetLoading();
        ApplyExperienceSettings(UserPlaybackSettingsDto.CreateDefaults(Guid.Empty));
        if (IsVideoMode)
        {
            PresentationSurface = PlaybackPresentationSurface.PrimaryVideo;
            IsVideoExpanded = true;
            IsPanelOpen = false;
            ActiveTab = ListenPlaybackTabs.Queue;
        }
        else
        {
            if (PresentationSurface is PlaybackPresentationSurface.PrimaryVideo or PlaybackPresentationSurface.PictureInPicture or PlaybackPresentationSurface.RestorableVideo or PlaybackPresentationSurface.Fullscreen)
            {
                PresentationSurface = PlaybackPresentationSurface.Docked;
                IsVideoExpanded = false;
            }
            if (!IsMusicMode && ActiveTab == ListenPlaybackTabs.Lyrics)
            {
                ActiveTab = ListenPlaybackTabs.Queue;
                IsPanelOpen = false;
            }
        }
        _currentAudiobookStartKind = IsAudiobookMode
            ? NormalizeAudiobookStartKind(item.AudiobookStartKind)
            : null;
        NotifyChanged();
    }

    private bool IsCurrentRequest(CancellationTokenSource request, long version, ListenQueueItem subject)
        => ReferenceEquals(_startCancellation, request)
            && !request.IsCancellationRequested
            && PlaybackRequestVersion == version
            && CurrentItem is { } current
            && current.WorkId == subject.WorkId
            && (!subject.AssetId.HasValue || current.AssetId == subject.AssetId);

    private bool IsCurrentProjection(long version, ListenQueueItem subject)
        => PlaybackRequestVersion == version
            && CurrentItem is { } current
            && current.WorkId == subject.WorkId
            && (!subject.AssetId.HasValue || current.AssetId == subject.AssetId);

    private bool IsCurrentProjectionOrEmpty(long version, ListenQueueItem? subject)
        => PlaybackRequestVersion == version
            && (subject is null
                ? CurrentItem is null
                : IsCurrentProjection(version, subject));

    private bool IsCurrentRequest(PlaybackRequestGuard guard)
        => IsCurrentRequest(guard.Request, guard.Version, guard.Subject);

    private sealed record PlaybackRequestGuard(CancellationTokenSource Request, long Version, ListenQueueItem Subject);

    private void RefreshUpcomingQueue()
    {
        _upcomingQueue = CurrentIndex < 0 || CurrentIndex >= _queue.Count
            ? _queue.ToList()
            : _queue.Skip(CurrentIndex + 1).ToList();
    }
}
