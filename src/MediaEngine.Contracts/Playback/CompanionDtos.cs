namespace MediaEngine.Contracts.Playback;

/// <summary>
/// Companion sessions let a second device (for example a phone) follow where one of the caller's own devices is
/// playing, or hold a position the person set by hand. Only the caller's own profile is ever visible.
/// </summary>
public static class CompanionModes
{
    public const string None = "none";
    public const string Follow = "follow";
    public const string Manual = "manual";
}

/// <summary>One device on the caller's profile that is playing or paused right now.</summary>
public sealed record CompanionLiveDeviceDto
{
    public string DeviceId { get; init; } = string.Empty;
    /// <summary>The paired device's name when known; clients show their own client label when null.</summary>
    public string? DeviceName { get; init; }
    public string Client { get; init; } = string.Empty;
    /// <summary>"playing" or "paused".</summary>
    public string PlaybackState { get; init; } = PlayerPlaybackStates.Playing;
    public double PositionSeconds { get; init; }
    public double? DurationSeconds { get; init; }
    public Guid? WorkId { get; init; }
    public Guid AssetId { get; init; }
    /// <summary>What the device is playing, when it can be resolved inside the caller's catalogue scope.</summary>
    public PlayerQueueItemDto? Item { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record CompanionDeviceListDto
{
    public Guid ProfileId { get; init; }
    public IReadOnlyList<CompanionLiveDeviceDto> Devices { get; init; } = [];
    public DateTimeOffset ObservedAt { get; init; }
}

/// <summary>A position set by hand, such as "episode 3" or "chapter 12".</summary>
public sealed record CompanionManualPositionDto
{
    public Guid WorkId { get; init; }
    public int? ChapterIndex { get; init; }
    public double? PositionSeconds { get; init; }
    public PlayerQueueItemDto? Item { get; init; }
}

public sealed record CompanionSessionDto
{
    public Guid ProfileId { get; init; }
    public string CompanionDeviceId { get; init; } = string.Empty;
    /// <summary>One of <see cref="CompanionModes"/>.</summary>
    public string Mode { get; init; } = CompanionModes.None;
    public string? FollowedDeviceId { get; init; }
    /// <summary>The followed device's latest state; null when that device is not live.</summary>
    public CompanionLiveDeviceDto? Followed { get; init; }
    public bool IsFollowedDeviceLive { get; init; }
    public CompanionManualPositionDto? Manual { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
}

public sealed record CompanionFollowRequestDto
{
    public Guid? ProfileId { get; init; }
    public string? CompanionDeviceId { get; init; }
    public string DeviceId { get; init; } = string.Empty;
}

public sealed record CompanionManualPositionRequestDto
{
    public Guid? ProfileId { get; init; }
    public string? CompanionDeviceId { get; init; }
    public Guid WorkId { get; init; }
    public int? ChapterIndex { get; init; }
    public double? PositionSeconds { get; init; }
}
