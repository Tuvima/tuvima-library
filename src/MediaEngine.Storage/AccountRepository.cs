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
            // The sign-in's own columns only. Administrator roles live on the profile and are changed through the profile access
            // repository, so saving a sign-in read a moment ago can never put back a role that was just changed.
            return conn.Execute("""
                UPDATE accounts SET email = @Email, normalized_email = @NormalizedEmail,
                    is_enabled = @IsEnabled, is_administrator = @IsAdministrator,
                    this_computer_only = @ThisComputerOnly,
                    authorization_version = authorization_version + 1, updated_at = @UpdatedAt
                WHERE id = @Id;
                """, Parameters(account), tx) > 0;
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
        }, ct);
    }

    public Task<bool> RevokeProfileAsync(Guid accountId, Guid profileId, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            return conn.Execute(
                "DELETE FROM account_profile_grants WHERE account_id = @accountId AND profile_id = @profileId;",
                new { accountId, profileId }, tx) > 0;
        }, ct);

    public Task<bool> HasProfileAccessAsync(Guid accountId, Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        return Task.FromResult(conn.ExecuteScalar<int>("""
            SELECT COUNT(1)
            FROM accounts a
            JOIN profiles own ON own.id = a.profile_id
            JOIN profiles target ON target.id = @profileId
            WHERE a.id = @accountId AND own.household_id IS NOT NULL AND target.household_id = own.household_id;
            """, new { accountId, profileId }) > 0);
    }

    public Task<IReadOnlyList<Guid>> GetProfileIdsAsync(Guid accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var ids = conn.Query<Guid>("""
            SELECT p.id
            FROM accounts a
            JOIN profiles own ON own.id = a.profile_id
            JOIN profiles p ON p.household_id = own.household_id
            WHERE a.id = @accountId AND own.household_id IS NOT NULL
            ORDER BY (p.id = a.profile_id) DESC, p.created_at, p.id;
            """, new { accountId }).ToList();
        return Task.FromResult<IReadOnlyList<Guid>>(ids);
    }

    public Task<bool> AreServerAdministratorsSecuredAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        // A server administrator profile is unsecured when it has no user, or its user has no email, or no password and no passkey.
        var unsecured = conn.ExecuteScalar<int>($"""
            SELECT COUNT(*) FROM profiles p
            WHERE p.is_server_admin=1
              AND NOT EXISTS (SELECT 1 FROM accounts a WHERE a.profile_id=p.id AND {SecuredUserSql});
            """);
        return Task.FromResult(unsecured == 0);
    }

    public Task<bool> IsUserSecuredAsync(Guid userId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        return Task.FromResult(conn.ExecuteScalar<int>(
            $"SELECT COUNT(*) FROM accounts a WHERE a.id=@userId AND {SecuredUserSql};", new { userId }) > 0);
    }

    /// <summary>"The user <c>a</c> has an email and a password or passkey", for use in a query over <c>accounts a</c>.</summary>
    private const string SecuredUserSql = """
        a.email IS NOT NULL AND TRIM(a.email)<>''
        AND a.normalized_email IS NOT NULL AND TRIM(a.normalized_email)<>''
        AND (EXISTS(SELECT 1 FROM account_credentials c WHERE c.account_id=a.id)
             OR EXISTS(SELECT 1 FROM account_passkeys k WHERE k.account_id=a.id))
        """;

    public Task<Guid?> GetDefaultProfileIdAsync(Guid accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var value = conn.QueryFirstOrDefault<Guid?>("""
            SELECT p.id FROM accounts a JOIN profiles p ON p.id = a.profile_id WHERE a.id = @accountId;
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

    // The household of a user is its own profile's household (the same source authority uses); accounts.household_id is written as
    // before but read for no decision.
    private const string Select = """
        SELECT id AS Id, email AS Email, normalized_email AS NormalizedEmail,
               is_enabled AS IsEnabled,
               COALESCE((SELECT p.is_server_admin FROM profiles p WHERE p.id = accounts.profile_id), 0) AS IsAdministrator,
               authorization_version AS AuthorizationVersion,
               created_at AS CreatedAt, updated_at AS UpdatedAt,
               (SELECT p.household_id FROM profiles p WHERE p.id = accounts.profile_id) AS HouseholdId,
               COALESCE((SELECT p.is_household_admin FROM profiles p WHERE p.id = accounts.profile_id), 0) AS HouseholdAdmin,
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

    public Task<ActiveProfileAccess?> GetActiveProfileAccessAsync(Guid accountId, Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<ActiveProfileRow>("""
            SELECT p.id AS ProfileId, p.household_id AS HouseholdId, p.role AS Role,
                   p.is_server_admin AS IsServerAdministrator, p.is_household_admin AS IsHouseholdAdministrator,
                   p.authorization_version AS AuthorizationVersion,
                   EXISTS(SELECT 1 FROM accounts u WHERE u.profile_id = p.id AND u.is_enabled = 1) AS HasEnabledUser
            FROM accounts a
            JOIN profiles own ON own.id = a.profile_id
            JOIN profiles p ON p.id = @profileId
            WHERE a.id = @accountId AND own.household_id IS NOT NULL AND p.household_id = own.household_id;
            """, new { accountId, profileId });
        return Task.FromResult(row is null || row.HouseholdId is not { } household
            ? null
            : new ActiveProfileAccess(row.ProfileId, household, Enum.Parse<ProfileRole>(row.Role), row.IsServerAdministrator,
                row.IsHouseholdAdministrator, row.HasEnabledUser, row.AuthorizationVersion));
    }

    private sealed class ActiveProfileRow
    {
        public Guid ProfileId { get; set; }
        public Guid? HouseholdId { get; set; }
        public string Role { get; set; } = string.Empty;
        public bool IsServerAdministrator { get; set; }
        public bool IsHouseholdAdministrator { get; set; }
        public bool HasEnabledUser { get; set; }
        public long AuthorizationVersion { get; set; }
    }

    public Task UpdateAccountAsync(Account account, CancellationToken ct = default) => db.ExecuteWriteAsync((conn, tx, token) =>
    {
        token.ThrowIfCancellationRequested();
        var current = conn.QuerySingleOrDefault<AccountRow>(Select + " WHERE id=@Id;", account, tx)
            ?? throw new InvalidOperationException("Account not found.");
        // Switching a sign-in off must leave a server administrator who can still sign in. (Demoting is a change to the profile's
        // role, made through the profile access repository, which keeps the same promise.)
        if (current.IsEnabled && current.IsAdministrator && !account.IsEnabled
           && !ProfileAccessRepository.HasUsableServerAdministratorWithoutAccount(conn, tx, account.Id))
        {
            throw new InvalidOperationException("The final effective administrator account cannot be disabled or demoted.");
        }

        if (account.IsEnabled && conn.ExecuteScalar<int>("SELECT COUNT(*) FROM account_profile_grants WHERE account_id=@Id AND is_enabled=1;", account, tx) == 0)
        {
            throw new InvalidOperationException("An enabled account must retain an enabled profile grant.");
        }

        conn.Execute("UPDATE accounts SET email=@Email,normalized_email=@NormalizedEmail,is_enabled=@IsEnabled,is_administrator=@IsAdministrator,authorization_version=authorization_version+1,updated_at=@UpdatedAt WHERE id=@Id;", Parameters(account), tx);
    }, ct);

    public Task DeleteAccountAsync(Guid accountId, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            DeleteAccountRows(connection, transaction, accountId);
        }, ct);

    public Task<UserRemovalPlan> PlanUserRemovalAsync(Guid userId, CancellationToken ct = default) =>
        db.ExecuteReadAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return PlanUserRemoval(connection, transaction, userId);
        }, ct);

    public Task<UserRemovalOutcome> DeleteUserAsync(
        Guid userId, IReadOnlyCollection<Guid>? confirmedProfileIds = null, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            // Worked out inside the transaction that deletes, so what goes is exactly what was just checked.
            var plan = PlanUserRemoval(connection, transaction, userId);
            var profileIds = plan.RemovesHousehold ? plan.HouseholdProfiles.Select(profile => profile.Id).ToArray() : [];
            // What goes must be exactly what the caller was shown and confirmed; otherwise nothing is deleted.
            if (confirmedProfileIds is not null && !confirmedProfileIds.ToHashSet().SetEquals(profileIds))
            {
                throw new AccountRemovalChangedException();
            }

            if (!plan.RemovesHousehold)
            {
                // D5: only the sign-in goes. The profile and everything in it stay (it loses any administrator role inside).
                DeleteAccountRows(connection, transaction, userId);
                return new UserRemovalOutcome(null, [], [userId]);
            }

            // D6: the household goes with every profile in it, unless its Shared Library still holds shared photos.
            if (plan.HouseholdHoldsSharedItems)
            {
                throw new InvalidOperationException(UserRemovalMessages.HouseholdHoldsSharedItems);
            }

            var householdId = plan.HouseholdId!.Value;
            var userIds = connection.Query<Guid>("""
                SELECT a.id FROM accounts a
                WHERE a.profile_id IN (SELECT id FROM profiles WHERE household_id=@householdId)
                   OR a.household_id=@householdId
                ORDER BY a.created_at, a.id;
                """, new { householdId }, transaction).ToList();
            foreach (var id in userIds)
            {
                DeleteAccountRows(connection, transaction, id);
            }

            foreach (var profileId in profileIds)
            {
                RequireNotFinalAdministratorProfile(connection, transaction, profileId);
                DeleteProfileRows(connection, transaction, profileId);
            }

            var removed = HouseholdRepository.DeleteEmptyHousehold(connection, transaction, householdId);
            if (removed != HouseholdDeleteOutcome.Deleted)
            {
                // Everything above is undone with the transaction.
                throw new InvalidOperationException(removed == HouseholdDeleteOutcome.HoldsSharedItems
                    ? UserRemovalMessages.HouseholdHoldsSharedItems
                    : UserRemovalMessages.HouseholdCouldNotBeRemoved);
            }

            return new UserRemovalOutcome(householdId, profileIds, userIds);
        }, ct);

    /// <summary>The account row's own work: the last-administrator backstop, handing its followers on, and removing it.</summary>
    private static void DeleteAccountRows(SqliteConnection connection, SqliteTransaction transaction, Guid accountId)
    {
        var account = connection.QuerySingleOrDefault<AccountRow>(
            Select + " WHERE id=@accountId;", new { accountId }, transaction)
            ?? throw new KeyNotFoundException("Account not found.");
        if (account.IsEnabled && account.IsAdministrator &&
            !ProfileAccessRepository.HasUsableServerAdministratorWithoutAccount(connection, transaction, accountId))
        {
            throw new InvalidOperationException("The final effective administrator account cannot be deleted.");
        }

        // The only server administrator profile never loses its role by losing a sign-in that cannot sign in anyway.
        if (account.ProfileId is { } adminProfileId &&
            connection.ExecuteScalar<int>("""
                SELECT EXISTS(SELECT 1 FROM profiles WHERE id=@adminProfileId AND is_server_admin=1)
                   AND NOT EXISTS(SELECT 1 FROM accounts WHERE profile_id=@adminProfileId AND id<>@accountId)
                   AND NOT EXISTS(SELECT 1 FROM profiles WHERE is_server_admin=1 AND id<>@adminProfileId);
                """, new { adminProfileId, accountId }, transaction) == 1)
        {
            throw new InvalidOperationException("The final effective administrator account cannot be deleted.");
        }

        // People whose access followed this account follow the household's next main sign-in instead.
        connection.Execute("""
            UPDATE accounts SET grants_inherit_from_account_id=(
                SELECT other.id FROM accounts other
                WHERE (SELECT po.household_id FROM profiles po WHERE po.id=other.profile_id)
                    = (SELECT pa.household_id FROM profiles pa WHERE pa.id=accounts.profile_id)
                  AND other.id<>@accountId
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
        // A person left without a sign-in cannot be an administrator (D15). The last-administrator checks above and in the service
        // run first, so this never removes the last one that can sign in.
        if (account.ProfileId is { } profileId)
        {
            connection.Execute("""
                UPDATE profiles
                SET is_server_admin=0, is_household_admin=0, authorization_version=authorization_version+1
                WHERE id=@profileId AND (is_server_admin=1 OR is_household_admin=1)
                  AND NOT EXISTS (SELECT 1 FROM accounts WHERE profile_id=@profileId);
                """, new { profileId }, transaction);
        }
    }

    public Task<IReadOnlyList<AccountProfileGrant>> GetGrantsAsync(Guid accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection();
        // What a user may act as is its own profile and the rest of its household; roles and versions are the profile's.
        var rows = conn.Query<GrantRow>("""
            SELECT a.id AS AccountId, p.id AS ProfileId, (p.id = a.profile_id) AS IsDefault, 1 AS IsEnabled,
                   p.is_server_admin AS AdminEnabled, p.authorization_version AS AuthorizationVersion, p.created_at AS GrantedAt
            FROM accounts a
            JOIN profiles own ON own.id = a.profile_id
            JOIN profiles p ON p.household_id = own.household_id
            WHERE a.id = @accountId AND own.household_id IS NOT NULL
            ORDER BY (p.id = a.profile_id) DESC, p.created_at, p.id;
            """, new { accountId });
        return Task.FromResult<IReadOnlyList<AccountProfileGrant>>(rows.Select(MapGrant).ToList());
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
        CancellationToken ct = default, Profile? newProfile = null, bool preserveProfileAccess = false) =>
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
            // A brand-new profile (or one the caller asked to set up, like the administrator's first start) takes the roles and access
            // this sign-in was created with. A person who already exists keeps exactly what they have.
            if (newProfile is not null || !preserveProfileAccess)
            {
                SeedProfileFromAccount(connection, transaction, account.Id, bumpVersion: newProfile is null);
            }
            else
            {
                ClearStaleRolesForNewSignIn(connection, transaction, account);
            }
        }, ct);

    public Task CreateInvitedAccountAsync(
        Account account,
        IReadOnlyList<AccountProfileGrant> grants,
        AccountInvitation invitation,
        CancellationToken ct = default,
        Profile? newProfile = null,
        bool preserveProfileAccess = false) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (grants.Count is 0 || grants.Count > Household.MaximumProfiles || grants.Count(grant => grant.IsDefault && grant.IsEnabled) != 1 ||
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

            if (account.HouseholdId is { } named && connection.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM households WHERE id=@named;", new { named }, transaction) == 0)
            {
                throw new KeyNotFoundException("Household not found.");
            }

            account.HouseholdId ??= profileHouseholds.Count == 1
                ? profileHouseholds[0]
                : CreateOrAdoptHousehold(connection, transaction, newProfile?.DisplayName ?? account.Email, account);
            if (newProfile is not null)
            {
                // A household holds up to eight people (D14), however the new person arrives.
                RequireRoom(connection, transaction, account.HouseholdId.Value);
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
            if (newProfile is not null || !preserveProfileAccess)
            {
                SeedProfileFromAccount(connection, transaction, account.Id, bumpVersion: newProfile is null);
            }
            else
            {
                ClearStaleRolesForNewSignIn(connection, transaction, account);
            }
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

        // The limit is the household's (eight profiles, D14, checked below), not a count of the sign-in's own grants.
        var grantCount = connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM account_profile_grants WHERE account_id=@AccountId;",
            targetGrant, transaction);
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

    public Task<ProfileRemovalOutcome> DeleteManagedProfileAsync(Guid profileId, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            RequireProfileRemovable(connection, transaction, profileId);
            // D7: a sign-in without a profile cannot exist, so the profile's user goes with it. The user is deleted through the same
            // steps as any other user delete (its hand-overs and the last-administrator backstop), then the profile and everything
            // personal in it.
            var userId = connection.QueryFirstOrDefault<Guid?>(
                "SELECT id FROM accounts WHERE profile_id=@profileId;", new { profileId }, transaction);
            if (userId is { } id)
            {
                DeleteAccountRows(connection, transaction, id);
            }

            DeleteProfileRows(connection, transaction, profileId);
            return new ProfileRemovalOutcome(userId);
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
    /// What deleting a user does (D5, D6). Only the sign-in goes, unless it is the last enabled user attached to a
    /// household-administrator profile of a household that is not the default one: then the household goes with every profile in it.
    /// Reads the household's profiles and whether its Shared Library holds shared photos, so the caller can refuse before it moves
    /// anything. Throws when the user does not exist.
    /// </summary>
    private static UserRemovalPlan PlanUserRemoval(SqliteConnection connection, SqliteTransaction transaction, Guid userId)
    {
        var user = connection.QuerySingleOrDefault<AccountRow>(Select + " WHERE id=@userId;", new { userId }, transaction)
            ?? throw new KeyNotFoundException("User not found.");
        var profile = user.ProfileId is { } profileId
            ? connection.QuerySingleOrDefault<PlanProfileRow>("""
                SELECT id AS Id, display_name AS DisplayName, household_id AS HouseholdId,
                       is_server_admin AS IsServerAdministrator, is_household_admin AS IsHouseholdAdministrator
                FROM profiles WHERE id=@profileId;
                """, new { profileId }, transaction)
            : null;
        if (profile?.HouseholdId is not { } householdId)
        {
            return new UserRemovalPlan(user.Id, user.Email, profile?.Id, profile?.DisplayName, null, null, false,
                profile?.IsServerAdministrator ?? false, profile?.IsHouseholdAdministrator ?? false, false, [], false);
        }

        var household = connection.QuerySingleOrDefault<(string Name, bool IsDefault)>(
            "SELECT name AS Name, is_default AS IsDefault FROM households WHERE id=@householdId;", new { householdId }, transaction);
        // Another enabled user on a household-administrator profile of the same household means someone can still look after it.
        var someoneElseLooksAfter = connection.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM accounts a
            JOIN profiles p ON p.id=a.profile_id
            WHERE p.household_id=@householdId AND p.is_household_admin=1 AND a.is_enabled=1 AND a.id<>@userId;
            """, new { householdId, userId }, transaction) > 0;
        var removesHousehold = !household.IsDefault && profile.IsHouseholdAdministrator && !someoneElseLooksAfter;
        IReadOnlyList<Profile> householdProfiles = removesHousehold
            ? connection.Query<OwnProfileRow>("""
                    SELECT id AS Id, display_name AS DisplayName, avatar_color AS AvatarColor, avatar_image_path AS AvatarImagePath,
                           avatar_icon AS AvatarIcon, household_id AS HouseholdId, role AS Role, created_at AS CreatedAt
                    FROM profiles WHERE household_id=@householdId ORDER BY created_at, id;
                    """, new { householdId }, transaction)
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
                }).ToList()
            : [];
        return new UserRemovalPlan(user.Id, user.Email, profile.Id, profile.DisplayName, householdId, household.Name, household.IsDefault,
            profile.IsServerAdministrator, profile.IsHouseholdAdministrator, removesHousehold, householdProfiles,
            removesHousehold && HouseholdRepository.HoldsSharedItems(connection, transaction, householdId));
    }

    private sealed class PlanProfileRow
    {
        public Guid Id { get; set; }
        public string DisplayName { get; set; } = "";
        public Guid? HouseholdId { get; set; }
        public bool IsServerAdministrator { get; set; }
        public bool IsHouseholdAdministrator { get; set; }
    }

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

        // A household always keeps at least one household administrator profile (D7): the last one cannot be deleted.
        if (connection.ExecuteScalar<int>("""
                SELECT EXISTS(SELECT 1 FROM profiles WHERE id=@profileId AND is_household_admin=1 AND household_id IS NOT NULL)
                   AND NOT EXISTS(SELECT 1 FROM profiles other
                                  WHERE other.id<>@profileId AND other.is_household_admin=1
                                    AND other.household_id=(SELECT household_id FROM profiles WHERE id=@profileId));
                """, new { profileId }, transaction) == 1)
        {
            throw new InvalidOperationException(UserRemovalMessages.LastHouseholdAdministrator);
        }

        RequireNotFinalAdministratorProfile(connection, transaction, profileId);
    }

    /// <summary>What a person is told when a delete reaches a profile that still has a sign-in (a path that did not remove it first).</summary>
    internal const string ProfileHasSignInMessage = "This person has their own sign-in. Delete the sign-in first.";

    /// <summary>
    /// A profile row is never removed while a sign-in is attached: removing it would also remove the sign-in (the data store
    /// cascades) without the hand-overs <c>DeleteAccountRows</c> does (the household's primary sign-in, the people whose access
    /// follows it). Every delete path removes the user first; this is the backstop for one that does not.
    /// </summary>
    private static void RequireNoLinkedSignIn(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, Guid profileId)
    {
        if (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM accounts WHERE profile_id=@profileId;", new { profileId }, transaction) > 0)
        {
            throw new InvalidOperationException(ProfileHasSignInMessage);
        }
    }

    /// <summary>Backstop: the last server administrator profile with an enabled user who can sign in cannot be deleted.</summary>
    private static void RequireNotFinalAdministratorProfile(SqliteConnection connection, SqliteTransaction transaction, Guid profileId)
    {
        if (ProfileAccessRepository.IsUsableServerAdministrator(connection, transaction, profileId) &&
            !ProfileAccessRepository.HasAnotherUsableServerAdministrator(connection, transaction, profileId))
        {
            throw new InvalidOperationException("The final effective administrator profile cannot be deleted.");
        }
    }

    public Task UpsertGrantAsync(AccountProfileGrant grant, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            // The limit is the household's (eight profiles, D14), enforced when a profile joins a household.
            if (grant.IsDefault && !grant.IsEnabled)
            {
                throw new InvalidOperationException("The default profile grant must be enabled.");
            }

            var account = connection.QuerySingle<(bool IsEnabled, bool IsAdministrator)>(
                "SELECT is_enabled AS IsEnabled," + AdministratorSql + " AS IsAdministrator FROM accounts WHERE id=@AccountId;",
                grant, transaction);
            if (grant.AdminEnabled && !account.IsAdministrator)
            {
                throw new InvalidOperationException("Only administrator accounts can enable grant administration.");
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
        }, ct);

    public Task RevokeGrantAsync(Guid accountId, Guid profileId, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var account = connection.QuerySingle<(bool IsEnabled, bool IsAdministrator)>(
                "SELECT is_enabled AS IsEnabled," + AdministratorSql + " AS IsAdministrator FROM accounts WHERE id=@accountId;",
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

            connection.Execute("""
                DELETE FROM account_profile_grants WHERE account_id=@accountId AND profile_id=@profileId;
                """, new { accountId, profileId }, transaction);
            EnsureEnabledDefault(connection, transaction, accountId);
            connection.Execute("""
                UPDATE accounts SET authorization_version=authorization_version+1,updated_at=@now
                WHERE id=@accountId;
                """, new { accountId, now = Iso(DateTimeOffset.UtcNow) }, transaction);
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
                "SELECT (SELECT p.household_id FROM profiles p WHERE p.id = accounts.profile_id) AS HouseholdId, (grants_inherit_from_account_id IS NOT NULL) AS Follows FROM accounts WHERE id=@accountId;",
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
        }, ct);

    public Task<bool> HasEnabledAdminProtectionAsync(Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection();
        return Task.FromResult(conn.ExecuteScalar<int>(
            "SELECT EXISTS(SELECT 1 FROM profile_admin_protections WHERE profile_id=@profileId AND is_enabled=1);", new { profileId }) == 1);
    }

    public Task ClearAdminUnlockAsync(Guid sessionId, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, _) => connection.Execute(
            "DELETE FROM profile_admin_unlocks WHERE session_id=@sessionId;",
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
    /// Sets up the profile a new sign-in opens from what the sign-in was created with, in the caller's transaction (profile-held
    /// access): the roles (server administrator when the account is an administrator with administrator access on its own profile,
    /// household administrator when it is its household's first main sign-in), the Read/Watch/Listen/View and library access it was
    /// given, and the server administrator's household as the default household when there is none yet. Only for a brand-new profile
    /// or a first start; a person who already exists is never changed by getting a sign-in. Does nothing for a sign-in with no profile.
    /// </summary>
    internal static void SeedProfileFromAccount(
        System.Data.IDbConnection c, System.Data.IDbTransaction tx, Guid accountId, bool bumpVersion = true)
    {
        var account = c.QueryFirstOrDefault<SyncRow>("""
            SELECT a.profile_id AS ProfileId, a.is_administrator AS IsAdministrator, a.household_admin AS HouseholdAdmin,
                   COALESCE(a.grants_inherit_from_account_id, a.id) AS AccessAccountId,
                   EXISTS(SELECT 1 FROM account_profile_grants g
                          WHERE g.account_id=a.id AND g.profile_id=a.profile_id AND g.is_enabled=1 AND g.admin_enabled=1) AS OwnGrantAdmin
            FROM accounts a WHERE a.id=@accountId;
            """, new { accountId }, tx);
        if (account?.ProfileId is not { } profileId)
        {
            return;
        }

        var server = account.IsAdministrator && account.OwnGrantAdmin;
        if (server)
        {
            // D17: a server administrator profile lives in the Library's own household.
            ProfileAccessRepository.RequireInDefaultHousehold(c, tx, profileId);
        }

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

    /// <summary>
    /// A person who already exists gets a sign-in: no administrator role left on the profile from an earlier sign-in comes back with
    /// it. The profile had no sign-in until now, so clearing a role cannot strand a server administrator; a role is given again only
    /// by an explicit choice afterwards (the server administrator's own, in the service). Their Read/Watch/Listen/View and library
    /// access stay as they are.
    /// </summary>
    private static void ClearStaleRolesForNewSignIn(System.Data.IDbConnection c, System.Data.IDbTransaction tx, Account account)
    {
        if (account.ProfileId is not { } profileId)
        {
            return;
        }

        c.Execute("""
            UPDATE profiles
            SET is_server_admin=0, is_household_admin=0, authorization_version=authorization_version+1
            WHERE id=@profileId AND (is_server_admin=1 OR is_household_admin=1);
            """, new { profileId }, tx);
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

    // Creation-time bookkeeping (household size, joining a household): the household of the profile the sign-in opens. The written
    // accounts.household_id column answers only for a sign-in that has no profile yet; no access decision reads it.
    private static Guid? HouseholdOfAccount(System.Data.IDbConnection c, System.Data.IDbTransaction tx, Guid accountId) =>
        c.QueryFirstOrDefault<Guid?>(
            "SELECT COALESCE(p.household_id, a.household_id) FROM accounts a LEFT JOIN profiles p ON p.id=a.profile_id WHERE a.id=@accountId;",
            new { accountId }, tx);

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
    /// <summary>SQL for "the profile this sign-in opens is a server administrator" (0 or 1), for use in a query over <c>accounts</c>.</summary>
    private const string AdministratorSql = "COALESCE((SELECT p.is_server_admin FROM profiles p WHERE p.id = accounts.profile_id), 0)";

    private const string GrantSelect = "SELECT account_id AS AccountId,profile_id AS ProfileId,is_default AS IsDefault,is_enabled AS IsEnabled,admin_enabled AS AdminEnabled,authorization_version AS AuthorizationVersion,granted_at AS GrantedAt FROM account_profile_grants";
    private static AccountProfileGrant MapGrant(GrantRow r) => new() { AccountId = r.AccountId, ProfileId = r.ProfileId, IsDefault = r.IsDefault, IsEnabled = r.IsEnabled, AdminEnabled = r.AdminEnabled, AuthorizationVersion = r.AuthorizationVersion, GrantedAt = ParseRequired(r.GrantedAt) };
    private static string Iso(DateTimeOffset v) => v.ToString("O", System.Globalization.CultureInfo.InvariantCulture); private static DateTimeOffset ParseRequired(string v) => DateTimeOffset.Parse(v, System.Globalization.CultureInfo.InvariantCulture); private static DateTimeOffset? Parse(string? v) => string.IsNullOrWhiteSpace(v) ? null : ParseRequired(v);
    private sealed class GrantRow { public Guid AccountId { get; set; } public Guid ProfileId { get; set; } public bool IsDefault { get; set; } public bool IsEnabled { get; set; } public bool AdminEnabled { get; set; } public long AuthorizationVersion { get; set; } public string GrantedAt { get; set; } = ""; }
}
