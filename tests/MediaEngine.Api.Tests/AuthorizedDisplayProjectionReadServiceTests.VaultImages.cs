using System.Text;
using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Api.Services.Vault;
using MediaEngine.Api.Services.View;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

// The Vault's image routes (Videos spec WP16, D44): the same one gate as every other Vault route. A locked session, an account without the
// Watch feature, and (for the routes that change things) a person who is not an administrator all get the same "not found" a missing page
// gets, privately and uncacheably; an unlocked session is let in and only ever sees Vault images; search words travel in a body.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private static readonly string[] VaultImageRouteNames =
    [
        "ListVaultImages", "SearchVaultImages", "ListVaultImageGalleries", "GetVaultImageGallery", "CreateVaultImageGallery",
        "UpdateVaultImageGallery", "DeleteVaultImageGallery", "AddVaultImagesToGallery", "RemoveVaultImagesFromGallery",
        "ListVaultImageTags", "ListVaultImageTrash", "EmptyVaultImageTrash", "ScanVaultImages", "GetVaultImage",
        "GetVaultImageThumbnail", "GetVaultImagePreview", "SetVaultImageTags", "SetVaultImageFavorite", "DeleteVaultImage", "RestoreVaultImage",
    ];

    private static readonly string[] VaultImageAdministratorRouteNames =
        ["ListVaultImageTrash", "EmptyVaultImageTrash", "ScanVaultImages", "DeleteVaultImage", "RestoreVaultImage"];

    private VaultImageService CreateVaultImageService()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tuvima-vault-image-routes-{Guid.NewGuid():N}");
        var assets = new LocalAssetRepository(_database);
        return new VaultImageService(
            new VaultLibraryRepository(_database), new VaultImageRepository(_database), new VaultGalleryRepository(_database), assets,
            new VaultImageServiceTests.SpyIndexer(assets), new VaultImageServiceTests.LibrariesLoader(Guid.NewGuid().ToString("D"), root),
            new VaultImageServiceTests.FolderResolver(Guid.NewGuid().ToString("D"), root, writable: true), new SourceMutationPolicyGate(),
            new ViewThumbnailService(new AssetPathService(root), NullLogger<ViewThumbnailService>.Instance),
            new RecycleBinService(root, NullLogger.Instance), NullLogger<VaultImageService>.Instance);
    }

    [Fact]
    public async Task VaultImages_EveryRoute_IsMapped_ByName()
    {
        await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var routes = await MapRoutesAsync(builder => builder.MapVaultEndpoints());

        Assert.All(VaultImageRouteNames, name => Assert.True(routes.ContainsKey(name), name));
    }

    [Theory]
    [InlineData("ListVaultImages")]
    [InlineData("SearchVaultImages")]
    [InlineData("ListVaultImageGalleries")]
    [InlineData("GetVaultImageGallery")]
    [InlineData("CreateVaultImageGallery")]
    [InlineData("UpdateVaultImageGallery")]
    [InlineData("DeleteVaultImageGallery")]
    [InlineData("AddVaultImagesToGallery")]
    [InlineData("RemoveVaultImagesFromGallery")]
    [InlineData("ListVaultImageTags")]
    [InlineData("ListVaultImageTrash")]
    [InlineData("EmptyVaultImageTrash")]
    [InlineData("ScanVaultImages")]
    [InlineData("GetVaultImage")]
    [InlineData("GetVaultImageThumbnail")]
    [InlineData("GetVaultImagePreview")]
    [InlineData("SetVaultImageTags")]
    [InlineData("SetVaultImageFavorite")]
    [InlineData("DeleteVaultImage")]
    [InlineData("RestoreVaultImage")]
    public async Task VaultImages_ALockedSession_GetsNotFound_PrivatelyAndUncacheably_ForEveryRoute(string routeName)
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: true);
        var routes = await MapRoutesAsync(builder => builder.MapVaultEndpoints());

        var result = await InvokeImageAsync(routes[routeName], shape.Account, NewVaultSession(shape.Account), body: "{\"text\":\"secret\"}");

        Assert.Equal(StatusCodes.Status404NotFound, result.Status);
        Assert.Equal("private, no-store", result.CacheControl);
    }

    [Fact]
    public async Task VaultImages_AnAccountWithoutTheWatchFeature_GetsNotFound_EvenWhenUnlocked()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false, features: new HashSet<AccountFeatureId> { AccountFeatureId.Read });
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var routes = await MapRoutesAsync(builder => builder.MapVaultEndpoints());

        var result = await InvokeImageAsync(routes["ListVaultImages"], shape.Account, session);

        Assert.Equal(StatusCodes.Status404NotFound, result.Status);
        Assert.Equal("private, no-store", result.CacheControl);
    }

    [Fact]
    public async Task VaultImages_AnUnlockedSession_ListsPrivately_AndAnUnknownImageIsTheSameNotFoundAsALockedOne()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var routes = await MapRoutesAsync(builder => builder.MapVaultEndpoints());

        var list = await InvokeImageAsync(routes["ListVaultImages"], shape.Account, session);
        var galleries = await InvokeImageAsync(routes["ListVaultImageGalleries"], shape.Account, session);
        var unknown = await InvokeImageAsync(routes["GetVaultImage"], shape.Account, session, new() { ["imageId"] = Guid.NewGuid().ToString("D") });
        var thumbnail = await InvokeImageAsync(routes["GetVaultImageThumbnail"], shape.Account, session, new() { ["imageId"] = Guid.NewGuid().ToString("D") }, query: "?w=320");

        Assert.Equal(StatusCodes.Status200OK, list.Status);
        Assert.Equal("private, no-store", list.CacheControl);
        Assert.Equal(0, JsonDocument.Parse(list.Body).RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(StatusCodes.Status200OK, galleries.Status);
        Assert.Equal("[]", galleries.Body);
        Assert.Equal(StatusCodes.Status404NotFound, unknown.Status);
        Assert.Equal("private, no-store", unknown.CacheControl);
        Assert.Equal(StatusCodes.Status404NotFound, thumbnail.Status);
        Assert.Equal("private, no-store", thumbnail.CacheControl);
    }

    [Theory]
    [InlineData("ListVaultImageTrash")]
    [InlineData("EmptyVaultImageTrash")]
    [InlineData("ScanVaultImages")]
    [InlineData("DeleteVaultImage")]
    [InlineData("RestoreVaultImage")]
    public async Task VaultImages_FindingDeletingAndTheBin_AreForAnAdministrator_ANonAdministratorGetsNotFound(string routeName)
    {
        var person = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var personSession = NewVaultSession(person.Account);
        await UnlockVaultAsync(personSession);
        var routes = await MapRoutesAsync(builder => builder.MapVaultEndpoints());

        var refused = await InvokeImageAsync(routes[routeName], person.Account, personSession, new() { ["imageId"] = Guid.NewGuid().ToString("D") });

        Assert.Equal(StatusCodes.Status404NotFound, refused.Status);
        Assert.Equal("private, no-store", refused.CacheControl);
        Assert.Contains(routeName, VaultImageAdministratorRouteNames);
    }

    [Fact]
    public async Task VaultImages_AnAdministratorInPerson_CanScan_AndTheAnswerIsCountsOnly()
    {
        var admin = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: true);
        var session = NewVaultSession(admin.Account);
        await UnlockVaultAsync(session);
        var routes = await MapRoutesAsync(builder => builder.MapVaultEndpoints());

        var scan = await InvokeImageAsync(routes["ScanVaultImages"], admin.Account, session);

        Assert.Equal(StatusCodes.Status200OK, scan.Status);
        Assert.Equal("private, no-store", scan.CacheControl);
        var counts = JsonDocument.Parse(scan.Body).RootElement;
        Assert.Equal(0, counts.GetProperty("images_found").GetInt32());
        Assert.Equal(["images_found", "images_added", "errors"], counts.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task VaultImages_Search_NeedsTwoCharacters_AndTheWordsComeFromTheBodyNotTheAddress()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var routes = await MapRoutesAsync(builder => builder.MapVaultEndpoints());

        var tooShort = await InvokeImageAsync(routes["SearchVaultImages"], shape.Account, session, body: "{\"text\":\"a\"}");
        var found = await InvokeImageAsync(routes["SearchVaultImages"], shape.Account, session, body: "{\"text\":\"holiday\"}");

        Assert.Equal(StatusCodes.Status400BadRequest, tooShort.Status);
        Assert.Equal(StatusCodes.Status200OK, found.Status);
        Assert.Equal("private, no-store", found.CacheControl);
        var endpoint = (RouteEndpoint)routes["SearchVaultImages"];
        Assert.Equal("/api/v1/vault/images/search", endpoint.RoutePattern.RawText);
        Assert.Contains(endpoint.Metadata.OfType<HttpMethodMetadata>().Single().HttpMethods, method => method == HttpMethods.Post);
    }

    private async Task<RouteResult> InvokeImageAsync(
        RouteEndpoint endpoint, Guid account, Guid? session = null, Dictionary<string, string>? routeValues = null, string? query = null, string? body = null)
    {
        var app = _routeApps[^1];
        await using var scope = app.Services.CreateAsyncScope();
        var context = VaultContext(account, session);
        context.RequestServices = scope.ServiceProvider;
        foreach (var (key, value) in routeValues ?? [])
        {
            context.Request.RouteValues[key] = value;
        }

        // Routes that need an id but are called without one still reach the gate first; give them one so binding never decides the answer.
        foreach (var name in new[] { "imageId", "galleryId" })
        {
            context.Request.RouteValues.TryAdd(name, Guid.NewGuid().ToString("D"));
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
