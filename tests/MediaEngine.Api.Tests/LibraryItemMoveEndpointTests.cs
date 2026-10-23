using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Library;
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
/// The three move actions run through the real authorization pipeline, with the real administrator-or-application policy and handler.
/// Only who is calling is supplied by the test. Moves change library structure, so they are authorised exactly like the other
/// structural edit actions: an administrator, or an application holding the metadata-write permission.
/// </summary>
public sealed class LibraryItemMoveEndpointTests : IAsyncLifetime
{
    private const string CallerHeader = "X-Test-Caller";

    private readonly LibraryMoveTestWorld _world = new();
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
        builder.Services.AddSingleton<IAuthorizationHandler, AdministratorOrApplicationHandler>();
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new CallerResolver());
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new AccountAccessDecisionService(
            new ProfileAccessRepository(_world.Database), new NoUnlock()));
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator>(new DenyingEvaluator());
        // The other routes in the group are mapped but never reached by these requests; their services are only named so the group can be built.
        builder.Services.AddSingleton<IProfileRepository>(new ProfileRepository(_world.Database));
        builder.Services.AddSingleton<MediaEngine.Api.Services.Libraries.LibraryItemDeletionService>(_ => throw new InvalidOperationException("Not used by these tests."));
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

    public static TheoryData<string, string, string> EveryoneWhoMayNotEdit()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var caller in new[] { "member", "serviceApplication", "delegatedClient" })
        {
            data.Add(caller, "GET", $"/library/items/{Guid.NewGuid()}/move-destinations");
            data.Add(caller, "POST", "/library/items/move-preview");
            data.Add(caller, "POST", "/library/items/move");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryoneWhoMayNotEdit))]
    public async Task ANonEditor_GetsForbidden_OnEveryMoveAction_AndNothingIsLookedAt(string caller, string method, string path)
    {
        var response = await SendAsync(caller, method, path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/library/items/00000000-0000-0000-0000-000000000001/move-destinations")]
    [InlineData("POST", "/library/items/move-preview")]
    [InlineData("POST", "/library/items/move")]
    public async Task ARequestWithNoSignedInCaller_IsNotServed(string method, string path)
    {
        var response = await SendAsync(null, method, path);

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
    }

    [Fact]
    public void TheThreeRoutes_UseTheSamePolicyAsTheOtherStructuralEdits_AreNamed_AndHaveTheDocumentedShapes()
    {
        var endpoints = ((IEndpointRouteBuilder)_app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToList();
        var expected = new[]
        {
            ("GetLibraryItemMoveDestinations", "/library/items/{id:guid}/move-destinations", "GET"),
            ("PreviewLibraryItemMove", "/library/items/move-preview", "POST"),
            ("MoveLibraryItems", "/library/items/move", "POST"),
        };
        foreach (var (name, pattern, method) in expected)
        {
            var endpoint = Assert.Single(endpoints, candidate => candidate.RoutePattern.RawText == pattern);
            Assert.Equal(name, endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);
            Assert.Equal([method], endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
            var policy = Assert.Single(endpoint.Metadata.OfType<AuthorizationPolicy>());
            var requirement = Assert.Single(policy.Requirements.OfType<AdministratorOrApplicationRequirement>());
            Assert.Equal(ApplicationPermissionIds.MetadataWrite, requirement.Permission);
            Assert.False(requirement.AllowHouseholdAdministrator);
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string? caller, string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new LibraryMovePreviewRequest { ItemIds = [Guid.NewGuid()], DestinationId = Guid.NewGuid() });
        }

        if (caller is not null)
        {
            request.Headers.Add(CallerHeader, caller);
        }

        return await _client.SendAsync(request);
    }

    private static RequestAuthority AuthorityFor(string caller)
    {
        var account = Guid.NewGuid();
        var profile = Guid.NewGuid();
        RequestAuthority Human(bool administrator) => new(
            PrincipalKind.Human, true,
            AccountId: account,
            ActiveProfileId: profile,
            SessionId: Guid.NewGuid(),
            AccountEnabled: true,
            ActiveProfileInUserHousehold: true,
            ActiveProfileIsServerAdministrator: administrator);

        return caller switch
        {
            "member" => Human(administrator: false),
            // A paired app acting for a signed-in person; the evaluator holds no metadata-write permission for it.
            "delegatedClient" => Human(administrator: false) with
            {
                PrincipalKind = PrincipalKind.DelegatedUserClient,
                ApplicationId = Guid.NewGuid(),
                ApplicationEnabled = true,
            },
            "serviceApplication" => new RequestAuthority(
                PrincipalKind.ServiceApplication, true,
                ApplicationId: Guid.NewGuid(),
                ApplicationEnabled: true),
            _ => new RequestAuthority(PrincipalKind.Anonymous, false),
        };
    }

    private sealed class CallerResolver : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(AuthorityFor(context.Request.Headers[CallerHeader].ToString()));
    }

    private sealed class NoUnlock : IGrantAdminUnlockService
    {
        public ValueTask<GrantAdminUnlockState> GetStateAsync(RequestAuthority authority, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new GrantAdminUnlockState(
                authority.AccountId ?? Guid.Empty,
                authority.ActiveProfileId ?? Guid.Empty,
                ProtectionEnabled: false,
                AdminUnlockMode.FixedDuration,
                IsUnlocked: false,
                ExpiresAt: null,
                ProtectionVersion: 1));

        public ValueTask<GrantAdminUnlockState> UnlockAsync(RequestAuthority authority, string pin, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask LockAsync(RequestAuthority authority, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>No application is allowed any operation, so only the person's own administrator rights could open these routes.</summary>
    private sealed class DenyingEvaluator : MediaEngine.Domain.Contracts.IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(
            RequestAuthority authority,
            AuthorizationRequirement requirement,
            ResourceAuthorizationContext? resource,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Deny(AuthorizationDenialReason.MissingPermission));
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
                    new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "library-move-test")], Scheme.Name)),
                    Scheme.Name)))
                : Task.FromResult(AuthenticateResult.NoResult());
    }
}
