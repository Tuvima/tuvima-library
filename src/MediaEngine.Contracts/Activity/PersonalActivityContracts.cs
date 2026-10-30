using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Activity;

/// <summary>The values of the <c>kind</c> field and filter on personal activity.</summary>
public static class PersonalActivityKindValues
{
    public const string Watch = "watch";
    public const string Listen = "listen";
    public const string Read = "read";
    public const string MyList = "my_list";

    /// <summary>A run of songs collapsed into one item. Only appears on items, never as a filter.</summary>
    public const string MusicSession = "music_session";
}

/// <summary>The natural shape of a Recent activity tile: portrait for books and comics, landscape for episodes and movies, square for music and audiobooks.</summary>
public static class PersonalActivityShapeValues
{
    public const string Portrait = "portrait";
    public const string Landscape = "landscape";
    public const string Square = "square";
}

/// <summary>The person an entry belongs to. Only present when the scope covers other people.</summary>
public sealed record PersonalActivityPersonDto(
    [property: JsonPropertyName("profile_id")] string ProfileId,
    [property: JsonPropertyName("name")] string Name);

/// <summary>
/// One item of the Activity list: a single entry (a watch, listen, read or My list add), a title with its attempts
/// (<c>group=title</c>), or a music session. Attempts and songs reuse this shape without further nesting. No IP
/// address or sign-in detail is ever carried.
/// </summary>
public sealed class PersonalActivityItemDto
{
    /// <summary>The entry id, or a stable key for a music session or title group.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary><c>watch</c>, <c>listen</c>, <c>read</c>, <c>my_list</c> or <c>music_session</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>UTC. For a group or session, the latest start.</summary>
    [JsonPropertyName("started_at")]
    public string StartedAt { get; set; } = string.Empty;

    /// <summary>UTC. The end of the entry, or the last time it was seen while it is still open. For a session, when the last song ended.</summary>
    [JsonPropertyName("ended_at")]
    public string? EndedAt { get; set; }

    /// <summary>Seconds actually played or read. For a group or session, the total.</summary>
    [JsonPropertyName("played_seconds")]
    public long PlayedSeconds { get; set; }

    /// <summary>The furthest point reached, 0 to 100; null where progress is not tracked (music, My list).</summary>
    [JsonPropertyName("progress_pct")]
    public double? ProgressPct { get; set; }

    [JsonPropertyName("finished")]
    public bool Finished { get; set; }

    [JsonPropertyName("device_name")]
    public string? DeviceName { get; set; }

    [JsonPropertyName("device_kind")]
    public string? DeviceKind { get; set; }

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>The episode label (season and episode, then the name), the author or the artist.</summary>
    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    /// <summary>The album for a song.</summary>
    [JsonPropertyName("parent_title")]
    public string? ParentTitle { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    /// <summary>A path on this Engine (<c>/api/v1/activity/artwork/{key}</c>) to the small picture kept with the entry; null when there is none.</summary>
    [JsonPropertyName("artwork_url")]
    public string? ArtworkUrl { get; set; }

    /// <summary>The work, while it still exists; may point at nothing once the item was deleted.</summary>
    [JsonPropertyName("work_id")]
    public string? WorkId { get; set; }

    /// <summary>Only when the requested scope includes other people.</summary>
    [JsonPropertyName("person")]
    public PersonalActivityPersonDto? Person { get; set; }

    /// <summary>For a title group: how many entries it holds. Null otherwise.</summary>
    [JsonPropertyName("attempt_count")]
    public int? AttemptCount { get; set; }

    /// <summary>For a title group: its entries, oldest first. Null otherwise.</summary>
    [JsonPropertyName("attempts")]
    public IReadOnlyList<PersonalActivityItemDto>? Attempts { get; set; }

    /// <summary>For a music session: the number of songs. Null otherwise.</summary>
    [JsonPropertyName("song_count")]
    public int? SongCount { get; set; }

    /// <summary>For a music session: the number of distinct albums. Null otherwise.</summary>
    [JsonPropertyName("album_count")]
    public int? AlbumCount { get; set; }

    /// <summary>For a music session: up to nine distinct album covers, chosen at random on each request. Null otherwise.</summary>
    [JsonPropertyName("cover_urls")]
    public IReadOnlyList<string>? CoverUrls { get; set; }

    /// <summary>For a music session, only when <c>expand=songs</c> was asked for: the songs in the order they played.</summary>
    [JsonPropertyName("songs")]
    public IReadOnlyList<PersonalActivityItemDto>? Songs { get; set; }
}

/// <summary>A page of the Activity list, newest first.</summary>
public sealed record PersonalActivityPageDto(
    [property: JsonPropertyName("items")] IReadOnlyList<PersonalActivityItemDto> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("includes_others")] bool IncludesOthers);

