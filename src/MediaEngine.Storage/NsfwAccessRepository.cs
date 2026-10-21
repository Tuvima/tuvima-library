using System.Globalization;
using Dapper;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="INsfwAccessRepository"/>. Every check reads the profile's role, content limit and mode
/// live, so a parent's change applies on the very next request.
/// </summary>
public sealed class NsfwAccessRepository(IDatabaseConnection database) : INsfwAccessRepository
{
    public Task<ProfileNsfwAccess> GetAsync(Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QueryFirstOrDefault<AccessRow>(
            """
            SELECT p.role AS Role, p.content_limit AS ContentLimit, s.access_mode AS AccessMode, s.idle_lock_minutes AS IdleLockMinutes,
                   EXISTS(SELECT 1 FROM profile_nsfw_pins n WHERE n.profile_id = p.id) AS HasPin
            FROM profiles p
            LEFT JOIN profile_nsfw_settings s ON s.profile_id = p.id
            WHERE p.id = @profileId;
            """,
            new { profileId });
        if (row is null)
        {
            return Task.FromResult(ProfileNsfwAccess.None(profileId));
        }

        var configured = row.AccessMode is null ? NsfwAccessMode.Off : NsfwAccessRules.Parse(row.AccessMode);
        var role = Enum.Parse<ProfileRole>(row.Role);
        return Task.FromResult(new ProfileNsfwAccess(
            profileId,
            configured,
            NsfwAccessRules.Effective(configured, role, row.ContentLimit),
            row.IdleLockMinutes,
            row.HasPin));
    }

