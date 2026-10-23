namespace MediaEngine.Domain.Entities;

/// <summary>
/// "Ask for my PIN on admin pages" for one profile (users, profiles and households spec: the profile holds the person's access).
/// The PIN asked for is the profile's own PIN; this row only records that the question is switched on and how long the answer lasts.
/// </summary>
public sealed class ProfileAdminProtection
{
    public Guid ProfileId { get; set; }
    public bool IsEnabled { get; set; }
    public string UnlockMode { get; set; } = "FixedDuration";
    public int? UnlockMinutes { get; set; }
    public long ProtectionVersion { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A profile's admin PIN answered on one sign-in session; it ends with the session or at <see cref="ExpiresAt"/>.</summary>
public sealed class ProfileAdminUnlock
{
    public Guid SessionId { get; set; }
    public Guid ProfileId { get; set; }
    public long ProtectionVersion { get; set; }
    public string Method { get; set; } = string.Empty;
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>The administrator roles a profile holds and the version that changes whenever its access does.</summary>
public sealed record ProfileAdminFlags(bool IsServerAdministrator, bool IsHouseholdAdministrator, long AuthorizationVersion);
