using MediaEngine.Domain.Entities;

namespace MediaEngine.Identity;

/// <summary>
/// The one definition of how a profile's PIN is shaped, hashed and rate-limited. A profile has one PIN; switching to the profile,
/// its admin pages and Vault mode all use it, so they share the hashing, the five-wrong-tries pause and the length of that pause.
/// </summary>
internal static class ProfileSecretFormat
{
    /// <summary>Wrong tries allowed before unlocking is paused.</summary>
    public const int MaxFailedAttempts = 5;

    /// <summary>How long unlocking is paused after <see cref="MaxFailedAttempts"/> wrong tries.</summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The text that is hashed. The credential kind is part of it, so a hash of one kind of secret is never valid for another.
    /// </summary>
    public static string Format(ProfileCredentialKind kind, string secret) => $"tuvima:{kind}:v1\n{secret}";

    /// <summary>A PIN is 4 to 12 digits.</summary>
    public static void ValidatePin(string value)
    {
        if (value.Length is < 4 or > 12 || value.Any(c => !char.IsAsciiDigit(c)))
        {
            throw new ArgumentException("PIN must contain 4 to 12 digits.");
        }
    }
}
