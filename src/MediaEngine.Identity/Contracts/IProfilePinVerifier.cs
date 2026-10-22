namespace MediaEngine.Identity.Contracts;

/// <summary>Checks a person's profile PIN, with the same lockout rules as switching to that person.</summary>
public interface IProfilePinVerifier
{
    /// <summary>
    /// Returns when <paramref name="profileId"/> has no PIN or <paramref name="pin"/> is right. Throws
    /// <see cref="ProfilePinRequiredException"/> for a missing or wrong PIN and <see cref="ProfilePinLockedException"/>
    /// while the profile is locked. Only attempts that <paramref name="countsTowardLockout"/> are blocked by, or add to, the lockout.
    /// </summary>
    Task VerifyProfilePinAsync(Guid profileId, string? pin, bool countsTowardLockout, CancellationToken ct = default);

    /// <summary>
    /// True when the profile has a PIN. <see cref="VerifyProfilePinAsync"/> lets a profile with no PIN through (switching to it needs
    /// none), so every place that asks for the PIN as protection checks this first and treats "no PIN" as "cannot pass".
    /// </summary>
    Task<bool> HasProfilePinAsync(Guid profileId, CancellationToken ct = default);
}
