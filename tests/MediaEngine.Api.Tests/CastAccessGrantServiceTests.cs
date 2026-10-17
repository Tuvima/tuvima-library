using Dapper;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Tests;

public sealed class CastAccessGrantServiceTests : IDisposable
{
    private static readonly string[] PlaybackRead = [ApplicationPermissionIds.PlaybackRead.Value];

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"tuvima_cast_grants_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly ApplicationRepository _applications;
    private readonly IdentityRepository _identities;
    private readonly ClientAuthorizationRepository _clients;
    private readonly EphemeralDataProtectionProvider _protection = new();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));

    public CastAccessGrantServiceTests()
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
    public async Task GrantRoundTripsForTheAssetItWasMadeFor()
    {
        var paired = await PairAsync();
        var assetId = Guid.NewGuid();
        var grant = await CreateService().CreateAsync(assetId, null, paired.Authority, paired.Token.Id, PlaybackRead, 5400);

        var validation = await CreateService().ValidateAsync(grant.Value);

        Assert.NotNull(validation);
        Assert.Equal(assetId, validation.AssetId);
        Assert.Null(validation.PackageId);
        Assert.Equal(paired.Account.Id, validation.Authority.AccountId);
        Assert.Equal(paired.Device.Id, validation.Authority.DeviceId);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(90 + 90), grant.ExpiresAt);
    }

    [Fact]
    public async Task GrantFromADifferentProtectorIsRefused()
    {
        var paired = await PairAsync();
        var grant = await CreateService().CreateAsync(Guid.NewGuid(), null, paired.Authority, paired.Token.Id, PlaybackRead, 5400);
        var other = new CastAccessGrantService(
            new EphemeralDataProtectionProvider(), _accounts, _applications, _clients, new PermissionRegistry(), _clock);

        Assert.Null(await other.ValidateAsync(grant.Value));
    }

    [Fact]
    public async Task HlsAndCastGrantsAreNotInterchangeable()
    {
        var paired = await PairAsync();
        var packageId = Guid.NewGuid();
        var hls = new HlsAccessGrantService(
            _protection,
            new ConfigurationDirectoryLoader(Path.Combine(FindRepoRoot(), "config")),
            _accounts, _applications, _identities, _clients, new PermissionRegistry(), _clock);
        var hlsGrant = hls.Create(Guid.NewGuid(), packageId, paired.Authority, paired.Token.Id, PlaybackRead);
        var cast = CreateService();
        var castGrant = await cast.CreateAsync(Guid.NewGuid(), packageId, paired.Authority, paired.Token.Id, PlaybackRead, 5400);

        Assert.Null(await cast.ValidateAsync(hlsGrant.Value));
        Assert.Null(await hls.ValidateAsync(castGrant.Value, packageId));
    }

    [Fact]
    public async Task ExpiredGrantIsRefused()
    {
        var paired = await PairAsync();
        var service = CreateService();
        var grant = await service.CreateAsync(Guid.NewGuid(), null, paired.Authority, paired.Token.Id, PlaybackRead, 3600);
        Assert.NotNull(await service.ValidateAsync(grant.Value));

        // One hour of film plus ninety minutes is two and a half hours.
        _clock.Advance(TimeSpan.FromHours(2).Add(TimeSpan.FromMinutes(31)));

        Assert.Null(await service.ValidateAsync(grant.Value));
    }

    [Fact]
    public async Task TamperedOrMissingGrantIsRefused()
    {
        var paired = await PairAsync();
        var service = CreateService();
        var grant = await service.CreateAsync(Guid.NewGuid(), null, paired.Authority, paired.Token.Id, PlaybackRead, 3600);
        var replacement = grant.Value[^1] == 'a' ? 'b' : 'a';

        Assert.Null(await service.ValidateAsync(grant.Value[..^1] + replacement));
        Assert.Null(await service.ValidateAsync(null));
        Assert.Null(await service.ValidateAsync(string.Empty));
        Assert.Null(await service.ValidateAsync(new string('a', 5000)));
    }

    [Fact]
    public async Task RevokingTheDeviceStopsTheGrant()
    {
        var paired = await PairAsync();
        var service = CreateService();
        var grant = await service.CreateAsync(Guid.NewGuid(), null, paired.Authority, paired.Token.Id, PlaybackRead, 3600);
        Assert.NotNull(await service.ValidateAsync(grant.Value));

        Assert.True(await _clients.RevokeDeviceByIdAsync(paired.Device.Id, _clock.GetUtcNow(), "test"));

        Assert.Null(await service.ValidateAsync(grant.Value));
    }

    [Fact]
    public async Task RevokingTheTokenFamilyStopsTheGrant()
    {
        var paired = await PairAsync();
        var service = CreateService();
        var grant = await service.CreateAsync(Guid.NewGuid(), null, paired.Authority, paired.Token.Id, PlaybackRead, 3600);
        Assert.NotNull(await service.ValidateAsync(grant.Value));

        await _clients.RevokeTokenFamilyAsync(paired.Token.TokenFamilyId, _clock.GetUtcNow(), "test");

        Assert.Null(await service.ValidateAsync(grant.Value));
    }

    [Fact]
    public async Task ASwitchToAnotherProfileStopsTheGrant()
    {
        var paired = await PairAsync();
        var service = CreateService();
        var grant = await service.CreateAsync(Guid.NewGuid(), null, paired.Authority, paired.Token.Id, PlaybackRead, 3600);
        var other = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                "INSERT INTO profiles (id, display_name, created_at) VALUES (@id, 'Other', @now);" +
                "UPDATE client_devices SET profile_id = @id WHERE id = @device;",
                new { id = other, now = _clock.GetUtcNow(), device = paired.Device.Id });
        }

        Assert.Null(await service.ValidateAsync(grant.Value));
    }

    [Theory]
    [InlineData("UPDATE accounts SET authorization_version = authorization_version + 1 WHERE id = @account")]
    [InlineData("UPDATE accounts SET is_enabled = 0 WHERE id = @account")]
    [InlineData("UPDATE account_profile_grants SET authorization_version = authorization_version + 1 WHERE account_id = @account")]
    [InlineData("UPDATE account_profile_grants SET is_enabled = 0 WHERE account_id = @account")]
    [InlineData("UPDATE applications SET authorization_version = authorization_version + 1 WHERE id = @application")]
    [InlineData("UPDATE applications SET is_enabled = 0 WHERE id = @application")]
    public async Task AnyAuthorisationChangeStopsTheGrant(string statement)
    {
        var paired = await PairAsync();
        var service = CreateService();
        var grant = await service.CreateAsync(Guid.NewGuid(), null, paired.Authority, paired.Token.Id, PlaybackRead, 3600);
        Assert.NotNull(await service.ValidateAsync(grant.Value));

        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(statement, new { account = paired.Account.Id, application = paired.Application.Id });
        }

        Assert.Null(await service.ValidateAsync(grant.Value));
    }

    [Fact]
    public async Task RemovingThePlaybackPermissionStopsTheGrant()
    {
        var paired = await PairAsync();
        var service = CreateService();
        var grant = await service.CreateAsync(Guid.NewGuid(), null, paired.Authority, paired.Token.Id, PlaybackRead, 3600);
        Assert.NotNull(await service.ValidateAsync(grant.Value));

        await _applications.ReplacePermissionsAsync(paired.Application.Id, new HashSet<ApplicationPermissionId>(), _clock.GetUtcNow());

        Assert.Null(await service.ValidateAsync(grant.Value));
    }

    [Fact]
    public async Task OnlyALivePairedDeviceWithPlaybackReadCanCreateAGrant()
    {
        var paired = await PairAsync();
        var service = CreateService();
        var assetId = Guid.NewGuid();

        // No playback.read scope on the token.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.CreateAsync(assetId, null, paired.Authority, paired.Token.Id, [], 3600));
        // No token id.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.CreateAsync(assetId, null, paired.Authority, null, PlaybackRead, 3600));
        // An unknown token.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.CreateAsync(assetId, null, paired.Authority, Guid.NewGuid(), PlaybackRead, 3600));
        // A signed-in person (not a paired device) and a service application.
        var human = new RequestAuthority(
            PrincipalKind.Human, true, paired.Account.Id, Profile.SeedProfileId, SessionId: Guid.NewGuid(),
            AccountEnabled: true, GrantEnabled: true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.CreateAsync(assetId, null, human, paired.Token.Id, PlaybackRead, 3600));
        var serviceApp = new RequestAuthority(
            PrincipalKind.ServiceApplication, true, ApplicationId: paired.Application.Id, ApplicationEnabled: true,
            ApplicationAuthorizationVersion: paired.Application.AuthorizationVersion);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.CreateAsync(assetId, null, serviceApp, paired.Token.Id, PlaybackRead, 3600));
        // A revoked token.
        await _clients.RevokeTokenFamilyAsync(paired.Token.TokenFamilyId, _clock.GetUtcNow(), "test");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.CreateAsync(assetId, null, paired.Authority, paired.Token.Id, PlaybackRead, 3600));
    }

    [Theory]
    [InlineData(null, 2 * 60)]
    [InlineData(600d, 2 * 60)]
    [InlineData(5400d, 3 * 60)]
    [InlineData(3 * 3600d, 4 * 60 + 30)]
    [InlineData(20 * 3600d, 6 * 60)]
    public void LifetimeIsFilmLengthPlusNinetyMinutesClampedToTwoToSixHours(double? seconds, int expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), CastAccessGrantService.LifetimeFor(seconds));
    }

    [Fact]
    public void LogIdIsEightHexCharactersAndNeverTheGrant()
    {
        var id = CastAccessGrantService.LogId("a-secret-grant-value");

        Assert.Matches("^[0-9a-f]{8}$", id);
    }

    private CastAccessGrantService CreateService() =>
        new(_protection, _accounts, _applications, _clients, new PermissionRegistry(), _clock);

    private sealed record Paired(
        Account Account,
        MediaEngine.Domain.Entities.Application Application,
        ClientDevice Device,
        ClientToken Token,
        RequestAuthority Authority);

    private async Task<Paired> PairAsync()
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

        var application = new MediaEngine.Domain.Entities.Application
        {
            Id = Guid.NewGuid(),
            Name = "Cast test",
            ApplicationType = ApplicationType.UserClient,
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = _clock.GetUtcNow(),
            UpdatedAt = _clock.GetUtcNow(),
        };
        await _applications.InsertApplicationAsync(
            application, new HashSet<ApplicationPermissionId> { ApplicationPermissionIds.PlaybackRead });
        await _applications.ReplaceClientBindingsAsync(application.Id, new HashSet<string> { "phone" }, _clock.GetUtcNow());
        application = (await _applications.GetApplicationAsync(application.Id))!;
        var accountGrant = (await _accounts.GetGrantAsync(account.Id, Profile.SeedProfileId))!;

        var device = new ClientDevice
        {
            Id = Guid.NewGuid(),
            ApplicationId = application.Id,
            AccountId = account.Id,
            ProfileId = Profile.SeedProfileId,
            DeviceName = "Phone",
            DeviceClass = "mobile",
            ClientId = "phone",
            ClientName = "Phone",
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
            ExpiresAt = _clock.GetUtcNow().AddDays(30),
        };
        using (var connection = _database.CreateConnection())
        {
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

        var authority = new RequestAuthority(
            PrincipalKind.DelegatedUserClient, true, account.Id, Profile.SeedProfileId, application.Id,
            DeviceId: device.Id, AccountEnabled: true, GrantEnabled: true, ApplicationEnabled: true,
            AccountAuthorizationVersion: account.AuthorizationVersion,
            GrantAuthorizationVersion: accountGrant.AuthorizationVersion,
            ApplicationAuthorizationVersion: application.AuthorizationVersion);
        return new Paired(account, application, device, token, authority);
    }

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
        using (var pool = new SqliteConnection($"Data Source={_databasePath}"))
        {
            SqliteConnection.ClearPool(pool);
        }
        try { File.Delete(_databasePath); } catch (IOException) { }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
