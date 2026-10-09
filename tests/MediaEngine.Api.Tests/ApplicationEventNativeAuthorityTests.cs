using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Events;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Events;
using MediaEngine.Storage;
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
    public async Task Delivery_DeniesRevokedFeatureGrantAndOtherMediaFeature()
    {
        var fixture = await CreateSubscriptionAsync();
        Assert.False(await fixture.Authorizer.CanDeliverDelegatedAsync(
            fixture.Subscriber,
            Event() with { Subject = Event().Subject with { FeatureId = AccountFeatureId.Read } }));
        using var connection = _database.CreateConnection();
        connection.Execute(
            "DELETE FROM account_feature_grants WHERE account_id=@accountId AND feature_id='watch';",
            new { accountId = _accountId });

        Assert.False(await fixture.Authorizer.CanDeliverDelegatedAsync(fixture.Subscriber, Event()));
    }

    private async Task<Fixture> CreateSubscriptionAsync()
    {
        var now = _clock.GetUtcNow();
        await _accounts.InsertAsync(new Account
        {
            Id = _accountId,
            Email = "events@example.com",
            NormalizedEmail = "EVENTS@EXAMPLE.COM",
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _accounts.GrantProfileAsync(new AccountProfileGrant
        {
            AccountId = _accountId,
            ProfileId = Profile.SeedProfileId,
            IsDefault = true,
            IsEnabled = true,
            AuthorizationVersion = 1,
            GrantedAt = now,
        });
        await _accounts.ReplaceAccountAccessAsync(
            _accountId,
            new HashSet<AccountFeatureId> { AccountFeatureId.Watch },
            new HashSet<Guid> { _libraryId },
            now);
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
            new PairingDecisionRequest { UserCode = started.UserCode, Approved = true },
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
            identity.Grant.ProfileId,
            identity.Application.Id,
            DeviceId: identity.Device.Id,
            AccountEnabled: true,
            GrantEnabled: true,
            ApplicationEnabled: true,
            AccountAuthorizationVersion: identity.Account.AuthorizationVersion,
            GrantAuthorizationVersion: identity.Grant.AuthorizationVersion,
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
        var permissions = new PermissionRegistry();
        var registry = new ApplicationEventRegistry(permissions);
        var authorizer = new ApplicationEventSubscriptionAuthorizer(
            new AllowAuthorizationEvaluator(),
            _accounts,
            _applications,
            _clients,
            permissions,
            registry,
            new ApplicationEventDeliveryAuthorizer(_applications, permissions, registry),
            _clock);
        return new(authorizer, subscriber);
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

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
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
