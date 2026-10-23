using System.Collections.Frozen;
using System.Data;
using Dapper;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IProfileAccessRepository"/>: the access a profile holds (users, profiles and households
/// spec). Every change to a profile's access raises <c>profiles.authorization_version</c> in the same transaction.
/// </summary>
public sealed class ProfileAccessRepository(IDatabaseConnection db) : IProfileAccessRepository
{
    public Task<IReadOnlySet<AccountFeatureId>> GetFeatureGrantsAsync(Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var features = conn.Query<string>("SELECT feature_id FROM profile_feature_grants WHERE profile_id=@profileId;", new { profileId })
            .Select(value => new AccountFeatureId(value)).ToFrozenSet();
        return Task.FromResult<IReadOnlySet<AccountFeatureId>>(features);
    }

    public Task<IReadOnlySet<Guid>> GetLibraryGrantsAsync(Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var libraries = conn.Query<Guid>("SELECT library_id FROM profile_library_grants WHERE profile_id=@profileId;", new { profileId })
            .ToFrozenSet();
        return Task.FromResult<IReadOnlySet<Guid>>(libraries);
    }

    public Task<bool> ReplaceAccessAsync(
        Guid profileId, IReadOnlySet<AccountFeatureId> features, IReadOnlySet<Guid> libraries, DateTimeOffset changedAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(libraries);
        return db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!ProfileExists(conn, tx, profileId))
            {
                return false;
            }

            // The Vault opens only through Vault mode, never through a library grant (owner decision 2026-10-22).
            var vaultIds = conn.Query<string>("SELECT library_id FROM vault_libraries;", transaction: tx)
                .Select(text => Guid.TryParse(text, out var parsed) ? parsed : Guid.Empty)
                .ToHashSet();
            if (libraries.Any(vaultIds.Contains))
            {
                throw new ArgumentException("The Vault can't be given to a profile as a library. It opens only through Vault mode.");
            }

            var at = Iso(changedAt);
            conn.Execute("DELETE FROM profile_feature_grants WHERE profile_id=@profileId;", new { profileId }, tx);
            conn.Execute("DELETE FROM profile_library_grants WHERE profile_id=@profileId;", new { profileId }, tx);
            foreach (var feature in features)
            {
                conn.Execute("INSERT INTO profile_feature_grants(profile_id,feature_id,granted_at) VALUES(@profileId,@feature,@at);",
                    new { profileId, feature = feature.Value, at }, tx);
            }

            foreach (var libraryId in libraries)
            {
                conn.Execute("INSERT INTO profile_library_grants(profile_id,library_id,granted_at) VALUES(@profileId,@libraryId,@at);",
                    new { profileId, libraryId, at }, tx);
            }

