using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services.Vault;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MediaEngine.Api.Tests;

// Identify, the match record and the quiet activity list for the Vault (Videos spec WP12, D32, D38): every one of them answers
// "not found" to anyone without an unlocked Vault mode session, exactly as a missing file does, and none of them can be cached. The routes
// run for real over the real data store; the Identify service itself is a stand-in that fails the test if a locked request reaches it.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private const string UnknownCandidate = """{"server_id":"stashdb","record_id":"rec-1"}""";

    private Task<IReadOnlyDictionary<string, RouteEndpoint>> MapVaultIdentifyRoutesAsync(DefaultHttpContext resourceContext)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddSingleton<IVaultLibraryRepository>(new VaultLibraryRepository(_database));
        builder.Services.AddSingleton<INsfwAccessRepository>(new NsfwAccessRepository(_database));
        builder.Services.AddSingleton<IVaultMatchRepository>(new VaultMatchRepository(_database));
        builder.Services.AddSingleton<IVaultActivityRepository>(new VaultActivityRepository(_database));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<VaultAccessResolver>();
        builder.Services.AddSingleton(CreateResourceService(resourceContext));
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new RequestAuthorityResolver(_accounts, _applications, new ProfileRepository(_database)));
        // Only here so mapping can infer each route's parameters; a locked request must never get as far as using it.
        builder.Services.AddSingleton(NewTouchFailsIdentifyService());
        var app = builder.Build();
        app.MapVaultIdentifyEndpoints();
        _routeApps.Add(app);
        IReadOnlyDictionary<string, RouteEndpoint> routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>() is not null)
            .ToDictionary(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName!, StringComparer.Ordinal);
        return Task.FromResult(routes);
    }

    /// <summary>The real Identify service built over stand-ins that fail the test when touched, so a locked request that reaches it is caught.</summary>
    private static VaultIdentifyService NewTouchFailsIdentifyService()
    {
        T Fails<T>() where T : class => (T)CreateTouchFailsProxy(typeof(T));
        var naming = new VaultNamingConfigurationProvider(() => null);
        var reader = new VaultFileNameReader(naming);
        var paths = new MediaEngine.Domain.Services.TuvimaDataPaths(Path.Combine(Path.GetTempPath(), "tuvima-vault-identify-routes"));
        var applier = new VaultRecordApplier(
            Fails<ICanonicalValueRepository>(), Fails<IMetadataClaimRepository>(), Fails<IVaultNameRepository>(), Fails<IVaultArtworkRepository>(),
            Fails<IVaultMatchRepository>(), paths, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<VaultRecordApplier>.Instance);
        return new VaultIdentifyService(
            Fails<IProviderAudienceGate>(), Fails<IVaultLibraryRepository>(), Fails<IMediaAssetRepository>(), Fails<ICanonicalValueRepository>(),
            Fails<IVaultMatchRepository>(), Fails<IVaultNameRepository>(), Fails<IVaultArtworkRepository>(), Fails<IVaultActivityRepository>(),
            [], Fails<IStashBoxSettings>(), reader, naming, applier, TimeProvider.System);
    }

    private async Task<RouteResult> InvokeVaultRouteAsync(
        RouteEndpoint endpoint, Guid account, Guid session, (string Name, string Value) route, string? body, string method)
    {
        var app = _routeApps[^1];
        await using var scope = app.Services.CreateAsyncScope();
        var context = VaultContext(account, session);
        context.RequestServices = scope.ServiceProvider;
        context.Request.Method = method;
        context.Request.RouteValues[route.Name] = route.Value;
        context.Response.Body = new MemoryStream();
        if (body is not null)
        {
            context.Request.ContentType = "application/json";
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new HasBody());
        }

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return new RouteResult(
            context.Response.StatusCode, text,
            context.Response.Headers.TryGetValue("Cache-Control", out var cache) ? cache.ToString() : null);
    }

    [Theory]
    [InlineData("SearchVaultIdentifyCandidates", "POST", """{"text":"anything"}""")]
    [InlineData("PreviewVaultIdentifyCandidate", "POST", UnknownCandidate)]
    [InlineData("ApplyVaultIdentifyCandidate", "POST", """{"server_id":"stashdb","record_id":"rec-1","fields":["title"]}""")]
    [InlineData("GetVaultMatch", "GET", null)]
    public async Task VaultIdentify_ALockedSession_GetsExactlyTheAnswerAMissingFileGets_AndItIsNeverCached(string routeName, string method, string? body)
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: true);
        var session = NewVaultSession(shape.Account);
        var routes = await MapVaultIdentifyRoutesAsync(VaultContext(shape.Account, session));

        var missing = await InvokeVaultRouteAsync(routes[routeName], shape.Account, session, ("assetId", Guid.NewGuid().ToString("D")), body, method);
        var vault = await InvokeVaultRouteAsync(routes[routeName], shape.Account, session, ("assetId", shape.Secret.AssetId.ToString("D")), body, method);

        Assert.Equal(StatusCodes.Status404NotFound, missing.Status);
        Assert.Equal(missing.Status, vault.Status);
        Assert.Equal(missing.Body, vault.Body);
        Assert.Equal("private, no-store", vault.CacheControl);
    }

    [Fact]
    public async Task VaultIdentify_AnUnlockedAdministrator_CanSeeHowAVaultVideoWasMatched_AndAnUnmatchedOneSaysSo()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: true);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var routes = await MapVaultIdentifyRoutesAsync(VaultContext(shape.Account, session));

        var unmatched = await InvokeVaultRouteAsync(routes["GetVaultMatch"], shape.Account, session, ("assetId", shape.Secret.AssetId.ToString("D")), null, "GET");
        Assert.Equal(StatusCodes.Status200OK, unmatched.Status);
        Assert.False(JsonDocument.Parse(unmatched.Body).RootElement.GetProperty("matched").GetBoolean());

        var now = DateTimeOffset.UtcNow;
        await new VaultMatchRepository(_database).SaveMatchAsync(
            new VaultMatch(shape.Secret.AssetId, "stashdb", "rec-1", "product_code", "automatic", now),
            [new VaultFieldSource(shape.Secret.AssetId, "title", "stashdb", "rec-1", "product_code", "automatic", "und-Latn", now)]);

        var matched = await InvokeVaultRouteAsync(routes["GetVaultMatch"], shape.Account, session, ("assetId", shape.Secret.AssetId.ToString("D")), null, "GET");
        var root = JsonDocument.Parse(matched.Body).RootElement;
        Assert.True(root.GetProperty("matched").GetBoolean());
        Assert.Equal("product_code", root.GetProperty("matched_by").GetString());
        Assert.Equal("title", root.GetProperty("fields")[0].GetProperty("field").GetString());
        Assert.Equal("private, no-store", matched.CacheControl);

        // An ordinary file has no Vault match to show, even to an unlocked administrator.
        var ordinary = await InvokeVaultRouteAsync(routes["GetVaultMatch"], shape.Account, session, ("assetId", shape.Normal.AssetId.ToString("D")), null, "GET");
        Assert.Equal(StatusCodes.Status404NotFound, ordinary.Status);
    }

    [Fact]
    public async Task VaultIdentify_ApplyingNoFieldsOrAnUnknownField_IsRefusedBeforeAnythingIsAsked()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: true);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var routes = await MapVaultIdentifyRoutesAsync(VaultContext(shape.Account, session));

        foreach (var body in new[]
        {
            """{"server_id":"stashdb","record_id":"rec-1","fields":[]}""",
            """{"server_id":"stashdb","record_id":"rec-1","fields":["hair_colour"]}""",
        })
        {
            var result = await InvokeVaultRouteAsync(routes["ApplyVaultIdentifyCandidate"], shape.Account, session, ("assetId", shape.Secret.AssetId.ToString("D")), body, "POST");

            Assert.Equal(StatusCodes.Status400BadRequest, result.Status);
        }
    }

    [Fact]
    public async Task VaultActivity_IsOnlyForAnAdministratorWithNsfwModeOn_AndEveryoneElseGetsNotFound()
    {
        var admin = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: true);
        var adminSession = NewVaultSession(admin.Account);
        var routes = await MapVaultIdentifyRoutesAsync(VaultContext(admin.Account, adminSession));
        await new VaultActivityRepository(_database).AddAsync(admin.Secret.AssetId, VaultActivityKinds.Unmatched, "clip.mkv", "No strong match was found.", DateTimeOffset.UtcNow);

        var locked = await InvokeVaultRouteAsync(routes["ListVaultActivity"], admin.Account, adminSession, ("none", "none"), null, "GET");
        Assert.Equal(StatusCodes.Status404NotFound, locked.Status);
        Assert.Equal("private, no-store", locked.CacheControl);

        await UnlockVaultAsync(adminSession);
        var unlocked = await InvokeVaultRouteAsync(routes["ListVaultActivity"], admin.Account, adminSession, ("none", "none"), null, "GET");
        Assert.Equal(StatusCodes.Status200OK, unlocked.Status);
        var line = JsonDocument.Parse(unlocked.Body).RootElement[0];
        Assert.Equal("unmatched", line.GetProperty("kind").GetString());
        Assert.Equal("clip.mkv", line.GetProperty("file_name").GetString());
        Assert.Equal("private, no-store", unlocked.CacheControl);
    }

    [Fact]
    public async Task VaultActivity_AnUnlockedPersonWhoIsNotAnAdministrator_GetsNotFound()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var routes = await MapVaultIdentifyRoutesAsync(VaultContext(shape.Account, session));

        var result = await InvokeVaultRouteAsync(routes["ListVaultActivity"], shape.Account, session, ("none", "none"), null, "GET");

        Assert.Equal(StatusCodes.Status404NotFound, result.Status);
    }
}
