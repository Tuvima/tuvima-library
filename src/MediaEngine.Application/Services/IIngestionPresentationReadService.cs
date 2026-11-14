using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Paging;

namespace MediaEngine.Application.Services;

public interface IIngestionPresentationReadService
{
    /// <param name="includeUnmatchedFiles">
    /// True for a caller who may see the files that could not be matched (an administrator), so the one "need review" number
    /// equals what the Needs Review list holds for that caller.
    /// </param>
    Task<IngestionPresentationSnapshotDto> GetSnapshotAsync(
        int currentLimit = 12,
        int recentDayLimit = 3,
        int recentItemsPerDay = 9,
        bool includeUnmatchedFiles = false,
        CancellationToken ct = default);

    Task<PagedResponse<IngestionMediaGroupDto>> GetCurrentMediaAsync(
        int offset,
        int limit,
        CancellationToken ct = default);

    Task<PagedResponse<IngestionMediaGroupDto>> GetRecentAdditionsAsync(
        string? search,
        string? lane,
        DateTimeOffset? start,
        DateTimeOffset? end,
        int offset,
        int limit,
        string? sort = null,
        string? status = null,
        CancellationToken ct = default);

    Task<IngestionMediaGroupDto?> GetMediaGroupAsync(
        Guid groupId,
        CancellationToken ct = default);

    Task<PagedResponse<IngestionMediaChildDto>> GetChildrenAsync(
        Guid groupId,
        int offset,
        int limit,
        CancellationToken ct = default);

    Task<ActivityHistorySummaryDto> GetActivitySummaryAsync(CancellationToken ct = default);
}
