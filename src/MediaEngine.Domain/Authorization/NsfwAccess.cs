using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Authorization;

/// <summary>
/// How one profile may reach the Vault (Videos spec D18): <see cref="Off"/> (invisible forever), <see cref="Pin"/>
/// (invisible until the profile enters its NSFW PIN, then visible until it locks again) or <see cref="Allowed"/>
/// (visible without a PIN once NSFW mode is switched on; adult profiles only).
/// </summary>
public enum NsfwAccessMode
{
    Off = 0,
    Pin = 1,
    Allowed = 2,
}

/// <summary>The rules that decide whether a profile can hold NSFW access at all, and how a mode is stored.</summary>
public static class NsfwAccessRules
{
    /// <summary>Minutes of inactivity after which NSFW mode locks itself when a profile sets nothing else (D19).</summary>
    public const int DefaultIdleLockMinutes = 15;

    /// <summary>The longest idle time a profile may pick, in minutes (24 hours).</summary>
    public const int MaximumIdleLockMinutes = 1440;

    /// <summary>
    /// True when a profile can hold NSFW access: a Kids profile (restricted role) or any profile with a content limit
    /// can only be Off and can never unlock, whatever it is set to or knows the PIN of (D18).
    /// </summary>
    public static bool IsEligible(ProfileRole role, string? contentLimit) =>
        role != ProfileRole.RestrictedProfile && string.IsNullOrWhiteSpace(contentLimit);

    /// <summary>The mode that actually applies: a profile that is not eligible is Off whatever is stored.</summary>
    public static NsfwAccessMode Effective(NsfwAccessMode configured, ProfileRole role, string? contentLimit) =>
        IsEligible(role, contentLimit) ? configured : NsfwAccessMode.Off;

    public static string ToStored(NsfwAccessMode mode) => mode switch
    {
        NsfwAccessMode.Off => "off",
        NsfwAccessMode.Pin => "pin",
        NsfwAccessMode.Allowed => "allowed",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown NSFW access mode."),
    };

    /// <summary>Reads a stored or requested mode. An unknown value is refused, never guessed.</summary>
    public static NsfwAccessMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "off" => NsfwAccessMode.Off,
        "pin" => NsfwAccessMode.Pin,
        "allowed" => NsfwAccessMode.Allowed,
        _ => throw new ArgumentException("Choose Off, PIN or Allowed."),
    };

    /// <summary>
    /// The idle time (minutes) a session locks after. <see langword="null"/> uses the default; 0 means never, which only an
    /// Allowed profile may choose (D23 c).
    /// </summary>
    public static int ResolveIdleLockMinutes(NsfwAccessMode mode, int? configured)
    {
        if (configured is null)
        {
            return DefaultIdleLockMinutes;
        }

        return configured == 0 && mode != NsfwAccessMode.Allowed ? DefaultIdleLockMinutes : configured.Value;
    }

    /// <summary>Checks a requested idle time: 0 (never) up to the maximum, and never only for an Allowed profile.</summary>
    public static void ValidateIdleLockMinutes(NsfwAccessMode mode, int? minutes)
    {
        if (minutes is null)
        {
            return;
        }

        if (minutes < 0 || minutes > MaximumIdleLockMinutes)
        {
            throw new ArgumentException($"Choose an idle time between 1 and {MaximumIdleLockMinutes} minutes, or never.");
        }

        if (minutes == 0 && mode != NsfwAccessMode.Allowed)
        {
            throw new ArgumentException("Only an Allowed profile can stay unlocked without an idle limit.");
        }
    }
}
