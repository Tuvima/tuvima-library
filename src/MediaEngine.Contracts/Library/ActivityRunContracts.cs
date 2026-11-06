using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Library;

/// <summary>The values of <see cref="ActivityRunDto.Kind"/>: what sort of batch a History entry is (spec library-activity D16).</summary>
public static class ActivityRunKindNames
{
    public const string Added = "added";
    public const string Updated = "updated";
    public const string Fixed = "fixed";
    public const string Removed = "removed";
}

/// <summary>The values of <see cref="ActivityRunDto.Status"/>.</summary>
public static class ActivityRunStatusNames
{
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

/// <summary>
/// The History list of the Activity page (<c>GET /library/activity/runs</c>, administrators only): one entry per batch, newest or oldest
/// first, plus the last completed scan for the "Everything is up to date" card. A Vault library is never recorded.
/// </summary>
public sealed record ActivityRunsDto(
    [property: JsonPropertyName("runs")] IReadOnlyList<ActivityRunDto> Runs,
    [property: JsonPropertyName("has_more")] bool HasMore,
    [property: JsonPropertyName("last_completed_at")] DateTimeOffset? LastCompletedAt);

/// <summary>One History entry: what was added, updated, fixed or removed, where, when, and up to five small covers.</summary>
public sealed record ActivityRunDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("library_id")] string LibraryId,
    [property: JsonPropertyName("library_name")] string LibraryName,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("ended_at")] DateTimeOffset? EndedAt,
    [property: JsonPropertyName("item_count")] int ItemCount,
    [property: JsonPropertyName("counts")] IReadOnlyList<ActivityRunCountDto> Counts,
    [property: JsonPropertyName("covers")] IReadOnlyList<ActivityCoverDto> Covers);

/// <summary>How many items of one media type a run touched ("TV": 85 becomes "85 episodes" on screen).</summary>
public sealed record ActivityRunCountDto(
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("count")] int Count);

/// <summary>One small cover: the cached small art address and the media type that sets its shape (square for albums, portrait for posters).</summary>
public sealed record ActivityCoverDto(
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("title")] string Title);

/// <summary>
/// One opened batch (<c>GET /library/activity/runs/{id}/groups</c>): its items stacked into tiles (D12), a page at a time, so a hundred
/// added files read as a few dozen tiles.
/// </summary>
public sealed record ActivityRunGroupsDto(
    [property: JsonPropertyName("entry_id")] Guid EntryId,
    [property: JsonPropertyName("groups")] IReadOnlyList<ActivityGroupDto> Groups,
    [property: JsonPropertyName("has_more")] bool HasMore);

/// <summary>One stacked tile: a show, an album, a folder of videos, or a single title. <c>route</c> is the item's own page when it has one.</summary>
public sealed record ActivityGroupDto(
    [property: JsonPropertyName("group_key")] string GroupKey,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("item_count")] int ItemCount,
    [property: JsonPropertyName("cover_url")] string? CoverUrl,
    [property: JsonPropertyName("route")] string? Route);

/// <summary>
/// The instant title search (<c>GET /library/activity/titles?q=</c>, D18): a title found in several batches appears once, with the total
/// added and one marker per batch for its own vertical timeline.
/// </summary>
public sealed record ActivityTitleSearchDto(
    [property: JsonPropertyName("titles")] IReadOnlyList<ActivityTitleCardDto> Titles);

/// <summary>One title with every batch it was added in.</summary>
public sealed record ActivityTitleCardDto(
    [property: JsonPropertyName("group_key")] string GroupKey,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("cover_url")] string? CoverUrl,
    [property: JsonPropertyName("route")] string? Route,
    [property: JsonPropertyName("total_count")] int TotalCount,
    [property: JsonPropertyName("markers")] IReadOnlyList<ActivityTitleMarkerDto> Markers);

/// <summary>One dated marker on a searched title's own timeline: the batch, when it started and how much of the title it added.</summary>
public sealed record ActivityTitleMarkerDto(
    [property: JsonPropertyName("entry_id")] Guid EntryId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("item_count")] int ItemCount);

/// <summary>The administrator's trim action (D20): runs that started before this moment are deleted.</summary>
public sealed record ActivityPurgeRequest(
    [property: JsonPropertyName("older_than")] DateTimeOffset OlderThan);

/// <summary>How many runs the trim removed.</summary>
public sealed record ActivityPurgeResultDto(
    [property: JsonPropertyName("removed")] int Removed);
