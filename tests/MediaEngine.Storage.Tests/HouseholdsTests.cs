using System.Security.Cryptography;
using System.Text;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Households: every account and every profile belongs to exactly one household, a household holds at most eight
/// people, and a sign-in can only open people from its own household.
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
        var admin = NewAccount("owner@example.com", isAdministrator: true);

        await fixture.Accounts.CreateAccountAsync(admin,
            Grant(admin.Id, Profile.SeedProfileId, isDefault: true, administrator: true),
            AccountFeatureId.All.ToHashSet(), new HashSet<Guid>());

        var household = await fixture.Households.GetForAccountAsync(admin.Id);
        Assert.NotNull(household);
        Assert.Equal(household.Id, (await fixture.Accounts.GetByIdAsync(admin.Id))?.HouseholdId);
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
            await fixture.Accounts.CreateManagedProfileAsync(profile, Grant(admin.Id, profile.Id));
            Assert.Equal(household.Id, profile.HouseholdId);
        }

        Assert.Equal(8, (await fixture.Households.ListProfilesAsync(household.Id)).Count);

        var ninth = NewProfile("Ninth");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Accounts.CreateManagedProfileAsync(ninth, Grant(admin.Id, ninth.Id)));
        Assert.Equal("A household can have up to 8 people.", failure.Message);
        Assert.Null(await fixture.Profiles.GetByIdAsync(ninth.Id));
    }

    [Fact]
    public async Task AnotherSignInInTheHouseholdCannotBringTheNinthPersonInEither()
    {
        using var fixture = NewFixture();
        var admin = await CreateAdministratorAsync(fixture);
        var household = (await fixture.Households.GetForAccountAsync(admin.Id))!;
        for (var index = 0; index < 7; index++)
        {
            var profile = NewProfile($"Person {index}");
            await fixture.Accounts.CreateManagedProfileAsync(profile, Grant(admin.Id, profile.Id));
        }

        // A second sign-in in the same household, then a loose profile that has no household yet.
        var member = NewAccount("member@example.com", householdId: household.Id);
        await fixture.Accounts.InsertAsync(member);
        await fixture.Accounts.UpsertGrantAsync(Grant(member.Id, Profile.SeedProfileId, isDefault: true));
        var loose = NewProfile("Loose");
        InsertLooseProfile(fixture, loose);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Accounts.UpsertGrantAsync(Grant(member.Id, loose.Id)));
        Assert.Equal("A household can have up to 8 people.", failure.Message);
        Assert.Equal(8, (await fixture.Households.ListProfilesAsync(household.Id)).Count);
    }

    [Fact]
    public async Task AGrantAcrossHouseholdsIsRefused()
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

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Accounts.UpsertGrantAsync(Grant(admin.Id, outsiderProfile.Id)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Accounts.GrantProfileAsync(Grant(outsider.Id, Profile.SeedProfileId)));
        Assert.Null(await fixture.Accounts.GetGrantAsync(admin.Id, outsiderProfile.Id));
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

        // Inviting for existing people keeps them in their household; people from two households cannot share one.
        var crossed = NewAccount("crossed@example.com");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Accounts.CreateInvitedAccountAsync(crossed,
            [Grant(crossed.Id, Profile.SeedProfileId, isDefault: true), Grant(crossed.Id, guestProfile.Id)],
            NewInvitation(crossed.Id)));
        Assert.Null(await fixture.Accounts.GetByIdAsync(crossed.Id));

        var member = NewAccount("member@example.com");
        await fixture.Accounts.CreateInvitedAccountAsync(member,
            [Grant(member.Id, Profile.SeedProfileId, isDefault: true)], NewInvitation(member.Id));
        Assert.Equal(adminHousehold.Id, (await fixture.Households.GetForAccountAsync(member.Id))!.Id);

        // Fill the admin household to eight, then a ninth person invited by another route is refused too.
        for (var index = 0; index < 7; index++)
        {
            var profile = NewProfile($"Person {index}");
            await fixture.Accounts.CreateManagedProfileAsync(profile, Grant(admin.Id, profile.Id));
        }

        var loose = NewProfile("Loose");
        InsertLooseProfile(fixture, loose);
        var late = NewAccount("late@example.com", householdId: adminHousehold.Id);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Accounts.CreateInvitedAccountAsync(late,
            [Grant(late.Id, loose.Id, isDefault: true)], NewInvitation(late.Id)));
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
        var admin = NewAccount("owner@example.com", isAdministrator: true);
        await fixture.Accounts.CreateAccountAsync(admin,
            Grant(admin.Id, Profile.SeedProfileId, isDefault: true, administrator: true),
            AccountFeatureId.All.ToHashSet(), new HashSet<Guid>());
        return admin;
    }

    private static void InsertLooseProfile(Fixture fixture, Profile profile)
    {
        using var connection = fixture.Database.CreateConnection();
        Dapper.SqlMapper.Execute(connection,
            "INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@Id,@DisplayName,@AvatarColor,'StandardUser',@Created);",
            new { profile.Id, profile.DisplayName, profile.AvatarColor, Created = profile.CreatedAt.ToString("O") });
    }

    private Account NewAccount(string email, bool isAdministrator = false, Guid? householdId = null) => new()
    {
        Id = Guid.NewGuid(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        IsEnabled = true,
        IsAdministrator = isAdministrator,
        AuthorizationVersion = 1,
        CreatedAt = _now,
        UpdatedAt = _now,
        HouseholdId = householdId,
    };

    private Profile NewProfile(string name) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = name,
        AvatarColor = "#7C4DFF",
        Role = ProfileRole.StandardUser,
        CreatedAt = _now,
    };

    private AccountProfileGrant Grant(Guid accountId, Guid profileId, bool isDefault = false, bool administrator = false) => new()
    {
        AccountId = accountId,
        ProfileId = profileId,
        IsDefault = isDefault,
        IsEnabled = true,
        AdminEnabled = administrator,
        AuthorizationVersion = 1,
        GrantedAt = _now,
    };

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
