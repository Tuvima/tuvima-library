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

    /// <summary>
    /// The household this account belongs to. <see langword="null"/> on a new account means "start a new household
    /// for it"; the data store fills it in when the account is saved.
    /// </summary>
    public Guid? HouseholdId { get; set; }

    /// <summary>
    /// When set, this account has no feature or library access of its own: it follows the access of the named
    /// account (a person's own sign-in follows the household's main sign-in). Read at use time, so later changes to
    /// the household's access reach the person without being copied.
    /// </summary>
    public Guid? GrantsInheritFromAccountId { get; set; }

    /// <summary>
    /// True for an account that was set up on this computer without a password. It can only be used in a browser
    /// on that same computer until the owner adds a password or passkey. Changed only through
    /// <see cref="MarkThisComputerOnly"/> and <see cref="ClearThisComputerOnly"/>.
    /// </summary>
    public bool IsThisComputerOnly { get; private set; }

    /// <summary>
    /// True while the account's password is one an administrator chose for it: the person may only choose their own
    /// password until then, and the temporary one stops working at <see cref="TemporaryPasswordExpiresAt"/>.
    /// </summary>
    public bool MustChangePassword { get; set; }

    /// <summary>When an administrator-set temporary password stops working; <see langword="null"/> otherwise.</summary>
    public DateTimeOffset? TemporaryPasswordExpiresAt { get; set; }

    /// <summary>True while a temporary password is in force and has run out.</summary>
    public bool IsTemporaryPasswordExpired(DateTimeOffset now) =>
        MustChangePassword && TemporaryPasswordExpiresAt is { } expires && expires <= now;

    /// <summary>Marks the account as usable only on this computer (no password has been set yet).</summary>
    public void MarkThisComputerOnly() => IsThisComputerOnly = true;

    /// <summary>Lifts the this-computer-only limit once the account has a password or passkey of its own.</summary>
    public void ClearThisComputerOnly() => IsThisComputerOnly = false;
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
