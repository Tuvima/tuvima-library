using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Identity.Tests;

public sealed class ThisComputerAccountTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_this_computer_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly IdentityRepository _identities;
    private readonly AccountRepository _accounts;
    private readonly FirstPartyIdentityService _service;

    public ThisComputerAccountTests()
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
            TimeProvider.System,
            new FixedPolicy());
    }

    [Fact]
    public async Task Setup_CreatesAnAccountWithNoPasswordThatIsThisComputerOnly()
    {
        var issued = await _service.BootstrapThisComputerAdministratorAsync(
            "owner@example.com", "Owner", "device", "Browser", "Dashboard");

        Assert.True(await _service.IsAdministratorConfiguredAsync());
        var account = Assert.Single(await _accounts.GetAllAsync());
        Assert.True(account.IsThisComputerOnly);
        Assert.True(account.IsAdministrator);
        Assert.Equal("owner@example.com", account.Email);
        Assert.NotNull(account.HouseholdId);
        Assert.Null(await _identities.GetAccountCredentialAsync(account.Id, AccountCredentialKind.Password));
        Assert.Empty(issued.RecoveryCodes);
        Assert.Equal(ClientIngress.ThisComputer, issued.Session.IssuedIngress);
        Assert.Equal("Owner", issued.Profile.DisplayName);
    }

    [Fact]
    public async Task Setup_WithAPassword_IsNotThisComputerOnly()
    {
        await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "device", "Browser", "Dashboard");

        var account = Assert.Single(await _accounts.GetAllAsync());
        Assert.False(account.IsThisComputerOnly);
        Assert.NotNull(await _identities.GetAccountCredentialAsync(account.Id, AccountCredentialKind.Password));
        Assert.Null(await _service.GetThisComputerAccountNameAsync());
        Assert.Null(await _service.SignInThisComputerAccountAsync("device", "Browser", "Dashboard"));
    }

    [Theory]
    [InlineData(ClientIngress.ThisComputer, true)]
    [InlineData(ClientIngress.HomeNetwork, false)]
    [InlineData(ClientIngress.Remote, false)]
    [InlineData("something-else", false)]
    public async Task Session_ContinuesOnlyFromThisComputer(string currentIngress, bool allowed)
    {
        var issued = await _service.BootstrapThisComputerAdministratorAsync(
            "owner@example.com", "Owner", "device", "Browser", "Dashboard");

        var validated = await _service.ValidateSessionAsync(issued.PlaintextToken, touch: false, currentIngress: currentIngress);

        Assert.Equal(allowed, validated is not null);
    }

    [Fact]
    public async Task Session_WithNoStatedOrigin_IsRefusedForThisKindOfAccount()
    {
        var issued = await _service.BootstrapThisComputerAdministratorAsync(
            "owner@example.com", "Owner", "device", "Browser", "Dashboard");

        Assert.Null(await _service.ValidateSessionAsync(issued.PlaintextToken, touch: false));
    }

    [Fact]
    public async Task Session_WithNoStatedOrigin_StillWorksForAnAccountWithAPassword()
    {
        var issued = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "device", "Browser", "Dashboard",
            ingress: ClientIngress.ThisComputer);

        Assert.NotNull(await _service.ValidateSessionAsync(issued.PlaintextToken, touch: false));
    }

    [Fact]
    public async Task SignInAgain_IssuesAThisComputerSessionWithoutAPassword()
    {
        await _service.BootstrapThisComputerAdministratorAsync(
            "owner@example.com", "Owner", "device", "Browser", "Dashboard");

        Assert.Equal("Owner", await _service.GetThisComputerAccountNameAsync());
        var again = await _service.SignInThisComputerAccountAsync("device-2", "Second tab", "Dashboard");

        Assert.NotNull(again);
        Assert.Equal(ClientIngress.ThisComputer, again!.Session.IssuedIngress);
        Assert.NotNull(await _service.ValidateSessionAsync(again.PlaintextToken, touch: false, currentIngress: ClientIngress.ThisComputer));
        Assert.Null(await _service.ValidateSessionAsync(again.PlaintextToken, touch: false, currentIngress: ClientIngress.HomeNetwork));
    }

    [Fact]
    public async Task SignInAgain_WithNoSuchAccount_ReturnsNothing()
    {
        Assert.Null(await _service.GetThisComputerAccountNameAsync());
        Assert.Null(await _service.SignInThisComputerAccountAsync("device", "Browser", "Dashboard"));
    }

    [Fact]
    public async Task PasswordSignIn_IsNeverPossibleForAnAccountWithoutAPassword()
    {
        await _service.BootstrapThisComputerAdministratorAsync(
            "owner@example.com", "Owner", "device", "Browser", "Dashboard");

        var attempt = await _service.AuthenticatePasswordAsync(
            "owner@example.com", "correct horse battery staple", "device", "Browser", "Dashboard",
            ingress: ClientIngress.ThisComputer);

        Assert.False(attempt.Succeeded);
    }

    [Fact]
    public async Task SecondSetup_IsRefused()
    {
        await _service.BootstrapThisComputerAdministratorAsync(
            "owner@example.com", "Owner", "device", "Browser", "Dashboard");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.BootstrapThisComputerAdministratorAsync(
            "other@example.com", "Other", "device", "Browser", "Dashboard"));
    }

    [Fact]
    public async Task Setup_WithoutAnEmail_IsRefusedAndCreatesNothing()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.BootstrapThisComputerAdministratorAsync(
            "", "Owner", "device", "Browser", "Dashboard"));

        Assert.False(await _service.IsAdministratorConfiguredAsync());
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

    private sealed class FixedPolicy : IAuthenticationPolicyProvider
    {
        public AuthSettings GetCurrent() => new();
    }
}
