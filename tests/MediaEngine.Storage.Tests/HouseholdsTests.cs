using System.Security.Cryptography;
using System.Text;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Households: every account and every profile belongs to exactly one household, a household holds at most eight
/// people, and a sign-in can only open people from its own household.
/// </summary>
public sealed class HouseholdsTests : IDisposable
{
    private static readonly Guid FirstAccount = Guid.Parse("a1000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondAccount = Guid.Parse("a1000000-0000-0000-0000-000000000002");
    private static readonly Guid FirstProfile = Guid.Parse("c1000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondProfile = Guid.Parse("c1000000-0000-0000-0000-000000000002");
    private static readonly Guid SharedProfile = Guid.Parse("c1000000-0000-0000-0000-000000000003");

    private readonly List<string> _paths = [];
    private readonly DateTimeOffset _now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    // ── Migration ──────────────────────────────────────────────────────────────

    [Fact]
    public void BackfillGivesEachAccountItsOwnHouseholdAndItsProfilesJoin()
    {
        var path = CreateLegacyDatabase(raw =>
        {
            SeedAccount(raw, FirstAccount, "first@example.com", "2026-01-01T00:00:00.0000000+00:00");
            SeedAccount(raw, SecondAccount, "second@example.com", "2026-01-02T00:00:00.0000000+00:00");
            SeedProfile(raw, FirstProfile, "Alex");
            SeedProfile(raw, SecondProfile, "Sam");
            SeedGrant(raw, FirstAccount, FirstProfile, isDefault: true);
            SeedGrant(raw, SecondAccount, SecondProfile, isDefault: true);
        });

        using (var database = Open(path))
        {
            Assert.Empty(database.StartupNotes);
        }

        Assert.Equal(2, Scalar(path, "SELECT COUNT(*) FROM households;"));
        Assert.Equal(2, Scalar(path, "SELECT COUNT(DISTINCT household_id) FROM accounts;"));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM accounts WHERE household_id IS NULL;"));
        Assert.Equal(1, Scalar(path, """
            SELECT COUNT(*) FROM accounts a JOIN profiles p ON p.household_id = a.household_id
            WHERE a.id = @a AND p.id = @p;
            """, ("@a", FirstAccount), ("@p", FirstProfile)));
        Assert.Equal(1, Scalar(path, """
            SELECT COUNT(*) FROM accounts a JOIN profiles p ON p.household_id = a.household_id
            WHERE a.id = @a AND p.id = @p;
            """, ("@a", SecondAccount), ("@p", SecondProfile)));
        Assert.Equal(1, Scalar(path, """
            SELECT COUNT(*) FROM households h JOIN accounts a ON a.household_id = h.id
            WHERE a.id = @a AND h.name = 'Alex''s household';
            """, ("@a", FirstAccount)));
    }

    [Fact]
    public void AProfileSharedByTwoAccountsJoinsTheHouseholdWhereItIsTheDefault()
    {
        var path = CreateLegacyDatabase(raw =>
        {
            SeedAccount(raw, FirstAccount, "first@example.com", "2026-01-01T00:00:00.0000000+00:00");
            SeedAccount(raw, SecondAccount, "second@example.com", "2026-01-02T00:00:00.0000000+00:00");
            SeedProfile(raw, FirstProfile, "Alex");
            SeedProfile(raw, SecondProfile, "Sam");
            SeedProfile(raw, SharedProfile, "Shared");
            SeedGrant(raw, FirstAccount, FirstProfile, isDefault: true);
            SeedGrant(raw, SecondAccount, SecondProfile, isDefault: false);
            SeedGrant(raw, FirstAccount, SharedProfile, isDefault: false);
            SeedGrant(raw, SecondAccount, SharedProfile, isDefault: true);
        });

        using (var database = Open(path))
        {
            Assert.Single(database.StartupNotes, note => note.Contains("was granted to", StringComparison.Ordinal));
            Assert.Single(database.StartupNotes, note => note.StartsWith("Removed", StringComparison.Ordinal));
        }

        Assert.Equal(1, Scalar(path, """
            SELECT COUNT(*) FROM profiles p JOIN accounts a ON a.household_id = p.household_id
            WHERE p.id = @p AND a.id = @a;
            """, ("@p", SharedProfile), ("@a", SecondAccount)));

        // The other account lost its grant to the profile, so no grant crosses households.
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM account_profile_grants WHERE account_id = @a AND profile_id = @p;",
            ("@a", FirstAccount), ("@p", SharedProfile)));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM account_profile_grants WHERE account_id = @a AND profile_id = @p;",
            ("@a", SecondAccount), ("@p", SharedProfile)));
    }

    [Fact]
    public void AProfileNobodyHoldsJoinsTheAdministratorsHousehold()
    {
        var path = CreateLegacyDatabase(raw =>
        {
            SeedAccount(raw, FirstAccount, "first@example.com", "2026-01-01T00:00:00.0000000+00:00");
            Exec(raw, "UPDATE accounts SET is_administrator = 1 WHERE id = @a;", ("@a", FirstAccount));
            SeedProfile(raw, FirstProfile, "Alex");
            SeedProfile(raw, SharedProfile, "Loose");
            SeedGrant(raw, FirstAccount, FirstProfile, isDefault: true);
        });

        using (var database = Open(path))
        {
            Assert.Single(database.StartupNotes, note => note.Contains("'Loose'", StringComparison.Ordinal));
        }

        Assert.Equal(1, Scalar(path, """
            SELECT COUNT(*) FROM profiles p JOIN accounts a ON a.household_id = p.household_id
            WHERE p.id = @p AND a.id = @a;
            """, ("@p", SharedProfile), ("@a", FirstAccount)));
    }

    [Fact]
    public void WithoutADefaultGrantTheOldestAccountWins()
    {
        var path = CreateLegacyDatabase(raw =>
        {
            SeedAccount(raw, FirstAccount, "first@example.com", "2026-01-01T00:00:00.0000000+00:00");
            SeedAccount(raw, SecondAccount, "second@example.com", "2026-01-02T00:00:00.0000000+00:00");
            SeedProfile(raw, SharedProfile, "Shared");
            SeedGrant(raw, SecondAccount, SharedProfile, isDefault: false);
            SeedGrant(raw, FirstAccount, SharedProfile, isDefault: false);
        });

        using (Open(path))
        {
        }

        Assert.Equal(1, Scalar(path, """
            SELECT COUNT(*) FROM profiles p JOIN accounts a ON a.household_id = p.household_id
            WHERE p.id = @p AND a.id = @a;
            """, ("@p", SharedProfile), ("@a", FirstAccount)));
    }

    [Fact]
    public void RunningTheBackfillAgainChangesNothing()
    {
        var path = CreateLegacyDatabase(raw =>
        {
            SeedAccount(raw, FirstAccount, "first@example.com", "2026-01-01T00:00:00.0000000+00:00");
            SeedProfile(raw, FirstProfile, "Alex");
            SeedGrant(raw, FirstAccount, FirstProfile, isDefault: true);
        });
        using (Open(path))
        {
        }

        var households = Scalar(path, "SELECT COUNT(*) FROM households;");
        var idBefore = Text(path, "SELECT lower(hex(household_id)) FROM accounts WHERE id = @a;", ("@a", FirstAccount));

        using (var again = Open(path))
        {
            Assert.Empty(again.StartupNotes);
        }

        Assert.Equal(households, Scalar(path, "SELECT COUNT(*) FROM households;"));
        Assert.Equal(idBefore, Text(path, "SELECT lower(hex(household_id)) FROM accounts WHERE id = @a;", ("@a", FirstAccount)));
    }

    [Fact]
    public void AFreshDataStoreHasNoHouseholdsUntilSetupCreatesOne()
    {
        var path = NewPath();
        using (var database = Open(path))
        {
            Assert.Empty(database.StartupNotes);
        }

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
    public void HouseholdAdministratorBackfill_MakesEachHouseholdsFirstSignInItsAdministrator_OnlyOnce()
    {
        var path = CreateLegacyDatabase(raw =>
        {
            SeedAccount(raw, FirstAccount, "first@example.com", "2026-01-01T00:00:00.0000000+00:00");
            SeedAccount(raw, SecondAccount, "second@example.com", "2026-01-02T00:00:00.0000000+00:00");
            SeedProfile(raw, FirstProfile, "Alex");
            SeedProfile(raw, SecondProfile, "Sam");
            SeedGrant(raw, FirstAccount, FirstProfile, isDefault: true);
            SeedGrant(raw, SecondAccount, SecondProfile, isDefault: true);
        });

        Open(path).Dispose();
        SqliteConnection.ClearAllPools();

        foreach (var account in new[] { FirstAccount, SecondAccount })
        {
            Assert.Equal(1, Scalar(path, "SELECT household_admin FROM accounts WHERE id=@a;", ("@a", account)));
            Assert.Equal(1, Scalar(path, """
                SELECT COUNT(*) FROM households h JOIN accounts a ON a.household_id = h.id
                WHERE a.id = @a AND h.primary_account_id = a.id;
                """, ("@a", account)));
        }

        // An administrator who later removed the flag does not get it back on the next start.
        using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            raw.Open();
            Exec(raw, "UPDATE accounts SET household_admin = 0 WHERE id = @a;", ("@a", SecondAccount));
        }

        Open(path).Dispose();
        SqliteConnection.ClearAllPools();
        Assert.Equal(0, Scalar(path, "SELECT household_admin FROM accounts WHERE id=@a;", ("@a", SecondAccount)));
        Assert.Equal(1, Scalar(path, "SELECT household_admin FROM accounts WHERE id=@a;", ("@a", FirstAccount)));
    }

    [Fact]
    public void ServerAdministratorsBecomeAdministratorsOfTheirOwnHousehold_OnUpgrade()
    {
        var path = CreateLegacyDatabase(raw =>
        {
            SeedAccount(raw, FirstAccount, "first@example.com", "2026-01-01T00:00:00.0000000+00:00");
            SeedAccount(raw, SecondAccount, "second@example.com", "2026-01-02T00:00:00.0000000+00:00");
            Exec(raw, "UPDATE accounts SET is_administrator = 1 WHERE id = @a;", ("@a", SecondAccount));
            SeedProfile(raw, FirstProfile, "Alex");
            SeedProfile(raw, SecondProfile, "Sam");
            SeedGrant(raw, FirstAccount, FirstProfile, isDefault: true);
            SeedGrant(raw, SecondAccount, SecondProfile, isDefault: true);
        });

        Open(path).Dispose();
        SqliteConnection.ClearAllPools();

        // The server administrator never loses (and now also holds) household administration for their own household.
        Assert.Equal(1, Scalar(path, "SELECT household_admin FROM accounts WHERE id=@a AND is_administrator=1;", ("@a", SecondAccount)));
    }

    [Fact]
    public void TheSingleSharedLibrary_MovesToTheServerAdministratorsHousehold_AndKeepsItsIdentity()
    {
        var library = Guid.NewGuid();
        var path = CreateLegacyDatabase(raw =>
        {
            SeedAccount(raw, FirstAccount, "first@example.com", "2026-01-01T00:00:00.0000000+00:00");
            SeedAccount(raw, SecondAccount, "second@example.com", "2026-01-02T00:00:00.0000000+00:00");
            Exec(raw, "UPDATE accounts SET is_administrator = 1 WHERE id = @a;", ("@a", SecondAccount));
            SeedProfile(raw, FirstProfile, "Alex");
            SeedProfile(raw, SecondProfile, "Sam");
            SeedGrant(raw, FirstAccount, FirstProfile, isDefault: true);
            SeedGrant(raw, SecondAccount, SecondProfile, isDefault: true);

            // The old shape: one row for the whole server.
            Exec(raw, """
                DROP TRIGGER IF EXISTS trg_view_shared_library_collision_insert;
                DROP TRIGGER IF EXISTS trg_view_shared_library_identity_immutable;
                DROP TRIGGER IF EXISTS trg_view_shared_library_delete;
                DROP TABLE view_shared_library;
                CREATE TABLE view_shared_library (
                    singleton_key INTEGER NOT NULL PRIMARY KEY CHECK (singleton_key = 1),
                    library_id BLOB NOT NULL UNIQUE,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                INSERT INTO view_shared_library (singleton_key, library_id, created_at, updated_at)
                VALUES (1, @library, '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');
                """, ("@library", library));
        });

        Open(path).Dispose();
        SqliteConnection.ClearAllPools();

        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM view_shared_library;"));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM pragma_table_info('view_shared_library') WHERE name = 'singleton_key';"));
        Assert.Equal(1, Scalar(path, """
            SELECT COUNT(*) FROM view_shared_library v JOIN accounts a ON a.household_id = v.household_id
            WHERE a.id = @a AND v.library_id = @library;
            """, ("@a", SecondAccount), ("@library", library)));

        // A second start leaves it alone.
        Open(path).Dispose();
        SqliteConnection.ClearAllPools();
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM view_shared_library;"));
    }

    [Fact]
    public void TheSingleSharedLibrary_WithNoHouseholdYet_GetsOneMadeForIt_AndNothingSharedIsOrphaned()
    {
        var library = Guid.NewGuid();
        var path = CreateLegacyDatabase(raw => Exec(raw, """
            DROP TRIGGER IF EXISTS trg_view_shared_library_collision_insert;
            DROP TRIGGER IF EXISTS trg_view_shared_library_identity_immutable;
            DROP TRIGGER IF EXISTS trg_view_shared_library_delete;
            DROP TABLE view_shared_library;
            CREATE TABLE view_shared_library (
                singleton_key INTEGER NOT NULL PRIMARY KEY CHECK (singleton_key = 1),
                library_id BLOB NOT NULL UNIQUE,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            INSERT INTO view_shared_library (singleton_key, library_id, created_at, updated_at)
            VALUES (1, @library, '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');
            """, ("@library", library)));

        // Startup must not crash, and must keep the library identity for anything already shared.
        Open(path).Dispose();
        SqliteConnection.ClearAllPools();
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM households;"));
        Assert.Equal(1, Scalar(path, """
            SELECT COUNT(*) FROM view_shared_library v JOIN households h ON h.id = v.household_id
            WHERE v.library_id = @library;
            """, ("@library", library)));

        Open(path).Dispose();
        SqliteConnection.ClearAllPools();
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM households;"));
    }

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
            SqliteConnection.ClearAllPools();
            throw;
        }
    }

    /// <summary>
    /// Builds a current data store, then puts <c>accounts</c> and <c>profiles</c> back into their shape from before
    /// households (no <c>household_id</c>) and seeds them, so the upgrade has real work to do.
    /// </summary>
    private string CreateLegacyDatabase(Action<SqliteConnection> seed)
    {
        var path = NewPath();
        using (var database = new DatabaseConnection(path))
        {
            database.InitializeSchema();
        }

        SqliteConnection.ClearAllPools();
        using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            raw.Open();
            Exec(raw, "PRAGMA foreign_keys = OFF;");
            Exec(raw, """
                CREATE TABLE accounts_previous (
                    id               BLOB NOT NULL PRIMARY KEY,
                    email            TEXT NOT NULL,
                    normalized_email TEXT NOT NULL,
                    is_enabled       INTEGER NOT NULL DEFAULT 1 CHECK (is_enabled IN (0, 1)),
                    is_administrator INTEGER NOT NULL DEFAULT 0 CHECK (is_administrator IN (0, 1)),
                    authorization_version INTEGER NOT NULL DEFAULT 1 CHECK (authorization_version > 0),
                    created_at       TEXT NOT NULL,
                    updated_at       TEXT NOT NULL
                );
                DROP TABLE accounts;
                ALTER TABLE accounts_previous RENAME TO accounts;
                CREATE UNIQUE INDEX ux_accounts_normalized_email ON accounts(normalized_email);

                CREATE TABLE profiles_previous (
                    id           BLOB NOT NULL PRIMARY KEY,
                    display_name TEXT NOT NULL,
                    avatar_color TEXT NOT NULL DEFAULT '#7C4DFF',
                    avatar_image_path TEXT,
                    role         TEXT NOT NULL DEFAULT 'RestrictedProfile'
                                     CHECK (role IN ('Administrator', 'StandardUser', 'RestrictedProfile')),
                    created_at   TEXT NOT NULL,
                    navigation_config TEXT
                );
                DROP TABLE profiles;
                ALTER TABLE profiles_previous RENAME TO profiles;
                """);
            Exec(raw, "PRAGMA foreign_keys = ON;");
            seed(raw);
        }

        SqliteConnection.ClearAllPools();
        return path;
    }

    private static void SeedAccount(SqliteConnection raw, Guid id, string email, string createdAt) => Exec(raw, """
        INSERT INTO accounts(id, email, normalized_email, is_enabled, is_administrator, authorization_version, created_at, updated_at)
        VALUES(@id, @email, @normalized, 1, 0, 1, @created, @created);
        """, ("@id", id), ("@email", email), ("@normalized", email.ToUpperInvariant()), ("@created", createdAt));

    private static void SeedProfile(SqliteConnection raw, Guid id, string name) => Exec(raw,
        "INSERT INTO profiles(id, display_name, role, created_at) VALUES(@id, @name, 'StandardUser', @now);",
        ("@id", id), ("@name", name), ("@now", DateTimeOffset.UtcNow.ToString("O")));

    private static void SeedGrant(SqliteConnection raw, Guid account, Guid profile, bool isDefault) => Exec(raw,
        "INSERT INTO account_profile_grants(account_id, profile_id, is_default, is_enabled, admin_enabled, authorization_version, granted_at) VALUES(@a, @p, @d, 1, 0, 1, @now);",
        ("@a", account), ("@p", profile), ("@d", isDefault ? 1 : 0), ("@now", DateTimeOffset.UtcNow.ToString("O")));

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

    private static string Text(string path, string sql, params (string Name, object Value)[] parameters) =>
        Convert.ToString(Query(path, sql, parameters), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

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
        SqliteConnection.ClearAllPools();
        foreach (var path in _paths)
        {
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
