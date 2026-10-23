using System.Collections.Frozen;
using System.Text.Json;
using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

public sealed class AccountRepository(IDatabaseConnection db) : IAccountRepository
{
    public Task<Account?> GetByIdAsync(Guid id, CancellationToken ct = default) => GetOneAsync("id = @value", GuidSql.ToBlob(id), ct);

    public Task<Account?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken ct = default) =>
        GetOneAsync("normalized_email = @value", normalizedEmail, ct);

    public Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var rows = conn.Query<AccountRow>(Select + " ORDER BY created_at;").Select(Map).ToList();
        return Task.FromResult<IReadOnlyList<Account>>(rows);
    }

    public Task InsertAsync(Account account, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            // Without a named household the account stays unplaced until its first profile grant, when it joins
            // that profile's household.
            InsertAccount(conn, tx, account);
        }, ct);

    public Task<bool> UpdateAsync(Account account, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var changed = conn.Execute("""
                UPDATE accounts SET email = @Email, normalized_email = @NormalizedEmail,
                    is_enabled = @IsEnabled, is_administrator = @IsAdministrator,
                    this_computer_only = @ThisComputerOnly,
                    authorization_version = authorization_version + 1, updated_at = @UpdatedAt
                WHERE id = @Id;
                """, Parameters(account), tx) > 0;
            if (changed)
            {
                SyncProfileFromAccount(conn, tx, account.Id);
            }

            return changed;
        }, ct);

    public Task<bool> SetTemporaryPasswordStateAsync(Guid accountId, bool mustChangePassword, DateTimeOffset? expiresAt, DateTimeOffset changedAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        return Task.FromResult(conn.Execute("""
            UPDATE accounts SET must_change_password = @mustChange, temporary_password_expires_at = @expiresAt,
                authorization_version = authorization_version + 1, updated_at = @changedAt
            WHERE id = @accountId;
            """, new
        {
            accountId,
            mustChange = mustChangePassword ? 1 : 0,
            expiresAt = expiresAt?.ToString("O"),
            changedAt = changedAt.ToString("O"),
        }) > 0);
    }

    public Task GrantProfileAsync(AccountProfileGrant grant, CancellationToken ct = default)
    {
        return db.ExecuteWriteAsync((conn, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            JoinHousehold(conn, transaction, grant.AccountId, grant.ProfileId, grant.GrantedAt);
            if (grant.IsDefault)
            {
                conn.Execute("UPDATE account_profile_grants SET is_default = 0 WHERE account_id = @accountId;",
                    new { accountId = grant.AccountId }, transaction);
            }
            conn.Execute("""
                INSERT INTO account_profile_grants
                    (account_id, profile_id, is_default, is_enabled, admin_enabled, authorization_version, granted_at)
                VALUES (@AccountId, @ProfileId, @IsDefault, @IsEnabled, @AdminEnabled, @AuthorizationVersion, @GrantedAt)
                ON CONFLICT(account_id, profile_id) DO UPDATE SET
                    is_default = excluded.is_default, is_enabled = excluded.is_enabled,
                    admin_enabled = excluded.admin_enabled,
                    authorization_version = account_profile_grants.authorization_version + 1;
                """, new
            {
                grant.AccountId,
                grant.ProfileId,
                IsDefault = grant.IsDefault ? 1 : 0,
                IsEnabled = grant.IsEnabled ? 1 : 0,
                AdminEnabled = grant.AdminEnabled ? 1 : 0,
                grant.AuthorizationVersion,
                GrantedAt = grant.GrantedAt.ToString("O"),
            }, transaction);
            SyncProfileFromAccount(conn, transaction, grant.AccountId);
        }, ct);
    }

    public Task<bool> RevokeProfileAsync(Guid accountId, Guid profileId, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var removed = conn.Execute(
                "DELETE FROM account_profile_grants WHERE account_id = @accountId AND profile_id = @profileId;",
                new { accountId, profileId }, tx) > 0;
            if (removed)
            {
                SyncProfileFromAccount(conn, tx, accountId);
            }

            return removed;
        }, ct);

    public Task<bool> HasProfileAccessAsync(Guid accountId, Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        return Task.FromResult(conn.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM account_profile_grants WHERE account_id = @accountId AND profile_id = @profileId AND is_enabled = 1;",
            new { accountId, profileId }) > 0);
    }

    public Task<IReadOnlyList<Guid>> GetProfileIdsAsync(Guid accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var ids = conn.Query<Guid>("""
            SELECT profile_id FROM account_profile_grants
            WHERE account_id = @accountId AND is_enabled = 1 ORDER BY is_default DESC, granted_at;
            """, new { accountId }).ToList();
        return Task.FromResult<IReadOnlyList<Guid>>(ids);
    }

    public Task<Guid?> GetDefaultProfileIdAsync(Guid accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var value = conn.QueryFirstOrDefault<Guid?>("""
            SELECT profile_id FROM account_profile_grants WHERE account_id = @accountId AND is_enabled = 1
            ORDER BY is_default DESC, granted_at LIMIT 1;
            """, new { accountId });
        return Task.FromResult(value);
    }

    public Task InsertInvitationAsync(AccountInvitation invitation, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection(); conn.Execute("INSERT INTO account_invitations(id,account_id,token_hash,created_at,expires_at,consumed_at) VALUES(@Id,@AccountId,@TokenHash,@CreatedAt,@ExpiresAt,@ConsumedAt);", new { invitation.Id, invitation.AccountId, invitation.TokenHash, CreatedAt = invitation.CreatedAt.ToString("O"), ExpiresAt = invitation.ExpiresAt.ToString("O"), ConsumedAt = invitation.ConsumedAt?.ToString("O") }); return Task.CompletedTask; }

    public Task<AccountInvitation?> GetActiveInvitationAsync(string tokenHash, DateTimeOffset now, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection(); var row = conn.QueryFirstOrDefault<InvitationRow>("SELECT id AS Id,account_id AS AccountId,token_hash AS TokenHash,created_at AS CreatedAt,expires_at AS ExpiresAt,consumed_at AS ConsumedAt FROM account_invitations WHERE token_hash=@tokenHash AND consumed_at IS NULL AND expires_at>@now LIMIT 1;", new { tokenHash, now = now.ToString("O") }); return Task.FromResult(row is null ? null : new AccountInvitation { Id = row.Id, AccountId = row.AccountId, TokenHash = row.TokenHash, CreatedAt = DateTimeOffset.Parse(row.CreatedAt), ExpiresAt = DateTimeOffset.Parse(row.ExpiresAt), ConsumedAt = string.IsNullOrWhiteSpace(row.ConsumedAt) ? null : DateTimeOffset.Parse(row.ConsumedAt) }); }

    public Task<bool> ConsumeInvitationAsync(Guid invitationId, DateTimeOffset consumedAt, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection(); return Task.FromResult(conn.Execute("UPDATE account_invitations SET consumed_at=@consumedAt WHERE id=@invitationId AND consumed_at IS NULL;", new { invitationId, consumedAt = consumedAt.ToString("O") }) > 0); }

    private Task<Account?> GetOneAsync(string predicate, object value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<AccountRow>(Select + $" WHERE {predicate} LIMIT 1;", new { value });
        return Task.FromResult(row is null ? null : Map(row));
    }

    private const string Select = """
        SELECT id AS Id, email AS Email, normalized_email AS NormalizedEmail,
               is_enabled AS IsEnabled,
               is_administrator AS IsAdministrator, authorization_version AS AuthorizationVersion,
               created_at AS CreatedAt, updated_at AS UpdatedAt,
               household_id AS HouseholdId,
               household_admin AS HouseholdAdmin,
               grants_inherit_from_account_id AS GrantsInheritFromAccountId,
               this_computer_only AS ThisComputerOnly,
               must_change_password AS MustChangePassword,
               temporary_password_expires_at AS TemporaryPasswordExpiresAt,
               profile_id AS ProfileId
        FROM accounts
        """;

    private static object Parameters(Account account) => new
    {
        account.Id,
        account.Email,
        account.NormalizedEmail,
        IsEnabled = account.IsEnabled ? 1 : 0,
        IsAdministrator = account.IsAdministrator ? 1 : 0,
        account.AuthorizationVersion,
        CreatedAt = account.CreatedAt.ToString("O"),
        UpdatedAt = account.UpdatedAt.ToString("O"),
        account.HouseholdId,
        HouseholdAdmin = account.HouseholdAdmin ? 1 : 0,
        account.GrantsInheritFromAccountId,
        ThisComputerOnly = account.IsThisComputerOnly ? 1 : 0,
        MustChangePassword = account.MustChangePassword ? 1 : 0,
        TemporaryPasswordExpiresAt = account.TemporaryPasswordExpiresAt?.ToString("O"),
        account.ProfileId,
    };

    private static Account Map(AccountRow row)
    {
        var account = MapFields(row);
        if (row.ThisComputerOnly)
        {
            account.MarkThisComputerOnly();
        }

        // A person's own sign-in never administers a household, whatever a stray row says.
        if (row.HouseholdAdmin && row.GrantsInheritFromAccountId is null)
        {
            account.MakeHouseholdAdmin();
        }

        return account;
    }

    private static Account MapFields(AccountRow row) => new()
    {
        Id = row.Id,
        Email = row.Email,
        NormalizedEmail = row.NormalizedEmail,
        IsEnabled = row.IsEnabled,
        IsAdministrator = row.IsAdministrator,
        AuthorizationVersion = row.AuthorizationVersion,
        CreatedAt = DateTimeOffset.Parse(row.CreatedAt),
        UpdatedAt = DateTimeOffset.Parse(row.UpdatedAt),
        HouseholdId = row.HouseholdId,
        ProfileId = row.ProfileId,
        GrantsInheritFromAccountId = row.GrantsInheritFromAccountId,
        MustChangePassword = row.MustChangePassword,
        TemporaryPasswordExpiresAt = string.IsNullOrWhiteSpace(row.TemporaryPasswordExpiresAt)
            ? null
            : DateTimeOffset.Parse(row.TemporaryPasswordExpiresAt),
    };

    private sealed class AccountRow
    {
        public Guid Id { get; set; }
        public string? Email { get; set; }
        public string? NormalizedEmail { get; set; }
        public bool IsEnabled { get; set; }
        public bool IsAdministrator { get; set; }
        public long AuthorizationVersion { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
        public Guid? HouseholdId { get; set; }
        public bool HouseholdAdmin { get; set; }
        public Guid? GrantsInheritFromAccountId { get; set; }
        public bool ThisComputerOnly { get; set; }
        public bool MustChangePassword { get; set; }
        public string? TemporaryPasswordExpiresAt { get; set; }
        public Guid? ProfileId { get; set; }
    }
    private sealed class InvitationRow { public Guid Id { get; set; } public Guid AccountId { get; set; } public string TokenHash { get; set; } = ""; public string CreatedAt { get; set; } = ""; public string ExpiresAt { get; set; } = ""; public string? ConsumedAt { get; set; } }

    public Task<AccountProfileGrant?> GetGrantAsync(Guid accountId, Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<GrantRow>(GrantSelect + " WHERE account_id=@accountId AND profile_id=@profileId LIMIT 1;", new { accountId, profileId });
        return Task.FromResult(row is null ? null : MapGrant(row));
    }

    public Task UpdateAccountAsync(Account account, CancellationToken ct = default) => db.ExecuteWriteAsync((conn, tx, token) =>
    {
        token.ThrowIfCancellationRequested();
        var current = conn.QuerySingleOrDefault<AccountRow>(Select + " WHERE id=@Id;", account, tx)
            ?? throw new InvalidOperationException("Account not found.");
        if (current.IsEnabled && current.IsAdministrator && (!account.IsEnabled || !account.IsAdministrator)
           && !HasOtherUsableAdministratorAccount(conn, tx, account.Id))
        {
            throw new InvalidOperationException("The final effective administrator account cannot be disabled or demoted.");
        }

        if (account.IsEnabled && conn.ExecuteScalar<int>("SELECT COUNT(*) FROM account_profile_grants WHERE account_id=@Id AND is_enabled=1;", account, tx) == 0)
        {
            throw new InvalidOperationException("An enabled account must retain an enabled profile grant.");
        }

        conn.Execute("UPDATE accounts SET email=@Email,normalized_email=@NormalizedEmail,is_enabled=@IsEnabled,is_administrator=@IsAdministrator,authorization_version=authorization_version+1,updated_at=@UpdatedAt WHERE id=@Id;", Parameters(account), tx);
        SyncProfileFromAccount(conn, tx, account.Id);
    }, ct);

    public Task DeleteAccountAsync(Guid accountId, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            DeleteAccountRows(connection, transaction, accountId);
        }, ct);

    public Task<AccountRemovalPlan> PlanAccountRemovalAsync(Guid accountId, CancellationToken ct = default) =>
        db.ExecuteReadAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return new AccountRemovalPlan(PlanAccountRemoval(connection, transaction, accountId));
        }, ct);

    public Task<AccountRemovalOutcome> DeleteAccountWithOwnProfilesAsync(
        Guid accountId, IReadOnlyCollection<Guid>? confirmedProfileIds = null, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            // Worked out inside the transaction that deletes, so the profiles that go are exactly the ones just checked.
            var profiles = PlanAccountRemoval(connection, transaction, accountId);
            var profileIds = profiles.Select(profile => profile.Id).ToArray();
            // What goes must be exactly what the caller was shown and confirmed; otherwise nothing is deleted.
            if (confirmedProfileIds is not null && !confirmedProfileIds.ToHashSet().SetEquals(profileIds))
            {
                throw new AccountRemovalChangedException();
            }

            var affected = profileIds.Length == 0
                ? []
                : connection.Query<Guid>(
                    "SELECT DISTINCT account_id FROM account_profile_grants WHERE profile_id IN @ids AND account_id<>@accountId;",
                    new { ids = profileIds.Select(GuidSql.ToBlob).ToArray(), accountId }, transaction).ToList();
            DeleteAccountRows(connection, transaction, accountId);
            foreach (var profileId in profileIds)
            {
                RequireNotFinalAdministratorProfile(connection, transaction, profileId);
                DeleteProfileRows(connection, transaction, profileId);
            }

            // A sign-in that used one of these profiles as its default moves to its next enabled profile.
            foreach (var other in affected)
            {
                EnsureEnabledDefault(connection, transaction, other);
                connection.Execute("""
                    UPDATE accounts SET authorization_version=authorization_version+1,updated_at=@now WHERE id=@other;
                    """, new { other, now = Iso(DateTimeOffset.UtcNow) }, transaction);
            }

            return new AccountRemovalOutcome(profileIds, affected);
        }, ct);

    /// <summary>The account row's own work: the last-administrator backstop, handing its followers on, and removing it.</summary>
    private static void DeleteAccountRows(SqliteConnection connection, SqliteTransaction transaction, Guid accountId)
    {
        var account = connection.QuerySingleOrDefault<AccountRow>(
            Select + " WHERE id=@accountId;", new { accountId }, transaction)
            ?? throw new KeyNotFoundException("Account not found.");
        if (account.IsEnabled && account.IsAdministrator &&
            !HasOtherUsableAdministratorAccount(connection, transaction, accountId))
        {
            throw new InvalidOperationException("The final effective administrator account cannot be deleted.");
        }

        // People whose access followed this account follow the household's next main sign-in instead.
        var followers = connection.Query<Guid>(
            "SELECT id FROM accounts WHERE grants_inherit_from_account_id=@accountId;", new { accountId }, transaction).ToList();
        connection.Execute("""
            UPDATE accounts SET grants_inherit_from_account_id=(
                SELECT other.id FROM accounts other
                WHERE other.household_id=accounts.household_id AND other.id<>@accountId
                  AND other.grants_inherit_from_account_id IS NULL AND other.is_enabled=1
                ORDER BY other.created_at LIMIT 1)
            WHERE grants_inherit_from_account_id=@accountId;
            """, new { accountId }, transaction);
        // A household whose main sign-in goes away hands "primary" to its next main sign-in (or none).
        connection.Execute("""
            UPDATE households SET primary_account_id=(
                SELECT other.id FROM accounts other
                WHERE other.household_id=households.id AND other.id<>@accountId
                  AND other.grants_inherit_from_account_id IS NULL AND other.is_enabled=1
                ORDER BY other.created_at LIMIT 1)
            WHERE primary_account_id=@accountId;
            """, new { accountId }, transaction);
        connection.Execute("DELETE FROM accounts WHERE id=@accountId;", new { accountId }, transaction);
        // Their profiles now hold the access of the sign-in they follow next (or none), the same as the account side.
        foreach (var follower in followers)
        {
            SyncProfileFromAccount(connection, transaction, follower);
        }
    }

    public Task<IReadOnlyList<AccountProfileGrant>> GetGrantsAsync(Guid accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection();
        return Task.FromResult<IReadOnlyList<AccountProfileGrant>>(conn.Query<GrantRow>(GrantSelect + " WHERE account_id=@accountId ORDER BY is_default DESC, granted_at;", new { accountId }).Select(MapGrant).ToList());
    }

    public Task<IReadOnlySet<AccountFeatureId>> GetFeatureGrantsAsync(Guid accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection();
        return Task.FromResult<IReadOnlySet<AccountFeatureId>>(conn.Query<string>("SELECT feature_id FROM account_feature_grants WHERE account_id=COALESCE((SELECT grants_inherit_from_account_id FROM accounts WHERE id=@accountId),@accountId);", new { accountId }).Select(x => new AccountFeatureId(x)).ToFrozenSet());
    }

    public Task<IReadOnlySet<Guid>> GetLibraryGrantsAsync(Guid accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection();
        return Task.FromResult<IReadOnlySet<Guid>>(conn.Query<Guid>("SELECT library_id FROM account_library_grants WHERE account_id=COALESCE((SELECT grants_inherit_from_account_id FROM accounts WHERE id=@accountId),@accountId);", new { accountId }).ToFrozenSet());
    }

    public async Task<bool> HasFeatureGrantAsync(Guid accountId, AccountFeatureId feature, CancellationToken ct = default) =>
        (await GetFeatureGrantsAsync(accountId, ct).ConfigureAwait(false)).Contains(feature);

    public async Task<bool> HasLibraryGrantAsync(Guid accountId, Guid libraryId, CancellationToken ct = default) =>
        (await GetLibraryGrantsAsync(accountId, ct).ConfigureAwait(false)).Contains(libraryId);

    public Task<GrantAdminProtection?> GetAdminProtectionAsync(Guid accountId, Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<ProtectionRow>("SELECT account_id AS AccountId, profile_id AS ProfileId, is_enabled AS IsEnabled, unlock_mode AS UnlockMode, unlock_minutes AS UnlockMinutes, protection_version AS ProtectionVersion, updated_at AS UpdatedAt FROM grant_admin_protections WHERE account_id=@accountId AND profile_id=@profileId;", new { accountId, profileId });
        return Task.FromResult(row is null ? null : MapProtection(row));
    }

    public Task<GrantAdminUnlock?> GetAdminUnlockAsync(Guid sessionId, Guid accountId, Guid profileId, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<UnlockRow>("SELECT session_id AS SessionId, account_id AS AccountId, profile_id AS ProfileId, protection_version AS ProtectionVersion, method AS Method, granted_at AS GrantedAt, expires_at AS ExpiresAt FROM grant_admin_unlocks WHERE session_id=@sessionId AND account_id=@accountId AND profile_id=@profileId AND (expires_at IS NULL OR expires_at>@now);", new { sessionId, accountId, profileId, now = Iso(now) });
        return Task.FromResult(row is null ? null : MapUnlock(row));
    }

    public Task WriteAuthorizationAuditAsync(AuthorizationAuditEvent e, CancellationToken ct = default) => db.ExecuteWriteAsync((conn, tx, token) => { token.ThrowIfCancellationRequested(); InsertAuthorizationAudit(conn, tx, e); }, ct);

    public Task<bool> HasAuthorizationAuditSinceAsync(string eventType, Guid? actorProfileId, string subjectType, string subjectId, DateTimeOffset since, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection();
        // Rows are written in UTC, so the ISO text compares in time order.
        return Task.FromResult(conn.ExecuteScalar<int>("SELECT EXISTS(SELECT 1 FROM authorization_audit_events WHERE event_type=@eventType AND actor_profile_id IS @actorProfileId AND subject_type=@subjectType AND subject_id=@subjectId AND occurred_at>=@since);", new { eventType, actorProfileId, subjectType, subjectId, since = Iso(since.ToUniversalTime()) }) == 1);
    }

    /// <summary>Writes one audit row on the caller's connection and transaction, so a change and its audit entry commit or roll back together.</summary>
    internal static void InsertAuthorizationAudit(System.Data.IDbConnection conn, System.Data.IDbTransaction tx, AuthorizationAuditEvent e) =>
        conn.Execute("INSERT INTO authorization_audit_events(event_type,occurred_at,actor_account_id,actor_profile_id,actor_application_id,subject_type,subject_id,changes_json) VALUES(@EventType,@OccurredAt,@ActorAccountId,@ActorProfileId,@ActorApplicationId,@SubjectType,@SubjectId,@ChangesJson);", new { e.EventType, OccurredAt = Iso(e.OccurredAt), e.ActorAccountId, e.ActorProfileId, e.ActorApplicationId, e.SubjectType, e.SubjectId, ChangesJson = JsonSerializer.Serialize(e.Changes) }, tx);

    public Task CreateAccountAsync(Account account, AccountProfileGrant initialGrant,
        IReadOnlySet<AccountFeatureId> features, IReadOnlySet<Guid> libraries,
        CancellationToken ct = default, Profile? newProfile = null) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (initialGrant.AccountId != account.Id || !initialGrant.IsDefault || !initialGrant.IsEnabled ||
                initialGrant.ProfileId == Guid.Empty ||
                (newProfile is not null && newProfile.Id != initialGrant.ProfileId))
            {
                throw new InvalidOperationException("The initial profile grant must be enabled and default.");
            }

            // The account's household: the one it names, else the one its existing profile is in, else a new one.
            var existingName = newProfile?.DisplayName ?? connection.QueryFirstOrDefault<string>(
                "SELECT display_name FROM profiles WHERE id=@ProfileId;", initialGrant, transaction);
            account.HouseholdId = ResolveAccountHousehold(
                connection, transaction, account, newProfile is null ? initialGrant.ProfileId : null, existingName ?? account.Email);
            if (newProfile is not null)
            {
                RequireRoom(connection, transaction, account.HouseholdId.Value);
                newProfile.HouseholdId = account.HouseholdId;
                connection.Execute("""
                    INSERT INTO profiles
                        (id,display_name,avatar_color,avatar_image_path,avatar_icon,content_limit,content_limit_allow_unrated,role,created_at,navigation_config,household_id)
                    VALUES(@Id,@DisplayName,@AvatarColor,@AvatarImagePath,@AvatarIcon,@ContentLimit,@ContentLimitAllowUnrated,@Role,@CreatedAt,@NavigationConfig,@HouseholdId);
                    """, new
                {
                    newProfile.Id,
                    newProfile.DisplayName,
                    newProfile.AvatarColor,
                    newProfile.AvatarImagePath,
                    newProfile.AvatarIcon,
                    newProfile.ContentLimit,
                    newProfile.ContentLimitAllowUnrated,
                    Role = newProfile.Role.ToString(),
                    CreatedAt = Iso(newProfile.CreatedAt),
                    newProfile.NavigationConfig,
                    newProfile.HouseholdId,
                }, transaction);
            }
            else if (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM profiles WHERE id=@ProfileId;",
                         initialGrant, transaction) != 1)
            {
                throw new KeyNotFoundException("Profile not found.");
            }
            // Every user opens its own profile: the profile it signs in as. A profile has at most one user.
            AttachToSignInProfile(connection, transaction, account, initialGrant.ProfileId);

            InsertAccount(connection, transaction, account);
            InsertGrant(connection, transaction, initialGrant);
            ReplaceAccess(connection, transaction, account.Id, features, libraries, account.UpdatedAt);
            EnsureHouseholdPrimary(connection, transaction, account.Id);
            // The profile now holds the same roles and access (a brand-new profile has no open sessions to re-check).
            SyncProfileFromAccount(connection, transaction, account.Id, bumpVersion: newProfile is null);
        }, ct);

    public Task CreateInvitedAccountAsync(
        Account account,
        IReadOnlyList<AccountProfileGrant> grants,
        AccountInvitation invitation,
        CancellationToken ct = default,
        Profile? newProfile = null) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (grants.Count is 0 or > 8 || grants.Count(grant => grant.IsDefault && grant.IsEnabled) != 1 ||
                grants.Any(grant => grant.AccountId != account.Id || !grant.IsEnabled || grant.AdminEnabled))
            {
                throw new InvalidOperationException("Invitation grants must contain one enabled default and at most eight profiles.");
            }

            if (invitation.AccountId != account.Id || string.IsNullOrWhiteSpace(invitation.TokenHash))
            {
                throw new InvalidOperationException("Invitation identity is invalid.");
            }

            if (newProfile is not null && (grants.Count != 1 || grants[0].ProfileId != newProfile.Id))
            {
                throw new InvalidOperationException("A new person's invitation must open exactly that person.");
            }

            // Someone outside the household starts a household of their own. Existing people keep their household.
            var profileHouseholds = new List<Guid>();
            if (newProfile is null)
            {
                profileHouseholds.AddRange(grants
                    .Select(grant => HouseholdOfProfile(connection, transaction, grant.ProfileId))
                    .Where(household => household is not null).Select(household => household!.Value).Distinct());
            }
            if (profileHouseholds.Count > 1)
            {
                throw new InvalidOperationException(CrossHouseholdMessage);
            }

            account.HouseholdId ??= profileHouseholds.Count == 1
                ? profileHouseholds[0]
                : CreateOrAdoptHousehold(connection, transaction, newProfile?.DisplayName ?? account.Email, account);
            if (newProfile is not null)
            {
                InsertProfileRow(connection, transaction, newProfile, account.HouseholdId.Value);
            }

            // The invited user opens the invitation's default profile, and a profile has at most one user.
            AttachToSignInProfile(connection, transaction, account, grants.Single(grant => grant.IsDefault && grant.IsEnabled).ProfileId);
            InsertAccount(connection, transaction, account);
            foreach (var grant in grants)
            {
                InsertGrant(connection, transaction, grant);
            }

            connection.Execute("""
                INSERT INTO account_invitations
                    (id, account_id, token_hash, created_at, expires_at, consumed_at)
                VALUES (@Id, @AccountId, @TokenHash, @CreatedAt, @ExpiresAt, NULL);
                """, new
            {
                invitation.Id,
                invitation.AccountId,
                invitation.TokenHash,
                CreatedAt = Iso(invitation.CreatedAt),
                ExpiresAt = Iso(invitation.ExpiresAt),
            }, transaction);
            // Someone invited from outside starts a household of their own and looks after it.
            EnsureHouseholdPrimary(connection, transaction, account.Id);
            SyncProfileFromAccount(connection, transaction, account.Id, bumpVersion: newProfile is null);
        }, ct);

    public Task CreateManagedProfileAsync(
        Profile profile,
        AccountProfileGrant targetGrant,
        CancellationToken ct = default,
        Guid? accessFromProfileId = null) => db.ExecuteWriteAsync((connection, transaction, token) =>
    {
        token.ThrowIfCancellationRequested();
        if (targetGrant.ProfileId != profile.Id || !targetGrant.IsEnabled || targetGrant.AdminEnabled)
        {
            throw new InvalidOperationException("The target profile grant is invalid.");
        }

        var grantCount = connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM account_profile_grants WHERE account_id=@AccountId;",
            targetGrant, transaction);
        if (grantCount >= 8)
        {
            throw new InvalidOperationException(Household.FullMessage);
        }

        if (!targetGrant.IsDefault && grantCount == 0)
        {
            throw new InvalidOperationException("The first profile grant must be the default.");
        }

        // The new person joins the account's household; a household holds up to eight people.
        var householdId = HouseholdOfAccount(connection, transaction, targetGrant.AccountId)
            ?? CreateHousehold(connection, transaction, null, profile.CreatedAt);
        connection.Execute("UPDATE accounts SET household_id=@householdId WHERE id=@AccountId AND household_id IS NULL;",
            new { householdId, targetGrant.AccountId }, transaction);
        RequireRoom(connection, transaction, householdId);
        InsertProfileRow(connection, transaction, profile, householdId);
        GiveNewProfileAccess(connection, transaction, profile.Id, householdId, accessFromProfileId, profile.CreatedAt);
        if (targetGrant.IsDefault)
        {
            connection.Execute("""
                UPDATE account_profile_grants SET is_default=0,
                    authorization_version=authorization_version+1
                WHERE account_id=@AccountId AND is_default=1;
                """, targetGrant, transaction);
        }

        InsertGrant(connection, transaction, targetGrant);
        connection.Execute("""
            UPDATE accounts SET authorization_version=authorization_version+1,updated_at=@now
            WHERE id=@accountId;
            """, new { accountId = targetGrant.AccountId, now = Iso(profile.CreatedAt) }, transaction);
    }, ct);

    public Task CreateHouseholdPersonAsync(
        Profile profile,
        Guid householdId,
        IReadOnlyList<AccountProfileGrant> grants,
        CancellationToken ct = default,
        Guid? accessFromProfileId = null) => db.ExecuteWriteAsync((connection, transaction, token) =>
    {
        token.ThrowIfCancellationRequested();
        if (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM households WHERE id=@householdId;",
                new { householdId }, transaction) == 0)
        {
            throw new KeyNotFoundException("Household not found.");
        }

        if (grants.Any(grant => grant.ProfileId != profile.Id || grant.IsDefault || !grant.IsEnabled || grant.AdminEnabled
            || HouseholdOfAccount(connection, transaction, grant.AccountId) != householdId))
        {
            throw new InvalidOperationException("A new person can only be opened by sign-ins in their own household.");
        }

        RequireRoom(connection, transaction, householdId);
        InsertProfileRow(connection, transaction, profile, householdId);
        GiveNewProfileAccess(connection, transaction, profile.Id, householdId, accessFromProfileId, profile.CreatedAt);
        foreach (var grant in grants)
        {
            InsertGrantRow(connection, transaction, grant);
            connection.Execute("""
                UPDATE accounts SET authorization_version=authorization_version+1,updated_at=@now
                WHERE id=@accountId;
                """, new { accountId = grant.AccountId, now = Iso(profile.CreatedAt) }, transaction);
        }
    }, ct);

    public Task UpdateManagedProfileAsync(Profile profile, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var changed = connection.Execute("""
                UPDATE profiles SET display_name=@DisplayName,avatar_color=@AvatarColor,avatar_icon=@AvatarIcon,
                    content_limit=@ContentLimit,content_limit_allow_unrated=@ContentLimitAllowUnrated
                WHERE id=@Id;
                """, profile, transaction);
            if (changed != 1)
            {
                throw new KeyNotFoundException("Profile not found.");
            }
        }, ct);

    public Task ValidateManagedProfileRemovalAsync(Guid profileId, CancellationToken ct = default) =>
        db.ExecuteReadAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            RequireProfileRemovable(connection, transaction, profileId);
            return 0;
        }, ct);

    public Task DeleteManagedProfileAsync(Guid profileId, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            RequireProfileRemovable(connection, transaction, profileId);
            DeleteProfileRows(connection, transaction, profileId);
        }, ct);

    /// <summary>
    /// Removes a profile and everything personal it left behind, in the caller's transaction (if anything fails, all of it is
    /// undone). Tables that point at a profile through a foreign key go with the profile row. The tables below have no foreign
    /// key, so they are cleared by hand. What stays: audit and approval history (the application event outbox,
    /// <c>approval_records</c>, <c>authorization_audit_events</c>, <c>identity_audit_events</c>), the library activity log
    /// (<c>system_activity</c>, apart from the person's Trash-emptied entries), and the reserved View storage label
    /// (<c>view_storage_labels</c>). A guardrail test lists every table with a profile column and no foreign key, so a new one
    /// has to be decided here.
    /// </summary>
    private static void DeleteProfileRows(SqliteConnection connection, SqliteTransaction transaction, Guid profileId)
    {
        // Backstop for every delete path: the sign-in is deleted first (that is what DeleteAccountWithOwnProfilesAsync does).
        RequireNoLinkedSignIn(connection, transaction, profileId);
        // The share records and file locations of the person's photos are released first (their files are handled by the caller).
        ProfilePersonalMediaRepository.ReleaseForRemoval(connection, transaction, profileId);
        connection.Execute("""
            DELETE FROM consumption_history WHERE profile_id=@profileId;
            DELETE FROM personal_status_commands WHERE profile_id=@profileId;
            DELETE FROM waiting_personal_state WHERE profile_id=@profileId;
            DELETE FROM system_activity WHERE profile_id=@profileId AND action_type=@trashEmptied;
            DELETE FROM encode_jobs WHERE owner_profile_id=@profileId;
            DELETE FROM offline_variants WHERE owner_profile_id=@profileId;
            DELETE FROM user_states WHERE user_id=@profileId;
            DELETE FROM user_taste_profiles WHERE user_id=@profileId;
            DELETE FROM reader_bookmarks WHERE user_id=@profileText;
            DELETE FROM reader_highlights WHERE user_id=@profileText;
            DELETE FROM reader_statistics WHERE user_id=@profileText;
            """, new { profileId, profileText = profileId.ToString("D"), trashEmptied = SystemActionType.PersonalTrashEmptied }, transaction);
        connection.Execute("DELETE FROM profiles WHERE id=@profileId;", new { profileId }, transaction);
    }

    /// <summary>
    /// The profiles that go with a sign-in. This is the one rule for "a user's own profiles" (Shy, 2026-10-22), worked out from
    /// grants alone because nothing records who created a profile:
    /// <list type="bullet">
    /// <item>A sign-in owns the profile it signs in as: its enabled default grant. That holds whether or not other sign-ins can
    /// also open the profile (a household's main sign-ins hold a non-default grant on its people, and a second adult may be
    /// invited into a profile another sign-in also signs in as). It holds for a person's own sign-in too
    /// (<c>grants_inherit_from_account_id</c> set), whose default grant is the one person it opens, so deleting that sign-in
    /// through Delete user deletes the person.</item>
    /// <item>A sign-in also owns every profile that only it holds a grant on (enabled or not): profiles created for that account
    /// (<c>CreateManagedProfileAsync</c>, the old default after a new default was chosen). Anyone else holding a grant on it,
    /// even a disabled one, makes it shared, and it stays.</item>
    /// <item>A profile must be in the sign-in's household (or have none) to count. Nothing else is ever a sign-in's own. People made
    /// for the household with no sign-in of their own (<c>CreateHouseholdPersonAsync</c>: granted to every main sign-in, default
    /// for none) are held by every main sign-in, so with two or more main sign-ins they stay with the household. In a household
    /// with a single main sign-in the data cannot tell such a person from a profile created for that account: both are held
    /// only by it, so both go with it (the confirmation names every profile that will go, and the last owner of a household
    /// cannot be deleted, so this only happens once someone else owns the household).</item>
    /// </list>
    /// Throws when the delete would strand someone: a sign-in that signs in as the Owner profile (never deleted) must pick another
    /// first (an Owner profile it merely holds stays and is not part of what goes), and no other sign-in, disabled ones included,
    /// may be left able to open only profiles that are going.
    /// </summary>
    private static IReadOnlyList<Profile> PlanAccountRemoval(SqliteConnection connection, SqliteTransaction transaction, Guid accountId)
    {
        var account = connection.QuerySingleOrDefault<AccountRow>(
            Select + " WHERE id=@accountId;", new { accountId }, transaction)
            ?? throw new KeyNotFoundException("Account not found.");
        var profileIds = OwnProfileIds(connection, transaction, account);
        if (profileIds.Contains(Profile.SeedProfileId))
        {
            // The Owner profile is never deleted. A sign-in that signs in as it must pick another profile first; one that merely
            // holds it (no other sign-in does) leaves it standing, and it is not part of what goes.
            var signInProfile = connection.Query<GrantRow>(
                    GrantSelect + " WHERE account_id=@Id AND is_enabled=1 AND is_default=1;", new { account.Id }, transaction)
                .FirstOrDefault()?.ProfileId;
            if (signInProfile == Profile.SeedProfileId)
            {
                throw new InvalidOperationException(
                    "This sign-in signs in as the Owner profile, which is never deleted. Choose a different profile for it first.");
            }

            profileIds.Remove(Profile.SeedProfileId);
        }

        if (profileIds.Count == 0)
        {
            return [];
        }

        var blobs = profileIds.Select(GuidSql.ToBlob).ToArray();
        var stranded = connection.QueryFirstOrDefault<StrandedRow>("""
            SELECT a.id AS AccountId, a.email AS Email,
                   (SELECT p.display_name FROM account_profile_grants own JOIN profiles p ON p.id=own.profile_id
                    WHERE own.account_id=a.id AND own.is_enabled=1
                    ORDER BY own.is_default DESC, own.granted_at LIMIT 1) AS PersonName
            FROM accounts a
            WHERE a.id<>@accountId
              AND EXISTS (SELECT 1 FROM account_profile_grants g
                          WHERE g.account_id=a.id AND g.is_enabled=1 AND g.profile_id IN @blobs)
              AND NOT EXISTS (SELECT 1 FROM account_profile_grants rest
                              WHERE rest.account_id=a.id AND rest.is_enabled=1 AND rest.profile_id NOT IN @blobs)
            ORDER BY a.created_at, a.id LIMIT 1;
            """, new { accountId, blobs }, transaction);
        if (stranded is not null)
        {
            // The sign-in is named by its email (its identity); the person's name only when it has no email.
            var name = !string.IsNullOrWhiteSpace(stranded.Email) ? stranded.Email
                : !string.IsNullOrWhiteSpace(stranded.PersonName) ? stranded.PersonName
                : "Someone";
            throw new InvalidOperationException($"{name} can only open this profile. Give {name} another profile first.");
        }

        return connection.Query<OwnProfileRow>("""
            SELECT id AS Id, display_name AS DisplayName, avatar_color AS AvatarColor, avatar_image_path AS AvatarImagePath,
                   avatar_icon AS AvatarIcon, household_id AS HouseholdId, role AS Role, created_at AS CreatedAt
            FROM profiles WHERE id IN @blobs ORDER BY created_at, id;
            """, new { blobs }, transaction)
            .Select(row => new Profile
            {
                Id = row.Id,
                DisplayName = row.DisplayName,
                AvatarColor = row.AvatarColor,
                AvatarImagePath = row.AvatarImagePath,
                AvatarIcon = row.AvatarIcon,
                HouseholdId = row.HouseholdId,
                Role = Enum.Parse<ProfileRole>(row.Role),
                CreatedAt = DateTimeOffset.Parse(row.CreatedAt, System.Globalization.CultureInfo.InvariantCulture),
            }).ToList();
    }

    /// <summary>The ids of the profiles that are this sign-in's own (see <see cref="PlanAccountRemoval"/> for the rule).</summary>
    private static List<Guid> OwnProfileIds(SqliteConnection connection, SqliteTransaction transaction, AccountRow account)
    {
        var candidates = new List<Guid>();
        if (connection.Query<GrantRow>(
                GrantSelect + " WHERE account_id=@Id AND is_enabled=1 AND is_default=1;", new { account.Id }, transaction)
            .FirstOrDefault() is { } signInGrant)
        {
            candidates.Add(signInGrant.ProfileId);
        }

        candidates.AddRange(connection.Query<Guid>("""
            SELECT g.profile_id FROM account_profile_grants g
            WHERE g.account_id=@Id
              AND NOT EXISTS (SELECT 1 FROM account_profile_grants other
                              WHERE other.profile_id=g.profile_id AND other.account_id<>@Id)
            ORDER BY g.granted_at, g.profile_id;
            """, new { account.Id }, transaction));
        return candidates
            .Distinct()
            .Where(profileId =>
            {
                var profileHousehold = HouseholdOfProfile(connection, transaction, profileId);
                return profileHousehold is null || account.HouseholdId is null || profileHousehold == account.HouseholdId;
            })
            .ToList();
    }

    private sealed class StrandedRow { public Guid AccountId { get; set; } public string? Email { get; set; } public string? PersonName { get; set; } }

    private sealed class OwnProfileRow
    {
        public Guid Id { get; set; }
        public string DisplayName { get; set; } = "";
        public string AvatarColor { get; set; } = "";
        public string? AvatarImagePath { get; set; }
        public string? AvatarIcon { get; set; }
        public Guid? HouseholdId { get; set; }
        public string Role { get; set; } = "";
        public string CreatedAt { get; set; } = "";
    }

    private static void RequireProfileRemovable(SqliteConnection connection, SqliteTransaction transaction, Guid profileId)
    {
        if (profileId == Profile.SeedProfileId)
        {
            throw new InvalidOperationException("The Owner profile cannot be deleted.");
        }

        if (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM profiles WHERE id=@profileId;",
                new { profileId }, transaction) == 0)
        {
            throw new KeyNotFoundException("Profile not found.");
        }

        RequireNoLinkedSignIn(connection, transaction, profileId);
        if (connection.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM accounts a
                JOIN account_profile_grants target ON target.account_id=a.id
                WHERE target.profile_id=@profileId AND target.is_enabled=1 AND a.is_enabled=1
                  AND (SELECT COUNT(*) FROM account_profile_grants other
                       WHERE other.account_id=a.id AND other.is_enabled=1)=1;
                """, new { profileId }, transaction) > 0)
        {
            throw new InvalidOperationException("Remove or move this person's sign-in before deleting them. A sign-in that opens only this profile must be removed, or moved to another profile, first.");
        }

        RequireNotFinalAdministratorProfile(connection, transaction, profileId);
    }

    /// <summary>What a person is told when they try to delete a profile that still has its own sign-in.</summary>
    internal const string ProfileHasSignInMessage = "This person has their own sign-in. Delete the sign-in first.";

    /// <summary>
    /// A profile with a sign-in attached cannot be deleted by itself: removing the profile row would also remove the sign-in
    /// (the data store cascades) without the hand-overs <c>DeleteAccountRows</c> does (the household's primary sign-in, the
    /// people whose access follows it). The sign-in is deleted first, which then takes its own profiles with it.
    /// </summary>
    private static void RequireNoLinkedSignIn(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, Guid profileId)
    {
        if (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM accounts WHERE profile_id=@profileId;", new { profileId }, transaction) > 0)
        {
            throw new InvalidOperationException(ProfileHasSignInMessage);
        }
    }

    /// <summary>Backstop: the last profile an administrator can sign in with cannot be deleted.</summary>
    private static void RequireNotFinalAdministratorProfile(SqliteConnection connection, SqliteTransaction transaction, Guid profileId)
    {
        var removesAdministrator = connection.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM account_profile_grants g JOIN accounts a ON a.id=g.account_id
            WHERE g.profile_id=@profileId AND g.is_enabled=1 AND g.admin_enabled=1
              AND a.is_enabled=1 AND a.is_administrator=1
              AND (
                EXISTS(SELECT 1 FROM account_credentials c WHERE c.account_id=a.id)
                OR EXISTS(SELECT 1 FROM account_passkeys p WHERE p.account_id=a.id)
                OR EXISTS(SELECT 1 FROM account_external_logins e WHERE e.account_id=a.id)
              );
            """, new { profileId }, transaction) > 0;
        var administratorsRemaining = connection.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM account_profile_grants g JOIN accounts a ON a.id=g.account_id
            WHERE g.profile_id<>@profileId AND g.is_enabled=1 AND g.admin_enabled=1
              AND a.is_enabled=1 AND a.is_administrator=1
              AND (
                EXISTS(SELECT 1 FROM account_credentials c WHERE c.account_id=a.id)
                OR EXISTS(SELECT 1 FROM account_passkeys p WHERE p.account_id=a.id)
                OR EXISTS(SELECT 1 FROM account_external_logins e WHERE e.account_id=a.id)
              );
            """, new { profileId }, transaction);
        if (removesAdministrator && administratorsRemaining == 0)
        {
            throw new InvalidOperationException("The final effective administrator profile cannot be deleted.");
        }
    }

    public Task UpsertGrantAsync(AccountProfileGrant grant, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var count = connection.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM account_profile_grants WHERE account_id=@AccountId;",
                grant, transaction);
            var prior = connection.QuerySingleOrDefault<GrantRow>(
                GrantSelect + " WHERE account_id=@AccountId AND profile_id=@ProfileId;",
                grant, transaction);
            if (prior is null && count >= 8)
            {
                throw new InvalidOperationException(Household.FullMessage);
            }

            if (grant.IsDefault && !grant.IsEnabled)
            {
                throw new InvalidOperationException("The default profile grant must be enabled.");
            }

            var account = connection.QuerySingle<(bool IsEnabled, bool IsAdministrator)>(
                "SELECT is_enabled AS IsEnabled,is_administrator AS IsAdministrator FROM accounts WHERE id=@AccountId;",
                grant, transaction);
            if (grant.AdminEnabled && !account.IsAdministrator)
            {
                throw new InvalidOperationException("Only administrator accounts can enable grant administration.");
            }

            if (prior?.IsEnabled == true && prior.AdminEnabled &&
                (!grant.IsEnabled || !grant.AdminEnabled) && account.IsEnabled && account.IsAdministrator &&
                !HasOtherEffectiveAdministrator(connection, transaction, grant.AccountId, grant.ProfileId))
            {
                throw new InvalidOperationException("The final effective administrator grant cannot be disabled.");
            }

            if (!grant.IsEnabled && account.IsEnabled && connection.ExecuteScalar<int>("""
                    SELECT COUNT(*) FROM account_profile_grants
                    WHERE account_id=@AccountId AND profile_id<>@ProfileId AND is_enabled=1;
                    """, grant, transaction) == 0)
            {
                throw new InvalidOperationException("An enabled account must retain an enabled profile grant.");
            }

            if (grant.IsDefault)
            {
                connection.Execute("""
                    UPDATE account_profile_grants SET is_default=0,
                        authorization_version=authorization_version+1
                    WHERE account_id=@AccountId AND is_default=1 AND profile_id<>@ProfileId;
                    """, grant, transaction);
            }

            InsertGrant(connection, transaction, grant);
            EnsureEnabledDefault(connection, transaction, grant.AccountId);
            connection.Execute("""
                UPDATE accounts SET authorization_version=authorization_version+1,updated_at=@now WHERE id=@id;
                """, new { id = grant.AccountId, now = Iso(grant.GrantedAt) }, transaction);
            SyncProfileFromAccount(connection, transaction, grant.AccountId);
        }, ct);

    public Task RevokeGrantAsync(Guid accountId, Guid profileId, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var account = connection.QuerySingle<(bool IsEnabled, bool IsAdministrator)>(
                "SELECT is_enabled AS IsEnabled,is_administrator AS IsAdministrator FROM accounts WHERE id=@accountId;",
                new { accountId }, transaction);
            var grant = connection.QuerySingleOrDefault<GrantRow>(
                GrantSelect + " WHERE account_id=@accountId AND profile_id=@profileId;",
                new { accountId, profileId }, transaction)
                ?? throw new InvalidOperationException("Profile grant not found.");
            var enabledRemaining = connection.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM account_profile_grants
                WHERE account_id=@accountId AND profile_id<>@profileId AND is_enabled=1;
                """, new { accountId, profileId }, transaction);
            if (account.IsEnabled && enabledRemaining == 0)
            {
                throw new InvalidOperationException("An enabled account must retain an enabled profile grant.");
            }

            if (account.IsEnabled && account.IsAdministrator && grant.AdminEnabled &&
                !HasOtherEffectiveAdministrator(connection, transaction, accountId, profileId))
            {
                throw new InvalidOperationException("The final effective administrator grant cannot be removed.");
            }

            connection.Execute("""
                DELETE FROM account_profile_grants WHERE account_id=@accountId AND profile_id=@profileId;
                """, new { accountId, profileId }, transaction);
            EnsureEnabledDefault(connection, transaction, accountId);
            connection.Execute("""
                UPDATE accounts SET authorization_version=authorization_version+1,updated_at=@now
                WHERE id=@accountId;
                """, new { accountId, now = Iso(DateTimeOffset.UtcNow) }, transaction);
            SyncProfileFromAccount(connection, transaction, accountId);
        }, ct);

    public Task<Guid?> GetHouseholdPrimaryAccountIdAsync(Guid householdId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var id = conn.QuerySingleOrDefault<Guid?>(
            "SELECT primary_account_id FROM households WHERE id=@householdId;", new { householdId });
        return Task.FromResult(id);
    }

    public Task<bool> SetHouseholdAdminAsync(Guid accountId, bool isHouseholdAdmin, DateTimeOffset changedAt, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var account = connection.QuerySingleOrDefault<(Guid? HouseholdId, bool Follows)>(
                "SELECT household_id AS HouseholdId, (grants_inherit_from_account_id IS NOT NULL) AS Follows FROM accounts WHERE id=@accountId;",
                new { accountId }, transaction);
            if (account.HouseholdId is null)
            {
                return false;
            }

            if (isHouseholdAdmin && account.Follows)
            {
                throw new InvalidOperationException("A person's own sign-in can't be a household administrator.");
            }

            var changed = connection.Execute("""
                UPDATE accounts SET household_admin=@flag, authorization_version=authorization_version+1, updated_at=@now
                WHERE id=@accountId AND household_admin<>@flag;
                """, new { accountId, flag = isHouseholdAdmin ? 1 : 0, now = Iso(changedAt) }, transaction);
            if (isHouseholdAdmin)
            {
                connection.Execute(
                    "UPDATE households SET primary_account_id=@accountId WHERE id=@household AND primary_account_id IS NULL;",
                    new { accountId, household = account.HouseholdId.Value }, transaction);
            }

            SyncProfileFromAccount(connection, transaction, accountId);
            return changed > 0;
        }, ct);

    public Task ReplaceAccountAccessAsync(Guid accountId, IReadOnlySet<AccountFeatureId> features,
        IReadOnlySet<Guid> libraries, DateTimeOffset changedAt, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            ReplaceAccess(connection, transaction, accountId, features, libraries, changedAt);
            connection.Execute("""
                UPDATE accounts SET authorization_version=authorization_version+1,updated_at=@now
                WHERE id=@accountId;
                """, new { accountId, now = Iso(changedAt) }, transaction);
            // The profile it signs in as, and the profiles of the people whose access follows this sign-in, hold the same access.
            SyncProfileFromAccount(connection, transaction, accountId);
        }, ct);
    public Task SetAdminProtectionAsync(GrantAdminProtection protection, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("""
                INSERT INTO grant_admin_protections
                    (account_id,profile_id,is_enabled,unlock_mode,unlock_minutes,protection_version,updated_at)
                VALUES(@AccountId,@ProfileId,@IsEnabled,@UnlockMode,@UnlockMinutes,@ProtectionVersion,@UpdatedAt)
                ON CONFLICT(account_id,profile_id) DO UPDATE SET
                    is_enabled=excluded.is_enabled,unlock_mode=excluded.unlock_mode,
                    unlock_minutes=excluded.unlock_minutes,
                    protection_version=grant_admin_protections.protection_version+1,
                    updated_at=excluded.updated_at;
                """, new
            {
                protection.AccountId,
                protection.ProfileId,
                IsEnabled = protection.IsEnabled ? 1 : 0,
                protection.UnlockMode,
                protection.UnlockMinutes,
                protection.ProtectionVersion,
                UpdatedAt = Iso(protection.UpdatedAt),
            }, transaction);
            connection.Execute("""
                UPDATE account_profile_grants SET authorization_version=authorization_version+1
                WHERE account_id=@AccountId AND profile_id=@ProfileId;
                DELETE FROM grant_admin_unlocks WHERE account_id=@AccountId AND profile_id=@ProfileId;
                """, protection, transaction);
            SyncProtectionOntoProfile(connection, transaction, protection.AccountId, protection.ProfileId);
        }, ct);

    public Task<bool> HasEnabledAdminProtectionAsync(Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection();
        return Task.FromResult(conn.ExecuteScalar<int>(
            "SELECT EXISTS(SELECT 1 FROM grant_admin_protections WHERE profile_id=@profileId AND is_enabled=1);", new { profileId }) == 1);
    }

    public Task SetAdminUnlockAsync(GrantAdminUnlock unlock, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, _) => connection.Execute("""
            INSERT INTO grant_admin_unlocks
                (session_id,account_id,profile_id,protection_version,method,granted_at,expires_at)
            VALUES(@SessionId,@AccountId,@ProfileId,@ProtectionVersion,@Method,@GrantedAt,@ExpiresAt)
            ON CONFLICT(session_id) DO UPDATE SET account_id=excluded.account_id,
                profile_id=excluded.profile_id,protection_version=excluded.protection_version,
                method=excluded.method,granted_at=excluded.granted_at,expires_at=excluded.expires_at;
            """, new
        {
            unlock.SessionId,
            unlock.AccountId,
            unlock.ProfileId,
            unlock.ProtectionVersion,
            unlock.Method,
            GrantedAt = Iso(unlock.GrantedAt),
            ExpiresAt = unlock.ExpiresAt is null ? null : Iso(unlock.ExpiresAt.Value),
        }, transaction), ct);

    public Task ClearAdminUnlockAsync(Guid sessionId, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, _) => connection.Execute(
            "DELETE FROM grant_admin_unlocks WHERE session_id=@sessionId;",
            new { sessionId }, transaction), ct);

    private static void InsertAccount(System.Data.IDbConnection c, System.Data.IDbTransaction tx, Account a) => c.Execute("INSERT INTO accounts(id,email,normalized_email,is_enabled,is_administrator,authorization_version,created_at,updated_at,household_id,household_admin,grants_inherit_from_account_id,this_computer_only,must_change_password,temporary_password_expires_at,profile_id) VALUES(@Id,@Email,@NormalizedEmail,@IsEnabled,@IsAdministrator,@AuthorizationVersion,@CreatedAt,@UpdatedAt,@HouseholdId,@HouseholdAdmin,@GrantsInheritFromAccountId,@ThisComputerOnly,@MustChangePassword,@TemporaryPasswordExpiresAt,@ProfileId);", Parameters(a), tx);
    private static void InsertGrant(System.Data.IDbConnection c, System.Data.IDbTransaction tx, AccountProfileGrant g)
    {
        JoinHousehold(c, tx, g.AccountId, g.ProfileId, g.GrantedAt);
        InsertGrantRow(c, tx, g);
    }

    private static void InsertGrantRow(System.Data.IDbConnection c, System.Data.IDbTransaction tx, AccountProfileGrant g) => c.Execute("INSERT INTO account_profile_grants(account_id,profile_id,is_default,is_enabled,admin_enabled,authorization_version,granted_at) VALUES(@AccountId,@ProfileId,@IsDefault,@IsEnabled,@AdminEnabled,@AuthorizationVersion,@GrantedAt) ON CONFLICT(account_id,profile_id) DO UPDATE SET is_default=excluded.is_default,is_enabled=excluded.is_enabled,admin_enabled=excluded.admin_enabled,authorization_version=account_profile_grants.authorization_version+1;", new { g.AccountId, g.ProfileId, IsDefault = g.IsDefault ? 1 : 0, IsEnabled = g.IsEnabled ? 1 : 0, AdminEnabled = g.AdminEnabled ? 1 : 0, g.AuthorizationVersion, GrantedAt = Iso(g.GrantedAt) }, tx);
    private static void ReplaceAccess(System.Data.IDbConnection c, System.Data.IDbTransaction tx, Guid accountId, IReadOnlySet<AccountFeatureId> features, IReadOnlySet<Guid> libraries, DateTimeOffset at)
    {
        c.Execute("DELETE FROM account_feature_grants WHERE account_id=@accountId;DELETE FROM account_library_grants WHERE account_id=@accountId;", new { accountId }, tx); foreach (var f in features)
        {
            c.Execute("INSERT INTO account_feature_grants(account_id,feature_id,granted_at) VALUES(@accountId,@feature,@at);", new { accountId, feature = f.Value, at = Iso(at) }, tx);
        }

        // The Vault is only ever opened through Vault mode, never through a library grant (owner decision 2026-10-22).
        var vaultIds = c.Query<string>("SELECT library_id FROM vault_libraries;", transaction: tx)
            .Select(text => Guid.TryParse(text, out var parsed) ? parsed : Guid.Empty)
            .ToHashSet();
        if (libraries.Any(vaultIds.Contains))
        {
            throw new ArgumentException("The Vault can't be given to an account as a library. It opens only through Vault mode.");
        }

        foreach (var id in libraries)
        {
            c.Execute("INSERT INTO account_library_grants(account_id,library_id,granted_at) VALUES(@accountId,@id,@at);", new { accountId, id, at = Iso(at) }, tx);
        }
    }
    private static void EnsureEnabledDefault(System.Data.IDbConnection c, System.Data.IDbTransaction tx, Guid accountId)
    {
        if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM account_profile_grants WHERE account_id=@accountId AND is_enabled=1;", new { accountId }, tx) == 0)
        {
            return;
        }

        if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM account_profile_grants WHERE account_id=@accountId AND is_enabled=1 AND is_default=1;", new { accountId }, tx) == 0)
        {
            c.Execute("UPDATE account_profile_grants SET is_default=1,authorization_version=authorization_version+1 WHERE rowid=(SELECT rowid FROM account_profile_grants WHERE account_id=@accountId AND is_enabled=1 ORDER BY granted_at LIMIT 1);", new { accountId }, tx);
        }
    }
    private static bool HasOtherEffectiveAdministrator(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        Guid accountId,
        Guid profileId) => connection.ExecuteScalar<int>("""
            SELECT COUNT(*)
            FROM account_profile_grants g
            JOIN accounts a ON a.id=g.account_id
            WHERE a.is_enabled=1 AND a.is_administrator=1
              AND g.is_enabled=1 AND g.admin_enabled=1
              AND NOT(g.account_id=@accountId AND g.profile_id=@profileId)
              AND (
                EXISTS(SELECT 1 FROM account_credentials c WHERE c.account_id=a.id)
                OR EXISTS(SELECT 1 FROM account_passkeys p WHERE p.account_id=a.id)
                OR EXISTS(SELECT 1 FROM account_external_logins e WHERE e.account_id=a.id)
              );
            """, new { accountId, profileId }, transaction) > 0;

    private static bool HasOtherUsableAdministratorAccount(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        Guid accountId) => connection.ExecuteScalar<int>("""
            SELECT COUNT(*)
            FROM accounts a
            JOIN account_profile_grants g ON g.account_id=a.id
            WHERE a.id<>@accountId AND a.is_enabled=1 AND a.is_administrator=1
              AND g.is_enabled=1 AND g.admin_enabled=1
              AND (
                EXISTS(SELECT 1 FROM account_credentials c WHERE c.account_id=a.id)
                OR EXISTS(SELECT 1 FROM account_passkeys p WHERE p.account_id=a.id)
                OR EXISTS(SELECT 1 FROM account_external_logins e WHERE e.account_id=a.id)
              );
            """, new { accountId }, transaction) > 0;
    private static void InsertProfileRow(
        System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, Profile profile, Guid householdId)
    {
        profile.HouseholdId = householdId;
        connection.Execute("""
            INSERT INTO profiles(id,display_name,avatar_color,avatar_image_path,avatar_icon,content_limit,content_limit_allow_unrated,role,created_at,navigation_config,household_id)
            VALUES(@Id,@DisplayName,@AvatarColor,@AvatarImagePath,@AvatarIcon,@ContentLimit,@ContentLimitAllowUnrated,@Role,@CreatedAt,@NavigationConfig,@HouseholdId);
            """, new
        {
            profile.Id,
            profile.DisplayName,
            profile.AvatarColor,
            profile.AvatarImagePath,
            profile.AvatarIcon,
            profile.ContentLimit,
            profile.ContentLimitAllowUnrated,
            Role = profile.Role.ToString(),
            CreatedAt = Iso(profile.CreatedAt),
            profile.NavigationConfig,
            profile.HouseholdId,
        }, transaction);
    }

    private const string CrossHouseholdMessage = "A sign-in can only open people from its own household.";

    private static Guid CreateHousehold(System.Data.IDbConnection c, System.Data.IDbTransaction tx, string? name, DateTimeOffset at)
    {
        var id = Guid.NewGuid();
        c.Execute("INSERT INTO households(id,name,created_at) VALUES(@id,@name,@at);",
            new { id, name = Household.DefaultNameFor(name), at = Iso(at) }, tx);
        return id;
    }

    /// <summary>
    /// The first main sign-in a household gets (never a person's own sign-in, which follows another) becomes its
    /// primary account and its household administrator. Later sign-ins leave that alone.
    /// </summary>
    private static void EnsureHouseholdPrimary(System.Data.IDbConnection c, System.Data.IDbTransaction tx, Guid accountId)
    {
        var householdId = HouseholdOfAccount(c, tx, accountId);
        if (householdId is null)
        {
            return;
        }

        var named = c.Execute("""
            UPDATE households SET primary_account_id=@accountId
            WHERE id=@householdId AND primary_account_id IS NULL
              AND EXISTS (SELECT 1 FROM accounts WHERE id=@accountId AND grants_inherit_from_account_id IS NULL);
            """, new { accountId, householdId = householdId.Value }, tx);
        if (named > 0)
        {
            // A brand-new account has no open sessions to re-check, so its authorization version stays as created.
            c.Execute("UPDATE accounts SET household_admin=1 WHERE id=@accountId;",
                new { accountId }, tx);
        }
    }

    /// <summary>
    /// Makes the sign-in open its own profile: the profile it signs in as (<paramref name="profileId"/>, the initial default grant). A
    /// profile has at most one user, so a profile that already has another sign-in is refused. Sets <see cref="Account.ProfileId"/>.
    /// </summary>
    private static void AttachToSignInProfile(System.Data.IDbConnection c, System.Data.IDbTransaction tx, Account account, Guid profileId)
    {
        if (account.ProfileId is { } named && named != profileId)
        {
            throw new InvalidOperationException("A sign-in can only be attached to the profile it opens.");
        }

        if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM accounts WHERE profile_id=@profileId AND id<>@accountId;",
                new { profileId, accountId = account.Id }, tx) > 0)
        {
            throw new InvalidOperationException(ProfileAlreadyHasSignInMessage);
        }

        account.ProfileId = profileId;
    }

    /// <summary>What a person is told when a profile that already has a sign-in is given a second one.</summary>
    internal const string ProfileAlreadyHasSignInMessage = "That profile already has a sign-in.";

    /// <summary>
    /// Brings the profile a sign-in opens (<c>accounts.profile_id</c>) in line with the account side, in the caller's transaction:
    /// <list type="bullet">
    /// <item>server administrator: the account is an administrator and has administrator access switched on for its own profile;</item>
    /// <item>household administrator: the account is one;</item>
    /// <item>Read/Watch/Listen/View and library access: what the account holds, or what the main sign-in it follows holds;</item>
    /// <item>a server administrator's household becomes the default household when there is none yet.</item>
    /// </list>
    /// The profile's authorization version goes up (unless <paramref name="bumpVersion"/> is off for a profile made in this same
    /// step). The people whose access follows this sign-in are brought in line too. Does nothing for a sign-in with no profile.
    /// This recomputes everything from the account side, so it can be called after any account-side access write.
    /// </summary>
    internal static void SyncProfileFromAccount(
        System.Data.IDbConnection c, System.Data.IDbTransaction tx, Guid accountId, bool bumpVersion = true, bool includeFollowers = true)
    {
        var account = c.QueryFirstOrDefault<SyncRow>("""
            SELECT a.profile_id AS ProfileId, a.is_administrator AS IsAdministrator, a.household_admin AS HouseholdAdmin,
                   COALESCE(a.grants_inherit_from_account_id, a.id) AS AccessAccountId,
                   EXISTS(SELECT 1 FROM account_profile_grants g
                          WHERE g.account_id=a.id AND g.profile_id=a.profile_id AND g.is_enabled=1 AND g.admin_enabled=1) AS OwnGrantAdmin
            FROM accounts a WHERE a.id=@accountId;
            """, new { accountId }, tx);
        if (account?.ProfileId is { } profileId)
        {
            var server = account.IsAdministrator && account.OwnGrantAdmin;
            c.Execute("""
                UPDATE profiles
                SET is_server_admin=@server, is_household_admin=@household, authorization_version=authorization_version+@bump
                WHERE id=@profileId;
                DELETE FROM profile_feature_grants WHERE profile_id=@profileId;
                DELETE FROM profile_library_grants WHERE profile_id=@profileId;
                INSERT INTO profile_feature_grants(profile_id,feature_id,granted_at)
                    SELECT @profileId,feature_id,granted_at FROM account_feature_grants WHERE account_id=@accessAccountId;
                INSERT INTO profile_library_grants(profile_id,library_id,granted_at)
                    SELECT @profileId,library_id,granted_at FROM account_library_grants WHERE account_id=@accessAccountId;
                """, new
            {
                profileId,
                accessAccountId = account.AccessAccountId,
                server = server ? 1 : 0,
                household = account.HouseholdAdmin ? 1 : 0,
                bump = bumpVersion ? 1 : 0,
            }, tx);
            if (server && HouseholdOfProfile(c, tx, profileId) is { } householdId)
            {
                ProfileAccessRepository.MarkDefaultHouseholdIfNone(c, tx, householdId);
            }
        }

        if (!includeFollowers)
        {
            return;
        }

        foreach (var follower in c.Query<Guid>(
                     "SELECT id FROM accounts WHERE grants_inherit_from_account_id=@accountId;", new { accountId }, tx).ToList())
        {
            SyncProfileFromAccount(c, tx, follower, bumpVersion: true, includeFollowers: false);
        }
    }

    private sealed class SyncRow
    {
        public Guid? ProfileId { get; set; }
        public bool IsAdministrator { get; set; }
        public bool HouseholdAdmin { get; set; }
        public Guid AccessAccountId { get; set; }
        public bool OwnGrantAdmin { get; set; }
    }

    /// <summary>
    /// Copies a sign-in's "ask for my PIN on admin pages" question onto the profile it signs in as, and clears that profile's earlier
    /// unlocks. Only the sign-in's own profile counts: it is the only profile a sign-in can be a server administrator on.
    /// </summary>
    private static void SyncProtectionOntoProfile(System.Data.IDbConnection c, System.Data.IDbTransaction tx, Guid accountId, Guid profileId)
    {
        if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM accounts WHERE id=@accountId AND profile_id=@profileId;", new { accountId, profileId }, tx) == 0)
        {
            return;
        }

        c.Execute("""
            INSERT INTO profile_admin_protections(profile_id,is_enabled,unlock_mode,unlock_minutes,protection_version,updated_at)
                SELECT profile_id,is_enabled,unlock_mode,unlock_minutes,protection_version,updated_at
                FROM grant_admin_protections WHERE account_id=@accountId AND profile_id=@profileId
            ON CONFLICT(profile_id) DO UPDATE SET
                is_enabled=excluded.is_enabled,unlock_mode=excluded.unlock_mode,unlock_minutes=excluded.unlock_minutes,
                protection_version=excluded.protection_version,updated_at=excluded.updated_at;
            DELETE FROM profile_admin_unlocks WHERE profile_id=@profileId;
            UPDATE profiles SET authorization_version=authorization_version+1 WHERE id=@profileId;
            """, new { accountId, profileId }, tx);
    }

    /// <summary>
    /// A person added without a sign-in starts with the Read/Watch/Listen/View access and libraries of the profile of whoever
    /// added them (<paramref name="sourceProfileId"/>); with no such profile they start with every feature and no library. Someone
    /// outside the adder's household starts with View off and no library. A source profile that does not exist gives nothing. Never an
    /// administrator role.
    /// </summary>
    private static void GiveNewProfileAccess(
        System.Data.IDbConnection c, System.Data.IDbTransaction tx, Guid profileId, Guid householdId, Guid? sourceProfileId, DateTimeOffset at)
    {
        List<string> features;
        List<Guid> libraries = [];
        if (sourceProfileId is not { } source)
        {
            features = AccountFeatureId.All.Select(feature => feature.Value).ToList();
        }
        else if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM profiles WHERE id=@source;", new { source }, tx) == 0)
        {
            features = [];
        }
        else
        {
            var sameHousehold = HouseholdOfProfile(c, tx, source) == householdId;
            features = c.Query<string>("SELECT feature_id FROM profile_feature_grants WHERE profile_id=@source;", new { source }, tx)
                .Where(feature => sameHousehold || !string.Equals(feature, AccountFeatureId.View.Value, StringComparison.Ordinal))
                .ToList();
            if (sameHousehold)
            {
                libraries = c.Query<Guid>("SELECT library_id FROM profile_library_grants WHERE profile_id=@source;", new { source }, tx).ToList();
            }
        }

        foreach (var feature in features)
        {
            c.Execute("INSERT INTO profile_feature_grants(profile_id,feature_id,granted_at) VALUES(@profileId,@feature,@at);",
                new { profileId, feature, at = Iso(at) }, tx);
        }

        foreach (var libraryId in libraries)
        {
            c.Execute("INSERT INTO profile_library_grants(profile_id,library_id,granted_at) VALUES(@profileId,@libraryId,@at);",
                new { profileId, libraryId, at = Iso(at) }, tx);
        }
    }

    private static Guid? HouseholdOfAccount(System.Data.IDbConnection c, System.Data.IDbTransaction tx, Guid accountId) =>
        c.QueryFirstOrDefault<Guid?>("SELECT household_id FROM accounts WHERE id=@accountId;", new { accountId }, tx);

    private static Guid? HouseholdOfProfile(System.Data.IDbConnection c, System.Data.IDbTransaction tx, Guid profileId) =>
        c.QueryFirstOrDefault<Guid?>("SELECT household_id FROM profiles WHERE id=@profileId;", new { profileId }, tx);

    private static void RequireRoom(System.Data.IDbConnection c, System.Data.IDbTransaction tx, Guid householdId)
    {
        if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM profiles WHERE household_id=@householdId;", new { householdId }, tx) >= Household.MaximumProfiles)
        {
            throw new InvalidOperationException(Household.FullMessage);
        }
    }

    /// <summary>
    /// The household a new account belongs to: the one it already names, else the one of the profile it is opening
    /// (<paramref name="profileId"/>), else a new household named after <paramref name="name"/>.
    /// </summary>
    private static Guid ResolveAccountHousehold(
        System.Data.IDbConnection c, System.Data.IDbTransaction tx, Account account, Guid? profileId, string? name)
    {
        if (account.HouseholdId is { } named)
        {
            if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM households WHERE id=@named;", new { named }, tx) == 0)
            {
                throw new KeyNotFoundException("Household not found.");
            }

            return named;
        }

        var fromProfile = profileId is { } p ? HouseholdOfProfile(c, tx, p) : null;
        return fromProfile ?? CreateOrAdoptHousehold(c, tx, name, account);
    }

    /// <summary>
    /// A new household for <paramref name="account"/>. The first server administrator joins the server's still-empty
    /// household (made by the Shared Library upgrade, or by the first shared item) instead of leaving it as a phantom.
    /// </summary>
    private static Guid CreateOrAdoptHousehold(
        System.Data.IDbConnection c, System.Data.IDbTransaction tx, string? name, Account account)
    {
        if (account.IsAdministrator &&
            c.QueryFirstOrDefault<Guid?>("""
                SELECT h.id FROM households h
                WHERE NOT EXISTS (SELECT 1 FROM accounts a WHERE a.household_id = h.id)
                  AND NOT EXISTS (SELECT 1 FROM profiles p WHERE p.household_id = h.id)
                  AND EXISTS (SELECT 1 FROM view_shared_library v WHERE v.household_id = h.id)
                ORDER BY h.created_at, h.id LIMIT 1;
                """, transaction: tx) is { } empty)
        {
            return empty;
        }

        return CreateHousehold(c, tx, name, account.CreatedAt);
    }

    /// <summary>
    /// Keeps an account and a profile in one household. A profile without a household (only the seeded Owner,
    /// before the first administrator exists) joins the account's household, and counts towards its limit of
    /// eight. Two different households are refused.
    /// </summary>
    private static void JoinHousehold(
        System.Data.IDbConnection c, System.Data.IDbTransaction tx, Guid accountId, Guid profileId, DateTimeOffset at)
    {
        var accountHousehold = HouseholdOfAccount(c, tx, accountId);
        var profileHousehold = HouseholdOfProfile(c, tx, profileId);
        if (accountHousehold is { } a && profileHousehold is { } p)
        {
            if (a != p)
            {
                throw new InvalidOperationException(CrossHouseholdMessage);
            }

            return;
        }

        if (accountHousehold is null && profileHousehold is null)
        {
            var created = CreateHousehold(c, tx, null, at);
            c.Execute("UPDATE accounts SET household_id=@created WHERE id=@accountId;", new { created, accountId }, tx);
            c.Execute("UPDATE profiles SET household_id=@created WHERE id=@profileId;", new { created, profileId }, tx);
        }
        else if (accountHousehold is null)
        {
            c.Execute("UPDATE accounts SET household_id=@profileHousehold WHERE id=@accountId;", new { profileHousehold, accountId }, tx);
        }
        else
        {
            RequireRoom(c, tx, accountHousehold.Value);
            c.Execute("UPDATE profiles SET household_id=@accountHousehold WHERE id=@profileId;", new { accountHousehold, profileId }, tx);
        }
    }
    private const string GrantSelect = "SELECT account_id AS AccountId,profile_id AS ProfileId,is_default AS IsDefault,is_enabled AS IsEnabled,admin_enabled AS AdminEnabled,authorization_version AS AuthorizationVersion,granted_at AS GrantedAt FROM account_profile_grants";
    private static AccountProfileGrant MapGrant(GrantRow r) => new() { AccountId = r.AccountId, ProfileId = r.ProfileId, IsDefault = r.IsDefault, IsEnabled = r.IsEnabled, AdminEnabled = r.AdminEnabled, AuthorizationVersion = r.AuthorizationVersion, GrantedAt = ParseRequired(r.GrantedAt) };
    private static GrantAdminProtection MapProtection(ProtectionRow r) => new() { AccountId = r.AccountId, ProfileId = r.ProfileId, IsEnabled = r.IsEnabled, UnlockMode = r.UnlockMode, UnlockMinutes = r.UnlockMinutes, ProtectionVersion = r.ProtectionVersion, UpdatedAt = ParseRequired(r.UpdatedAt) };
    private static GrantAdminUnlock MapUnlock(UnlockRow r) => new() { SessionId = r.SessionId, AccountId = r.AccountId, ProfileId = r.ProfileId, ProtectionVersion = r.ProtectionVersion, Method = r.Method, GrantedAt = ParseRequired(r.GrantedAt), ExpiresAt = Parse(r.ExpiresAt) };
    private static string Iso(DateTimeOffset v) => v.ToString("O", System.Globalization.CultureInfo.InvariantCulture); private static DateTimeOffset ParseRequired(string v) => DateTimeOffset.Parse(v, System.Globalization.CultureInfo.InvariantCulture); private static DateTimeOffset? Parse(string? v) => string.IsNullOrWhiteSpace(v) ? null : ParseRequired(v);
    private sealed class GrantRow { public Guid AccountId { get; set; } public Guid ProfileId { get; set; } public bool IsDefault { get; set; } public bool IsEnabled { get; set; } public bool AdminEnabled { get; set; } public long AuthorizationVersion { get; set; } public string GrantedAt { get; set; } = ""; }
    private sealed class ProtectionRow { public Guid AccountId { get; set; } public Guid ProfileId { get; set; } public bool IsEnabled { get; set; } public string UnlockMode { get; set; } = ""; public int? UnlockMinutes { get; set; } public long ProtectionVersion { get; set; } public string UpdatedAt { get; set; } = ""; }
    private sealed class UnlockRow { public Guid SessionId { get; set; } public Guid AccountId { get; set; } public Guid ProfileId { get; set; } public long ProtectionVersion { get; set; } public string Method { get; set; } = ""; public string GrantedAt { get; set; } = ""; public string? ExpiresAt { get; set; } }
}
