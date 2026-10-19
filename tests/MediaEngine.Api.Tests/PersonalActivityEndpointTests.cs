using System.Text;
using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Activity;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Activity;
using MediaEngine.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Activity routes run as the real mapped handlers (in memory, no network) over a temp data store: who gets which
/// answer, the status of every refusal, and the access rule each route is registered with.
/// </summary>
public sealed class PersonalActivityEndpointTests : IAsyncLifetime
{
    private const string CallerHeader = "X-Test-Caller";
    private static readonly DateTimeOffset Day = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private PersonalActivityWorld _world = null!;
    private WebApplication _app = null!;

    public Task InitializeAsync()
    {
        _world = PersonalActivityWorld.Create();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthorization();
        builder.Services.AddLogging();
        builder.Services.AddSingleton(_world.Service);
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new CallerResolver(_world));
        _app = builder.Build();
        _app.MapPersonalActivityEndpoints();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _world.DisposeAsync();
    }

    // ----- Routes -----

    [Fact]
    public async Task AMember_ReadsTheirOwnActivity_AndAskingForMoreIsForbidden()
    {
        await _world.AddAsync(_world.MemberA, PersonalActivityKinds.Watch, "Mine", Day);
        await _world.AddAsync(_world.AdminA, PersonalActivityKinds.Watch, "Theirs", Day);

        var own = await SendAsync("GET", "/", "member");
        var wider = await SendAsync("GET", "/", "member", query: "scope=everyone");

        Assert.Equal(StatusCodes.Status200OK, own.Status);
        using var json = JsonDocument.Parse(own.Body);
        var items = json.RootElement.GetProperty("items");
        Assert.Equal("Mine", Assert.Single(items.EnumerateArray()).GetProperty("title").GetString());
        Assert.False(json.RootElement.GetProperty("includes_others").GetBoolean());
        Assert.Equal(StatusCodes.Status403Forbidden, wider.Status);
    }

    [Fact]
    public async Task AnInvalidFilter_IsABadRequest()
    {
        Assert.Equal(StatusCodes.Status400BadRequest, (await SendAsync("GET", "/", "member", query: "kind=dance")).Status);
        Assert.Equal(StatusCodes.Status400BadRequest, (await SendAsync("GET", "/", "member", query: "scope=nobody")).Status);
        Assert.Equal(StatusCodes.Status400BadRequest, (await SendAsync("GET", "/periods", "member", query: "outcome=maybe")).Status);
        Assert.Equal(StatusCodes.Status400BadRequest, (await SendAsync("GET", "/", "member", query: "cursor=not-a-cursor")).Status);
    }

    [Fact]
    public async Task TheLimitIsClampedTo200()
    {
        for (var index = 0; index < 205; index++)
        {
            await _world.AddAsync(_world.MemberA, PersonalActivityKinds.Watch, $"Film {index}", Day.AddMinutes(index));
        }

        var page = await SendAsync("GET", "/", "member", query: "limit=100000");

        using var json = JsonDocument.Parse(page.Body);
        Assert.Equal(200, json.RootElement.GetProperty("items").GetArrayLength());
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("next_cursor").GetString()));
    }

    [Fact]
    public async Task TheRecentStrip_IsTheCallersOwnWithTileShapes()
    {
        await _world.AddAsync(_world.MemberA, PersonalActivityKinds.Watch, "Severance", Day, mediaType: "TV");

        var recent = await SendAsync("GET", "/recent", "member");

        using var json = JsonDocument.Parse(recent.Body);
        var tile = Assert.Single(json.RootElement.GetProperty("tiles").EnumerateArray());
        Assert.Equal("landscape", tile.GetProperty("shape").GetString());
    }

    [Fact]
    public async Task TheCsvExport_IsAFileDownloadOfTheCallersOwnActivity()
    {
        await _world.AddAsync(_world.MemberA, PersonalActivityKinds.Watch, "Tremors", Day);

        var export = await SendAsync("GET", "/export.csv", "member");

        Assert.Equal(StatusCodes.Status200OK, export.Status);
        Assert.StartsWith("text/csv", export.ContentType, StringComparison.Ordinal);
        Assert.Contains("Tremors", export.Body, StringComparison.Ordinal);
        Assert.Equal(StatusCodes.Status403Forbidden, (await SendAsync("GET", "/export.csv", "householdAdmin", query: $"scope=household:{_world.HouseholdA}")).Status);
        Assert.Equal(StatusCodes.Status403Forbidden, (await SendAsync("GET", "/export.csv", "member", query: "scope=everyone")).Status);
        Assert.Equal(StatusCodes.Status200OK, (await SendAsync("GET", "/export.csv", "serverAdmin", query: "scope=everyone")).Status);
    }

    [Fact]
    public async Task OnlyAServerAdministrator_CanPreviewOrDeleteHistory_EveryoneElseGets403()
    {
        await _world.AddAsync(_world.MemberA, PersonalActivityKinds.Watch, "Old", Day);
        var before = Uri.EscapeDataString(Day.AddDays(10).ToString("O"));

        foreach (var caller in new[] { "member", "householdAdmin", "lockedServerAdmin", "pairedApp" })
        {
            Assert.Equal(StatusCodes.Status403Forbidden, (await SendAsync("GET", "/delete-preview", caller, query: $"before={before}&scope=everyone")).Status);
            Assert.Equal(StatusCodes.Status403Forbidden, (await SendAsync("DELETE", "/", caller, query: $"before={before}&scope=everyone")).Status);
        }

        var preview = await SendAsync("GET", "/delete-preview", "serverAdmin", query: $"before={before}&scope=everyone");
        using (var json = JsonDocument.Parse(preview.Body))
        {
            Assert.Equal(1, json.RootElement.GetProperty("count").GetInt32());
        }

        var delete = await SendAsync("DELETE", "/", "serverAdmin", query: $"before={before}&scope=everyone");
        using (var json = JsonDocument.Parse(delete.Body))
        {
            Assert.Equal(1, json.RootElement.GetProperty("deleted").GetInt32());
        }

        Assert.Equal(1, _world.AuditCount(PersonalActivityReadService.HistoryDeleted));
    }

    [Fact]
    public async Task DeletingWithoutADate_IsABadRequest_AndRemovesNothing()
    {
        await _world.AddAsync(_world.MemberA, PersonalActivityKinds.Watch, "Old", Day);

        Assert.Equal(StatusCodes.Status400BadRequest, (await SendAsync("DELETE", "/", "serverAdmin", query: "scope=everyone")).Status);
        Assert.Equal(StatusCodes.Status400BadRequest, (await SendAsync("GET", "/delete-preview", "serverAdmin", query: "scope=everyone")).Status);
        Assert.Equal(StatusCodes.Status400BadRequest, (await SendAsync("DELETE", "/", "serverAdmin", query: "before=yesterday&scope=everyone")).Status);

        Assert.Equal(0, _world.AuditCount(PersonalActivityReadService.HistoryDeleted));
        Assert.Single((await _world.Service.ListAsync(_world.MemberOfA, new PersonalActivityQuery(), default)).Value!.Items);
    }

    [Fact]
    public async Task ThePictureRoute_ServesTheImageOnlyToSomeoneWhoMaySeeIt_WithPrivateCacheHeaders()
    {
        var key = new string('a', 64);
        await _world.Repository.SaveArtworkAsync(new PersonalActivityArtwork(key, [1, 2, 3, 4], "image/jpeg", 10, 10));
        await _world.AddAsync(_world.AdminA, PersonalActivityKinds.Watch, "Alex film", Day, artwork: key);

        var owner = await SendAsync("GET", "/artwork/{key}", "householdAdmin", key: key);
        var member = await SendAsync("GET", "/artwork/{key}", "member", key: key);
        var memberWider = await SendAsync("GET", "/artwork/{key}", "member", key: key, query: "scope=everyone");
        var serverAdmin = await SendAsync("GET", "/artwork/{key}", "serverAdmin", key: key, query: "scope=everyone");

        Assert.Equal(StatusCodes.Status200OK, owner.Status);
        Assert.Equal("image/jpeg", owner.ContentType);
        Assert.Equal("private, max-age=86400", owner.CacheControl);
        Assert.Equal(StatusCodes.Status404NotFound, member.Status);
        Assert.Equal(StatusCodes.Status403Forbidden, memberWider.Status);
        Assert.Equal(StatusCodes.Status200OK, serverAdmin.Status);
    }

    [Fact]
    public async Task ASignedOutCaller_IsRefusedOnEveryRoute()
    {
        foreach (var (method, route) in new[] { ("GET", "/"), ("GET", "/periods"), ("GET", "/recent"), ("GET", "/export.csv"), ("GET", "/delete-preview"), ("DELETE", "/") })
        {
            var result = await SendAsync(method, route, null, query: $"before={Uri.EscapeDataString(Day.ToString("O"))}");
            Assert.Contains(result.Status, new[] { StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden });
        }
    }

    // ----- Registration -----

    [Fact]
    public void EveryRoute_RequiresASignedInCaller_AndExportAndDeleteAreForPeopleNotApps()
    {
        var routes = Endpoints().ToArray();

        Assert.Equal(7, routes.Length);
        Assert.All(routes, endpoint =>
        {
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
            Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            // Nothing takes the person from a route value: the scope is decided by the Engine.
            Assert.DoesNotContain(endpoint.RoutePattern.Parameters, parameter =>
                parameter.Name.Contains("profile", StringComparison.OrdinalIgnoreCase));
        });

        var humanOnly = new[] { "ExportPersonalActivityCsv", "PreviewPersonalActivityDeletion", "DeletePersonalActivity" };
        foreach (var name in humanOnly)
        {
            var endpoint = Assert.Single(routes, route => route.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);
            var policy = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), data => !string.IsNullOrEmpty(data.Policy));
            Assert.Equal(AuthPolicies.HumanSelfService, policy.Policy);
        }

        var reads = routes.Where(route => route.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName is not null and not ("ExportPersonalActivityCsv" or "PreviewPersonalActivityDeletion" or "DeletePersonalActivity"));
        Assert.All(reads, endpoint =>
            Assert.Equal(ClientApiScopes.ProgressRead, Assert.Single(endpoint.Metadata.GetOrderedMetadata<ClientScopeRequirementMetadata>()).Scope));
    }

    [Fact]
    public void DeletingHistory_IsTheOnlyDeleteRoute()
    {
        var methods = Endpoints()
            .Select(endpoint => (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Single(), endpoint.RoutePattern.RawText))
            .ToArray();

        Assert.Equal([("DELETE", "/api/v1/activity/")], methods.Where(route => route.Item1 != "GET"));
    }

    private IEnumerable<RouteEndpoint> Endpoints() =>
        ((IEndpointRouteBuilder)_app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1/activity", StringComparison.Ordinal) == true);

    /// <summary>Runs the mapped route's handler as the given caller and returns what it wrote.</summary>
    private async Task<(int Status, string Body, string? ContentType, string? CacheControl)> SendAsync(
        string method, string route, string? caller, string? key = null, string? query = null)
    {
        var pattern = "/api/v1/activity" + (route == "/" ? "/" : route);
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

        if (key is not null)
        {
            context.Request.RouteValues["key"] = key;
        }

        using var body = new MemoryStream();
        context.Response.Body = body;
        await endpoint.RequestDelegate!(context);
        body.Position = 0;
        return (
            context.Response.StatusCode,
            Encoding.UTF8.GetString(body.ToArray()),
            context.Response.ContentType,
            context.Response.Headers.CacheControl.ToString() is { Length: > 0 } cache ? cache : null);
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
