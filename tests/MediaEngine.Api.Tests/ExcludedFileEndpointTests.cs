using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>
/// GET /library/excluded-files and POST /library/excluded-files/{id}/add-back run through the real authorization pipeline (the real
/// Administrator policy, handler and decision service) over the real service, a temp data store and temp folders.
/// </summary>
public sealed class ExcludedFileEndpointTests : IAsyncLifetime
{
    private const string CallerHeader = "X-Test-Caller";
    private const string ListRoute = "/library/excluded-files";

    private readonly ExcludedFileTestWorld _world = new();
    private readonly Guid _lockedSession = Guid.NewGuid();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        // The same Administrator policy Program.cs registers: an authenticated, effective administrator with the surface PIN unlocked.
        builder.Services.AddAuthorization(options => options.AddPolicy(AuthPolicies.Administrator, policy =>
            policy.RequireAuthenticatedUser().AddRequirements(new EffectiveAdministratorRequirement(true))));
        builder.Services.AddSingleton<IAuthorizationHandler, EffectiveAdministratorHandler>();
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new CallerResolver(this));
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new AccountAccessDecisionService(
            new AccountRepository(_world.Database), new UnlockState(_lockedSession)));
        builder.Services.AddSingleton<IProfileRepository>(new ProfileRepository(_world.Database));
        builder.Services.AddSingleton(_world.Service);
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapExcludedFileEndpoints();
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.Single()) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _world.Dispose();
    }

    [Fact]
    public async Task AnAdministrator_GetsOnlyThatLibrarysFiles_WithTheWireNames_AndNoDiskPath()
    {
        var now = DateTimeOffset.UtcNow;
        _world.WriteFile(_world.Managed, "Dune/Dune.mkv");
        _world.WriteFile(_world.AsIs, "Alien.mkv");
        await _world.ExcludeAsync("movies", "Dune/Dune.mkv", now.AddHours(-1));
        await _world.ExcludeAsync("old-rips", "Alien.mkv", now);

        var response = await SendAsync("admin", HttpMethod.Get, ListRoute + "?library_id=movies");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var row = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal(
            ["id", "library_id", "file_name", "folder_label", "title", "excluded_at", "excluded_by", "reason"],
            row.EnumerateObject().Select(property => property.Name));
        Assert.Equal("Dune.mkv", row.GetProperty("file_name").GetString());
        Assert.Equal("Movies › Dune", row.GetProperty("folder_label").GetString());
        Assert.All(row.EnumerateObject().Where(property => property.Value.ValueKind == JsonValueKind.String),
            property => Assert.DoesNotContain(_world.Root, property.Value.GetString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AddBack_AsAnAdministrator_RemovesTheRow_AndQueuesTheScan()
    {
        var path = _world.WriteFile(_world.AsIs, "Alien.mkv");
        var row = await _world.ExcludeAsync("old-rips", "Alien.mkv", DateTimeOffset.UtcNow);

        var response = await SendAsync("admin", HttpMethod.Post, $"{ListRoute}/{row.Id:D}/add-back");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["queued", "message"], json.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.True(json.RootElement.GetProperty("queued").GetBoolean());
        Assert.Empty(await _world.Excluded.ListAllAsync());
        Assert.Equal(Path.GetFullPath(path), Assert.Single(_world.Intake.Requests).Path, ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public async Task AddBack_OfAnUnknownId_Is404_AndChangesNothing()
    {
        _world.WriteFile(_world.Managed, "Dune.mkv");
        await _world.ExcludeAsync("movies", "Dune.mkv", DateTimeOffset.UtcNow);

        var response = await SendAsync("admin", HttpMethod.Post, $"{ListRoute}/{Guid.NewGuid():D}/add-back");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(await _world.Excluded.ListAllAsync());
        Assert.Empty(_world.Intake.Requests);
    }

    [Theory]
    [InlineData("member")]
    [InlineData("lockedAdmin")]
    public async Task EveryoneWhoIsNotAnUnlockedAdministrator_GetsForbidden_ForTheListAndForAddBack(string caller)
    {
        _world.WriteFile(_world.Managed, "Dune.mkv");
        var row = await _world.ExcludeAsync("movies", "Dune.mkv", DateTimeOffset.UtcNow);

        var list = await SendAsync(caller, HttpMethod.Get, ListRoute);
        var addBack = await SendAsync(caller, HttpMethod.Post, $"{ListRoute}/{row.Id:D}/add-back");

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, addBack.StatusCode);
        Assert.Single(await _world.Excluded.ListAllAsync());
        Assert.Empty(_world.Intake.Requests);
    }

    [Fact]
    public async Task ARequestWithNoSignedInCaller_IsNotServed()
    {
        var response = await SendAsync(null, HttpMethod.Get, ListRoute);

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
    }

    [Fact]
    public void BothRoutes_AreAdministratorOnly_WithNoApplicationPermission_AndAreNamed()
    {
        var endpoints = ((IEndpointRouteBuilder)_app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();
        var list = endpoints.Single(candidate => candidate.RoutePattern.RawText!.TrimEnd('/') == ListRoute);
        var addBack = endpoints.Single(candidate => candidate.RoutePattern.RawText == ListRoute + "/{id:guid}/add-back");

        Assert.Equal("ListExcludedFiles", list.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);
        Assert.Equal("AddBackExcludedFile", addBack.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);
        Assert.Equal(["GET"], list.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
        Assert.Equal(["POST"], addBack.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
        foreach (var endpoint in new[] { list, addBack })
        {
            Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), data => data.Policy == AuthPolicies.Administrator);
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
            Assert.DoesNotContain(
                endpoint.Metadata.OfType<AuthorizationPolicy>().SelectMany(policy => policy.Requirements),
                item => item is AdministratorOrApplicationRequirement or ApplicationPermissionRequirement);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string? caller, HttpMethod method, string route)
    {
        using var request = new HttpRequestMessage(method, route);
        if (caller is not null)
        {
            request.Headers.Add(CallerHeader, caller);
        }

        return await _client.SendAsync(request);
    }

    private RequestAuthority AuthorityFor(string caller)
    {
        var account = Guid.NewGuid();
        var profile = Guid.NewGuid();
        RequestAuthority Human(bool administrator, Guid? session = null) => new(
            PrincipalKind.Human, true,
            AccountId: account,
            ActiveProfileId: profile,
            SessionId: session ?? Guid.NewGuid(),
            AccountEnabled: true,
            GrantEnabled: true,
            AccountIsAdministrator: administrator,
            GrantAdminEnabled: administrator);

        return caller switch
        {
            "member" => Human(administrator: false),
            "admin" => Human(administrator: true),
            "lockedAdmin" => Human(administrator: true, session: _lockedSession),
            // A paired app or native client acting for a signed-in, unlocked administrator.
            "delegatedClient" => Human(administrator: true) with
            {
                PrincipalKind = PrincipalKind.DelegatedUserClient,
                ApplicationId = Guid.NewGuid(),
                ApplicationEnabled = true,
            },
            // An application acting on its own, with no signed-in person behind it.
            "serviceApplication" => new RequestAuthority(
                PrincipalKind.ServiceApplication, true,
                ApplicationId: Guid.NewGuid(),
                ApplicationEnabled: true,
                ApplicationIsAdministrator: true),
            _ => new RequestAuthority(PrincipalKind.Anonymous, false),
        };
    }

    private sealed class CallerResolver(ExcludedFileEndpointTests owner) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(owner.AuthorityFor(context.Request.Headers[CallerHeader].ToString()));
    }

    /// <summary>The administrator PIN: the session marked as locked has the PIN turned on and not yet entered.</summary>
    private sealed class UnlockState(Guid lockedSession) : IGrantAdminUnlockService
    {
        public ValueTask<GrantAdminUnlockState> GetStateAsync(RequestAuthority authority, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new GrantAdminUnlockState(
                authority.AccountId ?? Guid.Empty,
                authority.ActiveProfileId ?? Guid.Empty,
                ProtectionEnabled: authority.SessionId == lockedSession,
                AdminUnlockMode.FixedDuration,
                IsUnlocked: false,
                ExpiresAt: null,
                ProtectionVersion: 1));

        public ValueTask<GrantAdminUnlockState> UnlockAsync(RequestAuthority authority, string pin, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask LockAsync(RequestAuthority authority, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Request.Headers.ContainsKey(CallerHeader)
                ? Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "excluded-files-test")], Scheme.Name)),
                    Scheme.Name)))
                : Task.FromResult(AuthenticateResult.NoResult());
    }
}
