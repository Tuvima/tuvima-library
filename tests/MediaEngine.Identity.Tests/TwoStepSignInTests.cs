using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Identity.Tests;

/// <summary>Optional two-step codes: turning them on, the second sign-in step, recovery codes and resets.</summary>
public sealed class TwoStepSignInTests : IDisposable
{
    private const string Email = "owner@example.com";
    private const string Password = "correct horse battery staple";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_two_step_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly IdentityRepository _identities;
    private readonly AccountRepository _accounts;
    private readonly FirstPartyIdentityService _service;
    private readonly ClockStub _clock = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));

    public TwoStepSignInTests()
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
            new FixedPolicy(),
            twoStepSecrets: new ReversibleProtector());
    }

    [Fact]
    public async Task Setup_ShowsAKeyAndLink_ButTwoStepStaysOffUntilACodeMatches()
    {
        var owner = await SignUpAsync();

        var setup = await _service.BeginTwoStepSetupAsync(owner.Account.Id);

        Assert.Matches("^[A-Z2-7]{32}$", setup.Secret);
        Assert.StartsWith("otpauth://totp/Tuvima%20Library:owner%40example.com?", setup.OtpAuthUri, StringComparison.Ordinal);
        Assert.False(await _service.IsTwoStepEnabledAsync(owner.Account.Id));
        // Not yet confirmed, so a password sign-in is still just a password sign-in.
        Assert.True((await _service.AuthenticatePasswordAsync(Email, Password, "d", "Phone", "Dashboard")).Succeeded);
    }

    [Fact]
    public async Task Setup_IsStoredEncrypted()
    {
        var owner = await SignUpAsync();

        var setup = await _service.BeginTwoStepSetupAsync(owner.Account.Id);

        var stored = await _identities.GetAccountTwoStepAsync(owner.Account.Id);
        Assert.NotNull(stored);
        Assert.DoesNotContain(setup.Secret, stored!.SecretProtected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enable_NeedsACodeFromTheNewKey_AndReturnsFreshRecoveryCodes()
    {
        var owner = await SignUpAsync();
        var setup = await _service.BeginTwoStepSetupAsync(owner.Account.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.EnableTwoStepAsync(owner.Account.Id, "000000"));
        Assert.False(await _service.IsTwoStepEnabledAsync(owner.Account.Id));

        var codes = await _service.EnableTwoStepAsync(owner.Account.Id, CodeFor(setup.Secret));

        Assert.Equal(10, codes.Count);
        Assert.True(await _service.IsTwoStepEnabledAsync(owner.Account.Id));
        // The confirming code counts as used.
        var stored = await _identities.GetAccountTwoStepAsync(owner.Account.Id);
        Assert.Equal(TotpGenerator.StepAt(_clock.GetUtcNow()), stored!.LastUsedStep);
    }

    [Fact]
    public async Task Setup_NeedsAPassword_AndCannotRestartOnceOn()
    {
        var thisComputerOnly = await _service.BootstrapThisComputerAdministratorAsync(Email, "Owner", "d", "Browser", "Dashboard");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.BeginTwoStepSetupAsync(thisComputerOnly.Account.Id));

        var other = await _service.BootstrapAdministratorAsync("other@example.com", Password, "Other", "d", "Browser", "Dashboard");
        await TurnOnAsync(other.Account.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.BeginTwoStepSetupAsync(other.Account.Id));
    }

    [Fact]
    public async Task PasswordSignIn_WithoutTwoStep_IsUnchanged()
    {
        await SignUpAsync();

        var result = await _service.AuthenticatePasswordAsync(Email, Password, "d", "Phone", "Dashboard");

        Assert.True(result.Succeeded);
        Assert.False(result.TwoStepRequired);
        Assert.NotNull(result.IssuedSession);
    }

    [Fact]
    public async Task PasswordSignIn_WithTwoStepOn_NeedsTheCodeBeforeAnySessionExists()
    {
        var owner = await SignUpAsync();
        var secret = await TurnOnAsync(owner.Account.Id);
        _clock.Advance(TimeSpan.FromSeconds(31));

        var first = await _service.AuthenticatePasswordAsync(Email, Password, "d", "Phone", "Dashboard", ingress: ClientIngress.HomeNetwork);

        Assert.False(first.Succeeded);
        Assert.True(first.TwoStepRequired);
        Assert.Null(first.IssuedSession);
        Assert.Single(await _service.GetSessionsAsync(owner.Account.Id)); // only the setup session

        var second = await _service.CompleteTwoStepSignInAsync(first.TwoStepToken!, CodeFor(secret), ingress: ClientIngress.HomeNetwork);

        Assert.True(second.Succeeded);
        Assert.Equal("Password", second.IssuedSession!.Session.AuthenticationMethod);
        Assert.Equal(ClientIngress.HomeNetwork, second.IssuedSession.Session.IssuedIngress);
        Assert.Equal("Phone", second.IssuedSession.Session.DeviceName);
        Assert.Equal(2, (await _service.GetSessionsAsync(owner.Account.Id)).Count);
    }

    [Fact]
    public async Task AWrongPassword_NeverReachesTheCodeStep()
    {
        var owner = await SignUpAsync();
        await TurnOnAsync(owner.Account.Id);

        var result = await _service.AuthenticatePasswordAsync(Email, "not the password at all", "d", "Phone", "Dashboard");

        Assert.False(result.Succeeded);
        Assert.False(result.TwoStepRequired);
    }

    [Fact]
    public async Task ACodeWorksOnce_EvenForAFreshSignIn()
    {
        var owner = await SignUpAsync();
        var secret = await TurnOnAsync(owner.Account.Id);
        _clock.Advance(TimeSpan.FromSeconds(31));
        var code = CodeFor(secret);

        var first = await _service.AuthenticatePasswordAsync(Email, Password, "d", "Phone", "Dashboard");
        Assert.True((await _service.CompleteTwoStepSignInAsync(first.TwoStepToken!, code)).Succeeded);

        var replay = await _service.AuthenticatePasswordAsync(Email, Password, "d2", "Tablet", "Dashboard");
        Assert.False((await _service.CompleteTwoStepSignInAsync(replay.TwoStepToken!, code)).Succeeded);

        // The next code, 30 seconds later, is fine.
        _clock.Advance(TimeSpan.FromSeconds(30));
        var next = await _service.AuthenticatePasswordAsync(Email, Password, "d2", "Tablet", "Dashboard");
        Assert.True((await _service.CompleteTwoStepSignInAsync(next.TwoStepToken!, CodeFor(secret))).Succeeded);
    }

    [Fact]
    public async Task ThePendingToken_WorksOnce()
    {
        var owner = await SignUpAsync();
        var secret = await TurnOnAsync(owner.Account.Id);
        _clock.Advance(TimeSpan.FromSeconds(31));
        var pending = await _service.AuthenticatePasswordAsync(Email, Password, "d", "Phone", "Dashboard");

        Assert.True((await _service.CompleteTwoStepSignInAsync(pending.TwoStepToken!, CodeFor(secret))).Succeeded);

        _clock.Advance(TimeSpan.FromSeconds(30));
        var again = await _service.CompleteTwoStepSignInAsync(pending.TwoStepToken!, CodeFor(secret));
        Assert.False(again.Succeeded);
        Assert.Null(again.IssuedSession);
    }

    [Fact]
    public async Task ThePendingToken_ExpiresAfterFiveMinutes()
    {
        var owner = await SignUpAsync();
        var secret = await TurnOnAsync(owner.Account.Id);
        _clock.Advance(TimeSpan.FromSeconds(31));
        var pending = await _service.AuthenticatePasswordAsync(Email, Password, "d", "Phone", "Dashboard");

        _clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        Assert.False((await _service.CompleteTwoStepSignInAsync(pending.TwoStepToken!, CodeFor(secret))).Succeeded);
    }

    [Fact]
    public async Task ThePendingToken_CannotBeMovedToAnotherKindOfConnection()
    {
        var owner = await SignUpAsync();
        var secret = await TurnOnAsync(owner.Account.Id);
        _clock.Advance(TimeSpan.FromSeconds(31));
        var pending = await _service.AuthenticatePasswordAsync(Email, Password, "d", "Phone", "Dashboard", ingress: ClientIngress.HomeNetwork);

        var fromOutside = await _service.CompleteTwoStepSignInAsync(pending.TwoStepToken!, CodeFor(secret), ingress: ClientIngress.Remote);
        Assert.False(fromOutside.Succeeded);

        // Once it has turned up somewhere else it is dead, even back where it started.
        var backHome = await _service.CompleteTwoStepSignInAsync(pending.TwoStepToken!, CodeFor(secret), ingress: ClientIngress.HomeNetwork);
        Assert.False(backHome.Succeeded);
    }

    [Fact]
    public async Task ARandomPendingToken_IsRefused()
    {
        var owner = await SignUpAsync();
        var secret = await TurnOnAsync(owner.Account.Id);
        _clock.Advance(TimeSpan.FromSeconds(31));

        Assert.False((await _service.CompleteTwoStepSignInAsync("not-a-token", CodeFor(secret))).Succeeded);
        Assert.False((await _service.CompleteTwoStepSignInAsync("", CodeFor(secret))).Succeeded);
    }

    [Fact]
    public async Task FiveWrongCodes_EndThatSignInEvenIfTheNextOneIsRight_AtHome()
    {
        var owner = await SignUpAsync();
        var secret = await TurnOnAsync(owner.Account.Id);
        _clock.Advance(TimeSpan.FromSeconds(31));
        var pending = await _service.AuthenticatePasswordAsync(Email, Password, "d", "Phone", "Dashboard", ingress: ClientIngress.HomeNetwork);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.False((await _service.CompleteTwoStepSignInAsync(pending.TwoStepToken!, "000000", ingress: ClientIngress.HomeNetwork)).Succeeded);
        }

        Assert.False((await _service.CompleteTwoStepSignInAsync(pending.TwoStepToken!, CodeFor(secret), ingress: ClientIngress.HomeNetwork)).Succeeded);
        // Home mistakes never lock the account; the password still starts a fresh sign-in.
        var fresh = await _service.AuthenticatePasswordAsync(Email, Password, "d", "Phone", "Dashboard", ingress: ClientIngress.HomeNetwork);
        Assert.True(fresh.TwoStepRequired);
    }

    [Fact]
    public async Task WrongCodesFromOutside_CountTowardTheLockout_AndRepeatingThePasswordDoesNotResetIt()
    {
        var owner = await SignUpAsync();
        var secret = await TurnOnAsync(owner.Account.Id);
        _clock.Advance(TimeSpan.FromSeconds(31));

        AuthenticationAttemptResult last = default!;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var pending = await _service.AuthenticatePasswordAsync(Email, Password, "d", "Phone", "Dashboard", ingress: ClientIngress.Remote);
            Assert.True(pending.TwoStepRequired);
            last = await _service.CompleteTwoStepSignInAsync(pending.TwoStepToken!, "000000", ingress: ClientIngress.Remote);
            Assert.False(last.Succeeded);
        }

        Assert.True(last.LockedOut);
        var locked = await _service.AuthenticatePasswordAsync(Email, Password, "d", "Phone", "Dashboard", ingress: ClientIngress.Remote);
        Assert.True(locked.LockedOut);
        Assert.False(locked.TwoStepRequired);

        _clock.Advance(TimeSpan.FromMinutes(16));
        var after = await _service.AuthenticatePasswordAsync(Email, Password, "d", "Phone", "Dashboard", ingress: ClientIngress.Remote);
        Assert.True((await _service.CompleteTwoStepSignInAsync(after.TwoStepToken!, CodeFor(secret), ingress: ClientIngress.Remote)).Succeeded);
    }

    [Fact]
    public async Task WrongCodesAtHome_NeverLockTheAccount()
    {
        var owner = await SignUpAsync();
        await TurnOnAsync(owner.Account.Id);
        _clock.Advance(TimeSpan.FromSeconds(31));

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var pending = await _service.AuthenticatePasswordAsync(Email, Password, "d", "Phone", "Dashboard", ingress: ClientIngress.HomeNetwork);
            Assert.True(pending.TwoStepRequired);
            var result = await _service.CompleteTwoStepSignInAsync(pending.TwoStepToken!, "000000", ingress: ClientIngress.HomeNetwork);
            Assert.False(result.LockedOut);
        }
    }

    [Fact]
    public async Task ARecoveryCode_ReplacesTheAppCodeOnce()
    {
        var owner = await SignUpAsync();
        var secret = await TurnOnAsync(owner.Account.Id);
        var recovery = (await _service.RegenerateRecoveryCodesAsync(owner.Account.Id))[0];
        _clock.Advance(TimeSpan.FromSeconds(31));

        var first = await _service.AuthenticatePasswordAsync(Email, Password, "d", "Phone", "Dashboard");
        Assert.True((await _service.CompleteTwoStepSignInAsync(first.TwoStepToken!, recovery)).Succeeded);

        var second = await _service.AuthenticatePasswordAsync(Email, Password, "d", "Phone", "Dashboard");
        Assert.False((await _service.CompleteTwoStepSignInAsync(second.TwoStepToken!, recovery)).Succeeded);
        Assert.NotNull(secret);
    }

    [Fact]
    public async Task ConfirmItsYou_WithTwoStepOn_NeedsTheCodeToo()
    {
        var owner = await SignUpAsync();
        var secret = await TurnOnAsync(owner.Account.Id);
        _clock.Advance(TimeSpan.FromMinutes(11));

        Assert.False(await _service.IsRecentlyAuthenticatedAsync(owner.Session.Id));
        Assert.False(await _service.ConfirmWithPasswordAsync(owner.Account.Id, owner.Session.Id, Password));
        Assert.False(await _service.ConfirmWithPasswordAsync(owner.Account.Id, owner.Session.Id, Password, default, "000000"));
        Assert.False(await _service.IsRecentlyAuthenticatedAsync(owner.Session.Id));

        Assert.True(await _service.ConfirmWithPasswordAsync(owner.Account.Id, owner.Session.Id, Password, default, CodeFor(secret)));
        Assert.True(await _service.IsRecentlyAuthenticatedAsync(owner.Session.Id));
    }

    [Fact]
    public async Task ConfirmItsYou_WithoutTwoStep_StillOnlyNeedsThePassword()
    {
        var owner = await SignUpAsync();
        _clock.Advance(TimeSpan.FromMinutes(11));

        Assert.True(await _service.ConfirmWithPasswordAsync(owner.Account.Id, owner.Session.Id, Password));
    }

    [Fact]
    public async Task TurningOff_NeedsACurrentCodeOrARecoveryCode()
    {
        var owner = await SignUpAsync();
        var secret = await TurnOnAsync(owner.Account.Id);
        _clock.Advance(TimeSpan.FromSeconds(31));

        Assert.False(await _service.DisableTwoStepAsync(owner.Account.Id, owner.Session.Id, "000000"));
        Assert.False(await _service.DisableTwoStepAsync(owner.Account.Id, owner.Session.Id, ""));
        Assert.True(await _service.IsTwoStepEnabledAsync(owner.Account.Id));

        Assert.True(await _service.DisableTwoStepAsync(owner.Account.Id, owner.Session.Id, CodeFor(secret)));
        Assert.False(await _service.IsTwoStepEnabledAsync(owner.Account.Id));
        Assert.True((await _service.AuthenticatePasswordAsync(Email, Password, "d", "Phone", "Dashboard")).Succeeded);
    }

    [Fact]
    public async Task TurningOff_WorksWithARecoveryCode()
    {
        var owner = await SignUpAsync();
        await TurnOnAsync(owner.Account.Id);
        var recovery = (await _service.RegenerateRecoveryCodesAsync(owner.Account.Id))[0];

        Assert.True(await _service.DisableTwoStepAsync(owner.Account.Id, owner.Session.Id, recovery));
        Assert.False(await _service.IsTwoStepEnabledAsync(owner.Account.Id));
    }

    [Fact]
    public async Task TurningOff_ThenOnAgain_StartsFromANewKey()
    {
        var owner = await SignUpAsync();
        var first = await TurnOnAsync(owner.Account.Id);
        var recovery = (await _service.RegenerateRecoveryCodesAsync(owner.Account.Id))[0];
        await _service.DisableTwoStepAsync(owner.Account.Id, owner.Session.Id, recovery);

        var second = await _service.BeginTwoStepSetupAsync(owner.Account.Id);

        Assert.NotEqual(first, second.Secret);
    }

    [Fact]
    public async Task AnAdministratorReset_TurnsItOffWithoutACode_AndIsRecorded()
    {
        var owner = await SignUpAsync();
        await TurnOnAsync(owner.Account.Id);

        Assert.True(await _service.ResetTwoStepAsync(owner.Account.Id, "administrator_reset"));
        Assert.False(await _service.ResetTwoStepAsync(owner.Account.Id, "administrator_reset"));

        Assert.False(await _service.IsTwoStepEnabledAsync(owner.Account.Id));
        Assert.True((await _service.AuthenticatePasswordAsync(Email, Password, "d", "Phone", "Dashboard")).Succeeded);
        Assert.Contains(AuditEvents(), e => e == "two_step_reset");
    }

    [Fact]
    public async Task AReset_KillsAHalfFinishedSignIn()
    {
        var owner = await SignUpAsync();
        var secret = await TurnOnAsync(owner.Account.Id);
        _clock.Advance(TimeSpan.FromSeconds(31));
        var pending = await _service.AuthenticatePasswordAsync(Email, Password, "d", "Phone", "Dashboard");

        await _service.ResetTwoStepAsync(owner.Account.Id, "administrator_reset");

        Assert.False((await _service.CompleteTwoStepSignInAsync(pending.TwoStepToken!, CodeFor(secret))).Succeeded);
    }

    [Fact]
    public async Task TheHostConsoleReset_FindsTheAccountByEmail()
    {
        var owner = await SignUpAsync();
        await TurnOnAsync(owner.Account.Id);
        IHostTwoStepRecoveryService host = _service;

        Assert.True(await host.ResetTwoStepFromHostAsync("OWNER@example.com"));
        Assert.False(await _service.IsTwoStepEnabledAsync(owner.Account.Id));
        Assert.False(await host.ResetTwoStepFromHostAsync(Email));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.ResetTwoStepFromHostAsync("nobody@example.com"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.ResetTwoStepFromHostAsync("not an email"));
        Assert.Contains(AuditEvents(), e => e == "two_step_reset");
    }

    [Fact]
    public async Task PasskeyAndLinkedAccountSignIns_SkipTheCode()
    {
        var owner = await SignUpAsync();
        await TurnOnAsync(owner.Account.Id);

        var passkey = await _service.CreatePasskeySessionAsync(owner.Account.Id, "d", "Laptop", "Dashboard");
        var linked = await _service.CreateExternalSessionAsync(owner.Account.Id, "oidc", "d", "Laptop", "Dashboard");

        Assert.Equal("Passkey", passkey.Session.AuthenticationMethod);
        Assert.Equal("Oidc", linked.Session.AuthenticationMethod);
    }

    [Fact]
    public async Task WhenTheKeyRingChanged_RecoveryCodesStillWork_AndAppCodesAreRefused()
    {
        var owner = await SignUpAsync();
        var secret = await TurnOnAsync(owner.Account.Id);
        var recovery = (await _service.RegenerateRecoveryCodesAsync(owner.Account.Id))[0];
        using (var connection = _database.CreateConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE account_two_step SET secret_protected='garbage';";
            command.ExecuteNonQuery();
        }

        _clock.Advance(TimeSpan.FromSeconds(31));
        var first = await _service.AuthenticatePasswordAsync(Email, Password, "d", "Phone", "Dashboard");
        Assert.False((await _service.CompleteTwoStepSignInAsync(first.TwoStepToken!, CodeFor(secret))).Succeeded);

        var second = await _service.AuthenticatePasswordAsync(Email, Password, "d", "Phone", "Dashboard");
        Assert.True((await _service.CompleteTwoStepSignInAsync(second.TwoStepToken!, recovery)).Succeeded);
    }

    private async Task<SessionIssueResult> SignUpAsync() =>
        await _service.BootstrapAdministratorAsync(Email, Password, "Owner", "device", "Browser", "Dashboard");

    /// <summary>Starts setup, confirms it with the first code, and returns the authenticator key.</summary>
    private async Task<string> TurnOnAsync(Guid accountId)
    {
        var setup = await _service.BeginTwoStepSetupAsync(accountId);
        await _service.EnableTwoStepAsync(accountId, CodeFor(setup.Secret));
        return setup.Secret;
    }

    private string CodeFor(string base32Secret) =>
        TotpGenerator.Compute(TotpGenerator.FromBase32(base32Secret), TotpGenerator.StepAt(_clock.GetUtcNow()));

    private List<string> AuditEvents()
    {
        var events = new List<string>();
        using var connection = _database.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_type FROM identity_audit_events;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            events.Add(reader.GetString(0));
        }

        return events;
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

    /// <summary>Stands in for data protection: reversible, and never the plain key.</summary>
    private sealed class ReversibleProtector : ITwoStepSecretProtector
    {
        public string Protect(string base32Secret) => "protected:" + new string(base32Secret.Reverse().ToArray());

        public string? Unprotect(string protectedSecret) =>
            protectedSecret.StartsWith("protected:", StringComparison.Ordinal)
                ? new string(protectedSecret["protected:".Length..].Reverse().ToArray())
                : null;
    }
}
