namespace MediaEngine.Domain.Entities;

public enum ProfileCredentialKind
{
    /// <summary>The one PIN of a profile: switching to it, its admin pages (when asked) and Vault mode (when asked).</summary>
    ProfilePin = 0,
}

/// <summary>A versioned first-party credential belonging to one local profile.</summary>
public sealed class ProfileCredential
{
    public Guid Id { get; set; }
    public Guid ProfileId { get; set; }
    public ProfileCredentialKind Kind { get; set; }
    public string SecretHash { get; set; } = string.Empty;
    public string HashScheme { get; set; } = "aspnet-pbkdf2-v3";
    public int HashVersion { get; set; } = 1;
    public string SecurityStamp { get; set; } = string.Empty;
    public int FailedAttemptCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}
