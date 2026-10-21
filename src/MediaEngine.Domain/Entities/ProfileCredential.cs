namespace MediaEngine.Domain.Entities;

public enum ProfileCredentialKind
{
    ProfilePin = 0,

    /// <summary>The separate PIN that unlocks NSFW mode. Stored in its own table and never the same digits as the profile PIN.</summary>
    NsfwPin = 1,
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
