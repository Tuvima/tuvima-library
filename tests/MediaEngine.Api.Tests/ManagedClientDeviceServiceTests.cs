using MediaEngine.Api.Security;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class ManagedClientDeviceServiceTests : IDisposable
{
    private static readonly Guid OtherAccountId = new("00000000-0000-0000-0000-0000000000a2");

    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly ClientAuthorizationService _authorization;
    private readonly SpyNotifier _notifier = new();
    private readonly ManagedClientDeviceService _service;

    public ManagedClientDeviceServiceTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_managed_devices_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        var accounts = new AccountRepository(_database);
        AddAccount(accounts, Account.SeedAccountId, "owner@example.com", administrator: true);
        AddAccount(accounts, OtherAccountId, "member@example.com", administrator: false);
        var applications = new ApplicationRepository(_database);
        _authorization = new ClientAuthorizationService(
            new ClientAuthorizationRepository(_database), accounts, applications, new PermissionRegistry(), TimeProvider.System);
        _service = new ManagedClientDeviceService(
            new ClientAuthorizationRepository(_database), accounts, new ProfileRepository(_database), _notifier, TimeProvider.System);
    }

    [Fact]
    public async Task Administrator_SeesEveryDevice_AndStandardUserSeesOnlyTheirOwn()
    {
        var ownerTv = await PairAsync(Account.SeedAccountId, "Living room TV", "television");
        var memberPhone = await PairAsync(OtherAccountId, "Member phone", "mobile");

        var all = await _service.ListAsync(Administrator());
        Assert.Equal(
            new[] { ownerTv.DeviceId, memberPhone.DeviceId }.Order(),
            all.Select(device => device.Id).Order());
        var phone = Assert.Single(all, device => device.Id == memberPhone.DeviceId);
        Assert.Equal("member@example.com", phone.AccountDisplayName);
        Assert.Equal("mobile", phone.Platform);
        Assert.Equal("Member phone", phone.DeviceName);

        var own = await _service.ListAsync(Member());
        Assert.Equal(memberPhone.DeviceId, Assert.Single(own).Id);
    }

    [Fact]
    public async Task StandardUser_CannotRevokeAnotherAccountsDevice()
    {
        var ownerTv = await PairAsync(Account.SeedAccountId, "Living room TV", "television");

        Assert.Equal(ManagedDeviceRevokeOutcome.Forbidden, await _service.RevokeAsync(Member(), ownerTv.DeviceId));

        Assert.NotNull(await _authorization.ValidateAccessTokenAsync(ownerTv.AccessToken));
        Assert.Empty(_notifier.Revoked);
    }

    [Fact]
    public async Task Revoke_EndsTheDeviceTokens_AndTellsTheApp()
    {
        var memberPhone = await PairAsync(OtherAccountId, "Member phone", "mobile");

        Assert.Equal(ManagedDeviceRevokeOutcome.Revoked, await _service.RevokeAsync(Administrator(), memberPhone.DeviceId));

        Assert.Null(await _authorization.ValidateAccessTokenAsync(memberPhone.AccessToken));
        Assert.Equal([memberPhone.DeviceId], _notifier.Revoked);
        Assert.Empty(await _service.ListAsync(Administrator()));
        Assert.Equal(ManagedDeviceRevokeOutcome.NotFound, await _service.RevokeAsync(Administrator(), memberPhone.DeviceId));
    }

    [Fact]
    public async Task StandardUser_CanRevokeTheirOwnDevice()
    {
        var memberPhone = await PairAsync(OtherAccountId, "Member phone", "mobile");

        Assert.Equal(ManagedDeviceRevokeOutcome.Revoked, await _service.RevokeAsync(Member(), memberPhone.DeviceId));
        Assert.Null(await _authorization.ValidateAccessTokenAsync(memberPhone.AccessToken));
    }

    public void Dispose()
    {
        _database.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static RequestAuthority Administrator() => new(
        PrincipalKind.Human, true, Account.SeedAccountId, Profile.SeedProfileId,
        AccountEnabled: true, GrantEnabled: true, AccountIsAdministrator: true, GrantAdminEnabled: true);

    private static RequestAuthority Member() => new(
        PrincipalKind.Human, true, OtherAccountId, Profile.SeedProfileId,
        AccountEnabled: true, GrantEnabled: true);

    private async Task<OAuthTokenResponse> PairAsync(Guid accountId, string deviceName, string deviceClass)
    {
        var started = await _authorization.BeginAsync(new DeviceAuthorizationRequest
        {
            ClientId = "tuvima-tv",
            ClientName = "Test app",
            ClientVersion = "1.0.0",
            DeviceName = deviceName,
            DeviceClass = deviceClass,
            Scope = "library.read",
            Capabilities = new ClientCapabilitiesDto { Containers = ["mp4"], VideoCodecs = ["h264"], AudioCodecs = ["aac"] },
        }, "https://library.example");
        Assert.True(await _authorization.DecideAsync(
            new PairingDecisionRequest { UserCode = started.UserCode, Approved = true },
            accountId, Profile.SeedProfileId, Profile.SeedProfileId));
        var issued = await _authorization.ExchangeAsync(new OAuthTokenRequest
        {
            GrantType = ClientAuthorizationService.DeviceGrantType,
            ClientId = "tuvima-tv",
            DeviceCode = started.DeviceCode,
        });
        return Assert.IsType<OAuthTokenResponse>(issued.Success);
    }

    private static void AddAccount(AccountRepository accounts, Guid id, string email, bool administrator)
    {
        accounts.InsertAsync(new Account
        {
            Id = id,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            IsEnabled = true,
            IsAdministrator = administrator,
            AuthorizationVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        }).GetAwaiter().GetResult();
        accounts.GrantProfileAsync(new AccountProfileGrant
        {
            AccountId = id,
            ProfileId = Profile.SeedProfileId,
            IsDefault = true,
            IsEnabled = true,
            AdminEnabled = administrator,
            AuthorizationVersion = 1,
            GrantedAt = DateTimeOffset.UtcNow,
        }).GetAwaiter().GetResult();
    }

    private sealed class SpyNotifier : IDeviceRevocationNotifier
    {
        public List<Guid> Revoked { get; } = [];

        public Task NotifyDeviceRevokedAsync(Guid deviceId, CancellationToken ct = default)
        {
            Revoked.Add(deviceId);
            return Task.CompletedTask;
        }
    }
}
