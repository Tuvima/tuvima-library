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

    public Task<IReadOnlyList<Account>> GetEnabledServerAdministratorsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var rows = conn.Query<AccountRow>(Select + """
             WHERE is_enabled = 1 AND profile_id IN (SELECT id FROM profiles WHERE is_server_admin = 1) ORDER BY created_at;
            """).Select(Map).ToList();
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
                    is_enabled = @IsEnabled,
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
    internal const string SecuredUserSql = """
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

    // The household of a user is its own profile's household (the same source authority uses); the roles are the profile's.
    private const string Select = """
        SELECT id AS Id, email AS Email, normalized_email AS NormalizedEmail,
               is_enabled AS IsEnabled,
               COALESCE((SELECT p.is_server_admin FROM profiles p WHERE p.id = accounts.profile_id), 0) AS IsServerAdministrator,
               authorization_version AS AuthorizationVersion,
               created_at AS CreatedAt, updated_at AS UpdatedAt,
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
        account.AuthorizationVersion,
        CreatedAt = account.CreatedAt.ToString("O"),
        UpdatedAt = account.UpdatedAt.ToString("O"),
        ThisComputerOnly = account.IsThisComputerOnly ? 1 : 0,
        MustChangePassword = account.MustChangePassword ? 1 : 0,
        TemporaryPasswordExpiresAt = account.TemporaryPasswordExpiresAt?.ToString("O"),
        account.ProfileId,
    };

    private static Account Map(AccountRow row)
    {
        var account = new Account
        {
            Id = row.Id,
            Email = row.Email,
            NormalizedEmail = row.NormalizedEmail,
            IsEnabled = row.IsEnabled,
            AuthorizationVersion = row.AuthorizationVersion,
            CreatedAt = DateTimeOffset.Parse(row.CreatedAt),
            UpdatedAt = DateTimeOffset.Parse(row.UpdatedAt),
            ProfileId = row.ProfileId,
            MustChangePassword = row.MustChangePassword,
            TemporaryPasswordExpiresAt = string.IsNullOrWhiteSpace(row.TemporaryPasswordExpiresAt)
                ? null
                : DateTimeOffset.Parse(row.TemporaryPasswordExpiresAt),
        };
        if (row.ThisComputerOnly)
        {
            account.MarkThisComputerOnly();
        }

        return account;
    }

    private sealed class AccountRow
    {
        public Guid Id { get; set; }
        public string? Email { get; set; }
        public string? NormalizedEmail { get; set; }
        public bool IsEnabled { get; set; }
        public bool IsServerAdministrator { get; set; }
        public long AuthorizationVersion { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
        public bool ThisComputerOnly { get; set; }
        public bool MustChangePassword { get; set; }
        public string? TemporaryPasswordExpiresAt { get; set; }
        public Guid ProfileId { get; set; }
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
        if (current.IsEnabled && current.IsServerAdministrator && !account.IsEnabled
           && !ProfileAccessRepository.HasUsableServerAdministratorWithoutAccount(conn, tx, account.Id))
        {
            throw new InvalidOperationException("The final effective administrator account cannot be disabled or demoted.");
        }

        conn.Execute("UPDATE accounts SET email=@Email,normalized_email=@NormalizedEmail,is_enabled=@IsEnabled,authorization_version=authorization_version+1,updated_at=@UpdatedAt WHERE id=@Id;", Parameters(account), tx);
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
            // A household removal always needs that list: without one nothing is deleted.
            if (confirmedProfileIds is null ? plan.RemovesHousehold : !confirmedProfileIds.ToHashSet().SetEquals(profileIds))
            {
                throw new AccountRemovalChangedException();
            }

            if (!plan.RemovesHousehold)
            {
                // D5: only the sign-in goes. The profile and everything in it stay (it loses any administrator role inside).
                DeleteAccountRows(connection, transaction, userId);
                return new UserRemovalOutcome(null, [], [userId]);
            }

            // D6, D18: the household goes with every profile in it, and with every photo and video of theirs and of its Shared Library.
            var householdId = plan.HouseholdId!.Value;
            // A user belongs to the household of its profile.
            var userIds = connection.Query<Guid>("""
                SELECT a.id FROM accounts a
                WHERE a.profile_id IN (SELECT id FROM profiles WHERE household_id=@householdId)
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

            var sharedFiles = HouseholdRepository.DeleteSharedItems(connection, transaction, householdId);
            var removed = HouseholdRepository.DeleteEmptyHousehold(connection, transaction, householdId);
            if (removed != HouseholdDeleteOutcome.Deleted)
            {
                // Everything above is undone with the transaction.
                throw new InvalidOperationException(removed == HouseholdDeleteOutcome.HoldsSharedItems
                    ? UserRemovalMessages.HouseholdHoldsSharedItems
                    : UserRemovalMessages.HouseholdCouldNotBeRemoved);
            }

            return new UserRemovalOutcome(householdId, profileIds, userIds, sharedFiles);
        }, ct);

    /// <summary>The account row's own work: the last-administrator backstop and removing it.</summary>
    private static void DeleteAccountRows(SqliteConnection connection, SqliteTransaction transaction, Guid accountId)
    {
        var account = connection.QuerySingleOrDefault<AccountRow>(
            Select + " WHERE id=@accountId;", new { accountId }, transaction)
            ?? throw new KeyNotFoundException("Account not found.");
        if (account.IsEnabled && account.IsServerAdministrator &&
            !ProfileAccessRepository.HasUsableServerAdministratorWithoutAccount(connection, transaction, accountId))
        {
            throw new InvalidOperationException("The final effective administrator account cannot be deleted.");
        }

        // The only server administrator profile never loses its role by losing a sign-in that cannot sign in anyway.
        var adminProfileId = account.ProfileId;
        if (connection.ExecuteScalar<int>("""
                SELECT EXISTS(SELECT 1 FROM profiles WHERE id=@adminProfileId AND is_server_admin=1)
                   AND NOT EXISTS(SELECT 1 FROM accounts WHERE profile_id=@adminProfileId AND id<>@accountId)
                   AND NOT EXISTS(SELECT 1 FROM profiles WHERE is_server_admin=1 AND id<>@adminProfileId);
                """, new { adminProfileId, accountId }, transaction) == 1)
        {
            throw new InvalidOperationException("The final effective administrator account cannot be deleted.");
        }

        connection.Execute("DELETE FROM accounts WHERE id=@accountId;", new { accountId }, transaction);
        // A person left without a sign-in cannot be an administrator (D15). The last-administrator checks above and in the service
        // run first, so this never removes the last one that can sign in.
        connection.Execute("""
            UPDATE profiles
            SET is_server_admin=0, is_household_admin=0, authorization_version=authorization_version+1
            WHERE id=@profileId AND (is_server_admin=1 OR is_household_admin=1)
              AND NOT EXISTS (SELECT 1 FROM accounts WHERE profile_id=@profileId);
            """, new { profileId = account.ProfileId }, transaction);
    }

    public Task<IReadOnlyList<HouseholdProfileAccess>> GetHouseholdProfilesAsync(Guid accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection();
        // What a user may act as is its own profile and the rest of its household; roles and versions are the profile's.
        var rows = conn.Query<HouseholdProfileRow>("""
            SELECT p.id AS ProfileId, (p.id = a.profile_id) AS IsOwn, p.is_server_admin AS IsServerAdministrator,
                   p.authorization_version AS AuthorizationVersion, p.created_at AS CreatedAt
            FROM accounts a
            JOIN profiles own ON own.id = a.profile_id
            JOIN profiles p ON p.household_id = own.household_id
            WHERE a.id = @accountId AND own.household_id IS NOT NULL
            ORDER BY (p.id = a.profile_id) DESC, p.created_at, p.id;
            """, new { accountId });
        return Task.FromResult<IReadOnlyList<HouseholdProfileAccess>>(rows
            .Select(r => new HouseholdProfileAccess(r.ProfileId, r.IsOwn, r.IsServerAdministrator, r.AuthorizationVersion, ParseRequired(r.CreatedAt)))
            .ToList());
    }

    private sealed class HouseholdProfileRow
    {
        public Guid ProfileId { get; set; }
        public bool IsOwn { get; set; }
        public bool IsServerAdministrator { get; set; }
        public long AuthorizationVersion { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
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

    public Task CreateUserAsync(Account user, NewUserPlacement placement, AccountInvitation? invitation = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(placement);
        return db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var newProfile = placement.NewProfile;
            if (user.ProfileId == Guid.Empty || (newProfile is not null && newProfile.Id != user.ProfileId))
            {
                throw new InvalidOperationException("A user must open the profile it is created with.");
            }

            if (invitation is not null && (invitation.AccountId != user.Id || string.IsNullOrWhiteSpace(invitation.TokenHash)))
            {
                throw new InvalidOperationException("Invitation identity is invalid.");
            }

            if (newProfile is null && (placement.HouseholdId is not null || placement.NewHouseholdName is not null))
            {
                throw new InvalidOperationException("A household is named only for a new profile.");
            }

            if (placement.HouseholdId is not null && placement.NewHouseholdName is not null)
            {
                throw new InvalidOperationException("Name an existing household or a new one, not both.");
            }

            // The household of the profile this user opens: the one named, the one a new profile is made in, or the one the profile
            // already has; a profile with none (only the seeded Owner, before the first server administrator) gets a new one.
            var startedHousehold = false;
            Guid householdId;
            if (newProfile is not null)
            {
                if (placement.NewHouseholdName is { } householdName)
                {
                    // The household is made here, in the same transaction as the user and its profile.
                    householdId = CreateNamedHousehold(connection, transaction, householdName, user.CreatedAt);
                    startedHousehold = true;
                }
                else if (placement.HouseholdId is { } named)
                {
                    if (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM households WHERE id=@named;", new { named }, transaction) == 0)
                    {
                        throw new KeyNotFoundException("Household not found.");
                    }

                    householdId = named;
                }
                else
                {
                    householdId = CreateOrAdoptHousehold(connection, transaction, newProfile.DisplayName, placement.IsServerAdministrator, user.CreatedAt);
                    startedHousehold = true;
                }

                RequireRoom(connection, transaction, householdId);
                InsertProfileRow(connection, transaction, newProfile, householdId);
            }
            else
            {
                var existing = connection.QueryFirstOrDefault<ExistingProfileRow>(
                    "SELECT display_name AS DisplayName, household_id AS HouseholdId FROM profiles WHERE id=@ProfileId;",
                    new { user.ProfileId }, transaction) ?? throw new KeyNotFoundException("Profile not found.");
                if (existing.HouseholdId is { } own)
                {
                    householdId = own;
                }
                else
                {
                    householdId = CreateOrAdoptHousehold(connection, transaction, existing.DisplayName, placement.IsServerAdministrator, user.CreatedAt);
                    connection.Execute("UPDATE profiles SET household_id=@householdId WHERE id=@ProfileId;",
                        new { householdId, user.ProfileId }, transaction);
                    startedHousehold = true;
                }
            }

            // A profile has at most one user.
            if (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM accounts WHERE profile_id=@ProfileId AND id<>@Id;",
                    new { user.ProfileId, user.Id }, transaction) > 0)
            {
                throw new InvalidOperationException(ProfileAlreadyHasSignInMessage);
            }

            InsertAccount(connection, transaction, user);
            if (invitation is not null)
            {
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
            }

            // A brand-new profile (or one the caller asked to set up, like the administrator's first start) takes the roles and access
            // this user was created with. A person who already exists keeps exactly what they have.
            if (newProfile is not null || placement.SetUpProfile)
            {
                SetUpProfile(connection, transaction, user, placement, householdId, startedHousehold, bumpVersion: newProfile is null);
            }
            else
            {
                ClearStaleRolesForNewSignIn(connection, transaction, user.ProfileId);
            }
        }, ct);
    }

    private sealed class ExistingProfileRow
    {
        public string DisplayName { get; set; } = string.Empty;
        public Guid? HouseholdId { get; set; }
    }

    public Task CreateHouseholdPersonAsync(
        Profile profile,
        Guid householdId,
        CancellationToken ct = default,
        Guid? accessFromProfileId = null) => db.ExecuteWriteAsync((connection, transaction, token) =>
    {
        token.ThrowIfCancellationRequested();
        if (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM households WHERE id=@householdId;",
                new { householdId }, transaction) == 0)
        {
            throw new KeyNotFoundException("Household not found.");
        }

        RequireRoom(connection, transaction, householdId);
        InsertProfileRow(connection, transaction, profile, householdId);
        GiveNewProfileAccess(connection, transaction, profile.Id, householdId, accessFromProfileId, profile.CreatedAt);
    }, ct);

    public Task ReorderHouseholdProfilesAsync(Guid householdId, IReadOnlyList<Guid> orderedProfileIds, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var members = connection.Query<Guid>(
                "SELECT id FROM profiles WHERE household_id=@householdId;", new { householdId }, transaction).ToHashSet();
            if (members.Count == 0)
            {
                throw new KeyNotFoundException("Household not found.");
            }

            // The whole household in one list: nobody missing, nobody twice, nobody from another household.
            if (orderedProfileIds.Count != members.Count || orderedProfileIds.Distinct().Count() != members.Count
                || orderedProfileIds.Any(id => !members.Contains(id)))
            {
                throw new ArgumentException("The new order must list every person in the household exactly once.");
            }

            for (var position = 0; position < orderedProfileIds.Count; position++)
            {
                connection.Execute("UPDATE profiles SET sort_position=@position WHERE id=@id;",
                    new { position, id = orderedProfileIds[position] }, transaction);
            }
        }, ct);

    public Task UpdateManagedProfileAsync(Profile profile, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var changed = connection.Execute("""
                UPDATE profiles SET display_name=@DisplayName,avatar_color=@AvatarColor,avatar_icon=@AvatarIcon,role=@Role
                WHERE id=@Id;
                """, new { profile.Id, profile.DisplayName, profile.AvatarColor, profile.AvatarIcon, Role = profile.Role.ToString() }, transaction);
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
    /// Reads the household's profiles and whether its Shared Library holds shared photos, so the confirmation can say they go
    /// with the household (D18). Throws when the user does not exist.
    /// </summary>
    private static UserRemovalPlan PlanUserRemoval(SqliteConnection connection, SqliteTransaction transaction, Guid userId)
    {
        var user = connection.QuerySingleOrDefault<AccountRow>(Select + " WHERE id=@userId;", new { userId }, transaction)
            ?? throw new KeyNotFoundException("User not found.");
        // A user always opens a profile (accounts.profile_id is NOT NULL and a foreign key), so the profile row is always there.
        var profile = connection.QuerySingle<PlanProfileRow>("""
            SELECT id AS Id, display_name AS DisplayName, household_id AS HouseholdId,
                   is_server_admin AS IsServerAdministrator, is_household_admin AS IsHouseholdAdministrator
            FROM profiles WHERE id=@profileId;
            """, new { profileId = user.ProfileId }, transaction);
        if (profile.HouseholdId is not { } householdId)
        {
            return new UserRemovalPlan(user.Id, user.Email, profile.Id, profile.DisplayName, null, null, false,
                profile.IsServerAdministrator, profile.IsHouseholdAdministrator, false, [], false);
        }

        var household = connection.QuerySingleOrDefault<(string Name, bool IsDefault)>(
            "SELECT name AS Name, is_default AS IsDefault FROM households WHERE id=@householdId;", new { householdId }, transaction);
        // Another household-administrator profile of the same household with an enabled user means someone can still look after it
        // (the same meaning as every other last-household-administrator check).
        var someoneElseLooksAfter = connection.ExecuteScalar<int>(
            $"SELECT {ProfileAccessRepository.AnotherHouseholdAdministratorSql};", new { profileId = profile.Id }, transaction) == 1;
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

        // A household always keeps at least one household administrator profile with an enabled user (D7): the last one cannot be deleted.
        if (connection.ExecuteScalar<int>(
                $"SELECT EXISTS(SELECT 1 FROM profiles WHERE id=@profileId AND is_household_admin=1 AND household_id IS NOT NULL) AND NOT {ProfileAccessRepository.AnotherHouseholdAdministratorSql};",
                new { profileId }, transaction) == 1)
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

    private static void InsertAccount(System.Data.IDbConnection c, System.Data.IDbTransaction tx, Account a) => c.Execute("INSERT INTO accounts(id,email,normalized_email,is_enabled,authorization_version,created_at,updated_at,this_computer_only,must_change_password,temporary_password_expires_at,profile_id) VALUES(@Id,@Email,@NormalizedEmail,@IsEnabled,@AuthorizationVersion,@CreatedAt,@UpdatedAt,@ThisComputerOnly,@MustChangePassword,@TemporaryPasswordExpiresAt,@ProfileId);", Parameters(a), tx);
    private static void InsertProfileRow(
        System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, Profile profile, Guid householdId)
    {
        profile.HouseholdId = householdId;
        connection.Execute("""
            INSERT INTO profiles(id,display_name,avatar_color,avatar_image_path,avatar_icon,role,created_at,navigation_config,household_id,sort_position)
            VALUES(@Id,@DisplayName,@AvatarColor,@AvatarImagePath,@AvatarIcon,@Role,@CreatedAt,@NavigationConfig,@HouseholdId,
                   (SELECT COALESCE(MAX(sort_position) + 1, 0) FROM profiles WHERE household_id = @HouseholdId));
            """, new
        {
            profile.Id,
            profile.DisplayName,
            profile.AvatarColor,
            profile.AvatarImagePath,
            profile.AvatarIcon,
            Role = profile.Role.ToString(),
            CreatedAt = Iso(profile.CreatedAt),
            profile.NavigationConfig,
            profile.HouseholdId,
        }, transaction);
    }

    /// <summary>A household with exactly the name the administrator chose (trimmed and checked as every household name is).</summary>
    private static Guid CreateNamedHousehold(System.Data.IDbConnection c, System.Data.IDbTransaction tx, string name, DateTimeOffset at)
    {
        var household = new Household(Guid.NewGuid(), name, at);
        c.Execute("INSERT INTO households(id,name,created_at) VALUES(@Id,@Name,@at);",
            new { household.Id, household.Name, at = Iso(at) }, tx);
        return household.Id;
    }

    /// <summary>What a person is told when a profile that already has a sign-in is given a second one.</summary>
    internal const string ProfileAlreadyHasSignInMessage = "That profile already has a sign-in.";

    /// <summary>
    /// Sets up the profile a new user opens from the placement it was created with, in the caller's transaction (profile-held access):
    /// the server administrator role (in the Library's own household, D17), the household administrator role when the household was
    /// just made for this profile (a household needs one, D7; the user is attached, D15), and the Read/Watch/Listen/View access and
    /// libraries it starts with. Only for a brand-new profile or a first start; a person who already exists is never changed by
    /// getting a user.
    /// </summary>
    private static void SetUpProfile(
        System.Data.IDbConnection c, System.Data.IDbTransaction tx, Account user, NewUserPlacement placement, Guid householdId,
        bool startedHousehold, bool bumpVersion)
    {
        var profileId = user.ProfileId;
        if (placement.IsServerAdministrator)
        {
            ProfileAccessRepository.RequireInDefaultHousehold(c, tx, profileId);
        }

        c.Execute("""
            UPDATE profiles
            SET is_server_admin=@server, is_household_admin=@household, authorization_version=authorization_version+@bump
            WHERE id=@profileId;
            """, new
        {
            profileId,
            server = placement.IsServerAdministrator ? 1 : 0,
            household = startedHousehold ? 1 : 0,
            bump = bumpVersion ? 1 : 0,
        }, tx);
        ProfileAccessRepository.WriteAccessRows(c, tx, profileId, placement.Features ?? new HashSet<AccountFeatureId>(),
            placement.HiddenLibraries ?? new HashSet<Guid>(), user.CreatedAt);
        if (placement.IsServerAdministrator)
        {
            ProfileAccessRepository.MarkDefaultHouseholdIfNone(c, tx, householdId);
        }
    }

    /// <summary>
    /// A person who already exists gets a user: no administrator role left on the profile from an earlier sign-in comes back with
    /// it. The profile had no user until now, so clearing a role cannot strand a server administrator; a role is given again only
    /// by an explicit choice afterwards (the server administrator's own, in the service). Their Read/Watch/Listen/View and library
    /// access stay as they are.
    /// </summary>
    private static void ClearStaleRolesForNewSignIn(System.Data.IDbConnection c, System.Data.IDbTransaction tx, Guid profileId) =>
        c.Execute("""
            UPDATE profiles
            SET is_server_admin=0, is_household_admin=0, authorization_version=authorization_version+1
            WHERE id=@profileId AND (is_server_admin=1 OR is_household_admin=1);
            """, new { profileId }, tx);

    /// <summary>
    /// A person added without a sign-in starts with the Read/Watch/Listen/View access of the profile of whoever added them
    /// (<paramref name="sourceProfileId"/>); with no such profile they start with every feature. Nothing is hidden for the new profile
    /// itself: it gets what its household has, because the household's hidden libraries apply to every profile in it (library access D2,
    /// D3). Someone outside the adder's household starts with View off. A source profile that does not exist gives nothing. Never an
    /// administrator role.
    /// </summary>
    private static void GiveNewProfileAccess(
        System.Data.IDbConnection c, System.Data.IDbTransaction tx, Guid profileId, Guid householdId, Guid? sourceProfileId, DateTimeOffset at)
    {
        List<string> features;
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
        }

        foreach (var feature in features)
        {
            c.Execute("INSERT INTO profile_feature_grants(profile_id,feature_id,granted_at) VALUES(@profileId,@feature,@at);",
                new { profileId, feature, at = Iso(at) }, tx);
        }

    }

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
    /// A new household for a profile. The first server administrator joins the server's still-empty household (made by the Shared
    /// Library upgrade, or by the first shared item) instead of leaving it as a phantom.
    /// </summary>
    private static Guid CreateOrAdoptHousehold(
        System.Data.IDbConnection c, System.Data.IDbTransaction tx, string? name, bool isServerAdministrator, DateTimeOffset at)
    {
        if (isServerAdministrator &&
            c.QueryFirstOrDefault<Guid?>("""
                SELECT h.id FROM households h
                WHERE NOT EXISTS (SELECT 1 FROM profiles p WHERE p.household_id = h.id)
                  AND EXISTS (SELECT 1 FROM view_shared_library v WHERE v.household_id = h.id)
                ORDER BY h.created_at, h.id LIMIT 1;
                """, transaction: tx) is { } empty)
        {
            return empty;
        }

        var id = Guid.NewGuid();
        c.Execute("INSERT INTO households(id,name,created_at) VALUES(@id,@name,@at);",
            new { id, name = Household.DefaultNameFor(name), at = Iso(at) }, tx);
        return id;
    }

    private static string Iso(DateTimeOffset v) => v.ToString("O", System.Globalization.CultureInfo.InvariantCulture); private static DateTimeOffset ParseRequired(string v) => DateTimeOffset.Parse(v, System.Globalization.CultureInfo.InvariantCulture); private static DateTimeOffset? Parse(string? v) => string.IsNullOrWhiteSpace(v) ? null : ParseRequired(v);
}
