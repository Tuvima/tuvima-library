namespace MediaEngine.Contracts.Playback;

/// <summary>The single transient command envelope exchanged by player hosts.</summary>
public sealed record ListenPlaybackCommandDto
{
    public Guid CommandId { get; init; }
    public Guid SenderId { get; init; }
    public Guid RecipientId { get; init; }
    public Guid? DialogId { get; init; }
    public long OwnerGeneration { get; init; }
    public string Action { get; init; } = string.Empty;
    public string? Tab { get; init; }
    public Guid? ProfileId { get; init; }
    public Guid? WorkId { get; init; }
    public Guid? SessionLeaseId { get; init; }
    public Guid? ExpectedAssetId { get; init; }
    /// <summary>Playback start generation observed by the sender; required for rate changes from a popup.</summary>
    public long? ExpectedPlaybackRequestVersion { get; init; }
    public int? Index { get; init; }
    public int? ChapterIndex { get; init; }
    public double? Value { get; init; }
    public PlayerQueueItemDto? QueueItem { get; init; }
    public AudiobookListenHistoryItemDto? AudiobookHistoryItem { get; init; }
    public Guid? BookmarkId { get; init; }
    public AudiobookBookmarkDraftPayloadDto? BookmarkDraft { get; init; }
    public long? DraftGeneration { get; init; }
    public AudiobookSleepTimerSelectionDto? SleepTimer { get; init; }
    public ListenPlaybackCommandReplyDto? Reply { get; init; }
}

public sealed record AudiobookBookmarkDraftPayloadDto
{
    public long DraftGeneration { get; init; }
    public Guid ProfileId { get; init; }
    public Guid WorkId { get; init; }
    public Guid SessionLeaseId { get; init; }
    public Guid AssetId { get; init; }
    public int? ChapterIndex { get; init; }
    public string? ChapterTitle { get; init; }
    public double PositionSeconds { get; init; }
    public double? DurationSeconds { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
    public string? Note { get; init; }
}

public sealed record AudiobookSleepTimerSelectionDto
{
    public string Mode { get; init; } = AudiobookSleepTimerModes.Off;
    public int? Minutes { get; init; }
}

/// <summary>The authoritative timer arm and its exact, owner-captured playback boundary.</summary>
public sealed record AudiobookSleepTimerStateDto
{
    public string Mode { get; init; } = AudiobookSleepTimerModes.Off;
    public int? ChosenMinutes { get; init; }
    public DateTimeOffset? DeadlineUtc { get; init; }
    public long TimerGeneration { get; init; }
    public Guid ProfileId { get; init; }
    public Guid WorkId { get; init; }
    public Guid TimerSessionId { get; init; }
    public Guid OriginAssetId { get; init; }
    public int OriginChapterIndex { get; init; }
    public Guid TargetAssetId { get; init; }
    public int TargetChapterIndex { get; init; }
    public string TargetChapterTitle { get; init; } = string.Empty;
    public double TargetEndSeconds { get; init; }
    public Guid BoundAssetId { get; init; }
    public long PlaybackRequestVersion { get; init; }
}

/// <summary>Owner-computed availability for the current verified chapter timeline.</summary>
public sealed record AudiobookSleepTimerAvailabilityDto
{
    public bool CanEndCurrent { get; init; }
    public bool CanEndNext { get; init; }
    public string? CurrentChapterTitle { get; init; }
    public string? NextChapterTitle { get; init; }
    public string? CurrentUnavailableReason { get; init; }
    public string? NextUnavailableReason { get; init; }
}

public static class AudiobookSleepTimerModes
{
    public const string Off = "off";
    public const string Timer = "timer";
    public const string EndCurrent = "end-current";
    public const string EndNext = "end-next";
}

public static class ListenPlaybackCommandActions
{
    public const string TogglePlay = "toggle-play";
    public const string Pause = "pause";
    public const string PlayNext = "play-next";
    public const string PlayPrevious = "play-previous";
    public const string PlayNextChapter = "play-next-chapter";
    public const string PlayPreviousChapter = "play-previous-chapter";
    public const string SkipBack = "skip-back";
    public const string SkipForward = "skip-forward";
    public const string PlayChapter = "play-chapter";
    public const string TogglePanel = "toggle-panel";
    public const string ShowQueue = "show-queue";
    public const string ClearUpcoming = "clear-upcoming";
    public const string RemoveUpcoming = "remove-upcoming";
    public const string PlayIndex = "play-index";
    public const string PlayHistory = "play-history";
    public const string PlayAudiobookHistory = "play-audiobook-history";
    public const string SetTab = "set-tab";
    public const string Seek = "seek";
    public const string SetVolume = "set-volume";
    public const string SetSpeed = "set-speed";
    public const string SetSleepTimer = "set-sleep-timer";
    public const string ToggleMute = "toggle-mute";
    public const string ToggleShuffle = "toggle-shuffle";
    public const string CycleRepeat = "cycle-repeat";
    public const string ClosePlayer = "close-player";
    public const string PopupClosed = "popup-closed";
    public const string OpenBookmarkDialog = "open-bookmark-dialog";
    public const string BookmarkDialogState = "bookmark-dialog-state";
    public const string PreviewBookmarkDraft = "preview-bookmark-draft";
    public const string LoadBookmarks = "load-bookmarks";
    public const string SaveBookmarkDraft = "save-bookmark-draft";
    public const string ReplayBookmark = "replay-bookmark";
    public const string RequestDeleteBookmark = "request-delete-bookmark";
    public const string ConfirmDeleteBookmark = "confirm-delete-bookmark";
    public const string CancelDeleteBookmark = "cancel-delete-bookmark";
    public const string CloseBookmarkDialog = "close-bookmark-dialog";
}

public static class AudiobookBookmarkOperationOutcomes
{
    public const string Success = "success";
    public const string DefiniteFailure = "definite-failure";
    public const string Unknown = "unknown";
}

/// <summary>Ephemeral reply for correlating a popup action with its owner.</summary>
public sealed record ListenPlaybackCommandReplyDto
{
    public Guid CommandId { get; init; }
    /// <summary>The requesting host that should accept this transient reply.</summary>
    public Guid RecipientId { get; init; }
    public string Outcome { get; init; } = AudiobookBookmarkOperationOutcomes.Success;
    public AudiobookBookmarkDraftPayloadDto? BookmarkDraft { get; init; }
    public AudiobookBookmarkDialogSnapshotDto? BookmarkSnapshot { get; init; }
    public AudiobookBookmarkDto? Bookmark { get; init; }
    public IReadOnlyList<AudiobookBookmarkDto>? Bookmarks { get; init; }
    public AudiobookSleepTimerStateDto? SleepTimerState { get; init; }
    public AudiobookSleepTimerAvailabilityDto? SleepTimerAvailability { get; init; }
    public bool? BooleanResult { get; init; }
    public string? Message { get; init; }
}

/// <summary>Authoritative bookmark panel state returned by its owner to another player host.</summary>
public sealed record AudiobookBookmarkDialogSnapshotDto
{
    public IReadOnlySet<Guid> AuthorizedAssetIds { get; init; } = new HashSet<Guid>();
    public IReadOnlyList<AudiobookBookmarkDto> Saved { get; init; } = [];
    public AudiobookBookmarkDraftPayloadDto? Draft { get; init; }
    public bool IsLoading { get; init; }
    public bool IsSaving { get; init; }
    public Guid? DeleteConfirmationBookmarkId { get; init; }
    public string? Message { get; init; }
    public bool SaveOutcomeUnknown { get; init; }
}
