using System.Security.Claims;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

public sealed class AccessAuthorityIntegrationTests : IDisposable
{
    [Theory]
    [InlineData("review.read", "review.resolve")]
    [InlineData("providers.config.read", "providers.config.write")]
    [InlineData("ai.status.read", "ai.manage")]
    [InlineData("network.status.read", "network.config.write")]
    [InlineData("storage.status.read", "storage.config.write")]
    [InlineData("ingestion.status.read", "ingestion.run")]
    public async Task ServiceReadGrantCannotMutate_AndRevocationAppliesImmediately(string readId, string writeId)
    {
        var read = new ApplicationPermissionId(readId);
        var write = new ApplicationPermissionId(writeId);
        var ordinaryId = Guid.NewGuid();
        var ordinaryProfileId = await CreateAccountWithOwnProfileAsync(Account(ordinaryId, administrator: false), AccountFeatureId.All.ToHashSet());
        var application = await CreateApplicationAsync(ApplicationType.ServerIntegration, administrator: false);
        await _applications.ReplacePermissionsAsync(application.Id, new HashSet<ApplicationPermissionId> { read }, _clock.GetUtcNow());
        var handler = new AdministratorOrApplicationHandler(new RequestAuthorityResolver(_accounts, _applications), CreateDecisions(), CreateEvaluator().Evaluator);
        var service = ContextWithAuthority(PrincipalKind.ServiceApplication, Guid.Empty, Guid.Empty, application.Id);
        async Task<bool> Allows(HttpContext http, ApplicationPermissionId permission)
        {
            var context = new AuthorizationHandlerContext([new AdministratorOrApplicationRequirement(permission)], http.User, http);
            await handler.HandleAsync(context);
            return context.HasSucceeded;
        }
        Assert.False(await Allows(ContextWithAuthority(PrincipalKind.Human, ordinaryId, ordinaryProfileId), read));
        Assert.True(await Allows(service, read));
        Assert.False(await Allows(service, write));
        await _applications.ReplacePermissionsAsync(application.Id, new HashSet<ApplicationPermissionId> { write }, _clock.GetUtcNow());
        Assert.False(await Allows(service, read));
        Assert.True(await Allows(service, write));
        application.IsEnabled = false;
        await _applications.UpdateApplicationAsync(application);
        Assert.False(await Allows(service, write));
    }

    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly ApplicationRepository _applications;
    private readonly ManualTimeProvider _clock =
        new(new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly Guid _accountId = Guid.NewGuid();

    public AccessAuthorityIntegrationTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_authority_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        _applications = new ApplicationRepository(_database);

        _accounts.CreateAccountAsync(
            Account(_accountId, administrator: true),
            Grant(_accountId, administrator: true),
            AccountFeatureId.All.ToHashSet(),
            new HashSet<Guid>()).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task MetadataOperations_RejectOrdinaryAccountsAndUseLiveExactApplicationPermissions()
    {
        var ordinaryId = Guid.NewGuid();
        var ordinaryProfileId = await CreateAccountWithOwnProfileAsync(Account(ordinaryId, administrator: false), AccountFeatureId.All.ToHashSet());
        var application = await CreateApplicationAsync(ApplicationType.ServerIntegration, administrator: false);
        await _applications.ReplacePermissionsAsync(application.Id, new HashSet<ApplicationPermissionId> { ApplicationPermissionIds.MetadataRead }, _clock.GetUtcNow());
        var resolver = new RequestAuthorityResolver(_accounts, _applications);
        var handler = new AdministratorOrApplicationHandler(resolver, CreateDecisions(), CreateEvaluator().Evaluator);
        var ordinary = ContextWithAuthority(PrincipalKind.Human, ordinaryId, ordinaryProfileId);
        var service = ContextWithAuthority(PrincipalKind.ServiceApplication, Guid.Empty, Guid.Empty, application.Id);

        async Task<bool> Allows(HttpContext http, ApplicationPermissionId permission)
        {
            var requirement = new AdministratorOrApplicationRequirement(permission);
            var authorization = new AuthorizationHandlerContext([requirement], http.User, http);
            await handler.HandleAsync(authorization);
            return authorization.HasSucceeded;
        }

        Assert.False(await Allows(ordinary, ApplicationPermissionIds.MetadataRead));
        Assert.False(await Allows(ordinary, ApplicationPermissionIds.MetadataWrite));
        Assert.True(await Allows(service, ApplicationPermissionIds.MetadataRead));
        Assert.False(await Allows(service, ApplicationPermissionIds.MetadataWrite));
        Assert.False(await Allows(service, ApplicationPermissionIds.MetadataMatch));
        Assert.False(await Allows(service, ApplicationPermissionIds.MetadataEnrichmentRun));

        await _applications.ReplacePermissionsAsync(application.Id, new HashSet<ApplicationPermissionId> { ApplicationPermissionIds.MetadataWrite }, _clock.GetUtcNow());
        Assert.False(await Allows(service, ApplicationPermissionIds.MetadataRead));
        Assert.True(await Allows(service, ApplicationPermissionIds.MetadataWrite));
        application.IsEnabled = false;
        await _applications.UpdateApplicationAsync(application);
        Assert.False(await Allows(service, ApplicationPermissionIds.MetadataWrite));
    }

    [Fact]
    public async Task MetadataReadRoute_RefusesAHouseholdAdministratorUnlessTheRouteAdmitsHouseholdAdministrators()
    {
        var householdAdmin = new RequestAuthority(
            PrincipalKind.Human,
            true,
            AccountId: Guid.NewGuid(),
            ActiveProfileId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            AccountEnabled: true,
            ActiveProfileInUserHousehold: true,
            AccountAuthorizationVersion: 1,
            ProfileAuthorizationVersion: 1,
            ActiveProfileHouseholdId: Guid.NewGuid(),
            ActiveProfileIsHouseholdAdministrator: true);
        var handler = new AdministratorOrApplicationHandler(
            new FixedAuthorityResolver(householdAdmin), CreateDecisions(), CreateEvaluator().Evaluator);
        var http = ContextWithAuthority(PrincipalKind.Human, householdAdmin.AccountId!.Value, householdAdmin.ActiveProfileId!.Value);

        async Task<bool> Allows(AdministratorOrApplicationRequirement requirement)
        {
            var authorization = new AuthorizationHandlerContext([requirement], http.User, http);
            await handler.HandleAsync(authorization);
            return authorization.HasSucceeded;
        }

        // The control proves this authority is a valid household administrator; only the route decides.
        Assert.True(await Allows(new AdministratorOrApplicationRequirement(
            ApplicationPermissionIds.MetadataRead, allowHouseholdAdministrator: true)));
        Assert.False(await Allows(new AdministratorOrApplicationRequirement(ApplicationPermissionIds.MetadataRead)));
    }

    [Fact]
    public async Task Evaluator_UsesRegistryAvailabilityBeforeDynamicAdministratorApplicationGrant()
    {
        var application = await CreateApplicationAsync(ApplicationType.ServerIntegration, administrator: true);
        var (evaluator, _) = CreateEvaluator();

        var authority = ServiceAuthority(application);
        var available = await evaluator.EvaluateAsync(
            authority,
            new AuthorizationRequirement(ApplicationPermission: ApplicationPermissionIds.IdentityApplicationsWrite),
            null);
        var unavailable = await evaluator.EvaluateAsync(
            authority,
            new AuthorizationRequirement(ApplicationPermission: ApplicationPermissionIds.SystemMetricsRead),
            null);

        Assert.True(available.IsAllowed);
        Assert.Equal(AuthorizationDenialReason.PermissionUnavailable, unavailable.DenialReason);
    }

    [Fact]
    public async Task Evaluator_RestrictsAdministratorViewExceptionToAuditedReadPermission()
    {
        var application = await CreateApplicationAsync(ApplicationType.ServerIntegration, administrator: true);
        var (evaluator, _) = CreateEvaluator();
        var authority = ServiceAuthority(application);
        var targetProfile = Guid.NewGuid();
        var resource = new ResourceAuthorizationContext(
            "view-profile-admin-read",
            targetProfile.ToString("D"),
            OwnerProfileId: targetProfile,
            IsPrivate: true);

        var read = await evaluator.EvaluateAsync(
            authority,
            new AuthorizationRequirement(ApplicationPermission: ApplicationPermissionIds.ViewPersonalRead),
            resource);
        var write = await evaluator.EvaluateAsync(
            authority,
            new AuthorizationRequirement(ApplicationPermission: ApplicationPermissionIds.ViewGalleriesWrite),
            resource);

        Assert.True(read.IsAllowed);
        Assert.Equal(AuthorizationDenialReason.MissingHumanContext, write.DenialReason);
        using var connection = _database.CreateConnection();
        Assert.Equal(1, connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM authorization_audit_events WHERE event_type='view.admin_application_profile_read';"));
    }

    [Fact]
    public async Task ProfileSelfServiceResource_RequiresTheActiveProfileAndLiveDelegatedApplication()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRequestAuthorityResolver>(
            new RequestAuthorityResolver(_accounts, _applications));
        services.AddSingleton<ISelfServiceAuthorizationService>(CreateDecisions());
        services.AddSingleton<IAccountRepository>(_accounts);
        services.AddSingleton<IProfileAccessRepository>(new ProfileAccessRepository(_database));
        using var provider = services.BuildServiceProvider();

        var human = ContextWithAuthority(PrincipalKind.Human, _accountId, Profile.SeedProfileId);
        human.RequestServices = provider;
        Assert.True(await ProfileEndpoints.IsActiveProfileAuthorizedAsync(
            human, Profile.SeedProfileId));
        Assert.False(await ProfileEndpoints.IsActiveProfileAuthorizedAsync(
            human, Guid.NewGuid()));

        var application = await CreateApplicationAsync(ApplicationType.UserClient, administrator: false);
        application.IsEnabled = false;
        await _applications.UpdateApplicationAsync(application);
        var delegated = ContextWithAuthority(
            PrincipalKind.DelegatedUserClient,
            _accountId,
            Profile.SeedProfileId,
            application.Id);
        delegated.RequestServices = provider;
        Assert.False(await ProfileEndpoints.IsActiveProfileAuthorizedAsync(
            delegated, Profile.SeedProfileId));
    }

    [Fact]
    public async Task ProfileIdentityRead_AllowsAProfileOfTheUsersHouseholdButDeniesOneOutsideItOrMissing()
    {
        var profileId = Guid.NewGuid();
        await ProfileTestData.InsertAsync(_database, new Profile
        {
            Id = profileId,
            DisplayName = "Second",
            AvatarColor = "#123456",
            Role = ProfileRole.RestrictedProfile,
            CreatedAt = _clock.GetUtcNow(),
        }, (await new HouseholdRepository(_database).GetForAccountAsync(_accountId))!.Id);
        var services = new ServiceCollection();
        services.AddSingleton<IRequestAuthorityResolver>(
            new RequestAuthorityResolver(_accounts, _applications));
        services.AddSingleton<IAccountRepository>(_accounts);
        services.AddSingleton<IProfileAccessRepository>(new ProfileAccessRepository(_database));
        using var provider = services.BuildServiceProvider();
        var context = ContextWithAuthority(PrincipalKind.Human, _accountId, Profile.SeedProfileId);
        context.RequestServices = provider;

        Assert.True(await ProfileEndpoints.IsGrantedProfileIdentityAuthorizedAsync(context, profileId));
        Assert.False(await ProfileEndpoints.IsGrantedProfileIdentityAuthorizedAsync(context, Guid.NewGuid()));

        // A profile that is no longer in the user's household is no longer one the user can act as.
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE profiles SET household_id = NULL WHERE id = @profileId;", new { profileId });
        }

        Assert.False(await ProfileEndpoints.IsGrantedProfileIdentityAuthorizedAsync(context, profileId));
    }

