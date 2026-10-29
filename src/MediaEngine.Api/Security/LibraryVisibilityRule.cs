using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Security;

/// <summary>
/// The one rule that answers "may this profile see this library" (library access D2; simpler access D11). A profile sees a library when it
/// is a server administrator profile, or when the library is not a Vault library, the profile has the library's area (Read, Watch, Listen
/// or View), the library is not hidden for the profile's household, and it is not hidden for the profile itself. Nothing is hidden by
/// default: a new library is visible straight away to everyone with its area, and a new profile gets what its household has. Every place
/// that decides library visibility (browse, search, detail pages, players, artwork, streams, events, counts, downloads, the native apps)
/// asks this type or uses the hidden set it loads; none keeps a copy of the rule. "Hidden" here is the household's hidden libraries plus the
/// profile's own, so a caller that already applies <see cref="MaySee"/> to a hidden set needs no second check.
/// The Vault is never visible through this rule, not even to an administrator; its visibility stays with <see cref="VaultVisibility"/>
/// and <see cref="VaultAccessResolver"/>.
/// </summary>
public sealed class LibraryVisibilityRule(
    IProfileAccessRepository access,
    IHouseholdLibraryAccessRepository householdLibraries,
    IVaultLibraryRepository vaultLibraries)
{
    /// <summary>
    /// The rule on facts the caller already holds. <paramref name="hasArea"/> is whether the profile has the area of the library's media
    /// (a caller that cannot work the area out passes false, so the answer fails closed).
    /// </summary>
    public static bool MaySee(bool isAdministrator, bool isVault, bool hasArea, bool isHidden) =>
        !isVault && (isAdministrator || (hasArea && !isHidden));

    /// <summary>
    /// The libraries hidden from the profile: its household's hidden libraries plus its own hidden rows. A caller that asks about many
    /// libraries in one request loads it once and uses <see cref="MaySee"/>. An administrator never consults it.
    /// </summary>
    public async ValueTask<IReadOnlySet<Guid>> LoadHiddenAsync(Guid profileId, CancellationToken ct = default)
    {
        var own = await access.GetHiddenLibrariesAsync(profileId, ct).ConfigureAwait(false);
        var household = await householdLibraries.GetHiddenLibrariesForProfileAsync(profileId, ct).ConfigureAwait(false);
        if (household.Count == 0)
        {
            return own;
        }

        return own.Count == 0 ? household : own.Concat(household).ToHashSet();
    }

    /// <summary>Whether one library is hidden from the profile (hidden for its household, or for the profile itself).</summary>
    public async ValueTask<bool> IsHiddenAsync(Guid profileId, Guid libraryId, CancellationToken ct = default)
    {
        if (await access.IsLibraryHiddenAsync(profileId, libraryId, ct).ConfigureAwait(false))
        {
            return true;
        }

        return (await householdLibraries.GetHiddenLibrariesForProfileAsync(profileId, ct).ConfigureAwait(false)).Contains(libraryId);
    }

    /// <summary>The rule for one library, reading the Vault list, the profile's hidden libraries and its household's.</summary>
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
