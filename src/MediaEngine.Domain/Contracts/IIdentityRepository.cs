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

    /// <summary>The account's authenticator key row (pending or enabled); <c>null</c> when it never started setting one up.</summary>
    Task<AccountTwoStep?> GetAccountTwoStepAsync(Guid accountId, CancellationToken ct = default);
    /// <summary>Stores a new, not yet confirmed authenticator key, replacing any earlier unconfirmed one. Never replaces an enabled one.</summary>
    Task<bool> SavePendingAccountTwoStepAsync(Guid accountId, string secretProtected, DateTimeOffset now, CancellationToken ct = default);
    /// <summary>Turns a confirmed setup on and remembers the step of the code that confirmed it.</summary>
    Task<bool> EnableAccountTwoStepAsync(Guid accountId, long usedStep, DateTimeOffset enabledAt, CancellationToken ct = default);
    /// <summary>Remembers that a code for <paramref name="step"/> was used. False when that step (or a later one) was already used, so one code works once.</summary>
    Task<bool> AdvanceAccountTwoStepAsync(Guid accountId, long step, CancellationToken ct = default);
    Task<bool> DeleteAccountTwoStepAsync(Guid accountId, CancellationToken ct = default);

    Task InsertTwoStepChallengeAsync(TwoStepChallenge challenge, CancellationToken ct = default);
    Task<TwoStepChallenge?> GetActiveTwoStepChallengeAsync(string tokenHash, DateTimeOffset now, CancellationToken ct = default);
    Task<bool> ConsumeTwoStepChallengeAsync(Guid challengeId, DateTimeOffset consumedAt, CancellationToken ct = default);
    /// <summary>Counts one wrong code against the challenge and returns how many wrong codes it has had.</summary>
    Task<int> RecordTwoStepChallengeFailureAsync(Guid challengeId, CancellationToken ct = default);
    Task InvalidateTwoStepChallengesAsync(Guid accountId, CancellationToken ct = default);

    Task<ServiceCredential?> GetActiveServiceCredentialAsync(string purpose, CancellationToken ct = default);
    Task<ServiceCredential?> GetServiceCredentialByHashAsync(string tokenHash, CancellationToken ct = default);
    Task InsertServiceCredentialAsync(ServiceCredential credential, CancellationToken ct = default);
    Task RevokeServiceCredentialsAsync(string purpose, DateTimeOffset revokedAt, CancellationToken ct = default);

    Task WriteAuditEventAsync(Guid? accountId, Guid? profileId, Guid? sessionId, string eventType, bool succeeded, string? detail, DateTimeOffset occurredAt, CancellationToken ct = default);
}
