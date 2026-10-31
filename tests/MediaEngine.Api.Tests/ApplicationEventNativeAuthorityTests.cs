using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Events;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Events;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Tests;

public sealed class ApplicationEventNativeAuthorityTests : IDisposable
{
    private static readonly ApplicationPermissionId[] Scopes =
    [
        ApplicationPermissionIds.EventsSubscribe,
        ApplicationPermissionIds.LibraryChangesRead,
        ApplicationPermissionIds.LibraryRead,
    ];

    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly ApplicationRepository _applications;
    private readonly ClientAuthorizationRepository _clients;
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 9, 9, 15, 0, 0, TimeSpan.Zero));
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly Guid _libraryId = Guid.NewGuid();
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"tuvima_application_events_config_{Guid.NewGuid():N}");

    public ApplicationEventNativeAuthorityTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_application_events_native_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        _applications = new ApplicationRepository(_database);
        _clients = new ClientAuthorizationRepository(_database);
    }

    [Fact]
    public async Task Delivery_AllowsExactLiveNativeTokenAndDevice()
    {
        var fixture = await CreateSubscriptionAsync();

        Assert.True(await fixture.Authorizer.CanDeliverDelegatedAsync(fixture.Subscriber, Event()));
    }

    [Fact]
    public async Task Delivery_DeniesRevokedExactNativeToken()
    {
        var fixture = await CreateSubscriptionAsync();
        var token = (await _clients.GetTokenByIdAsync(fixture.Subscriber.TokenId!.Value))!;

        await _clients.RevokeTokenFamilyAsync(token.TokenFamilyId, _clock.GetUtcNow(), "test_revoke");

        Assert.False(await fixture.Authorizer.CanDeliverDelegatedAsync(fixture.Subscriber, Event()));
    }

    [Fact]
    public async Task Delivery_DeniesRevokedNativeDevice()
    {
        var fixture = await CreateSubscriptionAsync();

        Assert.True(await _clients.RevokeDeviceAsync(
            fixture.Subscriber.Authority.DeviceId!.Value,
            Profile.SeedProfileId,
            _clock.GetUtcNow(),
            "test_revoke"));

        Assert.False(await fixture.Authorizer.CanDeliverDelegatedAsync(fixture.Subscriber, Event()));
    }

    [Fact]
    public async Task Subscribe_IsRefusedAfterTheDeviceIsRevoked()
    {
        var fixture = await CreateSubscriptionAsync();
        var authority = fixture.Subscriber.Authority;
        IReadOnlySet<string> types = new HashSet<string>(StringComparer.Ordinal) { "library.item_added" };
        IReadOnlySet<Guid> libraries = new HashSet<Guid> { _libraryId };
        Assert.True(await fixture.Authorizer.CanSubscribeAsync(
            authority, fixture.Subscriber.Consent, types.ToList(), libraries, CancellationToken.None));

        await _clients.RevokeDeviceByIdAsync(authority.DeviceId!.Value, _clock.GetUtcNow(), "test_revoke");

        Assert.False(await fixture.Authorizer.CanSubscribeAsync(
            authority, fixture.Subscriber.Consent, types.ToList(), libraries, CancellationToken.None));
    }

    [Fact]
    public async Task Delivery_DeniesExpiredExactNativeToken()
    {
        var fixture = await CreateSubscriptionAsync();

        _clock.Advance(TimeSpan.FromHours(2));

        Assert.False(await fixture.Authorizer.CanDeliverDelegatedAsync(fixture.Subscriber, Event()));
    }

    [Fact]
    public async Task Delivery_DeniesDeviceProfileSwitchAfterConnection()
    {
        var fixture = await CreateSubscriptionAsync();
        using var connection = _database.CreateConnection();
        var switchedProfileId = Guid.NewGuid();
        connection.Execute(
            "INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@id,'Switched','#7C4DFF','RestrictedProfile',@at);",
            new { id = switchedProfileId, at = _clock.GetUtcNow().ToString("O") });
        connection.Execute(
            "UPDATE client_devices SET profile_id=@profileId WHERE id=@deviceId;",
            new { profileId = switchedProfileId, deviceId = fixture.Subscriber.Authority.DeviceId });

        Assert.False(await fixture.Authorizer.CanDeliverDelegatedAsync(fixture.Subscriber, Event()));
    }

    [Fact]
    public async Task Delivery_DeniesAnEventOfTheViewArea_ToAProfileOutsideTheDefaultHousehold()
    {
        var fixture = await CreateSubscriptionAsync();
        Assert.False(await fixture.Authorizer.CanDeliverDelegatedAsync(
            fixture.Subscriber,
            Event() with { Subject = Event().Subject with { FeatureId = AccountFeatureId.View } }));
    }

    private async Task<Fixture> CreateSubscriptionAsync()
    {
        var now = _clock.GetUtcNow();
        await _accounts.CreateAccountAsync(
            new Account
            {
                Id = _accountId,
                Email = "events@example.com",
                NormalizedEmail = "EVENTS@EXAMPLE.COM",
                IsEnabled = true,
                AuthorizationVersion = 1,
                CreatedAt = now,
                UpdatedAt = now,
            },
            new TestGrant(_accountId, Profile.SeedProfileId, IsDefault: true, AdminEnabled: false));
        await _applications.ReplacePermissionsAsync(
            BuiltInApplicationIds.NativeClient,
            Scopes.ToHashSet(),
            now);
        await _applications.ReplaceClientBindingsAsync(
            BuiltInApplicationIds.NativeClient,
            new HashSet<string>(StringComparer.Ordinal) { "tuvima-tv" },
            now);

        var clientAuthorization = new ClientAuthorizationService(
            _clients, _accounts, _applications, new PermissionRegistry(), _clock);
        var started = await clientAuthorization.BeginAsync(new DeviceAuthorizationRequest
        {
            ClientId = "tuvima-tv",
            ClientName = "TV",
            ClientVersion = "1",
            DeviceName = "Living room",
            DeviceClass = "television",
            Scope = string.Join(' ', Scopes.Select(permission => permission.Value)),
            Capabilities = new ClientCapabilitiesDto(),
        }, "https://library.example");
        Assert.True(await clientAuthorization.DecideAsync(
            new PairingDecisionRequest { UserCode = started.UserCode, Approved = true, Scopes = Scopes.Select(permission => permission.Value).ToArray() },
            _accountId,
            Profile.SeedProfileId,
            Profile.SeedProfileId));
        var issued = Assert.IsType<OAuthTokenResponse>((await clientAuthorization.ExchangeAsync(new OAuthTokenRequest
        {
            GrantType = ClientAuthorizationService.DeviceGrantType,
            ClientId = "tuvima-tv",
            DeviceCode = started.DeviceCode,
        })).Success);
        var identity = Assert.IsType<ClientAccessIdentity>(await clientAuthorization.ValidateAccessTokenAsync(issued.AccessToken));
        var authority = new RequestAuthority(
            PrincipalKind.DelegatedUserClient,
            true,
            identity.Account.Id,
            identity.Profile.ProfileId,
            identity.Application.Id,
            DeviceId: identity.Device.Id,
            AccountEnabled: true,
            ActiveProfileInUserHousehold: true,
            ApplicationEnabled: true,
            AccountAuthorizationVersion: identity.Account.AuthorizationVersion,
            ProfileAuthorizationVersion: identity.Profile.AuthorizationVersion,
            ApplicationAuthorizationVersion: identity.Application.AuthorizationVersion);
        var consent = Scopes.ToHashSet();
        var subscriber = new ApplicationEventSubscriber(
            "connection",
            identity.Application.Id,
            null,
            identity.Token.Id,
            authority,
            consent,
            new HashSet<string>(StringComparer.Ordinal) { "library.item_added" },
            new HashSet<Guid> { _libraryId });
        return new(NewAuthorizer(ConfiguredLibraries()), subscriber);
    }

    private ApplicationEventSubscriptionAuthorizer NewAuthorizer(IConfigurationLoader configuration, LibraryVisibilityRule? rule = null)
    {
        var permissions = new PermissionRegistry();
        var registry = new ApplicationEventRegistry(permissions);
        return new ApplicationEventSubscriptionAuthorizer(
            new AllowAuthorizationEvaluator(),
            _accounts,
            new ProfileAccessRepository(_database),
            _applications,
            _clients,
            permissions,
            registry,
            new ApplicationEventDeliveryAuthorizer(_applications, permissions, registry),
            _clock,
            rule ?? TestLibraryVisibility.Rule(new VaultLibraryRepository(_database)),
            configuration);
    }

    private StoredApplicationEvent Event() => new(
        1,
        Guid.NewGuid(),
        "library.item_added",
        1,
        _clock.GetUtcNow(),
        Guid.NewGuid().ToString("D"),
        new("asset", Guid.NewGuid().ToString("D"), _libraryId, Profile.SeedProfileId, AccountFeatureId.Watch),
        "{}");

    // One configured library in the Watch area, the one the fixture's events are about.
    private ConfigurationDirectoryLoader ConfiguredLibraries(string area = "watch", params Guid[] extra)
    {
        // Written straight to the file: the test only needs the loader to read the libraries back, not the settings screen's validation.
        Directory.CreateDirectory(_configPath);
        File.WriteAllText(Path.Combine(_configPath, "libraries.json"), System.Text.Json.JsonSerializer.Serialize(new LibrariesConfiguration
        {
            Libraries =
            [
                new LibraryFolderConfig { Id = _libraryId.ToString("D"), Name = "Films", Category = "Movies", Kind = LibraryKinds.Catalogued, KindKey = "movies", Area = area, MediaTypes = ["Movies"] },
                .. extra.Select(id => new LibraryFolderConfig { Id = id.ToString("D"), Name = "Other", Category = "Books", Kind = LibraryKinds.Catalogued, KindKey = "books", Area = "read", MediaTypes = ["Books"] }),
            ],
            StorageLocations = [new ServerStorageLocationConfig { Id = "media", Label = "Media", Path = Path.Combine(_configPath, "library"), AllowWrite = true }],
        }));
        return new ConfigurationDirectoryLoader(_configPath);
    }

    [Fact]
    public async Task Subscribe_IsRefusedForAnUnknownLibrary_ButNotForALibraryTheProfileMaySee_InAnyOfRead_Watch_Listen()
    {
        var otherLibrary = Guid.NewGuid();
        var fixture = await CreateSubscriptionAsync();
        var authority = fixture.Subscriber.Authority;
        var types = new List<string> { "library.item_added" };
        var configured = ConfiguredLibraries("watch", otherLibrary);
        var subscription = NewAuthorizer(configured);

        // Every profile reaches Watch, Listen and Read: the Watch library and the Read library are fine, an unknown id is refused.
        Assert.True(await subscription.CanSubscribeAsync(authority, fixture.Subscriber.Consent, types, new HashSet<Guid> { _libraryId }, CancellationToken.None));
        Assert.False(await subscription.CanSubscribeAsync(authority, fixture.Subscriber.Consent, types, new HashSet<Guid> { Guid.NewGuid() }, CancellationToken.None));
        Assert.True(await subscription.CanSubscribeAsync(authority, fixture.Subscriber.Consent, types, new HashSet<Guid> { otherLibrary }, CancellationToken.None));
        Assert.False(await subscription.CanSubscribeAsync(authority, fixture.Subscriber.Consent, types, new HashSet<Guid> { _libraryId, Guid.NewGuid() }, CancellationToken.None));
    }

    [Fact]
    public async Task ALibraryHiddenForTheHousehold_IsNeitherSubscribedToNorDeliveredToANativeClient_AndMaySubscribeOnceTheHouseholdShowsIt()
    {
        var fixture = await CreateSubscriptionAsync();
        var householdLibraries = new HouseholdLibraryAccessRepository(_database);
        var rule = new LibraryVisibilityRule(householdLibraries, new VaultLibraryRepository(_database));
        Guid household;
        using (var connection = _database.CreateConnection())
        {
            household = connection.ExecuteScalar<Guid>("SELECT household_id FROM profiles WHERE id=@id;", new { id = Profile.SeedProfileId });
        }

        await householdLibraries.ReplaceHiddenLibrariesAsync(household, new HashSet<Guid> { _libraryId }, _clock.GetUtcNow(), null);
        var authorizer = NewAuthorizer(ConfiguredLibraries(), rule);
        var types = new List<string> { "library.item_added" };
        var libraries = new HashSet<Guid> { _libraryId };

        Assert.False(await authorizer.CanSubscribeAsync(fixture.Subscriber.Authority, fixture.Subscriber.Consent, types, libraries, CancellationToken.None));
        Assert.False(await authorizer.CanDeliverDelegatedAsync(fixture.Subscriber, Event()));

        await householdLibraries.ReplaceHiddenLibrariesAsync(household, new HashSet<Guid>(), _clock.GetUtcNow(), null);

        // Showing the library again raises the profile's authorization version, so a native token issued before it must be refreshed (as for any access change);
        // a connection that carries the current version may subscribe.
        long version;
        using (var connection = _database.CreateConnection())
        {
            version = connection.ExecuteScalar<long>("SELECT authorization_version FROM profiles WHERE id=@id;", new { id = Profile.SeedProfileId });
        }

        var refreshed = fixture.Subscriber with { Authority = fixture.Subscriber.Authority with { ProfileAuthorizationVersion = version } };
        Assert.True(await authorizer.CanSubscribeAsync(refreshed.Authority, refreshed.Consent, types, libraries, CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_configPath))
        {
            Directory.Delete(_configPath, recursive: true);
        }

        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_databasePath);
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private sealed record Fixture(
        ApplicationEventSubscriptionAuthorizer Authorizer,
        ApplicationEventSubscriber Subscriber);

    private sealed class AllowAuthorizationEvaluator : IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(
            RequestAuthority authority,
            AuthorizationRequirement requirement,
            ResourceAuthorizationContext? resource,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
