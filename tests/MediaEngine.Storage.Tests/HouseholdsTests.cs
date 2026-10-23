using System.Security.Cryptography;
using System.Text;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Households: every profile belongs to exactly one household (a user belongs to the household of the profile it opens), a household
/// holds at most eight people, and a user can only act as people of its own household.
/// </summary>
public sealed class HouseholdsTests : IDisposable
{
    private readonly List<string> _paths = [];
    private readonly DateTimeOffset _now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    // ── Fresh data store ───────────────────────────────────────────────────────

    [Fact]
    public void AFreshDataStoreHasNoHouseholdsUntilSetupCreatesOne()
    {
        var path = NewPath();
        Open(path).Dispose();

        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM households;"));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM accounts;"));
    }

    // ── Rules ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetupCreatesTheFirstHouseholdWithTheAdministratorAccount()
    {
        using var fixture = NewFixture();
        var admin = NewAccount("owner@example.com");

        await fixture.Accounts.CreateAccountAsync(admin,
            Grant(admin.Id, Profile.SeedProfileId, isDefault: true, administrator: true),
            AccountFeatureId.All.ToHashSet(), new HashSet<Guid>());

        var household = await fixture.Households.GetForAccountAsync(admin.Id);
        Assert.NotNull(household);
        Assert.Equal(household.Id, (await fixture.Profiles.GetByIdAsync(Profile.SeedProfileId))?.HouseholdId);
        Assert.Equal(Profile.SeedProfileId, Assert.Single(await fixture.Households.ListProfilesAsync(household.Id)).Id);
        Assert.Equal(admin.Id, Assert.Single(await fixture.Households.ListAccountsAsync(household.Id)).Id);
    }

    [Fact]
    public async Task ANewProfileJoinsTheAccountsHouseholdAndTheNinthIsRefused()
    {
        using var fixture = NewFixture();
        var admin = await CreateAdministratorAsync(fixture);
        var household = (await fixture.Households.GetForAccountAsync(admin.Id))!;

        for (var index = 0; index < 7; index++)
        {
            var profile = NewProfile($"Person {index}");
            await fixture.Accounts.CreateHouseholdPersonAsync(profile, household.Id);
            Assert.Equal(household.Id, profile.HouseholdId);
        }

        Assert.Equal(8, (await fixture.Households.ListProfilesAsync(household.Id)).Count);

        var ninth = NewProfile("Ninth");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Accounts.CreateHouseholdPersonAsync(ninth, household.Id));
        Assert.Equal("A household can have up to 8 people.", failure.Message);
        Assert.Null(await fixture.Profiles.GetByIdAsync(ninth.Id));
    }

    [Fact]
    public async Task ANewUserWithANewPersonCannotBringTheNinthPersonIntoAFullHouseholdEither()
    {
        using var fixture = NewFixture();
        var admin = await CreateAdministratorAsync(fixture);
        var household = (await fixture.Households.GetForAccountAsync(admin.Id))!;
        for (var index = 0; index < 7; index++)
        {
            await fixture.Accounts.CreateHouseholdPersonAsync(NewProfile($"Person {index}"), household.Id);
        }

        var member = NewAccount("member@example.com");
        var ninth = NewProfile("Ninth");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Accounts.CreateAccountAsync(member,
            Grant(member.Id, ninth.Id, isDefault: true), new HashSet<AccountFeatureId>(), new HashSet<Guid>(),
            newProfile: ninth, householdId: household.Id));

        Assert.Equal("A household can have up to 8 people.", failure.Message);
        Assert.Equal(8, (await fixture.Households.ListProfilesAsync(household.Id)).Count);
        Assert.Null(await fixture.Accounts.GetByIdAsync(member.Id));
        Assert.Null(await fixture.Profiles.GetByIdAsync(ninth.Id));
    }

    [Fact]
    public async Task AUserCanOnlyActAsPeopleOfItsOwnHousehold()
    {
        using var fixture = NewFixture();
        var admin = await CreateAdministratorAsync(fixture);

        var outsider = NewAccount("outsider@example.com");
        var outsiderProfile = NewProfile("Outsider");
        await fixture.Accounts.CreateAccountAsync(outsider,
            Grant(outsider.Id, outsiderProfile.Id, isDefault: true),
            new HashSet<AccountFeatureId>(), new HashSet<Guid>(), newProfile: outsiderProfile);
        Assert.NotEqual(
            (await fixture.Households.GetForAccountAsync(admin.Id))!.Id,
            (await fixture.Households.GetForAccountAsync(outsider.Id))!.Id);

        Assert.False(await fixture.Accounts.HasProfileAccessAsync(admin.Id, outsiderProfile.Id));
        Assert.False(await fixture.Accounts.HasProfileAccessAsync(outsider.Id, Profile.SeedProfileId));
        Assert.DoesNotContain(outsiderProfile.Id, (await fixture.Accounts.GetHouseholdProfilesAsync(admin.Id)).Select(entry => entry.ProfileId));
        Assert.DoesNotContain(Profile.SeedProfileId, await fixture.Accounts.GetProfileIdsAsync(outsider.Id));
    }

    [Fact]
    public async Task AnInvitationForSomeoneOutsideStartsANewHouseholdAndTheNinthPersonIsRefused()
    {
        using var fixture = NewFixture();
        var admin = await CreateAdministratorAsync(fixture);
        var adminHousehold = (await fixture.Households.GetForAccountAsync(admin.Id))!;

        var guest = NewAccount("guest@example.com");
        var guestProfile = NewProfile("Guest");
        await fixture.Accounts.CreateInvitedAccountAsync(guest,
            [Grant(guest.Id, guestProfile.Id, isDefault: true)], NewInvitation(guest.Id), newProfile: guestProfile);

        var guestHousehold = (await fixture.Households.GetForAccountAsync(guest.Id))!;
        Assert.NotEqual(adminHousehold.Id, guestHousehold.Id);
        Assert.Equal(guestHousehold.Id, guestProfile.HouseholdId);
        Assert.Equal(guestProfile.Id, Assert.Single(await fixture.Households.ListProfilesAsync(guestHousehold.Id)).Id);

        var memberProfile = NewProfile("Member");
        await fixture.Accounts.CreateHouseholdPersonAsync(memberProfile, adminHousehold.Id);
        var member = NewAccount("member@example.com");
        await fixture.Accounts.CreateInvitedAccountAsync(member,
            [Grant(member.Id, memberProfile.Id, isDefault: true)], NewInvitation(member.Id));
        Assert.Equal(adminHousehold.Id, (await fixture.Households.GetForAccountAsync(member.Id))!.Id);

        // Fill the admin household to eight, then a ninth person invited by another route is refused too.
        for (var index = 0; index < 6; index++)
        {
            await fixture.Accounts.CreateHouseholdPersonAsync(NewProfile($"Person {index}"), adminHousehold.Id);
        }

        var latePerson = NewProfile("Late");
        var late = NewAccount("late@example.com");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Accounts.CreateInvitedAccountAsync(late,
            [Grant(late.Id, latePerson.Id, isDefault: true)], NewInvitation(late.Id), newProfile: latePerson, householdId: adminHousehold.Id));
        Assert.Equal("A household can have up to 8 people.", failure.Message);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private sealed class Fixture(DatabaseConnection database) : IDisposable
    {
        public AccountRepository Accounts { get; } = new(database);
        public ProfileRepository Profiles { get; } = new(database);
        public HouseholdRepository Households { get; } = new(database);
        public DatabaseConnection Database => database;

        public void Dispose() => database.Dispose();
    }

    private Fixture NewFixture()
    {
        DapperConfiguration.Configure();
        var database = Open(NewPath());
        return new Fixture(database);
    }

    private async Task<Account> CreateAdministratorAsync(Fixture fixture)
    {
        var admin = NewAccount("owner@example.com");
        await fixture.Accounts.CreateAccountAsync(admin,
            Grant(admin.Id, Profile.SeedProfileId, isDefault: true, administrator: true),
            AccountFeatureId.All.ToHashSet(), new HashSet<Guid>());
        return admin;
    }

    private Account NewAccount(string email) => new()
    {
        Id = Guid.NewGuid(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        IsEnabled = true,
        AuthorizationVersion = 1,
        CreatedAt = _now,
        UpdatedAt = _now,
    };

    private Profile NewProfile(string name) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = name,
        AvatarColor = "#7C4DFF",
        Role = ProfileRole.StandardUser,
        CreatedAt = _now,
    };

    private static TestGrant Grant(Guid accountId, Guid profileId, bool isDefault = false, bool administrator = false) =>
        new(accountId, profileId, isDefault, administrator);

    private AccountInvitation NewInvitation(Guid accountId) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        TokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")))),
        CreatedAt = _now,
        ExpiresAt = _now.AddDays(7),
    };

    private string NewPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima-households-{Guid.NewGuid():N}.db");
        _paths.Add(path);
        return path;
    }

    // ── Household administrators and per-household Shared libraries (B11a) ────

    [Fact]
    public async Task EachHousehold_GetsItsOwnSharedLibrary_AndTheServerOneBelongsToTheServerAdministrator()
    {
        var path = NewPath();
        using var database = Open(path);
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            raw.Open();
            Exec(raw, "INSERT INTO households(id,name,created_at) VALUES(@h,'Older','2026-01-01T00:00:00.0000000+00:00');", ("@h", older));
            Exec(raw, "INSERT INTO households(id,name,created_at) VALUES(@h,'Newer','2026-02-01T00:00:00.0000000+00:00');", ("@h", newer));
        }

        var libraries = new ViewSharedLibraryRepository(database);
        var first = await libraries.EnsureForHouseholdAsync(older);
        var second = await libraries.EnsureForHouseholdAsync(newer);

        Assert.NotEqual(first.LibraryId, second.LibraryId);
        Assert.Equal(older, first.HouseholdId);
        Assert.Equal(first.LibraryId, (await libraries.EnsureForHouseholdAsync(older)).LibraryId);

        // With no server administrator yet, the server's own is the oldest household's.
        Assert.Equal(first.LibraryId, (await libraries.GetAsync()).LibraryId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => libraries.EnsureForHouseholdAsync(Guid.NewGuid()));
    }

    private static DatabaseConnection Open(string path)
    {
        DapperConfiguration.Configure();
        var database = new DatabaseConnection(path);
        try
        {
            database.InitializeSchema();
            database.RunStartupChecks();
            return database;
        }
        catch
        {
            database.Dispose();
            TestTemp.ReleaseSqlitePool(path);
            throw;
        }
    }

    private static void Exec(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value is Guid guid ? GuidSql.ToBlob(guid) : value);
        }

        command.ExecuteNonQuery();
    }

    private static long Scalar(string path, string sql, params (string Name, object Value)[] parameters) =>
        Convert.ToInt64(Query(path, sql, parameters), System.Globalization.CultureInfo.InvariantCulture);

    private static object? Query(string path, string sql, (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value is Guid guid ? GuidSql.ToBlob(guid) : value);
        }

        return command.ExecuteScalar();
    }

    public void Dispose()
    {
        foreach (var path in _paths)
        {
            TestTemp.ReleaseSqlitePool(path);
            foreach (var file in new[] { path, $"{path}-wal", $"{path}-shm" })
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }
    }
}
