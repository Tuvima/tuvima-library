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
    /// <summary>How many pending, review-ready items each library holds (a library with none is absent), for the Libraries overview.</summary>
    Task<IReadOnlyDictionary<string, int>> GetPendingCountsByLibraryAsync(CancellationToken ct = default);
    Task<ReviewItemDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<int> GetPendingCountAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ReviewReasonCount>> GetPendingReasonCountsAsync(CancellationToken ct = default);
    /// <summary>The items a person kept with Keep as it is (Needs Review's hidden items), newest first; one library's when <paramref name="libraryId"/> is given.</summary>
    Task<IReadOnlyList<ReviewItemDto>> GetKeptAsync(int limit, string? libraryId, CancellationToken ct = default);
    /// <summary>How many items <see cref="GetKeptAsync"/> would return, without reading them.</summary>
    Task<int> GetKeptCountAsync(string? libraryId, CancellationToken ct = default);
}

/// <summary>
/// What a Needs Review row needs to know about the Work behind it, read for the whole list in a few batched queries: the family it sits in
/// (the top parent Work: show, album or Series) and the pictures the Engine can really serve.
/// </summary>
public interface INeedsReviewRowContextReadService
{
    /// <summary>Reads the context of the given files (media assets) and Works. Ids that are unknown are simply absent from the answer.</summary>
    Task<NeedsReviewRowContexts> ReadAsync(IReadOnlyCollection<Guid> assetIds, IReadOnlyCollection<Guid> workIds, CancellationToken ct = default);
}

public interface INeedsReviewListReadService
{
    /// <summary>
    /// One page of the Needs Review list: review items and unmatched files merged, newest first. <paramref name="kind"/> is
    /// <c>null</c>/<c>all</c>, <c>not_matched</c> or <c>not_sure</c>; <paramref name="libraryId"/> narrows to one library.
    /// <paramref name="includeFiles"/> is false for a caller who may not see the unmatched files (they are an administrator's list).
    /// Wikidata-only items (the provider match stands, only its Wikidata link is missing) are never listed. With
    /// <paramref name="keptOnly"/> the page holds only the items a person kept (Keep as it is), in the same row shape.
    /// </summary>
    Task<NeedsReviewListDto> ListAsync(string? kind, string? libraryId, bool includeFiles, int skip, int take, bool keptOnly = false, CancellationToken ct = default);

    /// <summary>What the Settings notifier shows: every row the list holds, split by kind.</summary>
    Task<ReviewCountResponse> CountAsync(bool includeFiles, CancellationToken ct = default);
}

public interface IUniversalSearchReadService
{
    Task<UniversalSearchResponseDto> SearchAsync(string? query, int limit, CancellationToken ct);
}
