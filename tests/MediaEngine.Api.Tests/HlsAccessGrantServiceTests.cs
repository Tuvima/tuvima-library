using Dapper;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Tests;

public sealed class HlsAccessGrantServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"tuvima_hls_grants_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly ApplicationRepository _applications;
    private readonly IdentityRepository _identities;
    private readonly ClientAuthorizationRepository _clients;
    private readonly ManualTimeProvider _clock = new(
        new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero));

    public HlsAccessGrantServiceTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        _applications = new ApplicationRepository(_database);
        _identities = new IdentityRepository(_database);
        _clients = new ClientAuthorizationRepository(_database);
    }

    [Fact]
    public async Task ServiceGrantIsPackageScopedAndExpires()
    {
        var application = await CreateApplicationAsync(ApplicationType.ServerIntegration);
        var service = CreateService();
        var assetId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        var authority = ServiceAuthority(application);

        var grant = service.Create(
            assetId, packageId, authority, null, [ApplicationPermissionIds.PlaybackRead.Value]);

        Assert.Equal(assetId, (await service.ValidateAsync(grant.Value, packageId))?.AssetId);
        Assert.Null(await service.ValidateAsync(grant.Value, Guid.NewGuid()));

        _clock.Advance(TimeSpan.FromHours(13));
        Assert.Null(await service.ValidateAsync(grant.Value, packageId));
    }

    [Fact]
    public async Task PermissionChangeInvalidatesExistingServiceGrant()
    {
        var application = await CreateApplicationAsync(ApplicationType.ServerIntegration);
        var service = CreateService();
        var packageId = Guid.NewGuid();
        var grant = service.Create(
            Guid.NewGuid(), packageId, ServiceAuthority(application), null,
            [ApplicationPermissionIds.PlaybackRead.Value]);
        Assert.NotNull(await service.ValidateAsync(grant.Value, packageId));

        await _applications.ReplacePermissionsAsync(application.Id, new HashSet<ApplicationPermissionId>(), _clock.GetUtcNow());

        Assert.Null(await service.ValidateAsync(grant.Value, packageId));
    }

    [Fact]
    public async Task CredentialRevocationInvalidatesGrantWhileAnotherCredentialRemainsLive()
    {
        var application = await CreateApplicationAsync(ApplicationType.ServerIntegration);
        var revoked = Credential(application.Id, "revoked");
        var remaining = Credential(application.Id, "remaining");
        await _applications.InsertCredentialAsync(revoked);
        await _applications.InsertCredentialAsync(remaining);
        application = (await _applications.GetApplicationAsync(application.Id))!;
        var service = CreateService();
        var packageId = Guid.NewGuid();
        var grant = service.Create(
            Guid.NewGuid(), packageId, ServiceAuthority(application), null,
            [ApplicationPermissionIds.PlaybackRead.Value]);
        Assert.NotNull(await service.ValidateAsync(grant.Value, packageId));

        Assert.True(await _applications.RevokeCredentialAsync(
            application.Id, revoked.Id, _clock.GetUtcNow()));

        Assert.Null(await service.ValidateAsync(grant.Value, packageId));
        Assert.Contains(await _applications.GetApplicationCredentialsAsync(application.Id),
            credential => credential.Id == remaining.Id && credential.RevokedAt is null);
    }

    [Fact]
    public async Task DisabledApplicationInvalidatesExistingServiceGrant()
    {
        var application = await CreateApplicationAsync(ApplicationType.ServerIntegration);
        var service = CreateService();
        var packageId = Guid.NewGuid();
        var grant = service.Create(
            Guid.NewGuid(), packageId, ServiceAuthority(application), null,
            [ApplicationPermissionIds.PlaybackRead.Value]);

        application.IsEnabled = false;
        application.UpdatedAt = _clock.GetUtcNow();
        await _applications.UpdateApplicationAsync(application);

        Assert.Null(await service.ValidateAsync(grant.Value, packageId));
    }

    [Fact]
    public async Task RevokedHumanSessionInvalidatesExistingGrant()
    {
        var account = await CreateAccountAsync();
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            ActiveProfileId = Profile.SeedProfileId,
            TokenHash = Guid.NewGuid().ToString("N"),
            DeviceId = "browser",
            DeviceName = "Browser",
            Client = "web",
            AuthenticationMethod = "password",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = _clock.GetUtcNow(),
            LastSeenAt = _clock.GetUtcNow(),
            ExpiresAt = _clock.GetUtcNow().AddDays(1),
        };
        await _identities.InsertSessionAsync(session);
        var grantRow = (await _accounts.GetGrantAsync(account.Id, Profile.SeedProfileId))!;
        var authority = new RequestAuthority(
            PrincipalKind.Human, true, account.Id, Profile.SeedProfileId, SessionId: session.Id,
            AccountEnabled: true, ActiveProfileInUserHousehold: true,
            AccountAuthorizationVersion: account.AuthorizationVersion,
            ProfileAuthorizationVersion: grantRow.AuthorizationVersion);
        var service = CreateService();
        var packageId = Guid.NewGuid();
        var grant = service.Create(Guid.NewGuid(), packageId, authority, null, []);
        Assert.NotNull(await service.ValidateAsync(grant.Value, packageId));

        await _identities.RevokeSessionAsync(session.Id, _clock.GetUtcNow(), "test");

        Assert.Null(await service.ValidateAsync(grant.Value, packageId));
    }

    [Fact]
    public async Task RevokedBearerTokenInvalidatesExistingDelegatedGrant()
    {
        var account = await CreateAccountAsync();
        var application = await CreateApplicationAsync(ApplicationType.UserClient, "tv");
        var accountGrant = (await _accounts.GetGrantAsync(account.Id, Profile.SeedProfileId))!;
        var device = new ClientDevice
        {
            Id = Guid.NewGuid(),
            ApplicationId = application.Id,
            AccountId = account.Id,
            ProfileId = Profile.SeedProfileId,
            DeviceName = "TV",
            DeviceClass = "television",
            ClientId = "tv",
            ClientName = "TV",
            ClientVersion = "1",
            Scopes = ApplicationPermissionIds.PlaybackRead.Value,
            CreatedAt = _clock.GetUtcNow(),
            LastSeenAt = _clock.GetUtcNow(),
        };
        var token = new ClientToken
        {
            Id = Guid.NewGuid(),
            ApplicationId = application.Id,
            AccountId = account.Id,
            ProfileId = Profile.SeedProfileId,
            DeviceId = device.Id,
            TokenFamilyId = Guid.NewGuid(),
            Kind = "access",
            TokenHash = Guid.NewGuid().ToString("N"),
            Scopes = ApplicationPermissionIds.PlaybackRead.Value,
            AccountAuthorizationVersion = account.AuthorizationVersion,
            GrantAuthorizationVersion = accountGrant.AuthorizationVersion,
            ApplicationAuthorizationVersion = application.AuthorizationVersion,
            CreatedAt = _clock.GetUtcNow(),
            ExpiresAt = _clock.GetUtcNow().AddHours(1),
        };
        await InsertClientAsync(device, token);
        var authority = new RequestAuthority(
            PrincipalKind.DelegatedUserClient, true, account.Id, Profile.SeedProfileId, application.Id,
            DeviceId: device.Id, AccountEnabled: true, ActiveProfileInUserHousehold: true, ApplicationEnabled: true,
            AccountAuthorizationVersion: account.AuthorizationVersion,
            ProfileAuthorizationVersion: accountGrant.AuthorizationVersion,
            ApplicationAuthorizationVersion: application.AuthorizationVersion);
        var service = CreateService();
        var packageId = Guid.NewGuid();
        var grant = service.Create(
            Guid.NewGuid(), packageId, authority, token.Id,
            [ApplicationPermissionIds.PlaybackRead.Value]);
        Assert.NotNull(await service.ValidateAsync(grant.Value, packageId));

        await _clients.RevokeTokenFamilyAsync(token.TokenFamilyId, _clock.GetUtcNow(), "test");

        Assert.Null(await service.ValidateAsync(grant.Value, packageId));
    }

    [Fact]
    public async Task GrantRejectsTampering()
    {
        var application = await CreateApplicationAsync(ApplicationType.ServerIntegration);
        var service = CreateService();
        var packageId = Guid.NewGuid();
        var grant = service.Create(
            Guid.NewGuid(), packageId, ServiceAuthority(application), null,
            [ApplicationPermissionIds.PlaybackRead.Value]);
        var replacement = grant.Value[^1] == 'a' ? 'b' : 'a';

        Assert.Null(await service.ValidateAsync(grant.Value[..^1] + replacement, packageId));
    }

    private HlsAccessGrantService CreateService() =>
        new(
            new EphemeralDataProtectionProvider(),
            new ConfigurationDirectoryLoader(Path.Combine(FindRepoRoot(), "config")),
            _accounts,
            _applications,
            _identities,
            _clients,
            new PermissionRegistry(),
            _clock);

    private async Task<Account> CreateAccountAsync()
    {
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = $"{Guid.NewGuid():N}@example.com",
            NormalizedEmail = $"{Guid.NewGuid():N}@EXAMPLE.COM",
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = _clock.GetUtcNow(),
            UpdatedAt = _clock.GetUtcNow(),
        };
        await _accounts.CreateAccountAsync(
            account,
            new AccountProfileGrant
            {
                AccountId = account.Id,
                ProfileId = Profile.SeedProfileId,
                IsDefault = true,
                IsEnabled = true,
                AuthorizationVersion = 1,
                GrantedAt = _clock.GetUtcNow(),
            },
            new HashSet<AccountFeatureId> { AccountFeatureId.Watch },
            new HashSet<Guid>());
        return account;
    }

    private async Task<MediaEngine.Domain.Entities.Application> CreateApplicationAsync(
        ApplicationType type,
        string? clientId = null)
    {
        var application = new MediaEngine.Domain.Entities.Application
        {
            Id = Guid.NewGuid(),
            Name = "HLS test",
            ApplicationType = type,
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = _clock.GetUtcNow(),
            UpdatedAt = _clock.GetUtcNow(),
        };
        await _applications.InsertApplicationAsync(
            application,
            new HashSet<ApplicationPermissionId> { ApplicationPermissionIds.PlaybackRead });
        if (clientId is not null)
        {
            await _applications.ReplaceClientBindingsAsync(
                application.Id, new HashSet<string> { clientId }, _clock.GetUtcNow());
        }

        return (await _applications.GetApplicationAsync(application.Id))!;
    }

    private async Task InsertClientAsync(ClientDevice device, ClientToken token)
    {
        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO client_devices
                (id,application_id,account_id,profile_id,device_name,device_class,client_id,client_name,client_version,
                 scopes,capabilities_json,created_at,last_seen_at)
            VALUES
                (@Id,@ApplicationId,@AccountId,@ProfileId,@DeviceName,@DeviceClass,@ClientId,@ClientName,@ClientVersion,
                 @Scopes,'{}',@CreatedAt,@LastSeenAt);
            """,
            device);
        await connection.ExecuteAsync(
            """
            INSERT INTO client_tokens
                (id,application_id,account_id,device_id,profile_id,token_family_id,token_kind,token_hash,scopes,
                 account_authorization_version,grant_authorization_version,application_authorization_version,
                 generation,created_at,expires_at)
            VALUES
                (@Id,@ApplicationId,@AccountId,@DeviceId,@ProfileId,@TokenFamilyId,@Kind,@TokenHash,@Scopes,
                 @AccountAuthorizationVersion,@GrantAuthorizationVersion,@ApplicationAuthorizationVersion,
                 @Generation,@CreatedAt,@ExpiresAt);
            """,
            token);
    }

    private static RequestAuthority ServiceAuthority(MediaEngine.Domain.Entities.Application application) =>
        new(PrincipalKind.ServiceApplication, true, ApplicationId: application.Id,
            ApplicationEnabled: true,
            ApplicationAuthorizationVersion: application.AuthorizationVersion);

    private ApplicationCredential Credential(Guid applicationId, string name) => new()
    {
        Id = Guid.NewGuid(),
        ApplicationId = applicationId,
        Name = name,
        CredentialHash = Guid.NewGuid().ToString("N"),
        CreatedAt = _clock.GetUtcNow(),
    };

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_databasePath);
        try { File.Delete(_databasePath); } catch (IOException) { }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
