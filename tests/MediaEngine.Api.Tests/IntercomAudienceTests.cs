using Dapper;
using MediaEngine.Api.Realtime;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Events;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class IntercomAudienceTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly IdentityRepository _identities;
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _libraryId = Guid.NewGuid();
    private readonly Guid _assetId = Guid.NewGuid();

    public IntercomAudienceTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_intercom_audience_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        _identities = new IdentityRepository(_database);
    }

    [Fact]
    public async Task LiveAudience_FiltersMediaByLibraryAndFeatureAndOperationsByAdministrator()
    {
        await SeedHumanAsync();
        var authorizer = new IntercomAudienceAuthorizer(
            _identities, _accounts, new FixedResources(_assetId, _libraryId, AccountFeatureId.Watch),
            new ProfileRepository(_database), TimeProvider.System);
        var connection = new IntercomAudienceConnection("connection", _sessionId, _accountId);
        var media = new MediaAddedEvent(Guid.NewGuid(), null, "Movies", "Visible", _assetId);

        Assert.True(await authorizer.CanReceiveAsync(connection, SignalREvents.MediaAdded, media));
        Assert.False(await authorizer.CanReceiveAsync(connection, SignalREvents.ProviderStatusChanged,
            new ProviderStatusChangedEvent("provider", "healthy", string.Empty)));

        using (var database = _database.CreateConnection())
        {
            database.Execute("DELETE FROM account_feature_grants WHERE account_id=@accountId;", new { accountId = _accountId });
        }

        Assert.False(await authorizer.CanReceiveAsync(connection, SignalREvents.MediaAdded, media));

        using (var database = _database.CreateConnection())
        {
            database.Execute("""
                UPDATE accounts SET is_administrator=1 WHERE id=@accountId;
                UPDATE account_profile_grants SET admin_enabled=1 WHERE account_id=@accountId AND profile_id=@profileId;
                """, new { accountId = _accountId, profileId = Profile.SeedProfileId });
        }

        Assert.True(await authorizer.CanReceiveAsync(connection, SignalREvents.ProviderStatusChanged,
            new ProviderStatusChangedEvent("provider", "healthy", string.Empty)));
        Assert.True(await authorizer.CanReceiveAsync(connection, SignalREvents.MediaAdded, media));

        await _accounts.SetAdminProtectionAsync(new GrantAdminProtection
        {
            AccountId = _accountId,
            ProfileId = Profile.SeedProfileId,
            IsEnabled = true,
            UnlockMode = AdminUnlockMode.FixedDuration.ToString(),
            UnlockMinutes = 30,
            PinHash = "test",
            HashScheme = "test",
            ProtectionVersion = 2,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        Assert.False(await authorizer.CanReceiveAsync(connection, SignalREvents.ProviderStatusChanged,
            new ProviderStatusChangedEvent("provider", "healthy", string.Empty)));
        await _accounts.SetAdminUnlockAsync(new GrantAdminUnlock
        {
            SessionId = _sessionId,
            AccountId = _accountId,
            ProfileId = Profile.SeedProfileId,
            ProtectionVersion = 2,
            Method = "test",
            GrantedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
        });
        Assert.True(await authorizer.CanReceiveAsync(connection, SignalREvents.ProviderStatusChanged,
            new ProviderStatusChangedEvent("provider", "healthy", string.Empty)));
    }

    [Fact]
    public async Task LiveAudience_KidsProfileOfAnAdministratorGetsNoAdministratorEvents()
    {
        await SeedHumanAsync();
        using (var database = _database.CreateConnection())
        {
            database.Execute("""
                UPDATE accounts SET is_administrator=1 WHERE id=@accountId;
                UPDATE account_profile_grants SET admin_enabled=1 WHERE account_id=@accountId AND profile_id=@profileId;
                UPDATE profiles SET role='RestrictedProfile' WHERE id=@profileId;
                """, new { accountId = _accountId, profileId = Profile.SeedProfileId });
        }

        var authorizer = new IntercomAudienceAuthorizer(
            _identities, _accounts, new FixedResources(_assetId, _libraryId, AccountFeatureId.Watch),
            new ProfileRepository(_database), TimeProvider.System);
        var connection = new IntercomAudienceConnection("connection", _sessionId, _accountId);

        Assert.False(await authorizer.CanReceiveAsync(connection, SignalREvents.ReviewItemCreated,
            new ReviewItemCreatedEvent(Guid.NewGuid(), Guid.NewGuid(), "LowConfidence", "Title")));
        Assert.False(await authorizer.CanReceiveAsync(connection, SignalREvents.ProviderStatusChanged,
            new ProviderStatusChangedEvent("provider", "healthy", string.Empty)));
        // Media events fall back to the account's own library and feature grants.
        Assert.True(await authorizer.CanReceiveAsync(connection, SignalREvents.MediaAdded,
            new MediaAddedEvent(Guid.NewGuid(), null, "Movies", "Visible", _assetId)));
    }

    [Fact]
    public async Task LiveAudience_StopsAfterSessionRevocation()
    {
        await SeedHumanAsync();
        var authorizer = new IntercomAudienceAuthorizer(
            _identities, _accounts, new FixedResources(_assetId, _libraryId, AccountFeatureId.Watch),
            new ProfileRepository(_database), TimeProvider.System);
        var connection = new IntercomAudienceConnection("connection", _sessionId, _accountId);
        await _identities.RevokeSessionAsync(_sessionId, DateTimeOffset.UtcNow, "test");

        Assert.False(await authorizer.CanReceiveAsync(connection, SignalREvents.MediaAdded,
            new MediaAddedEvent(Guid.NewGuid(), null, "Movies", "Hidden", _assetId)));
    }

    [Fact]
    public async Task Middleware_RejectsServiceCredentialWithoutHumanIntercomToken()
    {
        var nextCalled = false;
        var middleware = new IntercomTokenAuthenticationMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = SignalREvents.IntercomPath;
        context.Request.Headers[TuvimaAuthDefaults.ServiceHeader] = "service-credential";
        var tokens = new IntercomTokenService(
            new EphemeralDataProtectionProvider(), _identities, NullLogger<IntercomTokenService>.Instance);

        await middleware.InvokeAsync(context, tokens, NullLogger<IntercomTokenAuthenticationMiddleware>.Instance);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    private async Task SeedHumanAsync()
    {
        var now = DateTimeOffset.UtcNow;
        await _accounts.InsertAsync(new Account
        {
            Id = _accountId,
            Email = "viewer@example.com",
            NormalizedEmail = "VIEWER@EXAMPLE.COM",
            IsEnabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _accounts.GrantProfileAsync(new AccountProfileGrant
        {
            AccountId = _accountId,
            ProfileId = Profile.SeedProfileId,
            IsDefault = true,
            IsEnabled = true,
            GrantedAt = now,
        });
        await _accounts.ReplaceAccountAccessAsync(
            _accountId,
            new HashSet<AccountFeatureId> { AccountFeatureId.Watch },
            new HashSet<Guid> { _libraryId },
            now);
        await _identities.InsertSessionAsync(new AuthSession
        {
            Id = _sessionId,
            AccountId = _accountId,
            ActiveProfileId = Profile.SeedProfileId,
            TokenHash = "test",
            DeviceId = "browser",
            DeviceName = "Browser",
            Client = "dashboard",
            AuthenticationMethod = "password",
            SecurityStamp = "test",
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now.AddHours(1),
        });
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_databasePath);
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private sealed class FixedResources(Guid assetId, Guid libraryId, AccountFeatureId featureId)
        : IApplicationEventResourceResolver
    {
        public Task<ApplicationEventResourceProvenance?> ResolveAsync(Guid requestedAssetId, CancellationToken ct = default) =>
            Task.FromResult<ApplicationEventResourceProvenance?>(
                requestedAssetId == assetId ? new(libraryId, featureId) : null);
    }
}
