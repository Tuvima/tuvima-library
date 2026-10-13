using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Identity.Tests;

/// <summary>Linking an outside sign-in makes an account usable away from home, so an account with no email is given one.</summary>
public sealed class ExternalLoginEmailTests : IDisposable
{
    private const string Issuer = "https://accounts.example.test";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_external_email_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly FirstPartyIdentityService _identity;
    private readonly AccountExternalLoginService _links;

    public ExternalLoginEmailTests()
    {
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        _identity = new FirstPartyIdentityService(
            new IdentityRepository(_database), _accounts, new ProfileRepository(_database),
            new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(),
            TimeProvider.System, new FixedPolicy());
        _links = new AccountExternalLoginService(new AccountExternalLoginRepository(_database), _accounts);
    }

    [Fact]
    public async Task ProviderEmail_IsAdoptedByAnAccountWithNone()
    {
        var accountId = await NewAccountWithoutEmailAsync();

        await _links.LinkAsync(accountId, "Google", Issuer, "subject-1", "Person@Example.com", "Person");

        var account = (await _accounts.GetByIdAsync(accountId))!;
        Assert.Equal("Person@Example.com", account.Email);
        Assert.Equal("PERSON@EXAMPLE.COM", account.NormalizedEmail);
    }

    [Fact]
    public async Task WhenTheProviderEmailIsTaken_TheTypedEmailIsUsed()
    {
        await _identity.BootstrapAdministratorAsync("taken@example.com", "correct horse battery staple", "Admin", "d", "Browser", "Dashboard");
        var accountId = await NewAccountWithoutEmailAsync();

        await _links.LinkAsync(accountId, "Google", Issuer, "subject-2", "taken@example.com", null, accountEmail: "typed@example.com");

        Assert.Equal("TYPED@EXAMPLE.COM", (await _accounts.GetByIdAsync(accountId))!.NormalizedEmail);
    }

    [Fact]
    public async Task WithNoUsableEmailAtAll_LinkingIsRefusedAndNothingIsLinked()
    {
        var accountId = await NewAccountWithoutEmailAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _links.LinkAsync(accountId, "Google", Issuer, "subject-3", null, null));

        Assert.Null((await _accounts.GetByIdAsync(accountId))!.Email);
        Assert.Empty(await _links.GetByAccountAsync(accountId));
    }

    [Fact]
    public async Task ATypedEmailAnotherAccountUses_IsRefused()
    {
        await _identity.BootstrapAdministratorAsync("taken@example.com", "correct horse battery staple", "Admin", "d", "Browser", "Dashboard");
        var accountId = await NewAccountWithoutEmailAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _links.LinkAsync(accountId, "Google", Issuer, "subject-4", null, null, accountEmail: "TAKEN@example.com"));
    }

    [Fact]
    public async Task AnAccountThatHasAnEmailKeepsIt()
    {
        var issued = await _identity.BootstrapAdministratorAsync("owner@example.com", "correct horse battery staple", "Owner", "d", "Browser", "Dashboard");

        await _links.LinkAsync(issued.Account.Id, "Google", Issuer, "subject-5", "other@example.com", null);

        Assert.Equal("owner@example.com", (await _accounts.GetByIdAsync(issued.Account.Id))!.Email);
    }

    private async Task<Guid> NewAccountWithoutEmailAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        await _accounts.CreateAccountAsync(
            new Account { Id = id, IsEnabled = true, AuthorizationVersion = 1, CreatedAt = now, UpdatedAt = now },
            new AccountProfileGrant { AccountId = id, ProfileId = MediaEngine.Domain.Aggregates.Profile.SeedProfileId, IsDefault = true, IsEnabled = true, AuthorizationVersion = 1, GrantedAt = now },
            new HashSet<AccountFeatureId>(), new HashSet<Guid>());
        return id;
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
    }

    private sealed class FixedPolicy : IAuthenticationPolicyProvider
    {
        public AuthSettings GetCurrent() => new();
    }
}
