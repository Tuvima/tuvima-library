using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Security;

/// <summary>
/// The one rule that answers "may this profile see this library" (simpler access D2, D11; access is held by the profile, users, profiles
/// and households spec). A profile sees a library when it is a server administrator profile, or when the library is not a Vault library,
/// the profile has the library's area (Read, Watch, Listen or View) and an administrator has not hidden the library from it. Nothing is granted per library: a new library is visible straight away to everyone
/// with its area. Every place that decides library visibility (browse, search, detail pages, players, artwork, streams, events, counts,
/// downloads, the native apps) asks this type or uses the hidden set it loads; none keeps a copy of the rule.
/// The Vault is never visible through this rule, not even to an administrator; its visibility stays with <see cref="VaultVisibility"/>
/// and <see cref="VaultAccessResolver"/>.
/// </summary>
public sealed class LibraryVisibilityRule(IProfileAccessRepository access, IVaultLibraryRepository vaultLibraries)
{
    /// <summary>
    /// The rule on facts the caller already holds. <paramref name="hasArea"/> is whether the profile has the area of the library's media
    /// (a caller that cannot work the area out passes false, so the answer fails closed).
    /// </summary>
    public static bool MaySee(bool isAdministrator, bool isVault, bool hasArea, bool isHidden) =>
        !isVault && (isAdministrator || (hasArea && !isHidden));

    /// <summary>
    /// The libraries hidden from the profile (its own rows). One read; a caller that asks about many libraries in one request loads it
    /// once and uses <see cref="MaySee"/>.
    /// </summary>
    public ValueTask<IReadOnlySet<Guid>> LoadHiddenAsync(Guid profileId, CancellationToken ct = default) =>
        new(access.GetHiddenLibrariesAsync(profileId, ct));

    /// <summary>The rule for one library, reading the Vault list and the profile's hidden libraries.</summary>
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
        var isHidden = hasArea && await access.IsLibraryHiddenAsync(profileId, libraryId, ct).ConfigureAwait(false);
        return MaySee(false, false, hasArea, isHidden);
    }
}
