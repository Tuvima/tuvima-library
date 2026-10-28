using MediaEngine.Application.Services;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Decides which people a viewer may be shown when they are reached through a link from another person (a pen name behind a real
/// name, a group's members, the groups a person belongs to). A link is not a reason to show someone: a pen name on an ordinary book must
/// not expose a real person who is credited only on Vault, hidden-library or over-limit titles (security review of Vault sections WP0).
/// The rule is the same one the person's own page uses: a person with at least one credited file is shown only when the viewer can see
/// one of those files. A person with no credited file at all (a band member with no owned music) is shown as before.
/// </summary>
internal static class LinkedPersonVisibility
{
    public static async Task<IReadOnlySet<Guid>> VisibleAsync(
        IPersonRepository people,
        IEnumerable<Guid> personIds,
        ViewerCatalogue viewer,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(viewer);
        var ids = personIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var credited = (await people.GetCreditedFilesAsync(ids, ct).ConfigureAwait(false))
            .GroupBy(file => file.PersonId)
            .ToDictionary(group => group.Key, group => group.ToList());
        return ids
            .Where(id => !credited.TryGetValue(id, out var files)
                || files.Any(file => viewer.CanSeeCredit(file.WorkId, file.AssetId, file.LibraryId)))
            .ToHashSet();
    }
}
