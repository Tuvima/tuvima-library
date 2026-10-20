using MediaEngine.Application.ReadModels;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Paging;

namespace MediaEngine.Application.Services;

public interface ILibraryCurationReadService
{
    /// <summary>
    /// Reads the selected items for a batch edit, keyed by the id that was selected. A selected id may be a work or a
    /// media asset; an id that is neither (deleted, never existed) has no entry.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, BatchEditItemRead>> LoadBatchEditItemsAsync(
        IReadOnlyCollection<Guid> entityIds,
        IReadOnlyCollection<string> fieldKeys,
        CancellationToken ct = default);
}

public interface ILibraryOverviewReadService
{
    Task<LibraryOverviewReadModel> GetOverviewAggregatesAsync(CancellationToken ct);
}

public interface ILibraryWorkFeedReadService
{
    Task<PagedResponse<LibraryWorkListItemDto>> GetWorksAsync(
        PagedRequest page,
        CancellationToken ct = default,
        IReadOnlySet<Guid>? allowedWorkIds = null);
}

public interface IWorkDetailReadService
{
    Task<WorkDetailDto?> GetAsync(Guid workId, CancellationToken ct = default);
}
