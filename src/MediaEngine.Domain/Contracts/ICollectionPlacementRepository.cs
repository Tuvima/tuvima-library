using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Where collections appear. A placement with no profile is the shared "Show on" choice; a placement with a profile is
/// that profile's own choice about the same collection and location (an invisible one is "Hide this lane"). The reads
/// below that do not mention a profile return shared placements only.
/// </summary>
public interface ICollectionPlacementRepository
{
    Task<IReadOnlyList<CollectionPlacement>> GetByCollectionIdAsync(Guid collectionId, CancellationToken ct = default);

    /// <summary>The visible shared placements at a location, in order.</summary>
    Task<IReadOnlyList<CollectionPlacement>> GetByLocationAsync(string location, CancellationToken ct = default);

    /// <summary>Every shared placement at a location, hidden ones included, in order. The administrator's lane list.</summary>
    Task<IReadOnlyList<CollectionPlacement>> GetSharedByLocationAsync(string location, CancellationToken ct = default);

    Task UpsertAsync(CollectionPlacement placement, CancellationToken ct = default);
    Task DeleteAsync(Guid placementId, CancellationToken ct = default);
    Task DeleteByCollectionIdAsync(Guid collectionId, CancellationToken ct = default);

    /// <summary>
    /// Makes the shared placements of one collection match <paramref name="wantedLocations"/> within
    /// <paramref name="managedLocations"/>: missing ones are added at the end of their location, unwanted ones (and the
    /// profiles' hides of them) are removed, and placements at any other location are left alone.
    /// </summary>
    Task SyncSharedLocationsAsync(
        Guid collectionId,
        IReadOnlyCollection<string> wantedLocations,
        IReadOnlyCollection<string> managedLocations,
        int displayLimit,
        CancellationToken ct = default);

    /// <summary>Puts the shared placements at a location in the given order and sets whether each is shown to everyone.</summary>
    Task SaveSharedOrderAsync(
        string location,
        IReadOnlyList<(Guid CollectionId, bool Visible)> ordered,
        CancellationToken ct = default);

    /// <summary>The collections one profile hid at a location.</summary>
    Task<IReadOnlySet<Guid>> GetHiddenCollectionIdsAsync(Guid profileId, string location, CancellationToken ct = default);

    /// <summary>Hides (true) or shows again (false) one lane for one profile.</summary>
    Task SetProfileHiddenAsync(Guid profileId, Guid collectionId, string location, bool hidden, CancellationToken ct = default);
}
