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

    private (LibraryVisibilityRule Rule, LibraryProfileGrantRepository Grants, FixedLibraryAccessCatalog Catalog) SelectedAwareRule(params Guid[] selected)
    {
        var grants = new LibraryProfileGrantRepository(_database);
        var catalog = new FixedLibraryAccessCatalog(selected);
        return (new LibraryVisibilityRule(_profileAccess, new VaultLibraryRepository(_database), grants, catalog), grants, catalog);
    }

    [Fact]
    public async Task ASelectedLibrary_IsVisibleOnlyToTheProfilesChosen_AndToAdministrators()
    {
        var library = Guid.NewGuid();
        var chosen = await CreateProfileAsync("chosen@example.test", new HashSet<Guid>());
        var notChosen = await CreateProfileAsync("notchosen@example.test", new HashSet<Guid>());
        var (rule, grants, _) = SelectedAwareRule(library);
        await grants.ReplaceGrantedProfilesAsync(library, new HashSet<Guid> { chosen }, _now, null);

        Assert.True(await rule.MaySeeAsync(chosen, false, library, true));
        Assert.False(await rule.MaySeeAsync(notChosen, false, library, true));
        Assert.True(await rule.MaySeeAsync(notChosen, true, library, true));
        Assert.True(await rule.IsHiddenAsync(notChosen, library));
        Assert.False(await rule.IsHiddenAsync(chosen, library));
    }

    [Fact]
    public async Task ANewProfile_HasNoAccessToASelectedLibrary_AndFullAccessToAStandardOne()
    {
        var selected = Guid.NewGuid();
        var standard = Guid.NewGuid();
        var earlier = await CreateProfileAsync("earlier@example.test", new HashSet<Guid>());
        var (rule, grants, _) = SelectedAwareRule(selected);
        await grants.ReplaceGrantedProfilesAsync(selected, new HashSet<Guid> { earlier }, _now, null);

        var later = await CreateProfileAsync("later@example.test", new HashSet<Guid>());

        Assert.False(await rule.MaySeeAsync(later, false, selected, true));
        Assert.True(await rule.MaySeeAsync(later, false, standard, true));
        Assert.Empty(await grants.GetGrantedLibrariesAsync(later));
    }

    [Fact]
    public async Task LoadHidden_ListsEverySelectedLibraryTheProfileWasNotChosenFor_NextToItsOwnHiddenOnes()
    {
        var hiddenByAdmin = Guid.NewGuid();
        var chosenFor = Guid.NewGuid();
        var notChosenFor = Guid.NewGuid();
        var profile = await CreateProfileAsync("loader@example.test", new HashSet<Guid> { hiddenByAdmin });
        var (rule, grants, _) = SelectedAwareRule(chosenFor, notChosenFor);
        await grants.ReplaceGrantedProfilesAsync(chosenFor, new HashSet<Guid> { profile }, _now, null);

        Assert.Equal(new HashSet<Guid> { hiddenByAdmin, notChosenFor }, (await rule.LoadHiddenAsync(profile)).ToHashSet());
    }

    [Fact]
    public async Task ASelectedLibrary_StillNeedsTheArea_AndStillHonoursAHiddenRow_EvenWhenChosen()
    {
        var library = Guid.NewGuid();
        var profile = await CreateProfileAsync("limits@example.test", new HashSet<Guid> { library });
        var (rule, grants, _) = SelectedAwareRule(library);
        await grants.ReplaceGrantedProfilesAsync(library, new HashSet<Guid> { profile }, _now, null);

        Assert.False(await rule.MaySeeAsync(profile, false, library, true));
        Assert.False(await rule.MaySeeAsync(profile, false, library, false));
    }

    [Fact]
    public async Task AVaultLibrary_IsNeverAcceptedAsAGrant_AndNeverVisibleThroughASelectedChoice()
    {
        var vault = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO vault_libraries(library_id, lookup_source, adults_confirmed_at, created_at, updated_at) VALUES(@vault,'none',@now,@now,@now);",
                new { vault = vault.ToString("D"), now = _now.ToString("O") });
        }

        var profile = await CreateProfileAsync("vaultgrant@example.test", new HashSet<Guid>());
        var (rule, grants, _) = SelectedAwareRule(vault);

        await Assert.ThrowsAsync<ArgumentException>(() => grants.ReplaceGrantedProfilesAsync(vault, new HashSet<Guid> { profile }, _now, null));
        Assert.False(await rule.MaySeeAsync(profile, false, vault, true));
        Assert.False(await rule.MaySeeAsync(profile, true, vault, true));
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
