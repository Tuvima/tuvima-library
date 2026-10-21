using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

/// <summary>What one profile may do about the Vault, as the Engine reads it live (Videos spec D18, D19, D36).</summary>
/// <param name="ProfileId">The profile.</param>
/// <param name="ConfiguredMode">What an administrator set. Off when nothing was set.</param>
/// <param name="EffectiveMode">What applies now: a Kids or content-limited profile is Off whatever is stored.</param>
/// <param name="IdleLockMinutes">The profile's own idle time (0 = never), or <see langword="null"/> for the default.</param>
/// <param name="HasPin">True when an NSFW PIN has been saved. The PIN itself is never readable.</param>
public sealed record ProfileNsfwAccess(
    Guid ProfileId,
    NsfwAccessMode ConfiguredMode,
    NsfwAccessMode EffectiveMode,
    int? IdleLockMinutes,
    bool HasPin)
{
    public static ProfileNsfwAccess None(Guid profileId) => new(profileId, NsfwAccessMode.Off, NsfwAccessMode.Off, null, false);

    /// <summary>True when the profile can use NSFW mode at all.</summary>
    public bool CanUnlock => EffectiveMode != NsfwAccessMode.Off;
}

/// <summary>How a session came to be unlocked.</summary>
public enum NsfwUnlockMethod
{
    Pin = 0,
    Allowed = 1,
}

/// <summary>
/// Per-profile NSFW access settings and the per-device-session unlock state. A session unlock belongs to one
/// session and one profile: switching profile, changing the mode, the PIN, the content limit or the role, or ending the
/// session removes it (all enforced by the data structure itself, not only by callers).
/// </summary>
public interface INsfwAccessRepository
{
    /// <summary>The profile's NSFW access. A profile that cannot be found is Off.</summary>
    Task<ProfileNsfwAccess> GetAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>Saves the mode and idle time for an eligible profile. Throws <see cref="InvalidOperationException"/> for one that is not.</summary>
    Task SetAsync(Guid profileId, NsfwAccessMode mode, int? idleLockMinutes, Guid? changedByProfileId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>
    /// True when the session is unlocked for this profile right now: the unlock exists and has not run out, and the
    /// profile is still eligible and still set to a mode that allows the way it was unlocked.
    /// </summary>
    Task<bool> IsUnlockedAsync(Guid sessionId, Guid profileId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Unlocks the session. <paramref name="idleLockMinutes"/> of 0 means never run out.</summary>
    Task UnlockAsync(Guid sessionId, Guid profileId, NsfwUnlockMethod method, int idleLockMinutes, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Moves the idle limit forward for an unlock that is still valid. Returns false when the session is locked.</summary>
    Task<bool> TouchAsync(Guid sessionId, Guid profileId, int idleLockMinutes, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Locks one session.</summary>
    Task LockSessionAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Locks every session of a profile.</summary>
    Task LockProfileAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>The saved NSFW PIN credential (hash only), or <see langword="null"/>.</summary>
    Task<ProfileCredential?> GetPinAsync(Guid profileId, CancellationToken ct = default);

    Task UpsertPinAsync(ProfileCredential credential, CancellationToken ct = default);

    /// <summary>Removes the NSFW PIN. Returns true when one existed.</summary>
    Task<bool> DeletePinAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>Same contract as <see cref="IIdentityRepository.ReserveCredentialAttemptAsync"/>, for the NSFW PIN.</summary>
    Task<int?> ReservePinAttemptAsync(Guid credentialId, int lockoutThreshold, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Same contract as <see cref="IIdentityRepository.LockExhaustedCredentialAsync"/>, for the NSFW PIN.</summary>
    Task LockExhaustedPinAsync(Guid credentialId, int lockoutThreshold, DateTimeOffset lockedUntil, CancellationToken ct = default);

    /// <summary>Same contract as <see cref="IIdentityRepository.ResetCredentialAttemptsIfUnlockedAsync"/>, for the NSFW PIN.</summary>
    Task<bool> ResetPinAttemptsIfUnlockedAsync(Guid credentialId, DateTimeOffset now, CancellationToken ct = default);
}
