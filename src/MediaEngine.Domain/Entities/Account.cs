namespace MediaEngine.Domain.Entities;

/// <summary>A sign-in principal that may be granted access to one or more library profiles.</summary>
public sealed class Account
{
    public static readonly Guid SeedAccountId = new("00000000-0000-0000-0000-000000000002");

    public Guid Id { get; set; }
    public string? Email { get; set; }
    public string? NormalizedEmail { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsAdministrator { get; set; }
    public long AuthorizationVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AccountProfileGrant
{
    public Guid AccountId { get; set; }
    public Guid ProfileId { get; set; }
    public bool IsDefault { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool AdminEnabled { get; set; }
    public long AuthorizationVersion { get; set; } = 1;
    public DateTimeOffset GrantedAt { get; set; }
}

public sealed class AccountFeatureGrant
{
    public Guid AccountId { get; set; }
    public string FeatureId { get; set; } = string.Empty;
    public DateTimeOffset GrantedAt { get; set; }
}

public sealed class AccountLibraryGrant
{
    public Guid AccountId { get; set; }
    public Guid LibraryId { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
}

public sealed class GrantAdminProtection
{
    public Guid AccountId { get; set; }
    public Guid ProfileId { get; set; }
    public bool IsEnabled { get; set; }
    public string UnlockMode { get; set; } = "FixedDuration";
    public int? UnlockMinutes { get; set; }
    public string? PinHash { get; set; }
    public string? HashScheme { get; set; }
    public int FailedAttemptCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public long ProtectionVersion { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class GrantAdminUnlock
{
    public Guid SessionId { get; set; }
    public Guid AccountId { get; set; }
    public Guid ProfileId { get; set; }
    public long ProtectionVersion { get; set; }
    public string Method { get; set; } = string.Empty;
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}
