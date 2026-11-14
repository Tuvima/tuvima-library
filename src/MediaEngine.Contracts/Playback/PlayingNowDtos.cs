namespace MediaEngine.Contracts.Playback;

/// <summary>What one row of Playing now is, for the Dashboard's wording (playing-now spec section 3, D9-D10).</summary>
public static class PlayingNowKinds
{
    /// <summary>Someone is watching and nothing is being prepared for them.</summary>
    public const string Watching = "watching";

    /// <summary>A video is being prepared for a player that is playing or about to play it.</summary>
    public const string PlaybackEncode = "playback_encode";

    /// <summary>A copy is being prepared ahead of time for a phone or download.</summary>
    public const string OfflineEncode = "offline_encode";
}

/// <summary>The plain label for what a row is doing (the four steps of the playback path, plus Preparing).</summary>
public static class PlayingNowSteps
{
    public const string PlayingDirectly = "playing_directly";
    public const string Repackaging = "repackaging";
    public const string ConvertingAudio = "converting_audio";
    public const string EncodingPicture = "encoding_picture";

    /// <summary>Nothing has started yet; <see cref="PlayingNowRowDto.PreparingReason"/> says why.</summary>
    public const string Preparing = "preparing";
}

/// <summary>How heavy an encode is on the machine: a word, never a raw number (D9).</summary>
public static class PlayingNowHeaviness
{
    public const string Light = "light";
    public const string Medium = "medium";
    public const string Heavy = "heavy";
}

/// <summary>One thing that is being watched or prepared right now, live Engine state (never stored).</summary>
public sealed record PlayingNowRowDto
{
    /// <summary>The id to stop. Empty for a row that has nothing to stop.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>One of <see cref="PlayingNowKinds"/>.</summary>
    public string Kind { get; init; } = PlayingNowKinds.Watching;

    /// <summary>The title ("Dune: Part One"), or "Vault title" for a title from a Vault library.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>The show and episode line ("Severance · S2E4") for an episode, else null.</summary>
    public string? Subtitle { get; init; }

    /// <summary>The cover's address, or null (always null for a Vault title).</summary>
    public string? CoverUrl { get; init; }

    public string? AccountId { get; init; }
    public string? ProfileId { get; init; }
    public string? DeviceId { get; init; }

    /// <summary>Who is watching ("Dana"), or who requested an offline copy; null when unknown.</summary>
    public string? PersonName { get; init; }

    public string? ProfileName { get; init; }

    /// <summary>True for a Kids profile, so the row can say so.</summary>
    public bool IsKidsProfile { get; init; }

    /// <summary>The device ("Living room TV"), or null.</summary>
    public string? DeviceName { get; init; }

    /// <summary>One of <see cref="PlayingNowSteps"/>.</summary>
    public string Step { get; init; } = PlayingNowSteps.PlayingDirectly;

    /// <summary>For <see cref="PlayingNowSteps.Preparing"/>: "Waiting for a free slot" or "Reading the file".</summary>
    public string? PreparingReason { get; init; }

    /// <summary>True while the encode is waiting behind the Streaming limit.</summary>
    public bool WaitingForSlot { get; init; }

    /// <summary>How much of the video is prepared (0 to 100), or null when it is not known.</summary>
    public int? PercentPrepared { get; init; }

    /// <summary>Where the viewer is, in seconds, for a row someone is watching; null otherwise.</summary>
    public double? PositionSeconds { get; init; }

    public double? DurationSeconds { get; init; }

    /// <summary>One of <see cref="PlayingNowHeaviness"/>, or null when there is nothing to measure (watching only, or waiting).</summary>
    public string? Heaviness { get; init; }

    /// <summary>"Processor" or "Graphics card", or null.</summary>
    public string? RanOn { get; init; }

    /// <summary>True while a player is playing, paused within the allowed time, or has just read the stream.</summary>
    public bool IsWatched { get; init; }

    /// <summary>Whole seconds until an unwatched encode stops by itself, or null when someone is watching.</summary>
    public int? StopsInSeconds { get; init; }

    /// <summary>The offline copy's quality ("Small"), or null.</summary>
    public string? OfflineQuality { get; init; }

    /// <summary>True when the row has an encode that can be stopped.</summary>
    public bool CanStop { get; init; }

    public DateTimeOffset StartedAt { get; init; }
}

/// <summary>Everything Playing now shows, newest first. Administrators only.</summary>
public sealed record PlayingNowDto
{
    public IReadOnlyList<PlayingNowRowDto> Rows { get; init; } = [];

    /// <summary>The Streaming limit: how many encodes may run at once.</summary>
    public int MaxActiveEncodes { get; init; } = 1;

    /// <summary>How many encodes are running right now (not waiting).</summary>
    public int ActiveEncodes { get; init; }

    /// <summary>How many sessions are being watched, for the Right now card ("3 watching").</summary>
    public int WatchingCount { get; init; }

    /// <summary>How many encodes are running or waiting, for the Right now card ("1 preparing").</summary>
    public int PreparingCount { get; init; }

    /// <summary>The heaviest word across running encodes, or null when none runs.</summary>
    public string? HeaviestLoad { get; init; }

    /// <summary>"Processor" or "Graphics card" for the heaviest running encode, or null.</summary>
    public string? HeaviestRanOn { get; init; }
}

/// <summary>The answer to Stop all unwatched.</summary>
public sealed record StopUnwatchedResultDto
{
    public int Stopped { get; init; }
}
