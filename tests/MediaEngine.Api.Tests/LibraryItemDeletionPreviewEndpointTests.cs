using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Contracts.Items;
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
/// POST /library/items/deletion-preview run through the real authorization pipeline: the real Administrator policy, handler and
/// decision service, with only the PIN-unlock state and who is calling supplied by the test. The preview behind it is the real service
/// over a temp data store and temp folders.
/// </summary>
public sealed class LibraryItemDeletionPreviewEndpointTests : IAsyncLifetime
{
    private const string CallerHeader = "X-Test-Caller";
    private const string Route = "/library/items/deletion-preview";

    private readonly LibraryItemDeletionTestWorld _world = new();
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
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IAuthorizationHandler, EffectiveAdministratorHandler>();
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new CallerResolver(this));
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new AccountAccessDecisionService(
            new AccountRepository(_world.Database), new UnlockState(_lockedSession)));
        builder.Services.AddSingleton(_world.Service);
        // The other routes in the group are mapped but never called here; their services are only named so the group can be built.
        builder.Services.AddSingleton<ILibraryItemRepository>(_ => throw new InvalidOperationException("Not used by these tests."));
        builder.Services.AddSingleton<ILibraryItemCurationRepository>(_ => throw new InvalidOperationException("Not used by these tests."));
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapLibraryItemEndpoints();
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
    public async Task AnAdministrator_GetsThePreview_WithTheWireNames()
    {
        var file = _world.WriteFile(_world.Managed, Path.Combine("Dune", "Dune.mkv"), bytes: 321);
        var (workId, _) = _world.AddWork("Dune", [file]);

        var response = await SendAsync("admin", [workId]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(
            ["items", "files", "curated_collections", "profiles_with_progress", "processing_running", "bytes_freed", "fingerprint"],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(321, root.GetProperty("bytes_freed").GetInt64());
        var row = root.GetProperty("files")[0];
        Assert.Equal(
            ["work_id", "asset_id", "library_name", "folder_label", "file_name", "size_bytes", "outcome", "reason", "is_sidecar"],
            row.EnumerateObject().Select(property => property.Name));
        Assert.Equal("delete", row.GetProperty("outcome").GetString());
        Assert.True(File.Exists(file));
    }

    [Theory]
    [InlineData("member")]
    [InlineData("lockedAdmin")]
    [InlineData("serviceApplication")]
    [InlineData("delegatedClient")]
    public async Task EveryoneWhoIsNotAnUnlockedAdministratorInPerson_GetsForbidden_AndNothingIsPreviewed(string caller)
    {
        var (workId, _) = _world.AddWork("Dune", []);

        var response = await SendAsync(caller, [workId]);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ARequestWithNoSignedInCaller_IsNotServed()
    {
        var (workId, _) = _world.AddWork("Dune", []);

        var response = await SendAsync(null, [workId]);

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
    }

    [Fact]
    public async Task AViewPersonalItem_IsA404_AParent_IsA400_AndTooManyIds_IsA400()
    {
        var viewItem = _world.AddViewItem();
        var (show, _) = _world.AddWork("A show", [], mediaType: "TV", workKind: "parent");

        var notFound = await SendAsync("admin", [viewItem]);
        var parent = await SendAsync("admin", [show]);
        var tooMany = await SendAsync("admin", Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToList());
        var none = await SendAsync("admin", []);

        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Contains(viewItem.ToString("D"), await notFound.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, parent.StatusCode);
        Assert.Contains("Only individual titles can be deleted.", await parent.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
    }

    [Fact]
    public void TheRoute_IsInPersonAdministratorOnly_WithNoApplicationPermission_AndIsNamed()
    {
        var endpoint = ((IEndpointRouteBuilder)_app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == Route);

        Assert.Equal("PreviewLibraryItemDeletion", endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);
        Assert.Equal(["POST"], endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
        var policy = Assert.Single(endpoint.Metadata.OfType<AuthorizationPolicy>());
        var requirement = Assert.Single(policy.Requirements.OfType<EffectiveAdministratorRequirement>());
        Assert.True(requirement.SurfaceUnlock);
        Assert.True(requirement.HumanOnly);
        Assert.False(requirement.AllowHouseholdAdministrator);
        Assert.DoesNotContain(policy.Requirements, item => item is AdministratorOrApplicationRequirement or ApplicationPermissionRequirement);
        Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
    }

    private async Task<HttpResponseMessage> SendAsync(string? caller, IReadOnlyList<Guid> workIds)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(new LibraryItemDeletionPreviewRequest { WorkIds = workIds }),
        };
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

    private sealed class CallerResolver(LibraryItemDeletionPreviewEndpointTests owner) : IRequestAuthorityResolver
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
                    new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "deletion-preview-test")], Scheme.Name)),
                    Scheme.Name)))
                : Task.FromResult(AuthenticateResult.NoResult());
    }
}
