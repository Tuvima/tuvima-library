using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Aggregates;

/// <summary>
/// A lightweight local user profile.
///
/// Profiles enable multi-user access to the Dashboard without any
/// external authentication service.  Each profile has a display name, an
/// avatar colour, and a role that determines which Settings tabs are visible.
///
/// The default seed profile ("Owner") is always an <see cref="ProfileRole.Administrator"/>
/// and cannot be deleted.
///
/// Maps to the <c>profiles</c> table.
/// Spec: Settings & Management Layer — Identity & Multi-User.
/// </summary>
public sealed class Profile
{
    /// <summary>Well-known UUID for the seed "Owner" profile created on first run.</summary>
    public static readonly Guid SeedProfileId = new("00000000-0000-0000-0000-000000000001");

    /// <summary>Stable identifier.  PK in <c>profiles</c>.</summary>
    public Guid Id { get; set; }

    /// <summary>Human-readable name shown in the AppBar and Users tab.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Hex colour used for the avatar circle in the AppBar.
    /// Defaults to default violet (<c>#7C4DFF</c>).
    /// </summary>
    public string AvatarColor { get; set; } = "#7C4DFF";

    /// <summary>Absolute path to the uploaded avatar image, when one has been stored.</summary>
    public string? AvatarImagePath { get; set; }

    /// <summary>Access level.  Determines which Settings tabs are visible.</summary>
    public ProfileRole Role { get; set; } = ProfileRole.RestrictedProfile;

    /// <summary>When this profile was created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Opaque JSON blob storing the user's navigation configuration.
    /// Contains action cluster and tray layout preferences.
    /// <see langword="null"/> means "use defaults".
    /// The Engine stores it as-is; the Dashboard interprets the structure.
    /// </summary>
    public string? NavigationConfig { get; set; }

    /// <summary>
    /// The household this profile belongs to. A profile belongs to exactly one household. Only the seeded Owner
    /// profile can be without one, and only until the first administrator account is created.
    /// </summary>
    public Guid? HouseholdId { get; set; }
}
