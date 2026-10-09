using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

public sealed class IdentityRepository(IDatabaseConnection db) : IIdentityRepository
{
    public Task<AccountCredential?> GetAccountCredentialAsync(Guid accountId, AccountCredentialKind kind, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<AccountCredentialRow>(AccountCredentialSelect +
            " WHERE account_id = @accountId AND credential_kind = @kind LIMIT 1;", new { accountId, kind = kind.ToString() });
        return Task.FromResult(row is null ? null : Map(row));
    }

    public Task UpsertAccountCredentialAsync(AccountCredential credential, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        conn.Execute("""
            INSERT INTO account_credentials
                (id, account_id, credential_kind, secret_hash, hash_scheme, hash_version, security_stamp,
                 failed_attempt_count, locked_until, created_at, updated_at, last_used_at)
            VALUES (@Id, @AccountId, @Kind, @SecretHash, @HashScheme, @HashVersion, @SecurityStamp,
                    @FailedAttemptCount, @LockedUntil, @CreatedAt, @UpdatedAt, @LastUsedAt)
            ON CONFLICT(account_id, credential_kind) DO UPDATE SET
                secret_hash = excluded.secret_hash, hash_scheme = excluded.hash_scheme,
                hash_version = excluded.hash_version, security_stamp = excluded.security_stamp,
                failed_attempt_count = excluded.failed_attempt_count, locked_until = excluded.locked_until,
                updated_at = excluded.updated_at, last_used_at = excluded.last_used_at;
            """, ToParameters(credential));
        return Task.CompletedTask;
    }

    public Task UpdateAccountCredentialAttemptAsync(Guid credentialId, int failedAttemptCount, DateTimeOffset? lockedUntil, DateTimeOffset? lastUsedAt, CancellationToken ct = default) =>
        UpdateAttemptAsync("account_credentials", credentialId, failedAttemptCount, lockedUntil, lastUsedAt, ct);

    public Task<ProfileCredential?> GetCredentialAsync(Guid profileId, ProfileCredentialKind kind, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<ProfileCredentialRow>(ProfileCredentialSelect +
            " WHERE profile_id = @profileId AND credential_kind = @kind LIMIT 1;", new { profileId, kind = kind.ToString() });
        return Task.FromResult(row is null ? null : Map(row));
    }

    public Task UpsertCredentialAsync(ProfileCredential credential, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        conn.Execute("""
            INSERT INTO profile_credentials
                (id, profile_id, credential_kind, secret_hash, hash_scheme, hash_version, security_stamp,
                 failed_attempt_count, locked_until, created_at, updated_at, last_used_at)
            VALUES (@Id, @ProfileId, @Kind, @SecretHash, @HashScheme, @HashVersion, @SecurityStamp,
                    @FailedAttemptCount, @LockedUntil, @CreatedAt, @UpdatedAt, @LastUsedAt)
            ON CONFLICT(profile_id, credential_kind) DO UPDATE SET
                secret_hash = excluded.secret_hash, hash_scheme = excluded.hash_scheme,
                hash_version = excluded.hash_version, security_stamp = excluded.security_stamp,
                failed_attempt_count = excluded.failed_attempt_count, locked_until = excluded.locked_until,
                updated_at = excluded.updated_at, last_used_at = excluded.last_used_at;
            """, ToParameters(credential));
        return Task.CompletedTask;
    }

    public Task DeleteCredentialAsync(Guid profileId, ProfileCredentialKind kind, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        conn.Execute("DELETE FROM profile_credentials WHERE profile_id = @profileId AND credential_kind = @kind;", new { profileId, kind = kind.ToString() });
        return Task.CompletedTask;
    }

    public Task UpdateCredentialAttemptAsync(Guid credentialId, int failedAttemptCount, DateTimeOffset? lockedUntil, DateTimeOffset? lastUsedAt, CancellationToken ct = default) =>
        UpdateAttemptAsync("profile_credentials", credentialId, failedAttemptCount, lockedUntil, lastUsedAt, ct);

