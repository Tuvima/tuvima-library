using System.Security.Claims;
using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// "Sign out everywhere else" is for every signed-in person, a member included, and only ever for their own account:
/// the self-service sign-in routes read the account and session from the caller's own sign-in, never from the request.
/// </summary>
public sealed class OwnSessionSignOutEndpointTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-own-sessions-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly IdentityRepository _identities;
    private readonly FirstPartyIdentityService _identity;
    private readonly ClockStub _clock = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));

    public OwnSessionSignOutEndpointTests()
    {
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        _identities = new IdentityRepository(_database);
        _identity = new FirstPartyIdentityService(
            _identities, _accounts, new ProfileRepository(_database),
            new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(),
            _clock, new FixedPolicy());
    }

    [Fact]
    public async Task Member_SignOutEverywhereElse_EndsOnlyTheirOtherSessions_AndKeepsTheCurrentOne()
    {
        var admin = await _identity.BootstrapAdministratorAsync("owner@example.com", Password, "Owner", "admin-device", "Browser", "Dashboard");
        var member = await InsertMemberAsync("member@example.com");
        var current = await InsertSessionAsync(member.Id, admin.ActiveProfile.Id, "phone-1");
        var lostPhone = await InsertSessionAsync(member.Id, admin.ActiveProfile.Id, "lost-phone");
        var tablet = await InsertSessionAsync(member.Id, admin.ActiveProfile.Id, "tablet");
        await using var app = BuildApplication();

        var result = await SendAsync(app, "DELETE", "/auth/sessions/others", HumanUser(member.Id, current.Id));

        Assert.Equal(StatusCodes.Status200OK, result.Status);
        Assert.Equal(2, JsonDocument.Parse(result.Text).RootElement.EnumerateObject().Single().Value.GetInt32());
        Assert.True(await IsActiveAsync(member.Id, current.Id));
        Assert.False(await IsActiveAsync(member.Id, lostPhone.Id));
        Assert.False(await IsActiveAsync(member.Id, tablet.Id));
        // Nobody else's sessions are touched.
        Assert.True(await IsActiveAsync(admin.Account.Id, admin.Session.Id));
    }

    [Fact]
    public async Task Member_ListsOnlyTheirOwnSessions()
    {
        var admin = await _identity.BootstrapAdministratorAsync("owner@example.com", Password, "Owner", "admin-device", "Browser", "Dashboard");
        var member = await InsertMemberAsync("member@example.com");
        var current = await InsertSessionAsync(member.Id, admin.ActiveProfile.Id, "phone-1");
        var other = await InsertSessionAsync(member.Id, admin.ActiveProfile.Id, "tablet");
        await using var app = BuildApplication();

        var result = await SendAsync(app, "GET", "/auth/sessions", HumanUser(member.Id, current.Id));

        Assert.Equal(StatusCodes.Status200OK, result.Status);
        var ids = JsonDocument.Parse(result.Text).RootElement.EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Equal(new HashSet<Guid> { current.Id, other.Id }, ids);
        Assert.DoesNotContain(admin.Session.Id, ids);
    }

    [Fact]
    public async Task Member_CannotEndAnotherPersonsSession()
    {
        var admin = await _identity.BootstrapAdministratorAsync("owner@example.com", Password, "Owner", "admin-device", "Browser", "Dashboard");
        var member = await InsertMemberAsync("member@example.com");
        var current = await InsertSessionAsync(member.Id, admin.ActiveProfile.Id, "phone-1");
        await using var app = BuildApplication();

        var result = await SendAsync(app, "DELETE", "/auth/sessions/{sessionId:guid}",
            HumanUser(member.Id, current.Id), ("sessionId", admin.Session.Id.ToString("D")));

        Assert.Equal(StatusCodes.Status403Forbidden, result.Status);
        Assert.True(await IsActiveAsync(admin.Account.Id, admin.Session.Id));
        Assert.True(await IsActiveAsync(member.Id, current.Id));
    }

    [Fact]
    public async Task Member_CanEndOneOfTheirOwnOtherSessions_AndTheCurrentSessionSurvives()
    {
        var admin = await _identity.BootstrapAdministratorAsync("owner@example.com", Password, "Owner", "admin-device", "Browser", "Dashboard");
        var member = await InsertMemberAsync("member@example.com");
        var current = await InsertSessionAsync(member.Id, admin.ActiveProfile.Id, "phone-1");
        var other = await InsertSessionAsync(member.Id, admin.ActiveProfile.Id, "tablet");
        await using var app = BuildApplication();

        var result = await SendAsync(app, "DELETE", "/auth/sessions/{sessionId:guid}",
            HumanUser(member.Id, current.Id), ("sessionId", other.Id.ToString("D")));

        Assert.Equal(StatusCodes.Status204NoContent, result.Status);
        Assert.False(await IsActiveAsync(member.Id, other.Id));
        Assert.True(await IsActiveAsync(member.Id, current.Id));
    }

    [Fact]
    public async Task Administrator_SignOutEverywhereElse_StillEndsOnlyTheirOwnOtherSessions()
    {
        var admin = await _identity.BootstrapAdministratorAsync("owner@example.com", Password, "Owner", "admin-device", "Browser", "Dashboard");
        var member = await InsertMemberAsync("member@example.com");
        var memberSession = await InsertSessionAsync(member.Id, admin.ActiveProfile.Id, "phone-1");
        var adminOther = await InsertSessionAsync(admin.Account.Id, admin.ActiveProfile.Id, "admin-tablet");
        await using var app = BuildApplication();

        var result = await SendAsync(app, "DELETE", "/auth/sessions/others", HumanUser(admin.Account.Id, admin.Session.Id));

        Assert.Equal(StatusCodes.Status200OK, result.Status);
        Assert.Equal(1, JsonDocument.Parse(result.Text).RootElement.EnumerateObject().Single().Value.GetInt32());
        Assert.True(await IsActiveAsync(admin.Account.Id, admin.Session.Id));
        Assert.False(await IsActiveAsync(admin.Account.Id, adminOther.Id));
        Assert.True(await IsActiveAsync(member.Id, memberSession.Id));
    }

    private async Task<bool> IsActiveAsync(Guid accountId, Guid sessionId) =>
        (await _identities.GetSessionsAsync(accountId)).Single(session => session.Id == sessionId).IsActive(_clock.GetUtcNow());

    private async Task<Account> InsertMemberAsync(string email)
    {
        var member = new Account
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            IsEnabled = true,
            IsAdministrator = false,
            CreatedAt = _clock.GetUtcNow(),
            UpdatedAt = _clock.GetUtcNow(),
        };
        await _accounts.InsertAsync(member);
        return member;
    }

    private async Task<AuthSession> InsertSessionAsync(Guid accountId, Guid profileId, string deviceId)
    {
        var now = _clock.GetUtcNow();
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ActiveProfileId = profileId,
            TokenHash = Guid.NewGuid().ToString("N"),
            DeviceId = deviceId,
            DeviceName = deviceId,
            Client = "Dashboard",
            AuthenticationMethod = "Password",
            IssuedIngress = ClientIngress.HomeNetwork,
            SecurityStamp = "test",
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now.AddDays(1),
            AuthenticatedAt = now,
        };
        await _identities.InsertSessionAsync(session);
        return session;
    }

    private static ClaimsPrincipal HumanUser(Guid accountId, Guid sessionId) => new(new ClaimsIdentity(
    [
        new Claim(TuvimaClaimTypes.AccountId, accountId.ToString("D")),
        new Claim(TuvimaClaimTypes.SessionId, sessionId.ToString("D")),
    ], "test"));

    private WebApplication BuildApplication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(_ => { });
        // Results.Forbid() asks the authentication service to answer; the stub answers 403 like the real handler would.
        builder.Services.AddSingleton<IAuthenticationService, ForbidOnlyAuthenticationService>();
        builder.Services.AddSingleton<IFirstPartyIdentityService>(_identity);
        builder.Services.AddScoped<RecentSignInGuard>();
        // The other sign-in routes are mapped too; their services only need to be known so they bind as services.
        builder.Services.AddSingleton<DashboardAuthorityProjector>(_ => null!);
        builder.Services.AddSingleton<IConfigurationLoader>(_ => null!);
        builder.Services.AddSingleton<AuthenticationProviderConfigurationService>(_ => null!);
        builder.Services.AddSingleton<IRequestAuthorityResolver>(_ => null!);
        builder.Services.AddSingleton<ExternalIdentityTransactionService>(_ => null!);
        builder.Services.AddSingleton<IAccountExternalLoginService>(_ => null!);
        builder.Services.AddSingleton<ISelfServiceAuthorizationService>(_ => null!);
        builder.Services.AddSingleton<IAccountRepository>(_ => null!);
        builder.Services.AddSingleton<IProfileAccessRepository>(_ => null!);
        builder.Services.AddSingleton<IPasskeyHandler<Account>>(_ => null!);
        builder.Services.AddSingleton<UserManager<Account>>(_ => null!);
        builder.Services.AddSingleton<IAccountSignInMethodRepository>(_ => null!);
        builder.Services.AddSingleton<AuthenticationPolicyMutationGate>(_ => null!);
        builder.Services.AddSingleton<IIdentityRepository>(_ => null!);
        builder.Services.AddSingleton<IntercomTokenService>(_ => null!);
        builder.Services.AddSingleton<TimeProvider>(_clock);
        var app = builder.Build();
        app.MapAuthenticationEndpoints();
        return app;
    }

    private static async Task<(int Status, string Text)> SendAsync(
        WebApplication app, string method, string pattern, ClaimsPrincipal user, params (string Name, string Value)[] routeValues)
    {
        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>(), candidate =>
                candidate.RoutePattern.RawText == pattern &&
                candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) == true);
        var responseBody = new MemoryStream();
        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = user,
            Request = { Method = method, Path = pattern },
            Response = { Body = responseBody },
        };
        foreach (var (name, value) in routeValues)
        {
            context.Request.RouteValues[name] = value;
        }

        await endpoint.RequestDelegate!(context);
        responseBody.Position = 0;
        var text = await new StreamReader(responseBody).ReadToEndAsync();
        return (context.Response.StatusCode, text);
    }

    public void Dispose()
    {
        _database.Dispose();
        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
    }

    private sealed class ForbidOnlyAuthenticationService : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;
    }

    private sealed class ClockStub(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedPolicy : IAuthenticationPolicyProvider
    {
        public AuthSettings GetCurrent() => new();
    }
}
