using System.Text;
using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

// Browsing the Vault (Videos spec WP14, D39 to D41): one central gate. A locked session, an account without the Watch feature and a
// session that has not switched Vault mode on all get the same "not found" a missing page gets, privately and uncacheably; an
// unlocked one is let in and only sees Vault files.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private static readonly string[] VaultBrowseRouteNames =
    [
        "ListVaultScenes", "GetVaultScene", "ListVaultFolders", "ListVaultPerformers", "ListVaultStudios", "ListVaultTags", "SearchVault",
    ];

    [Theory]
    [InlineData("ListVaultScenes")]
    [InlineData("GetVaultScene")]
    [InlineData("ListVaultFolders")]
    [InlineData("ListVaultPerformers")]
    [InlineData("ListVaultStudios")]
    [InlineData("ListVaultTags")]
    [InlineData("SearchVault")]
    public async Task VaultBrowse_ALockedSession_GetsNotFound_PrivatelyAndUncacheably_ForEveryRoute(string routeName)
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: true);
        var routes = await MapRoutesAsync(builder => builder.MapVaultEndpoints());

        var result = await InvokeBrowseAsync(routes[routeName], shape.Account, NewVaultSession(shape.Account), shape.Secret.AssetId);

        Assert.Equal(StatusCodes.Status404NotFound, result.Status);
        Assert.Equal("private, no-store", result.CacheControl);
        Assert.DoesNotContain("Secret scene", result.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VaultBrowse_EveryBrowsingRoute_IsMapped_AndTheLockedAnswerIsTheSameAsForAMissingPage()
    {
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: false);
        var routes = await MapRoutesAsync(builder => builder.MapVaultEndpoints());
        Assert.All(VaultBrowseRouteNames, name => Assert.True(routes.ContainsKey(name), name));
        var session = NewVaultSession(shape.Account);

        var withRealId = await InvokeBrowseAsync(routes["GetVaultScene"], shape.Account, session, shape.Secret.AssetId);
        var withMissingId = await InvokeBrowseAsync(routes["GetVaultScene"], shape.Account, session, Guid.NewGuid());

        Assert.Equal(withMissingId.Status, withRealId.Status);
        Assert.Equal(withMissingId.Body, withRealId.Body);
    }

    [Fact]
    public async Task VaultBrowse_AnAccountWithoutTheWatchFeature_GetsNotFound_EvenWhenUnlocked()
    {
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: false, features: new HashSet<AccountFeatureId> { AccountFeatureId.Read });
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var routes = await MapRoutesAsync(builder => builder.MapVaultEndpoints());

        var result = await InvokeBrowseAsync(routes["ListVaultScenes"], shape.Account, session);

        Assert.Equal(StatusCodes.Status404NotFound, result.Status);
        Assert.Equal("private, no-store", result.CacheControl);
    }

    [Fact]
    public async Task VaultBrowse_AnUnlockedSession_ListsVaultScenesOnly_PrivatelyAndUncacheably()
    {
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: false);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var routes = await MapRoutesAsync(builder => builder.MapVaultEndpoints());

        var list = await InvokeBrowseAsync(routes["ListVaultScenes"], shape.Account, session);

        Assert.Equal(StatusCodes.Status200OK, list.Status);
        Assert.Equal("private, no-store", list.CacheControl);
        using var json = JsonDocument.Parse(list.Body);
        var ids = json.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal([shape.Secret.AssetId], ids);
        Assert.DoesNotContain(shape.Normal.AssetId, ids);
        Assert.False(json.RootElement.GetProperty("items")[0].GetProperty("matched").GetBoolean());

        var scene = await InvokeBrowseAsync(routes["GetVaultScene"], shape.Account, session, shape.Secret.AssetId);
        Assert.Equal(StatusCodes.Status200OK, scene.Status);
        var ordinary = await InvokeBrowseAsync(routes["GetVaultScene"], shape.Account, session, shape.Normal.AssetId);
        Assert.Equal(StatusCodes.Status404NotFound, ordinary.Status);
        Assert.Equal("private, no-store", ordinary.CacheControl);
    }

    [Fact]
    public async Task VaultBrowse_TheUnmatchedFilter_AndTheNameLists_ComeBackEmptyForAnUnmatchedVault()
    {
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: false);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var routes = await MapRoutesAsync(builder => builder.MapVaultEndpoints());

        var unmatched = await InvokeBrowseAsync(routes["ListVaultScenes"], shape.Account, session, query: "?match=unmatched");
        var matched = await InvokeBrowseAsync(routes["ListVaultScenes"], shape.Account, session, query: "?match=matched");

        Assert.Equal(1, JsonDocument.Parse(unmatched.Body).RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(0, JsonDocument.Parse(matched.Body).RootElement.GetProperty("items").GetArrayLength());
        foreach (var name in new[] { "ListVaultPerformers", "ListVaultStudios", "ListVaultTags" })
        {
            var list = await InvokeBrowseAsync(routes[name], shape.Account, session);
            Assert.Equal("[]", list.Body);
            Assert.Equal("private, no-store", list.CacheControl);
        }
    }

    [Fact]
    public async Task VaultBrowse_Search_NeedsTwoCharacters_AndReadsTheTextFromTheBody()
    {
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: false);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var routes = await MapRoutesAsync(builder => builder.MapVaultEndpoints());

        var tooShort = await InvokeBrowseAsync(routes["SearchVault"], shape.Account, session, body: "{\"text\":\"a\"}");
        var found = await InvokeBrowseAsync(routes["SearchVault"], shape.Account, session, body: "{\"text\":\"secret\"}");
        var none = await InvokeBrowseAsync(routes["SearchVault"], shape.Account, session, body: "{\"text\":\"no such words\"}");

        Assert.Equal(StatusCodes.Status400BadRequest, tooShort.Status);
        Assert.Equal(StatusCodes.Status200OK, found.Status);
        Assert.Equal(1, JsonDocument.Parse(found.Body).RootElement.GetProperty("scenes").GetArrayLength());
        Assert.Equal(0, JsonDocument.Parse(none.Body).RootElement.GetProperty("scenes").GetArrayLength());
        Assert.Equal("private, no-store", found.CacheControl);
    }

    [Fact]
    public async Task VaultBrowse_ASizedArtworkRequest_IsStillNotFoundAndUncacheableForALockedSession()
    {
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: false);
        var routes = await MapRoutesAsync(builder => builder.MapVaultEndpoints());

        var locked = await InvokeBrowseAsync(routes["GetVaultArtwork"], shape.Account, NewVaultSession(shape.Account), artworkId: Guid.NewGuid(), query: "?w=640");

        Assert.Equal(StatusCodes.Status404NotFound, locked.Status);
        Assert.Equal("private, no-store", locked.CacheControl);
    }

    /// <summary>Runs one real browsing route over this test's data store, with the id route values the route may ask for.</summary>
    private async Task<RouteResult> InvokeBrowseAsync(
        RouteEndpoint endpoint, Guid account, Guid? session = null, Guid? assetId = null, Guid? artworkId = null, string? query = null, string? body = null)
    {
        var app = _routeApps[^1];
        await using var scope = app.Services.CreateAsyncScope();
        var context = VaultContext(account, session);
        context.RequestServices = scope.ServiceProvider;
        if (assetId is { } asset)
        {
            context.Request.RouteValues["assetId"] = asset.ToString("D");
        }

        if (artworkId is { } artwork)
        {
            context.Request.RouteValues["artworkId"] = artwork.ToString("D");
        }

        if (query is not null)
        {
            context.Request.QueryString = new QueryString(query);
        }

        context.Response.Body = new MemoryStream();
        if (body is not null)
        {
            context.Request.Method = HttpMethods.Post;
            context.Request.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
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
}
