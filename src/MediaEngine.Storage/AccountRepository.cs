using System.Collections.Frozen;
using System.Text.Json;
using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

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

    public Task<bool> UpdateAsync(Account account, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        return Task.FromResult(conn.Execute("""
            UPDATE accounts SET email = @Email, normalized_email = @NormalizedEmail,
                is_enabled = @IsEnabled, is_administrator = @IsAdministrator,
                this_computer_only = @ThisComputerOnly,
                authorization_version = authorization_version + 1, updated_at = @UpdatedAt
            WHERE id = @Id;
            """, Parameters(account)) > 0);
    }

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

    public Task<bool> RevokeProfileAsync(Guid accountId, Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        return Task.FromResult(conn.Execute(
            "DELETE FROM account_profile_grants WHERE account_id = @accountId AND profile_id = @profileId;",
            new { accountId, profileId }) > 0);
    }

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
               this_computer_only AS ThisComputerOnly,
               must_change_password AS MustChangePassword,
               temporary_password_expires_at AS TemporaryPasswordExpiresAt
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
        ThisComputerOnly = account.IsThisComputerOnly ? 1 : 0,
        MustChangePassword = account.MustChangePassword ? 1 : 0,
        TemporaryPasswordExpiresAt = account.TemporaryPasswordExpiresAt?.ToString("O"),
    };

    private static Account Map(AccountRow row)
    {
        var account = MapFields(row);
        if (row.ThisComputerOnly)
        {
            account.MarkThisComputerOnly();
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
        public bool ThisComputerOnly { get; set; }
        public bool MustChangePassword { get; set; }
        public string? TemporaryPasswordExpiresAt { get; set; }
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
    }, ct);

    public Task DeleteAccountAsync(Guid accountId, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var account = connection.QuerySingleOrDefault<AccountRow>(
                Select + " WHERE id=@accountId;", new { accountId }, transaction)
                ?? throw new KeyNotFoundException("Account not found.");
            if (account.IsEnabled && account.IsAdministrator &&
                !HasOtherUsableAdministratorAccount(connection, transaction, accountId))
            {
                throw new InvalidOperationException("The final effective administrator account cannot be deleted.");
            }

            connection.Execute("DELETE FROM accounts WHERE id=@accountId;", new { accountId }, transaction);
        }, ct);

    public Task<IReadOnlyList<AccountProfileGrant>> GetGrantsAsync(Guid accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection();
        return Task.FromResult<IReadOnlyList<AccountProfileGrant>>(conn.Query<GrantRow>(GrantSelect + " WHERE account_id=@accountId ORDER BY is_default DESC, granted_at;", new { accountId }).Select(MapGrant).ToList());
    }

    public Task<IReadOnlySet<AccountFeatureId>> GetFeatureGrantsAsync(Guid accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection();
        return Task.FromResult<IReadOnlySet<AccountFeatureId>>(conn.Query<string>("SELECT feature_id FROM account_feature_grants WHERE account_id=@accountId;", new { accountId }).Select(x => new AccountFeatureId(x)).ToFrozenSet());
    }

    public Task<IReadOnlySet<Guid>> GetLibraryGrantsAsync(Guid accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection();
        return Task.FromResult<IReadOnlySet<Guid>>(conn.Query<Guid>("SELECT library_id FROM account_library_grants WHERE account_id=@accountId;", new { accountId }).ToFrozenSet());
    }

    public async Task<bool> HasFeatureGrantAsync(Guid accountId, AccountFeatureId feature, CancellationToken ct = default) =>
        (await GetFeatureGrantsAsync(accountId, ct).ConfigureAwait(false)).Contains(feature);

    public async Task<bool> HasLibraryGrantAsync(Guid accountId, Guid libraryId, CancellationToken ct = default) =>
        (await GetLibraryGrantsAsync(accountId, ct).ConfigureAwait(false)).Contains(libraryId);

    public Task<GrantAdminProtection?> GetAdminProtectionAsync(Guid accountId, Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<ProtectionRow>("SELECT account_id AS AccountId, profile_id AS ProfileId, is_enabled AS IsEnabled, unlock_mode AS UnlockMode, unlock_minutes AS UnlockMinutes, pin_hash AS PinHash, hash_scheme AS HashScheme, failed_attempt_count AS FailedAttemptCount, locked_until AS LockedUntil, protection_version AS ProtectionVersion, updated_at AS UpdatedAt FROM grant_admin_protections WHERE account_id=@accountId AND profile_id=@profileId;", new { accountId, profileId });
        return Task.FromResult(row is null ? null : MapProtection(row));
    }

    public Task<GrantAdminUnlock?> GetAdminUnlockAsync(Guid sessionId, Guid accountId, Guid profileId, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<UnlockRow>("SELECT session_id AS SessionId, account_id AS AccountId, profile_id AS ProfileId, protection_version AS ProtectionVersion, method AS Method, granted_at AS GrantedAt, expires_at AS ExpiresAt FROM grant_admin_unlocks WHERE session_id=@sessionId AND account_id=@accountId AND profile_id=@profileId AND (expires_at IS NULL OR expires_at>@now);", new { sessionId, accountId, profileId, now = Iso(now) });
        return Task.FromResult(row is null ? null : MapUnlock(row));
    }

    public Task WriteAuthorizationAuditAsync(AuthorizationAuditEvent e, CancellationToken ct = default) => db.ExecuteWriteAsync((conn, tx, token) => { token.ThrowIfCancellationRequested(); conn.Execute("INSERT INTO authorization_audit_events(event_type,occurred_at,actor_account_id,actor_profile_id,actor_application_id,subject_type,subject_id,changes_json) VALUES(@EventType,@OccurredAt,@ActorAccountId,@ActorProfileId,@ActorApplicationId,@SubjectType,@SubjectId,@ChangesJson);", new { e.EventType, OccurredAt = Iso(e.OccurredAt), e.ActorAccountId, e.ActorProfileId, e.ActorApplicationId, e.SubjectType, e.SubjectId, ChangesJson = JsonSerializer.Serialize(e.Changes) }, tx); }, ct);

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
                        (id,display_name,avatar_color,avatar_image_path,role,created_at,navigation_config,household_id)
                    VALUES(@Id,@DisplayName,@AvatarColor,@AvatarImagePath,@Role,@CreatedAt,@NavigationConfig,@HouseholdId);
                    """, new
                {
                    newProfile.Id,
                    newProfile.DisplayName,
                    newProfile.AvatarColor,
                    newProfile.AvatarImagePath,
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
            InsertAccount(connection, transaction, account);
            InsertGrant(connection, transaction, initialGrant);
            ReplaceAccess(connection, transaction, account.Id, features, libraries, account.UpdatedAt);
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
                : CreateHousehold(connection, transaction, newProfile?.DisplayName ?? account.Email, account.CreatedAt);
            if (newProfile is not null)
            {
                InsertProfileRow(connection, transaction, newProfile, account.HouseholdId.Value);
            }

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
        }, ct);

    public Task CreateManagedProfileAsync(
        Profile profile,
        AccountProfileGrant targetGrant,
        CancellationToken ct = default) => db.ExecuteWriteAsync((connection, transaction, token) =>
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

    public Task UpdateManagedProfileAsync(Profile profile, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var changed = connection.Execute("""
                UPDATE profiles SET display_name=@DisplayName,avatar_color=@AvatarColor
                WHERE id=@Id;
                """, profile, transaction);
            if (changed != 1)
            {
                throw new KeyNotFoundException("Profile not found.");
            }
        }, ct);

    public Task DeleteManagedProfileAsync(Guid profileId, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (profileId == Profile.SeedProfileId)
            {
                throw new InvalidOperationException("The Owner profile cannot be deleted.");
            }

            if (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM profiles WHERE id=@profileId;",
                    new { profileId }, transaction) == 0)
            {
                throw new KeyNotFoundException("Profile not found.");
            }

            if (connection.ExecuteScalar<int>("""
                    SELECT COUNT(*) FROM accounts a
                    JOIN account_profile_grants target ON target.account_id=a.id
                    WHERE target.profile_id=@profileId AND target.is_enabled=1 AND a.is_enabled=1
                      AND (SELECT COUNT(*) FROM account_profile_grants other
                           WHERE other.account_id=a.id AND other.is_enabled=1)=1;
                    """, new { profileId }, transaction) > 0)
            {
                throw new InvalidOperationException("Move accounts to another profile before deleting this profile.");
            }

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

            connection.Execute("DELETE FROM profiles WHERE id=@profileId;", new { profileId }, transaction);
        }, ct);

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
    public Task SetAdminProtectionAsync(GrantAdminProtection protection, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("""
                INSERT INTO grant_admin_protections
                    (account_id,profile_id,is_enabled,unlock_mode,unlock_minutes,pin_hash,hash_scheme,
                     failed_attempt_count,locked_until,protection_version,updated_at)
                VALUES(@AccountId,@ProfileId,@IsEnabled,@UnlockMode,@UnlockMinutes,@PinHash,@HashScheme,
                       0,NULL,@ProtectionVersion,@UpdatedAt)
                ON CONFLICT(account_id,profile_id) DO UPDATE SET
                    is_enabled=excluded.is_enabled,unlock_mode=excluded.unlock_mode,
                    unlock_minutes=excluded.unlock_minutes,pin_hash=excluded.pin_hash,
                    hash_scheme=excluded.hash_scheme,failed_attempt_count=0,locked_until=NULL,
                    protection_version=grant_admin_protections.protection_version+1,
                    updated_at=excluded.updated_at;
                """, new
            {
                protection.AccountId,
                protection.ProfileId,
                IsEnabled = protection.IsEnabled ? 1 : 0,
                protection.UnlockMode,
                protection.UnlockMinutes,
                protection.PinHash,
                protection.HashScheme,
                protection.ProtectionVersion,
                UpdatedAt = Iso(protection.UpdatedAt),
            }, transaction);
            connection.Execute("""
                UPDATE account_profile_grants SET authorization_version=authorization_version+1
                WHERE account_id=@AccountId AND profile_id=@ProfileId;
                DELETE FROM grant_admin_unlocks WHERE account_id=@AccountId AND profile_id=@ProfileId;
                """, protection, transaction);
        }, ct);

    public Task<GrantAdminProtection> RecordAdminProtectionFailureAsync(Guid accountId, Guid profileId,
        DateTimeOffset lockedUntilAfterLimit, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var row = connection.QuerySingle<ProtectionRow>("""
                UPDATE grant_admin_protections
                SET failed_attempt_count=failed_attempt_count+1,
                    locked_until=CASE WHEN failed_attempt_count+1>=5 THEN @lockedUntil ELSE locked_until END
                WHERE account_id=@accountId AND profile_id=@profileId
                RETURNING account_id AS AccountId,profile_id AS ProfileId,is_enabled AS IsEnabled,
                    unlock_mode AS UnlockMode,unlock_minutes AS UnlockMinutes,pin_hash AS PinHash,
                    hash_scheme AS HashScheme,failed_attempt_count AS FailedAttemptCount,
                    locked_until AS LockedUntil,protection_version AS ProtectionVersion,updated_at AS UpdatedAt;
                """, new { accountId, profileId, lockedUntil = Iso(lockedUntilAfterLimit) }, transaction);
            return MapProtection(row);
        }, ct);

    public Task ResetAdminProtectionAttemptsAsync(Guid accountId, Guid profileId,
        CancellationToken ct = default) => db.ExecuteWriteAsync((connection, transaction, _) =>
            connection.Execute("""
                UPDATE grant_admin_protections SET failed_attempt_count=0,locked_until=NULL
                WHERE account_id=@accountId AND profile_id=@profileId;
                """, new { accountId, profileId }, transaction), ct);

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

    private static void InsertAccount(System.Data.IDbConnection c, System.Data.IDbTransaction tx, Account a) => c.Execute("INSERT INTO accounts(id,email,normalized_email,is_enabled,is_administrator,authorization_version,created_at,updated_at,household_id,this_computer_only,must_change_password,temporary_password_expires_at) VALUES(@Id,@Email,@NormalizedEmail,@IsEnabled,@IsAdministrator,@AuthorizationVersion,@CreatedAt,@UpdatedAt,@HouseholdId,@ThisComputerOnly,@MustChangePassword,@TemporaryPasswordExpiresAt);", Parameters(a), tx);
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
            INSERT INTO profiles(id,display_name,avatar_color,avatar_image_path,role,created_at,navigation_config,household_id)
            VALUES(@Id,@DisplayName,@AvatarColor,@AvatarImagePath,@Role,@CreatedAt,@NavigationConfig,@HouseholdId);
            """, new
        {
            profile.Id,
            profile.DisplayName,
            profile.AvatarColor,
            profile.AvatarImagePath,
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
        return fromProfile ?? CreateHousehold(c, tx, name, account.CreatedAt);
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
    private static GrantAdminProtection MapProtection(ProtectionRow r) => new() { AccountId = r.AccountId, ProfileId = r.ProfileId, IsEnabled = r.IsEnabled, UnlockMode = r.UnlockMode, UnlockMinutes = r.UnlockMinutes, PinHash = r.PinHash, HashScheme = r.HashScheme, FailedAttemptCount = r.FailedAttemptCount, LockedUntil = Parse(r.LockedUntil), ProtectionVersion = r.ProtectionVersion, UpdatedAt = ParseRequired(r.UpdatedAt) };
    private static GrantAdminUnlock MapUnlock(UnlockRow r) => new() { SessionId = r.SessionId, AccountId = r.AccountId, ProfileId = r.ProfileId, ProtectionVersion = r.ProtectionVersion, Method = r.Method, GrantedAt = ParseRequired(r.GrantedAt), ExpiresAt = Parse(r.ExpiresAt) };
    private static string Iso(DateTimeOffset v) => v.ToString("O", System.Globalization.CultureInfo.InvariantCulture); private static DateTimeOffset ParseRequired(string v) => DateTimeOffset.Parse(v, System.Globalization.CultureInfo.InvariantCulture); private static DateTimeOffset? Parse(string? v) => string.IsNullOrWhiteSpace(v) ? null : ParseRequired(v);
    private sealed class GrantRow { public Guid AccountId { get; set; } public Guid ProfileId { get; set; } public bool IsDefault { get; set; } public bool IsEnabled { get; set; } public bool AdminEnabled { get; set; } public long AuthorizationVersion { get; set; } public string GrantedAt { get; set; } = ""; }
    private sealed class ProtectionRow { public Guid AccountId { get; set; } public Guid ProfileId { get; set; } public bool IsEnabled { get; set; } public string UnlockMode { get; set; } = ""; public int? UnlockMinutes { get; set; } public string? PinHash { get; set; } public string? HashScheme { get; set; } public int FailedAttemptCount { get; set; } public string? LockedUntil { get; set; } public long ProtectionVersion { get; set; } public string UpdatedAt { get; set; } = ""; }
    private sealed class UnlockRow { public Guid SessionId { get; set; } public Guid AccountId { get; set; } public Guid ProfileId { get; set; } public long ProtectionVersion { get; set; } public string Method { get; set; } = ""; public string GrantedAt { get; set; } = ""; public string? ExpiresAt { get; set; } }
}
