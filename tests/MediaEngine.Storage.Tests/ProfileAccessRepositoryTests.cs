using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The profile holds the person's access (users, profiles and households spec, wave UPH1a): administrator roles, features, libraries,
/// the admin PIN question and its unlocks live on the profile, a sign-in is attached to one profile, and one household is the default.
/// </summary>
public sealed class ProfileAccessRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly ProfileAccessRepository _access;
    private readonly AccountRepository _accounts;
    private readonly HouseholdRepository _households;
    private readonly DateTimeOffset _now = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    public ProfileAccessRepositoryTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_profile_access_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _access = new ProfileAccessRepository(_database);
        _accounts = new AccountRepository(_database);
        _households = new HouseholdRepository(_database);
    }

    [Fact]
    public void TheUpgrade_AddsTheProfileSideColumnsAndTables()
    {
        using var connection = _database.CreateConnection();

        Assert.Contains("is_server_admin", Columns(connection, "profiles"));
        Assert.Contains("is_household_admin", Columns(connection, "profiles"));
        Assert.Contains("authorization_version", Columns(connection, "profiles"));
        Assert.Contains("profile_id", Columns(connection, "accounts"));
        Assert.Contains("is_default", Columns(connection, "households"));
        foreach (var table in new[] { "profile_feature_grants", "profile_library_hidden", "profile_admin_protections", "profile_admin_unlocks" })
        {
            Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@table;", new { table }));
        }
    }

    [Fact]
    public async Task AProfilesAccess_RoundTrips_AndEveryChangeRaisesItsAuthorizationVersion()
    {
        var profileId = InsertProfile();
        var libraryA = Guid.NewGuid();
        var libraryB = Guid.NewGuid();
        var before = (await _access.GetAdminFlagsAsync(profileId))!;
        Assert.Equal(1, before.AuthorizationVersion);
        Assert.False(before.IsServerAdministrator);
        Assert.False(before.IsHouseholdAdministrator);

        Assert.True(await _access.ReplaceAccessAsync(profileId, new HashSet<AccountFeatureId> { AccountFeatureId.Read, AccountFeatureId.Watch }, new HashSet<Guid> { libraryA, libraryB }, _now));
        Assert.Equal(2, (await _access.GetAdminFlagsAsync(profileId))!.AuthorizationVersion);
        Assert.Equal([AccountFeatureId.Read, AccountFeatureId.Watch], (await _access.GetFeatureGrantsAsync(profileId)).OrderBy(feature => feature.Value));
        Assert.Equal(new[] { libraryA, libraryB }.Order(), (await _access.GetHiddenLibrariesAsync(profileId)).Order());

        Assert.True(await _access.ReplaceAccessAsync(profileId, new HashSet<AccountFeatureId> { AccountFeatureId.Listen }, new HashSet<Guid>(), _now));
        Assert.Equal([AccountFeatureId.Listen], await _access.GetFeatureGrantsAsync(profileId));
        Assert.Empty(await _access.GetHiddenLibrariesAsync(profileId));

        Assert.True(await _access.SetAdminFlagsAsync(profileId, isServerAdministrator: true, isHouseholdAdministrator: true, _now));
        var after = (await _access.GetAdminFlagsAsync(profileId))!;
        Assert.True(after.IsServerAdministrator);
        Assert.True(after.IsHouseholdAdministrator);
        Assert.Equal(4, after.AuthorizationVersion);
        var read = await new ProfileRepository(_database).GetByIdAsync(profileId);
        Assert.True(read!.IsServerAdministrator);
        Assert.True(read.IsHouseholdAdministrator);
        Assert.Equal(4, read.AuthorizationVersion);
    }

    [Fact]
    public async Task ChangingTheAccessOfAProfileThatDoesNotExist_ChangesNothingAndSaysSo()
    {
        var missing = Guid.NewGuid();

        Assert.False(await _access.ReplaceAccessAsync(missing, new HashSet<AccountFeatureId> { AccountFeatureId.Read }, new HashSet<Guid>(), _now));
        Assert.False(await _access.SetAdminFlagsAsync(missing, true, true, _now));
        Assert.Null(await _access.GetAdminFlagsAsync(missing));
        Assert.Empty(await _access.GetFeatureGrantsAsync(missing));
    }

    [Fact]
    public async Task TheVault_CanNeverBeHiddenFromOrShownToAProfileAsALibrary()
    {
        var profileId = InsertProfile();
        var vault = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("INSERT INTO vault_libraries(library_id,adults_confirmed_at,created_at,updated_at) VALUES (@id,@now,@now,@now);",
                new { id = vault.ToString(), now = _now.ToString("O") });
        }

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _access.ReplaceAccessAsync(profileId, new HashSet<AccountFeatureId>(), new HashSet<Guid> { vault }, _now));
        Assert.Empty(await _access.GetHiddenLibrariesAsync(profileId));
    }

    [Fact]
    public async Task TheAdminPinQuestion_RoundTrips_AndEverySaveRaisesItsVersionClearsUnlocksAndRaisesTheProfilesVersion()
    {
        var profileId = InsertProfile();
        var sessionId = InsertSession(profileId);

        await _access.SetAdminProtectionAsync(new ProfileAdminProtection
        {
            ProfileId = profileId,
            IsEnabled = true,
            UnlockMode = "FixedDuration",
            UnlockMinutes = 15,
            ProtectionVersion = 1,
            UpdatedAt = _now,
        });
        await _access.SetAdminUnlockAsync(new ProfileAdminUnlock
        {
            SessionId = sessionId,
            ProfileId = profileId,
            ProtectionVersion = 1,
            Method = "ProfilePin",
            GrantedAt = _now,
            ExpiresAt = _now.AddMinutes(15),
        });

        var protection = (await _access.GetAdminProtectionAsync(profileId))!;
        Assert.True(protection.IsEnabled);
        Assert.Equal(15, protection.UnlockMinutes);
        Assert.Equal(1, protection.ProtectionVersion);
        Assert.NotNull(await _access.GetAdminUnlockAsync(sessionId, profileId, _now.AddMinutes(1)));
        Assert.Null(await _access.GetAdminUnlockAsync(sessionId, profileId, _now.AddMinutes(16)));

        await _access.SetAdminProtectionAsync(new ProfileAdminProtection
        {
            ProfileId = profileId,
            IsEnabled = true,
            UnlockMode = "LockOnLeave",
            ProtectionVersion = 1,
            UpdatedAt = _now,
        });

        Assert.Equal(2, (await _access.GetAdminProtectionAsync(profileId))!.ProtectionVersion);
        Assert.Equal("LockOnLeave", (await _access.GetAdminProtectionAsync(profileId))!.UnlockMode);
        Assert.Null(await _access.GetAdminUnlockAsync(sessionId, profileId, _now.AddMinutes(1)));
        Assert.Equal(3, (await _access.GetAdminFlagsAsync(profileId))!.AuthorizationVersion);
    }

    [Fact]
    public async Task TheFirstSaveOfTheAdminPinQuestion_StartsAtVersionOne_WhateverVersionTheCallerSends()
    {
        var profileId = InsertProfile();

        await _access.SetAdminProtectionAsync(new ProfileAdminProtection
        {
            ProfileId = profileId,
            IsEnabled = true,
            UnlockMode = "FixedDuration",
            UnlockMinutes = 15,
            ProtectionVersion = 9,
            UpdatedAt = _now,
        });

        Assert.Equal(1, (await _access.GetAdminProtectionAsync(profileId))!.ProtectionVersion);
    }

    [Fact]
    public async Task AnAdminUnlock_IsClearedBySession_AndGoesWithItsSession()
    {
        var profileId = InsertProfile();
        var cleared = InsertSession(profileId);
        var ended = InsertSession(profileId);
        foreach (var session in new[] { cleared, ended })
        {
            await _access.SetAdminUnlockAsync(new ProfileAdminUnlock { SessionId = session, ProfileId = profileId, ProtectionVersion = 1, Method = "ProfilePin", GrantedAt = _now });
        }

        await _access.ClearAdminUnlockAsync(cleared);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM auth_sessions WHERE id = @ended;", new { ended });
        }

        Assert.Null(await _access.GetAdminUnlockAsync(cleared, profileId, _now));
        Assert.Null(await _access.GetAdminUnlockAsync(ended, profileId, _now));
    }

    [Fact]
    public async Task DeletingAProfile_DeletesItsUserItsGrantsItsProtectionAndItsUnlocks()
    {
        var profileId = InsertProfile();
        var spared = InsertProfile();
        var userId = InsertUser(profileId, "mom@home.com");
        var sparedUser = InsertUser(spared, "dad@home.com");
        var sessionId = InsertSession(spared);
        await _access.ReplaceAccessAsync(profileId, new HashSet<AccountFeatureId> { AccountFeatureId.Read }, new HashSet<Guid> { Guid.NewGuid() }, _now);
        await _access.ReplaceAccessAsync(spared, new HashSet<AccountFeatureId> { AccountFeatureId.Watch }, new HashSet<Guid>(), _now);
        await _access.SetAdminProtectionAsync(new ProfileAdminProtection { ProfileId = profileId, IsEnabled = true, UpdatedAt = _now });
        await _access.SetAdminUnlockAsync(new ProfileAdminUnlock { SessionId = sessionId, ProfileId = profileId, ProtectionVersion = 1, Method = "ProfilePin", GrantedAt = _now });
        Assert.Equal(userId, await _access.FindUserIdOfProfileAsync(profileId));

        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM profiles WHERE id = @profileId;", new { profileId });
        }

        Assert.Null(await _accounts.GetByIdAsync(userId));
        Assert.Null(await _access.FindUserIdOfProfileAsync(profileId));
        Assert.Empty(await _access.GetFeatureGrantsAsync(profileId));
        Assert.Empty(await _access.GetHiddenLibrariesAsync(profileId));
        Assert.Null(await _access.GetAdminProtectionAsync(profileId));
        Assert.Null(await _access.GetAdminUnlockAsync(sessionId, profileId, _now));
        Assert.NotNull(await _accounts.GetByIdAsync(sparedUser));
        Assert.Equal([AccountFeatureId.Watch], await _access.GetFeatureGrantsAsync(spared));
    }

    [Fact]
    public void AProfile_HasAtMostOneUser()
    {
        var profileId = InsertProfile();
        InsertUser(profileId, "mom@home.com");

        Assert.Throws<SqliteException>(() => InsertUser(profileId, "also-mom@home.com"));
    }

    [Fact]
    public async Task OnlyOneHouseholdIsTheDefault_AndMakingAnotherTheDefaultMovesTheMark()
    {
        var first = await _households.CreateAsync("First", _now);
        var second = await _households.CreateAsync("Second", _now);
        Assert.Null(await _access.GetDefaultHouseholdIdAsync());

        Assert.True(await _access.SetDefaultHouseholdAsync(first.Id));
        Assert.Equal(first.Id, await _access.GetDefaultHouseholdIdAsync());
        Assert.True((await _households.GetByIdAsync(first.Id))!.IsDefault);

        Assert.True(await _access.SetDefaultHouseholdAsync(second.Id));
        Assert.Equal(second.Id, await _access.GetDefaultHouseholdIdAsync());
        Assert.False((await _households.GetByIdAsync(first.Id))!.IsDefault);
        Assert.False(await _access.SetDefaultHouseholdAsync(Guid.NewGuid()));
        Assert.Equal(second.Id, await _access.GetDefaultHouseholdIdAsync());

        using var connection = _database.CreateConnection();
        Assert.Throws<SqliteException>(() =>
            connection.Execute("UPDATE households SET is_default = 1 WHERE id = @id;", new { id = first.Id }));
    }

    [Fact]
    public async Task TheServersOwnHousehold_IsTheDefault_WhenTheSharedLibraryCreatesIt()
    {
        var shared = await new ViewSharedLibraryRepository(_database).GetAsync();

        Assert.Equal(shared.HouseholdId, await _access.GetDefaultHouseholdIdAsync());
    }

    [Fact]
    public async Task ASignInCreatedForAProfile_GivesThatProfileTheAccountsRolesAndAccess_AndMakesItsHouseholdTheDefault()
    {
        var profile = new Profile { Id = Guid.NewGuid(), DisplayName = "Shy", CreatedAt = _now };
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = "shy@tuvima.com",
            NormalizedEmail = "SHY@TUVIMA.COM",
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = _now,
            UpdatedAt = _now,
            ProfileId = profile.Id,
        };
        var library = Guid.NewGuid();

        await _accounts.CreateAccountAsync(
            account,
            new TestGrant(account.Id, profile.Id, IsDefault: true, AdminEnabled: true),
            AccountFeatureId.All.ToHashSet(),
            new HashSet<Guid> { library },
            newProfile: profile);

        var flags = (await _access.GetAdminFlagsAsync(profile.Id))!;
        Assert.True(flags.IsServerAdministrator);
        Assert.True(flags.IsHouseholdAdministrator);
        Assert.Equal(AccountFeatureId.All.OrderBy(feature => feature.Value), (await _access.GetFeatureGrantsAsync(profile.Id)).OrderBy(feature => feature.Value));
        Assert.Equal([library], await _access.GetHiddenLibrariesAsync(profile.Id));
        Assert.Equal(account.Id, await _access.FindUserIdOfProfileAsync(profile.Id));
        Assert.Equal(profile.Id, (await _accounts.GetByIdAsync(account.Id))!.ProfileId);
        var householdId = (await new HouseholdRepository(_database).GetForAccountAsync(account.Id))!.Id;
        Assert.Equal(householdId, await _access.GetDefaultHouseholdIdAsync());
        Assert.Equal(householdId, (await new ProfileRepository(_database).GetByIdAsync(profile.Id))!.HouseholdId);
    }

    [Fact]
    public async Task ASignInCannotBeAttachedToAProfileItDoesNotOpen()
    {
        var profile = new Profile { Id = Guid.NewGuid(), DisplayName = "Shy", CreatedAt = _now };
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = "shy@tuvima.com",
            NormalizedEmail = "SHY@TUVIMA.COM",
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = _now,
            UpdatedAt = _now,
            ProfileId = Guid.NewGuid(),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => _accounts.CreateUserAsync(
            account,
            new NewUserPlacement(NewProfile: profile)));
        Assert.Null(await _accounts.GetByIdAsync(account.Id));
    }

    private static List<string> Columns(SqliteConnection connection, string table) =>
        connection.Query<string>($"SELECT name FROM pragma_table_info('{table}');").ToList();

    private Guid InsertProfile()
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@id,@name,'#7C4DFF','StandardUser',@now);",
            new { id, name = $"Person {id:N}", now = _now.ToString("O") });
        return id;
    }

    private Guid InsertUser(Guid profileId, string email)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO accounts(id,email,normalized_email,is_enabled,authorization_version,created_at,updated_at,profile_id) VALUES(@id,@email,@normalized,1,1,@now,@now,@profileId);",
            new { id, email, normalized = email.ToUpperInvariant(), now = _now.ToString("O"), profileId });
        return id;
    }

    private Guid InsertSession(Guid profileId)
    {
        var sessionId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@ownerProfileId,'Session owner','#7C4DFF','StandardUser',@now);
            INSERT INTO accounts(id,email,normalized_email,is_enabled,authorization_version,created_at,updated_at,profile_id)
            VALUES(@accountId,@email,@normalized,1,1,@now,@now,@ownerProfileId);
            INSERT INTO auth_sessions(id,account_id,active_profile_id,token_hash,device_id,device_name,client,authentication_method,security_stamp,created_at,last_seen_at,expires_at)
            VALUES(@sessionId,@accountId,@profileId,@hash,'device','Device','Dashboard','password','stamp',@now,@now,@later);
            """,
            new
            {
                sessionId,
                accountId,
                profileId,
                ownerProfileId = Guid.NewGuid(),
                email = $"{accountId:N}@example.com",
                normalized = $"{accountId:N}@EXAMPLE.COM",
                hash = Guid.NewGuid().ToString("N"),
                now = _now.ToString("O"),
                later = _now.AddDays(1).ToString("O"),
            });
        return sessionId;
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
    }
}
