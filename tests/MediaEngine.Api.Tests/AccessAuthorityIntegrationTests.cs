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
        await _accounts.CreateAccountAsync(Account(ordinaryId, administrator: false),
            Grant(ordinaryId, administrator: false), AccountFeatureId.All.ToHashSet(), new HashSet<Guid>());
        var application = await CreateApplicationAsync(ApplicationType.ServerIntegration, administrator: false);
        await _applications.ReplacePermissionsAsync(application.Id, new HashSet<ApplicationPermissionId> { read }, _clock.GetUtcNow());
        var handler = new AdministratorOrApplicationHandler(new RequestAuthorityResolver(_accounts, _applications, new ProfileRepository(_database)), CreateDecisions(), CreateEvaluator().Evaluator);
        var service = ContextWithAuthority(PrincipalKind.ServiceApplication, Guid.Empty, Guid.Empty, application.Id);
        async Task<bool> Allows(HttpContext http, ApplicationPermissionId permission)
        {
            var context = new AuthorizationHandlerContext([new AdministratorOrApplicationRequirement(permission)], http.User, http);
            await handler.HandleAsync(context);
            return context.HasSucceeded;
        }
        Assert.False(await Allows(ContextWithAuthority(PrincipalKind.Human, ordinaryId, Profile.SeedProfileId), read));
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
        await _accounts.CreateAccountAsync(Account(ordinaryId, administrator: false),
            Grant(ordinaryId, administrator: false), AccountFeatureId.All.ToHashSet(), new HashSet<Guid>());
        var application = await CreateApplicationAsync(ApplicationType.ServerIntegration, administrator: false);
        await _applications.ReplacePermissionsAsync(application.Id, new HashSet<ApplicationPermissionId> { ApplicationPermissionIds.MetadataRead }, _clock.GetUtcNow());
        var resolver = new RequestAuthorityResolver(_accounts, _applications, new ProfileRepository(_database));
        var handler = new AdministratorOrApplicationHandler(resolver, CreateDecisions(), CreateEvaluator().Evaluator);
        var ordinary = ContextWithAuthority(PrincipalKind.Human, ordinaryId, Profile.SeedProfileId);
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
            new RequestAuthorityResolver(_accounts, _applications, new ProfileRepository(_database)));
        services.AddSingleton<ISelfServiceAuthorizationService>(CreateDecisions());
        services.AddSingleton<IAccountRepository>(_accounts);
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
    public async Task ProfileIdentityRead_AllowsInactiveGrantedProfileButDeniesDisabledOrMissingGrant()
    {
        var profileId = Guid.NewGuid();
        await ProfileTestData.InsertAsync(_database, new Profile
        {
            Id = profileId,
            DisplayName = "Second",
            AvatarColor = "#123456",
            Role = ProfileRole.RestrictedProfile,
            CreatedAt = _clock.GetUtcNow(),
        });
        await _accounts.UpsertGrantAsync(new AccountProfileGrant
        {
            AccountId = _accountId,
            ProfileId = profileId,
            IsEnabled = true,
            AuthorizationVersion = 1,
            GrantedAt = _clock.GetUtcNow(),
        });
        var services = new ServiceCollection();
        services.AddSingleton<IRequestAuthorityResolver>(
            new RequestAuthorityResolver(_accounts, _applications, new ProfileRepository(_database)));
        services.AddSingleton<IAccountRepository>(_accounts);
        using var provider = services.BuildServiceProvider();
        var context = ContextWithAuthority(PrincipalKind.Human, _accountId, Profile.SeedProfileId);
        context.RequestServices = provider;

        Assert.True(await ProfileEndpoints.IsGrantedProfileIdentityAuthorizedAsync(context, profileId));
        Assert.False(await ProfileEndpoints.IsGrantedProfileIdentityAuthorizedAsync(context, Guid.NewGuid()));

        var grant = (await _accounts.GetGrantAsync(_accountId, profileId))!;
        grant.IsEnabled = false;
        await _accounts.UpsertGrantAsync(grant);
        Assert.False(await ProfileEndpoints.IsGrantedProfileIdentityAuthorizedAsync(context, profileId));
    }

    [Fact]
    public async Task HumanSelfServicePolicy_DeniesDelegatedClientWithoutADeclaredServicePermission()
    {
        var application = await CreateApplicationAsync(ApplicationType.UserClient, administrator: false);
        var resolver = new RequestAuthorityResolver(_accounts, _applications, new ProfileRepository(_database));
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
            new RequestAuthorityResolver(_accounts, _applications, new ProfileRepository(_database)));
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

    [Fact]
    public async Task GrantPinFailures_AreAtomicAndLockAfterFiveAttempts()
    {
        var protection = new GrantAdminProtection
        {
            AccountId = _accountId,
            ProfileId = Profile.SeedProfileId,
            IsEnabled = true,
            UnlockMode = AdminUnlockMode.FixedDuration.ToString(),
            UnlockMinutes = null,
            ProtectionVersion = 1,
            UpdatedAt = _clock.GetUtcNow(),
        };
        var hasher = new PasswordHasher<GrantAdminProtection>();
        protection.PinHash = hasher.HashPassword(protection, "2468");
        protection.HashScheme = "aspnet-passwordhasher-v3";
        await _accounts.SetAdminProtectionAsync(protection);

        var service = new GrantAdminUnlockService(_accounts, hasher, _clock);
        var authority = HumanAdministratorAuthority(Guid.NewGuid());
        var attempts = Enumerable.Range(0, 5)
            .Select(_ => Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => service.UnlockAsync(authority, "0000").AsTask()));
        await Task.WhenAll(attempts);

        var stored = await _accounts.GetAdminProtectionAsync(_accountId, Profile.SeedProfileId);
        Assert.Equal(5, stored?.FailedAttemptCount);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(15), stored?.LockedUntil);
    }

    [Theory]
    [InlineData(AdminUnlockMode.FixedDuration, 30)]
    [InlineData(AdminUnlockMode.LockOnLeave, 5)]
    public async Task GrantPinUnlock_UsesBoundedExpiry(AdminUnlockMode mode, int expectedMinutes)
    {
        var protection = new GrantAdminProtection
        {
            AccountId = _accountId,
            ProfileId = Profile.SeedProfileId,
            IsEnabled = true,
            UnlockMode = mode.ToString(),
            UnlockMinutes = null,
            ProtectionVersion = 1,
            UpdatedAt = _clock.GetUtcNow(),
        };
        var hasher = new PasswordHasher<GrantAdminProtection>();
        protection.PinHash = hasher.HashPassword(protection, "2468");
        protection.HashScheme = "aspnet-passwordhasher-v3";
        await _accounts.SetAdminProtectionAsync(protection);
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
        var service = new GrantAdminUnlockService(_accounts, hasher, _clock);
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
        var protection = new GrantAdminProtection
        {
            AccountId = _accountId,
            ProfileId = Profile.SeedProfileId,
            IsEnabled = true,
            UnlockMode = AdminUnlockMode.FixedDuration.ToString(),
            UnlockMinutes = 30,
            PinHash = "unused",
            HashScheme = "test",
            ProtectionVersion = 1,
            UpdatedAt = _clock.GetUtcNow(),
        };
        await _accounts.SetAdminProtectionAsync(protection);
        var projector = new DashboardAuthorityProjector(
            _accounts,
            new ProfileRepository(_database),
            new GrantAdminUnlockService(_accounts, new PasswordHasher<GrantAdminProtection>(), _clock),
            _clock);

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
        await _accounts.CreateAccountAsync(disabled, Grant(disabledId, administrator: false),
            new HashSet<AccountFeatureId>(), new HashSet<Guid>());
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
        new(_accounts, new GrantAdminUnlockService(
            _accounts, new PasswordHasher<GrantAdminProtection>(), _clock));

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
            GrantEnabled: true,
            ApplicationEnabled: applicationEnabled,
            AccountIsAdministrator: true,
            GrantAdminEnabled: true,
            ApplicationIsAdministrator: application.IsAdministrator);

    private RequestAuthority HumanAdministratorAuthority(Guid sessionId) =>
        new(
            PrincipalKind.Human,
            true,
            _accountId,
            Profile.SeedProfileId,
            SessionId: sessionId,
            AccountEnabled: true,
            GrantEnabled: true,
            AccountIsAdministrator: true,
            GrantAdminEnabled: true);

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

    private Account Account(Guid id, bool administrator) =>
        new()
        {
            Id = id,
            Email = $"{id:N}@example.com",
            NormalizedEmail = $"{id:N}@EXAMPLE.COM",
            IsEnabled = true,
            IsAdministrator = administrator,
            AuthorizationVersion = 1,
            CreatedAt = _clock.GetUtcNow(),
            UpdatedAt = _clock.GetUtcNow(),
        };

    private AccountProfileGrant Grant(Guid accountId, bool administrator) =>
        new()
        {
            AccountId = accountId,
            ProfileId = Profile.SeedProfileId,
            IsDefault = true,
            IsEnabled = true,
            AdminEnabled = administrator,
            AuthorizationVersion = 1,
            GrantedAt = _clock.GetUtcNow(),
        };

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_databasePath); } catch { }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