    public Task<bool> IsAdministratorBootstrapCompletedAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        // Account creation is the durable installation boundary. Authentication
        // methods and grants can be revoked or disabled later and must never
        // reopen unauthenticated setup.
        return Task.FromResult(conn.ExecuteScalar<int>(
            "SELECT EXISTS(SELECT 1 FROM accounts LIMIT 1);") == 1);
    }

    public Task InsertSessionAsync(AuthSession session, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        conn.Execute("""
            INSERT INTO auth_sessions
                (id, account_id, active_profile_id, token_hash, device_id, device_name, client,
                 authentication_method, issued_ingress, security_stamp, created_at, last_seen_at, expires_at, authenticated_at, revoked_at, revoked_reason)
            VALUES (@Id, @AccountId, @ActiveProfileId, @TokenHash, @DeviceId, @DeviceName, @Client,
                    @AuthenticationMethod, @IssuedIngress, @SecurityStamp, @CreatedAt, @LastSeenAt, @ExpiresAt, @AuthenticatedAt, @RevokedAt, @RevokedReason);
            """, ToParameters(session));
        return Task.CompletedTask;
    }

    public Task<AuthSession?> GetSessionByTokenHashAsync(string tokenHash, CancellationToken ct = default) => GetSessionAsync("token_hash = @value", tokenHash, ct);
    public Task<AuthSession?> GetSessionByIdAsync(Guid sessionId, CancellationToken ct = default) => GetSessionAsync("id = @value", GuidSql.ToBlob(sessionId), ct);

    public Task<IReadOnlyList<AuthSession>> GetSessionsAsync(Guid accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var rows = conn.Query<SessionRow>(SessionSelect + " WHERE account_id = @accountId ORDER BY last_seen_at DESC;", new { accountId }).AsList();
        return Task.FromResult<IReadOnlyList<AuthSession>>(rows.ConvertAll(Map));
    }

    public Task TouchSessionAsync(Guid sessionId, DateTimeOffset lastSeenAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection();
        conn.Execute("UPDATE auth_sessions SET last_seen_at = @lastSeenAt WHERE id = @sessionId;", new { sessionId, lastSeenAt = lastSeenAt.ToString("O") });
        return Task.CompletedTask;
    }

    public Task<bool> MarkSessionAuthenticatedAsync(Guid sessionId, DateTimeOffset authenticatedAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection();
        return Task.FromResult(conn.Execute("UPDATE auth_sessions SET authenticated_at = @authenticatedAt WHERE id = @sessionId AND revoked_at IS NULL;", new { sessionId, authenticatedAt = authenticatedAt.ToString("O") }) > 0);
    }

    public Task<bool> UpdateActiveProfileAsync(Guid sessionId, Guid activeProfileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection();
        return Task.FromResult(conn.Execute("UPDATE auth_sessions SET active_profile_id = @activeProfileId WHERE id = @sessionId AND revoked_at IS NULL;", new { sessionId, activeProfileId }) > 0);
    }

    public Task<bool> RevokeSessionAsync(Guid sessionId, DateTimeOffset revokedAt, string reason,
        CancellationToken ct = default) => db.ExecuteWriteAsync((connection, transaction, token) =>
    {
        token.ThrowIfCancellationRequested();
        var changed = connection.Execute(
            "UPDATE auth_sessions SET revoked_at=@revokedAt, revoked_reason=@reason WHERE id=@sessionId AND revoked_at IS NULL;",
            new { sessionId, revokedAt = revokedAt.ToString("O"), reason }, transaction);
        if (changed == 1)
        {
            connection.Execute("DELETE FROM grant_admin_unlocks WHERE session_id=@sessionId;",
                new { sessionId }, transaction);
        }

        return changed == 1;
    }, ct);

    public Task<int> RevokeAccountSessionsAsync(Guid accountId, DateTimeOffset revokedAt, string reason,
        Guid? exceptSessionId = null, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
    {
        token.ThrowIfCancellationRequested();
        var affected = connection.Query<Guid>("""
            SELECT id FROM auth_sessions
            WHERE account_id=@accountId AND revoked_at IS NULL
              AND (@exceptSessionId IS NULL OR id<>@exceptSessionId);
            """, new { accountId, exceptSessionId }, transaction).ToArray();
        if (affected.Length == 0)
        {
            return 0;
        }

        var changed = connection.Execute("""
            UPDATE auth_sessions SET revoked_at=@revokedAt, revoked_reason=@reason
            WHERE account_id=@accountId AND revoked_at IS NULL AND (@exceptSessionId IS NULL OR id<>@exceptSessionId);
            """, new { accountId, revokedAt = revokedAt.ToString("O"), reason, exceptSessionId }, transaction);
        connection.Execute("DELETE FROM grant_admin_unlocks WHERE session_id IN @affected;",
            new { affected }, transaction);
        return changed;
    }, ct);

    public Task InsertRecoveryCodesAsync(IReadOnlyList<PasswordRecoveryCode> codes, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); if (codes.Count == 0)
        {
            return Task.CompletedTask;
        }

        return db.ExecuteWriteAsync((conn, tx, _) => conn.Execute("""
            INSERT INTO password_recovery_codes (id, account_id, code_hash, created_at, expires_at, consumed_at)
            VALUES (@Id, @AccountId, @CodeHash, @CreatedAt, @ExpiresAt, @ConsumedAt);
            """, codes.Select(c => new { c.Id, c.AccountId, c.CodeHash, CreatedAt = c.CreatedAt.ToString("O"), ExpiresAt = c.ExpiresAt.ToString("O"), ConsumedAt = c.ConsumedAt?.ToString("O") }), tx), ct);
    }

    public Task<PasswordRecoveryCode?> GetActiveRecoveryCodeAsync(Guid accountId, string codeHash, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<RecoveryRow>("""
            SELECT id AS Id, account_id AS AccountId, code_hash AS CodeHash, created_at AS CreatedAt, expires_at AS ExpiresAt, consumed_at AS ConsumedAt
            FROM password_recovery_codes WHERE account_id=@accountId AND code_hash=@codeHash AND consumed_at IS NULL AND expires_at>@now LIMIT 1;
            """, new { accountId, codeHash, now = now.ToString("O") });
        return Task.FromResult(row is null ? null : Map(row));
    }

    public Task<int> CountActiveRecoveryCodesAsync(Guid accountId, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection();
        return Task.FromResult(conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM password_recovery_codes WHERE account_id=@accountId AND consumed_at IS NULL AND expires_at>@now;",
            new { accountId, now = now.ToString("O") }));
    }

    public Task<bool> ConsumeRecoveryCodeAsync(Guid codeId, DateTimeOffset consumedAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection();
        return Task.FromResult(conn.Execute("UPDATE password_recovery_codes SET consumed_at=@consumedAt WHERE id=@codeId AND consumed_at IS NULL;", new { codeId, consumedAt = consumedAt.ToString("O") }) > 0);
    }

    public Task DeleteRecoveryCodesAsync(Guid accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection(); conn.Execute("DELETE FROM password_recovery_codes WHERE account_id=@accountId;", new { accountId }); return Task.CompletedTask;
    }

    public Task InsertPasswordResetChallengeAsync(PasswordResetChallenge challenge, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection();
        conn.Execute("INSERT INTO password_reset_challenges(id,account_id,token_hash,created_at,expires_at,consumed_at) VALUES(@Id,@AccountId,@TokenHash,@CreatedAt,@ExpiresAt,@ConsumedAt);", new { challenge.Id, challenge.AccountId, challenge.TokenHash, CreatedAt = challenge.CreatedAt.ToString("O"), ExpiresAt = challenge.ExpiresAt.ToString("O"), ConsumedAt = challenge.ConsumedAt?.ToString("O") }); return Task.CompletedTask;
    }

    public Task<PasswordResetChallenge?> GetActivePasswordResetChallengeAsync(string tokenHash, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<PasswordResetRow>("SELECT id AS Id,account_id AS AccountId,token_hash AS TokenHash,created_at AS CreatedAt,expires_at AS ExpiresAt,consumed_at AS ConsumedAt FROM password_reset_challenges WHERE token_hash=@tokenHash AND consumed_at IS NULL AND expires_at>@now LIMIT 1;", new { tokenHash, now = now.ToString("O") });
        return Task.FromResult(row is null ? null : Map(row));
    }

    public Task<bool> ConsumePasswordResetChallengeAsync(Guid challengeId, DateTimeOffset consumedAt, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection(); return Task.FromResult(conn.Execute("UPDATE password_reset_challenges SET consumed_at=@consumedAt WHERE id=@challengeId AND consumed_at IS NULL;", new { challengeId, consumedAt = consumedAt.ToString("O") }) > 0); }

    public Task InvalidatePasswordResetChallengesAsync(Guid accountId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection(); conn.Execute("DELETE FROM password_reset_challenges WHERE account_id=@accountId;", new { accountId }); return Task.CompletedTask; }

    public Task<AccountTwoStep?> GetAccountTwoStepAsync(Guid accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<TwoStepRow>(
            "SELECT account_id AS AccountId, secret_protected AS SecretProtected, created_at AS CreatedAt, enabled_at AS EnabledAt, last_used_step AS LastUsedStep FROM account_two_step WHERE account_id=@accountId LIMIT 1;",
            new { accountId });
        return Task.FromResult(row is null ? null : Map(row));
    }

    public Task<bool> SavePendingAccountTwoStepAsync(Guid accountId, string secretProtected, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        // An enabled key is never replaced by a new setup: the person turns two-step off first.
        var changed = conn.Execute("""
            INSERT INTO account_two_step (account_id, secret_protected, created_at, enabled_at, last_used_step)
            VALUES (@accountId, @secretProtected, @now, NULL, 0)
            ON CONFLICT(account_id) DO UPDATE SET secret_protected = excluded.secret_protected, created_at = excluded.created_at, last_used_step = 0
            WHERE account_two_step.enabled_at IS NULL;
            """, new { accountId, secretProtected, now = now.ToString("O") });
        return Task.FromResult(changed > 0);
    }

    public Task<bool> EnableAccountTwoStepAsync(Guid accountId, long usedStep, DateTimeOffset enabledAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        return Task.FromResult(conn.Execute(
            "UPDATE account_two_step SET enabled_at=@enabledAt, last_used_step=@usedStep WHERE account_id=@accountId AND enabled_at IS NULL;",
            new { accountId, usedStep, enabledAt = enabledAt.ToString("O") }) > 0);
    }

    public Task<bool> AdvanceAccountTwoStepAsync(Guid accountId, long step, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        return Task.FromResult(conn.Execute(
            "UPDATE account_two_step SET last_used_step=@step WHERE account_id=@accountId AND enabled_at IS NOT NULL AND last_used_step<@step;",
            new { accountId, step }) > 0);
    }

    public Task<bool> DeleteAccountTwoStepAsync(Guid accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        return Task.FromResult(conn.Execute("DELETE FROM account_two_step WHERE account_id=@accountId;", new { accountId }) > 0);
    }

    public Task InsertTwoStepChallengeAsync(TwoStepChallenge challenge, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        conn.Execute("""
            INSERT INTO two_step_challenges (id, account_id, token_hash, ingress, device_id, device_name, client, failed_attempts, created_at, expires_at, consumed_at)
            VALUES (@Id, @AccountId, @TokenHash, @Ingress, @DeviceId, @DeviceName, @Client, @FailedAttempts, @CreatedAt, @ExpiresAt, NULL);
            """, new { challenge.Id, challenge.AccountId, challenge.TokenHash, challenge.Ingress, challenge.DeviceId, challenge.DeviceName, challenge.Client, challenge.FailedAttempts, CreatedAt = challenge.CreatedAt.ToString("O"), ExpiresAt = challenge.ExpiresAt.ToString("O") });
        return Task.CompletedTask;
    }

    public Task<TwoStepChallenge?> GetActiveTwoStepChallengeAsync(string tokenHash, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<TwoStepChallengeRow>(
            "SELECT id AS Id, account_id AS AccountId, token_hash AS TokenHash, ingress AS Ingress, device_id AS DeviceId, device_name AS DeviceName, client AS Client, failed_attempts AS FailedAttempts, created_at AS CreatedAt, expires_at AS ExpiresAt, consumed_at AS ConsumedAt FROM two_step_challenges WHERE token_hash=@tokenHash AND consumed_at IS NULL AND expires_at>@now LIMIT 1;",
            new { tokenHash, now = now.ToString("O") });
        return Task.FromResult(row is null ? null : Map(row));
    }

    public Task<bool> ConsumeTwoStepChallengeAsync(Guid challengeId, DateTimeOffset consumedAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        return Task.FromResult(conn.Execute(
            "UPDATE two_step_challenges SET consumed_at=@consumedAt WHERE id=@challengeId AND consumed_at IS NULL;",
            new { challengeId, consumedAt = consumedAt.ToString("O") }) > 0);
    }

    public Task<int> RecordTwoStepChallengeFailureAsync(Guid challengeId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        conn.Execute("UPDATE two_step_challenges SET failed_attempts=failed_attempts+1 WHERE id=@challengeId;", new { challengeId });
        return Task.FromResult(conn.ExecuteScalar<int>("SELECT failed_attempts FROM two_step_challenges WHERE id=@challengeId;", new { challengeId }));
    }

    public Task InvalidateTwoStepChallengesAsync(Guid accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        conn.Execute("DELETE FROM two_step_challenges WHERE account_id=@accountId;", new { accountId });
        return Task.CompletedTask;
    }

    public Task<ServiceCredential?> GetActiveServiceCredentialAsync(string purpose, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection(); var row = conn.QueryFirstOrDefault<ServiceRow>(ServiceSelect + " WHERE purpose=@purpose AND revoked_at IS NULL LIMIT 1;", new { purpose }); return Task.FromResult(row is null ? null : Map(row)); }
    public Task<ServiceCredential?> GetServiceCredentialByHashAsync(string tokenHash, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection(); var row = conn.QueryFirstOrDefault<ServiceRow>(ServiceSelect + " WHERE token_hash=@tokenHash AND revoked_at IS NULL LIMIT 1;", new { tokenHash }); return Task.FromResult(row is null ? null : Map(row)); }
    public Task InsertServiceCredentialAsync(ServiceCredential c, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection(); conn.Execute("INSERT INTO service_credentials(id,purpose,key_id,token_hash,created_at,last_used_at,revoked_at) VALUES(@Id,@Purpose,@KeyId,@TokenHash,@CreatedAt,@LastUsedAt,@RevokedAt);", new { c.Id, c.Purpose, c.KeyId, c.TokenHash, CreatedAt = c.CreatedAt.ToString("O"), LastUsedAt = c.LastUsedAt?.ToString("O"), RevokedAt = c.RevokedAt?.ToString("O") }); return Task.CompletedTask; }
    public Task RevokeServiceCredentialsAsync(string purpose, DateTimeOffset revokedAt, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection(); conn.Execute("UPDATE service_credentials SET revoked_at=@revokedAt WHERE purpose=@purpose AND revoked_at IS NULL;", new { purpose, revokedAt = revokedAt.ToString("O") }); return Task.CompletedTask; }

    public Task WriteAuditEventAsync(Guid? accountId, Guid? profileId, Guid? sessionId, string eventType, bool succeeded, string? detail, DateTimeOffset occurredAt, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection(); conn.Execute("INSERT INTO identity_audit_events(account_id,profile_id,session_id,event_type,succeeded,detail,occurred_at) VALUES(@accountId,@profileId,@sessionId,@eventType,@succeeded,@detail,@occurredAt);", new { accountId, profileId, sessionId, eventType, succeeded = succeeded ? 1 : 0, detail, occurredAt = occurredAt.ToString("O") }); return Task.CompletedTask; }

    private Task UpdateAttemptAsync(string table, Guid id, int count, DateTimeOffset? locked, DateTimeOffset? used, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection(); conn.Execute($"UPDATE {table} SET failed_attempt_count=@count, locked_until=@locked, last_used_at=COALESCE(@used,last_used_at), updated_at=@updated WHERE id=@id;", new { id, count, locked = locked?.ToString("O"), used = used?.ToString("O"), updated = DateTimeOffset.UtcNow.ToString("O") }); return Task.CompletedTask; }
    private Task<AuthSession?> GetSessionAsync(string predicate, object value, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection(); var row = conn.QueryFirstOrDefault<SessionRow>(SessionSelect + $" WHERE {predicate} LIMIT 1;", new { value }); return Task.FromResult(row is null ? null : Map(row)); }

    private const string AccountCredentialSelect = "SELECT id AS Id,account_id AS AccountId,credential_kind AS Kind,secret_hash AS SecretHash,hash_scheme AS HashScheme,hash_version AS HashVersion,security_stamp AS SecurityStamp,failed_attempt_count AS FailedAttemptCount,locked_until AS LockedUntil,created_at AS CreatedAt,updated_at AS UpdatedAt,last_used_at AS LastUsedAt FROM account_credentials";
    private const string ProfileCredentialSelect = "SELECT id AS Id,profile_id AS ProfileId,credential_kind AS Kind,secret_hash AS SecretHash,hash_scheme AS HashScheme,hash_version AS HashVersion,security_stamp AS SecurityStamp,failed_attempt_count AS FailedAttemptCount,locked_until AS LockedUntil,created_at AS CreatedAt,updated_at AS UpdatedAt,last_used_at AS LastUsedAt FROM profile_credentials";
    private const string SessionSelect = "SELECT id AS Id,account_id AS AccountId,active_profile_id AS ActiveProfileId,token_hash AS TokenHash,device_id AS DeviceId,device_name AS DeviceName,client AS Client,authentication_method AS AuthenticationMethod,issued_ingress AS IssuedIngress,security_stamp AS SecurityStamp,created_at AS CreatedAt,last_seen_at AS LastSeenAt,expires_at AS ExpiresAt,authenticated_at AS AuthenticatedAt,revoked_at AS RevokedAt,revoked_reason AS RevokedReason FROM auth_sessions";
    private const string ServiceSelect = "SELECT id AS Id,purpose AS Purpose,key_id AS KeyId,token_hash AS TokenHash,created_at AS CreatedAt,last_used_at AS LastUsedAt,revoked_at AS RevokedAt FROM service_credentials";

    private static object ToParameters(AccountCredential c) => new { c.Id, c.AccountId, Kind = c.Kind.ToString(), c.SecretHash, c.HashScheme, c.HashVersion, c.SecurityStamp, c.FailedAttemptCount, LockedUntil = c.LockedUntil?.ToString("O"), CreatedAt = c.CreatedAt.ToString("O"), UpdatedAt = c.UpdatedAt.ToString("O"), LastUsedAt = c.LastUsedAt?.ToString("O") };
    private static object ToParameters(ProfileCredential c) => new { c.Id, c.ProfileId, Kind = c.Kind.ToString(), c.SecretHash, c.HashScheme, c.HashVersion, c.SecurityStamp, c.FailedAttemptCount, LockedUntil = c.LockedUntil?.ToString("O"), CreatedAt = c.CreatedAt.ToString("O"), UpdatedAt = c.UpdatedAt.ToString("O"), LastUsedAt = c.LastUsedAt?.ToString("O") };
    private static object ToParameters(AuthSession s) => new { s.Id, s.AccountId, s.ActiveProfileId, s.TokenHash, s.DeviceId, s.DeviceName, s.Client, s.AuthenticationMethod, s.IssuedIngress, s.SecurityStamp, CreatedAt = s.CreatedAt.ToString("O"), LastSeenAt = s.LastSeenAt.ToString("O"), ExpiresAt = s.ExpiresAt.ToString("O"), AuthenticatedAt = s.AuthenticatedAt.ToString("O"), RevokedAt = s.RevokedAt?.ToString("O"), s.RevokedReason };
    private static AccountCredential Map(AccountCredentialRow r) => new() { Id = r.Id, AccountId = r.AccountId, Kind = Enum.Parse<AccountCredentialKind>(r.Kind), SecretHash = r.SecretHash, HashScheme = r.HashScheme, HashVersion = r.HashVersion, SecurityStamp = r.SecurityStamp, FailedAttemptCount = r.FailedAttemptCount, LockedUntil = ParseNullable(r.LockedUntil), CreatedAt = DateTimeOffset.Parse(r.CreatedAt), UpdatedAt = DateTimeOffset.Parse(r.UpdatedAt), LastUsedAt = ParseNullable(r.LastUsedAt) };
    private static ProfileCredential Map(ProfileCredentialRow r) => new() { Id = r.Id, ProfileId = r.ProfileId, Kind = Enum.Parse<ProfileCredentialKind>(r.Kind), SecretHash = r.SecretHash, HashScheme = r.HashScheme, HashVersion = r.HashVersion, SecurityStamp = r.SecurityStamp, FailedAttemptCount = r.FailedAttemptCount, LockedUntil = ParseNullable(r.LockedUntil), CreatedAt = DateTimeOffset.Parse(r.CreatedAt), UpdatedAt = DateTimeOffset.Parse(r.UpdatedAt), LastUsedAt = ParseNullable(r.LastUsedAt) };
    private static AuthSession Map(SessionRow r) => new() { Id = r.Id, AccountId = r.AccountId, ActiveProfileId = r.ActiveProfileId, TokenHash = r.TokenHash, DeviceId = r.DeviceId, DeviceName = r.DeviceName, Client = r.Client, AuthenticationMethod = r.AuthenticationMethod, IssuedIngress = r.IssuedIngress, SecurityStamp = r.SecurityStamp, CreatedAt = DateTimeOffset.Parse(r.CreatedAt), LastSeenAt = DateTimeOffset.Parse(r.LastSeenAt), ExpiresAt = DateTimeOffset.Parse(r.ExpiresAt), AuthenticatedAt = ParseNullable(r.AuthenticatedAt) ?? DateTimeOffset.Parse(r.CreatedAt), RevokedAt = ParseNullable(r.RevokedAt), RevokedReason = r.RevokedReason };
    private static PasswordRecoveryCode Map(RecoveryRow r) => new() { Id = r.Id, AccountId = r.AccountId, CodeHash = r.CodeHash, CreatedAt = DateTimeOffset.Parse(r.CreatedAt), ExpiresAt = DateTimeOffset.Parse(r.ExpiresAt), ConsumedAt = ParseNullable(r.ConsumedAt) };
    private static PasswordResetChallenge Map(PasswordResetRow r) => new() { Id = r.Id, AccountId = r.AccountId, TokenHash = r.TokenHash, CreatedAt = DateTimeOffset.Parse(r.CreatedAt), ExpiresAt = DateTimeOffset.Parse(r.ExpiresAt), ConsumedAt = ParseNullable(r.ConsumedAt) };
    private static AccountTwoStep Map(TwoStepRow r) => new() { AccountId = r.AccountId, SecretProtected = r.SecretProtected, CreatedAt = DateTimeOffset.Parse(r.CreatedAt), EnabledAt = ParseNullable(r.EnabledAt), LastUsedStep = r.LastUsedStep };
    private static TwoStepChallenge Map(TwoStepChallengeRow r) => new() { Id = r.Id, AccountId = r.AccountId, TokenHash = r.TokenHash, Ingress = r.Ingress, DeviceId = r.DeviceId, DeviceName = r.DeviceName, Client = r.Client, FailedAttempts = r.FailedAttempts, CreatedAt = DateTimeOffset.Parse(r.CreatedAt), ExpiresAt = DateTimeOffset.Parse(r.ExpiresAt), ConsumedAt = ParseNullable(r.ConsumedAt) };
    private static ServiceCredential Map(ServiceRow r) => new() { Id = r.Id, Purpose = r.Purpose, KeyId = r.KeyId, TokenHash = r.TokenHash, CreatedAt = DateTimeOffset.Parse(r.CreatedAt), LastUsedAt = ParseNullable(r.LastUsedAt), RevokedAt = ParseNullable(r.RevokedAt) };
    private static DateTimeOffset? ParseNullable(string? v) => string.IsNullOrWhiteSpace(v) ? null : DateTimeOffset.Parse(v);

    private sealed class AccountCredentialRow { public Guid Id { get; set; } public Guid AccountId { get; set; } public string Kind { get; set; } = ""; public string SecretHash { get; set; } = ""; public string HashScheme { get; set; } = ""; public int HashVersion { get; set; } public string SecurityStamp { get; set; } = ""; public int FailedAttemptCount { get; set; } public string? LockedUntil { get; set; } public string CreatedAt { get; set; } = ""; public string UpdatedAt { get; set; } = ""; public string? LastUsedAt { get; set; } }
    private sealed class ProfileCredentialRow { public Guid Id { get; set; } public Guid ProfileId { get; set; } public string Kind { get; set; } = ""; public string SecretHash { get; set; } = ""; public string HashScheme { get; set; } = ""; public int HashVersion { get; set; } public string SecurityStamp { get; set; } = ""; public int FailedAttemptCount { get; set; } public string? LockedUntil { get; set; } public string CreatedAt { get; set; } = ""; public string UpdatedAt { get; set; } = ""; public string? LastUsedAt { get; set; } }
    private sealed class SessionRow { public Guid Id { get; set; } public Guid AccountId { get; set; } public Guid ActiveProfileId { get; set; } public string TokenHash { get; set; } = ""; public string DeviceId { get; set; } = ""; public string DeviceName { get; set; } = ""; public string Client { get; set; } = ""; public string AuthenticationMethod { get; set; } = ""; public string IssuedIngress { get; set; } = "home_network"; public string SecurityStamp { get; set; } = ""; public string CreatedAt { get; set; } = ""; public string LastSeenAt { get; set; } = ""; public string ExpiresAt { get; set; } = ""; public string? AuthenticatedAt { get; set; } public string? RevokedAt { get; set; } public string? RevokedReason { get; set; } }
    private sealed class RecoveryRow { public Guid Id { get; set; } public Guid AccountId { get; set; } public string CodeHash { get; set; } = ""; public string CreatedAt { get; set; } = ""; public string ExpiresAt { get; set; } = ""; public string? ConsumedAt { get; set; } }
    private sealed class PasswordResetRow { public Guid Id { get; set; } public Guid AccountId { get; set; } public string TokenHash { get; set; } = ""; public string CreatedAt { get; set; } = ""; public string ExpiresAt { get; set; } = ""; public string? ConsumedAt { get; set; } }
    private sealed class TwoStepRow { public Guid AccountId { get; set; } public string SecretProtected { get; set; } = ""; public string CreatedAt { get; set; } = ""; public string? EnabledAt { get; set; } public long LastUsedStep { get; set; } }
    private sealed class TwoStepChallengeRow { public Guid Id { get; set; } public Guid AccountId { get; set; } public string TokenHash { get; set; } = ""; public string Ingress { get; set; } = ""; public string DeviceId { get; set; } = ""; public string DeviceName { get; set; } = ""; public string Client { get; set; } = ""; public int FailedAttempts { get; set; } public string CreatedAt { get; set; } = ""; public string ExpiresAt { get; set; } = ""; public string? ConsumedAt { get; set; } }
    private sealed class ServiceRow { public Guid Id { get; set; } public string Purpose { get; set; } = ""; public string KeyId { get; set; } = ""; public string TokenHash { get; set; } = ""; public string CreatedAt { get; set; } = ""; public string? LastUsedAt { get; set; } public string? RevokedAt { get; set; } }
}
