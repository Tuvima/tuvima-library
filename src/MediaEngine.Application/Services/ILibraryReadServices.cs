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

    /// <summary>
    /// Which of the given ids belong to something in the library that is not a title a batch edit can change
    /// (an edition, a collection or a person), so a refusal can say so instead of "gone".
    /// </summary>
    Task<IReadOnlySet<Guid>> FindNonTitleIdsAsync(
        IReadOnlyCollection<Guid> entityIds,
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
