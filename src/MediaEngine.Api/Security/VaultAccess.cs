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

    /// <param name="vaultLibraryIds">Every Vault library.</param>
    /// <param name="unlocked">True when the device session has switched Vault mode on.</param>
    /// <param name="allowedLibraryIds">The Vault sections this person may open once unlocked; <see langword="null"/> means every section (an administrator).</param>
    public VaultVisibility(IReadOnlySet<string> vaultLibraryIds, bool unlocked, IReadOnlySet<string>? allowedLibraryIds = null)
    {
        VaultLibraryIds = vaultLibraryIds;
        Unlocked = unlocked;
        AllowedLibraryIds = allowedLibraryIds;
        HiddenLibraryIds = !unlocked
            ? vaultLibraryIds
            : allowedLibraryIds is null
                ? Empty
                : vaultLibraryIds.Where(id => !allowedLibraryIds.Contains(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every Vault library (hyphenated GUID text, compared case-insensitively).</summary>
    public IReadOnlySet<string> VaultLibraryIds { get; }

    /// <summary>True only for a device session that has switched Vault mode on, for an eligible profile.</summary>
    public bool Unlocked { get; }

    /// <summary>The sections an unlocked person may open (Vault sections D28, D39); <see langword="null"/> means all of them.</summary>
    public IReadOnlySet<string>? AllowedLibraryIds { get; }

    /// <summary>The Vault libraries this request must treat as absent: all of them when locked, and when unlocked every section the person was not given.</summary>
    public IReadOnlySet<string> HiddenLibraryIds { get; }

    public bool HasHiddenLibraries => HiddenLibraryIds.Count > 0;

    public bool IsVaultLibrary(string? libraryId) => libraryId is not null && VaultLibraryIds.Contains(libraryId);

    /// <summary>True when an item in this library must be treated as absent for this request.</summary>
    public bool HidesLibrary(string? libraryId) => libraryId is not null && HiddenLibraryIds.Contains(libraryId);
}

/// <summary>
/// Works out what a request may know about the Vault (Videos spec D17, D36). Only a person on a Dashboard session can ever
/// unlock: a service application, a paired native or television app, a cast session and an anonymous caller are never
/// unlocked (the Vault is Dashboard only in this version, D20).
/// </summary>
public sealed class VaultAccessResolver(
    IVaultLibraryRepository libraries, INsfwAccessRepository nsfw, TimeProvider clock, IVaultSectionGrantRepository? sections = null)
{
    public async ValueTask<VaultVisibility> ResolveAsync(RequestAuthority authority, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var ids = await libraries.GetIdsAsync(ct).ConfigureAwait(false);
        if (ids.Count == 0)
        {
            return VaultVisibility.NoVault;
        }

        if (!await IsUnlockedAsync(authority, ct).ConfigureAwait(false))
        {
            return new VaultVisibility(ids, unlocked: false);
        }

        // Once unlocked, a server administrator opens every section; anyone else opens only the sections an administrator ticked for them
        // (D28, D39). With no grant store (a test host) every section is open, as before sections existed.
        if (sections is null || authority.IsEffectiveAdministrator || authority.ActiveProfileId is not { } profileId)
        {
            return new VaultVisibility(ids, unlocked: true);
        }

        return new VaultVisibility(ids, unlocked: true, await sections.GetGrantedAsync(profileId, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// True when the request is unlocked and the person may open at least one Vault section (an administrator opens all). A person with
    /// nothing ticked is treated as locked out of every Vault read (Vault sections D28, D39).
    /// </summary>
    public async ValueTask<bool> MayOpenAnySectionAsync(RequestAuthority authority, CancellationToken ct = default)
    {
        var visibility = await ResolveAsync(authority, ct).ConfigureAwait(false);
        return visibility.Unlocked && (visibility.AllowedLibraryIds is null || visibility.AllowedLibraryIds.Count > 0);
    }

    /// <summary>True when this request is a person's own live Dashboard session that has switched Vault mode on.</summary>
    public async ValueTask<bool> IsUnlockedAsync(RequestAuthority authority, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (authority.PrincipalKind != PrincipalKind.Human
            || !authority.IsAuthenticated
            || !authority.AccountEnabled
            || !authority.ActiveProfileInUserHousehold
            || authority.SessionId is not { } sessionId || sessionId == Guid.Empty
            || authority.ActiveProfileId is not { } profileId || profileId == Guid.Empty)
        {
            return false;
        }

        return await nsfw.IsUnlockedAsync(sessionId, profileId, clock.GetUtcNow(), ct).ConfigureAwait(false);
    }
}