    public Task SetAsync(Guid profileId, NsfwAccessMode mode, int? idleLockMinutes, Guid? changedByProfileId, DateTimeOffset now, CancellationToken ct = default)
    {
        NsfwAccessRules.ValidateIdleLockMinutes(mode, idleLockMinutes);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var profile = connection.QueryFirstOrDefault<AccessRow>(
                "SELECT role AS Role, content_limit AS ContentLimit FROM profiles WHERE id = @profileId;",
                new { profileId }, transaction)
                ?? throw new KeyNotFoundException("Profile not found.");
            if (mode != NsfwAccessMode.Off && !NsfwAccessRules.IsEligible(Enum.Parse<ProfileRole>(profile.Role), profile.ContentLimit))
            {
                throw new InvalidOperationException("A Kids or content-limited profile cannot have NSFW access.");
            }

            connection.Execute(
                """
                INSERT INTO profile_nsfw_settings (profile_id, access_mode, idle_lock_minutes, changed_by_profile_id, updated_at)
                VALUES (@profileId, @mode, @idleLockMinutes, @changedByProfileId, @updated)
                ON CONFLICT(profile_id) DO UPDATE SET
                    access_mode = excluded.access_mode,
                    idle_lock_minutes = excluded.idle_lock_minutes,
                    changed_by_profile_id = excluded.changed_by_profile_id,
                    updated_at = excluded.updated_at;
                """,
                new { profileId, mode = NsfwAccessRules.ToStored(mode), idleLockMinutes, changedByProfileId, updated = Iso(now) }, transaction);
        }, ct);
    }

    public Task<bool> IsUnlockedAsync(Guid sessionId, Guid profileId, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (sessionId == Guid.Empty || profileId == Guid.Empty)
        {
            return Task.FromResult(false);
        }

        using var connection = database.CreateConnection();
        // One statement checks everything live: the unlock exists for this session and this profile, it has not run out,
        // the profile is still eligible, and the mode still allows the way it was unlocked.
        var unlocked = connection.ExecuteScalar<int>(
            """
            SELECT EXISTS(
                SELECT 1
                FROM nsfw_session_unlocks u
                JOIN profiles p ON p.id = u.profile_id
                JOIN auth_sessions a ON a.id = u.session_id AND a.active_profile_id = u.profile_id AND a.revoked_at IS NULL
                JOIN profile_nsfw_settings s ON s.profile_id = u.profile_id
                WHERE u.session_id = @sessionId
                  AND u.profile_id = @profileId
                  AND (u.expires_at IS NULL OR u.expires_at > @now)
                  AND p.role <> 'RestrictedProfile'
                  AND p.content_limit IS NULL
                  AND (s.access_mode = 'allowed' OR (s.access_mode = 'pin' AND u.method = 'pin')));
            """,
            new { sessionId, profileId, now = Iso(now) });
        return Task.FromResult(unlocked == 1);
    }

    public Task UnlockAsync(Guid sessionId, Guid profileId, NsfwUnlockMethod method, int idleLockMinutes, DateTimeOffset now, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            // Only a profile that can use NSFW mode, by the way it is set up now, on its own live session, can be unlocked.
            var written = connection.Execute(
                """
                INSERT INTO nsfw_session_unlocks (session_id, profile_id, method, unlocked_at, last_activity_at, expires_at)
                SELECT @sessionId, @profileId, @method, @now, @now, @expires
                FROM profiles p
                JOIN profile_nsfw_settings s ON s.profile_id = p.id
                JOIN auth_sessions a ON a.id = @sessionId AND a.active_profile_id = p.id AND a.revoked_at IS NULL
                WHERE p.id = @profileId
                  AND p.role <> 'RestrictedProfile'
                  AND p.content_limit IS NULL
                  AND (s.access_mode = 'allowed' OR (s.access_mode = 'pin' AND @method = 'pin'))
                ON CONFLICT(session_id) DO UPDATE SET
                    profile_id = excluded.profile_id, method = excluded.method, unlocked_at = excluded.unlocked_at,
                    last_activity_at = excluded.last_activity_at, expires_at = excluded.expires_at;
                """,
                new
                {
                    sessionId,
                    profileId,
                    method = method == NsfwUnlockMethod.Pin ? "pin" : "allowed",
                    now = Iso(now),
                    expires = idleLockMinutes <= 0 ? null : Iso(now.AddMinutes(idleLockMinutes)),
                }, transaction);
            if (written == 0)
            {
                throw new InvalidOperationException("NSFW mode is not available for this profile.");
            }
        }, ct);

    public Task<bool> TouchAsync(Guid sessionId, Guid profileId, int idleLockMinutes, DateTimeOffset now, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute(
                """
                UPDATE nsfw_session_unlocks
                SET last_activity_at = @now, expires_at = @expires
                WHERE session_id = @sessionId AND profile_id = @profileId AND (expires_at IS NULL OR expires_at > @now);
                """,
                new
                {
                    sessionId,
                    profileId,
                    now = Iso(now),
                    expires = idleLockMinutes <= 0 ? null : Iso(now.AddMinutes(idleLockMinutes)),
                }, transaction) == 1;
        }, ct);

    public Task LockSessionAsync(Guid sessionId, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("DELETE FROM nsfw_session_unlocks WHERE session_id = @sessionId;", new { sessionId }, transaction);
        }, ct);

    public Task LockProfileAsync(Guid profileId, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("DELETE FROM nsfw_session_unlocks WHERE profile_id = @profileId;", new { profileId }, transaction);
        }, ct);

    public Task<ProfileCredential?> GetPinAsync(Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QueryFirstOrDefault<PinRow>(
            """
            SELECT id AS Id, profile_id AS ProfileId, secret_hash AS SecretHash, hash_scheme AS HashScheme, hash_version AS HashVersion,
                   security_stamp AS SecurityStamp, failed_attempt_count AS FailedAttemptCount, locked_until AS LockedUntil,
                   created_at AS CreatedAt, updated_at AS UpdatedAt, last_used_at AS LastUsedAt
            FROM profile_nsfw_pins WHERE profile_id = @profileId LIMIT 1;
            """,
            new { profileId });
        return Task.FromResult(row is null ? null : new ProfileCredential
        {
            Id = row.Id,
            ProfileId = row.ProfileId,
            Kind = ProfileCredentialKind.NsfwPin,
            SecretHash = row.SecretHash,
            HashScheme = row.HashScheme,
            HashVersion = row.HashVersion,
            SecurityStamp = row.SecurityStamp,
            FailedAttemptCount = row.FailedAttemptCount,
            LockedUntil = ParseNullable(row.LockedUntil),
            CreatedAt = DateTimeOffset.Parse(row.CreatedAt, CultureInfo.InvariantCulture),
            UpdatedAt = DateTimeOffset.Parse(row.UpdatedAt, CultureInfo.InvariantCulture),
            LastUsedAt = ParseNullable(row.LastUsedAt),
        });
    }

    public Task UpsertPinAsync(ProfileCredential credential, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute(
                """
                INSERT INTO profile_nsfw_pins
                    (id, profile_id, secret_hash, hash_scheme, hash_version, security_stamp, failed_attempt_count, locked_until, created_at, updated_at, last_used_at)
                VALUES (@Id, @ProfileId, @SecretHash, @HashScheme, @HashVersion, @SecurityStamp, @FailedAttemptCount, @LockedUntil, @CreatedAt, @UpdatedAt, @LastUsedAt)
                ON CONFLICT(profile_id) DO UPDATE SET
                    secret_hash = excluded.secret_hash, hash_scheme = excluded.hash_scheme, hash_version = excluded.hash_version,
                    security_stamp = excluded.security_stamp, failed_attempt_count = excluded.failed_attempt_count,
                    locked_until = excluded.locked_until, updated_at = excluded.updated_at, last_used_at = excluded.last_used_at;
                """,
                new
                {
                    credential.Id,
                    credential.ProfileId,
                    credential.SecretHash,
                    credential.HashScheme,
                    credential.HashVersion,
                    credential.SecurityStamp,
                    credential.FailedAttemptCount,
                    LockedUntil = credential.LockedUntil is { } locked ? Iso(locked) : null,
                    CreatedAt = Iso(credential.CreatedAt),
                    UpdatedAt = Iso(credential.UpdatedAt),
                    LastUsedAt = credential.LastUsedAt is { } used ? Iso(used) : null,
                }, transaction);
        }, ct);
    }

    public Task<bool> DeletePinAsync(Guid profileId, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute("DELETE FROM profile_nsfw_pins WHERE profile_id = @profileId;", new { profileId }, transaction) > 0;
        }, ct);

    public Task<int?> ReservePinAttemptAsync(Guid credentialId, int lockoutThreshold, DateTimeOffset now, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            // One statement decides everything (same shape as the profile PIN): a running lock or a full set of outstanding
            // attempts matches no row, so parallel guesses can never exceed the limit.
            return connection.ExecuteScalar<int?>(
                """
                UPDATE profile_nsfw_pins
                SET failed_attempt_count = (CASE WHEN locked_until IS NULL THEN failed_attempt_count ELSE 0 END) + 1,
                    locked_until = NULL,
                    updated_at = @now
                WHERE id = @credentialId
                  AND (locked_until IS NULL OR locked_until <= @now)
                  AND (CASE WHEN locked_until IS NULL THEN failed_attempt_count ELSE 0 END) < @lockoutThreshold
                RETURNING failed_attempt_count;
                """,
                new { credentialId, lockoutThreshold, now = Iso(now) }, transaction);
        }, ct);

    public Task LockExhaustedPinAsync(Guid credentialId, int lockoutThreshold, DateTimeOffset lockedUntil, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute(
                """
                UPDATE profile_nsfw_pins
                SET locked_until = @lockedUntil, updated_at = @updated
                WHERE id = @credentialId AND locked_until IS NULL AND failed_attempt_count >= @lockoutThreshold;
                """,
                new { credentialId, lockoutThreshold, lockedUntil = Iso(lockedUntil), updated = Iso(DateTimeOffset.UtcNow) }, transaction);
        }, ct);

    public Task<bool> ResetPinAttemptsIfUnlockedAsync(Guid credentialId, DateTimeOffset now, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute(
                """
                UPDATE profile_nsfw_pins
                SET failed_attempt_count = 0, locked_until = NULL, last_used_at = @now, updated_at = @now
                WHERE id = @credentialId AND (locked_until IS NULL OR locked_until <= @now);
                """,
                new { credentialId, now = Iso(now) }, transaction) == 1;
        }, ct);

    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private sealed class AccessRow
    {
        public string Role { get; set; } = string.Empty;
        public string? ContentLimit { get; set; }
        public string? AccessMode { get; set; }
        public int? IdleLockMinutes { get; set; }
        public bool HasPin { get; set; }
    }

    private sealed class PinRow
    {
        public Guid Id { get; set; }
        public Guid ProfileId { get; set; }
        public string SecretHash { get; set; } = string.Empty;
        public string HashScheme { get; set; } = string.Empty;
        public int HashVersion { get; set; }
        public string SecurityStamp { get; set; } = string.Empty;
        public int FailedAttemptCount { get; set; }
        public string? LockedUntil { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
        public string? LastUsedAt { get; set; }
    }
}
