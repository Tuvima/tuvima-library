using MediaEngine.Domain.Authorization;

namespace MediaEngine.Domain.Playback;

public static class PlaybackTelemetryStates
{
    public const string Playing = "playing";
    public const string Paused = "paused";
    public const string Stopped = "stopped";
    public const string Completed = "completed";
}

public static class PlaybackCompletionReasons
{
    public const string Stopped = "stopped";
    public const string Completed = "completed";
    public const string Replaced = "replaced";
    public const string Takeover = "takeover";
    public const string Stale = "stale";
    public const string Error = "error";
}

public static class PlaybackTelemetryDeliveryModes
{
    public const string DirectPlay = "direct-play";
    public const string Remux = "remux";
    public const string Transcode = "transcode";
    public const string Reader = "reader";
    public const string Offline = "offline";
    public const string Unknown = "unknown";
}

public static class PlaybackConnectionTypes
{
    public const string Local = "local";
    public const string Remote = "remote";
    public const string Unknown = "unknown";
}

public sealed record PlaybackTelemetryObservation(
    Guid PlayerSessionId,
    Guid AccountId,
    Guid ProfileId,
    Guid? ApplicationId,
    Guid? DeviceId,
    Guid? AuthoritySessionId,
    Guid AssetId,
    Guid LibraryId,
    AccountFeatureId Feature,
    string MediaType,
    string State,
    DateTimeOffset ObservedAt,
    double PositionSeconds,
    double? DurationSeconds,
    long? Sequence,
    double PlaybackRate,
    bool IsExplicitSeek,
    bool HasPlaybackEnded,
    string? CompletionReason,
    PlaybackDeliveryFacts Delivery,
    PlaybackClientFacts Client);

public sealed record PlaybackDeliveryFacts(
    string? Mode = null,
    string? Container = null,
    string? VideoCodec = null,
    string? AudioCodec = null,
    int? Width = null,
    int? Height = null,
    int? BitrateKbps = null,
    string? ConnectionType = null);

public sealed record PlaybackClientFacts(string? Name = null, string? Version = null);

public sealed record PlaybackTelemetrySession
{
    public required Guid Id { get; init; }
    public required Guid PlayerSessionId { get; init; }
    public Guid? AuthoritySessionId { get; init; }
    public required Guid AccountId { get; init; }
    public required Guid ProfileId { get; init; }
    public Guid? ApplicationId { get; init; }
    public Guid? DeviceId { get; init; }
    public Guid? AssetId { get; init; }
    public required Guid LibraryId { get; init; }
    public required AccountFeatureId Feature { get; init; }
    public required string MediaType { get; init; }
    public required string State { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset LastObservedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public double StartedPositionSeconds { get; init; }
    public double LastPositionSeconds { get; init; }
    public double? DurationSeconds { get; init; }
    public double PlayedDurationSeconds { get; init; }
    public long LastSequence { get; init; } = -1;
    public string? CompletionReason { get; init; }
    public PlaybackDeliveryFacts Delivery { get; init; } = new();
    public PlaybackClientFacts Client { get; init; } = new();
}

public sealed record PlaybackTelemetryTransition(
    PlaybackTelemetrySession Session,
    string? PreviousState,
    bool WasCreated,
    bool WasIgnored)
{
    public bool StateChanged => !WasIgnored &&
        (WasCreated || !string.Equals(PreviousState, Session.State, StringComparison.Ordinal));
}

public sealed record PlaybackTelemetryReadScope(
    bool IsAllowed,
    bool AllAccounts,
    Guid? AccountId,
    Guid? ProfileId,
    bool AllLibraries,
    IReadOnlySet<Guid> HiddenLibraryIds,
    IReadOnlySet<AccountFeatureId> Features)
{
    public static PlaybackTelemetryReadScope Denied { get; } = new(
        false, false, null, null, false, new HashSet<Guid>(), new HashSet<AccountFeatureId>());
}

public sealed record PlaybackTelemetryPage(
    IReadOnlyList<PlaybackTelemetrySession> Items,
    string? NextCursor);

public sealed record PlaybackTelemetryAggregate(
    long SessionCount,
    long CompletedCount,
    double PlayedDurationSeconds,
    long UniqueAccounts,
    long UniqueProfiles,
    long UniqueAssets,
    long DirectPlayCount,
    long RemuxCount,
    long TranscodeCount);

public sealed record PlaybackTelemetryGroupAggregate(
    Guid? AccountId,
    Guid? ProfileId,
    Guid? LibraryId,
    Guid? DeviceId,
    long SessionCount,
    long CompletedCount,
    double PlayedDurationSeconds,
    DateTimeOffset LastPlayedAt);