    [Fact]
    public async Task HumanSelfServicePolicy_DeniesDelegatedClientWithoutADeclaredServicePermission()
    {
        var application = await CreateApplicationAsync(ApplicationType.UserClient, administrator: false);
        var resolver = new RequestAuthorityResolver(_accounts, _applications);
        var requirement = new HumanSelfServiceRequirement();
        var handler = new HumanSelfServiceHandler(resolver);
        var delegated = ContextWithAuthority(
            PrincipalKind.DelegatedUserClient,
            _accountId,
            Profile.SeedProfileId,
            application.Id);
        var delegatedAuthorization = new AuthorizationHandlerContext(
            [requirement], delegated.User, delegated);

        await handler.HandleAsync(delegatedAuthorization);

        Assert.False(delegatedAuthorization.HasSucceeded);

        var human = ContextWithAuthority(PrincipalKind.Human, _accountId, Profile.SeedProfileId);
        var humanAuthorization = new AuthorizationHandlerContext(
            [requirement], human.User, human);
        await handler.HandleAsync(humanAuthorization);
        Assert.True(humanAuthorization.HasSucceeded);
    }

    [Fact]
    public async Task ClientScopeFilter_UsesLiveAuthorityInsteadOfDashboardTransportOrScopeClaimsAlone()
    {
        var application = await CreateApplicationAsync(ApplicationType.UserClient, administrator: false);
        var delegated = ContextWithAuthority(
            PrincipalKind.DelegatedUserClient,
            _accountId,
            Profile.SeedProfileId,
            application.Id);
        ((ClaimsIdentity)delegated.User.Identity!).AddClaim(
            new Claim(TuvimaClaimTypes.Scope, ApplicationPermissionIds.LibraryRead.Value));
        var accessor = new HttpContextAccessor { HttpContext = delegated };
        var services = new ServiceCollection();
        services.AddSingleton<IRequestAuthorityResolver>(
            new RequestAuthorityResolver(_accounts, _applications));
        services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator>(
            CreateEvaluator(accessor).Evaluator);
        using var provider = services.BuildServiceProvider();
        delegated.RequestServices = provider;
        var filter = new ClientScopeFilter(ApplicationPermissionIds.LibraryRead.Value);

        Assert.False(await filter.IsAllowedAsync(delegated));
        await _applications.ReplacePermissionsAsync(
            application.Id,
            new HashSet<ApplicationPermissionId> { ApplicationPermissionIds.LibraryRead },
            _clock.GetUtcNow());
        Assert.True(await filter.IsAllowedAsync(delegated));

        var transport = ContextWithAuthority(
            PrincipalKind.DashboardTransport,
            Guid.Empty,
            Guid.Empty);
        ((ClaimsIdentity)transport.User.Identity!).AddClaim(
            new Claim(TuvimaClaimTypes.DashboardService, "true"));
        transport.RequestServices = provider;
        Assert.False(await filter.IsAllowedAsync(transport));

        var human = ContextWithAuthority(PrincipalKind.Human, _accountId, Profile.SeedProfileId);
        ((ClaimsIdentity)human.User.Identity!).AddClaim(
            new Claim(TuvimaClaimTypes.DashboardService, "true"));
        human.RequestServices = provider;
        Assert.True(await filter.IsAllowedAsync(human));
    }

