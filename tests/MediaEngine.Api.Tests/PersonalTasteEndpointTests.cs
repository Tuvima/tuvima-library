using System.Text;
using System.Text.Json;
using MediaEngine.AI.Configuration;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Taste;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Taste;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Taste routes run as the real mapped handlers (in memory, no network) over a temp data store: who gets which answer,
/// the status of every refusal, and the access rule each route is registered with.
/// </summary>
public sealed class PersonalTasteEndpointTests : IAsyncLifetime
{
    private const string CallerHeader = "X-Test-Caller";

    private PersonalActivityWorld _world = null!;
    private WebApplication _app = null!;
    private TasteOverrideRepository _overrides = null!;

    public Task InitializeAsync()
    {
        _world = PersonalActivityWorld.Create();
        _overrides = new TasteOverrideRepository(_world.Database);
        var service = new PersonalTasteService(
            _world.Scope, new LearnedNothing(), _overrides, new ProfileRepository(_world.Database), new AiSettings(), _world.Clock);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthorization();
        builder.Services.AddLogging();
        builder.Services.AddSingleton(service);
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new CallerResolver(_world));
        _app = builder.Build();
        _app.MapPersonalTasteEndpoints();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _world.DisposeAsync();
    }

    // ----- Reading -----

    [Fact]
    public async Task AMember_ReadsTheirOwnTaste_AndAskingForMoreIsForbidden()
    {
        var own = await SendAsync("GET", "/", "member");
        var wider = await SendAsync("GET", "/", "member", query: "scope=everyone");

        Assert.Equal(StatusCodes.Status200OK, own.Status);
        using var json = JsonDocument.Parse(own.Body);
        Assert.Equal("me", json.RootElement.GetProperty("scope").GetString());
        Assert.False(json.RootElement.GetProperty("includes_others").GetBoolean());
        Assert.Equal(1, json.RootElement.GetProperty("people").GetArrayLength());
        Assert.Equal(StatusCodes.Status403Forbidden, wider.Status);
    }

    [Fact]
    public async Task AnInvalidScope_IsABadRequest()
    {
        Assert.Equal(StatusCodes.Status400BadRequest, (await SendAsync("GET", "/", "member", query: "scope=nobody")).Status);
    }

    [Fact]
    public async Task EachScopeGetsTheRightStatus()
    {
        var other = $"profile:{_world.MemberB.ProfileId}";
        var household = $"household:{_world.HouseholdA}";

        Assert.Equal(StatusCodes.Status403Forbidden, (await SendAsync("GET", "/", "householdAdmin", query: "scope=everyone")).Status);
        Assert.Equal(StatusCodes.Status403Forbidden, (await SendAsync("GET", "/", "householdAdmin", query: $"scope={other}")).Status);
        Assert.Equal(StatusCodes.Status200OK, (await SendAsync("GET", "/", "householdAdmin", query: $"scope={household}")).Status);
        Assert.Equal(StatusCodes.Status200OK, (await SendAsync("GET", "/", "serverAdmin", query: "scope=everyone")).Status);
        Assert.Equal(StatusCodes.Status403Forbidden, (await SendAsync("GET", "/", "lockedServerAdmin", query: "scope=everyone")).Status);
        Assert.Equal(StatusCodes.Status404NotFound, (await SendAsync("GET", "/", "serverAdmin", query: $"scope=profile:{Guid.NewGuid()}")).Status);
    }

    [Fact]
    public async Task ViewingOthersOverTheWire_WritesTheLogEntry()
    {
        await SendAsync("GET", "/", "serverAdmin", query: "scope=everyone");
        await SendAsync("GET", "/", "member");

        Assert.Equal(1, _world.AuditCount(PersonalScopeAuthorizer.TasteViewedOthers));
    }

    // ----- Editing -----

    [Fact]
    public async Task APersonAddsAndRemovesTheirOwnTaste()
    {
        var add = await SendAsync("POST", "/items", "member", body: """{"group":"moods","value":"Gritty"}""");
        var remove = await SendAsync("DELETE", "/items/{group}/{**value}", "member", group: "genres", value: "drama");
        var read = await SendAsync("GET", "/", "member");

        Assert.Equal(StatusCodes.Status204NoContent, add.Status);
        Assert.Equal(StatusCodes.Status204NoContent, remove.Status);
        var changes = await _overrides.ListAsync([_world.MemberA.ProfileId]);
        Assert.Contains(new TasteOverride(_world.MemberA.ProfileId, TasteGroups.Mood, "gritty", TasteOverrideActions.Added), changes);
        Assert.Contains(new TasteOverride(_world.MemberA.ProfileId, TasteGroups.Genre, "drama", TasteOverrideActions.Removed), changes);
        Assert.Contains("gritty", read.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AValueWithASlash_CanBeRemoved()
    {
        var remove = await SendAsync("DELETE", "/items/{group}/{**value}", "member", group: "genres", value: "rhythm and blues/soul");

        Assert.Equal(StatusCodes.Status204NoContent, remove.Status);
        Assert.Contains(await _overrides.ListAsync(null), change => change.Value == "rhythm and blues/soul");
    }

    [Fact]
    public async Task EditingSignedOut_IsRefusedAndStoresNothing()
    {
        foreach (var caller in new string?[] { null })
        {
            var add = await SendAsync("POST", "/items", caller, body: """{"group":"genres","value":"western"}""");
            var remove = await SendAsync("DELETE", "/items/{group}/{**value}", caller, group: "genres", value: "drama");

            Assert.Contains(add.Status, new[] { StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden });
            Assert.Contains(remove.Status, new[] { StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden });
        }

        Assert.Empty(await _overrides.ListAsync(null));
    }

    [Fact]
    public async Task AnInvalidEdit_IsABadRequest()
    {
        Assert.Equal(StatusCodes.Status400BadRequest, (await SendAsync("POST", "/items", "member", body: """{"group":"flavours","value":"sweet"}""")).Status);
        Assert.Equal(StatusCodes.Status400BadRequest, (await SendAsync("POST", "/items", "member", body: """{"group":"eras","value":"long ago"}""")).Status);
        Assert.Equal(StatusCodes.Status400BadRequest, (await SendAsync("DELETE", "/items/{group}/{**value}", "member", group: "moods", value: " ")).Status);
    }

    [Fact]
    public async Task TheEditRoutesCarryNoPerson_SoNobodyCanNameSomeoneElse()
    {
        var edits = Endpoints().Where(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Single() != "GET").ToArray();

        Assert.Equal(2, edits.Length);
        Assert.All(edits, endpoint =>
            Assert.DoesNotContain(endpoint.RoutePattern.Parameters, parameter =>
                parameter.Name.Contains("profile", StringComparison.OrdinalIgnoreCase) ||
                parameter.Name.Contains("scope", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(
            typeof(AddTasteItemRequest).GetProperties(),
            property => property.Name.Contains("Profile", StringComparison.OrdinalIgnoreCase));
    }

    // ----- Registration -----

    [Fact]
    public void EveryRoute_RequiresASignedInCaller_AndEditsAreForPeopleNotApps()
    {
        var routes = Endpoints().ToArray();

        Assert.Equal(3, routes.Length);
        Assert.All(routes, endpoint =>
        {
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
            Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        });

        foreach (var name in new[] { "AddPersonalTasteItem", "RemovePersonalTasteItem" })
        {
            var endpoint = Assert.Single(routes, route => route.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);
            var policy = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), data => !string.IsNullOrEmpty(data.Policy));
            Assert.Equal(AuthPolicies.HumanSelfService, policy.Policy);
        }

        var read = Assert.Single(routes, route => route.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "GetPersonalTaste");
        Assert.Equal(ClientApiScopes.ProgressRead, Assert.Single(read.Metadata.GetOrderedMetadata<ClientScopeRequirementMetadata>()).Scope);
    }

    private IEnumerable<RouteEndpoint> Endpoints() =>
        ((IEndpointRouteBuilder)_app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1/taste", StringComparison.Ordinal) == true);

    /// <summary>Runs the mapped route's handler as the given caller and returns what it wrote.</summary>
    private async Task<(int Status, string Body)> SendAsync(
        string method, string route, string? caller, string? query = null, string? body = null, string? group = null, string? value = null)
    {
        var pattern = "/api/v1/taste" + (route == "/" ? "/" : route);
        var endpoint = Endpoints().Single(candidate => candidate.RoutePattern.RawText == pattern
            && candidate.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));
        await using var scope = _app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = method;
        if (caller is not null)
        {
            context.Request.Headers[CallerHeader] = caller;
        }

        if (query is not null)
        {
            context.Request.QueryString = new QueryString("?" + query);
        }

        if (group is not null)
        {
            context.Request.RouteValues["group"] = group;
        }

        if (value is not null)
        {
            context.Request.RouteValues["value"] = value;
        }

        if (body is not null)
        {
            context.Request.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection());
        }

        using var response = new MemoryStream();
        context.Response.Body = response;
        await endpoint.RequestDelegate!(context);
        return (context.Response.StatusCode, Encoding.UTF8.GetString(response.ToArray()));
    }

    private sealed class BodyDetection : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class LearnedNothing : ITasteProfiler
    {
        public Task<TasteProfileBuildResult> GetProfileAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult(new TasteProfileBuildResult(TasteProfileBuildStatus.InsufficientData, userId, null, 0, "none"));
    }

    private sealed class CallerResolver(PersonalActivityWorld world) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(context.Request.Headers[CallerHeader].ToString() switch
            {
                "member" => world.MemberOfA,
                "householdAdmin" => world.AdminOfA,
                "serverAdmin" => world.ServerAdmin,
                "lockedServerAdmin" => world.LockedServerAdmin,
                "pairedApp" => world.ServerAdminApp,
                _ => new RequestAuthority(PrincipalKind.Anonymous, false),
            });
    }
}
