using System.Security.Cryptography;
using System.Text;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Identity.Tests;

/// <summary>
/// Away-from-home access (spec remote-access WP3 and WP6): a person with no password or passkey can sign in only at home,
/// whatever the way in, and every outside attempt is kept (who, when, how, and the coarse kind of place, never an address).
/// </summary>
public sealed class OutsideSignInGateTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_outside_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly IdentityRepository _identities;
    private readonly AccountRepository _accounts;
    private readonly FirstPartyIdentityService _service;
    private readonly DateTimeOffset _now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    public OutsideSignInGateTests()
    {
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _identities = new IdentityRepository(_database);
        _accounts = new AccountRepository(_database);
        _service = new FirstPartyIdentityService(
            _identities, _accounts, new ProfileRepository(_database),
            new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(),
            new FixedTime(_now), new FixedPolicy());
    }

    [Theory]
    [InlineData(ClientIngress.Remote)]
    [InlineData("")]
    [InlineData("somewhere-else")]
    public async Task LinkedSignIn_FromOutside_IsRefusedForAPersonWithoutAPassword(string ingress)
    {
        var owner = await _service.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");

        var refusal = await Assert.ThrowsAsync<OutsideSignInRefusedException>(() =>
            _service.CreateExternalSessionAsync(owner.Account.Id, "google", "phone", "Phone", "Dashboard", ingress: ingress));

        Assert.Contains("only sign in at home", refusal.Message, StringComparison.Ordinal);
        Assert.Equal("outside_needs_password", OutsideSignInRefusedException.Code);
    }

    [Fact]
    public async Task LinkedSignIn_AtHomeAndOnThisComputer_StillWorksForAPersonWithoutAPassword()
    {
        var owner = await _service.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");

        Assert.NotNull(await _service.CreateExternalSessionAsync(owner.Account.Id, "google", "phone", "Phone", "Dashboard", ingress: ClientIngress.HomeNetwork));
        Assert.NotNull(await _service.CreateExternalSessionAsync(owner.Account.Id, "google", "laptop", "Laptop", "Dashboard", ingress: ClientIngress.ThisComputer));
        Assert.Empty(await _identities.GetRecentOutsideSignInsAsync(20));
    }

    [Fact]
    public async Task PasskeySignIn_FromOutside_IsRefusedForAnAccountThatIsStillThisComputerOnly()
    {
        // A passkey added on this computer is not enough on its own: until the account is secured it stays home only.
        var owner = await _service.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");

        await Assert.ThrowsAsync<OutsideSignInRefusedException>(() =>
            _service.CreatePasskeySessionAsync(owner.Account.Id, "phone", "Phone", "Dashboard", ingress: ClientIngress.Remote));
        Assert.NotNull(await _service.CreatePasskeySessionAsync(owner.Account.Id, "phone", "Phone", "Dashboard", ingress: ClientIngress.HomeNetwork));
    }

    [Fact]
    public async Task InvitedPersonWhoHasNotChosenAPassword_CannotSignInFromOutsideByAnyOtherWay()
    {
        await _service.BootstrapAdministratorAsync("owner@example.com", Password, "Owner", "d", "Browser", "Dashboard");
        var invited = await InsertInvitedAccountAsync("family@example.com");

        await Assert.ThrowsAsync<OutsideSignInRefusedException>(() =>
            _service.CreateExternalSessionAsync(invited.Id, "google", "phone", "Phone", "Dashboard", ingress: ClientIngress.Remote));
        await Assert.ThrowsAsync<OutsideSignInRefusedException>(() =>
            _service.CreatePasskeySessionAsync(invited.Id, "phone", "Phone", "Dashboard", ingress: ClientIngress.Remote));
        var password = await _service.AuthenticatePasswordAsync("family@example.com", Password, "phone", "Phone", "Dashboard", ingress: ClientIngress.Remote);
        Assert.False(password.Succeeded);
    }

    [Fact]
    public async Task Invitation_RedeemedFromOutside_CreatesAPasswordProtectedAccountAndIsRecorded()
    {
        await _service.BootstrapAdministratorAsync("owner@example.com", Password, "Owner", "d", "Browser", "Dashboard");
        var invited = await InsertInvitedAccountAsync("family@example.com");
        const string code = "WD5N8RB3CJ";
        await _accounts.InsertInvitationAsync(new AccountInvitation
        {
            Id = Guid.NewGuid(),
            AccountId = invited.Id,
            TokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code))),
            CreatedAt = _now,
            ExpiresAt = _now.AddDays(7),
        });

        var accepted = await _service.AcceptInvitationAsync(code, "family password", "phone", "Phone", "Dashboard", ingress: ClientIngress.Remote);

        Assert.True(await _accounts.IsUserSecuredAsync(accepted.Account.Id));
        var seen = Assert.Single(await _identities.GetRecentOutsideSignInsAsync(20), record => record.Method == "Password" && record.Succeeded && record.ProfileName == "Invited");
        Assert.Equal(ClientIngress.Remote, seen.IngressKind);
    }

    [Fact]
    public async Task PasswordSignIn_FromOutside_WorksForASecuredPerson_AndIsRecordedWithNoAddress()
    {
        await _service.BootstrapAdministratorAsync("owner@example.com", Password, "Owner", "d", "Browser", "Dashboard");

        var attempt = await _service.AuthenticatePasswordAsync("owner@example.com", Password, "phone", "Phone", "Dashboard", ingress: ClientIngress.Remote);

        Assert.True(attempt.Succeeded);
        var newest = (await _identities.GetRecentOutsideSignInsAsync(20))[0];
        Assert.True(newest.Succeeded);
        Assert.Equal("Owner", newest.ProfileName);
        Assert.Equal("Password", newest.Method);
        Assert.Equal(_now, newest.At);
        // Only the coarse kind of place is kept; the stored text has nothing that could be an address.
        Assert.Equal(ClientIngress.Remote, newest.IngressKind);
        using var connection = _database.CreateConnection();
        var detail = Dapper.SqlMapper.ExecuteScalar<string>(connection,
            "SELECT detail FROM identity_audit_events WHERE event_type = 'outside_sign_in' ORDER BY id DESC LIMIT 1;");
        Assert.Equal("remote|Password", detail);
    }

    [Fact]
    public async Task PasswordSignIn_AtHome_IsNotPartOfTheOutsideHistory()
    {
        await _service.BootstrapAdministratorAsync("owner@example.com", Password, "Owner", "d", "Browser", "Dashboard", ingress: ClientIngress.HomeNetwork);

        var attempt = await _service.AuthenticatePasswordAsync("owner@example.com", Password, "tv", "TV", "Dashboard", ingress: ClientIngress.HomeNetwork);

        Assert.True(attempt.Succeeded);
        Assert.Empty(await _identities.GetRecentOutsideSignInsAsync(20));
    }

    [Fact]
    public async Task SigningInOnThisComputerFromOutside_IsRefusedAndRecordedWithTheProfileName()
    {
        await _service.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");

        Assert.NotNull(await _service.SignInThisComputerAccountAsync("phone", "Phone", "Dashboard", ClientIngress.HomeNetwork));
        Assert.Empty(await _identities.GetRecentOutsideSignInsAsync(20));
        Assert.Null(await _service.SignInThisComputerAccountAsync("phone", "Phone", "Dashboard", ClientIngress.Remote));

        var refused = Assert.Single(await _identities.GetRecentOutsideSignInsAsync(20));
        Assert.False(refused.Succeeded);
        Assert.Equal("Owner", refused.ProfileName);
        Assert.Equal("ThisComputer", refused.Method);
        Assert.Equal(ClientIngress.Remote, refused.IngressKind);
    }

    [Fact]
    public async Task TheOutsideHistory_IsNewestFirst_AndKeepsOnlyTheLatestTwoHundred()
    {
        await _service.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "d", "Browser", "Dashboard");

        for (var attempt = 0; attempt < OutsideSignInEventTypes.RetainedRows + 5; attempt++)
        {
            await _service.SignInThisComputerAccountAsync("phone", "Phone", "Dashboard", ClientIngress.Remote);
        }

        var kept = await _identities.GetRecentOutsideSignInsAsync(1000);
        Assert.Equal(OutsideSignInEventTypes.RetainedRows, kept.Count);
        Assert.Equal(20, (await _identities.GetRecentOutsideSignInsAsync(20)).Count);
    }

    private async Task<Account> InsertInvitedAccountAsync(string email)
    {
        var invited = new Account
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        var person = new Profile { Id = Guid.NewGuid(), DisplayName = "Invited", AvatarColor = "#7C4DFF", Role = ProfileRole.StandardUser, CreatedAt = _now };
        await _accounts.CreateAccountAsync(invited, new TestGrant(invited.Id, person.Id, IsDefault: true), newProfile: person);
        return invited;
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedPolicy : IAuthenticationPolicyProvider
    {
        public AuthSettings GetCurrent() => new();
    }
}
