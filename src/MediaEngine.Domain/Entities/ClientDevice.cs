namespace MediaEngine.Domain.Entities;

public sealed class ClientDevice
{
    public Guid Id { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid AccountId { get; set; }
    public Guid ProfileId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceClass { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string ClientVersion { get; set; } = string.Empty;
    public string Scopes { get; set; } = string.Empty;
    public string CapabilitiesJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }

    /// <summary>The person this phone backs its photos up to. Null until someone chooses one.</summary>
    public Guid? BackupProfileId { get; set; }

    public bool IsActive => RevokedAt is null;
}

public sealed class DevicePairingRequest
{
    public Guid Id { get; set; }
    public Guid ApplicationId { get; set; }
    public string DeviceCodeHash { get; set; } = string.Empty;
    public string UserCodeHash { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string ClientVersion { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceClass { get; set; } = string.Empty;
    public string RequestedScopes { get; set; } = string.Empty;
    public string ApprovedScopes { get; set; } = string.Empty;
    public string CapabilitiesJson { get; set; } = "{}";
    public string Status { get; set; } = "pending";
    public int PollIntervalSeconds { get; set; } = 5;
    public DateTimeOffset? LastPolledAt { get; set; }
    public Guid? ProfileId { get; set; }
    public Guid? AccountId { get; set; }
    public Guid? ApprovedByProfileId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

public sealed class ClientToken
{
    public Guid Id { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid AccountId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid ProfileId { get; set; }
    public Guid TokenFamilyId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string Scopes { get; set; } = string.Empty;
    public long AccountAuthorizationVersion { get; set; }
    public long GrantAuthorizationVersion { get; set; }
    public long ApplicationAuthorizationVersion { get; set; }
    public int Generation { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
}
