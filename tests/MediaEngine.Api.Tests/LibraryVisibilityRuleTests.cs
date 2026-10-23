using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Api.Tests;

// The one rule that answers "may this account see this library" (simpler access D11), on real temp SQLite.
public sealed class LibraryVisibilityRuleTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_library_visibility_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly LibraryVisibilityRule _rule;
    private readonly DateTimeOffset _now = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    public LibraryVisibilityRuleTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        _rule = new LibraryVisibilityRule(_accounts, new VaultLibraryRepository(_database));
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
        var account = await CreateAccountAsync("area@example.test", new HashSet<Guid>());

        Assert.True(await _rule.MaySeeAsync(account.Id, false, Guid.NewGuid(), true));
    }

    [Fact]
    public async Task ALibraryHiddenFromTheAccount_IsNotVisible_ButIsToAnotherAccount()
    {
        var hidden = Guid.NewGuid();
        var sam = await CreateAccountAsync("sam@example.test", new HashSet<Guid> { hidden });
        var other = await CreateAccountAsync("other@example.test", new HashSet<Guid>());

        Assert.False(await _rule.MaySeeAsync(sam.Id, false, hidden, true));
        Assert.True(await _rule.MaySeeAsync(other.Id, false, hidden, true));
    }

    [Fact]
    public async Task WithoutTheArea_TheLibraryIsNotVisible_EvenWhenNothingIsHidden()
    {
        var account = await CreateAccountAsync("noarea@example.test", new HashSet<Guid>());

        Assert.False(await _rule.MaySeeAsync(account.Id, false, Guid.NewGuid(), false));
    }

    [Fact]
    public async Task AnAdministrator_SeesALibraryEvenWhenAHiddenRowExists()
    {
        var hidden = Guid.NewGuid();
        var administrator = await CreateAccountAsync("admin@example.test", new HashSet<Guid> { hidden });

        Assert.True(await _rule.MaySeeAsync(administrator.Id, true, hidden, false));
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

        var person = await CreateAccountAsync("vault@example.test", new HashSet<Guid>());

        Assert.False(await _rule.MaySeeAsync(person.Id, false, vault, true));
        Assert.False(await _rule.MaySeeAsync(person.Id, true, vault, true));
    }

    [Fact]
    public async Task AnEmptyLibraryId_IsNeverVisible()
    {
        var account = await CreateAccountAsync("empty@example.test", new HashSet<Guid>());

        Assert.False(await _rule.MaySeeAsync(account.Id, true, Guid.Empty, true));
    }

    [Fact]
    public async Task LoadHidden_ReturnsTheAccountsOwnAndTheInheritedLibraries()
    {
        var byPrimary = Guid.NewGuid();
        var byMember = Guid.NewGuid();
        var primary = await CreateAccountAsync("primary@example.test", new HashSet<Guid> { byPrimary });
        var member = await CreateAccountAsync("member@example.test", new HashSet<Guid> { byMember });
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE accounts SET grants_inherit_from_account_id=@primary WHERE id=@member;", new { primary = primary.Id, member = member.Id });
        }

        Assert.Equal(new HashSet<Guid> { byPrimary, byMember }, (await _rule.LoadHiddenAsync(member.Id)).ToHashSet());
    }

    private async Task<Account> CreateAccountAsync(string email, IReadOnlySet<Guid> hidden)
    {
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
            new AccountProfileGrant
            {
                AccountId = account.Id,
                ProfileId = Profile.SeedProfileId,
                IsDefault = true,
                IsEnabled = true,
                AuthorizationVersion = 1,
                GrantedAt = _now,
            },
            new HashSet<AccountFeatureId> { AccountFeatureId.Watch },
            hidden);
        return account;
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
    }
}
