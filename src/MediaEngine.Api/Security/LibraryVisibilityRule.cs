using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Security;

/// <summary>
/// The one rule that answers "may this account see this library" (simpler access D2, D11). An account sees a library when it is an
/// administrator, or when the library is not a Vault library, the account has the library's area (Read, Watch, Listen or View) and an
/// administrator has not hidden the library from it. Nothing is granted per library: a new library is visible straight away to everyone
/// with its area. Every place that decides library visibility (browse, search, detail pages, players, artwork, streams, events, counts,
/// downloads, the native apps) asks this type or uses the hidden set it loads; none keeps a copy of the rule.
/// The Vault is never visible through this rule, not even to an administrator; its visibility stays with <see cref="VaultVisibility"/>
/// and <see cref="VaultAccessResolver"/>.
/// </summary>
public sealed class LibraryVisibilityRule(IAccountRepository access, IVaultLibraryRepository vaultLibraries)
{
    /// <summary>
    /// The rule on facts the caller already holds. <paramref name="hasArea"/> is whether the account has the area of the library's media
    /// (a caller that cannot work the area out passes false, so the answer fails closed).
    /// </summary>
    public static bool MaySee(bool isAdministrator, bool isVault, bool hasArea, bool isHidden) =>
        !isVault && (isAdministrator || (hasArea && !isHidden));

    /// <summary>
    /// The libraries hidden from the account: its own and, for a household member, those of the account it follows. One read; a caller
    /// that asks about many libraries in one request loads it once and uses <see cref="MaySee"/>.
    /// </summary>
    public ValueTask<IReadOnlySet<Guid>> LoadHiddenAsync(Guid accountId, CancellationToken ct = default) =>
        new(access.GetHiddenLibrariesAsync(accountId, ct));

    /// <summary>The rule for one library, reading the Vault list and the account's hidden libraries.</summary>
    public async ValueTask<bool> MaySeeAsync(
        Guid accountId,
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
        var isHidden = hasArea && await access.IsLibraryHiddenAsync(accountId, libraryId, ct).ConfigureAwait(false);
        return MaySee(false, false, hasArea, isHidden);
    }
}