            BumpAuthorizationVersion(conn, tx, profileId);
            return true;
        }, ct);
    }

    public Task<ProfileAdminFlags?> GetAdminFlagsAsync(Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<FlagsRow>(
            "SELECT is_server_admin AS IsServerAdministrator, is_household_admin AS IsHouseholdAdministrator, authorization_version AS AuthorizationVersion FROM profiles WHERE id=@profileId;",
            new { profileId });
        return Task.FromResult(row is null ? null : new ProfileAdminFlags(row.IsServerAdministrator, row.IsHouseholdAdministrator, row.AuthorizationVersion));
    }

    public Task<bool> SetAdminFlagsAsync(
        Guid profileId, bool isServerAdministrator, bool isHouseholdAdministrator, DateTimeOffset changedAt, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            return conn.Execute("""
                UPDATE profiles
                SET is_server_admin=@server, is_household_admin=@household, authorization_version=authorization_version+1
                WHERE id=@profileId;
                """, new { profileId, server = isServerAdministrator ? 1 : 0, household = isHouseholdAdministrator ? 1 : 0 }, tx) > 0;
        }, ct);

    public Task<bool> SetServerAdministratorAsync(Guid profileId, bool isServerAdministrator, DateTimeOffset changedAt, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var current = conn.QueryFirstOrDefault<int?>("SELECT is_server_admin FROM profiles WHERE id=@profileId;", new { profileId }, tx);
            if (current is null)
            {
                return false;
            }

            if ((current == 1) == isServerAdministrator)
            {
                return true;
            }

            if (!isServerAdministrator && !HasAnotherUsableServerAdministrator(conn, tx, profileId))
            {
                throw new InvalidOperationException("The final effective administrator cannot be demoted.");
            }

            if (isServerAdministrator)
            {
                RequireInDefaultHousehold(conn, tx, profileId);
            }

            conn.Execute("UPDATE profiles SET is_server_admin=@value, authorization_version=authorization_version+1 WHERE id=@profileId;",
                new { profileId, value = isServerAdministrator ? 1 : 0 }, tx);
            if (isServerAdministrator
                && conn.QueryFirstOrDefault<Guid?>("SELECT household_id FROM profiles WHERE id=@profileId;", new { profileId }, tx) is { } householdId)
            {
                MarkDefaultHouseholdIfNone(conn, tx, householdId);
            }

            return true;
        }, ct);

    public Task<bool> SetHouseholdAdministratorAsync(Guid profileId, bool isHouseholdAdministrator, DateTimeOffset changedAt, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var current = conn.QueryFirstOrDefault<int?>("SELECT is_household_admin FROM profiles WHERE id=@profileId;", new { profileId }, tx);
            if (current is null)
            {
                return false;
            }

            if ((current == 1) == isHouseholdAdministrator)
            {
                return true;
            }

            if (isHouseholdAdministrator && conn.ExecuteScalar<int>("SELECT COUNT(*) FROM accounts WHERE profile_id=@profileId;", new { profileId }, tx) == 0)
            {
                throw new InvalidOperationException("A household administrator needs a sign-in. Attach one to this profile first.");
            }

            conn.Execute("UPDATE profiles SET is_household_admin=@value, authorization_version=authorization_version+1 WHERE id=@profileId;",
                new { profileId, value = isHouseholdAdministrator ? 1 : 0 }, tx);
            return true;
        }, ct);

    /// <summary>
    /// True when a server administrator profile other than <paramref name="profileId"/> has an enabled user that can sign in (a
    /// password, a passkey or an outside sign-in). The last-administrator backstop for every way a server administrator can be lost.
    /// </summary>
    internal static bool HasAnotherUsableServerAdministrator(IDbConnection conn, IDbTransaction tx, Guid profileId) =>
        conn.ExecuteScalar<int>(UsableServerAdministratorSql + " AND p.id<>@profileId;", new { profileId }, tx) > 0;

    /// <summary>True when this profile is a server administrator with an enabled user that can sign in.</summary>
    internal static bool IsUsableServerAdministrator(IDbConnection conn, IDbTransaction tx, Guid profileId) =>
        conn.ExecuteScalar<int>(UsableServerAdministratorSql + " AND p.id=@profileId;", new { profileId }, tx) > 0;

    /// <summary>
    /// True when a server administrator profile has an enabled user that can sign in, leaving out the user
    /// <paramref name="accountId"/> (whatever profile it is attached to).
    /// </summary>
    internal static bool HasUsableServerAdministratorWithoutAccount(IDbConnection conn, IDbTransaction tx, Guid accountId) =>
        conn.ExecuteScalar<int>(UsableServerAdministratorSql + " AND a.id<>@accountId;", new { accountId }, tx) > 0;

    private const string UsableServerAdministratorSql = """
        SELECT COUNT(*)
        FROM profiles p
        JOIN accounts a ON a.profile_id=p.id
        WHERE p.is_server_admin=1 AND a.is_enabled=1
          AND (
            EXISTS(SELECT 1 FROM account_credentials c WHERE c.account_id=a.id)
            OR EXISTS(SELECT 1 FROM account_passkeys k WHERE k.account_id=a.id)
            OR EXISTS(SELECT 1 FROM account_external_logins e WHERE e.account_id=a.id)
          )
        """;

    public Task<ProfileAdminProtection?> GetAdminProtectionAsync(Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<ProtectionRow>("""
            SELECT profile_id AS ProfileId, is_enabled AS IsEnabled, unlock_mode AS UnlockMode, unlock_minutes AS UnlockMinutes,
                   protection_version AS ProtectionVersion, updated_at AS UpdatedAt
            FROM profile_admin_protections WHERE profile_id=@profileId;
            """, new { profileId });
        return Task.FromResult(row is null ? null : new ProfileAdminProtection
        {
            ProfileId = row.ProfileId,
            IsEnabled = row.IsEnabled,
            UnlockMode = row.UnlockMode,
            UnlockMinutes = row.UnlockMinutes,
            ProtectionVersion = row.ProtectionVersion,
            UpdatedAt = ParseRequired(row.UpdatedAt),
        });
    }

    public Task SetAdminProtectionAsync(ProfileAdminProtection protection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(protection);
        return db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!ProfileExists(conn, tx, protection.ProfileId))
            {
                throw new KeyNotFoundException("Profile not found.");
            }

            conn.Execute("""
                INSERT INTO profile_admin_protections
                    (profile_id,is_enabled,unlock_mode,unlock_minutes,protection_version,updated_at)
                VALUES(@ProfileId,@IsEnabled,@UnlockMode,@UnlockMinutes,1,@UpdatedAt)
                ON CONFLICT(profile_id) DO UPDATE SET
                    is_enabled=excluded.is_enabled,unlock_mode=excluded.unlock_mode,
                    unlock_minutes=excluded.unlock_minutes,
                    protection_version=profile_admin_protections.protection_version+1,
                    updated_at=excluded.updated_at;
                """, new
            {
                protection.ProfileId,
                IsEnabled = protection.IsEnabled ? 1 : 0,
                protection.UnlockMode,
                protection.UnlockMinutes,
                UpdatedAt = Iso(protection.UpdatedAt),
            }, tx);
            conn.Execute("DELETE FROM profile_admin_unlocks WHERE profile_id=@ProfileId;", new { protection.ProfileId }, tx);
            BumpAuthorizationVersion(conn, tx, protection.ProfileId);
        }, ct);
    }

    public Task<ProfileAdminUnlock?> GetAdminUnlockAsync(Guid sessionId, Guid profileId, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<UnlockRow>("""
            SELECT session_id AS SessionId, profile_id AS ProfileId, protection_version AS ProtectionVersion, method AS Method,
                   granted_at AS GrantedAt, expires_at AS ExpiresAt
            FROM profile_admin_unlocks
            WHERE session_id=@sessionId AND profile_id=@profileId AND (expires_at IS NULL OR expires_at>@now);
            """, new { sessionId, profileId, now = Iso(now) });
        return Task.FromResult(row is null ? null : new ProfileAdminUnlock
        {
            SessionId = row.SessionId,
            ProfileId = row.ProfileId,
            ProtectionVersion = row.ProtectionVersion,
            Method = row.Method,
            GrantedAt = ParseRequired(row.GrantedAt),
            ExpiresAt = string.IsNullOrWhiteSpace(row.ExpiresAt) ? null : ParseRequired(row.ExpiresAt),
        });
    }

    public Task SetAdminUnlockAsync(ProfileAdminUnlock unlock, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unlock);
        return db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            conn.Execute("""
                INSERT INTO profile_admin_unlocks(session_id,profile_id,protection_version,method,granted_at,expires_at)
                VALUES(@SessionId,@ProfileId,@ProtectionVersion,@Method,@GrantedAt,@ExpiresAt)
                ON CONFLICT(session_id) DO UPDATE SET profile_id=excluded.profile_id,protection_version=excluded.protection_version,
                    method=excluded.method,granted_at=excluded.granted_at,expires_at=excluded.expires_at;
                """, new
            {
                unlock.SessionId,
                unlock.ProfileId,
                unlock.ProtectionVersion,
                unlock.Method,
                GrantedAt = Iso(unlock.GrantedAt),
                ExpiresAt = unlock.ExpiresAt is null ? null : Iso(unlock.ExpiresAt.Value),
            }, tx);
        }, ct);
    }

    public Task ClearAdminUnlockAsync(Guid sessionId, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            conn.Execute("DELETE FROM profile_admin_unlocks WHERE session_id=@sessionId;", new { sessionId }, tx);
        }, ct);

    public Task<Guid?> FindUserIdOfProfileAsync(Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        return Task.FromResult(conn.QueryFirstOrDefault<Guid?>("SELECT id FROM accounts WHERE profile_id=@profileId LIMIT 1;", new { profileId }));
    }

    public Task<Guid?> GetDefaultHouseholdIdAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        return Task.FromResult(conn.QueryFirstOrDefault<Guid?>("SELECT id FROM households WHERE is_default=1 LIMIT 1;"));
    }

    public Task<bool> SetDefaultHouseholdAsync(Guid householdId, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (conn.ExecuteScalar<int>("SELECT COUNT(*) FROM households WHERE id=@householdId;", new { householdId }, tx) == 0)
            {
                return false;
            }

            // The unique index allows one default, so the old mark goes before the new one is set.
            conn.Execute("UPDATE households SET is_default=0 WHERE is_default=1 AND id<>@householdId;", new { householdId }, tx);
            conn.Execute("UPDATE households SET is_default=1 WHERE id=@householdId;", new { householdId }, tx);
            return true;
        }, ct);

    /// <summary>
    /// Refuses a server administrator role for a profile outside the default household (D17). With no default household marked yet
    /// the check passes and <see cref="MarkDefaultHouseholdIfNone"/> makes the profile's household the default.
    /// </summary>
    internal static void RequireInDefaultHousehold(IDbConnection conn, IDbTransaction tx, Guid profileId)
    {
        var defaultHousehold = conn.QueryFirstOrDefault<Guid?>("SELECT id FROM households WHERE is_default=1 LIMIT 1;", transaction: tx);
        if (defaultHousehold is not { } home)
        {
            return;
        }

        var household = conn.QueryFirstOrDefault<Guid?>("SELECT household_id FROM profiles WHERE id=@profileId;", new { profileId }, tx);
        if (household != home)
        {
            throw new InvalidOperationException("A server administrator must belong to the Library's own household.");
        }
    }

    /// <summary>
    /// Marks a household as the default only when no household is yet. Used by setup, where the server administrator's household
    /// becomes the default the moment it exists.
    /// </summary>
    internal static void MarkDefaultHouseholdIfNone(IDbConnection conn, IDbTransaction tx, Guid householdId) =>
        conn.Execute("""
            UPDATE households SET is_default=1
            WHERE id=@householdId AND NOT EXISTS (SELECT 1 FROM households WHERE is_default=1);
            """, new { householdId }, tx);

    private static bool ProfileExists(IDbConnection conn, IDbTransaction tx, Guid profileId) =>
        conn.ExecuteScalar<int>("SELECT COUNT(*) FROM profiles WHERE id=@profileId;", new { profileId }, tx) == 1;

    private static void BumpAuthorizationVersion(IDbConnection conn, IDbTransaction tx, Guid profileId) =>
        conn.Execute("UPDATE profiles SET authorization_version=authorization_version+1 WHERE id=@profileId;", new { profileId }, tx);

    private static string Iso(DateTimeOffset value) => value.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseRequired(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    private sealed class FlagsRow
    {
        public bool IsServerAdministrator { get; set; }
        public bool IsHouseholdAdministrator { get; set; }
        public long AuthorizationVersion { get; set; }
    }

    private sealed class ProtectionRow
    {
        public Guid ProfileId { get; set; }
        public bool IsEnabled { get; set; }
        public string UnlockMode { get; set; } = string.Empty;
        public int? UnlockMinutes { get; set; }
        public long ProtectionVersion { get; set; }
        public string UpdatedAt { get; set; } = string.Empty;
    }

    private sealed class UnlockRow
    {
        public Guid SessionId { get; set; }
        public Guid ProfileId { get; set; }
        public long ProtectionVersion { get; set; }
        public string Method { get; set; } = string.Empty;
        public string GrantedAt { get; set; } = string.Empty;
        public string? ExpiresAt { get; set; }
    }
}
