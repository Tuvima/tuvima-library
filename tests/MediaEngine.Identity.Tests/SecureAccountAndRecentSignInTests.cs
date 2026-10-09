using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Identity.Tests;

/// <summary>"Secure this account" and the recent sign-in check that guards sensitive account actions.</summary>
public sealed class SecureAccountAndRecentSignInTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_secure_account_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly IdentityRepository _identities;
    private readonly AccountRepository _accounts;
    private readonly FirstPartyIdentityService _service;
    private readonly ClockStub _clock = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));

    public SecureAccountAndRecentSignInTests()
    {
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _identities = new IdentityRepository(_database);
        _accounts = new AccountRepository(_database);
        _service = new FirstPartyIdentityService(
            _identities,
            _accounts,
            new ProfileRepository(_database),
            new PasswordHasher<AccountCredential>(),
            new PasswordHasher<ProfileCredential>(),
            _clock,
            new FixedPolicy());
    }

    [Fact]
    public async Task Secure_KeepsTheAccountAndItsProfilesAndAccess_AndLiftsTheLimit()
    {
        var thisComputer = await _service.BootstrapThisComputerAdministratorAsync(
            "owner@example.com", "Owner", "device", "Browser", "Dashboard");
        var before = Assert.Single(await _accounts.GetAllAsync());
        var profileIds = await _accounts.GetProfileIdsAsync(before.Id);

        var secured = await _service.SecureThisComputerAccountAsync(before.Id, Password, hasPasskey: false, "device", "Browser", "Dashboard");

        var after = Assert.Single(await _accounts.GetAllAsync());
        Assert.Equal(before.Id, after.Id);
        Assert.False(after.IsThisComputerOnly);
        Assert.True(after.IsAdministrator);
        Assert.Equal(before.HouseholdId, after.HouseholdId);
        Assert.Equal(profileIds, await _accounts.GetProfileIdsAsync(after.Id));
        foreach (var profileId in profileIds)
        {
            Assert.True(await _accounts.HasProfileAccessAsync(after.Id, profileId));
        }

        Assert.NotNull(await _identities.GetAccountCredentialAsync(after.Id, AccountCredentialKind.Password));
        Assert.Equal(10, secured.RecoveryCodes.Count);
        Assert.Equal(ClientIngress.ThisComputer, secured.Session.IssuedIngress);
        Assert.Equal("Password", secured.Session.AuthenticationMethod);
        Assert.NotEqual(thisComputer.Session.Id, secured.Session.Id);
    }

    [Fact]
    public async Task Secure_EndsTheOldSessions_AndTheNewOneWorksAtHomeToo()
    {
        var thisComputer = await _service.BootstrapThisComputerAdministratorAsync(
            "owner@example.com", "Owner", "device", "Browser", "Dashboard");
        var other = (await _service.SignInThisComputerAccountAsync("device-2", "Other tab", "Dashboard"))!;
        var accountId = thisComputer.Account.Id;

        var secured = await _service.SecureThisComputerAccountAsync(accountId, Password, hasPasskey: false, "device", "Browser", "Dashboard");

        Assert.Null(await _service.ValidateSessionAsync(thisComputer.PlaintextToken, touch: false, currentIngress: ClientIngress.ThisComputer));
        Assert.Null(await _service.ValidateSessionAsync(other.PlaintextToken, touch: false, currentIngress: ClientIngress.ThisComputer));
        Assert.NotNull(await _service.ValidateSessionAsync(secured.PlaintextToken, touch: false, currentIngress: ClientIngress.ThisComputer));
        // A session made on this computer still works only where it started or closer, but the account itself is
        // now a normal one: it can sign in with its password from the home network.
        var home = await _service.AuthenticatePasswordAsync("owner@example.com", Password, "tv", "TV", "Dashboard", ingress: ClientIngress.HomeNetwork);
        Assert.True(home.Succeeded);
    }

    [Fact]
    public async Task Secure_WithAPasskeyOnly_ReturnsNoRecoveryCodes()
    {
        var issued = await _service.BootstrapThisComputerAdministratorAsync(
            "owner@example.com", "Owner", "device", "Browser", "Dashboard");

        var secured = await _service.SecureThisComputerAccountAsync(issued.Account.Id, null, hasPasskey: true, "device", "Browser", "Dashboard");

        Assert.Empty(secured.RecoveryCodes);
        Assert.Equal("Passkey", secured.Session.AuthenticationMethod);
        Assert.False((await _accounts.GetByIdAsync(issued.Account.Id))!.IsThisComputerOnly);
    }

    [Fact]
    public async Task Secure_RefusesWeakPasswordsAndLeavesTheAccountAsItWas()
    {
        var issued = await _service.BootstrapThisComputerAdministratorAsync(
            "owner@example.com", "Owner", "device", "Browser", "Dashboard");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.SecureThisComputerAccountAsync(issued.Account.Id, "short", hasPasskey: false, "device", "Browser", "Dashboard"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.SecureThisComputerAccountAsync(issued.Account.Id, "owner@example.com", hasPasskey: false, "device", "Browser", "Dashboard"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SecureThisComputerAccountAsync(issued.Account.Id, null, hasPasskey: false, "device", "Browser", "Dashboard"));

        Assert.True((await _accounts.GetByIdAsync(issued.Account.Id))!.IsThisComputerOnly);
        Assert.Null(await _identities.GetAccountCredentialAsync(issued.Account.Id, AccountCredentialKind.Password));
        Assert.NotNull(await _service.ValidateSessionAsync(issued.PlaintextToken, touch: false, currentIngress: ClientIngress.ThisComputer));
    }

    [Fact]
    public async Task Secure_RefusesAnAccountThatIsAlreadySecured()
    {
        var issued = await _service.BootstrapAdministratorAsync(
            "owner@example.com", Password, "Owner", "device", "Browser", "Dashboard");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SecureThisComputerAccountAsync(issued.Account.Id, "another secure password", hasPasskey: false, "device", "Browser", "Dashboard"));
    }

    [Fact]
    public async Task SignIn_IsRecentForTenMinutes_ThenAsksToConfirm()
    {
        var issued = await _service.BootstrapAdministratorAsync(
            "owner@example.com", Password, "Owner", "device", "Browser", "Dashboard");
        var sessionId = issued.Session.Id;

        Assert.True(await _service.IsRecentlyAuthenticatedAsync(sessionId));
        _clock.Advance(TimeSpan.FromMinutes(9));
        Assert.True(await _service.IsRecentlyAuthenticatedAsync(sessionId));
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(await _service.IsRecentlyAuthenticatedAsync(sessionId));
    }

    [Fact]
    public async Task Confirm_WithThePassword_MakesTheSessionRecentAgain()
    {
        var issued = await _service.BootstrapAdministratorAsync(
            "owner@example.com", Password, "Owner", "device", "Browser", "Dashboard");
        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.False(await _service.IsRecentlyAuthenticatedAsync(issued.Session.Id));

        Assert.False(await _service.ConfirmWithPasswordAsync(issued.Account.Id, issued.Session.Id, "not the password"));
        Assert.False(await _service.IsRecentlyAuthenticatedAsync(issued.Session.Id));

        Assert.True(await _service.ConfirmWithPasswordAsync(issued.Account.Id, issued.Session.Id, Password));
        Assert.True(await _service.IsRecentlyAuthenticatedAsync(issued.Session.Id));
        _clock.Advance(TimeSpan.FromMinutes(9));
        Assert.True(await _service.IsRecentlyAuthenticatedAsync(issued.Session.Id));
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(await _service.IsRecentlyAuthenticatedAsync(issued.Session.Id));
    }

    [Fact]
    public async Task Confirm_CannotBorrowAnotherAccountsSession()
    {
        var owner = await _service.BootstrapAdministratorAsync(
            "owner@example.com", Password, "Owner", "device", "Browser", "Dashboard");
        _clock.Advance(TimeSpan.FromMinutes(11));

        Assert.False(await _service.ConfirmSessionAsync(Guid.NewGuid(), owner.Session.Id, "Passkey"));
        Assert.False(await _service.IsRecentlyAuthenticatedAsync(owner.Session.Id));
    }

    [Fact]
    public async Task ARevokedSession_IsNeverRecent()
    {
        var issued = await _service.BootstrapAdministratorAsync(
            "owner@example.com", Password, "Owner", "device", "Browser", "Dashboard");
        await _service.RevokeSessionAsync(issued.Session.Id, "test");

        Assert.False(await _service.IsRecentlyAuthenticatedAsync(issued.Session.Id));
        Assert.False(await _service.ConfirmWithPasswordAsync(issued.Account.Id, issued.Session.Id, Password));
    }

    [Fact]
    public async Task AThisComputerSession_CountsAsRecent_BecauseThereIsNoPasswordToAsk()
    {
        var issued = await _service.BootstrapThisComputerAdministratorAsync(
            "owner@example.com", "Owner", "device", "Browser", "Dashboard");

        _clock.Advance(TimeSpan.FromHours(3));

        Assert.True(await _service.IsRecentlyAuthenticatedAsync(issued.Session.Id));
    }

    [Fact]
    public async Task ChangePassword_NoLongerAsksForTheCurrentPassword_ButKeepsTheCurrentSession()
    {
        var first = await _service.BootstrapAdministratorAsync(
            "owner@example.com", Password, "Owner", "device", "Browser", "Dashboard");
        var second = (await _service.AuthenticatePasswordAsync("owner@example.com", Password, "device-2", "Other", "Dashboard")).IssuedSession!;

        await _service.ChangePasswordAsync(first.Account.Id, "a brand new password", first.Session.Id);

        Assert.NotNull(await _service.ValidateSessionAsync(first.PlaintextToken, touch: false));
        Assert.Null(await _service.ValidateSessionAsync(second.PlaintextToken, touch: false));
    }

    public void Dispose()
    {
        _database.Dispose();
        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
    }

    private sealed class ClockStub(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class FixedPolicy : IAuthenticationPolicyProvider
    {
        public AuthSettings GetCurrent() => new();
    }
}
