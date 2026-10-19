using MediaEngine.Contracts.Activity;
using MediaEngine.Contracts.Paging;

namespace MediaEngine.Application.Services;

/// <summary>Reads the Activity log: the Events part of System &gt; Logs, worded in plain language.</summary>
public interface IActivityLogReadService
{
    /// <summary>
    /// One page of events, newest first. <paramref name="category"/> is one of <c>added</c>, <c>review</c>, <c>failed</c>,
    /// <c>system</c> or <c>all</c>; empty or unknown means all. <paramref name="search"/> matches names and details.
    /// </summary>
    Task<PagedResponse<ActivityLogEntryDto>> GetLogAsync(
        string? category,
        string? search,
        int offset,
        int limit,
        CancellationToken ct = default);
}
