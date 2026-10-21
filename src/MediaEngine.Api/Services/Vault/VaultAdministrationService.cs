using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Identity.Contracts;

namespace MediaEngine.Api.Services.Vault;

/// <summary>The administrator has no profile PIN yet, so nothing that opens up the Vault can happen (Videos spec D25).</summary>
public sealed class VaultAdminPinNotSetException : InvalidOperationException
{
    public VaultAdminPinNotSetException()
        : base("Set a PIN on your own profile first. It is asked for again before the Vault can be created or opened to anyone.")
    {
    }
}

/// <summary>The profile that asked cannot use NSFW mode (a Kids or content-limited profile), or does not exist.</summary>
public sealed class NsfwNotAvailableException : InvalidOperationException
{
    public NsfwNotAvailableException()
        : base("Not found.")
    {
    }
}

/// <summary>
/// Everything that creates a Vault library or opens NSFW access to a profile (Videos spec D18, D19, D25). The actions that open
/// the Vault up ask for the administrator's own profile PIN again at that moment, with the same wrong-try count and pause as
/// switching profile, and refuse when the administrator has no profile PIN. Nothing here logs, returns or throws a PIN.
/// </summary>
public sealed class VaultAdministrationService(
    IVaultLibraryRepository libraries,
    INsfwAccessRepository nsfw,
    INsfwPinService nsfwPins,
    IProfileRepository profiles,
    IIdentityRepository identities,
    IProfilePinVerifier profilePins,
    TimeProvider clock,
    ILogger<VaultAdministrationService> logger)
{
    /// <summary>
    /// Passes only when <paramref name="adminProfileId"/> has a profile PIN and <paramref name="adminPin"/> is it. A missing PIN is
    /// a different answer from a wrong one so the Dashboard can send the administrator to set one.
    /// </summary>
    public async Task RequireAdminPinAsync(Guid adminProfileId, string? adminPin, CancellationToken ct)
    {
        if (await identities.GetCredentialAsync(adminProfileId, MediaEngine.Domain.Entities.ProfileCredentialKind.ProfilePin, ct).ConfigureAwait(false) is null)
        {
            throw new VaultAdminPinNotSetException();
        }

        await profilePins.VerifyProfilePinAsync(adminProfileId, adminPin, countsTowardLockout: true, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VaultLibrary>> ListLibrariesAsync(CancellationToken ct) =>
        await libraries.GetAllAsync(ct).ConfigureAwait(false);

    /// <summary>Creates an empty Vault library with its own settings. The folder is attached by the ingestion work, never shared with a normal library.</summary>
    public async Task<VaultLibrary> CreateLibraryAsync(
        Guid adminProfileId, string lookupSource, string? lookupServerId, bool adultsConfirmed, string? adminPin, CancellationToken ct)
    {
        if (!adultsConfirmed)
        {
            throw new ArgumentException("Confirm that everyone who can open the Vault is an adult and that the content is lawful.");
        }

        ValidateLookup(lookupSource, lookupServerId);
        await RequireAdminPinAsync(adminProfileId, adminPin, ct).ConfigureAwait(false);

        var now = clock.GetUtcNow();
        var library = new VaultLibrary(
            Guid.NewGuid().ToString("D"), lookupSource, NullIfBlank(lookupServerId), now, adminProfileId, now, now);
        await libraries.AddAsync(library, ct).ConfigureAwait(false);
        logger.LogInformation("Vault library {LibraryId} created by profile {ProfileId}.", library.LibraryId, adminProfileId);
        return library;
    }

    public async Task<VaultLibrary?> UpdateLibraryAsync(
        Guid adminProfileId, string libraryId, string lookupSource, string? lookupServerId, string? adminPin, CancellationToken ct)
    {
        ValidateLookup(lookupSource, lookupServerId);
        if (await libraries.GetAsync(libraryId, ct).ConfigureAwait(false) is null)
        {
            return null;
        }

        await RequireAdminPinAsync(adminProfileId, adminPin, ct).ConfigureAwait(false);
        return await libraries.UpdateLookupAsync(libraryId, lookupSource, NullIfBlank(lookupServerId), clock.GetUtcNow(), ct).ConfigureAwait(false)
            ? await libraries.GetAsync(libraryId, ct).ConfigureAwait(false)
            : null;
    }

    public async Task<bool> RemoveLibraryAsync(Guid adminProfileId, string libraryId, string? adminPin, CancellationToken ct)
    {
        if (await libraries.GetAsync(libraryId, ct).ConfigureAwait(false) is null)
        {
            return false;
        }

        await RequireAdminPinAsync(adminProfileId, adminPin, ct).ConfigureAwait(false);
        return await libraries.RemoveAsync(libraryId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets one profile's NSFW mode. Opening access (PIN or Allowed) needs the administrator's own PIN; closing it never does, and
    /// closing it locks every device that profile had unlocked.
    /// </summary>
    public async Task<ProfileNsfwAccess> SetAccessAsync(
        Guid adminProfileId, Guid targetProfileId, NsfwAccessMode mode, int? idleLockMinutes, string? adminPin, CancellationToken ct)
    {
        NsfwAccessRules.ValidateIdleLockMinutes(mode, idleLockMinutes);
        var target = await profiles.GetByIdAsync(targetProfileId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Profile not found.");

        if (mode != NsfwAccessMode.Off)
        {
            if (!NsfwAccessRules.IsEligible(target.Role, target.ContentLimit))
            {
                throw new InvalidOperationException("A Kids or content-limited profile can never use NSFW mode.");
            }

            await RequireAdminPinAsync(adminProfileId, adminPin, ct).ConfigureAwait(false);
        }

        await nsfw.SetAsync(targetProfileId, mode, idleLockMinutes, adminProfileId, clock.GetUtcNow(), ct).ConfigureAwait(false);
        if (mode == NsfwAccessMode.Off)
        {
            await nsfw.LockProfileAsync(targetProfileId, ct).ConfigureAwait(false);
        }

        logger.LogInformation("NSFW access of profile {ProfileId} set to {Mode} by profile {AdminProfileId}.", targetProfileId, mode, adminProfileId);
        return await nsfw.GetAsync(targetProfileId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// An administrator removes a profile's NSFW PIN without reading it, and every device that profile had unlocked locks. Like every
    /// other Vault action it asks for the administrator's own profile PIN at this moment (Videos spec D25).
    /// </summary>
    public async Task<bool> ResetPinAsync(Guid adminProfileId, Guid targetProfileId, string? adminPin, CancellationToken ct)
    {
        await RequireAdminPinAsync(adminProfileId, adminPin, ct).ConfigureAwait(false);
        var removed = await nsfwPins.ResetPinAsync(targetProfileId, ct).ConfigureAwait(false);
        await nsfw.LockProfileAsync(targetProfileId, ct).ConfigureAwait(false);
        return removed;
    }

    /// <summary>The caller's own NSFW state, or <see langword="null"/> when the profile cannot use NSFW mode (then the action answers not found).</summary>
    public async Task<NsfwState?> GetStateAsync(Guid sessionId, Guid profileId, CancellationToken ct)
    {
        var access = await nsfw.GetAsync(profileId, ct).ConfigureAwait(false);
        if (!access.CanUnlock)
        {
            return null;
        }

        var idle = NsfwAccessRules.ResolveIdleLockMinutes(access.EffectiveMode, access.IdleLockMinutes);
        var unlocked = await nsfw.IsUnlockedAsync(sessionId, profileId, clock.GetUtcNow(), ct).ConfigureAwait(false);
        return new NsfwState(access.EffectiveMode, unlocked, access.HasPin, idle);
    }

    /// <summary>Turns NSFW mode on for this device session. A PIN profile must enter its NSFW PIN; five wrong tries pause unlocking.</summary>
    public async Task<NsfwState> UnlockAsync(Guid sessionId, Guid profileId, string? pin, CancellationToken ct)
    {
        var access = await nsfw.GetAsync(profileId, ct).ConfigureAwait(false);
        if (!access.CanUnlock)
        {
            throw new NsfwNotAvailableException();
        }

        var idle = NsfwAccessRules.ResolveIdleLockMinutes(access.EffectiveMode, access.IdleLockMinutes);
        if (access.EffectiveMode == NsfwAccessMode.Pin)
        {
            await nsfwPins.VerifyPinAsync(profileId, pin, ct).ConfigureAwait(false);
        }

        try
        {
            await nsfw.UnlockAsync(
                sessionId, profileId, access.EffectiveMode == NsfwAccessMode.Pin ? NsfwUnlockMethod.Pin : NsfwUnlockMethod.Allowed,
                idle, clock.GetUtcNow(), ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The profile or session changed between the checks (switched profile, signed out): it looks like any other refusal.
            throw new NsfwNotAvailableException();
        }

        return new NsfwState(access.EffectiveMode, true, access.HasPin, idle);
    }

    public Task LockAsync(Guid sessionId, CancellationToken ct) => nsfw.LockSessionAsync(sessionId, ct);

    /// <summary>Restarts the idle timer. Returns false when the session is not unlocked (then there is nothing to keep).</summary>
    public async Task<bool> KeepAliveAsync(Guid sessionId, Guid profileId, CancellationToken ct)
    {
        var access = await nsfw.GetAsync(profileId, ct).ConfigureAwait(false);
        if (!access.CanUnlock)
        {
            return false;
        }

        var idle = NsfwAccessRules.ResolveIdleLockMinutes(access.EffectiveMode, access.IdleLockMinutes);
        return await nsfw.TouchAsync(sessionId, profileId, idle, clock.GetUtcNow(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// True when saving the caller's NSFW PIN must be backed by a recent sign-in: the profile can use NSFW mode, has no NSFW PIN yet
    /// (a first PIN, or one just reset by an administrator) and has no profile PIN to prove who is typing. The endpoint asks the
    /// recent sign-in guard for those; a profile with a profile PIN is asked for that PIN instead.
    /// </summary>
    public async Task<bool> RequiresRecentSignInForFirstPinAsync(Guid profileId, CancellationToken ct)
    {
        var access = await nsfw.GetAsync(profileId, ct).ConfigureAwait(false);
        return access.CanUnlock
            && !await nsfwPins.HasPinAsync(profileId, ct).ConfigureAwait(false)
            && await identities.GetCredentialAsync(profileId, MediaEngine.Domain.Entities.ProfileCredentialKind.ProfilePin, ct).ConfigureAwait(false) is null;
    }

    /// <summary>
    /// Saves or changes the caller's own NSFW PIN. Changing needs the current NSFW PIN. A first PIN (or one after an administrator
    /// reset) needs the profile's own PIN when it has one, so a signed-in but unattended device cannot claim the PIN; the endpoint
    /// asks for a recent sign-in when the profile has no PIN (<see cref="RequiresRecentSignInForFirstPinAsync"/>).
    /// </summary>
    public async Task SetOwnPinAsync(Guid profileId, string newPin, string? currentPin, string? profilePin, CancellationToken ct)
    {
        var access = await nsfw.GetAsync(profileId, ct).ConfigureAwait(false);
        if (!access.CanUnlock)
        {
            throw new NsfwNotAvailableException();
        }

        if (!await nsfwPins.HasPinAsync(profileId, ct).ConfigureAwait(false)
            && await identities.GetCredentialAsync(profileId, MediaEngine.Domain.Entities.ProfileCredentialKind.ProfilePin, ct).ConfigureAwait(false) is not null)
        {
            await profilePins.VerifyProfilePinAsync(profileId, profilePin, countsTowardLockout: true, ct).ConfigureAwait(false);
        }

        await nsfwPins.SetPinAsync(profileId, newPin, currentPin, ct).ConfigureAwait(false);
    }

    private static void ValidateLookup(string lookupSource, string? lookupServerId)
    {
        if (!VaultLookupSources.IsValid(lookupSource))
        {
            throw new ArgumentException("Choose None or a Stash-box server for the Vault's lookups.");
        }

        if (lookupSource == VaultLookupSources.None && !string.IsNullOrWhiteSpace(lookupServerId))
        {
            throw new ArgumentException("A Vault library with no lookups has no server.");
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>A profile's own NSFW state as the service reads it. Never carries a PIN.</summary>
public sealed record NsfwState(NsfwAccessMode Mode, bool Unlocked, bool HasPin, int IdleLockMinutes);
