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
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Identity.Tests;

public sealed class FirstPartyIdentityServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_identity_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly IdentityRepository _identities;
    private readonly AccountRepository _accounts;
    private readonly ProfileRepository _profiles;
    private readonly ProfileAccessRepository _profileAccess;
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
        _profileAccess = new ProfileAccessRepository(_database);
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
                "UPDATE accounts SET is_enabled=0 WHERE id=@accountId;",
                new { accountId = Account.SeedAccountId });
            connection.Execute(
                "UPDATE profiles SET is_server_admin=0 WHERE id=(SELECT profile_id FROM accounts WHERE id=@accountId);",
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

        await _profileAccess.SetAdminUnlockAsync(new ProfileAdminUnlock
        {
            SessionId = login.IssuedSession.Session.Id,
            ProfileId = login.IssuedSession.ActiveProfile.Id,
            ProtectionVersion = 1,
            Method = "GrantPin",
            GrantedAt = _clock.GetUtcNow(),
            ExpiresAt = _clock.GetUtcNow().AddMinutes(30),
        });

        Assert.True(await _service.RevokeSessionAsync(login.IssuedSession.Session.Id, "test"));
        Assert.Null(await _service.ValidateSessionAsync(login.IssuedSession.PlaintextToken));
        Assert.Null(await _profileAccess.GetAdminUnlockAsync(login.IssuedSession.Session.Id, login.IssuedSession.ActiveProfile.Id, _clock.GetUtcNow()));
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
    public async Task SessionLimit_BurstOfFreshSignIns_DoesNotRevokeSessionInUse()
    {
        _policy.Settings.MaximumActiveSessions = 20;
        var bootstrap = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Living room", "Dashboard");
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(await _service.ValidateSessionAsync(bootstrap.PlaintextToken));
        _clock.Advance(TimeSpan.FromSeconds(30));

        for (var i = 0; i < 20; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(2));
            var attempt = await _service.AuthenticatePasswordAsync(
                "owner@example.com", "correct horse battery staple", $"browser-{i + 2}", "Burst", "Dashboard");
            Assert.True(attempt.Succeeded);
        }

        Assert.NotNull(await _service.ValidateSessionAsync(bootstrap.PlaintextToken));
        Assert.Equal(20, (await _service.GetSessionsAsync(bootstrap.Account.Id)).Count(session => session.IsActive(_clock.GetUtcNow())));
    }

    [Fact]
    public async Task SessionLimit_RevokesNeverUsedSessionsBeforeIdleUsedOnes()
    {
        _policy.Settings.MaximumActiveSessions = 2;
        var a = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Living room", "Dashboard");
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(await _service.ValidateSessionAsync(a.PlaintextToken));
        _clock.Advance(TimeSpan.FromMinutes(30));
        var b = (await _service.AuthenticatePasswordAsync(
            "owner@example.com", "correct horse battery staple", "browser-2", "Office", "Dashboard")).IssuedSession!;
        _clock.Advance(TimeSpan.FromSeconds(5));
        var c = (await _service.AuthenticatePasswordAsync(
            "owner@example.com", "correct horse battery staple", "browser-3", "Phone", "Dashboard")).IssuedSession!;

        Assert.Null(await _service.ValidateSessionAsync(b.PlaintextToken));
        Assert.NotNull(await _service.ValidateSessionAsync(a.PlaintextToken));
        Assert.NotNull(await _service.ValidateSessionAsync(c.PlaintextToken));
    }

    [Fact]
    public async Task SessionLimit_RevokesIdleUsedSessionBeforeRecentlyActiveOne()
    {
        _policy.Settings.MaximumActiveSessions = 2;
        var a = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Living room", "Dashboard");
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(await _service.ValidateSessionAsync(a.PlaintextToken));
        var b = (await _service.AuthenticatePasswordAsync(
            "owner@example.com", "correct horse battery staple", "browser-2", "Office", "Dashboard")).IssuedSession!;
        _clock.Advance(TimeSpan.FromMinutes(29));
        Assert.NotNull(await _service.ValidateSessionAsync(b.PlaintextToken));
        _clock.Advance(TimeSpan.FromMinutes(1));
        var c = (await _service.AuthenticatePasswordAsync(
            "owner@example.com", "correct horse battery staple", "browser-3", "Phone", "Dashboard")).IssuedSession!;

        Assert.Null(await _service.ValidateSessionAsync(a.PlaintextToken));
        Assert.NotNull(await _service.ValidateSessionAsync(b.PlaintextToken));
        Assert.NotNull(await _service.ValidateSessionAsync(c.PlaintextToken));
    }

    [Fact]
    public async Task SessionLimit_WhenEverySessionIsRecentlyActive_RevokesLeastRecentlySeenAndStillSignsIn()
    {
        _policy.Settings.MaximumActiveSessions = 2;
        var a = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Living room", "Dashboard");
        _clock.Advance(TimeSpan.FromMinutes(1));
        var b = (await _service.AuthenticatePasswordAsync(
            "owner@example.com", "correct horse battery staple", "browser-2", "Office", "Dashboard")).IssuedSession!;
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(await _service.ValidateSessionAsync(a.PlaintextToken));
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.NotNull(await _service.ValidateSessionAsync(b.PlaintextToken));
        _clock.Advance(TimeSpan.FromMinutes(1));

        var attempt = await _service.AuthenticatePasswordAsync(
            "owner@example.com", "correct horse battery staple", "browser-3", "Phone", "Dashboard");

        Assert.True(attempt.Succeeded);
        Assert.Null(await _service.ValidateSessionAsync(a.PlaintextToken));
        Assert.NotNull(await _service.ValidateSessionAsync(b.PlaintextToken));
        Assert.NotNull(await _service.ValidateSessionAsync(attempt.IssuedSession!.PlaintextToken));
    }

    [Fact]
    public async Task ValidateSession_FirstUseMarksSessionUsedImmediately()
    {
        var used = (await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Living room", "Dashboard"));
        var untouched = (await _service.AuthenticatePasswordAsync(
            "owner@example.com", "correct horse battery staple", "browser-2", "Office", "Dashboard")).IssuedSession!;

        Assert.Null((await _identities.GetSessionByIdAsync(used.Session.Id))!.FirstUsedAt);
        Assert.NotNull(await _service.ValidateSessionAsync(used.PlaintextToken));
        Assert.NotNull(await _service.ValidateSessionAsync(untouched.PlaintextToken, touch: false));

        Assert.Equal(_clock.GetUtcNow(), (await _identities.GetSessionByIdAsync(used.Session.Id))!.FirstUsedAt);
        Assert.Null((await _identities.GetSessionByIdAsync(untouched.Session.Id))!.FirstUsedAt);
    }

    [Fact]
    public async Task ValidateSession_RejectedRequest_DoesNotMarkSessionUsed()
    {
        await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Living room", "Dashboard");
        var stale = (await _service.AuthenticatePasswordAsync(
            "owner@example.com", "correct horse battery staple", "browser-2", "Phone", "Dashboard")).IssuedSession!;
        var credential = (await _identities.GetAccountCredentialAsync(stale.Account.Id, AccountCredentialKind.Password))!;
        credential.SecurityStamp = "changed-elsewhere";
        await _identities.UpsertAccountCredentialAsync(credential);
        Assert.Null(await _service.ValidateSessionAsync(stale.PlaintextToken));

        Assert.Null((await _identities.GetSessionByIdAsync(stale.Session.Id))!.FirstUsedAt);
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
            first.Account.Id, "replacement password",
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
        await _profileAccess.SetAdminUnlockAsync(new ProfileAdminUnlock
        {
            SessionId = first.Session.Id,
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
        Assert.Null(await _profileAccess.GetAdminUnlockAsync(first.Session.Id, first.ActiveProfile.Id, _clock.GetUtcNow()));
        using var connection = _database.CreateConnection();
        Assert.Equal(1, connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM identity_audit_events WHERE event_type='other_sessions_revoked' AND succeeded=1;"));
    }

    [Theory]
    [InlineData("ProfileEntry")]
    [InlineData("ProfilePin")]
    public async Task SessionsFromRetiredProfileSignIn_AreNeverAccepted(string retiredMethod)
    {
        var bootstrap = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Living room", "Dashboard");
        const string token = "retired-profile-sign-in-token";
        var now = _clock.GetUtcNow();
        await _identities.InsertSessionAsync(new AuthSession
        {
            Id = Guid.NewGuid(),
            AccountId = bootstrap.Account.Id,
            ActiveProfileId = bootstrap.Profile.Id,
            TokenHash = HashToken(token),
            DeviceId = "tablet",
            DeviceName = "Kids tablet",
            Client = "Dashboard",
            AuthenticationMethod = retiredMethod,
            IssuedIngress = ClientIngress.HomeNetwork,
            SecurityStamp = "profile-entry:none",
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now.AddDays(1),
        });

        Assert.Null(await _service.ValidateSessionAsync(token));
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
        await JoinHouseholdOfAsync(bootstrap.Account.Id, profile.Id);
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
        await JoinHouseholdOfAsync(bootstrap.Account.Id, kid.Id);
        await _service.SetProfilePinAsync(kid.Id, "2468");
        return (bootstrap, kid);
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
    public async Task LocalPassword_AcceptsTwelveCharacters()
    {
        var bootstrap = await _service.BootstrapAdministratorAsync(
            "administrator@example.com", "mossy-anvil2", "Administrator", "browser-1", "Server", "Dashboard");

        Assert.Equal("Administrator", bootstrap.Profile.DisplayName);
    }

    [Fact]
    public async Task LocalPassword_RejectsElevenCharacters()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.BootstrapAdministratorAsync(
            "administrator@example.com", "mossy-anvi1", "Administrator", "browser-1", "Server", "Dashboard"));

        Assert.Contains("Use at least 12 characters, and avoid common passwords or your email address.", exception.Message, StringComparison.Ordinal);
        Assert.False(await _service.IsAdministratorConfiguredAsync());
    }

    [Fact]
    public async Task LocalPassword_RejectsCommonPasswordsCaseInsensitively()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.BootstrapAdministratorAsync(
            "administrator@example.com", "ILoveYou1234", "Administrator", "browser-1", "Server", "Dashboard"));

        Assert.Contains("avoid common passwords", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalPassword_RejectsEmailAddressAsPassword()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.BootstrapAdministratorAsync(
            "administrator@example.com", "Administrator@Example.com", "Administrator", "browser-1", "Server", "Dashboard"));

        Assert.Contains("your email address", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalPassword_RejectsDisplayNameAsPassword()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.BootstrapAdministratorAsync(
            "administrator@example.com", "Living Room Owner", "living room owner", "browser-1", "Server", "Dashboard"));

        Assert.Contains("avoid common passwords", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangePassword_RejectsShortAndCommonPasswords()
    {
        var first = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Living room", "Dashboard");

        await Assert.ThrowsAsync<ArgumentException>(() => _service.ChangePasswordAsync(
            first.Account.Id, "short-pass1"));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.ChangePasswordAsync(
            first.Account.Id, "password1234"));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.ChangePasswordAsync(
            first.Account.Id, "owner@example.com"));

        Assert.True((await _service.AuthenticatePasswordAsync(
            "owner@example.com", "correct horse battery staple", "browser-2", "Office", "Dashboard")).Succeeded);
    }

    [Fact]
    public async Task InvitationAccept_RejectsShortPasswordWithoutConsumingInvitation()
    {
        var bootstrap = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Server", "Dashboard");
        const string token = "KQ7M4XH2TA";
        var (invited, _) = await InsertInvitedAccountAsync("guest@example.com", bootstrap.Profile.Id);
        await _accounts.InsertInvitationAsync(new AccountInvitation
        {
            Id = Guid.NewGuid(),
            AccountId = invited.Id,
            TokenHash = HashToken(token),
            CreatedAt = _clock.GetUtcNow(),
            ExpiresAt = _clock.GetUtcNow().AddDays(7),
        });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.AcceptInvitationAsync(token, "short-pass1", "remote-browser", "Guest laptop", "Dashboard"));

        var accepted = await _service.AcceptInvitationAsync(
            token, "family password", "remote-browser", "Guest laptop", "Dashboard");
        Assert.Equal(invited.Id, accepted.Account.Id);
    }

    [Fact]
    public async Task ExistingEightCharacterPassword_StillSignsIn()
    {
        await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Server", "Dashboard");
        var account = await _accounts.GetByNormalizedEmailAsync("OWNER@EXAMPLE.COM");
        var credential = await _identities.GetAccountCredentialAsync(account!.Id, AccountCredentialKind.Password);
        Assert.NotNull(credential);

        // Simulates a password set before the 12-character rule existed.
        credential.SecretHash = new PasswordHasher<AccountCredential>().HashPassword(credential, $"tuvima:{credential.Kind}:v1\n12345678");
        await _identities.UpsertAccountCredentialAsync(credential);

        var login = await _service.AuthenticatePasswordAsync(
            "owner@example.com", "12345678", "browser-2", "Office", "Dashboard");
        Assert.True(login.Succeeded);
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
    public async Task TheHousehold_ControlsSwitchingAndSwitchingClearsTheAdminUnlock()
    {
        var bootstrap = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Dad", "browser-1", "Server", "Dashboard");
        await _profileAccess.SetAdminUnlockAsync(new ProfileAdminUnlock
        {
            SessionId = bootstrap.Session.Id,
            ProfileId = bootstrap.Profile.Id,
            ProtectionVersion = 1,
            Method = "GrantPin",
            GrantedAt = _clock.GetUtcNow(),
            ExpiresAt = _clock.GetUtcNow().AddMinutes(30),
        });
        Assert.NotNull(await _profileAccess.GetAdminUnlockAsync(bootstrap.Session.Id, bootstrap.Profile.Id, _clock.GetUtcNow()));

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
        await JoinHouseholdOfAsync(bootstrap.Account.Id, child.Id);

        await _service.SwitchActiveProfileAsync(bootstrap.PlaintextToken, child.Id, null);
        await _service.SwitchActiveProfileAsync(bootstrap.PlaintextToken, bootstrap.Profile.Id, null);
        Assert.Null(await _profileAccess.GetAdminUnlockAsync(bootstrap.Session.Id, bootstrap.Profile.Id, _clock.GetUtcNow()));
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
    public async Task RecoveryCodeRegeneration_InvalidatesPreviousCodes()
    {
        var bootstrap = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Server", "Dashboard");

        var replacement = await _service.RegenerateRecoveryCodesAsync(bootstrap.Account.Id);
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
        const string token = "WD5N8RB3CJ";
        var (invited, _) = await InsertInvitedAccountAsync("family@example.com", bootstrap.Profile.Id);
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

    /// <summary>A sign-in that has not chosen a password yet. A user opens one profile of its own, so it gets a new one (<paramref name="profileId"/> is the inviter's, kept for the callers' signature).</summary>
    private async Task<(Account Account, Guid ProfileId)> InsertInvitedAccountAsync(string email, Guid profileId)
    {
        var invited = new Account
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = _clock.GetUtcNow(),
            UpdatedAt = _clock.GetUtcNow(),
        };
        var person = new Profile { Id = Guid.NewGuid(), DisplayName = "Invited", AvatarColor = "#7C4DFF", Role = ProfileRole.StandardUser, CreatedAt = _clock.GetUtcNow() };
        await _accounts.CreateAccountAsync(
            invited,
            new TestGrant(invited.Id, person.Id, IsDefault: true),
            new HashSet<AccountFeatureId>(), new HashSet<Guid>(), newProfile: person);
        return (invited, person.Id);
    }

    [Fact]
    public async Task Invitation_AcceptsTheCodeTypedWithDashesAndLowerCase()
    {
        var bootstrap = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Server", "Dashboard");
        var (invited, _) = await InsertInvitedAccountAsync("typed@example.com", bootstrap.Profile.Id);
        var code = InvitationCode.Generate();
        await _accounts.InsertInvitationAsync(new AccountInvitation
        {
            Id = Guid.NewGuid(),
            AccountId = invited.Id,
            TokenHash = InvitationCode.Hash(code),
            CreatedAt = _clock.GetUtcNow(),
            ExpiresAt = _clock.GetUtcNow().AddDays(7),
        });

        var preview = await _service.PreviewInvitationAsync(InvitationCode.Format(code).ToLowerInvariant());
        Assert.Equal("typed@example.com", preview!.Email);

        var accepted = await _service.AcceptInvitationAsync(
            " " + InvitationCode.Format(code).ToLowerInvariant() + " ", "family password", "remote-browser", "Laptop", "Dashboard");
        Assert.Equal(invited.Id, accepted.Account.Id);
        Assert.Null(await _service.PreviewInvitationAsync(code));
    }

    [Fact]
    public async Task Invitation_ExpiredOrUnknownCodeIsRefused()
    {
        var bootstrap = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Server", "Dashboard");
        var (invited, _) = await InsertInvitedAccountAsync("late@example.com", bootstrap.Profile.Id);
        var code = InvitationCode.Generate();
        await _accounts.InsertInvitationAsync(new AccountInvitation
        {
            Id = Guid.NewGuid(),
            AccountId = invited.Id,
            TokenHash = InvitationCode.Hash(code),
            CreatedAt = _clock.GetUtcNow(),
            ExpiresAt = _clock.GetUtcNow().AddDays(7),
        });

        _clock.Advance(TimeSpan.FromDays(8));

        Assert.Null(await _service.PreviewInvitationAsync(code));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.AcceptInvitationAsync(code, "family password", "d", "D", "Dashboard"));
        Assert.Null(await _service.PreviewInvitationAsync("not a code"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.AcceptInvitationAsync("AAAAA-AAAAA", "family password", "d", "D", "Dashboard"));
    }

    private async Task<Account> CreateTemporaryPasswordAccountAsync(string temporary, TimeSpan lifetime)
    {
        var bootstrap = await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Server", "Dashboard");
        var (account, _) = await InsertInvitedAccountAsync("temp@example.com", bootstrap.Profile.Id);
        await _service.SetTemporaryPasswordAsync(account.Id, temporary, _clock.GetUtcNow().Add(lifetime));
        return account;
    }

    [Fact]
    public async Task TemporaryPassword_SignsIn_AndFlagsTheAccount()
    {
        var account = await CreateTemporaryPasswordAccountAsync("temporary pass 123", TimeSpan.FromDays(7));

        var attempt = await _service.AuthenticatePasswordAsync(
            "temp@example.com", "temporary pass 123", "browser-2", "Laptop", "Dashboard");

        Assert.True(attempt.Succeeded);
        var stored = await _accounts.GetByIdAsync(account.Id);
        Assert.True(stored!.MustChangePassword);
        var validated = await _service.ValidateSessionAsync(attempt.IssuedSession!.PlaintextToken);
        Assert.True(validated!.Account.MustChangePassword);
    }

    [Fact]
    public async Task TemporaryPassword_ChangeClearsTheFlag_RevokesOldSessions_AndIssuesANewOne()
    {
        var account = await CreateTemporaryPasswordAccountAsync("temporary pass 123", TimeSpan.FromDays(7));
        var first = (await _service.AuthenticatePasswordAsync(
            "temp@example.com", "temporary pass 123", "browser-2", "Laptop", "Dashboard")).IssuedSession!;
        var other = (await _service.AuthenticatePasswordAsync(
            "temp@example.com", "temporary pass 123", "browser-3", "Phone", "Dashboard")).IssuedSession!;

        var fresh = await _service.ChangeTemporaryPasswordAsync(
            account.Id, "temporary pass 123", "my own new password", "browser-2", "Laptop", "Dashboard");

        Assert.Null(await _service.ValidateSessionAsync(first.PlaintextToken));
        Assert.Null(await _service.ValidateSessionAsync(other.PlaintextToken));
        var validated = await _service.ValidateSessionAsync(fresh.PlaintextToken);
        Assert.False(validated!.Account.MustChangePassword);
        Assert.Null(validated.Account.TemporaryPasswordExpiresAt);
        Assert.False((await _service.AuthenticatePasswordAsync(
            "temp@example.com", "temporary pass 123", "browser-4", "Tablet", "Dashboard")).Succeeded);
        Assert.True((await _service.AuthenticatePasswordAsync(
            "temp@example.com", "my own new password", "browser-4", "Tablet", "Dashboard")).Succeeded);
    }

    [Fact]
    public async Task TemporaryPassword_ChangeRefusesTheSamePasswordAndShortOnes()
    {
        var account = await CreateTemporaryPasswordAccountAsync("temporary pass 123", TimeSpan.FromDays(7));

        await Assert.ThrowsAsync<ArgumentException>(() => _service.ChangeTemporaryPasswordAsync(
            account.Id, "temporary pass 123", "temporary pass 123", "d", "D", "Dashboard"));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.ChangeTemporaryPasswordAsync(
            account.Id, "temporary pass 123", "short", "d", "D", "Dashboard"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.ChangeTemporaryPasswordAsync(
            account.Id, "wrong current password", "my own new password", "d", "D", "Dashboard"));
    }

    [Fact]
    public async Task TemporaryPassword_ExpiredIsRefusedWithAdministratorMessage()
    {
        var account = await CreateTemporaryPasswordAccountAsync("temporary pass 123", TimeSpan.FromDays(7));
        var session = (await _service.AuthenticatePasswordAsync(
            "temp@example.com", "temporary pass 123", "browser-2", "Laptop", "Dashboard")).IssuedSession!;

        _clock.Advance(TimeSpan.FromDays(8));

        var attempt = await _service.AuthenticatePasswordAsync(
            "temp@example.com", "temporary pass 123", "browser-2", "Laptop", "Dashboard");
        Assert.False(attempt.Succeeded);
        Assert.Equal("Ask your administrator for a new temporary password.", attempt.Error);
        Assert.Null(await _service.ValidateSessionAsync(session.PlaintextToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.ChangeTemporaryPasswordAsync(
            account.Id, "temporary pass 123", "my own new password", "d", "D", "Dashboard"));
    }

    [Fact]
    public async Task TemporaryPassword_AdministratorCanSetAnotherAfterExpiry()
    {
        var account = await CreateTemporaryPasswordAccountAsync("temporary pass 123", TimeSpan.FromDays(7));
        _clock.Advance(TimeSpan.FromDays(8));

        await _service.SetTemporaryPasswordAsync(account.Id, "another temp pass 456", _clock.GetUtcNow().AddDays(7));

        Assert.True((await _service.AuthenticatePasswordAsync(
            "temp@example.com", "another temp pass 456", "browser-2", "Laptop", "Dashboard")).Succeeded);
    }

    [Fact]
    public async Task OrdinaryPassword_SetByAnAdministrator_StoresANormalCredential_ThatNeverExpires()
    {
        var account = await CreateTemporaryPasswordAccountAsync("temporary pass 123", TimeSpan.FromDays(7));
        var oldSession = (await _service.AuthenticatePasswordAsync(
            "temp@example.com", "temporary pass 123", "browser-2", "Laptop", "Dashboard")).IssuedSession!;

        await _service.SetTemporaryPasswordAsync(account.Id, "an ordinary password 456", _clock.GetUtcNow().AddDays(7), mustChange: false);

        var stored = (await _accounts.GetByIdAsync(account.Id))!;
        Assert.False(stored.MustChangePassword);
        Assert.Null(stored.TemporaryPasswordExpiresAt);
        // An administrator-set password still ends every existing sign-in of the account.
        Assert.Null(await _service.ValidateSessionAsync(oldSession.PlaintextToken));

        // It signs in at once with no forced change, and does not lapse the way a temporary one does.
        _clock.Advance(TimeSpan.FromDays(30));
        var attempt = await _service.AuthenticatePasswordAsync(
            "temp@example.com", "an ordinary password 456", "browser-3", "Phone", "Dashboard");
        Assert.True(attempt.Succeeded);
        Assert.False((await _service.ValidateSessionAsync(attempt.IssuedSession!.PlaintextToken))!.Account.MustChangePassword);
        Assert.False((await _service.AuthenticatePasswordAsync(
            "temp@example.com", "temporary pass 123", "browser-4", "Tablet", "Dashboard")).Succeeded);
    }

    [Fact]
    public async Task OrdinaryPassword_StillFollowsThePasswordRules()
    {
        var account = await CreateTemporaryPasswordAccountAsync("temporary pass 123", TimeSpan.FromDays(7));

        await Assert.ThrowsAsync<ArgumentException>(() => _service.SetTemporaryPasswordAsync(
            account.Id, "short", _clock.GetUtcNow().AddDays(7), mustChange: false));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SetTemporaryPasswordAsync(
            account.Id, "temp@example.com", _clock.GetUtcNow().AddDays(7), mustChange: false));
        // Nothing changed: the temporary password still works and is still temporary.
        var stored = (await _accounts.GetByIdAsync(account.Id))!;
        Assert.True(stored.MustChangePassword);
        Assert.True((await _service.AuthenticatePasswordAsync(
            "temp@example.com", "temporary pass 123", "browser-2", "Laptop", "Dashboard")).Succeeded);
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

    /// <summary>Puts a profile into the household of the profile the user opens, so the user may act as it.</summary>
    private Task JoinHouseholdOfAsync(Guid accountId, Guid profileId)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "UPDATE profiles SET household_id = (SELECT p.household_id FROM profiles p JOIN accounts a ON a.profile_id = p.id WHERE a.id = @accountId) WHERE id = @profileId;",
            new { accountId, profileId });
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
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

    [Theory]
    [InlineData(ClientIngress.HomeNetwork)]
    [InlineData(ClientIngress.ThisComputer)]
    public async Task PasswordFailures_LockCredentialAfterFiveAttemptsFromAnywhere(string ingress)
    {
        await _service.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "browser-1", "Living room", "Dashboard");

        AuthenticationAttemptResult? attempt = null;
        for (var index = 0; index < 5; index++)
        {
            attempt = await _service.AuthenticatePasswordAsync(
                "owner@example.com", "wrong password", $"browser-{index}", "Unknown", "Dashboard", ingress: ingress);
        }

        Assert.NotNull(attempt);
        Assert.False(attempt.Succeeded);
        Assert.True(attempt.LockedOut);

        var correctWhileLocked = await _service.AuthenticatePasswordAsync(
            "owner@example.com", "correct horse battery staple", "browser-6", "Office", "Dashboard", ingress: ingress);
        Assert.False(correctWhileLocked.Succeeded);
        Assert.True(correctWhileLocked.LockedOut);
    }
}