/// <summary>How many items fall in one month, for the date rail.</summary>
public sealed record PersonalActivityMonthCountDto(
    [property: JsonPropertyName("month")] int Month,
    [property: JsonPropertyName("count")] int Count);

/// <summary>How many items fall in one year, with its months. Newest year first.</summary>
public sealed record PersonalActivityYearCountDto(
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("months")] IReadOnlyList<PersonalActivityMonthCountDto> Months);

/// <summary>Counts per year and month (UTC) under the same scope and filters as the list.</summary>
public sealed record PersonalActivityPeriodsDto(
    [property: JsonPropertyName("years")] IReadOnlyList<PersonalActivityYearCountDto> Years);

/// <summary>One tile of the Overview's Recent activity strip.</summary>
public sealed record PersonalActivityRecentTileDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("subtitle")] string? Subtitle,
    [property: JsonPropertyName("parent_title")] string? ParentTitle,
    [property: JsonPropertyName("started_at")] string StartedAt,
    [property: JsonPropertyName("ended_at")] string? EndedAt,
    [property: JsonPropertyName("played_seconds")] long PlayedSeconds,
    [property: JsonPropertyName("progress_pct")] double? ProgressPct,
    [property: JsonPropertyName("finished")] bool Finished,
    [property: JsonPropertyName("shape")] string Shape,
    [property: JsonPropertyName("artwork_url")] string? ArtworkUrl,
    [property: JsonPropertyName("cover_urls")] IReadOnlyList<string>? CoverUrls,
    [property: JsonPropertyName("song_count")] int? SongCount,
    [property: JsonPropertyName("album_count")] int? AlbumCount,
    [property: JsonPropertyName("work_id")] string? WorkId);

/// <summary>The caller's latest activity, music collapsed into sessions.</summary>
public sealed record PersonalActivityRecentDto(
    [property: JsonPropertyName("tiles")] IReadOnlyList<PersonalActivityRecentTileDto> Tiles);

/// <summary>
/// One person's lifetime totals. Titles are counted once whatever the number of attempts; songs and My list adds are
/// not titles.
/// </summary>
public sealed record PersonalActivitySummaryDto(
    [property: JsonPropertyName("watch_seconds")] long WatchSeconds,
    [property: JsonPropertyName("read_seconds")] long ReadSeconds,
    [property: JsonPropertyName("listen_seconds")] long ListenSeconds,
    [property: JsonPropertyName("music_seconds")] long MusicSeconds,
    [property: JsonPropertyName("audiobook_seconds")] long AudiobookSeconds,
    [property: JsonPropertyName("finished_count")] int FinishedCount,
    [property: JsonPropertyName("in_progress_count")] int InProgressCount,
    [property: JsonPropertyName("first_activity_at")] string? FirstActivityAt,
    [property: JsonPropertyName("finished_last_30_days")] int FinishedLast30Days = 0,
    [property: JsonPropertyName("days_active_last_30")] int DaysActiveLast30 = 0,
    [property: JsonPropertyName("daily")] IReadOnlyList<PersonalActivityDayDto>? Daily = null);

/// <summary>One day (UTC, <c>yyyy-MM-dd</c>) with activity in the last 30 days: time played and how many entries started. Days without activity are left out.</summary>
public sealed record PersonalActivityDayDto(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("played_seconds")] long PlayedSeconds,
    [property: JsonPropertyName("entries")] int Entries);

/// <summary>What deleting history would remove.</summary>
public sealed record PersonalActivityDeletePreviewDto(
    [property: JsonPropertyName("count")] int Count);

/// <summary>What deleting history removed.</summary>
public sealed record PersonalActivityDeleteResultDto(
    [property: JsonPropertyName("deleted")] int Deleted);
