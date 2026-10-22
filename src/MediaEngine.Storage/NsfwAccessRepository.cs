using System.Globalization;
using Dapper;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
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
                   EXISTS(SELECT 1 FROM profile_credentials c WHERE c.profile_id = p.id AND c.credential_kind = 'ProfilePin') AS HasPin
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
                throw new InvalidOperationException("A Kids or content-limited profile cannot have Vault access.");
            }

            // One PIN: asking for it to open Vault mode only makes sense when the profile has one.
            if (mode == NsfwAccessMode.Pin
                && connection.ExecuteScalar<int>(
                    "SELECT EXISTS(SELECT 1 FROM profile_credentials WHERE profile_id = @profileId AND credential_kind = 'ProfilePin');",
                    new { profileId }, transaction) == 0)
            {
                throw new InvalidOperationException("Set a PIN on this profile first.");
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
                  AND (s.access_mode = 'allowed'
                       OR (s.access_mode = 'pin' AND u.method = 'pin'
                           AND EXISTS(SELECT 1 FROM profile_credentials c WHERE c.profile_id = u.profile_id AND c.credential_kind = 'ProfilePin'))));
            """,
            new { sessionId, profileId, now = Iso(now) });
        return Task.FromResult(unlocked == 1);
    }

    public Task UnlockAsync(Guid sessionId, Guid profileId, NsfwUnlockMethod method, int idleLockMinutes, DateTimeOffset now, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            // Only a profile that can use Vault mode, by the way it is set up now, on its own live session, can be unlocked.
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
                  AND (s.access_mode = 'allowed'
                       OR (s.access_mode = 'pin' AND @method = 'pin'
                           AND EXISTS(SELECT 1 FROM profile_credentials c WHERE c.profile_id = p.id AND c.credential_kind = 'ProfilePin')))
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
                throw new InvalidOperationException("Vault mode is not available for this profile.");
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

    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed class AccessRow
    {
        public string Role { get; set; } = string.Empty;
        public string? ContentLimit { get; set; }
        public string? AccessMode { get; set; }
        public int? IdleLockMinutes { get; set; }
        public bool HasPin { get; set; }
    }
}
