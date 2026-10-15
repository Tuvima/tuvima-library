using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Tests;

public sealed class ClientAuthorizationServiceFlowTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly ClientAuthorizationService _authorization;

    public ClientAuthorizationServiceFlowTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_client_auth_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        var accounts = new AccountRepository(_database);
        accounts.InsertAsync(new Account
        {
            Id = Account.SeedAccountId,
            Email = "owner@example.com",
            NormalizedEmail = "OWNER@EXAMPLE.COM",
            IsEnabled = true,
            IsAdministrator = true,
            AuthorizationVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        }).GetAwaiter().GetResult();
        accounts.GrantProfileAsync(new AccountProfileGrant
        {
            AccountId = Account.SeedAccountId,
            ProfileId = Profile.SeedProfileId,
            IsDefault = true,
            IsEnabled = true,
            AdminEnabled = true,
            AuthorizationVersion = 1,
            GrantedAt = DateTimeOffset.UtcNow,
        }).GetAwaiter().GetResult();
        _authorization = new ClientAuthorizationService(
            new ClientAuthorizationRepository(_database),
            accounts,
            new ApplicationRepository(_database),
            new PermissionRegistry(),
            TimeProvider.System);
    }

    [Fact]
    public async Task PairRefreshAndRevoke_EnforcesServerIssuedDeviceIdentity()
    {
        var started = await _authorization.BeginAsync(new DeviceAuthorizationRequest
        {
            ClientId = "tuvima-tv",
            ClientName = "Headless TV test",
            ClientVersion = "1.0.0",
            DeviceName = "Living room television",
            DeviceClass = "television",
            Scope = "library.read progress.read progress.write playback.read playback.write queue.read queue.write",
            Capabilities = new ClientCapabilitiesDto
            {
                Containers = ["mp4"],
                VideoCodecs = ["h264"],
                AudioCodecs = ["aac"],
                MaxHeight = 2160,
            },
        }, "https://library.example");

        Assert.Equal("https://library.example/pair", started.VerificationUri);
        var review = Assert.IsType<PairingReviewResponse>(await _authorization.ReviewAsync(started.UserCode));
        Assert.Equal("television", review.DeviceClass);
        var accountRepository = new AccountRepository(_database);
        var applicationRepository = new ApplicationRepository(_database);
        Assert.True((await accountRepository.GetByIdAsync(Account.SeedAccountId))?.IsEnabled);
        Assert.True((await accountRepository.GetGrantAsync(Account.SeedAccountId, Profile.SeedProfileId))?.IsEnabled);
        Assert.Equal(BuiltInApplicationIds.NativeClient,
            (await applicationRepository.GetApplicationByClientIdAsync("tuvima-tv"))?.Id);
        using (var connection = _database.CreateConnection())
        {
            Assert.Equal(1, connection.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM device_pairing_requests WHERE id=@id AND status='pending' AND expires_at>@now;",
                new { id = review.RequestId, now = DateTimeOffset.UtcNow.ToString("O") }));
            Assert.Equal(BuiltInApplicationIds.NativeClient, connection.QuerySingle<Guid>(
                "SELECT application_id FROM device_pairing_requests WHERE id=@id;", new { id = review.RequestId }));
        }
        Assert.True(await _authorization.DecideAsync(
            new PairingDecisionRequest { UserCode = started.UserCode, Approved = true, Scopes = ["library.read", "progress.read", "progress.write", "playback.read", "playback.write", "queue.read", "queue.write"] },
            Account.SeedAccountId,
            Profile.SeedProfileId,
            Profile.SeedProfileId));

        var issued = await _authorization.ExchangeAsync(new OAuthTokenRequest
        {
            GrantType = ClientAuthorizationService.DeviceGrantType,
            ClientId = "tuvima-tv",
            DeviceCode = started.DeviceCode,
        });
        var token = Assert.IsType<OAuthTokenResponse>(issued.Success);
        var identity = Assert.IsType<ClientAccessIdentity>(await _authorization.ValidateAccessTokenAsync(token.AccessToken));
        Assert.Equal(token.DeviceId, identity.Device.Id);
        Assert.Equal(Profile.SeedProfileId, identity.Device.ProfileId);
        Assert.Equal("television", identity.Device.DeviceClass);
        Assert.Equal("tuvima-tv", identity.Device.ClientId);

        var refreshed = await _authorization.ExchangeAsync(new OAuthTokenRequest
        {
            GrantType = ClientAuthorizationService.RefreshGrantType,
            ClientId = "tuvima-tv",
            RefreshToken = token.RefreshToken,
        });
        var rotated = Assert.IsType<OAuthTokenResponse>(refreshed.Success);
        Assert.NotEqual(token.RefreshToken, rotated.RefreshToken);
        Assert.NotNull(await _authorization.ValidateAccessTokenAsync(rotated.AccessToken));

        Assert.True(await _authorization.RevokeDeviceAsync(token.DeviceId, Profile.SeedProfileId));
        Assert.Null(await _authorization.ValidateAccessTokenAsync(rotated.AccessToken));
    }

    [Fact]
    public async Task Begin_RejectsUnregisteredClientId()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _authorization.BeginAsync(
            new DeviceAuthorizationRequest
            {
                ClientId = "browser-supplied-name",
                ClientName = "Unknown",
                ClientVersion = "1",
                DeviceName = "Unknown",
                DeviceClass = "television",
                Scope = "library.read",
                Capabilities = new ClientCapabilitiesDto(),
            },
            "https://library.example"));
    }

    [Fact]
    public async Task ApprovedPairing_DeniesExchangeWhenClientBindingIsRemoved()
    {
        var started = await _authorization.BeginAsync(new DeviceAuthorizationRequest
        {
            ClientId = "tuvima-tv",
            ClientName = "TV",
            ClientVersion = "1",
            DeviceName = "Living room",
            DeviceClass = "television",
            Scope = "library.read",
            Capabilities = new ClientCapabilitiesDto(),
        }, "https://library.example");
        Assert.True(await _authorization.DecideAsync(
            new PairingDecisionRequest { UserCode = started.UserCode, Approved = true, Scopes = ["library.read"] },
            Account.SeedAccountId,
            Profile.SeedProfileId,
            Profile.SeedProfileId));

        await new ApplicationRepository(_database).ReplaceClientBindingsAsync(
            BuiltInApplicationIds.NativeClient,
            new HashSet<string>(StringComparer.Ordinal),
            DateTimeOffset.UtcNow);

        var result = await _authorization.ExchangeAsync(new OAuthTokenRequest
        {
            GrantType = ClientAuthorizationService.DeviceGrantType,
            ClientId = "tuvima-tv",
            DeviceCode = started.DeviceCode,
        });

        Assert.Null(result.Success);
        Assert.Equal("access_denied", result.Error?.Error);
    }

    [Fact]
    public async Task IssuedTokens_AreInvalidAfterClientBindingRemoval()
    {
        var token = await IssueTvTokenAsync();
        await new ApplicationRepository(_database).ReplaceClientBindingsAsync(
            BuiltInApplicationIds.NativeClient,
            new HashSet<string>(StringComparer.Ordinal),
            DateTimeOffset.UtcNow);

        Assert.Null(await _authorization.ValidateAccessTokenAsync(token.AccessToken));
        var refreshed = await _authorization.ExchangeAsync(new OAuthTokenRequest
        {
            GrantType = ClientAuthorizationService.RefreshGrantType,
            ClientId = "tuvima-tv",
            RefreshToken = token.RefreshToken,
        });
        Assert.Null(refreshed.Success);
        Assert.Equal("invalid_grant", refreshed.Error?.Error);
    }

    [Fact]
    public async Task ApprovedPairing_DeniesExchangeWhenBindingIsReassigned()
    {
        var started = await StartApprovedTvPairingAsync();
        var applications = new ApplicationRepository(_database);
        var replacement = new MediaEngine.Domain.Entities.Application
        {
            Id = Guid.NewGuid(),
            Name = "Replacement TV client",
            ApplicationType = ApplicationType.UserClient,
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await applications.InsertApplicationAsync(
            replacement,
            new HashSet<ApplicationPermissionId> { ApplicationPermissionIds.LibraryRead });
        await applications.ReplaceClientBindingsAsync(
            BuiltInApplicationIds.NativeClient,
            new HashSet<string>(StringComparer.Ordinal),
            DateTimeOffset.UtcNow);
        await applications.ReplaceClientBindingsAsync(
            replacement.Id,
            new HashSet<string>(StringComparer.Ordinal) { "tuvima-tv" },
            DateTimeOffset.UtcNow);

        var result = await ExchangeTvAsync(started);
        Assert.Null(result.Success);
        Assert.Equal("access_denied", result.Error?.Error);
    }

    [Fact]
    public async Task ApprovedPairing_DeniesExchangeWhenApplicationTypeChanges()
    {
        var started = await StartApprovedTvPairingAsync();
        var applications = new ApplicationRepository(_database);
        var native = (await applications.GetApplicationAsync(BuiltInApplicationIds.NativeClient))!;
        native.ApplicationType = ApplicationType.ServerIntegration;
        native.UpdatedAt = DateTimeOffset.UtcNow;
        await applications.UpdateApplicationAsync(native);

        var result = await ExchangeTvAsync(started);
        Assert.Null(result.Success);
        Assert.Equal("access_denied", result.Error?.Error);
    }

    private async Task<OAuthTokenResponse> IssueTvTokenAsync()
    {
        var started = await StartApprovedTvPairingAsync();
        return Assert.IsType<OAuthTokenResponse>((await ExchangeTvAsync(started)).Success);
    }

    private async Task<DeviceAuthorizationResponse> StartApprovedTvPairingAsync()
    {
        var started = await _authorization.BeginAsync(new DeviceAuthorizationRequest
        {
            ClientId = "tuvima-tv",
            ClientName = "TV",
            ClientVersion = "1",
            DeviceName = "Living room",
            DeviceClass = "television",
            Scope = "library.read",
            Capabilities = new ClientCapabilitiesDto(),
        }, "https://library.example");
        Assert.True(await _authorization.DecideAsync(
            new PairingDecisionRequest { UserCode = started.UserCode, Approved = true, Scopes = ["library.read"] },
            Account.SeedAccountId,
            Profile.SeedProfileId,
            Profile.SeedProfileId));
        return started;
    }

    private Task<ClientTokenResult> ExchangeTvAsync(DeviceAuthorizationResponse started) =>
        _authorization.ExchangeAsync(new OAuthTokenRequest
        {
            GrantType = ClientAuthorizationService.DeviceGrantType,
            ClientId = "tuvima-tv",
            DeviceCode = started.DeviceCode,
        });

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }
}
