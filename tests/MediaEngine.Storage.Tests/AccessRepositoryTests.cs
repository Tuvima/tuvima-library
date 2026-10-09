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
    }

    [Fact]
    public async Task Grants_EnforceMaximumDefaultAndFinalAdministratorTransactionally()
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

        var newDefault = Grant(account.Id, profileIds[0], isDefault: true);
        await _accounts.UpsertGrantAsync(newDefault);
        var grants = await _accounts.GetGrantsAsync(account.Id);
        Assert.Single(grants, grant => grant.IsDefault);
        Assert.Equal(profileIds[0], grants.Single(grant => grant.IsDefault).ProfileId);

        var adminGrant = Grant(
            account.Id,
            Profile.SeedProfileId,
            administrator: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _accounts.UpsertGrantAsync(adminGrant));
        Assert.True((await _accounts.GetGrantAsync(account.Id, Profile.SeedProfileId))?.AdminEnabled);

        account.IsAdministrator = false;
        account.UpdatedAt = _now.AddMinutes(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _accounts.UpdateAccountAsync(account));
        Assert.True((await _accounts.GetByIdAsync(account.Id))?.IsAdministrator);
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
        await _accounts.CreateAccountAsync(
            pending,
            Grant(pending.Id, Profile.SeedProfileId, isDefault: true, administrator: true),
            AccountFeatureId.All.ToHashSet(),
            new HashSet<Guid>());

        owner.IsAdministrator = false;
        owner.UpdatedAt = _now.AddMinutes(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _accounts.UpdateAccountAsync(owner));
        Assert.True((await _accounts.GetByIdAsync(owner.Id))?.IsAdministrator);
    }

    [Fact]
    public async Task PresentationRoleDoesNotReplaceAccountGrantProtectionForSoleUsableAdministrator()
    {
        var owner = NewAdministrator("owner@example.com");
        await _accounts.CreateAccountAsync(
            owner,
            Grant(owner.Id, Profile.SeedProfileId, isDefault: true),
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
        var managed = NewProfile(Guid.NewGuid(), "Administration");
        Assert.Equal(ProfileRole.StandardUser, managed.Role);
        await _accounts.CreateManagedProfileAsync(managed, Grant(owner.Id, managed.Id));
        await _accounts.UpsertGrantAsync(Grant(owner.Id, managed.Id, administrator: true));

        var pending = NewAdministrator("pending@example.com");
        await _accounts.CreateAccountAsync(
            pending,
            Grant(pending.Id, Profile.SeedProfileId, isDefault: true, administrator: true),
            AccountFeatureId.All.ToHashSet(),
            new HashSet<Guid>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _accounts.DeleteManagedProfileAsync(managed.Id));
        Assert.NotNull(await _profiles.GetByIdAsync(managed.Id));
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
            IsLocalOnly = true,
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
        Assert.NotNull(await _accounts.GetGrantAsync(target.Id, profile.Id));
        Assert.Single(await _accounts.GetAllAsync());
        await _accounts.DeleteManagedProfileAsync(profile.Id);
        Assert.Null(await _profiles.GetByIdAsync(profile.Id));
        Assert.NotNull(await _accounts.GetByIdAsync(target.Id));
        Assert.Null(await _accounts.GetGrantAsync(target.Id, profile.Id));
    }

    [Theory]
    [InlineData(false, "remote@example.com")]
    [InlineData(true, null)]
    public async Task AccountCreation_WithNewDefaultProfile_IsAtomicAndDoesNotShareAnExistingProfile(
        bool localOnly,
        string? email)
    {
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = email?.ToUpperInvariant(),
            IsLocalOnly = localOnly,
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        var profile = NewProfile(Guid.NewGuid(), localOnly ? "Local user" : "Remote user");

        await _accounts.CreateAccountAsync(
            account,
            Grant(account.Id, profile.Id, isDefault: true),
            new HashSet<AccountFeatureId>(),
            new HashSet<Guid>(),
            newProfile: profile);

        Assert.NotNull(await _accounts.GetByIdAsync(account.Id));
        Assert.Equal(profile.DisplayName, (await _profiles.GetByIdAsync(profile.Id))?.DisplayName);
        Assert.True((await _accounts.GetGrantAsync(account.Id, profile.Id))?.IsDefault);
        Assert.Null(await _accounts.GetGrantAsync(account.Id, Profile.SeedProfileId));
    }

    [Fact]
    public async Task AccessReplacement_IncrementsAccountAuthorizationVersion()
    {
        var account = new Account
        {
            Id = Guid.NewGuid(),
            IsLocalOnly = true,
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

        var before = (await _accounts.GetByIdAsync(account.Id))!.AuthorizationVersion;
        await _accounts.ReplaceAccountAccessAsync(
            account.Id,
            new HashSet<AccountFeatureId> { AccountFeatureId.Listen },
            new HashSet<Guid>(),
            _now.AddMinutes(1));

        Assert.Equal(before + 1, (await _accounts.GetByIdAsync(account.Id))!.AuthorizationVersion);
        Assert.False(await _accounts.HasFeatureGrantAsync(account.Id, AccountFeatureId.Read));
        Assert.True(await _accounts.HasFeatureGrantAsync(account.Id, AccountFeatureId.Listen));
    }

    [Fact]
    public void ViewScopeParentsAndSharedIdentity_CannotBeMutatedIntoInvalidState()
    {
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
            "UPDATE view_shared_library SET library_id=@other WHERE singleton_key=1;",
            new { other = Guid.NewGuid() }));
        Assert.Throws<SqliteException>(() => connection.Execute(
            "DELETE FROM view_shared_library WHERE singleton_key=1;"));
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

    [Fact]
    public async Task StartupMigration_PromotesRestrictedProfilesOfAdministratorsOnly_AndIsIdempotent()
    {
        var admin = NewAdministrator("admin@example.com");
        var adminProfile = NewProfile(Guid.NewGuid(), "Admin profile");
        adminProfile.Role = ProfileRole.RestrictedProfile;
        InsertProfile(adminProfile);
        await _accounts.CreateAccountAsync(
            admin,
            Grant(admin.Id, adminProfile.Id, isDefault: true, administrator: true),
            AccountFeatureId.All.ToHashSet(),
            new HashSet<Guid>());

        var member = NewAdministrator("member@example.com");
        member.IsAdministrator = false;
        var childProfile = NewProfile(Guid.NewGuid(), "Child");
        childProfile.Role = ProfileRole.RestrictedProfile;
        InsertProfile(childProfile);
        await _accounts.CreateAccountAsync(
            member,
            Grant(member.Id, childProfile.Id, isDefault: true),
            new HashSet<AccountFeatureId>(),
            new HashSet<Guid>());

        _database.RunStartupChecks();
        _database.RunStartupChecks();

        Assert.Equal(ProfileRole.StandardUser, (await _profiles.GetByIdAsync(adminProfile.Id))!.Role);
        Assert.Equal(ProfileRole.RestrictedProfile, (await _profiles.GetByIdAsync(childProfile.Id))!.Role);
    }

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
