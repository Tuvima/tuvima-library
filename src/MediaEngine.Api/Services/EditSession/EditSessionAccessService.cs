using MediaEngine.Api.Models;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.BatchEdit;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Decides which of the items an edit session names the caller may not edit, and why. A library title, person, Series and Universe
/// follow the same resource check a single edit gets (<see cref="LibraryBatchEditAccessService"/>); a curated collection also follows
/// the collection ownership rule the collection update action applies, so a collection that belongs to another profile is refused by
/// name instead of being changed because the caller is an administrator.
/// </summary>
internal sealed class EditSessionAccessService(
    LibraryBatchEditAccessService resources,
    ICollectionRepository collections,
    IProfileRepository profiles,
    IRequestAuthorityResolver authorityResolver)
{
    public async Task<IReadOnlyDictionary<Guid, string>> FindRefusalsAsync(
        HttpContext http,
        IReadOnlyCollection<Guid> entityIds,
        CancellationToken ct = default)
    {
        var refusals = new Dictionary<Guid, string>(await resources.FindRefusalsAsync(http, entityIds, ct).ConfigureAwait(false));

        Profile? activeProfile = null;
        var profileLoaded = false;
        foreach (var id in entityIds.Where(id => id != Guid.Empty).Distinct())
        {
            if (refusals.ContainsKey(id)
                || await collections.GetByIdAsync(id, ct).ConfigureAwait(false) is not { } collection
                || !CollectionAccessPolicy.IsManagedCollectionType(collection.CollectionType))
            {
                continue;
            }

            if (!profileLoaded)
            {
                var authority = await authorityResolver.ResolveAsync(http, ct).ConfigureAwait(false);
                activeProfile = authority.ActiveProfileId is { } profileId
                    ? await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false)
                    : null;
                profileLoaded = true;
            }

            if (!CollectionAccessPolicy.CanEdit(collection, activeProfile, hasCollectionsWrite: true))
            {
                refusals[id] = EditSessionReasons.NotAllowedToEdit;
            }
        }

        return refusals;
    }
}
