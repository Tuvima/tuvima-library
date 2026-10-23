using System.Security.Cryptography;
using System.Text;
using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

public sealed class AccessRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly ProfileRepository _profiles;
    private readonly ProfileAccessRepository _profileAccess;
    private readonly DateTimeOffset _now =
        new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    public AccessRepositoryTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_access_repo_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        _profiles = new ProfileRepository(_database);
        _profileAccess = new ProfileAccessRepository(_database);
    }

    [Fact]
    public async Task Grants_EnforceMaximumAndTheFinalAdministratorIsKeptOnTheProfileTransactionally()
    {
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = "owner@example.com",
            NormalizedEmail = "OWNER@EXAMPLE.COM",
            IsEnabled = true,
            IsAdministrator = true,
            AuthorizationVersion = 1,
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        await _accounts.CreateAccountAsync(
            account,
            Grant(account.Id, Profile.SeedProfileId, isDefault: true, administrator: true),
            AccountFeatureId.All.ToHashSet(),
            new HashSet<Guid>());

        var profileIds = new List<Guid>();
        for (var index = 0; index < 7; index++)
        {
            var profileId = Guid.NewGuid();
            profileIds.Add(profileId);
            InsertProfile(NewProfile(profileId, $"Profile {index + 2}"));
            await _accounts.UpsertGrantAsync(Grant(account.Id, profileId));
        }

        var ninthProfile = Guid.NewGuid();
        InsertProfile(NewProfile(ninthProfile, "Ninth"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _accounts.UpsertGrantAsync(Grant(account.Id, ninthProfile)));

        // A sign-in signs in as the profile it is attached to; asking for another default does not move it.
        await _accounts.UpsertGrantAsync(Grant(account.Id, profileIds[0], isDefault: true));
        var grants = await _accounts.GetGrantsAsync(account.Id);
        Assert.Single(grants, grant => grant.IsDefault);
        Assert.Equal(Profile.SeedProfileId, grants.Single(grant => grant.IsDefault).ProfileId);
        Assert.Equal(Profile.SeedProfileId, await _accounts.GetDefaultProfileIdAsync(account.Id));

        // The administrator role is the profile's: the last one cannot be taken away, and saving the sign-in never changes it.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _profileAccess.SetServerAdministratorAsync(Profile.SeedProfileId, false, _now.AddMinutes(1)));
        Assert.True(grants.Single(grant => grant.ProfileId == Profile.SeedProfileId).AdminEnabled);

        account.IsAdministrator = false;
        account.UpdatedAt = _now.AddMinutes(1);
        await _accounts.UpdateAccountAsync(account);
        Assert.True((await _accounts.GetByIdAsync(account.Id))?.IsAdministrator);
        account.IsEnabled = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _accounts.UpdateAccountAsync(account));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _accounts.DeleteAccountAsync(account.Id));
    }

    [Fact]
    public async Task PendingAdministratorWithoutSignInMethod_DoesNotReplaceTheSoleUsableAdministrator()
    {
        var owner = NewAdministrator("owner@example.com");
        await _accounts.CreateAccountAsync(
            owner,
            Grant(owner.Id, Profile.SeedProfileId, isDefault: true, administrator: true),
            AccountFeatureId.All.ToHashSet(),
            new HashSet<Guid>());
        await new IdentityRepository(_database).UpsertAccountCredentialAsync(new AccountCredential
        {
            Id = Guid.NewGuid(),
            AccountId = owner.Id,
            SecretHash = "work-factored-hash",
            SecurityStamp = "owner-security-stamp",
            CreatedAt = _now,
            UpdatedAt = _now,
        });

        var pending = NewAdministrator("pending@example.com");
        // A server administrator lives in the Library's own household, like the one who signs in.
        pending.HouseholdId = (await new HouseholdRepository(_database).GetForAccountAsync(owner.Id))!.Id;
        var pendingProfile = Guid.NewGuid();
        InsertProfile(NewProfile(pendingProfile, "Pending"));
        await _accounts.CreateAccountAsync(
            pending,
            Grant(pending.Id, pendingProfile, isDefault: true, administrator: true),
            AccountFeatureId.All.ToHashSet(),
            new HashSet<Guid>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _profileAccess.SetServerAdministratorAsync(Profile.SeedProfileId, false, _now.AddMinutes(1)));
        Assert.True((await _accounts.GetByIdAsync(owner.Id))?.IsAdministrator);
    }

    [Fact]
    public async Task TheProfileOfTheSoleUsableAdministrator_CannotBeDeleted_EvenWhenAnotherAdministratorHasNoSignInMethod()
    {
        var owner = NewAdministrator("owner@example.com");
        var ownerProfile = NewProfile(Guid.NewGuid(), "Administration");
        Assert.Equal(ProfileRole.StandardUser, ownerProfile.Role);
        await _accounts.CreateAccountAsync(
            owner,
            Grant(owner.Id, ownerProfile.Id, isDefault: true, administrator: true),
            AccountFeatureId.All.ToHashSet(),
            new HashSet<Guid>(),
            newProfile: ownerProfile);
        await new IdentityRepository(_database).UpsertAccountCredentialAsync(new AccountCredential
        {
            Id = Guid.NewGuid(),
            AccountId = owner.Id,
            SecretHash = "work-factored-hash",
            SecurityStamp = "owner-security-stamp",
            CreatedAt = _now,
            UpdatedAt = _now,
        });

        var pending = NewAdministrator("pending@example.com");
        // A server administrator lives in the Library's own household, like the one who signs in.
        pending.HouseholdId = (await new HouseholdRepository(_database).GetForAccountAsync(owner.Id))!.Id;
        var pendingProfile = Guid.NewGuid();
        InsertProfile(NewProfile(pendingProfile, "Pending"));
        await _accounts.CreateAccountAsync(
            pending,
            Grant(pending.Id, pendingProfile, isDefault: true, administrator: true),
            AccountFeatureId.All.ToHashSet(),
            new HashSet<Guid>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _accounts.DeleteManagedProfileAsync(ownerProfile.Id));
        Assert.NotNull(await _profiles.GetByIdAsync(ownerProfile.Id));
        Assert.True((await _profiles.GetByIdAsync(ownerProfile.Id))!.IsServerAdministrator);
    }

    [Fact]
    public async Task DeletingAProfile_ThatHasItsOwnSignIn_TellsTheAdministratorToDeleteTheSignInFirst()
    {
        var personId = Guid.NewGuid();
        InsertProfile(NewProfile(personId, "Mary"));
        var signIn = NewAdministrator("mary@example.com");
        signIn.IsAdministrator = false;
        await _accounts.CreateAccountAsync(
            signIn,
            Grant(signIn.Id, personId, isDefault: true),
            new HashSet<AccountFeatureId>(),
            new HashSet<Guid>());

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _accounts.DeleteManagedProfileAsync(personId));

        Assert.Equal("This person has their own sign-in. Delete the sign-in first.", refused.Message);
        Assert.NotNull(await _profiles.GetByIdAsync(personId));
        Assert.NotNull(await _accounts.GetByIdAsync(signIn.Id));
    }

    [Fact]
    public async Task DeletingAProfile_ThatAlsoHasAnotherSignInLinkedToIt_IsRefused_AndValidationSaysTheSame()
    {
        // The sign-in has other people to open as well, so the older "only opens this person" rule would not stop the delete.
        var personId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        InsertProfile(NewProfile(personId, "Mary"));
        InsertProfile(NewProfile(otherId, "Mary's brother"));
        var signIn = NewAdministrator("mary@example.com");
        signIn.IsAdministrator = false;
        await _accounts.CreateAccountAsync(
            signIn,
            Grant(signIn.Id, personId, isDefault: true),
            new HashSet<AccountFeatureId>(),
            new HashSet<Guid>());
        await _accounts.UpsertGrantAsync(Grant(signIn.Id, otherId));

        var validation = await Assert.ThrowsAsync<InvalidOperationException>(() => _accounts.ValidateManagedProfileRemovalAsync(personId));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _accounts.DeleteManagedProfileAsync(personId));

        Assert.Equal("This person has their own sign-in. Delete the sign-in first.", validation.Message);
        Assert.Equal(validation.Message, refused.Message);
        Assert.NotNull(await _profiles.GetByIdAsync(personId));
        Assert.NotNull(await _accounts.GetByIdAsync(signIn.Id));
        Assert.Equal(personId, (await _accounts.GetByIdAsync(signIn.Id))!.ProfileId);
    }

    [Fact]
    public async Task DeletingAProfile_ThatASignInWithNoLinkedProfileOpensOnly_StillTellsTheAdministratorToRemoveOrMoveTheSignInFirst()
    {
        var personId = Guid.NewGuid();
        InsertProfile(NewProfile(personId, "Mary"));
        var signIn = NewAdministrator("mary@example.com");
        signIn.IsAdministrator = false;
        await _accounts.InsertAsync(signIn);
        await _accounts.GrantProfileAsync(Grant(signIn.Id, personId, isDefault: true));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _accounts.DeleteManagedProfileAsync(personId));

        Assert.Contains("Remove or move this person's sign-in", refused.Message, StringComparison.Ordinal);
        Assert.NotNull(await _profiles.GetByIdAsync(personId));
    }

    [Fact]
    public async Task InvitationAccount_CreatesAllGrantsAndHashOnlyInvitationAtomically()
    {
        var secondProfileId = Guid.NewGuid();
        InsertProfile(NewProfile(secondProfileId, "Second"));
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = "invited@example.com",
            NormalizedEmail = "INVITED@EXAMPLE.COM",
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        const string plaintext = "invitation-secret-that-is-returned-once";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));
        var invitation = new AccountInvitation
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            TokenHash = hash,
            CreatedAt = _now,
            ExpiresAt = _now.AddDays(7),
        };

        await _accounts.CreateInvitedAccountAsync(account,
            [Grant(account.Id, Profile.SeedProfileId, isDefault: true),
             Grant(account.Id, secondProfileId)], invitation);

        var grants = await _accounts.GetGrantsAsync(account.Id);
        Assert.Equal(2, grants.Count);
        Assert.Single(grants, grant => grant.IsDefault && grant.IsEnabled);
        Assert.Equal(invitation.Id, (await _accounts.GetActiveInvitationAsync(hash, _now))?.Id);
        using var connection = _database.CreateConnection();
        Assert.Equal(hash, connection.QuerySingle<string>(
            "SELECT token_hash FROM account_invitations WHERE id=@id;", new { id = invitation.Id }));
        Assert.Equal(0, connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM account_invitations WHERE token_hash=@plaintext;", new { plaintext }));
    }

    [Fact]
    public async Task ConcurrentExternalLoginRemoval_CannotRemoveBothFinalSignInMethods()
    {
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = "external@example.com",
            NormalizedEmail = "EXTERNAL@EXAMPLE.COM",
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        await _accounts.CreateAccountAsync(account,
            Grant(account.Id, Profile.SeedProfileId, isDefault: true),
            new HashSet<AccountFeatureId>(), new HashSet<Guid>());
        var external = new AccountExternalLoginRepository(_database);
        var first = NewExternalLogin(account.Id, "first");
        var second = NewExternalLogin(account.Id, "second");
        await external.InsertAsync(first);
        await external.InsertAsync(second);
        var signInMethods = new AccountSignInMethodRepository(_database);

        var results = await Task.WhenAll(
            signInMethods.RemoveExternalLoginAsync(account.Id, first.Id),
            signInMethods.RemoveExternalLoginAsync(account.Id, second.Id));

        Assert.Contains(SignInMethodRemovalResult.Removed, results);
        Assert.Contains(SignInMethodRemovalResult.LastSignInMethod, results);
        Assert.Single(await external.GetByAccountAsync(account.Id));
    }

    [Fact]
    public async Task ManagedProfileCreationAndDeletion_AreAtomicAcrossProfileAndAccountGrants()
    {
        var target = new Account
        {
            Id = Guid.NewGuid(),
            Email = "target@example.test",
            NormalizedEmail = "TARGET@EXAMPLE.TEST",
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        await _accounts.CreateAccountAsync(target,
            Grant(target.Id, Profile.SeedProfileId, isDefault: true),
            new HashSet<AccountFeatureId>(), new HashSet<Guid>());
        var profile = NewProfile(Guid.NewGuid(), "Managed");

        await _accounts.CreateManagedProfileAsync(
            profile,
            Grant(target.Id, profile.Id));

        Assert.NotNull(await _profiles.GetByIdAsync(profile.Id));
        Assert.True(await _accounts.HasProfileAccessAsync(target.Id, profile.Id));
        Assert.Single(await _accounts.GetAllAsync());
        await _accounts.DeleteManagedProfileAsync(profile.Id);
        Assert.Null(await _profiles.GetByIdAsync(profile.Id));
        Assert.NotNull(await _accounts.GetByIdAsync(target.Id));
        Assert.False(await _accounts.HasProfileAccessAsync(target.Id, profile.Id));
    }

    [Fact]
    public async Task AccountCreation_WithNewDefaultProfile_IsAtomicAndDoesNotShareAnExistingProfile()
    {
        const string email = "remote@example.com";
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        var profile = NewProfile(Guid.NewGuid(), "Remote user");

        await _accounts.CreateAccountAsync(
            account,
            Grant(account.Id, profile.Id, isDefault: true),
            new HashSet<AccountFeatureId>(),
            new HashSet<Guid>(),
            newProfile: profile);

        Assert.NotNull(await _accounts.GetByIdAsync(account.Id));
        Assert.Equal(profile.DisplayName, (await _profiles.GetByIdAsync(profile.Id))?.DisplayName);
        Assert.Equal(profile.Id, await _accounts.GetDefaultProfileIdAsync(account.Id));
        Assert.True(await _accounts.HasProfileAccessAsync(account.Id, profile.Id));
        Assert.False(await _accounts.HasProfileAccessAsync(account.Id, Profile.SeedProfileId));
    }

    [Fact]
    public async Task AccessReplacement_OnTheProfile_ChangesWhatThePersonMayUse_AndRaisesTheProfilesAuthorizationVersion()
    {
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = "access@example.test",
            NormalizedEmail = "ACCESS@EXAMPLE.TEST",
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        await _accounts.CreateAccountAsync(
            account,
            Grant(account.Id, Profile.SeedProfileId, isDefault: true),
            new HashSet<AccountFeatureId> { AccountFeatureId.Read },
            new HashSet<Guid>());

        var before = (await _profileAccess.GetAdminFlagsAsync(Profile.SeedProfileId))!.AuthorizationVersion;
        await _profileAccess.ReplaceAccessAsync(
            Profile.SeedProfileId,
            new HashSet<AccountFeatureId> { AccountFeatureId.Listen },
            new HashSet<Guid>(),
            _now.AddMinutes(1));

        Assert.Equal(before + 1, (await _profileAccess.GetAdminFlagsAsync(Profile.SeedProfileId))!.AuthorizationVersion);
        var features = await _profileAccess.GetFeatureGrantsAsync(Profile.SeedProfileId);
        Assert.DoesNotContain(AccountFeatureId.Read, features);
        Assert.Contains(AccountFeatureId.Listen, features);
    }

    [Fact]
    public async Task AccountSideAccessReplacement_LeavesTheProfilesAccessAlone()
    {
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = "legacy@example.test",
            NormalizedEmail = "LEGACY@EXAMPLE.TEST",
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        await _accounts.CreateAccountAsync(
            account,
            Grant(account.Id, Profile.SeedProfileId, isDefault: true),
            new HashSet<AccountFeatureId> { AccountFeatureId.Read },
            new HashSet<Guid>());
        var versionBefore = (await _profileAccess.GetAdminFlagsAsync(Profile.SeedProfileId))!.AuthorizationVersion;

        await _accounts.ReplaceAccountAccessAsync(
            account.Id,
            new HashSet<AccountFeatureId> { AccountFeatureId.Listen },
            new HashSet<Guid>(),
            _now.AddMinutes(1));

        // The profile holds the access; the sign-in's own rows are never read for a decision.
        Assert.Equal([AccountFeatureId.Read], (await _profileAccess.GetFeatureGrantsAsync(Profile.SeedProfileId)).ToArray());
        Assert.Equal(versionBefore, (await _profileAccess.GetAdminFlagsAsync(Profile.SeedProfileId))!.AuthorizationVersion);
    }

    [Fact]
    public async Task AVaultLibrary_CanNeverBeGrantedToASignInsAccessRows_AndTheProfileIsUnchangedWhenItIsTried()
    {
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = "vaultgrant@example.test",
            NormalizedEmail = "VAULTGRANT@EXAMPLE.TEST",
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        var ordinary = Guid.NewGuid();
        var vault = Guid.NewGuid();
        await _accounts.CreateAccountAsync(
            account,
            Grant(account.Id, Profile.SeedProfileId, isDefault: true),
            new HashSet<AccountFeatureId> { AccountFeatureId.Read },
            new HashSet<Guid> { ordinary });
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO vault_libraries(library_id, lookup_source, lookup_server_id, adults_confirmed_at, created_at, updated_at) VALUES(@vault,'none',NULL,@now,@now,@now);",
                new { vault = vault.ToString("D"), now = _now.ToString("O") });
        }

        await Assert.ThrowsAsync<ArgumentException>(() => _accounts.ReplaceAccountAccessAsync(
            account.Id,
            new HashSet<AccountFeatureId> { AccountFeatureId.Read },
            new HashSet<Guid> { ordinary, vault },
            _now.AddMinutes(1)));

        Assert.Equal(new HashSet<Guid> { ordinary }, (await _profileAccess.GetLibraryGrantsAsync(Profile.SeedProfileId)).ToHashSet());
    }

    [Fact]
    public async Task ViewScopeParentsAndSharedIdentity_CannotBeMutatedIntoInvalidState()
    {
        var sharedLibrary = await new ViewSharedLibraryRepository(_database).GetAsync();
        using var connection = _database.CreateConnection();
        var spaceId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        connection.Execute(
            "INSERT INTO view_personal_spaces(id,owner_profile_id,library_id,created_at,updated_at) VALUES(@spaceId,@profileId,@libraryId,@now,@now);",
            new { spaceId, profileId = Profile.SeedProfileId, libraryId, now = _now.ToString("O") });

        Assert.Throws<SqliteException>(() => connection.Execute(
            "UPDATE view_personal_spaces SET library_id=@other WHERE id=@spaceId;",
            new { other = Guid.NewGuid(), spaceId }));
        Assert.Throws<SqliteException>(() => connection.Execute(
            "UPDATE view_shared_library SET library_id=@other;",
            new { other = Guid.NewGuid() }));
        // A Shared Library that holds items cannot be deleted (an empty one may go with its household).
        connection.Execute("""
            INSERT INTO local_items(id,scope_kind,library_id,media_kind,primary_file_name,primary_mime_type,created_at,updated_at)
            VALUES(@id,'shared',@libraryId,'image','beach.jpg','image/jpeg',@now,@now);
            """, new { id = Guid.NewGuid(), libraryId = sharedLibrary.LibraryId, now = _now.ToString("O") });
        Assert.Throws<SqliteException>(() => connection.Execute(
            "DELETE FROM view_shared_library;"));
    }

    [Fact]
    public async Task RevokeAccountSessions_DeletesRevokedSessionsAdminUnlocks_AndKeepsTheExceptedOne()
    {
        var owner = NewAdministrator("owner@example.com");
        await _accounts.CreateAccountAsync(
            owner,
            Grant(owner.Id, Profile.SeedProfileId, isDefault: true, administrator: true),
            AccountFeatureId.All.ToHashSet(),
            new HashSet<Guid>());
        var identities = new IdentityRepository(_database);
        var kept = Guid.NewGuid();
        var revoked = Guid.NewGuid();
        foreach (var sessionId in new[] { kept, revoked })
        {
            await identities.InsertSessionAsync(new AuthSession
            {
                Id = sessionId,
                AccountId = owner.Id,
                ActiveProfileId = Profile.SeedProfileId,
                TokenHash = $"token-{sessionId:N}",
                DeviceId = $"device-{sessionId:N}",
                DeviceName = "Browser",
                Client = "dashboard",
                AuthenticationMethod = "password",
                SecurityStamp = "owner-security-stamp",
                CreatedAt = _now,
                LastSeenAt = _now,
                ExpiresAt = _now.AddHours(1),
            });
            await _profileAccess.SetAdminUnlockAsync(new ProfileAdminUnlock
            {
                SessionId = sessionId,
                ProfileId = Profile.SeedProfileId,
                ProtectionVersion = 1,
                Method = "pin",
                GrantedAt = _now,
                ExpiresAt = _now.AddMinutes(30),
            });
        }

        var changed = await identities.RevokeAccountSessionsAsync(
            owner.Id, _now.AddMinutes(1), "password-changed", exceptSessionId: kept);

        Assert.Equal(1, changed);
        Assert.Null(await _profileAccess.GetAdminUnlockAsync(revoked, Profile.SeedProfileId, _now.AddMinutes(2)));
        Assert.NotNull(await _profileAccess.GetAdminUnlockAsync(kept, Profile.SeedProfileId, _now.AddMinutes(2)));
    }

    private void InsertProfile(Profile profile)
    {
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO profiles(id,display_name,avatar_color,avatar_image_path,role,created_at,navigation_config)
            VALUES(@Id,@DisplayName,@AvatarColor,@AvatarImagePath,@Role,@CreatedAt,@NavigationConfig);
            """, new
        {
            profile.Id,
            profile.DisplayName,
            profile.AvatarColor,
            profile.AvatarImagePath,
            Role = profile.Role.ToString(),
            CreatedAt = profile.CreatedAt.ToString("O"),
            profile.NavigationConfig,
        });
    }

    private AccountProfileGrant Grant(
        Guid accountId,
        Guid profileId,
        bool isDefault = false,
        bool administrator = false) =>
        new()
        {
            AccountId = accountId,
            ProfileId = profileId,
            IsDefault = isDefault,
            IsEnabled = true,
            AdminEnabled = administrator,
            AuthorizationVersion = 1,
            GrantedAt = _now,
        };

    private Profile NewProfile(Guid id, string name) =>
        new()
        {
            Id = id,
            DisplayName = name,
            AvatarColor = "#7C4DFF",
            Role = ProfileRole.StandardUser,
            CreatedAt = _now,
        };

    private AccountExternalLogin NewExternalLogin(Guid accountId, string subject) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        Provider = "oidc",
        Issuer = "https://identity.example",
        Subject = subject,
        LinkedAt = _now,
    };

    private Account NewAdministrator(string email) => new()
    {
        Id = Guid.NewGuid(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        IsEnabled = true,
        IsAdministrator = true,
        AuthorizationVersion = 1,
        CreatedAt = _now,
        UpdatedAt = _now,
    };

    public void Dispose()
    {
        _database.Dispose();
        using (var pool = new SqliteConnection($"Data Source={_databasePath}"))
        {
            SqliteConnection.ClearPool(pool);
        }

        try { File.Delete(_databasePath); } catch { }
    }
}