    [Fact]
    public async Task DelegatedAdministratorApplication_StillRequiresLiveHumanBindingAndConsent()
    {
        var application = await CreateApplicationAsync(ApplicationType.UserClient, administrator: true);
        var accessor = new HttpContextAccessor { HttpContext = ContextWithScope("library.read") };
        var evaluator = CreateEvaluator(accessor).Evaluator;
        var authority = DelegatedAuthority(application, applicationEnabled: true);

        Assert.True((await evaluator.EvaluateAsync(
            authority,
            new AuthorizationRequirement(ApplicationPermission: ApplicationPermissionIds.LibraryRead),
            null)).IsAllowed);

        accessor.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([], "test")),
        };
        Assert.Equal(AuthorizationDenialReason.MissingPermission, (await evaluator.EvaluateAsync(
            authority,
            new AuthorizationRequirement(ApplicationPermission: ApplicationPermissionIds.LibraryRead),
            null)).DenialReason);

        var decisions = CreateDecisions();
        Assert.Equal(AuthorizationDenialReason.DisabledPrincipal, (await decisions.EvaluateFeatureAsync(
            DelegatedAuthority(application, applicationEnabled: false),
            AccountFeatureId.Read)).DenialReason);
    }

    // "Ask for my PIN on admin pages" asks for the profile's own PIN (one PIN per profile, owner decision 2026-10-21). The wrong-try
    // count and the pause belong to that PIN (covered with the real PIN service in OneProfilePinTests and VaultAdministrationServiceTests), so here a wrong PIN, a
    // paused PIN and a profile with no PIN each fail closed.
    [Fact]
    public async Task GrantPinUnlock_FailsClosed_ForAWrongPin_APausedPin_AndAProfileWithNoPin()
    {
        await new ProfileAccessRepository(_database).SetAdminProtectionAsync(new ProfileAdminProtection
        {
            ProfileId = Profile.SeedProfileId,
            IsEnabled = true,
            UnlockMode = AdminUnlockMode.FixedDuration.ToString(),
            UnlockMinutes = null,
            ProtectionVersion = 1,
            UpdatedAt = _clock.GetUtcNow(),
        });
        var authority = HumanAdministratorAuthority(Guid.NewGuid());

        var noPin = new GrantAdminUnlockService(new ProfileAccessRepository(_database), new StubProfilePins(), _clock);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => noPin.UnlockAsync(authority, "2468").AsTask());

        var withPin = new GrantAdminUnlockService(new ProfileAccessRepository(_database), new StubProfilePins().With(Profile.SeedProfileId, "2468"), _clock);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => withPin.UnlockAsync(authority, "0000").AsTask());

        var paused = new GrantAdminUnlockService(new ProfileAccessRepository(_database), new PausedProfilePins(), _clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => paused.UnlockAsync(authority, "2468").AsTask());
    }

    [Fact]
    public async Task GrantPinUnlock_IsNotAsked_WhenTheSwitchIsOff_SoAdminFunctionsWorkWithoutAPin()
    {
        var service = new GrantAdminUnlockService(new ProfileAccessRepository(_database), new StubProfilePins(), _clock);
        var authority = HumanAdministratorAuthority(Guid.NewGuid());

        var state = await service.GetStateAsync(authority);

        Assert.False(state.ProtectionEnabled);
        Assert.True(state.IsUnlocked);
    }

    private sealed class PausedProfilePins : MediaEngine.Identity.Contracts.IProfilePinVerifier
    {
        public Task<bool> HasProfilePinAsync(Guid profileId, CancellationToken ct = default) => Task.FromResult(true);

        public Task VerifyProfilePinAsync(Guid profileId, string? pin, bool countsTowardLockout, CancellationToken ct = default) =>
            throw new MediaEngine.Identity.ProfilePinLockedException();
    }

    [Theory]
    [InlineData(AdminUnlockMode.FixedDuration, 30)]
    [InlineData(AdminUnlockMode.LockOnLeave, 5)]
    public async Task GrantPinUnlock_UsesBoundedExpiry(AdminUnlockMode mode, int expectedMinutes)
    {
        var protection = new ProfileAdminProtection
        {
            ProfileId = Profile.SeedProfileId,
            IsEnabled = true,
            UnlockMode = mode.ToString(),
            UnlockMinutes = null,
            ProtectionVersion = 1,
            UpdatedAt = _clock.GetUtcNow(),
        };
        await new ProfileAccessRepository(_database).SetAdminProtectionAsync(protection);
        var sessionId = Guid.NewGuid();
        await new IdentityRepository(_database).InsertSessionAsync(new AuthSession
        {
            Id = sessionId,
            AccountId = _accountId,
            ActiveProfileId = Profile.SeedProfileId,
            TokenHash = $"hash-{sessionId:N}",
            DeviceId = "test-device",
            DeviceName = "Test device",
            Client = "Test",
            AuthenticationMethod = "Password",
            SecurityStamp = "stamp",
            CreatedAt = _clock.GetUtcNow(),
            LastSeenAt = _clock.GetUtcNow(),
            ExpiresAt = _clock.GetUtcNow().AddHours(1),
        });
        var service = new GrantAdminUnlockService(new ProfileAccessRepository(_database), new StubProfilePins().With(Profile.SeedProfileId, "2468"), _clock);
        var authority = HumanAdministratorAuthority(sessionId);

        var unlocked = await service.UnlockAsync(authority, "2468");
        Assert.True(unlocked.IsUnlocked);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(expectedMinutes), unlocked.ExpiresAt);

        _clock.Advance(TimeSpan.FromMinutes(expectedMinutes + 1));
        Assert.False((await service.GetStateAsync(authority)).IsUnlocked);
    }

    [Fact]
    public async Task DashboardProjection_DeniesDisabledBindingAndSeparatesEligibilityFromUnlock()
    {
        var protection = new ProfileAdminProtection
        {
            ProfileId = Profile.SeedProfileId,
            IsEnabled = true,
            UnlockMode = AdminUnlockMode.FixedDuration.ToString(),
            UnlockMinutes = 30,
            ProtectionVersion = 1,
            UpdatedAt = _clock.GetUtcNow(),
        };
        await new ProfileAccessRepository(_database).SetAdminProtectionAsync(protection);
        var projector = new DashboardAuthorityProjector(
            _accounts,
            new ProfileRepository(_database),
            new ProfileAccessRepository(_database),
            new GrantAdminUnlockService(new ProfileAccessRepository(_database), new StubProfilePins(), _clock));

        var projected = await projector.ProjectAsync(_accountId, Profile.SeedProfileId, Guid.NewGuid());
        Assert.True(projected.EffectiveAdministrator);
        Assert.False(projected.AdministratorSurfaceUnlocked);
        Assert.Contains("read", projected.NavigationCapabilities);
        Assert.Contains("settings.administration", projected.NavigationCapabilities);
        Assert.Contains("administrator.unlock", projected.ActionCapabilities);
        Assert.DoesNotContain("access.manage", projected.ActionCapabilities);

        var disabledId = Guid.NewGuid();
        var disabled = Account(disabledId, administrator: false);
        disabled.IsEnabled = false;
        await CreateAccountWithOwnProfileAsync(disabled, new HashSet<AccountFeatureId>());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            projector.ProjectAsync(disabledId, Profile.SeedProfileId, Guid.NewGuid()));
    }

    private (AuthorizationEvaluator Evaluator, HttpContextAccessor Accessor) CreateEvaluator(
        HttpContextAccessor? accessor = null)
    {
        accessor ??= new HttpContextAccessor();
        var decisions = CreateDecisions();
        return (new AuthorizationEvaluator(
            decisions,
            _applications,
            new PermissionRegistry(),
            new AuthorizationAuditWriter(_accounts),
            accessor), accessor);
    }

    private AccountAccessDecisionService CreateDecisions() =>
        new(new ProfileAccessRepository(_database), new GrantAdminUnlockService(
            new ProfileAccessRepository(_database), new StubProfilePins(), _clock));

    private async Task<MediaEngine.Domain.Entities.Application> CreateApplicationAsync(
        ApplicationType type,
        bool administrator)
    {
        var application = new MediaEngine.Domain.Entities.Application
        {
            Id = Guid.NewGuid(),
            Name = "Test Application",
            ApplicationType = type,
            IsEnabled = true,
            IsAdministrator = administrator,
            AuthorizationVersion = 1,
            CreatedAt = _clock.GetUtcNow(),
            UpdatedAt = _clock.GetUtcNow(),
        };
        await _applications.InsertApplicationAsync(
            application,
            new HashSet<ApplicationPermissionId>());
        return application;
    }

    private RequestAuthority ServiceAuthority(MediaEngine.Domain.Entities.Application application) =>
        new(
            PrincipalKind.ServiceApplication,
            true,
            ApplicationId: application.Id,
            ApplicationEnabled: true,
            ApplicationAuthorizationVersion: application.AuthorizationVersion,
            ApplicationIsAdministrator: application.IsAdministrator);

    private RequestAuthority DelegatedAuthority(
        MediaEngine.Domain.Entities.Application application,
        bool applicationEnabled) =>
        new(
            PrincipalKind.DelegatedUserClient,
            true,
            _accountId,
            Profile.SeedProfileId,
            application.Id,
            AccountEnabled: true,
            ActiveProfileInUserHousehold: true,
            ApplicationEnabled: applicationEnabled,
            ActiveProfileIsServerAdministrator: true,
            ApplicationIsAdministrator: application.IsAdministrator);

    private RequestAuthority HumanAdministratorAuthority(Guid sessionId) =>
        new(
            PrincipalKind.Human,
            true,
            _accountId,
            Profile.SeedProfileId,
            SessionId: sessionId,
            AccountEnabled: true,
            ActiveProfileInUserHousehold: true,
            ActiveProfileIsServerAdministrator: true);

    private static DefaultHttpContext ContextWithScope(string scope) =>
        new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(TuvimaClaimTypes.Scope, scope)], "test")),
        };

    private static DefaultHttpContext ContextWithAuthority(
        PrincipalKind kind,
        Guid accountId,
        Guid profileId,
        Guid? applicationId = null)
    {
        var claims = new List<Claim>
        {
            new(TuvimaClaimTypes.PrincipalKind, kind.ToString()),
            new(TuvimaClaimTypes.AccountId, accountId.ToString("D")),
            new(TuvimaClaimTypes.ActiveProfileId, profileId.ToString("D")),
        };
        if (applicationId is { } id)
        {
            claims.Add(new Claim(TuvimaClaimTypes.ApplicationId, id.ToString("D")));
        }

        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
        };
    }

    private Account Account(Guid id, bool administrator)
    {
        if (administrator)
        {
            _administrators.Add(id);
        }

        return new()
        {
            Id = id,
            Email = $"{id:N}@example.com",
            NormalizedEmail = $"{id:N}@EXAMPLE.COM",
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = _clock.GetUtcNow(),
            UpdatedAt = _clock.GetUtcNow(),
        };
    }

    /// <summary>Creates the account with a new person of its own to sign in as (a profile has at most one user); returns that profile's id.</summary>
    private async Task<Guid> CreateAccountWithOwnProfileAsync(Account account, IReadOnlySet<AccountFeatureId> features)
    {
        var profile = new Profile { Id = Guid.NewGuid(), DisplayName = "Person", Role = ProfileRole.StandardUser, CreatedAt = _clock.GetUtcNow() };
        var grant = Grant(account.Id, administrator: _administrators.Contains(account.Id)) with { ProfileId = profile.Id };
        await _accounts.CreateAccountAsync(account, grant, features, new HashSet<Guid>(), newProfile: profile);
        return profile.Id;
    }

    private static TestGrant Grant(Guid accountId, bool administrator) =>
        new(accountId, Profile.SeedProfileId, IsDefault: true, AdminEnabled: administrator);

    /// <summary>The users the test made as server administrators (the role itself is written on their profile).</summary>
    private readonly HashSet<Guid> _administrators = [];

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_databasePath);
        try { File.Delete(_databasePath); } catch { }
    }

    private sealed class FixedAuthorityResolver(RequestAuthority authority) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(authority);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
