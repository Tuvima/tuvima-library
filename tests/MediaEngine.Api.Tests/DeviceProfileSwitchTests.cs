using System.Reflection;
using System.Security.Claims;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// In-app profile switch for paired devices: the same device moves to another person of its account without
/// re-pairing, the PIN is checked at every switch, and only one token pair per device is ever live.
/// </summary>
public sealed partial class DeviceProfileSwitchTests : IAsyncLifetime
{
    private const string ClientId = "tuvima-phone";
    private const string Pin = "4821";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-device-switch-{Guid.NewGuid():N}.db");
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"tuvima-device-switch-{Guid.NewGuid():N}");
    private readonly Guid _library = Guid.NewGuid();
    private DatabaseConnection _database = null!;
    private ConfigurationDirectoryLoader _configuration = null!;
    private AccountRepository _accounts = null!;
    private ClientAuthorizationRepository _devices = null!;
    private ClientAuthorizationService _authorization = null!;
    private DeviceProfileSwitchService _service = null!;
    private FirstPartyIdentityService _identity = null!;
    private Account _homeOwner = null!;
    private Profile _mary = null!;
    private Profile _jim = null!;
    private Profile _sam = null!;
    private Profile _bea = null!;
    private Guid _phoneId;
    private OAuthTokenResponse _paired = null!;
    private string _access = null!;
    private TestClock _clock = null!;
    private DeviceActionThrottle _throttle = null!;

    public async Task InitializeAsync()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _configuration = new ConfigurationDirectoryLoader(_configPath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        var identities = new IdentityRepository(_database);
        var profiles = new ProfileRepository(_database);
        _identity = new FirstPartyIdentityService(
            identities, _accounts, profiles,
            new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(),
            TimeProvider.System, new ConfigurationAuthenticationPolicyProvider(_configuration));
        var decisions = new RealAdministratorDecisions();
        var mutations = AccountSetupKit.Attach(new AccountAccessMutationService(
            _accounts, new ProfileAccessRepository(_database), identities, profiles, _configuration, decisions, new AllowEvaluator(),
            new NoOpInvalidation(), new NoOpAudit(), _identity, new NoProfilePhotos(), TimeProvider.System, new AlwaysUsableAdministrators(), new AuthenticationPolicyMutationGate()),
            _accounts, new ProfileAccessRepository(_database), _identity);
        _devices = new ClientAuthorizationRepository(_database);
        var applications = new ApplicationRepository(_database);
        _authorization = new ClientAuthorizationService(_devices, _accounts, applications, new PermissionRegistry(), TimeProvider.System);
        _clock = new TestClock(DateTimeOffset.UtcNow);
        _throttle = new DeviceActionThrottle(_clock);
        _service = new DeviceProfileSwitchService(
            _devices, _authorization, _accounts, profiles, _identity, _identity, _throttle, TimeProvider.System);

        var bootstrap = Authority(Guid.NewGuid(), Guid.NewGuid(), serverAdmin: true);
        var serverAccount = await mutations.CreateAsync(bootstrap, NewAccount("server@example.com", administrator: true, "Server"));
        await identities.GiveAsync(serverAccount.Id);
        var serverAdmin = Authority(serverAccount.Id, (await _accounts.GetDefaultProfileIdAsync(serverAccount.Id))!.Value, serverAdmin: true);

        var createdHome = await mutations.CreateAsync(serverAdmin, NewAccount("home@example.com", administrator: false, "Mary"));
        var createdOther = await mutations.CreateAsync(serverAdmin, NewAccount("other@example.com", administrator: false, "Bea"));
        _homeOwner = (await _accounts.GetByIdAsync(createdHome.Id))!;
        // Nothing is hidden from the signed-in person, so the library is visible through its area alone.
        var otherOwner = (await _accounts.GetByIdAsync(createdOther.Id))!;
        var homeHousehold = (await new HouseholdRepository(_database).GetForAccountAsync(_homeOwner.Id))!;
        _mary = (await profiles.GetByIdAsync((await _accounts.GetDefaultProfileIdAsync(_homeOwner.Id))!.Value))!;
        _bea = (await profiles.GetByIdAsync((await _accounts.GetDefaultProfileIdAsync(otherOwner.Id))!.Value))!;
        _jim = await mutations.AddHouseholdPersonAsync(serverAdmin,
            new AddHouseholdPersonCommand(homeHousehold.Id, "Jim", null, false, null));
        _sam = await mutations.AddHouseholdPersonAsync(serverAdmin,
            new AddHouseholdPersonCommand(homeHousehold.Id, "Sam", null, true, null));

        using (var connection = _database.CreateConnection())
        {
            // Each person holds their own access: Jim and Sam are given what Mary has.
            connection.Execute("""
                INSERT OR IGNORE INTO profile_library_hidden (profile_id, library_id, created_at)
                SELECT p.id, g.library_id, g.created_at FROM profile_library_hidden g, profiles p
                WHERE g.profile_id = @mary AND p.id IN (@jim, @sam);
                INSERT OR IGNORE INTO profile_feature_grants (profile_id, feature_id, granted_at)
                SELECT p.id, g.feature_id, g.granted_at FROM profile_feature_grants g, profiles p
                WHERE g.profile_id = @mary AND p.id IN (@jim, @sam);
                """, new { mary = _mary.Id, jim = _jim.Id, sam = _sam.Id });
        }

        _paired = await PairPhoneAsync(applications);
        _phoneId = _paired.DeviceId;
        _access = _paired.AccessToken;
    }

    [Fact]
    public async Task TheList_HoldsOnlyTheAccountsGrantedPeople_WithKidsPinAndActiveFlags()
    {
        await _identity.SetProfilePinAsync(_jim.Id, Pin);

        var list = Assert.IsType<DeviceProfileListDto>((await _service.ListAsync(PhoneAuthority(_mary.Id))).Profiles);

        Assert.Equivalent(new[] { _mary.Id, _jim.Id, _sam.Id }, list.Profiles.Select(profile => profile.ProfileId));
        Assert.DoesNotContain(list.Profiles, profile => profile.ProfileId == _bea.Id);
        Assert.Equal(_mary.Id, Assert.Single(list.Profiles, profile => profile.IsActive).ProfileId);
        Assert.True(list.Profiles.Single(profile => profile.ProfileId == _sam.Id).IsKids);
        Assert.False(list.Profiles.Single(profile => profile.ProfileId == _jim.Id).IsKids);
        Assert.True(list.Profiles.Single(profile => profile.ProfileId == _jim.Id).RequiresPin);
        Assert.False(list.Profiles.Single(profile => profile.ProfileId == _sam.Id).RequiresPin);
        Assert.All(list.Profiles, profile => Assert.Null(profile.AvatarUrl));
        Assert.Equal("Jim", list.Profiles.Single(profile => profile.ProfileId == _jim.Id).DisplayName);
    }

    [Fact]
    public async Task APersonWhoLeftTheHousehold_IsNeitherListedNorSwitchedTo()
    {
        // Who a device may switch to is the household of the signed-in person; moving Jim out of it takes him off the list.
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "UPDATE profiles SET household_id = (SELECT household_id FROM profiles WHERE id = @bea) WHERE id = @profile;",
                new { bea = _bea.Id, profile = _jim.Id });
        }

        var list = Assert.IsType<DeviceProfileListDto>((await _service.ListAsync(PhoneAuthority(_mary.Id))).Profiles);
        var result = await SwitchAsAsync(_mary.Id, _jim.Id, null);

        Assert.DoesNotContain(list.Profiles, profile => profile.ProfileId == _jim.Id);
        Assert.Equal(DeviceProfileSwitchOutcome.ProfileNotAllowed, result.Outcome);
        Assert.Equal(_mary.Id, (await _devices.GetDeviceAsync(_phoneId))!.ProfileId);
    }

    [Fact]
    public async Task APersonWithAPin_NeedsThePin_AndAWrongOneIsRefused()
    {
        await _identity.SetProfilePinAsync(_jim.Id, Pin);

        Assert.Equal(DeviceProfileSwitchOutcome.PinRequired, (await SwitchAsAsync(_mary.Id, _jim.Id, null)).Outcome);
        Assert.Equal(DeviceProfileSwitchOutcome.PinRequired, (await SwitchAsAsync(_mary.Id, _jim.Id, "0000")).Outcome);
        Assert.Equal(_mary.Id, (await _devices.GetDeviceAsync(_phoneId))!.ProfileId);
        Assert.Equal(0, AuditCount());

        var switched = await SwitchAsAsync(_mary.Id, _jim.Id, Pin);

        Assert.Equal(DeviceProfileSwitchOutcome.Switched, switched.Outcome);
        Assert.Equal(_jim.Id, switched.Tokens!.ProfileId);
        Assert.Equal(_jim.Id, (await _devices.GetDeviceAsync(_phoneId))!.ProfileId);
    }

    [Fact]
    public async Task RepeatedWrongPins_LockThePerson_EvenForTheRightPin()
    {
        await _identity.SetProfilePinAsync(_jim.Id, Pin);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            Assert.Equal(DeviceProfileSwitchOutcome.PinRequired, (await SwitchAsAsync(_mary.Id, _jim.Id, "0000")).Outcome);
        }

        Assert.Equal(DeviceProfileSwitchOutcome.Locked, (await SwitchAsAsync(_mary.Id, _jim.Id, "0000")).Outcome);
        Assert.Equal(DeviceProfileSwitchOutcome.Locked, (await SwitchAsAsync(_mary.Id, _jim.Id, Pin)).Outcome);
        Assert.Equal(_mary.Id, (await _devices.GetDeviceAsync(_phoneId))!.ProfileId);
    }

    [Fact]
    public async Task ThePin_IsAskedAtEverySwitch_IncludingTheWayBackFromAKidsProfile()
    {
        await _identity.SetProfilePinAsync(_mary.Id, Pin);
        await _identity.SetProfilePinAsync(_jim.Id, Pin);

        var toKids = await SwitchAsAsync(_mary.Id, _sam.Id, null);
        Assert.Equal(DeviceProfileSwitchOutcome.Switched, toKids.Outcome);

        Assert.Equal(DeviceProfileSwitchOutcome.PinRequired, (await SwitchAsAsync(_sam.Id, _mary.Id, null)).Outcome);
        Assert.Equal(DeviceProfileSwitchOutcome.PinRequired, (await SwitchAsAsync(_sam.Id, _jim.Id, null)).Outcome);
        Assert.Equal(_sam.Id, (await _devices.GetDeviceAsync(_phoneId))!.ProfileId);
        Assert.Equal(DeviceProfileSwitchOutcome.Switched, (await SwitchAsAsync(_sam.Id, _mary.Id, Pin)).Outcome);
    }

    [Fact]
    public async Task AKidsProfileWithoutAPin_CanBeSwitchedTo_OnTheSameDevice()
    {
        var result = await SwitchAsAsync(_mary.Id, _sam.Id, null);

        Assert.Equal(DeviceProfileSwitchOutcome.Switched, result.Outcome);
        var tokens = Assert.IsType<OAuthTokenResponse>(result.Tokens);
        Assert.Equal(_sam.Id, tokens.ProfileId);
        Assert.Equal(_phoneId, tokens.DeviceId);
        Assert.Equal(_paired.Scope, tokens.Scope);
        Assert.NotEqual(_paired.AccessToken, tokens.AccessToken);
        Assert.NotEqual(_paired.RefreshToken, tokens.RefreshToken);
        var identity = Assert.IsType<ClientAccessIdentity>(await _authorization.ValidateAccessTokenAsync(tokens.AccessToken));
        Assert.Equal(_sam.Id, identity.Device.ProfileId);
        Assert.Equal(_phoneId, identity.Device.Id);
        Assert.Equal(_homeOwner.Id, identity.Account.Id);
    }

    [Fact]
    public async Task AfterASwitch_TheOldAccessAndRefreshTokensStopWorking()
    {
        var switched = (await SwitchAsAsync(_mary.Id, _sam.Id, null)).Tokens!;

        Assert.Null(await _authorization.ValidateAccessTokenAsync(_paired.AccessToken));
        var refresh = await _authorization.ExchangeAsync(new OAuthTokenRequest
        {
            GrantType = ClientAuthorizationService.RefreshGrantType,
            ClientId = ClientId,
            RefreshToken = _paired.RefreshToken,
        });
        Assert.Null(refresh.Success);
        Assert.Equal("invalid_grant", refresh.Error!.Error);
        using (var connection = _database.CreateConnection())
        {
            Assert.Equal(2, connection.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM client_tokens WHERE device_id = @device AND revoked_at IS NOT NULL AND revoked_reason = 'profile_switched';",
                new { device = _phoneId }));
            Assert.Equal(2, connection.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM client_tokens WHERE device_id = @device AND revoked_at IS NULL;", new { device = _phoneId }));
        }

        // The new pair works, rotates, and stays on the new person.
        var rotated = await _authorization.ExchangeAsync(new OAuthTokenRequest
        {
            GrantType = ClientAuthorizationService.RefreshGrantType,
            ClientId = ClientId,
            RefreshToken = switched.RefreshToken,
        });
        Assert.Equal(_sam.Id, Assert.IsType<OAuthTokenResponse>(rotated.Success).ProfileId);
        Assert.Equal(_phoneId, rotated.Success!.DeviceId);
    }

    [Fact]
    public async Task ASwitch_WritesAnAuditEntry_WithBothPeople()
    {
        await SwitchAsAsync(_mary.Id, _sam.Id, null);

        using var connection = _database.CreateConnection();
        var row = connection.QuerySingle<AuditRow>(
            """
            SELECT actor_account_id AS ActorAccountId, actor_profile_id AS ActorProfileId, subject_type AS SubjectType,
                   subject_id AS SubjectId, changes_json AS ChangesJson
            FROM authorization_audit_events WHERE event_type = @type;
            """, new { type = DeviceProfileSwitchService.AuditEvent });
        Assert.Equal(_homeOwner.Id, row.ActorAccountId);
        Assert.Equal(_sam.Id, row.ActorProfileId);
        Assert.Equal("device", row.SubjectType);
        Assert.Equal(_phoneId.ToString("D"), row.SubjectId);
        Assert.Contains(_mary.Id.ToString("D"), row.ChangesJson);
        Assert.Contains(_sam.Id.ToString("D"), row.ChangesJson);
        Assert.DoesNotContain(Pin, row.ChangesJson);
    }

    [Fact]
    public async Task APersonFromAnotherHousehold_AndAnUnknownId_AreRefusedTheSameWay()
    {
        var other = await SwitchAsAsync(_mary.Id, _bea.Id, null);
        var unknown = await SwitchAsAsync(_mary.Id, Guid.NewGuid(), null);

        Assert.Equal(DeviceProfileSwitchOutcome.ProfileNotAllowed, other.Outcome);
        Assert.Null(other.Tokens);
        // No existence oracle: an id that does not exist answers exactly like one the account cannot use.
        Assert.Equal(DeviceProfileSwitchOutcome.ProfileNotAllowed, unknown.Outcome);
        Assert.Equal(other.Description, unknown.Description);
        Assert.Equal(_mary.Id, (await _devices.GetDeviceAsync(_phoneId))!.ProfileId);
        Assert.NotNull(await _authorization.ValidateAccessTokenAsync(_paired.AccessToken));
        Assert.Equal(0, AuditCount());
    }

    [Fact]
    public async Task OnlyThePairedAppOfALiveDevice_MaySwitch()
    {
        var browser = new RequestAuthority(PrincipalKind.Human, true, _homeOwner.Id, _mary.Id, DeviceId: _phoneId);
        var otherAccount = new RequestAuthority(
            PrincipalKind.DelegatedUserClient, true, Guid.NewGuid(), _bea.Id, DeviceId: _phoneId);
        var service = new RequestAuthority(PrincipalKind.ServiceApplication, true, null, null, DeviceId: _phoneId);
        var tokenId = await TokenIdAsync(_access);

        foreach (var caller in new[] { browser, otherAccount, service })
        {
            Assert.Null((await _service.ListAsync(caller)).Profiles);
            Assert.Equal(DeviceProfileSwitchOutcome.Forbidden, (await _service.SwitchAsync(caller, tokenId, _sam.Id, null)).Outcome);
        }

        Assert.True(await _devices.RevokeDeviceByIdAsync(_phoneId, DateTimeOffset.UtcNow, "test"));
        Assert.Null((await _service.ListAsync(PhoneAuthority(_mary.Id))).Profiles);
        Assert.Equal(DeviceProfileSwitchOutcome.Forbidden, (await _service.SwitchAsync(PhoneAuthority(_mary.Id), tokenId, _sam.Id, null)).Outcome);
        Assert.Equal(0, AuditCount());
    }

    [Fact]
    public async Task ADisabledAccount_CannotSwitch_AndGetsNoTokens()
    {
        var tokenId = await TokenIdAsync(_access);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE accounts SET is_enabled = 0 WHERE id = @account;", new { account = _homeOwner.Id });
        }

        var result = await _service.SwitchAsync(PhoneAuthority(_mary.Id), tokenId, _sam.Id, null);

        Assert.Equal(DeviceProfileSwitchOutcome.DeviceUnavailable, result.Outcome);
        Assert.Null(result.Tokens);
        Assert.Equal(_mary.Id, (await _devices.GetDeviceAsync(_phoneId))!.ProfileId);
        Assert.Equal(0, AuditCount());
    }

    [Fact]
    public async Task AKidsProfileToken_CannotOpenAnRRatedDetailsPage_OrItsFile()
    {
        var family = await InsertOwnedMovieAsync("Family film", "PG");
        var adult = await InsertOwnedMovieAsync("Adult film", "R");
        var maryContext = ContextFor(Assert.IsType<ClientAccessIdentity>(await _authorization.ValidateAccessTokenAsync(_paired.AccessToken)));
        var resources = CreateResourceService(maryContext);

        // Mary has no limit: both open.
        Assert.NotEmpty(await resources.GetAuthorizedAssetIdsForWorkAsync(maryContext, adult.WorkId, _mary.Id, ApplicationPermissionIds.LibraryRead));

        var kidsTokens = (await SwitchAsAsync(_mary.Id, _sam.Id, null)).Tokens!;
        var kidsContext = ContextFor(Assert.IsType<ClientAccessIdentity>(await _authorization.ValidateAccessTokenAsync(kidsTokens.AccessToken)));
        resources = CreateResourceService(kidsContext);

        // The details page asks for exactly this list and answers 404 when it is empty.
        Assert.Empty(await resources.GetAuthorizedAssetIdsForWorkAsync(kidsContext, adult.WorkId, _sam.Id, ApplicationPermissionIds.LibraryRead));
        Assert.Equal(CatalogueResourceAccess.Denied, await resources.EvaluateAssetAsync(kidsContext, adult.AssetId, ApplicationPermissionIds.PlaybackRead));
        Assert.Equal([family.AssetId], await resources.GetAuthorizedAssetIdsForWorkAsync(kidsContext, family.WorkId, _sam.Id, ApplicationPermissionIds.LibraryRead));
    }

    [Fact]
    public async Task TwoSwitchesAtOnce_OnlyOneSucceeds_AndTheOtherLeavesNothingBehind()
    {
        var tokenId = await TokenIdAsync(_access);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(index =>
            SwitchWithTokenAsync(index % 2 == 0 ? _sam.Id : _jim.Id, tokenId)));

        var switched = Assert.Single(results, result => result.Outcome == DeviceProfileSwitchOutcome.Switched);
        Assert.All(results.Where(result => result.Outcome != DeviceProfileSwitchOutcome.Switched),
            result => Assert.Equal(DeviceProfileSwitchOutcome.DeviceUnavailable, result.Outcome));
        Assert.Equal(switched.Tokens!.ProfileId, (await _devices.GetDeviceAsync(_phoneId))!.ProfileId);
        Assert.Equal(1, AuditCount());
        using var connection = _database.CreateConnection();
        Assert.Equal(2, connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM client_tokens WHERE device_id = @device AND revoked_at IS NULL;", new { device = _phoneId }));
    }

    [Fact]
    public async Task ARevokedCallerToken_CannotSwitch_EvenIfTheDeviceIsStillOnItsProfile()
    {
        var tokenId = await TokenIdAsync(_access);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE client_tokens SET revoked_at = @now WHERE id = @id;", new { id = tokenId, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        var result = await _service.SwitchAsync(PhoneAuthority(_mary.Id), tokenId, _sam.Id, null);

        Assert.Equal(DeviceProfileSwitchOutcome.DeviceUnavailable, result.Outcome);
        Assert.Equal(_mary.Id, (await _devices.GetDeviceAsync(_phoneId))!.ProfileId);
        Assert.Equal(0, AuditCount());
    }

    [Fact]
    public async Task TheAuditFromProfile_IsTheOneTheCallerAuthenticatedAs()
    {
        await SwitchAsAsync(_mary.Id, _sam.Id, null);
        await SwitchAsAsync(_sam.Id, _jim.Id, null);

        using var connection = _database.CreateConnection();
        var changes = connection.Query<string>(
            "SELECT changes_json FROM authorization_audit_events WHERE event_type = @type ORDER BY id;",
            new { type = DeviceProfileSwitchService.AuditEvent }).ToList();
        Assert.Equal(2, changes.Count);
        Assert.Contains($"\"from_profile_id\":\"{_mary.Id:D}\"", changes[0]);
        Assert.Contains($"\"to_profile_id\":\"{_sam.Id:D}\"", changes[0]);
        Assert.Contains($"\"from_profile_id\":\"{_sam.Id:D}\"", changes[1]);
        Assert.Contains($"\"to_profile_id\":\"{_jim.Id:D}\"", changes[1]);
    }

    [Fact]
    public async Task AStaleCaller_WhoseProfileIsNoLongerTheDevices_IsRefused()
    {
        await SwitchAsAsync(_mary.Id, _sam.Id, null);
        var staleTokenId = await TokenIdAsync(_access);

        // The device is on Sam; a caller that still believes it is Mary must not switch it.
        var result = await _service.SwitchAsync(PhoneAuthority(_mary.Id), staleTokenId, _jim.Id, null);

        Assert.Equal(DeviceProfileSwitchOutcome.DeviceUnavailable, result.Outcome);
        Assert.Equal(_sam.Id, (await _devices.GetDeviceAsync(_phoneId))!.ProfileId);
        Assert.Equal(1, AuditCount());
    }

    [Fact]
    public async Task AFailedAuditWrite_RollsTheWholeSwitchBack()
    {
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("ALTER TABLE authorization_audit_events RENAME TO authorization_audit_events_hidden;");
        }

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => SwitchAsAsync(_mary.Id, _sam.Id, null));
        }
        finally
        {
            using var connection = _database.CreateConnection();
            connection.Execute("ALTER TABLE authorization_audit_events_hidden RENAME TO authorization_audit_events;");
        }

        Assert.Equal(_mary.Id, (await _devices.GetDeviceAsync(_phoneId))!.ProfileId);
        Assert.NotNull(await _authorization.ValidateAccessTokenAsync(_paired.AccessToken));
        Assert.Equal(0, AuditCount());
    }

    [Fact]
    public async Task ParallelWrongPins_AllCount_AndLockThePerson()
    {
        await _identity.SetProfilePinAsync(_jim.Id, Pin);
        await _identity.SetProfilePinAsync(_mary.Id, Pin);

        // Fewer than the limit: every one of them is counted (a read-then-overwrite count would end near 1), nothing is locked.
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => AttemptAsync(_jim.Id)));
        Assert.Equal(4, FailedPinCount(_jim.Id));
        Assert.Null(PinLockedUntil(_jim.Id));

        // More than the limit: the person is locked and the count reached the limit. Attempts that start after the
        // lock is visible are turned away without counting, so only the lower bound is fixed.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => AttemptAsync(_mary.Id)));
        Assert.InRange(FailedPinCount(_mary.Id), 5, 8);
        Assert.NotNull(PinLockedUntil(_mary.Id));
        await Assert.ThrowsAsync<ProfilePinLockedException>(() => _identity.VerifyProfilePinAsync(_mary.Id, Pin, true));

        async Task AttemptAsync(Guid profileId)
        {
            try
            {
                await _identity.VerifyProfilePinAsync(profileId, "0000", true);
            }
            catch (UnauthorizedAccessException)
            {
                // A wrong PIN, or the lock it triggers, is the expected answer; only the stored count matters here.
            }
        }
    }

    [Fact]
    public async Task SequentialWrongPins_StillLockOnTheFifth_AndARightPinResetsTheCount()
    {
        await _identity.SetProfilePinAsync(_jim.Id, Pin);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _identity.VerifyProfilePinAsync(_jim.Id, "0000", true));
            Assert.Equal(attempt, FailedPinCount(_jim.Id));
        }

        await _identity.VerifyProfilePinAsync(_jim.Id, Pin, true);
        Assert.Equal(0, FailedPinCount(_jim.Id));
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _identity.VerifyProfilePinAsync(_jim.Id, "0000", true));
        }

        await Assert.ThrowsAsync<ProfilePinLockedException>(() => _identity.VerifyProfilePinAsync(_jim.Id, "0000", true));
    }

    [Fact]
    public async Task OverHttp_AWrongPinIs428_ABrowserWithADeviceIdIs403_AndAPairedBearerGetsTokens()
    {
        await _identity.SetProfilePinAsync(_jim.Id, Pin);
        using var app = BuildHttpApplication();
        var pairedIdentity = Assert.IsType<ClientAccessIdentity>(await _authorization.ValidateAccessTokenAsync(_access));
        var pairedClaims = ContextFor(pairedIdentity).User.Clone();
        pairedClaims.AddIdentity(new ClaimsIdentity([new Claim(TuvimaClaimTypes.TokenId, pairedIdentity.Token.Id.ToString("D"))]));

        var missing = await PostSwitchAsync(app, pairedClaims, _jim.Id, null);
        var wrong = await PostSwitchAsync(app, pairedClaims, _jim.Id, "0000");
        var browser = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(TuvimaClaimTypes.PrincipalKind, PrincipalKind.Human.ToString()),
            new Claim(TuvimaClaimTypes.AccountId, _homeOwner.Id.ToString("D")),
            new Claim(TuvimaClaimTypes.ActiveProfileId, _mary.Id.ToString("D")),
            new Claim(TuvimaClaimTypes.DeviceId, _phoneId.ToString("D")),
            new Claim(TuvimaClaimTypes.TokenId, pairedIdentity.Token.Id.ToString("D")),
        ], "test"));
        var forbidden = await PostSwitchAsync(app, browser, _sam.Id, null);
        var notAllowed = await PostSwitchAsync(app, pairedClaims, _bea.Id, null);
        var unknown = await PostSwitchAsync(app, pairedClaims, Guid.NewGuid(), null);
        var ok = await PostSwitchAsync(app, pairedClaims, _jim.Id, Pin);
        var listForBrowser = await GetProfilesAsync(app, browser);

        Assert.Equal(StatusCodes.Status428PreconditionRequired, missing.Status);
        Assert.Contains("\"error\":\"pin_required\"", missing.Body);
        Assert.Equal(StatusCodes.Status428PreconditionRequired, wrong.Status);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.Status);
        Assert.Contains("\"error\":\"forbidden\"", forbidden.Body);
        Assert.Equal(StatusCodes.Status403Forbidden, notAllowed.Status);
        Assert.Contains("\"error\":\"profile_not_allowed\"", notAllowed.Body);
        Assert.Equal(notAllowed.Status, unknown.Status);
        Assert.Equal(notAllowed.Body, unknown.Body);
        Assert.Equal(StatusCodes.Status200OK, ok.Status);
        Assert.Contains($"\"profile_id\":\"{_jim.Id:D}\"", ok.Body);
        Assert.Contains($"\"device_id\":\"{_phoneId:D}\"", ok.Body);
        Assert.Equal(StatusCodes.Status403Forbidden, listForBrowser.Status);
    }

    [Fact]
    public async Task ABurstOfParallelGuesses_NeverCountsMoreThanTheLimit_AndALockedPersonRefusesEvenTheRightPin()
    {
        await _identity.SetProfilePinAsync(_jim.Id, Pin);

        // From fresh: 20 guesses at once, one of them right. At most 5 attempts are ever reserved in a cycle.
        var burst = Enumerable.Range(0, 20).Select(index => VerifyAsync(_jim.Id, index == 7 ? Pin : "0000")).ToArray();
        var outcomes = await Task.WhenAll(burst);

        var successes = outcomes.Count(outcome => outcome == "ok");
        var count = FailedPinCount(_jim.Id);
        var lockedUntilAfterBurst = PinLockedUntil(_jim.Id);
        Assert.True(successes <= 1);
        Assert.InRange(count, 0, 5);
        // Count and lock always agree: a lock exists exactly when the limit's worth of attempts is used up.
        Assert.Equal(count == 5, lockedUntilAfterBurst is not null);
        if (successes == 0)
        {
            // Five attempts were reserved in all: the fifth wrong one locked, the rest of the burst was refused. When the
            // right guess held one of the five, its reset found the lock already set and was refused, so only three
            // wrong guesses were told "wrong PIN"; otherwise four were.
            var required = outcomes.Count(outcome => outcome == "required");
            Assert.InRange(required, 3, 4);
            Assert.Equal(20 - required, outcomes.Count(outcome => outcome == "locked"));
            Assert.Equal(5, count);
            Assert.NotNull(lockedUntilAfterBurst);
        }

        // Bring Mary to the limit one guess at a time: the fifth wrong guess locks her.
        await _identity.SetProfilePinAsync(_mary.Id, Pin);
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            Assert.Equal("required", await VerifyAsync(_mary.Id, "0000"));
        }

        Assert.Equal("locked", await VerifyAsync(_mary.Id, "0000"));
        var lockedUntil = PinLockedUntil(_mary.Id);
        Assert.NotNull(lockedUntil);

        // Locked: a burst with the right PIN in it (ten times) can neither succeed nor clear the lock or the count.
        var locked = await Task.WhenAll(Enumerable.Range(0, 20).Select(index => VerifyAsync(_mary.Id, index % 2 == 0 ? Pin : "0000")));

        Assert.All(locked, outcome => Assert.Equal("locked", outcome));
        Assert.Equal(lockedUntil, PinLockedUntil(_mary.Id));
        Assert.Equal(5, FailedPinCount(_mary.Id));
    }

    [Fact]
    public async Task AnExhaustedPerson_WithNoLockWritten_IsRefusedEvenForTheRightPin_AndLockedAtOnce()
    {
        await _identity.SetProfilePinAsync(_jim.Id, Pin);
        using (var connection = _database.CreateConnection())
        {
            // The state a guess that never finished leaves behind: the limit's worth of attempts used, no lock.
            connection.Execute(
                "UPDATE profile_credentials SET failed_attempt_count = 5, locked_until = NULL WHERE profile_id = @profileId;",
                new { profileId = _jim.Id });
        }

        Assert.Equal("locked", await VerifyAsync(_jim.Id, Pin));

        Assert.NotNull(PinLockedUntil(_jim.Id));
        Assert.Equal(5, FailedPinCount(_jim.Id));
        Assert.Equal("locked", await VerifyAsync(_jim.Id, Pin));
    }

    [Fact]
    public async Task CancellingAfterTheAttemptIsCounted_StillFinishesIt()
    {
        await _identity.SetProfilePinAsync(_jim.Id, Pin);
        var profiles = new ProfileRepository(_database);
        using var cancel = new CancellationTokenSource();
        var cancellingIdentities = CancelAfterReserve.Wrap(new IdentityRepository(_database), cancel);
        var identity = new FirstPartyIdentityService(
            cancellingIdentities, _accounts, profiles,
            new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(),
            TimeProvider.System, new ConfigurationAuthenticationPolicyProvider(_configuration));

        // A right guess whose request is cancelled once the attempt is counted still clears the count.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _identity.VerifyProfilePinAsync(_jim.Id, "0000", true));
        }

        await identity.VerifyProfilePinAsync(_jim.Id, Pin, true, cancel.Token);
        Assert.True(cancel.IsCancellationRequested);
        Assert.Equal(0, FailedPinCount(_jim.Id));

        // A wrong fifth guess whose request is cancelled once the attempt is counted still writes the lock.
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await Assert.ThrowsAsync<ProfilePinRequiredException>(() => _identity.VerifyProfilePinAsync(_jim.Id, "0000", true));
        }

        using var second = new CancellationTokenSource();
        var secondIdentity = new FirstPartyIdentityService(
            CancelAfterReserve.Wrap(new IdentityRepository(_database), second), _accounts, profiles,
            new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(),
            TimeProvider.System, new ConfigurationAuthenticationPolicyProvider(_configuration));
        await Assert.ThrowsAsync<ProfilePinLockedException>(() => secondIdentity.VerifyProfilePinAsync(_jim.Id, "0000", true, second.Token));
        Assert.True(second.IsCancellationRequested);
        Assert.Equal(5, FailedPinCount(_jim.Id));
        Assert.NotNull(PinLockedUntil(_jim.Id));
    }

    [Fact]
    public async Task AfterTheLockRunsOut_TheFirstMissCountsAsOne_AndARightPinStillWorks()
    {
        await _identity.SetProfilePinAsync(_jim.Id, Pin);
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await VerifyAsync(_jim.Id, "0000");
        }

        Assert.Equal("locked", await VerifyAsync(_jim.Id, Pin));
        ExpireThePinLock(_jim.Id);

        Assert.Equal("required", await VerifyAsync(_jim.Id, "0000"));
        Assert.Equal(1, FailedPinCount(_jim.Id));
        Assert.Null(PinLockedUntil(_jim.Id));

        ExpireThePinLock(_jim.Id);
        Assert.Equal("ok", await VerifyAsync(_jim.Id, Pin));
        Assert.Equal(0, FailedPinCount(_jim.Id));
    }

    [Fact]
    public async Task TheFifthGuessCanStillSucceed_WhenTheFirstFourWereWrong()
    {
        await _identity.SetProfilePinAsync(_jim.Id, Pin);
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            Assert.Equal("required", await VerifyAsync(_jim.Id, "0000"));
        }

        Assert.Equal("ok", await VerifyAsync(_jim.Id, Pin));
        Assert.Equal(0, FailedPinCount(_jim.Id));
        Assert.Null(PinLockedUntil(_jim.Id));
    }

    [Fact]
    public async Task SwitchingToTheProfileInUse_IsRefusedWith409_BeforeAnyPinCheck()
    {
        await _identity.SetProfilePinAsync(_mary.Id, Pin);

        var same = await SwitchAsAsync(_mary.Id, _mary.Id, null);

        Assert.Equal(DeviceProfileSwitchOutcome.AlreadyActive, same.Outcome);
        Assert.Null(same.Tokens);
        Assert.Equal(0, FailedPinCount(_mary.Id));
        Assert.Equal(0, AuditCount());
        Assert.NotNull(await _authorization.ValidateAccessTokenAsync(_paired.AccessToken));
        Assert.Equal(_mary.Id, (await _devices.GetDeviceAsync(_phoneId))!.ProfileId);
    }

    [Fact]
    public async Task TheAllowance_FollowsTheDeviceAcrossTokenRotation()
    {
        // Every success mints a new token; the allowance must not restart with it.
        var switches = 0;
        var current = _mary.Id;
        DeviceProfileSwitchResult result;
        do
        {
            var next = current == _mary.Id ? _sam.Id : _mary.Id;
            result = await SwitchAsAsync(current, next, null);
            if (result.Outcome == DeviceProfileSwitchOutcome.Switched)
            {
                switches++;
                current = next;
            }
        }
        while (result.Outcome == DeviceProfileSwitchOutcome.Switched && switches < DeviceActionThrottle.PermitLimit + 5);

        Assert.Equal(DeviceActionThrottle.PermitLimit, switches);
        Assert.Equal(DeviceProfileSwitchOutcome.RateLimited, result.Outcome);
        Assert.True(result.RetryAfterSeconds is >= 1 and <= 60);
        Assert.Equal(current, (await _devices.GetDeviceAsync(_phoneId))!.ProfileId);
        var list = await _service.ListAsync(PhoneAuthority(current));
        Assert.Null(list.Profiles);
        Assert.Equal(DeviceProfileSwitchOutcome.RateLimited, list.Failure);

        _clock.Advance(TimeSpan.FromSeconds(61));
        Assert.NotNull((await _service.ListAsync(PhoneAuthority(current))).Profiles);
    }

    [Fact]
    public async Task CallersThatAreNotAVerifiedDevice_AllocateNoThrottleEntry()
    {
        var browser = new RequestAuthority(PrincipalKind.Human, true, _homeOwner.Id, _mary.Id, DeviceId: _phoneId);
        var unknownDevice = new RequestAuthority(
            PrincipalKind.DelegatedUserClient, true, _homeOwner.Id, _mary.Id, DeviceId: Guid.NewGuid());
        var otherAccount = new RequestAuthority(
            PrincipalKind.DelegatedUserClient, true, Guid.NewGuid(), _bea.Id, DeviceId: _phoneId);

        for (var index = 0; index < 100; index++)
        {
            foreach (var caller in new[] { browser, unknownDevice, otherAccount })
            {
                Assert.Null((await _service.ListAsync(caller)).Profiles);
                Assert.Equal(DeviceProfileSwitchOutcome.Forbidden, (await _service.SwitchAsync(caller, Guid.NewGuid(), _sam.Id, null)).Outcome);
            }
        }

        Assert.Equal(0, _throttle.TrackedDevices);
        await _service.ListAsync(PhoneAuthority(_mary.Id));
        Assert.Equal(1, _throttle.TrackedDevices);
    }

    [Fact]
    public async Task OverHttp_TheCurrentProfileIs409_AndTheAllowanceIs429WithRetryAfter()
    {
        using var app = BuildHttpApplication();
        var identity = Assert.IsType<ClientAccessIdentity>(await _authorization.ValidateAccessTokenAsync(_access));
        var user = ContextFor(identity).User.Clone();
        user.AddIdentity(new ClaimsIdentity([new Claim(TuvimaClaimTypes.TokenId, identity.Token.Id.ToString("D"))]));

        var same = await PostSwitchAsync(app, user, _mary.Id, null);
        Assert.Equal(StatusCodes.Status409Conflict, same.Status);
        Assert.Contains("\"error\":\"already_active_profile\"", same.Body);

        // Rejected calls count too, so the allowance cannot be probed for free.
        (int Status, string Body, string? RetryAfter) limited = (0, "", null);
        for (var call = 1; call <= DeviceActionThrottle.PermitLimit; call++)
        {
            limited = await PostSwitchWithHeadersAsync(app, user, _mary.Id, null);
        }

        Assert.Equal(StatusCodes.Status429TooManyRequests, limited.Status);
        Assert.Contains("\"error\":\"rate_limited\"", limited.Body);
        Assert.False(string.IsNullOrEmpty(limited.RetryAfter));
    }

    private async Task<string> VerifyAsync(Guid profileId, string pin)
    {
        try
        {
            await Task.Run(() => _identity.VerifyProfilePinAsync(profileId, pin, true));
            return "ok";
        }
        catch (ProfilePinLockedException)
        {
            return "locked";
        }
        catch (ProfilePinRequiredException)
        {
            return "required";
        }
    }

    private void ExpireThePinLock(Guid profileId)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "UPDATE profile_credentials SET locked_until = @past WHERE profile_id = @profileId AND locked_until IS NOT NULL;",
            new { profileId, past = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O") });
    }

    private async Task<(int Status, string Body, string? RetryAfter)> PostSwitchWithHeadersAsync(
        WebApplication app, ClaimsPrincipal user, Guid profileId, string? pin)
    {
        var (status, body, headers) = await InvokeSwitchAsync(app, user, profileId, pin);
        return (status, body, headers.RetryAfter.ToString());
    }

    private int FailedPinCount(Guid profileId)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<int>(
            "SELECT failed_attempt_count FROM profile_credentials WHERE profile_id = @profileId;", new { profileId });
    }

    private string? PinLockedUntil(Guid profileId)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<string?>(
            "SELECT locked_until FROM profile_credentials WHERE profile_id = @profileId;", new { profileId });
    }

    private Task<DeviceProfileSwitchResult> SwitchWithTokenAsync(Guid target, Guid tokenId) =>
        Task.Run(() => _service.SwitchAsync(PhoneAuthority(_mary.Id), tokenId, target, null));

    private WebApplication BuildHttpApplication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(_ => { });
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new RequestAuthorityResolver(
            _accounts, new ApplicationRepository(_database)));
        builder.Services.AddSingleton(_service);
        builder.Services.AddSingleton(_authorization);
        builder.Services.AddSingleton<IConfigurationLoader>(_ => null!);
        builder.Services.AddSingleton<IFirstPartyIdentityService>(_identity);
        // Other actions in the group take these; they are never reached here, but route building must be able to infer them.
        builder.Services.AddSingleton<MediaEngine.Api.Services.Security.SecureAccountGate>(_ => null!);
        builder.Services.AddSingleton<MediaEngine.Api.Services.Security.RecentSignInGuard>(_ => null!);
        builder.Services.AddSingleton<PhoneBackupProfileService>(_ => null!);
        // The offline download routes: the real access checks, so a Kids profile and a foreign variant are judged for real.
        var downloadAccessor = new HttpContextAccessor();
        var downloadDecisions = new AccountAccessDecisionService(
            new ProfileAccessRepository(_database),
            new GrantAdminUnlockService(new ProfileAccessRepository(_database), new StubProfilePins(), TimeProvider.System),
            new LibraryVisibilityRule(new ProfileAccessRepository(_database), new VaultLibraryRepository(_database)));
        var downloadApplications = new ApplicationRepository(_database);
        var downloadEvaluator = new AuthorizationEvaluator(
            downloadDecisions, downloadApplications, new PermissionRegistry(), new AuthorizationAuditWriter(_accounts), downloadAccessor);
        builder.Services.AddSingleton<IHttpContextAccessor>(downloadAccessor);
        builder.Services.AddSingleton<IAuthorizationEvaluator>(downloadEvaluator);
        builder.Services.AddSingleton(new CatalogueResourceAuthorizationService(
            _database,
            new RequestAuthorityResolver(_accounts, downloadApplications),
            downloadDecisions,
            downloadEvaluator));
        builder.Services.AddSingleton(new MediaEngine.Api.Services.Playback.DeviceDownloadService(
            _devices, new MediaEngine.Storage.Playback.DeviceDownloadRepository(_database), TimeProvider.System));
        var app = builder.Build();
        app.MapClientAuthorizationEndpoints();
        return app;
    }

    private static IEnumerable<RouteEndpoint> EndpointsFor(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>();

    private static async Task<(int Status, string Body)> PostSwitchAsync(WebApplication app, ClaimsPrincipal user, Guid profileId, string? pin)
    {
        var (status, body, _) = await InvokeSwitchAsync(app, user, profileId, pin);
        return (status, body);
    }

    private static async Task<(int Status, string Body, IHeaderDictionary Headers)> InvokeSwitchAsync(
        WebApplication app, ClaimsPrincipal user, Guid profileId, string? pin)
    {
        var endpoint = Assert.Single(EndpointsFor(app), candidate =>
            candidate.RoutePattern.RawText == "/api/v1/devices/current/switch-profile" &&
            candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("POST") == true);
        var request = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new SwitchDeviceProfileRequest { ProfileId = profileId, Pin = pin });
        var body = new MemoryStream();
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            User = user,
            Request =
            {
                Method = "POST",
                ContentType = "application/json",
                ContentLength = request.Length,
                Body = new MemoryStream(request),
            },
            Response = { Body = body },
        };
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetectionFeature());
        await endpoint.RequestDelegate!(context);
        body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(body).ReadToEndAsync(), context.Response.Headers);
    }

    private static async Task<(int Status, string Body)> GetProfilesAsync(WebApplication app, ClaimsPrincipal user)
    {
        var endpoint = Assert.Single(EndpointsFor(app), candidate => candidate.RoutePattern.RawText == "/api/v1/devices/current/profiles");
        var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = app.Services, User = user, Response = { Body = body } };
        await endpoint.RequestDelegate!(context);
        body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(body).ReadToEndAsync());
    }

    /// <summary>Forwards to the real repository and cancels the request right after an attempt has been counted.</summary>
    public class CancelAfterReserve : DispatchProxy
    {
        private IIdentityRepository _inner = null!;
        private CancellationTokenSource _cancel = null!;

        public static IIdentityRepository Wrap(IIdentityRepository inner, CancellationTokenSource cancel)
        {
            var proxy = Create<IIdentityRepository, CancelAfterReserve>();
            var self = (CancelAfterReserve)(object)proxy;
            self._inner = inner;
            self._cancel = cancel;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            try
            {
                return targetMethod!.Invoke(_inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
            finally
            {
                if (targetMethod!.Name == nameof(IIdentityRepository.ReserveCredentialAttemptAsync))
                {
                    _cancel.Cancel();
                }
            }
        }
    }

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class RequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    public Task DisposeAsync()
    {
        _configuration.Dispose();
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_databasePath);
        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        if (Directory.Exists(_configPath))
        {
            Directory.Delete(_configPath, recursive: true);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The phone asks to switch while authenticated as <paramref name="activeProfileId"/> with its current access token;
    /// a successful switch hands the phone its new token.
    /// </summary>
    private async Task<DeviceProfileSwitchResult> SwitchAsAsync(Guid activeProfileId, Guid target, string? pin)
    {
        var result = await _service.SwitchAsync(PhoneAuthority(activeProfileId), await TokenIdAsync(_access), target, pin);
        if (result.Tokens is { } tokens)
        {
            _access = tokens.AccessToken;
        }

        return result;
    }

    private async Task<Guid> TokenIdAsync(string accessToken) =>
        Assert.IsType<ClientAccessIdentity>(await _authorization.ValidateAccessTokenAsync(accessToken)).Token.Id;

    private int AuditCount()
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM authorization_audit_events WHERE event_type = @type;",
            new { type = DeviceProfileSwitchService.AuditEvent });
    }

    private RequestAuthority PhoneAuthority(Guid activeProfileId) =>
        new(PrincipalKind.DelegatedUserClient, true, _homeOwner.Id, activeProfileId,
            ApplicationId: BuiltInApplicationIds.NativeClient, DeviceId: _phoneId);

    /// <summary>The claims the bearer-token handler builds for a validated paired-app token.</summary>
    private static DefaultHttpContext ContextFor(ClientAccessIdentity client) =>
        new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[]
                {
                    new Claim(TuvimaClaimTypes.PrincipalKind, PrincipalKind.DelegatedUserClient.ToString()),
                    new Claim(TuvimaClaimTypes.ApplicationId, client.Application.Id.ToString("D")),
                    new Claim(TuvimaClaimTypes.AccountId, client.Account.Id.ToString("D")),
                    new Claim(TuvimaClaimTypes.ProfileId, client.Device.ProfileId.ToString("D")),
                    new Claim(TuvimaClaimTypes.ActiveProfileId, client.Device.ProfileId.ToString("D")),
                    new Claim(TuvimaClaimTypes.DeviceId, client.Device.Id.ToString("D")),
                }.Concat(ClientAuthorizationService.SplitScopes(client.Token.Scopes)
                    .Select(scope => new Claim(TuvimaClaimTypes.Scope, scope))),
                "test")),
        };

    private CatalogueResourceAuthorizationService CreateResourceService(DefaultHttpContext context)
    {
        var accessor = new HttpContextAccessor { HttpContext = context };
        var decisions = new AccountAccessDecisionService(
            new ProfileAccessRepository(_database),
            new GrantAdminUnlockService(new ProfileAccessRepository(_database), new StubProfilePins(), TimeProvider.System),
            new LibraryVisibilityRule(new ProfileAccessRepository(_database), new VaultLibraryRepository(_database)));
        var applications = new ApplicationRepository(_database);
        return new CatalogueResourceAuthorizationService(
            _database,
            new RequestAuthorityResolver(_accounts, applications),
            decisions,
            new AuthorizationEvaluator(decisions, applications, new PermissionRegistry(), new AuthorizationAuditWriter(_accounts), accessor));
    }

    private async Task<(Guid WorkId, Guid AssetId)> InsertOwnedMovieAsync(string title, string rating)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state)
            VALUES (@workId, 'Movie', 'standalone', 'accepted');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
            VALUES (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP, @libraryId);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@assetId, 'title', @title, CURRENT_TIMESTAMP),
                   (@workId, 'content_rating', @rating, CURRENT_TIMESTAMP);
            """,
            new
            {
                workId,
                editionId,
                assetId,
                hash = Guid.NewGuid().ToString("N"),
                path = $"C:/library/{assetId:N}.mkv",
                libraryId = _library.ToString("D"),
                title,
                rating,
            });
        return (workId, assetId);
    }

    private async Task<OAuthTokenResponse> PairPhoneAsync(ApplicationRepository applications)
    {
        await applications.ReplacePermissionsAsync(
            BuiltInApplicationIds.NativeClient,
            new HashSet<ApplicationPermissionId>
            {
                ApplicationPermissionIds.LibraryRead, ApplicationPermissionIds.PlaybackRead,
                ApplicationPermissionIds.DownloadsRead, ApplicationPermissionIds.DownloadsWrite,
            },
            DateTimeOffset.UtcNow);
        await applications.ReplaceClientBindingsAsync(
            BuiltInApplicationIds.NativeClient,
            new HashSet<string>(StringComparer.Ordinal) { ClientId },
            DateTimeOffset.UtcNow);
        return await PairAnotherDeviceAsync();
    }

    /// <summary>Pairs one more device with the permissions and client already in place (changing them would invalidate earlier tokens).</summary>
    private async Task<OAuthTokenResponse> PairAnotherDeviceAsync(IReadOnlyList<string>? scopes = null)
    {
        scopes ??= ["library.read", "playback.read", "downloads.read", "downloads.write"];
        var started = await _authorization.BeginAsync(new DeviceAuthorizationRequest
        {
            ClientId = ClientId,
            ClientName = "Test app",
            ClientVersion = "1.0.0",
            DeviceName = "Mary's phone",
            DeviceClass = "mobile",
            Scope = string.Join(' ', scopes),
            Capabilities = new ClientCapabilitiesDto { Containers = ["mp4"], VideoCodecs = ["h264"], AudioCodecs = ["aac"] },
        }, "https://library.example");
        Assert.True(await _authorization.DecideAsync(
            new PairingDecisionRequest { UserCode = started.UserCode, Approved = true, Scopes = scopes.ToArray() },
            _homeOwner.Id, _mary.Id, _mary.Id));
        var issued = await _authorization.ExchangeAsync(new OAuthTokenRequest
        {
            GrantType = ClientAuthorizationService.DeviceGrantType,
            ClientId = ClientId,
            DeviceCode = started.DeviceCode,
        });
        return Assert.IsType<OAuthTokenResponse>(issued.Success);
    }

    private static CreateAccountAccessCommand NewAccount(string email, bool administrator, string personName) => new(
        email, IsAdministrator: administrator, ProfileId: null,
        NewProfile: new NewAccountProfileCommand(personName, "#112233"),
        Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read, AccountFeatureId.Watch },
        HiddenLibraries: new HashSet<Guid>(),
        TemporaryPassword: null);

    private static RequestAuthority Authority(
        Guid accountId, Guid profileId, bool serverAdmin = false, Guid? householdId = null, bool householdAdmin = false) =>
        new(PrincipalKind.Human, true,
            AccountId: accountId,
            ActiveProfileId: profileId,
            SessionId: Guid.NewGuid(),
            AccountEnabled: true,
            ActiveProfileInUserHousehold: true,
            ActiveProfileIsServerAdministrator: serverAdmin,
            ActiveProfileHouseholdId: householdId,
            ActiveProfileIsHouseholdAdministrator: householdAdmin);

    private sealed class AuditRow
    {
        public Guid? ActorAccountId { get; init; }
        public Guid? ActorProfileId { get; init; }
        public string SubjectType { get; init; } = "";
        public string SubjectId { get; init; } = "";
        public string ChangesJson { get; init; } = "";
    }

    private sealed class RealAdministratorDecisions : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateHouseholdAdministratorAsync(RequestAuthority authority,
            bool requireSurfaceUnlock, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(authority.IsEffectiveHouseholdAdministrator
                ? AuthorizationDecision.Allow()
                : AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));

        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(RequestAuthority authority, AccountFeatureId feature,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());

        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(RequestAuthority authority, Guid libraryId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());

        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(RequestAuthority authority, bool requireSurfaceUnlock,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(authority.IsEffectiveAdministrator
                ? AuthorizationDecision.Allow()
                : AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));
    }

    private sealed class AllowEvaluator : IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority,
            AuthorizationRequirement requirement, ResourceAuthorizationContext? resource,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    private sealed class NoOpInvalidation : IAuthorizationInvalidationService
    {
        public ValueTask InvalidateAccountAsync(Guid accountId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask InvalidateGrantAsync(Guid accountId, Guid profileId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask InvalidateApplicationAsync(Guid applicationId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask InvalidateSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class NoOpAudit : IAuthorizationAuditWriter
    {
        public ValueTask WriteAsync(AuthorizationAuditEvent auditEvent,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
