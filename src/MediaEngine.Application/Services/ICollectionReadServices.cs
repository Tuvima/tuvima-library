using MediaEngine.Application.ReadModels;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Search;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;

namespace MediaEngine.Application.Services;

public interface ICollectionBrowseReadService
{
    Task<List<CollectionDto>> GetAllAsync(CancellationToken ct);
    Task<Guid?> GetRootWorkIdAsync(Guid workId, CancellationToken ct);
    Task<Guid?> GetRepresentativeAssetIdAsync(Guid workId, CancellationToken ct);
    Task<Dictionary<Guid, Guid?>> GetPrimaryAssetIdsAsync(IEnumerable<Guid> workIds, CancellationToken ct);
    Task<CollectionPaletteReadModel?> GetAssetPaletteAsync(Guid entityId, CancellationToken ct);
    Task<IReadOnlyList<CollectionArtistWorkReadModel>> GetArtistWorksAsync(string artistName, CancellationToken ct);
    Task<IReadOnlyList<CollectionSystemViewDetailWorkReadModel>> GetSystemViewDetailWorksAsync(
        string groupField,
        string groupValue,
        string? mediaType,
        string? artistName,
        CancellationToken ct, Guid? rootWorkId = null);
    IReadOnlyList<Guid> EvaluateRules(
        CollectionRuleDefinition definition,
        string? sortField = null,
        string sortDirection = "desc",
        int limit = 0,
        string? query = null,
        string? secondarySortField = null,
        string? secondarySortDirection = null);
    int CountRuleMatches(CollectionRuleDefinition definition, string? query = null);
    Task<IReadOnlyList<string>> GetFieldValuesAsync(string field, string? query, int limit, CancellationToken ct);
    Task<IReadOnlyList<CollectionRuleValueDto>> GetEntityFieldValuesAsync(string field, string? query, int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CollectionRuleValueDto>>([]);
    Task<List<ContentGroupDto>> GetSystemViewGroupsAsync(
        string? mediaType,
        string? groupField,
        CancellationToken ct,
        IReadOnlySet<Guid>? allowedWorkIds = null);

    /// <summary>
    /// Finds the Music album group that contains the album root <paramref name="rootWorkId"/>.
    /// Album groups merge roots that share a name (one album split across roots), so a root that is not
    /// the group's representative id still resolves to its group. Returns <c>null</c> when no album group holds it.
    /// </summary>
    async Task<ContentGroupDto?> GetMusicAlbumGroupForRootAsync(Guid rootWorkId, CancellationToken ct)
    {
        var groups = await GetSystemViewGroupsAsync("Music", "album", ct).ConfigureAwait(false);
        return groups.FirstOrDefault(group => group.RootWorkId == rootWorkId);
    }
}

public interface ICollectionMediaLookupReadService
{
    Task<List<CollectionMediaLookupDto>> LookupAsync(
        string? query,
        string? mediaTypes,
        IReadOnlySet<Guid> existingWorkIds,
        IReadOnlySet<Guid> allowedWorkIds,
        int? offset,
        int? limit,
        CancellationToken ct);

    Task<List<CollectionItemDto>> ResolveItemsAsync(
        Guid collectionId,
        IReadOnlyList<CollectionItem> items,
        CancellationToken ct);

    Task<List<CollectionResolvedItemDto>> ResolveMetadataAsync(
        IReadOnlyList<Guid> workIds,
        CancellationToken ct);

    Task<Dictionary<string, int>> CountMediaTypesAsync(
        IReadOnlyList<Guid> workIds,
        CancellationToken ct);
}

public interface ICollectionSearchReadService
{
    Task<List<SearchResultDto>> SearchAsync(string? query, CancellationToken ct);
}
