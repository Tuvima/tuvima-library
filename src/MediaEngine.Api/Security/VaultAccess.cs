using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Security;

/// <summary>
/// What one request may know about the Vault. An item is Vault when its file sits in one of <see cref="VaultLibraryIds"/>.
/// Unless <see cref="Unlocked"/> is true every such item is absent for this request: not listed, not counted, not found.
/// </summary>
public sealed class VaultVisibility
{
    /// <summary>No Vault library exists, so nothing is Vault.</summary>
    public static readonly VaultVisibility NoVault = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false);

    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public VaultVisibility(IReadOnlySet<string> vaultLibraryIds, bool unlocked)
    {
        VaultLibraryIds = vaultLibraryIds;
        Unlocked = unlocked;
    }

    /// <summary>Every Vault library (hyphenated GUID text, compared case-insensitively).</summary>
    public IReadOnlySet<string> VaultLibraryIds { get; }

    /// <summary>True only for a device session that has switched Vault mode on, for an eligible profile.</summary>
    public bool Unlocked { get; }

    /// <summary>The Vault libraries this request must treat as absent: all of them when locked, none when unlocked.</summary>
    public IReadOnlySet<string> HiddenLibraryIds => Unlocked ? Empty : VaultLibraryIds;

    public bool HasHiddenLibraries => !Unlocked && VaultLibraryIds.Count > 0;

    public bool IsVaultLibrary(string? libraryId) => libraryId is not null && VaultLibraryIds.Contains(libraryId);

    /// <summary>True when an item in this library must be treated as absent for this request.</summary>
    public bool HidesLibrary(string? libraryId) => !Unlocked && IsVaultLibrary(libraryId);
}

/// <summary>
/// Works out what a request may know about the Vault (Videos spec D17, D36). Only a person on a Dashboard session can ever
/// unlock: a service application, a paired native or television app, a cast session and an anonymous caller are never
/// unlocked (the Vault is Dashboard only in this version, D20).
/// </summary>
public sealed class VaultAccessResolver(IVaultLibraryRepository libraries, INsfwAccessRepository nsfw, TimeProvider clock)
{
    public async ValueTask<VaultVisibility> ResolveAsync(RequestAuthority authority, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var ids = await libraries.GetIdsAsync(ct).ConfigureAwait(false);
        if (ids.Count == 0)
        {
            return VaultVisibility.NoVault;
        }

        return new VaultVisibility(ids, await IsUnlockedAsync(authority, ct).ConfigureAwait(false));
    }

    /// <summary>True when this request is a person's own live Dashboard session that has switched Vault mode on.</summary>
    public async ValueTask<bool> IsUnlockedAsync(RequestAuthority authority, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (authority.PrincipalKind != PrincipalKind.Human
            || !authority.IsAuthenticated
            || !authority.AccountEnabled
            || !authority.GrantEnabled
            || authority.SessionId is not { } sessionId || sessionId == Guid.Empty
            || authority.ActiveProfileId is not { } profileId || profileId == Guid.Empty)
        {
            return false;
        }

        return await nsfw.IsUnlockedAsync(sessionId, profileId, clock.GetUtcNow(), ct).ConfigureAwait(false);
    }
}
