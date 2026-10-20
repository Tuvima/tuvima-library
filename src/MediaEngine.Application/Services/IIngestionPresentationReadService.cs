using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Paging;

namespace MediaEngine.Application.Services;

public interface IIngestionPresentationReadService
{
    Task<IngestionPresentationSnapshotDto> GetSnapshotAsync(
        int currentLimit = 12,
        int recentDayLimit = 3,
        int recentItemsPerDay = 9,
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
