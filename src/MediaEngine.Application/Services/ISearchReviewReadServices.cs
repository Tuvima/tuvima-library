using MediaEngine.Application.ReadModels;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Review;

namespace MediaEngine.Application.Services;

public interface IOrphanImageReferenceReadService
{
    Task<OrphanImageReferenceSet> GetKnownReferencesAsync(CancellationToken ct);
}

public interface IReviewQueueReadService
{
    Task<IReadOnlyList<ReviewItemDto>> GetPendingAsync(int limit, CancellationToken ct = default);
    /// <summary>The pending items whose file belongs to one library (Library page's Needs review link).</summary>
    Task<IReadOnlyList<ReviewItemDto>> GetPendingForLibraryAsync(int limit, string libraryId, CancellationToken ct = default);
    Task<ReviewItemDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<int> GetPendingCountAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ReviewReasonCount>> GetPendingReasonCountsAsync(CancellationToken ct = default);
}

public interface IUniversalSearchReadService
{
    Task<UniversalSearchResponseDto> SearchAsync(string? query, int limit, CancellationToken ct);
}
