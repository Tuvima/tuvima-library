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
    Task<PersonAliasResponse?> GetAliasesAsync(Guid personId, ViewerCatalogue viewer, CancellationToken ct);
}

public interface IPersonPresenceReadService
{
    /// <summary>
    /// Media type counts per person, counting only the titles <paramref name="viewer"/> may see. A title in the Vault, in a library
    /// hidden from the viewer or above the viewer's content limit is not counted (Vault sections spec D27, D32).
    /// </summary>
    Task<IReadOnlyDictionary<string, Dictionary<string, int>>> GetPresenceAsync(
        IReadOnlyList<Guid> personIds,
        ViewerCatalogue viewer,
        CancellationToken ct);
}

public interface IPersonWorksReadService
{
    Task<IReadOnlySet<Guid>> GetCollectionIdsForPersonAsync(Guid personId, CancellationToken ct);
}

public interface IPersonAssetScopeReadService
{
    /// <summary>The people credited on the viewer's visible titles in a collection.</summary>
    Task<IReadOnlyList<PersonSummaryResponse>> GetByCollectionAsync(Guid collectionId, ViewerCatalogue viewer, CancellationToken ct);

    /// <summary>The people credited on one work, when the viewer may see it.</summary>
    Task<IReadOnlyList<PersonSummaryResponse>> GetByWorkAsync(Guid workId, ViewerCatalogue viewer, CancellationToken ct);

    /// <summary>Every person credited on a title the viewer may see.</summary>
    Task<IReadOnlySet<Guid>> GetCanonicalPersonIdsAsync(ViewerCatalogue viewer, CancellationToken ct);

    /// <summary>How many people hold each role across the titles the viewer may see.</summary>
    Task<IReadOnlyDictionary<string, int>> GetCanonicalRoleCountsAsync(ViewerCatalogue viewer, CancellationToken ct);
}
