namespace MediaEngine.Identity.Contracts;

/// <summary>
/// The separate PIN that turns NSFW mode on (Videos spec D19, D25). It is hashed like the profile PIN but kept apart, has its
/// own wrong-try count and pause, never signs in to the profile, and is never the same digits as the profile PIN. Nothing
/// here ever returns, logs or throws a PIN.
/// </summary>
public interface INsfwPinService
{
    /// <summary>True when the profile has saved an NSFW PIN.</summary>
    Task<bool> HasPinAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Saves or changes the NSFW PIN of a profile that can use NSFW mode. Changing needs the current NSFW PIN
    /// (<paramref name="currentPin"/>), counted like any unlock try. Throws <see cref="ArgumentException"/> for a PIN that is not 4 to 12
    /// digits or that has the same digits as the profile PIN (every save compares against the profile PIN and spends one of its
    /// tries, whether or not the digits match), <see cref="InvalidOperationException"/> when the profile cannot use
    /// NSFW mode, <see cref="ProfilePinRequiredException"/> for a missing or wrong current PIN and <see cref="ProfilePinLockedException"/>
    /// while unlocking is paused.
    /// </summary>
    Task SetPinAsync(Guid profileId, string newPin, string? currentPin, CancellationToken ct = default);

    /// <summary>An administrator removes the PIN without ever reading it. Returns true when one existed.</summary>
    Task<bool> ResetPinAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Returns when <paramref name="pin"/> is the profile's NSFW PIN. Every try counts: five wrong ones pause unlocking for a
    /// while. A profile with no NSFW PIN can never pass. Throws <see cref="ProfilePinRequiredException"/> for a missing or wrong
    /// PIN and <see cref="ProfilePinLockedException"/> while unlocking is paused.
    /// </summary>
    Task VerifyPinAsync(Guid profileId, string? pin, CancellationToken ct = default);
}
