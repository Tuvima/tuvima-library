namespace MediaEngine.Domain.Entities;

/// <summary>
/// A user: a sign-in (email, credentials, passkeys, two-step codes, outside sign-ins) attached to exactly one profile. The profile
/// holds the person and what the person may see and do (roles, Read/Watch/Listen/View, libraries); the household of a user is its
/// profile's household.
/// </summary>
public sealed class Account
{
    public static readonly Guid SeedAccountId = new("00000000-0000-0000-0000-000000000002");

    public Guid Id { get; set; }
    public string? Email { get; set; }
    public string? NormalizedEmail { get; set; }

    /// <summary>True when the account has an email to sign in with. Only a this-computer account made without one is false.</summary>
    public bool HasEmail => !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(NormalizedEmail);
    public bool IsEnabled { get; set; } = true;

    public long AuthorizationVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// The profile this sign-in opens (a user is a sign-in attached to one profile; a profile has at most one user). A sign-in cannot
    /// exist without its profile.
    /// </summary>
    public Guid ProfileId { get; set; }

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
