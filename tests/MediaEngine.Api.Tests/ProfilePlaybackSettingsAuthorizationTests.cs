using System.Security.Claims;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Identity;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

public sealed class ProfilePlaybackSettingsAuthorizationTests : IDisposable
{
    private static readonly Guid HouseholdOne = new("7e57a000-0000-0000-0000-000000000001");
    private static readonly Guid HouseholdTwo = new("7e57a000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_settings_authz_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly ProfileRepository _profileRepository;
    private readonly UserPlaybackSettingsService _settings;
    private readonly IServiceProvider _services;

    private readonly Profile _householdOneAdultProfile = NewProfile("Adult one", ProfileRole.StandardUser);
    private readonly Profile _householdOneOtherProfile = NewProfile("Adult two", ProfileRole.StandardUser);
    private readonly Profile _householdOneKidProfile = NewProfile("Kid", ProfileRole.RestrictedProfile);
    private readonly Profile _householdTwoProfile = NewProfile("Other house", ProfileRole.StandardUser);
    private readonly Profile _serverAdminProfile = NewProfile("Server admin", ProfileRole.StandardUser);
    private Account _serverAdmin = null!;
    private Account _householdOneAdmin = null!;
    private Account _householdOnePlain = null!;
    private Account _householdOneKidAdmin = null!;

    public ProfilePlaybackSettingsAuthorizationTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        _profileRepository = new ProfileRepository(_database);
        _settings = new UserPlaybackSettingsService(_database, _profileRepository);

        var decisions = new AccountAccessDecisionService(
            _accounts,
            new GrantAdminUnlockService(_accounts, new PasswordHasher<GrantAdminProtection>(), TimeProvider.System));
        _services = new ServiceCollection()
            .AddSingleton<IRequestAuthorityResolver>(
                new RequestAuthorityResolver(_accounts, new ApplicationRepository(_database), _profileRepository))
            .AddSingleton<IAccountAccessDecisionService>(decisions)
            .AddSingleton<ISelfServiceAuthorizationService>(decisions)
            .AddSingleton<IProfileService>(new ProfileService(_profileRepository))
            .AddSingleton<IAccountRepository>(_accounts)
            .BuildServiceProvider();

        SeedAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Self_MayReadAndSaveOwnSettings()
    {
        var http = Caller(_householdOnePlain, _householdOneAdultProfile);

        Assert.Null(await ProfileEndpoints.AuthorizeProfileSettingsAsync(http, _householdOneAdultProfile.Id));
    }

    [Fact]
    public async Task HouseholdAdministrator_ManagesOwnHouseholdAndTheSavedSettingsComeBack()
    {
        var http = Caller(_householdOneAdmin, _householdOneAdultProfile);

        Assert.Null(await ProfileEndpoints.AuthorizeProfileSettingsAsync(http, _householdOneOtherProfile.Id));

        var update = UserPlaybackSettingsDto.CreateDefaults(_householdOneOtherProfile.Id);
        update.Reading.FontSizePercent = 140;
        await _settings.UpdateAsync(_householdOneOtherProfile.Id, update);

        Assert.Null(await ProfileEndpoints.AuthorizeProfileSettingsAsync(http, _householdOneOtherProfile.Id));
        var saved = await _settings.GetOrCreateDefaultsAsync(_householdOneOtherProfile.Id);
        Assert.Equal(140, saved.Reading.FontSizePercent);
    }

    [Fact]
    public async Task HouseholdAdministrator_CannotManageAnotherHousehold()
    {
        var http = Caller(_householdOneAdmin, _householdOneAdultProfile);

        await AssertForbiddenAsync(http, _householdTwoProfile.Id);
    }

    [Fact]
    public async Task ServerAdministrator_ManagesAnyHousehold()
    {
        var http = Caller(_serverAdmin, _serverAdminProfile);

        Assert.Null(await ProfileEndpoints.AuthorizeProfileSettingsAsync(http, _householdTwoProfile.Id));
        Assert.Null(await ProfileEndpoints.AuthorizeProfileSettingsAsync(http, _householdOneOtherProfile.Id));
    }

    [Fact]
    public async Task NonAdministrator_CannotManageAnotherProfileInTheSameHousehold()
    {
        var http = Caller(_householdOnePlain, _householdOneAdultProfile);

        await AssertForbiddenAsync(http, _householdOneOtherProfile.Id);
    }

    [Fact]
    public async Task KidsCaller_CannotManageAnotherProfile()
    {
        var http = Caller(_householdOneKidAdmin, _householdOneKidProfile);

        await AssertForbiddenAsync(http, _householdOneOtherProfile.Id);
    }

    [Fact]
    public async Task KidsCaller_MayManageOwnSettings()
    {
        var http = Caller(_householdOneKidAdmin, _householdOneKidProfile);

        Assert.Null(await ProfileEndpoints.AuthorizeProfileSettingsAsync(http, _householdOneKidProfile.Id));
    }

    [Fact]
    public async Task HouseholdAdministrator_WithPinProtectionLocked_IsForbidden()
    {
        await _accounts.SetAdminProtectionAsync(new GrantAdminProtection
        {
            AccountId = _householdOneAdmin.Id,
            ProfileId = _householdOneAdultProfile.Id,
            IsEnabled = true,
            UnlockMode = "FixedDuration",
            UnlockMinutes = 30,
            PinHash = "not-used",
            HashScheme = "test",
            ProtectionVersion = 1,
            UpdatedAt = Now,
        });
        var http = Caller(_householdOneAdmin, _householdOneAdultProfile);

        await AssertForbiddenAsync(http, _householdOneOtherProfile.Id);
        // Their own settings never need the unlock.
        Assert.Null(await ProfileEndpoints.AuthorizeProfileSettingsAsync(http, _householdOneAdultProfile.Id));
    }

    [Fact]
    public async Task UnknownProfile_AsAdministrator_IsNotFound()
    {
        var http = Caller(_serverAdmin, _serverAdminProfile);

        var result = await ProfileEndpoints.AuthorizeProfileSettingsAsync(http, Guid.NewGuid());

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_IsRefused()
    {
        var http = new DefaultHttpContext { RequestServices = _services };

        await AssertForbiddenAsync(http, _householdOneOtherProfile.Id);
    }

    private static async Task AssertForbiddenAsync(HttpContext http, Guid profileId)
    {
        var result = await ProfileEndpoints.AuthorizeProfileSettingsAsync(http, profileId);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    private DefaultHttpContext Caller(Account account, Profile activeProfile) => new()
    {
        RequestServices = _services,
        User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(TuvimaClaimTypes.PrincipalKind, PrincipalKind.Human.ToString()),
            new Claim(TuvimaClaimTypes.AccountId, account.Id.ToString("D")),
            new Claim(TuvimaClaimTypes.ActiveProfileId, activeProfile.Id.ToString("D")),
        ], "test")),
    };

