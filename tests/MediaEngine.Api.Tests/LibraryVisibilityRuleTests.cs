using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Api.Tests;

// The one rule that answers "may this profile see this library" (simpler access D11, access held by the profile), on real temp SQLite.
public sealed class LibraryVisibilityRuleTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_library_visibility_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly LibraryVisibilityRule _rule;
    private readonly ProfileAccessRepository _profileAccess;
    private readonly DateTimeOffset _now = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    public LibraryVisibilityRuleTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        _profileAccess = new ProfileAccessRepository(_database);
        _rule = TestLibraryVisibility.Rule(_profileAccess, new VaultLibraryRepository(_database));
    }

    [Theory]
    [InlineData(false, false, true, false, true)]   // a person with the area, nothing hidden
    [InlineData(false, false, true, true, false)]   // hidden from them
    [InlineData(false, false, false, false, false)] // no area, nothing hidden
    [InlineData(false, true, true, false, false)]   // a Vault library is never visible through this rule
    [InlineData(true, false, false, true, true)]    // an administrator sees everything that is not Vault
    [InlineData(true, true, true, false, false)]    // not even an administrator, the Vault has its own rule
    public void MaySee_FollowsTheOneRule(bool administrator, bool vault, bool hasArea, bool hidden, bool expected) =>
        Assert.Equal(expected, LibraryVisibilityRule.MaySee(administrator, vault, hasArea, hidden));

    [Fact]
    public async Task ANewLibrary_IsVisibleToAnAccountWithItsArea_WithNoRowWritten()
    {
        var profile = await CreateProfileAsync("area@example.test", new HashSet<Guid>());

        Assert.True(await _rule.MaySeeAsync(profile, false, Guid.NewGuid(), true));
    }

    [Fact]
    public async Task ALibraryHiddenFromTheAccount_IsNotVisible_ButIsToAnotherAccount()
    {
        var hidden = Guid.NewGuid();
        var sam = await CreateProfileAsync("sam@example.test", new HashSet<Guid> { hidden });
        var other = await CreateProfileAsync("other@example.test", new HashSet<Guid>());

        Assert.False(await _rule.MaySeeAsync(sam, false, hidden, true));
        Assert.True(await _rule.MaySeeAsync(other, false, hidden, true));
    }

    [Fact]
    public async Task WithoutTheArea_TheLibraryIsNotVisible_EvenWhenNothingIsHidden()
    {
        var profile = await CreateProfileAsync("noarea@example.test", new HashSet<Guid>());

        Assert.False(await _rule.MaySeeAsync(profile, false, Guid.NewGuid(), false));
    }

    [Fact]
    public async Task AServerAdministrator_SeesALibraryEvenWhenAHiddenRowExists()
    {
        var hidden = Guid.NewGuid();
        var administrator = await CreateProfileAsync("admin@example.test", new HashSet<Guid> { hidden });

        Assert.True(await _rule.MaySeeAsync(administrator, true, hidden, false));
    }

    [Fact]
    public async Task AVaultLibrary_IsNeverVisibleThroughTheRule_NotEvenToAnAdministrator()
    {
        var vault = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO vault_libraries(library_id, lookup_source, adults_confirmed_at, created_at, updated_at) VALUES(@vault,'none',@now,@now,@now);",
                new { vault = vault.ToString("D"), now = _now.ToString("O") });
        }

        var person = await CreateProfileAsync("vault@example.test", new HashSet<Guid>());

        Assert.False(await _rule.MaySeeAsync(person, false, vault, true));
        Assert.False(await _rule.MaySeeAsync(person, true, vault, true));
    }

    [Fact]
    public async Task AnEmptyLibraryId_IsNeverVisible()
    {
        var profile = await CreateProfileAsync("empty@example.test", new HashSet<Guid>());

        Assert.False(await _rule.MaySeeAsync(profile, true, Guid.Empty, true));
    }

    [Fact]
    public async Task LoadHidden_ReturnsOnlyTheProfilesOwnLibraries()
    {
        var byFirst = Guid.NewGuid();
        var first = await CreateProfileAsync("first@example.test", new HashSet<Guid> { byFirst });
        var second = await CreateProfileAsync("second@example.test", new HashSet<Guid>());

        Assert.Equal(new HashSet<Guid> { byFirst }, (await _rule.LoadHiddenAsync(first)).ToHashSet());
        Assert.Empty(await _rule.LoadHiddenAsync(second));
    }

    private LibraryVisibilityRule HouseholdAwareRule() =>
        new(_profileAccess, new HouseholdLibraryAccessRepository(_database), new VaultLibraryRepository(_database));

    private async Task<(Guid Household, Guid First, Guid Second)> CreateHouseholdOfTwoAsync(string name)
    {
        var households = new HouseholdRepository(_database);
        var household = await households.CreateAsync(name, _now);
        var first = await CreateProfileInAsync(household.Id, $"{name} first");
        var second = await CreateProfileInAsync(household.Id, $"{name} second");
        return (household.Id, first, second);
    }

    private async Task<Guid> CreateProfileInAsync(Guid householdId, string name)
    {
        var profile = new MediaEngine.Domain.Aggregates.Profile
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            Role = ProfileRole.StandardUser,
            CreatedAt = _now,
        };
        await _accounts.CreateHouseholdPersonAsync(profile, householdId);
        return profile.Id;
    }

    [Fact]
    public async Task ALibraryHiddenForAHousehold_IsAbsentForEveryProfileInIt_AndPresentForAnotherHousehold()
    {
        var library = Guid.NewGuid();
        var (householdTwo, firstOfTwo, secondOfTwo) = await CreateHouseholdOfTwoAsync("Household 2");
        var (_, firstOfOne, _) = await CreateHouseholdOfTwoAsync("Household 1");
        var rule = HouseholdAwareRule();
        await new HouseholdLibraryAccessRepository(_database).ReplaceHiddenLibrariesAsync(householdTwo, new HashSet<Guid> { library }, _now, null);

        Assert.False(await rule.MaySeeAsync(firstOfTwo, false, library, true));
        Assert.False(await rule.MaySeeAsync(secondOfTwo, false, library, true));
        Assert.True(await rule.IsHiddenAsync(firstOfTwo, library));
        Assert.True(await rule.MaySeeAsync(firstOfOne, false, library, true));
        Assert.False(await rule.IsHiddenAsync(firstOfOne, library));
    }

    [Fact]
    public async Task AServerAdministrator_SeesALibraryHiddenForTheirHousehold()
    {
        var library = Guid.NewGuid();
        var (household, first, _) = await CreateHouseholdOfTwoAsync("Admins");
        var rule = HouseholdAwareRule();
        await new HouseholdLibraryAccessRepository(_database).ReplaceHiddenLibrariesAsync(household, new HashSet<Guid> { library }, _now, null);

        Assert.True(await rule.MaySeeAsync(first, true, library, false));
    }

    [Fact]
    public async Task AProfileHiddenFromOneLibrary_KeepsTheRest_WhenItsHouseholdHidesAnother()
    {
        var householdHidden = Guid.NewGuid();
        var profileHidden = Guid.NewGuid();
        var untouched = Guid.NewGuid();
        var (household, first, second) = await CreateHouseholdOfTwoAsync("Mixed");
        var rule = HouseholdAwareRule();
        await new HouseholdLibraryAccessRepository(_database).ReplaceHiddenLibrariesAsync(household, new HashSet<Guid> { householdHidden }, _now, null);
        await _profileAccess.ReplaceAccessAsync(first, new HashSet<AccountFeatureId> { AccountFeatureId.Watch }, new HashSet<Guid> { profileHidden }, _now);

        Assert.False(await rule.MaySeeAsync(first, false, householdHidden, true));
        Assert.False(await rule.MaySeeAsync(first, false, profileHidden, true));
        Assert.True(await rule.MaySeeAsync(first, false, untouched, true));
        Assert.True(await rule.MaySeeAsync(second, false, profileHidden, true));
        Assert.False(await rule.MaySeeAsync(second, false, householdHidden, true));
    }

    [Fact]
    public async Task LoadHidden_ListsTheHouseholdsHiddenLibraries_NextToTheProfilesOwn()
    {
        var byHousehold = Guid.NewGuid();
        var byProfile = Guid.NewGuid();
        var (household, first, _) = await CreateHouseholdOfTwoAsync("Loader");
        var rule = HouseholdAwareRule();
        await new HouseholdLibraryAccessRepository(_database).ReplaceHiddenLibrariesAsync(household, new HashSet<Guid> { byHousehold }, _now, null);
        await _profileAccess.ReplaceAccessAsync(first, new HashSet<AccountFeatureId> { AccountFeatureId.Watch }, new HashSet<Guid> { byProfile }, _now);

        Assert.Equal(new HashSet<Guid> { byHousehold, byProfile }, (await rule.LoadHiddenAsync(first)).ToHashSet());
    }

    [Fact]
    public async Task ANewProfileInAHousehold_GetsWhatTheHouseholdHas_WithNothingHiddenForItself()
    {
        var hiddenForHousehold = Guid.NewGuid();
        var other = Guid.NewGuid();
        var (household, _, _) = await CreateHouseholdOfTwoAsync("Newcomers");
        var rule = HouseholdAwareRule();
        await new HouseholdLibraryAccessRepository(_database).ReplaceHiddenLibrariesAsync(household, new HashSet<Guid> { hiddenForHousehold }, _now, null);

        var later = await CreateProfileInAsync(household, "later");

        Assert.Empty(await _profileAccess.GetHiddenLibrariesAsync(later));
        Assert.False(await rule.MaySeeAsync(later, false, hiddenForHousehold, true));
        Assert.True(await rule.MaySeeAsync(later, false, other, true));
    }

    [Fact]
    public async Task AVaultLibrary_IsRefusedInAHouseholdList_AndNeverVisibleThroughTheRule()
    {
        var vault = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO vault_libraries(library_id, lookup_source, adults_confirmed_at, created_at, updated_at) VALUES(@vault,'none',@now,@now,@now);",
                new { vault = vault.ToString("D"), now = _now.ToString("O") });
        }

        var (household, first, _) = await CreateHouseholdOfTwoAsync("Vaulty");
        var rule = HouseholdAwareRule();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new HouseholdLibraryAccessRepository(_database).ReplaceHiddenLibrariesAsync(household, new HashSet<Guid> { vault }, _now, null));
        Assert.False(await rule.MaySeeAsync(first, false, vault, true));
        Assert.False(await rule.MaySeeAsync(first, true, vault, true));
    }

    private async Task<Guid> CreateProfileAsync(string email, IReadOnlySet<Guid> hidden)
    {
        var profile = new MediaEngine.Domain.Aggregates.Profile
        {
            Id = Guid.NewGuid(),
            DisplayName = email,
            Role = ProfileRole.StandardUser,
            CreatedAt = _now,
        };
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
        await _accounts.CreateAccountAsync(
            account,
            new TestGrant(account.Id, profile.Id),
            new HashSet<AccountFeatureId> { AccountFeatureId.Watch },
            hidden,
            newProfile: profile,
            newHouseholdName: $"{email} home");
        return profile.Id;
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
    }
}
