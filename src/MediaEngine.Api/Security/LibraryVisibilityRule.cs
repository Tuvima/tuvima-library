using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Security;

/// <summary>
/// The one rule that answers "may this profile see this library" (simpler access D2, D11; access is held by the profile, users, profiles
/// and households spec; Library pages D9). A profile sees a library when it is a server administrator profile, or when the library is not a
/// Vault library, the profile has the library's area (Read, Watch, Listen or View), an administrator has not hidden the library from it,
/// and, for a library whose access is "Selected profiles", an administrator has chosen the profile for that library. A Standard library
/// grants nothing per library: a new library is visible straight away to everyone with its area. A Selected library is visible only to
/// administrators until profiles are chosen, and a new profile starts without it. Every place that decides library visibility (browse,
/// search, detail pages, players, artwork, streams, events, counts, downloads, the native apps) asks this type or uses the hidden set it
/// loads; none keeps a copy of the rule. "Hidden" here is the profile's own hidden libraries plus every Selected library the profile was not
/// chosen for, so a caller that already applies <see cref="MaySee"/> to a hidden set needs no second check.
/// The Vault is never visible through this rule, not even to an administrator; its visibility stays with <see cref="VaultVisibility"/>
/// and <see cref="VaultAccessResolver"/>.
/// </summary>
public sealed class LibraryVisibilityRule(
    IProfileAccessRepository access,
    IVaultLibraryRepository vaultLibraries,
    ILibraryProfileGrantRepository grants,
    ILibraryAccessCatalog catalog)
{
    /// <summary>
    /// The rule on facts the caller already holds. <paramref name="hasArea"/> is whether the profile has the area of the library's media
    /// (a caller that cannot work the area out passes false, so the answer fails closed).
    /// </summary>
    public static bool MaySee(bool isAdministrator, bool isVault, bool hasArea, bool isHidden) =>
        !isVault && (isAdministrator || (hasArea && !isHidden));

    /// <summary>
    /// The libraries hidden from the profile: its own hidden rows plus every Selected-profiles library the profile has not been chosen for.
    /// A caller that asks about many libraries in one request loads it once and uses <see cref="MaySee"/>. An administrator never consults it.
    /// </summary>
    public async ValueTask<IReadOnlySet<Guid>> LoadHiddenAsync(Guid profileId, CancellationToken ct = default)
    {
        var hidden = await access.GetHiddenLibrariesAsync(profileId, ct).ConfigureAwait(false);
        var selected = await catalog.GetSelectedLibraryIdsAsync(ct).ConfigureAwait(false);
        if (selected.Count == 0)
        {
            return hidden;
        }

        var granted = await grants.GetGrantedLibrariesAsync(profileId, ct).ConfigureAwait(false);
        var notChosen = selected.Where(id => !granted.Contains(id)).ToList();
        return notChosen.Count == 0 ? hidden : hidden.Concat(notChosen).ToHashSet();
    }

    /// <summary>Whether one library is hidden from the profile (its own hidden rows, or a Selected library it was not chosen for).</summary>
    public async ValueTask<bool> IsHiddenAsync(Guid profileId, Guid libraryId, CancellationToken ct = default)
    {
        if (await access.IsLibraryHiddenAsync(profileId, libraryId, ct).ConfigureAwait(false))
        {
            return true;
        }

        var selected = await catalog.GetSelectedLibraryIdsAsync(ct).ConfigureAwait(false);
        return selected.Contains(libraryId)
            && !(await grants.GetGrantedLibrariesAsync(profileId, ct).ConfigureAwait(false)).Contains(libraryId);
    }

    /// <summary>The rule for one library, reading the Vault list, the profile's hidden libraries and its Selected-profiles choices.</summary>
    public async ValueTask<bool> MaySeeAsync(
        Guid profileId,
        bool isAdministrator,
        Guid libraryId,
        bool hasArea,
        CancellationToken ct = default)
    {
        if (libraryId == Guid.Empty)
        {
            return false;
        }

        var isVault = (await vaultLibraries.GetIdsAsync(ct).ConfigureAwait(false)).Contains(libraryId.ToString("D"));
        if (isVault || isAdministrator)
        {
            return MaySee(isAdministrator, isVault, hasArea, false);
        }

        // Only ask about hiding when it can change the answer.
        var isHidden = hasArea && await IsHiddenAsync(profileId, libraryId, ct).ConfigureAwait(false);
        return MaySee(false, false, hasArea, isHidden);
    }
}
