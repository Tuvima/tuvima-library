using System.Security.Cryptography;
using System.Text;
using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Identity.Tests;

public sealed class FirstPartyIdentityServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_identity_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly IdentityRepository _identities;
    private readonly AccountRepository _accounts;
    private readonly ProfileRepository _profiles;
    private readonly FirstPartyIdentityService _service;
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly MutableAuthenticationPolicyProvider _policy = new();

    public FirstPartyIdentityServiceTests()
    {
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _identities = new IdentityRepository(_database);
        _accounts = new AccountRepository(_database);
        _profiles = new ProfileRepository(_database);
        _service = new FirstPartyIdentityService(
            _identities,
            _accounts,
            _profiles,
            new PasswordHasher<AccountCredential>(),
            new PasswordHasher<ProfileCredential>(),
            _clock,
            _policy);
    }

    [Fact]
    public async Task BootstrapValidatesPinBeforeCreatingAdministratorAndPreservesLeadingZeroes()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "device", "Browser", "Dashboard", pin: "123"));
        Assert.False(await _service.IsAdministratorConfiguredAsync());
        var issued = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "device", "Browser", "Dashboard", pin: "0123");
        var credential = await _identities.GetCredentialAsync(issued.Profile.Id, MediaEngine.Domain.Entities.ProfileCredentialKind.ProfilePin);
        Assert.NotNull(credential);
        Assert.False((await _service.AuthenticatePinAsync(issued.Profile.Id, "0123", "pin-device", "Browser", "Dashboard")).Succeeded);
        Assert.NotEmpty(issued.RecoveryCodes);
        Assert.True(await _service.IsAdministratorConfiguredAsync());
        Assert.Single(await _accounts.GetAllAsync());
    }

    [Fact]
    public async Task BootstrapCompletion_DoesNotDependOnProfilePresentationRole()
    {
        await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner",
            "browser-1", "Living room", "Dashboard");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "UPDATE profiles SET role='RestrictedProfile' WHERE id=@profileId;",
                new { profileId = Profile.SeedProfileId });
        }

        Assert.True(await _service.IsAdministratorConfiguredAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.BootstrapAdministratorAsync(
                "takeover@example.com", "different secure password", "Takeover",
                "browser-2", "Other", "Dashboard"));
    }

    [Fact]
    public async Task BootstrapCompletion_PersistsWhenAccountGrantAndSignInMethodAreDisabled()
    {
        await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner",
            "browser-1", "Living room", "Dashboard");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "UPDATE accounts SET is_enabled=0,is_administrator=0 WHERE id=@accountId;",
                new { accountId = Account.SeedAccountId });
            connection.Execute(
                "UPDATE account_profile_grants SET is_enabled=0,admin_enabled=0 WHERE account_id=@accountId;",
                new { accountId = Account.SeedAccountId });
            connection.Execute(
                "DELETE FROM account_credentials WHERE account_id=@accountId;",
                new { accountId = Account.SeedAccountId });
        }

        Assert.True(await _service.IsAdministratorConfiguredAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.BootstrapAdministratorAsync(
                "takeover@example.com", "different secure password", "Takeover",
                "browser-2", "Other", "Dashboard"));
    }

    [Fact]
    public async Task BootstrapCompletion_PersistsForPasskeyOnlyAdministrator()
    {
        await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner",
            "browser-1", "Living room", "Dashboard");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "DELETE FROM account_credentials WHERE account_id=@accountId;",
                new { accountId = Account.SeedAccountId });
            connection.Execute("""
                INSERT INTO account_passkeys(credential_id,account_id,name,data_json,created_at,last_used_at)
                VALUES(@credentialId,@accountId,'Security key','{}',@createdAt,NULL);
                """, new
            {
                credentialId = new byte[] { 1, 2, 3, 4 },
                accountId = Account.SeedAccountId,
                createdAt = _clock.GetUtcNow().ToString("O"),
            });
        }

        Assert.True(await _service.IsAdministratorConfiguredAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.BootstrapAdministratorAsync(
                "takeover@example.com", "different secure password", "Takeover",
                "browser-2", "Other", "Dashboard"));
    }

    [Fact]
    public async Task ConcurrentBootstrap_AllowsOnlyOneAccountCreation()
    {
        var attempts = Enumerable.Range(0, 2).Select(index => Task.Run(async () =>
        {
            try
            {
                await _service.BootstrapAdministratorAsync(
                    $"owner{index}@example.com", "correct horse battery staple", "Owner",
                    $"browser-{index}", "Browser", "Dashboard");
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }));

        var outcomes = await Task.WhenAll(attempts);

        Assert.Single(outcomes, succeeded => succeeded);
        Assert.Single(await _accounts.GetAllAsync());
        Assert.True(await _service.IsAdministratorConfiguredAsync());
    }

    [Fact]
    public async Task BootstrapAndPasswordLogin_UseWorkFactoredHashAndRevocableDeviceSession()
    {
        var bootstrap = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Living room", "Dashboard");

        var account = await _accounts.GetByNormalizedEmailAsync("OWNER@EXAMPLE.COM");
        var credential = await _identities.GetAccountCredentialAsync(account!.Id, AccountCredentialKind.Password);
        Assert.NotNull(credential);
        Assert.Equal("aspnet-pbkdf2-v3", credential.HashScheme);
        Assert.DoesNotContain("correct horse battery staple", credential.SecretHash, StringComparison.Ordinal);
        Assert.NotEqual(64, credential.SecretHash.Length);
        Assert.Equal(10, bootstrap.RecoveryCodes.Count);
        Assert.Equal("owner@example.com", account.Email);
        Assert.Equal("Owner", bootstrap.Profile.DisplayName);

        var login = await _service.AuthenticatePasswordAsync(
            "OWNER@example.com", "correct horse battery staple", "browser-2", "Office", "Dashboard");
        Assert.True(login.Succeeded);
        Assert.NotNull(login.IssuedSession);
        Assert.NotNull(await _service.ValidateSessionAsync(login.IssuedSession.PlaintextToken));

        await _accounts.SetAdminUnlockAsync(new GrantAdminUnlock
        {
            SessionId = login.IssuedSession.Session.Id,
            AccountId = login.IssuedSession.Account.Id,
            ProfileId = login.IssuedSession.ActiveProfile.Id,
            ProtectionVersion = 1,
            Method = "GrantPin",
            GrantedAt = _clock.GetUtcNow(),
            ExpiresAt = _clock.GetUtcNow().AddMinutes(30),
        });

        Assert.True(await _service.RevokeSessionAsync(login.IssuedSession.Session.Id, "test"));
        Assert.Null(await _service.ValidateSessionAsync(login.IssuedSession.PlaintextToken));
        Assert.Null(await _accounts.GetAdminUnlockAsync(login.IssuedSession.Session.Id,
            login.IssuedSession.Account.Id, login.IssuedSession.ActiveProfile.Id, _clock.GetUtcNow()));
    }

    [Fact]
    public async Task SessionCreatedByLogin_CanBeReadBackByItsGuid()
    {
        var bootstrap = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Living room", "Dashboard");

        var stored = await _identities.GetSessionByIdAsync(bootstrap.Session.Id);

        Assert.NotNull(stored);
        Assert.Equal(bootstrap.Session.Id, stored.Id);
        Assert.Equal(bootstrap.Session.AccountId, stored.AccountId);
    }

    [Fact]
    public async Task SessionPolicy_ControlsExpiryAndRevokesOldestSessionAtLimit()
    {
        _policy.Settings.SessionLifetimeHours = 6;
        _policy.Settings.MaximumActiveSessions = 2;
        var bootstrap = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Living room", "Dashboard");
        _clock.Advance(TimeSpan.FromMinutes(1));
        var second = (await _service.AuthenticatePasswordAsync(
            "owner@example.com", "correct horse battery staple", "browser-2", "Office", "Dashboard")).IssuedSession!;
        _clock.Advance(TimeSpan.FromMinutes(1));
        var third = (await _service.AuthenticatePasswordAsync(
            "owner@example.com", "correct horse battery staple", "browser-3", "Phone", "Dashboard")).IssuedSession!;

        Assert.Equal(TimeSpan.FromHours(6), third.Session.ExpiresAt - third.Session.CreatedAt);
        Assert.Null(await _service.ValidateSessionAsync(bootstrap.PlaintextToken));
        Assert.NotNull(await _service.ValidateSessionAsync(second.PlaintextToken));
        Assert.NotNull(await _service.ValidateSessionAsync(third.PlaintextToken));
        Assert.Equal(2, (await _service.GetSessionsAsync(third.Account.Id)).Count(session => session.IsActive(_clock.GetUtcNow())));
    }

    [Fact]
    public async Task SessionPolicy_SerializesConcurrentIssuanceAtTheConfiguredLimit()
    {
        _policy.Settings.MaximumActiveSessions = 2;
        var bootstrap = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Living room", "Dashboard");

        var attempts = await Task.WhenAll(
            _service.AuthenticatePasswordAsync(
                "owner@example.com", "correct horse battery staple", "browser-2", "Office", "Dashboard"),
            _service.AuthenticatePasswordAsync(
                "owner@example.com", "correct horse battery staple", "browser-3", "Phone", "Dashboard"));

        Assert.All(attempts, attempt => Assert.True(attempt.Succeeded));
        Assert.Equal(2, (await _service.GetSessionsAsync(bootstrap.Account.Id))
            .Count(session => session.IsActive(_clock.GetUtcNow())));
        Assert.Null(await _service.ValidateSessionAsync(bootstrap.PlaintextToken));
    }

    [Fact]
    public async Task ChangePassword_PreservesCurrentSessionAndRevokesEveryOtherSession()
    {
        var first = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner",
            "browser-1", "Living room", "Dashboard");
        var current = (await _service.AuthenticatePasswordAsync(
            "owner@example.com", "correct horse battery staple", "browser-2", "Office", "Dashboard")).IssuedSession!;

        await _service.ChangePasswordAsync(
            first.Account.Id, "correct horse battery staple", "replacement password",
            current.Session.Id);

        Assert.Null(await _service.ValidateSessionAsync(first.PlaintextToken));
        Assert.NotNull(await _service.ValidateSessionAsync(current.PlaintextToken));
        Assert.False((await _service.AuthenticatePasswordAsync(
            "owner@example.com", "correct horse battery staple", "browser-3", "Other", "Dashboard")).Succeeded);
        Assert.True((await _service.AuthenticatePasswordAsync(
            "owner@example.com", "replacement password", "browser-4", "Other", "Dashboard")).Succeeded);
    }

    [Fact]
    public async Task RevokeOtherSessions_PreservesCurrentSessionAndClearsOtherAdminUnlocks()
    {
        var first = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner",
            "browser-1", "Living room", "Dashboard");
        var current = (await _service.AuthenticatePasswordAsync(
            "owner@example.com", "correct horse battery staple", "browser-2", "Office", "Dashboard")).IssuedSession!;
        await _accounts.SetAdminUnlockAsync(new GrantAdminUnlock
        {
            SessionId = first.Session.Id,
            AccountId = first.Account.Id,
            ProfileId = first.ActiveProfile.Id,
            ProtectionVersion = 1,
            Method = "GrantPin",
            GrantedAt = _clock.GetUtcNow(),
            ExpiresAt = _clock.GetUtcNow().AddMinutes(30),
        });

        var revoked = await _service.RevokeOtherSessionsAsync(
            first.Account.Id, current.Session.Id, "user_revoked_other_sessions");

        Assert.Equal(1, revoked);
        Assert.Null(await _service.ValidateSessionAsync(first.PlaintextToken));
        Assert.NotNull(await _service.ValidateSessionAsync(current.PlaintextToken));
        Assert.Null(await _accounts.GetAdminUnlockAsync(
            first.Session.Id, first.Account.Id, first.ActiveProfile.Id, _clock.GetUtcNow()));
        using var connection = _database.CreateConnection();
        Assert.Equal(1, connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM identity_audit_events WHERE event_type='other_sessions_revoked' AND succeeded=1;"));
    }

    [Fact]
    public async Task PasswordFailures_LockCredentialAfterFiveRemoteAttempts()
    {
        await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Living room", "Dashboard");

        AuthenticationAttemptResult? attempt = null;
        for (var index = 0; index < 5; index++)
        {
            attempt = await _service.AuthenticatePasswordAsync(
                "owner@example.com", "wrong password", $"browser-{index}", "Unknown", "Dashboard",
                ingress: ClientIngress.Remote);
        }

        Assert.NotNull(attempt);
        Assert.False(attempt.Succeeded);
        Assert.True(attempt.LockedOut);

        var correctWhileLocked = await _service.AuthenticatePasswordAsync(
            "owner@example.com", "correct horse battery staple", "browser-6", "Office", "Dashboard",
            ingress: ClientIngress.Remote);
        Assert.False(correctWhileLocked.Succeeded);
        Assert.True(correctWhileLocked.LockedOut);
    }

    [Theory]
    [InlineData(ClientIngress.HomeNetwork)]
    [InlineData(ClientIngress.ThisComputer)]
    public async Task RemoteLockout_DoesNotBlockHomeSignIn_AndHomeFailuresDoNotCount(string homeIngress)
    {
        await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Living room", "Dashboard");

        for (var index = 0; index < 50; index++)
        {
            await _service.AuthenticatePasswordAsync(
                "owner@example.com", "wrong password", $"attacker-{index}", "Unknown", "Dashboard",
                ingress: ClientIngress.Remote);
        }

        var remote = await _service.AuthenticatePasswordAsync(
            "owner@example.com", "correct horse battery staple", "attacker-x", "Unknown", "Dashboard",
            ingress: ClientIngress.Remote);
        Assert.False(remote.Succeeded);
        Assert.True(remote.LockedOut);

        var home = await _service.AuthenticatePasswordAsync(
            "owner@example.com", "correct horse battery staple", "home-1", "Living room", "Dashboard",
            ingress: homeIngress);
        Assert.True(home.Succeeded);

        // Home failures never count toward the lockout or block home sign-in.
        for (var index = 0; index < 20; index++)
        {
            await _service.AuthenticatePasswordAsync(
                "owner@example.com", "wrong password", $"home-{index}", "Living room", "Dashboard",
                ingress: homeIngress);
        }

        var stillHome = await _service.AuthenticatePasswordAsync(
            "owner@example.com", "correct horse battery staple", "home-final", "Living room", "Dashboard",
            ingress: homeIngress);
        Assert.True(stillHome.Succeeded);
    }

    [Fact]
    public async Task HomeFailures_NeverLockRemoteSignIn()
    {
        await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Living room", "Dashboard");

        for (var index = 0; index < 20; index++)
        {
            await _service.AuthenticatePasswordAsync(
                "owner@example.com", "wrong password", $"home-{index}", "Living room", "Dashboard",
                ingress: ClientIngress.HomeNetwork);
        }

        var remote = await _service.AuthenticatePasswordAsync(
            "owner@example.com", "correct horse battery staple", "away-1", "Phone", "Dashboard",
            ingress: ClientIngress.Remote);
        Assert.True(remote.Succeeded);
    }

    [Fact]
    public async Task LocalOnlyAccount_CanEnterWithoutPinAndRequiresPinAfterOneIsConfigured()
    {
        await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Living room", "Dashboard");
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            DisplayName = "Kids",
            AvatarColor = "#7C4DFF",
            Role = ProfileRole.RestrictedProfile,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await InsertProfileAsync(profile);

        var localAccount = new Account
        {
            Id = Guid.NewGuid(),
            IsLocalOnly = true,
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = _clock.GetUtcNow(),
            UpdatedAt = _clock.GetUtcNow(),
        };
        await _accounts.CreateAccountAsync(
            localAccount,
            new AccountProfileGrant
            {
                AccountId = localAccount.Id,
                ProfileId = profile.Id,
                IsDefault = true,
                IsEnabled = true,
                AuthorizationVersion = 1,
                GrantedAt = _clock.GetUtcNow(),
            },
            new HashSet<MediaEngine.Domain.Authorization.AccountFeatureId>(),
            new HashSet<Guid>());

        var passwordlessLogin = await _service.AuthenticatePinAsync(
            profile.Id, string.Empty, "tablet", "Kids tablet", "Dashboard");
        Assert.True(passwordlessLogin.Succeeded);
        Assert.Equal("ProfileEntry", passwordlessLogin.IssuedSession!.Session.AuthenticationMethod);
        Assert.NotNull(await _service.ValidateSessionAsync(passwordlessLogin.IssuedSession.PlaintextToken));

        await _service.SetProfilePinAsync(profile.Id, "2468");
        Assert.Null(await _service.ValidateSessionAsync(passwordlessLogin.IssuedSession.PlaintextToken));
        Assert.False((await _service.AuthenticatePinAsync(
            profile.Id, "0000", "tablet", "Kids tablet", "Dashboard")).Succeeded);
        var pinLogin = await _service.AuthenticatePinAsync(
            profile.Id, "2468", "tablet", "Kids tablet", "Dashboard");
        Assert.True(pinLogin.Succeeded);
        Assert.Equal("ProfilePin", pinLogin.IssuedSession!.Session.AuthenticationMethod);

        await _service.SetProfilePinAsync(profile.Id, null);
        Assert.Null(await _identities.GetCredentialAsync(profile.Id, ProfileCredentialKind.ProfilePin));
        Assert.Null(await _service.ValidateSessionAsync(pinLogin.IssuedSession.PlaintextToken));
    }

    [Fact]
    public async Task BootstrapAdministrator_BlankDisplayNameDefaultsToAdministrator()
    {
        var bootstrap = await _service.BootstrapAdministratorAsync(
            "administrator@example.com", "correct horse battery staple", "", "browser-1", "Server", "Dashboard");

        Assert.Equal("Administrator", bootstrap.Profile.DisplayName);
    }

    [Fact]
    public async Task SwitchActiveProfileAsync_ReportsPinRequirementSeparatelyFromAccessDenial()
    {
        var bootstrap = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Living room", "Dashboard");
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            DisplayName = "Kids",
            AvatarColor = "#7C4DFF",
            Role = ProfileRole.RestrictedProfile,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await InsertProfileAsync(profile);
        await _accounts.GrantProfileAsync(new AccountProfileGrant
        {
            AccountId = bootstrap.Account.Id,
            ProfileId = profile.Id,
            GrantedAt = DateTimeOffset.UtcNow,
        });
        await _service.SetProfilePinAsync(profile.Id, "2468");

        await Assert.ThrowsAsync<ProfilePinRequiredException>(() =>
            _service.SwitchActiveProfileAsync(bootstrap.PlaintextToken, profile.Id, null));

        var switched = await _service.SwitchActiveProfileAsync(
            bootstrap.PlaintextToken, profile.Id, "2468");
        Assert.Equal(profile.Id, switched.ActiveProfile.Id);
    }

    private async Task<(SessionIssueResult Session, Profile Kid)> SessionWithPinProfileAsync(string ingress)
    {
        var bootstrap = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Living room", "Dashboard",
            ingress: ingress);
        var kid = new Profile
        {
            Id = Guid.NewGuid(),
            DisplayName = "Kids",
            AvatarColor = "#7C4DFF",
            Role = ProfileRole.RestrictedProfile,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await InsertProfileAsync(kid);
        await _accounts.GrantProfileAsync(new AccountProfileGrant
        {
            AccountId = bootstrap.Account.Id,
            ProfileId = kid.Id,
            GrantedAt = DateTimeOffset.UtcNow,
        });
        await _service.SetProfilePinAsync(kid.Id, "2468");
        return (bootstrap, kid);
    }

    [Fact]
    public async Task SwitchProfilePinGuesses_FromARemoteSession_LockAfterFiveFailures()
    {
        var (session, kid) = await SessionWithPinProfileAsync(ClientIngress.Remote);

        for (var index = 0; index < 4; index++)
        {
            await Assert.ThrowsAsync<ProfilePinRequiredException>(() =>
                _service.SwitchActiveProfileAsync(session.PlaintextToken, kid.Id, "0000"));
        }

        await Assert.ThrowsAsync<ProfilePinLockedException>(() =>
            _service.SwitchActiveProfileAsync(session.PlaintextToken, kid.Id, "0000"));
        await Assert.ThrowsAsync<ProfilePinLockedException>(() =>
            _service.SwitchActiveProfileAsync(session.PlaintextToken, kid.Id, "2468"));
    }

    [Fact]
    public async Task SwitchProfilePinGuesses_FromAHomeSession_NeverLock()
    {
        var (session, kid) = await SessionWithPinProfileAsync(ClientIngress.HomeNetwork);

        for (var index = 0; index < 20; index++)
        {
            await Assert.ThrowsAsync<ProfilePinRequiredException>(() =>
                _service.SwitchActiveProfileAsync(session.PlaintextToken, kid.Id, "0000"));
        }

        var switched = await _service.SwitchActiveProfileAsync(session.PlaintextToken, kid.Id, "2468");
        Assert.Equal(kid.Id, switched.ActiveProfile.Id);
    }

    [Fact]
    public async Task SwitchProfilePin_RemoteSuccessResetsTheFailureCount()
    {
        var (session, kid) = await SessionWithPinProfileAsync(ClientIngress.Remote);

        for (var round = 0; round < 3; round++)
        {
            for (var index = 0; index < 4; index++)
            {
                await Assert.ThrowsAsync<ProfilePinRequiredException>(() =>
                    _service.SwitchActiveProfileAsync(session.PlaintextToken, kid.Id, "0000"));
            }

            await _service.SwitchActiveProfileAsync(session.PlaintextToken, kid.Id, "2468");
        }
    }

    [Fact]
    public async Task ProfilePinSignIn_OnlyRemoteFailuresCountOrLock()
    {
        var (_, kid) = await SessionWithPinProfileAsync(ClientIngress.HomeNetwork);
        await InsertLocalOnlyAccountForAsync(kid);

        for (var index = 0; index < 20; index++)
        {
            Assert.False((await _service.AuthenticatePinAsync(
                kid.Id, "0000", $"home-{index}", "Living room", "Dashboard", ingress: ClientIngress.HomeNetwork)).Succeeded);
        }

        Assert.True((await _service.AuthenticatePinAsync(
            kid.Id, "2468", "home-ok", "Living room", "Dashboard", ingress: ClientIngress.HomeNetwork)).Succeeded);

        AuthenticationAttemptResult? last = null;
        for (var index = 0; index < 5; index++)
        {
            last = await _service.AuthenticatePinAsync(
                kid.Id, "0000", $"away-{index}", "Phone", "Dashboard", ingress: ClientIngress.Remote);
        }

        Assert.True(last!.LockedOut);
        Assert.True((await _service.AuthenticatePinAsync(
            kid.Id, "2468", "home-again", "Living room", "Dashboard", ingress: ClientIngress.HomeNetwork)).Succeeded);
        Assert.False((await _service.AuthenticatePinAsync(
            kid.Id, "2468", "away-ok", "Phone", "Dashboard", ingress: ClientIngress.Remote)).Succeeded);
    }

    [Fact]
    public async Task UnspecifiedIngress_IsTreatedAsRemote()
    {
        await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Living room", "Dashboard");

        AuthenticationAttemptResult? last = null;
        for (var index = 0; index < 5; index++)
        {
            last = await _service.AuthenticatePasswordAsync(
                "owner@example.com", "wrong password", $"browser-{index}", "Unknown", "Dashboard");
        }

        Assert.True(last!.LockedOut);
    }

    [Fact]
    public async Task LocalPassword_AllowsEightCharacters()
    {
        var bootstrap = await _service.BootstrapAdministratorAsync(
            "administrator@example.com", "12345678", "Administrator", "browser-1", "Server", "Dashboard");

        Assert.Equal("Administrator", bootstrap.Profile.DisplayName);
    }

    [Fact]
    public async Task LocalPassword_RejectsFewerThanEightCharacters()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.BootstrapAdministratorAsync(
            "administrator@example.com", "1234567", "Administrator", "browser-1", "Server", "Dashboard"));

        Assert.Contains("between 8 and 128 characters", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostAdministratorPasswordReset_RevokesSessionsClearsLockoutAndRotatesRecoveryCodes()
    {
        var bootstrap = await _service.BootstrapAdministratorAsync(
            "administrator@example.com", "correct horse battery staple", "Administrator", "browser-1", "Server", "Dashboard");
        var previousRecoveryCode = bootstrap.RecoveryCodes[0];

        for (var index = 0; index < 5; index++)
        {
            await _service.AuthenticatePasswordAsync(
                "administrator@example.com", "wrong password", $"browser-{index + 2}", "Unknown", "Dashboard");
        }

        var replacementCodes = await _service.ResetAdministratorPasswordFromHostAsync(
            "administrator@example.com", "a newer correct horse battery staple");

        Assert.Equal(10, replacementCodes.Count);
        Assert.DoesNotContain(previousRecoveryCode, replacementCodes);
        Assert.Null(await _service.ValidateSessionAsync(bootstrap.PlaintextToken));

        var oldPassword = await _service.AuthenticatePasswordAsync(
            "administrator@example.com", "correct horse battery staple", "browser-8", "Office", "Dashboard");
        Assert.False(oldPassword.Succeeded);
        Assert.False(oldPassword.LockedOut);

        var newPassword = await _service.AuthenticatePasswordAsync(
            "administrator@example.com", "a newer correct horse battery staple", "browser-9", "Office", "Dashboard");
        Assert.True(newPassword.Succeeded);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.ResetPasswordWithRecoveryCodeAsync(
                "administrator@example.com",
                previousRecoveryCode,
                "yet another correct horse battery staple"));
    }

    [Fact]
    public async Task HostAdministratorPasswordReset_RejectsUnknownEmail()
    {
        await _service.BootstrapAdministratorAsync(
            "administrator@example.com", "correct horse battery staple", "Administrator", "browser-1", "Server", "Dashboard");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.ResetAdministratorPasswordFromHostAsync(
                "somebody-else@example.com",
            "a newer correct horse battery staple"));
    }

    [Fact]
    public async Task AccountProfileGrants_ControlSwitchingAndSwitchingClearsGrantUnlock()
    {
        var bootstrap = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Dad", "browser-1", "Server", "Dashboard");
        await _accounts.SetAdminUnlockAsync(new GrantAdminUnlock
        {
            SessionId = bootstrap.Session.Id,
            AccountId = bootstrap.Account.Id,
            ProfileId = bootstrap.Profile.Id,
            ProtectionVersion = 1,
            Method = "GrantPin",
            GrantedAt = _clock.GetUtcNow(),
            ExpiresAt = _clock.GetUtcNow().AddMinutes(30),
        });
        Assert.NotNull(await _accounts.GetAdminUnlockAsync(
            bootstrap.Session.Id,
            bootstrap.Account.Id,
            bootstrap.Profile.Id,
            _clock.GetUtcNow()));

        var child = new Profile
        {
            Id = Guid.NewGuid(),
            DisplayName = "Child",
            AvatarColor = "#123456",
            Role = ProfileRole.RestrictedProfile,
            CreatedAt = _clock.GetUtcNow(),
        };
        var ungranted = new Profile
        {
            Id = Guid.NewGuid(),
            DisplayName = "Guest",
            AvatarColor = "#654321",
            Role = ProfileRole.StandardUser,
            CreatedAt = _clock.GetUtcNow(),
        };
        await InsertProfileAsync(child);
        await InsertProfileAsync(ungranted);
        await _accounts.GrantProfileAsync(new AccountProfileGrant
        {
            AccountId = bootstrap.Account.Id,
            ProfileId = child.Id,
            GrantedAt = _clock.GetUtcNow(),
        });

        await _service.SwitchActiveProfileAsync(bootstrap.PlaintextToken, child.Id, null);
        await _service.SwitchActiveProfileAsync(bootstrap.PlaintextToken, bootstrap.Profile.Id, null);
        Assert.Null(await _accounts.GetAdminUnlockAsync(
            bootstrap.Session.Id,
            bootstrap.Account.Id,
            bootstrap.Profile.Id,
            _clock.GetUtcNow()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.SwitchActiveProfileAsync(bootstrap.PlaintextToken, ungranted.Id, null));
    }

    [Fact]
    public async Task PasswordResetToken_IsSingleUseAndRevokesExistingSessions()
    {
        var bootstrap = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Server", "Dashboard");
        var token = await _service.BeginPasswordResetAsync("OWNER@example.com");
        Assert.NotNull(token);
        Assert.Null(await _service.BeginPasswordResetAsync("unknown@example.com"));

        await _service.ResetPasswordWithTokenAsync(token!, "replacement password");
        Assert.Null(await _service.ValidateSessionAsync(bootstrap.PlaintextToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.ResetPasswordWithTokenAsync(token!, "another replacement"));
        Assert.True((await _service.AuthenticatePasswordAsync(
            "owner@example.com", "replacement password", "browser-2", "Office", "Dashboard")).Succeeded);
    }

    [Fact]
    public async Task RecoveryCodeRegeneration_RequiresPasswordAndInvalidatesPreviousCodes()
    {
        var bootstrap = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Server", "Dashboard");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.RegenerateRecoveryCodesAsync(bootstrap.Account.Id, "wrong password"));

        var replacement = await _service.RegenerateRecoveryCodesAsync(
            bootstrap.Account.Id, "correct horse battery staple");
        Assert.Equal(10, replacement.Count);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.ResetPasswordWithRecoveryCodeAsync(
                "owner@example.com", bootstrap.RecoveryCodes[0], "replacement password"));
    }

    [Fact]
    public async Task Invitation_IsSingleUseAndCreatesAccountPasswordSession()
    {
        var bootstrap = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Server", "Dashboard");
        const string token = "one-time-invitation-token";
        var invited = new Account
        {
            Id = Guid.NewGuid(),
            Email = "family@example.com",
            NormalizedEmail = "FAMILY@EXAMPLE.COM",
            IsEnabled = true,
            CreatedAt = _clock.GetUtcNow(),
            UpdatedAt = _clock.GetUtcNow(),
        };
        await _accounts.InsertAsync(invited);
        await _accounts.GrantProfileAsync(new AccountProfileGrant
        {
            AccountId = invited.Id,
            ProfileId = bootstrap.Profile.Id,
            IsDefault = true,
            GrantedAt = _clock.GetUtcNow(),
        });
        await _accounts.InsertInvitationAsync(new AccountInvitation
        {
            Id = Guid.NewGuid(),
            AccountId = invited.Id,
            TokenHash = HashToken(token),
            CreatedAt = _clock.GetUtcNow(),
            ExpiresAt = _clock.GetUtcNow().AddDays(7),
        });

        var accepted = await _service.AcceptInvitationAsync(
            token, "family password", "remote-browser", "Family laptop", "Dashboard");
        Assert.Equal(invited.Id, accepted.Account.Id);
        Assert.NotNull(await _service.ValidateSessionAsync(accepted.PlaintextToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.AcceptInvitationAsync(token, "different password", "other", "Other", "Dashboard"));
    }

    private Task InsertLocalOnlyAccountForAsync(Profile profile)
    {
        var localAccount = new Account
        {
            Id = Guid.NewGuid(),
            IsLocalOnly = true,
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = _clock.GetUtcNow(),
            UpdatedAt = _clock.GetUtcNow(),
        };
        return _accounts.CreateAccountAsync(
            localAccount,
            new AccountProfileGrant
            {
                AccountId = localAccount.Id,
                ProfileId = profile.Id,
                IsDefault = true,
                IsEnabled = true,
                AuthorizationVersion = 1,
                GrantedAt = _clock.GetUtcNow(),
            },
            new HashSet<MediaEngine.Domain.Authorization.AccountFeatureId>(),
            new HashSet<Guid>());
    }

    private Task InsertProfileAsync(Profile profile)
    {
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO profiles(id,display_name,avatar_color,avatar_image_path,role,created_at,navigation_config)
            VALUES(@Id,@DisplayName,@AvatarColor,@AvatarImagePath,@Role,@CreatedAt,@NavigationConfig);
            """, new
        {
            profile.Id,
            profile.DisplayName,
            profile.AvatarColor,
            profile.AvatarImagePath,
            Role = profile.Role.ToString(),
            CreatedAt = profile.CreatedAt.ToString("O"),
            profile.NavigationConfig,
        });
        return Task.CompletedTask;
    }

    [Fact]
    public async Task HomeSession_IsRefusedFromOutsideButStillValidAtHome()
    {
        await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Living room", "Dashboard");
        var home = (await _service.AuthenticatePasswordAsync(
            "owner@example.com", "correct horse battery staple", "browser-2", "Office", "Dashboard",
            ingress: ClientIngress.HomeNetwork)).IssuedSession!;

        Assert.Equal(ClientIngress.HomeNetwork, home.Session.IssuedIngress);
        Assert.Null(await _service.ValidateSessionAsync(home.PlaintextToken, currentIngress: ClientIngress.Remote));
        Assert.NotNull(await _service.ValidateSessionAsync(home.PlaintextToken, currentIngress: ClientIngress.HomeNetwork));
        Assert.NotNull(await _service.ValidateSessionAsync(home.PlaintextToken, currentIngress: ClientIngress.ThisComputer));
        // Refusing it from outside did not revoke it.
        Assert.Null((await _identities.GetSessionByTokenHashAsync(HashToken(home.PlaintextToken)))!.RevokedAt);
    }

    [Fact]
    public async Task RemoteSession_StaysValidFromHomeAndFromOutside()
    {
        await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Living room", "Dashboard");
        var remote = (await _service.AuthenticatePasswordAsync(
            "owner@example.com", "correct horse battery staple", "browser-3", "Phone", "Dashboard",
            ingress: ClientIngress.Remote)).IssuedSession!;

        Assert.NotNull(await _service.ValidateSessionAsync(remote.PlaintextToken, currentIngress: ClientIngress.Remote));
        Assert.NotNull(await _service.ValidateSessionAsync(remote.PlaintextToken, currentIngress: ClientIngress.HomeNetwork));
    }

    [Fact]
    public async Task SessionIssuedWithUnknownIngress_IsStoredAsRemote()
    {
        await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Living room", "Dashboard");
        var issued = (await _service.AuthenticatePasswordAsync(
            "owner@example.com", "correct horse battery staple", "browser-4", "Tablet", "Dashboard",
            ingress: "not-a-real-place")).IssuedSession!;

        Assert.Equal(ClientIngress.Remote, issued.Session.IssuedIngress);
    }

    [Fact]
    public void SessionIngressMigration_IsIdempotentAndDefaultsExistingRowsToHome()
    {
        using (var connection = _database.CreateConnection())
        {
            var column = connection.QuerySingle<string>(
                "SELECT dflt_value FROM pragma_table_info('auth_sessions') WHERE name = 'issued_ingress';");
            Assert.Equal("'home_network'", column);
        }

        // Running the startup checks again must not fail or duplicate the column.
        _database.RunStartupChecks();
        using var again = _database.CreateConnection();
        Assert.Equal(1, again.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM pragma_table_info('auth_sessions') WHERE name = 'issued_ingress';"));
    }

    public void Dispose()
    {
        _database.Dispose();
        TryDelete(_databasePath);
        TryDelete($"{_databasePath}-wal");
        TryDelete($"{_databasePath}-shm");
    }

    private static void TryDelete(string path)
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

    private static string HashToken(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class MutableAuthenticationPolicyProvider : IAuthenticationPolicyProvider
    {
        public AuthSettings Settings { get; } = new();
        public AuthSettings GetCurrent() => Settings;
    }
}
