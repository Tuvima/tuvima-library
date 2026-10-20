using MediaEngine.Application.ReadModels;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Persons;

namespace MediaEngine.Application.Services;

public interface IJourneyReadService
{
    Task<IReadOnlyList<JourneyItemResponse>> GetJourneyAsync(
        Guid userId,
        Guid? collectionId,
        int limit,
        CancellationToken ct);
}

public interface IPersonAliasReadService
{
    Task<PersonAliasResponse?> GetAliasesAsync(Guid personId, CancellationToken ct);
}

public interface IPersonPresenceReadService
{
    Task<IReadOnlyDictionary<string, Dictionary<string, int>>> GetPresenceAsync(
        IReadOnlyList<Guid> personIds,
        CancellationToken ct);
}

public interface IPersonWorksReadService
{
    Task<IReadOnlySet<Guid>> GetCollectionIdsForPersonAsync(Guid personId, CancellationToken ct);
}

public interface IPersonAssetScopeReadService
{
    Task<IReadOnlyList<PersonSummaryResponse>> GetByCollectionAsync(Guid collectionId, CancellationToken ct);
    Task<IReadOnlyList<PersonSummaryResponse>> GetByWorkAsync(Guid workId, CancellationToken ct);
    Task<IReadOnlySet<Guid>> GetCanonicalPersonIdsAsync(
        IReadOnlyCollection<Guid> assetIds,
        CancellationToken ct);
    Task<IReadOnlySet<Guid>> GetCanonicalPersonIdsForCollectionAsync(
        Guid collectionId,
        IReadOnlyCollection<Guid> assetIds,
        CancellationToken ct);
    Task<IReadOnlyDictionary<string, int>> GetCanonicalRoleCountsAsync(
        IReadOnlyCollection<Guid> assetIds,
        CancellationToken ct);
}
