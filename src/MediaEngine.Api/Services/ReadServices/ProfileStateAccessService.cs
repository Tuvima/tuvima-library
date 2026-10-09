using MediaEngine.Api.Services.Display;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Api.Services.ReadServices;

public sealed class ProfileStateAccessService(
    IDisplayProjectionReadService display,
    CollectionCatalogReadService collections,
    IProfileRepository profiles)
{
    public async Task<ProfileStateAccessScope> ResolveAsync(Guid profileId, CancellationToken ct)
    {
        var profile = await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false);
        if (profile is null)
        {
            return ProfileStateAccessScope.Empty;
        }

        var works = await display.LoadWorksAsync(ct).ConfigureAwait(false);
        var workIds = works.Select(row => row.WorkId)
            .Concat(works.Where(row => row.RootWorkId != Guid.Empty).Select(row => row.RootWorkId))
            .ToHashSet();
        var containerIds = works.Where(row => row.CollectionId.HasValue)
            .Select(row => row.CollectionId!.Value)
            .ToHashSet();
        var managed = await collections.GetManagedAsync(profile, ct, workIds).ConfigureAwait(false);
        containerIds.UnionWith(managed.Select(collection => collection.Id));
        return new ProfileStateAccessScope(workIds, containerIds);
    }
}

public sealed record ProfileStateAccessScope(
    IReadOnlySet<Guid> WorkIds,
    IReadOnlySet<Guid> ContainerIds)
{
    public static ProfileStateAccessScope Empty { get; } = new(new HashSet<Guid>(), new HashSet<Guid>());

    public bool Allows(ProfileEntityKind kind, Guid id) => kind switch
    {
        ProfileEntityKind.Collection or ProfileEntityKind.Playlist => ContainerIds.Contains(id),
        ProfileEntityKind.Album => WorkIds.Contains(id) || ContainerIds.Contains(id),
        _ => WorkIds.Contains(id),
    };
}