    private async Task SeedAsync()
    {
        foreach (var (profile, household) in new[]
        {
            (_householdOneAdultProfile, HouseholdOne),
            (_householdOneOtherProfile, HouseholdOne),
            (_householdOneKidProfile, HouseholdOne),
            (_householdTwoProfile, HouseholdTwo),
            (_serverAdminProfile, HouseholdOne),
        })
        {
            await ProfileTestData.InsertAsync(_database, profile, household);
        }

        _serverAdmin = await CreateAccountAsync(null, administrator: true, householdAdmin: false, _serverAdminProfile, adminGrant: true);
        _householdOneAdmin = await CreateAccountAsync(HouseholdOne, administrator: false, householdAdmin: true, _householdOneAdultProfile, adminGrant: false);
        _householdOnePlain = await CreateAccountAsync(HouseholdOne, administrator: false, householdAdmin: false, _householdOneAdultProfile, adminGrant: false);
        _householdOneKidAdmin = await CreateAccountAsync(HouseholdOne, administrator: false, householdAdmin: true, _householdOneKidProfile, adminGrant: false);
    }

    private async Task<Account> CreateAccountAsync(
        Guid? household, bool administrator, bool householdAdmin, Profile profile, bool adminGrant)
    {
        var id = Guid.NewGuid();
        var account = new Account
        {
            Id = id,
            Email = $"{id:N}@example.com",
            NormalizedEmail = $"{id:N}@EXAMPLE.COM",
            IsEnabled = true,
            IsAdministrator = administrator,
            HouseholdId = household,
            AuthorizationVersion = 1,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        if (householdAdmin)
        {
            account.MakeHouseholdAdmin();
        }

        await _accounts.CreateAccountAsync(
            account,
            new AccountProfileGrant
            {
                AccountId = id,
                ProfileId = profile.Id,
                IsDefault = true,
                IsEnabled = true,
                AdminEnabled = adminGrant,
                AuthorizationVersion = 1,
                GrantedAt = Now,
            },
            AccountFeatureId.All.ToHashSet(),
            new HashSet<Guid>());
        return account;
    }

    private static Profile NewProfile(string name, ProfileRole role) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = name,
        AvatarColor = "#7C4DFF",
        Role = role,
        CreatedAt = Now,
    };

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_databasePath);
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
            // Temp test database; the OS cleans it up later.
        }
    }
}
