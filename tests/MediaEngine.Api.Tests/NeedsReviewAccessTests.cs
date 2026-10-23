using System.Security.Claims;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Api.Services.Review;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Needs Review is for server administrators only (registry-retirement D12), plus approved apps that a server
/// administrator granted review permission (D13). Every check uses the authorization metadata of the real mapped
/// review routes, so loosening a route (for example allowing household administrators) fails here.
/// </summary>
public sealed class NeedsReviewAccessTests : IDisposable
{
    private static readonly string[] ReadRoutes = ["GetPendingReviews", "GetReviewCount", "GetReviewItem"];
    private static readonly string[] ResolveRoutes = ["ResolveReviewItem", "DismissReviewItem", "MoveReviewItemToTv", "SkipUniverseMatch"];

    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly ApplicationRepository _applications;
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));

    public NeedsReviewAccessTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_needs_review_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        _applications = new ApplicationRepository(_database);
    }

    [Fact]
    public async Task EveryReviewRoute_DeclaresTheReviewPermissionAndDoesNotInviteHouseholdAdministrators()
    {
        var routes = await MapReviewRoutesAsync();

        Assert.Equal(
            ReadRoutes.Concat(ResolveRoutes).Order(StringComparer.Ordinal),
            routes.Keys.Order(StringComparer.Ordinal));
        foreach (var name in ReadRoutes)
        {
            Assert.Equal(ApplicationPermissionIds.ReviewRead, routes[name].Permission);
        }

        foreach (var name in ResolveRoutes)
        {
            Assert.Equal(ApplicationPermissionIds.ReviewResolve, routes[name].Permission);
        }

        foreach (var (name, requirement) in routes)
        {
            Assert.False(requirement.AllowHouseholdAdministrator, $"{name} must not allow household administrators");
        }
    }

    [Fact]
    public async Task EveryReviewRoute_AllowsAServerAdministrator()
    {
        var routes = await MapReviewRoutesAsync();
        var serverAdmin = await CreateAccountAsync(administrator: true);

        await AssertAllRoutesAsync(routes, ContextFor(serverAdmin), allowed: true, "server administrator");
    }

    [Fact]
    public async Task EveryReviewRoute_RefusesAHouseholdAdministratorWhoIsNotAServerAdministrator()
    {
        var routes = await MapReviewRoutesAsync();
        var householdAdmin = await CreateAccountAsync(administrator: false);
        await _accounts.SetHouseholdAdminAsync(householdAdmin, true, _clock.GetUtcNow());

        // Prove the principal really is a household administrator, so the refusal below is not vacuous.
        var authority = await Resolver().ResolveAsync(ContextFor(householdAdmin));
        Assert.True(authority.IsEffectiveHouseholdAdministrator);
        Assert.False(authority.IsEffectiveAdministrator);

        await AssertAllRoutesAsync(routes, ContextFor(householdAdmin), allowed: false, "household administrator");
    }

    [Fact]
    public async Task EveryReviewRoute_RefusesAPlainSignedInPersonWithEveryFeatureGrant()
    {
        var routes = await MapReviewRoutesAsync();
        var member = await CreateAccountAsync(administrator: false);

        await AssertAllRoutesAsync(routes, ContextFor(member), allowed: false, "ordinary account");
    }

    [Fact]
    public async Task EveryReviewRoute_RefusesAKidsProfile_EvenWhenItsAccountIsAServerAdministratorWithAdminEnabled()
    {
        var routes = await MapReviewRoutesAsync();
        var serverAdmin = await CreateAccountAsync(administrator: true);
        var kidsProfile = await AddKidsProfileAsync(serverAdmin, adminEnabled: true);

        var authority = await Resolver().ResolveAsync(ContextFor(serverAdmin, kidsProfile));
        Assert.True(authority.ActiveProfileIsRestricted);
        Assert.False(authority.IsEffectiveAdministrator);

        await AssertAllRoutesAsync(routes, ContextFor(serverAdmin, kidsProfile), allowed: false, "Kids profile");
    }

    [Fact]
    public async Task EveryReviewRoute_RefusesAnAnonymousCaller()
    {
        var routes = await MapReviewRoutesAsync();

        await AssertAllRoutesAsync(routes, new DefaultHttpContext(), allowed: false, "anonymous caller");
    }

    [Fact]
    public async Task ReviewRoutes_AllowAnApprovedAppOnlyForThePermissionItWasGranted()
    {
        var routes = await MapReviewRoutesAsync();
        var readOnly = await CreateApplicationAsync(ApplicationType.ServerIntegration, ApplicationPermissionIds.ReviewRead);
        var resolveOnly = await CreateApplicationAsync(ApplicationType.ServerIntegration, ApplicationPermissionIds.ReviewResolve);
        var both = await CreateApplicationAsync(
            ApplicationType.ServerIntegration, ApplicationPermissionIds.ReviewRead, ApplicationPermissionIds.ReviewResolve);

        foreach (var (name, requirement) in routes)
        {
            var isRead = ReadRoutes.Contains(name);
            Assert.True(await AllowsAsync(requirement, AppContext(both)), $"{name} with read and resolve");
            Assert.Equal(isRead, await AllowsAsync(requirement, AppContext(readOnly)));
            Assert.Equal(!isRead, await AllowsAsync(requirement, AppContext(resolveOnly)));
        }
    }

    [Fact]
    public async Task ReviewRoutes_ForAnAppActingForAPerson_AllowOnlyAServerAdministrator()
    {
        // D15: an app signed in as a person needs that person to be a server administrator too.
        var routes = await MapReviewRoutesAsync();
        var app = await CreateApplicationAsync(
            ApplicationType.UserClient, ApplicationPermissionIds.ReviewRead, ApplicationPermissionIds.ReviewResolve);
        var serverAdmin = await CreateAccountAsync(administrator: true);
        var householdAdmin = await CreateAccountAsync(administrator: false);
        await _accounts.SetHouseholdAdminAsync(householdAdmin, true, _clock.GetUtcNow());
        var person = await CreateAccountAsync(administrator: false);
        var kidsProfile = await AddKidsProfileAsync(serverAdmin, adminEnabled: true);

        await AssertAllRoutesAsync(routes, DelegatedContext(app, serverAdmin), allowed: true, "app for a server administrator");
        await AssertAllRoutesAsync(routes, DelegatedContext(app, householdAdmin), allowed: false, "app for a household administrator");
        await AssertAllRoutesAsync(routes, DelegatedContext(app, person), allowed: false, "app for a signed-in person");
        await AssertAllRoutesAsync(routes, DelegatedContext(app, serverAdmin, kidsProfile), allowed: false, "app for a Kids profile");
    }

    [Fact]
    public async Task ReviewRoutes_RefuseAnAppThatWasNeverGrantedReviewPermission()
    {
        var routes = await MapReviewRoutesAsync();
        var unrelated = await CreateApplicationAsync(
            ApplicationType.ServerIntegration, ApplicationPermissionIds.ProvidersStatusRead);
        var none = await CreateApplicationAsync(ApplicationType.ServerIntegration);

        await AssertAllRoutesAsync(routes, AppContext(unrelated), allowed: false, "app with another permission");
        await AssertAllRoutesAsync(routes, AppContext(none), allowed: false, "app with no permissions");
    }

    [Fact]
    public async Task ReviewRoutes_RefuseAnApprovedAppOnceItIsDisabled()
    {
        var routes = await MapReviewRoutesAsync();
        var application = await CreateApplicationAsync(
            ApplicationType.ServerIntegration, ApplicationPermissionIds.ReviewRead, ApplicationPermissionIds.ReviewResolve);
        await AssertAllRoutesAsync(routes, AppContext(application), allowed: true, "enabled app");

        application.IsEnabled = false;
        await _applications.UpdateApplicationAsync(application);

        await AssertAllRoutesAsync(routes, AppContext(application), allowed: false, "disabled app");
    }

    [Fact]
    public async Task DashboardAuthority_GivesTheNeedsReviewEntryToAServerAdministrator()
    {
        var serverAdmin = await CreateAccountAsync(administrator: true);

        var projected = await Projector().ProjectAsync(serverAdmin, Profile.SeedProfileId, Guid.NewGuid());

        Assert.True(projected.EffectiveAdministrator);
        Assert.Contains("settings.administration", projected.NavigationCapabilities);
    }

    [Fact]
    public async Task DashboardAuthority_GivesNothingForNeedsReviewToAHouseholdAdministrator()
    {
        var householdAdmin = await CreateAccountAsync(administrator: false);
        await _accounts.SetHouseholdAdminAsync(householdAdmin, true, _clock.GetUtcNow());

        var projected = await Projector().ProjectAsync(householdAdmin, Profile.SeedProfileId, Guid.NewGuid());

        Assert.False(projected.EffectiveAdministrator);
        Assert.Contains("household.manage", projected.ActionCapabilities);
        Assert.DoesNotContain("settings.administration", projected.NavigationCapabilities);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DashboardAuthority_GivesNothingForNeedsReviewToAKidsProfile_WhateverItsAccountIs(bool accountIsServerAdministrator)
    {
        var account = await CreateAccountAsync(administrator: accountIsServerAdministrator);
        var kidsProfile = await AddKidsProfileAsync(account, adminEnabled: accountIsServerAdministrator);

        var projected = await Projector().ProjectAsync(account, kidsProfile, Guid.NewGuid());

        Assert.False(projected.EffectiveAdministrator);
        Assert.DoesNotContain("settings.administration", projected.NavigationCapabilities);
    }

    private async Task AssertAllRoutesAsync(
        IReadOnlyDictionary<string, AdministratorOrApplicationRequirement> routes,
        HttpContext http,
        bool allowed,
        string who)
    {
        foreach (var (name, requirement) in routes)
        {
            Assert.True(allowed == await AllowsAsync(requirement, http), $"{name} as {who}: expected allowed={allowed}");
        }
    }

    private async Task<bool> AllowsAsync(AdministratorOrApplicationRequirement requirement, HttpContext http)
    {
        var decisions = new AccountAccessDecisionService(_accounts, new GrantAdminUnlockService(
            _accounts, new StubProfilePins(), _clock), new LibraryVisibilityRule(_accounts, new VaultLibraryRepository(_database)));
        var evaluator = new AuthorizationEvaluator(
            decisions, _applications, new PermissionRegistry(), new AuthorizationAuditWriter(_accounts),
            new HttpContextAccessor { HttpContext = http });
        var handler = new AdministratorOrApplicationHandler(Resolver(), decisions, evaluator);
        var context = new AuthorizationHandlerContext([requirement], http.User, http);
        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private DashboardAuthorityProjector Projector() => new(
        _accounts,
        new ProfileRepository(_database),
        new GrantAdminUnlockService(_accounts, new StubProfilePins(), _clock));

    private async Task<Guid> AddKidsProfileAsync(Guid accountId, bool adminEnabled)
    {
        var kidsProfile = Guid.NewGuid();
        await ProfileTestData.InsertAsync(_database, new Profile
        {
            Id = kidsProfile,
            DisplayName = "Kids",
            AvatarColor = "#123456",
            Role = ProfileRole.RestrictedProfile,
            CreatedAt = _clock.GetUtcNow(),
        }, (await _accounts.GetByIdAsync(accountId))!.HouseholdId);
        await _accounts.UpsertGrantAsync(new AccountProfileGrant
        {
            AccountId = accountId,
            ProfileId = kidsProfile,
            IsEnabled = true,
            AdminEnabled = adminEnabled,
            AuthorizationVersion = 1,
            GrantedAt = _clock.GetUtcNow(),
        });
        return kidsProfile;
    }

    private RequestAuthorityResolver Resolver() =>
        new(_accounts, _applications, new ProfileRepository(_database));

    /// <summary>Maps the real review routes on a bare application and reads each route's own requirement.</summary>
    private static async Task<IReadOnlyDictionary<string, AdministratorOrApplicationRequirement>> MapReviewRoutesAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Services.AddAuthorization();
        // Route mapping only infers each handler's parameter sources; no handler ever runs, so none of these resolve.
        foreach (var service in new[]
        {
            typeof(IReviewQueueReadService), typeof(IReviewQueueRepository), typeof(IMetadataClaimRepository),
            typeof(IHydrationPipelineService), typeof(ICanonicalValueRepository), typeof(IEventPublisher),
            typeof(ISystemActivityRepository), typeof(IReviewMoveToTvService), typeof(ICollectionRepository), typeof(IVaultLibraryRepository),
        })
        {
            builder.Services.AddSingleton(service, _ => throw new InvalidOperationException(
                $"Review route authorization checks must not resolve {service.Name}."));
        }

        await using var app = builder.Build();
        app.MapReviewEndpoints();

        var routes = new Dictionary<string, AdministratorOrApplicationRequirement>(StringComparer.Ordinal);
        foreach (var endpoint in ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>())
        {
            var name = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName
                ?? throw new InvalidOperationException($"Review route {endpoint.RoutePattern.RawText} has no name.");
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
            var policy = Assert.Single(endpoint.Metadata.OfType<AuthorizationPolicy>());
            routes.Add(name, Assert.Single(policy.Requirements.OfType<AdministratorOrApplicationRequirement>()));
            Assert.Contains(policy.Requirements, requirement => requirement is DenyAnonymousAuthorizationRequirement);
        }

        return routes;
    }

    private async Task<Guid> CreateAccountAsync(bool administrator)
    {
        var id = Guid.NewGuid();
        await _accounts.CreateAccountAsync(
            new Account
            {
                Id = id,
                Email = $"{id:N}@example.com",
                NormalizedEmail = $"{id:N}@EXAMPLE.COM",
                IsEnabled = true,
                IsAdministrator = administrator,
                AuthorizationVersion = 1,
                CreatedAt = _clock.GetUtcNow(),
                UpdatedAt = _clock.GetUtcNow(),
            },
            new AccountProfileGrant
            {
                AccountId = id,
                ProfileId = Profile.SeedProfileId,
                IsDefault = true,
                IsEnabled = true,
                AdminEnabled = administrator,
                AuthorizationVersion = 1,
                GrantedAt = _clock.GetUtcNow(),
            },
            AccountFeatureId.All.ToHashSet(),
            new HashSet<Guid>());
        return id;
    }

    private async Task<MediaEngine.Domain.Entities.Application> CreateApplicationAsync(
        ApplicationType type,
        params ApplicationPermissionId[] permissions)
    {
        var application = new MediaEngine.Domain.Entities.Application
        {
            Id = Guid.NewGuid(),
            Name = "Review test app",
            ApplicationType = type,
            IsEnabled = true,
            IsAdministrator = false,
            AuthorizationVersion = 1,
            CreatedAt = _clock.GetUtcNow(),
            UpdatedAt = _clock.GetUtcNow(),
        };
        await _applications.InsertApplicationAsync(application, permissions.ToHashSet());
        return application;
    }

    private static DefaultHttpContext ContextFor(Guid accountId, Guid? profileId = null) =>
        ContextWithClaims(PrincipalKind.Human, accountId, profileId ?? Profile.SeedProfileId, null);

    private static DefaultHttpContext AppContext(MediaEngine.Domain.Entities.Application application) =>
        ContextWithClaims(PrincipalKind.ServiceApplication, Guid.Empty, Guid.Empty, application.Id);

    private static DefaultHttpContext DelegatedContext(
        MediaEngine.Domain.Entities.Application application, Guid accountId, Guid? profileId = null)
    {
        var http = ContextWithClaims(
            PrincipalKind.DelegatedUserClient, accountId, profileId ?? Profile.SeedProfileId, application.Id);
        // The person consented to both review scopes, so only the person's own standing decides.
        ((ClaimsIdentity)http.User.Identity!).AddClaims(
        [
            new Claim(TuvimaClaimTypes.Scope, ApplicationPermissionIds.ReviewRead.Value),
            new Claim(TuvimaClaimTypes.Scope, ApplicationPermissionIds.ReviewResolve.Value),
        ]);
        return http;
    }

    private static DefaultHttpContext ContextWithClaims(
        PrincipalKind kind,
        Guid accountId,
        Guid profileId,
        Guid? applicationId)
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

        return new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
    }

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
            // Temp file still held by a pooled connection; the OS temp cleaner removes it.
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
