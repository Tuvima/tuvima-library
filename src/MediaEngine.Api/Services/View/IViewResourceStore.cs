namespace MediaEngine.Api.Services.View;

using MediaEngine.Storage.Contracts;

/// <summary>
/// Persistence lookup for resource ownership. It returns one opaque descriptor,
/// never an enumerable list of inaccessible resources. For assets reached via
/// an explicitly shared Gallery, implementations populate SharedWithProfileIds
/// only for the profiles granted that Gallery.
/// </summary>
public interface IViewResourceStore
{
    Task<ViewResourceDescriptor?> FindAsync(
        ViewResourceKind kind,
        Guid resourceId,
        Guid requestingProfileId,
        CancellationToken ct = default);

    /// <summary>
    /// Looks up many resources of one kind at once; an id with no resource is absent from the answer. The default asks
    /// <see cref="FindAsync"/> one by one; the persistence service answers a whole list with a handful of queries.
    /// </summary>
    async Task<IReadOnlyDictionary<Guid, ViewResourceDescriptor>> FindManyAsync(
        ViewResourceKind kind,
        IReadOnlyCollection<Guid> resourceIds,
        Guid requestingProfileId,
        CancellationToken ct = default)
    {
        var found = new Dictionary<Guid, ViewResourceDescriptor>();
        foreach (var id in resourceIds.Distinct())
        {
            if (await FindAsync(kind, id, requestingProfileId, ct).ConfigureAwait(false) is { } descriptor)
            {
                found[id] = descriptor;
            }
        }

        return found;
    }

    /// <summary>
    /// The items among <paramref name="itemIds"/> that still have a file row for <paramref name="role"/> in an indexed source
    /// of their own library, that is, the ones <see cref="ResolveContentAsync"/> would find a location for. Nothing is
    /// checked on disk. The default says every item has one (a store that cannot tell).
    /// </summary>
    Task<IReadOnlySet<Guid>> FindItemsWithContentAsync(
        IReadOnlyCollection<Guid> itemIds, string role, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<Guid>>(itemIds.ToHashSet());

    Task<LocalAssetContentLocation?> ResolveContentAsync(Guid itemId, string role,
        ResolvedViewScope scope, CancellationToken ct = default) =>
        Task.FromResult<LocalAssetContentLocation?>(null);
}
