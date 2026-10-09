using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

public interface IIdentityRepository
{
    Task<AccountCredential?> GetAccountCredentialAsync(Guid accountId, AccountCredentialKind kind, CancellationToken ct = default);
    Task UpsertAccountCredentialAsync(AccountCredential credential, CancellationToken ct = default);
    Task UpdateAccountCredentialAttemptAsync(Guid credentialId, int failedAttemptCount, DateTimeOffset? lockedUntil, DateTimeOffset? lastUsedAt, CancellationToken ct = default);
    Task<ProfileCredential?> GetCredentialAsync(Guid profileId, ProfileCredentialKind kind, CancellationToken ct = default);
    Task UpsertCredentialAsync(ProfileCredential credential, CancellationToken ct = default);
    Task DeleteCredentialAsync(Guid profileId, ProfileCredentialKind kind, CancellationToken ct = default);
    Task UpdateCredentialAttemptAsync(Guid credentialId, int failedAttemptCount, DateTimeOffset? lockedUntil, DateTimeOffset? lastUsedAt, CancellationToken ct = default);
    Task<bool> IsAdministratorBootstrapCompletedAsync(CancellationToken ct = default);

    Task InsertSessionAsync(AuthSession session, CancellationToken ct = default);
    Task<AuthSession?> GetSessionByTokenHashAsync(string tokenHash, CancellationToken ct = default);
    Task<AuthSession?> GetSessionByIdAsync(Guid sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<AuthSession>> GetSessionsAsync(Guid accountId, CancellationToken ct = default);
    Task TouchSessionAsync(Guid sessionId, DateTimeOffset lastSeenAt, CancellationToken ct = default);
    /// <summary>Records that the person just proved it was them on this session.</summary>
    Task<bool> MarkSessionAuthenticatedAsync(Guid sessionId, DateTimeOffset authenticatedAt, CancellationToken ct = default);
    Task<bool> UpdateActiveProfileAsync(Guid sessionId, Guid activeProfileId, CancellationToken ct = default);

    /// <summary>Marks the session as still needing the person to pick who is using Tuvima; the next profile switch clears it.</summary>
    Task MarkSessionProfilePendingAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>The profile this account always opens as on this device, or <c>null</c> when none is set.</summary>
    Task<Guid?> GetDeviceProfilePreferenceAsync(Guid accountId, string deviceId, CancellationToken ct = default);
    Task SetDeviceProfilePreferenceAsync(Guid accountId, string deviceId, Guid profileId, DateTimeOffset updatedAt, CancellationToken ct = default);
    Task<bool> ClearDeviceProfilePreferenceAsync(Guid accountId, string deviceId, CancellationToken ct = default);
    /// <summary>The subset of the given profiles that have a PIN.</summary>
    Task<IReadOnlySet<Guid>> GetProfileIdsWithPinAsync(IReadOnlyCollection<Guid> profileIds, CancellationToken ct = default);
    Task<bool> RevokeSessionAsync(Guid sessionId, DateTimeOffset revokedAt, string reason, CancellationToken ct = default);
    Task<int> RevokeAccountSessionsAsync(Guid accountId, DateTimeOffset revokedAt, string reason, Guid? exceptSessionId = null, CancellationToken ct = default);

    Task InsertRecoveryCodesAsync(IReadOnlyList<PasswordRecoveryCode> codes, CancellationToken ct = default);
    Task<PasswordRecoveryCode?> GetActiveRecoveryCodeAsync(Guid accountId, string codeHash, DateTimeOffset now, CancellationToken ct = default);
    Task<int> CountActiveRecoveryCodesAsync(Guid accountId, DateTimeOffset now, CancellationToken ct = default);
    Task<bool> ConsumeRecoveryCodeAsync(Guid codeId, DateTimeOffset consumedAt, CancellationToken ct = default);
    Task DeleteRecoveryCodesAsync(Guid accountId, CancellationToken ct = default);

    Task InsertPasswordResetChallengeAsync(PasswordResetChallenge challenge, CancellationToken ct = default);
    Task<PasswordResetChallenge?> GetActivePasswordResetChallengeAsync(string tokenHash, DateTimeOffset now, CancellationToken ct = default);
    Task<bool> ConsumePasswordResetChallengeAsync(Guid challengeId, DateTimeOffset consumedAt, CancellationToken ct = default);
    Task InvalidatePasswordResetChallengesAsync(Guid accountId, CancellationToken ct = default);

    Task<ServiceCredential?> GetActiveServiceCredentialAsync(string purpose, CancellationToken ct = default);
    Task<ServiceCredential?> GetServiceCredentialByHashAsync(string tokenHash, CancellationToken ct = default);
    Task InsertServiceCredentialAsync(ServiceCredential credential, CancellationToken ct = default);
    Task RevokeServiceCredentialsAsync(string purpose, DateTimeOffset revokedAt, CancellationToken ct = default);

    Task WriteAuditEventAsync(Guid? accountId, Guid? profileId, Guid? sessionId, string eventType, bool succeeded, string? detail, DateTimeOffset occurredAt, CancellationToken ct = default);
}
