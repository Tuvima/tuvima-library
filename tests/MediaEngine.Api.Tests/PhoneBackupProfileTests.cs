using MediaEngine.Api.Security;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Phone photo backup (packet B10a): a paired phone backs up to one chosen person, whoever it is browsing as.
/// </summary>
public sealed class PhoneBackupProfileTests : IAsyncLifetime
{
    private const string TemporaryPassword = "a long temporary password";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-phone-backup-{Guid.NewGuid():N}.db");
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"tuvima-phone-backup-{Guid.NewGuid():N}");
    private DatabaseConnection _database = null!;
    private ConfigurationDirectoryLoader _configuration = null!;
    private PhoneBackupProfileService _service = null!;
    private ClientAuthorizationRepository _devices = null!;
    private FirstPartyIdentityService _identity = null!;
    private RequestAuthority _serverAdmin = null!;
    private RequestAuthority _adminOfHome = null!;
    private RequestAuthority _adminOfOther = null!;
    private RequestAuthority _memberOfHome = null!;
    private Account _homeOwner = null!;
    private Profile _mary = null!;
    private Profile _jim = null!;
    private Profile _bea = null!;
    private Guid _phoneId;

    public async Task InitializeAsync()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _configuration = new ConfigurationDirectoryLoader(_configPath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        var accounts = new AccountRepository(_database);
        var identities = new IdentityRepository(_database);
        var profiles = new ProfileRepository(_database);
        _identity = new FirstPartyIdentityService(
            identities, accounts, profiles,
            new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(),
            TimeProvider.System, new ConfigurationAuthenticationPolicyProvider(_configuration));
        var decisions = new RealAdministratorDecisions();
        var mutations = new AccountAccessMutationService(
            accounts, identities, profiles, _configuration, decisions, new AllowEvaluator(),
            new PasswordHasher<GrantAdminProtection>(), new NoOpInvalidation(), new NoOpAudit(), _identity, TimeProvider.System);
        _devices = new ClientAuthorizationRepository(_database);
        _service = new PhoneBackupProfileService(_devices, accounts, profiles, _identity, decisions, new NoOpAudit(), TimeProvider.System);

        var bootstrap = Authority(Guid.NewGuid(), Guid.NewGuid(), serverAdmin: true);
        var serverAccount = await mutations.CreateAsync(bootstrap, NewAccount("server@example.com", administrator: true, "Server"));
        _serverAdmin = Authority(serverAccount.Id, (await accounts.GetDefaultProfileIdAsync(serverAccount.Id))!.Value, serverAdmin: true);

        var createdHome = await mutations.CreateAsync(_serverAdmin, NewAccount("home@example.com", administrator: false, "Mary"));
        var createdOther = await mutations.CreateAsync(_serverAdmin, NewAccount("other@example.com", administrator: false, "Bea"));
        _homeOwner = (await accounts.GetByIdAsync(createdHome.Id))!;
        var otherOwner = (await accounts.GetByIdAsync(createdOther.Id))!;
        var homeHousehold = (await new HouseholdRepository(_database).GetForAccountAsync(_homeOwner.Id))!;
        _mary = (await profiles.GetByIdAsync((await accounts.GetDefaultProfileIdAsync(_homeOwner.Id))!.Value))!;
        _bea = (await profiles.GetByIdAsync((await accounts.GetDefaultProfileIdAsync(otherOwner.Id))!.Value))!;
        _jim = await mutations.AddHouseholdPersonAsync(_serverAdmin,
            new AddHouseholdPersonCommand(homeHousehold.Id, "Jim", null, false, null));
        var givenSignIn = (await mutations.GiveOwnSignInAsync(_serverAdmin,
            new GiveOwnSignInCommand(_jim.Id, "jim@example.com", TemporaryPassword))).Account;
        var memberSignIn = (await accounts.GetByIdAsync(givenSignIn.Id))!;

        _adminOfHome = Authority(_homeOwner.Id, _mary.Id, householdId: _homeOwner.HouseholdId, householdAdmin: true);
        _adminOfOther = Authority(otherOwner.Id, _bea.Id, householdId: otherOwner.HouseholdId, householdAdmin: true);
        _memberOfHome = Authority(memberSignIn.Id, _jim.Id, householdId: memberSignIn.HouseholdId, householdAdmin: false);

        _phoneId = await PairPhoneAsync(accounts, identities);
    }

    [Fact]
    public async Task APhoneBrowsingAsJim_BacksUpIntoMarysSpace_WhenMaryIsChosen()
    {
        Assert.Equal(BackupProfileOutcome.Changed, await _service.SetByAdministratorAsync(_adminOfHome, _phoneId, _mary.Id));

        var phoneBrowsingAsJim = PhoneAuthority(_jim.Id);
        var target = await _service.ResolveUploadTargetAsync(phoneBrowsingAsJim, _jim.Id);

        Assert.Equal(_mary.Id, target.ProfileId);
    }

    [Fact]
    public async Task ABrowserCarryingAnyDeviceId_IsNotAPhone()
    {
        await _service.SetByAdministratorAsync(_adminOfHome, _phoneId, _mary.Id);

        var forged = new RequestAuthority(PrincipalKind.Human, true, _homeOwner.Id, _jim.Id, DeviceId: Guid.NewGuid());
        var withRealPhoneId = new RequestAuthority(PrincipalKind.Human, true, _homeOwner.Id, _jim.Id, DeviceId: _phoneId);

        Assert.Equal(_jim.Id, (await _service.ResolveUploadTargetAsync(forged, _jim.Id)).ProfileId);
        Assert.Equal(_jim.Id, (await _service.ResolveUploadTargetAsync(withRealPhoneId, _jim.Id)).ProfileId);
        Assert.Equal(BackupProfileOutcome.Forbidden, await _service.SetFromDeviceAsync(withRealPhoneId, _phoneId, _jim.Id, null));
    }

    [Fact]
    public async Task APhone_CannotChooseForAnotherAccountsDevice_OrUseItsTarget()
    {
        await _service.SetByAdministratorAsync(_serverAdmin, _phoneId, _mary.Id);
        var otherAccountPhone = new RequestAuthority(
            PrincipalKind.DelegatedUserClient, true, _adminOfOther.AccountId, _bea.Id, DeviceId: _phoneId);

        Assert.Equal(BackupProfileOutcome.Forbidden, await _service.SetFromDeviceAsync(otherAccountPhone, _phoneId, _bea.Id, null));
        Assert.Null((await _service.ResolveUploadTargetAsync(otherAccountPhone, _bea.Id)).ProfileId);
        Assert.Equal(BackupProfileOutcome.Forbidden, await _service.SetFromDeviceAsync(PhoneAuthority(_mary.Id), Guid.NewGuid(), _mary.Id, null));
    }

    [Fact]
    public async Task AStoredTargetOutsideTheHousehold_IsNotUsed()
    {
        Assert.True(await _devices.SetBackupProfileAsync(_phoneId, _bea.Id));

        Assert.Null((await _service.ResolveUploadTargetAsync(PhoneAuthority(_jim.Id), _jim.Id)).ProfileId);
    }

    [Fact]
    public async Task APhoneWithNoChosenPerson_HasNoUploadTarget()
    {
        var target = await _service.ResolveUploadTargetAsync(PhoneAuthority(_jim.Id), _jim.Id);

        Assert.Null(target.ProfileId);
    }

    [Fact]
    public async Task ABrowserUpload_StillGoesToThePersonSignedIn()
    {
        var browser = new RequestAuthority(PrincipalKind.Human, true, _homeOwner.Id, _jim.Id);

        Assert.Equal(_jim.Id, (await _service.ResolveUploadTargetAsync(browser, _jim.Id)).ProfileId);
    }

    [Fact]
    public async Task APhoneChoosingAPersonWithAPin_NeedsThatPin()
    {
        await _identity.SetProfilePinAsync(_jim.Id, "4821");

        Assert.Equal(BackupProfileOutcome.PinRequired, await _service.SetFromDeviceAsync(PhoneAuthority(_mary.Id), _phoneId, _jim.Id, null));
        Assert.Equal(BackupProfileOutcome.PinRequired, await _service.SetFromDeviceAsync(PhoneAuthority(_mary.Id), _phoneId, _jim.Id, "0000"));
        Assert.Null((await _devices.GetDeviceAsync(_phoneId))!.BackupProfileId);

        Assert.Equal(BackupProfileOutcome.Changed, await _service.SetFromDeviceAsync(PhoneAuthority(_mary.Id), _phoneId, _jim.Id, "4821"));
        Assert.Equal(_jim.Id, (await _devices.GetDeviceAsync(_phoneId))!.BackupProfileId);
    }

    [Fact]
    public async Task APhoneChoosingAPersonWithoutAPin_NeedsNothing()
    {
        Assert.Equal(BackupProfileOutcome.Changed, await _service.SetFromDeviceAsync(PhoneAuthority(_jim.Id), _phoneId, _mary.Id, null));
        Assert.Equal(_mary.Id, (await _devices.GetDeviceAsync(_phoneId))!.BackupProfileId);
    }

    [Fact]
    public async Task ChangingAnExistingChoice_FollowsTheSameRule()
    {
        await _identity.SetProfilePinAsync(_jim.Id, "4821");
        Assert.Equal(BackupProfileOutcome.Changed, await _service.SetFromDeviceAsync(PhoneAuthority(_mary.Id), _phoneId, _mary.Id, null));

        Assert.Equal(BackupProfileOutcome.PinRequired, await _service.SetFromDeviceAsync(PhoneAuthority(_mary.Id), _phoneId, _jim.Id, null));
        Assert.Equal(_mary.Id, (await _devices.GetDeviceAsync(_phoneId))!.BackupProfileId);
    }

    [Fact]
    public async Task AnAdministrator_SetsAPinProtectedPersonWithoutThePin_AndCanClearIt()
    {
        await _identity.SetProfilePinAsync(_jim.Id, "4821");

        Assert.Equal(BackupProfileOutcome.Changed, await _service.SetByAdministratorAsync(_serverAdmin, _phoneId, _jim.Id));
        Assert.Equal(_jim.Id, (await _devices.GetDeviceAsync(_phoneId))!.BackupProfileId);

        Assert.Equal(BackupProfileOutcome.Changed, await _service.SetByAdministratorAsync(_adminOfHome, _phoneId, null));
        Assert.Null((await _devices.GetDeviceAsync(_phoneId))!.BackupProfileId);
    }

    [Fact]
    public async Task APersonFromAnotherHousehold_IsRefused_ForThePhoneAndForAdministrators()
    {
        Assert.Equal(BackupProfileOutcome.ProfileNotFound, await _service.SetFromDeviceAsync(PhoneAuthority(_mary.Id), _phoneId, _bea.Id, null));
        Assert.Equal(BackupProfileOutcome.ProfileNotFound, await _service.SetByAdministratorAsync(_serverAdmin, _phoneId, _bea.Id));
        Assert.Equal(BackupProfileOutcome.ProfileNotFound, await _service.SetByAdministratorAsync(_adminOfHome, _phoneId, _bea.Id));
        Assert.Null((await _devices.GetDeviceAsync(_phoneId))!.BackupProfileId);
    }

    [Fact]
    public async Task OnlyAdministratorsOfThePhonesHousehold_CanChooseForIt()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.SetByAdministratorAsync(_adminOfOther, _phoneId, _bea.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.SetByAdministratorAsync(_memberOfHome, _phoneId, _mary.Id));
        Assert.Null((await _devices.GetDeviceAsync(_phoneId))!.BackupProfileId);
    }

    [Fact]
    public async Task ARevokedPhone_CannotBeChanged_AndHasNoUploadTarget()
    {
        Assert.Equal(BackupProfileOutcome.Changed, await _service.SetByAdministratorAsync(_serverAdmin, _phoneId, _mary.Id));
        Assert.True(await _devices.RevokeDeviceByIdAsync(_phoneId, DateTimeOffset.UtcNow, "test"));

        Assert.Equal(BackupProfileOutcome.DeviceNotFound, await _service.SetByAdministratorAsync(_serverAdmin, _phoneId, _jim.Id));
        Assert.Null((await _service.ResolveUploadTargetAsync(PhoneAuthority(_jim.Id), _jim.Id)).ProfileId);
    }

    public Task DisposeAsync()
    {
        _configuration.Dispose();
        _database.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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

    private RequestAuthority PhoneAuthority(Guid browsingProfileId) =>
        new(PrincipalKind.DelegatedUserClient, true, _homeOwner.Id, browsingProfileId, DeviceId: _phoneId);

    private async Task<Guid> PairPhoneAsync(AccountRepository accounts, IdentityRepository identities)
    {
        var applications = new ApplicationRepository(_database);
        await applications.ReplacePermissionsAsync(
            BuiltInApplicationIds.NativeClient,
            new HashSet<ApplicationPermissionId> { ApplicationPermissionIds.LibraryRead },
            DateTimeOffset.UtcNow);
        await applications.ReplaceClientBindingsAsync(
            BuiltInApplicationIds.NativeClient,
            new HashSet<string>(StringComparer.Ordinal) { "tuvima-phone" },
            DateTimeOffset.UtcNow);
        var authorization = new ClientAuthorizationService(
            _devices, accounts, applications, new PermissionRegistry(), TimeProvider.System);
        var started = await authorization.BeginAsync(new DeviceAuthorizationRequest
        {
            ClientId = "tuvima-phone",
            ClientName = "Test app",
            ClientVersion = "1.0.0",
            DeviceName = "Mary's phone",
            DeviceClass = "mobile",
            Scope = "library.read",
            Capabilities = new ClientCapabilitiesDto { Containers = ["mp4"], VideoCodecs = ["h264"], AudioCodecs = ["aac"] },
        }, "https://library.example");
        Assert.True(await authorization.DecideAsync(
            new PairingDecisionRequest { UserCode = started.UserCode, Approved = true },
            _homeOwner.Id, _mary.Id, _mary.Id));
        var issued = await authorization.ExchangeAsync(new OAuthTokenRequest
        {
            GrantType = ClientAuthorizationService.DeviceGrantType,
            ClientId = "tuvima-phone",
            DeviceCode = started.DeviceCode,
        });
        Assert.IsType<OAuthTokenResponse>(issued.Success);
        return Assert.Single(await _devices.GetActiveDevicesAsync(_homeOwner.Id)).Id;
    }

    private static CreateAccountAccessCommand NewAccount(string email, bool administrator, string personName) => new(
        email, IsAdministrator: administrator, ProfileId: null,
        NewProfile: new NewAccountProfileCommand(personName, "#112233"),
        Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read },
        Libraries: new HashSet<Guid>(), TemporaryPassword: null);

    private static RequestAuthority Authority(
        Guid accountId, Guid profileId, bool serverAdmin = false, Guid? householdId = null, bool householdAdmin = false) =>
        new(PrincipalKind.Human, true,
            AccountId: accountId,
            ActiveProfileId: profileId,
            SessionId: Guid.NewGuid(),
            AccountEnabled: true,
            GrantEnabled: true,
            AccountIsAdministrator: serverAdmin,
            GrantAdminEnabled: serverAdmin,
            AccountHouseholdId: householdId,
            AccountIsHouseholdAdmin: householdAdmin);

    /// <summary>Allows what the real rules allow: an effective server administrator, or an effective household administrator.</summary>
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
