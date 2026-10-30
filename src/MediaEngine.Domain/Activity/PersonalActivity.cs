namespace MediaEngine.Domain.Activity;

/// <summary>The kinds of personal activity a person's history keeps.</summary>
public static class PersonalActivityKinds
{
    public const string Watch = "watch";
    public const string Listen = "listen";
    public const string Read = "read";
    public const string MyList = "my_list";

    /// <summary>The item kind a collapsed run of songs is shown as; it is never stored on a row.</summary>
    public const string MusicSession = "music_session";

    public static bool IsKnown(string? kind) => kind is Watch or Listen or Read or MyList;
}

/// <summary>What the item was when the activity happened. Kept on the row so history survives deleting the item.</summary>
public sealed record PersonalActivitySnapshot(
    string MediaType,
    string Title,
    string? Subtitle,
    string? ParentTitle,
    int? Year,
    Guid? WorkId,
    Guid? AssetId,
    string? ArtworkKey);

/// <summary>One stored activity row.</summary>
public sealed record PersonalActivityEntry
{
    public required Guid Id { get; init; }
    public required Guid ProfileId { get; init; }

    /// <summary>The person's name; filled by reads that cross people, otherwise null.</summary>
    public string? PersonName { get; init; }

    public required string Kind { get; init; }
    public Guid? TelemetrySessionId { get; init; }
    public Guid? MusicSessionId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public long PlayedSeconds { get; init; }
    public double? ProgressPct { get; init; }
    public bool Finished { get; init; }
    public string? DeviceName { get; init; }
    public string? DeviceKind { get; init; }
    public Guid? WorkId { get; init; }
    public Guid? AssetId { get; init; }
    public required string MediaType { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string? ParentTitle { get; init; }
    public int? Year { get; init; }
    public string? ArtworkKey { get; init; }
}

/// <summary>The device a playback session ran on, as shown in history.</summary>
public sealed record PersonalActivityDevice(string? Name, string? Kind);

/// <summary>
/// How far a playback session has got, taken from the live session. Written on every observation and on close.
/// </summary>
public sealed record PersonalActivityProgress(
    DateTimeOffset LastSeenAt,
    long PlayedSeconds,
    double? ProgressPct,
    bool Finished);

/// <summary>
/// Which rows a read covers. <see cref="ProfileIds"/> is null for everyone; the Engine's scope decision has already
/// been made by the caller.
/// </summary>
public sealed record PersonalActivityFilter
{
    public IReadOnlySet<Guid>? ProfileIds { get; init; }

    /// <summary>Text to find in the title, subtitle, album and device (and in the person's name when <see cref="SearchPeople"/> is set).</summary>
    public string? Query { get; init; }

    public bool SearchPeople { get; init; }
    public IReadOnlyCollection<string>? Kinds { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }

    /// <summary>True for finished entries only, false for entries that were stopped, null for both.</summary>
    public bool? Finished { get; init; }

    public string? Device { get; init; }
}

/// <summary>How a page of history is grouped into items.</summary>
public enum PersonalActivityGrouping
{
    /// <summary>One item per entry; the songs of a music session collapse into one item.</summary>
    None,

    /// <summary>One item per title, holding its entries; the songs of a music session still collapse into one item.</summary>
    Title,
}

/// <summary>One item of a page: a single entry, a title with its entries, or a music session with its songs.</summary>
public sealed record PersonalActivityItem(
    string Key,
    bool IsMusicSession,
    bool IsTitleGroup,
    IReadOnlyList<PersonalActivityEntry> Entries);

public sealed record PersonalActivityItemPage(IReadOnlyList<PersonalActivityItem> Items, string? NextCursor);

/// <summary>How many items fall in one month, for the date rail.</summary>
public sealed record PersonalActivityPeriodCount(int Year, int Month, int Count);

/// <summary>The small stored copy of a cover.</summary>
public sealed record PersonalActivityArtwork(string Key, byte[] Image, string ContentType, int? Width, int? Height);

/// <summary>
/// One person's lifetime totals. Time played is split by what was played; titles are counted once whatever the number
/// of attempts, songs are not counted as titles, and adding to My list is not an attempt.
/// </summary>
public sealed record PersonalActivitySummary(
    long WatchSeconds,
    long ReadSeconds,
    long MusicSeconds,
    long AudiobookSeconds,
    int FinishedCount,
    int InProgressCount,
    DateTimeOffset? FirstActivityAt);


/// <summary>One day (UTC) of a person's activity: time played and how many entries started.</summary>
public sealed record PersonalActivityDay(DateOnly Date, long PlayedSeconds, int Entries);

/// <summary>
/// The rolling window behind the Overview: titles finished and days with any activity in the last <c>days</c> days
/// ending today (UTC), and each active day. Days without activity are left out.
/// </summary>
public sealed record PersonalActivityRolling(int FinishedTitles, int DaysActive, IReadOnlyList<PersonalActivityDay> Days);

/// <summary>Titles first finished in one month (UTC, <c>yyyy-MM</c>) under one genre; a title without a known genre has a null genre.</summary>
public sealed record PersonalFinishedMonthGenre(string Month, string? Genre, int Titles);
