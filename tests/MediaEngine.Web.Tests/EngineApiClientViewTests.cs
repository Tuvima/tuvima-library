using System.Net;
using System.Text;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class EngineApiClientViewTests
{
    [Fact]
    public async Task UploadViewMediaAsync_PostsOnlyMultipartFileToCleanRoute()
    {
        var itemId = Guid.NewGuid(); string? body = null; string? path = null;
        using var http = Http(request => { path = request.RequestUri!.PathAndQuery; body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult(); return Json(HttpStatusCode.OK, $$"""{"item_id":"{{itemId}}","item_added":true,"files_added":1,"sources_added":1}"""); });
        var client = Client(http); await using var stream = new MemoryStream([1, 2, 3]);
        var result = await client.UploadViewMediaAsync(stream, "photo.jpg", "image/jpeg");
        Assert.True(result.Success); Assert.Equal(itemId, result.Upload?.ItemId); Assert.Equal("/view/uploads", path);
        Assert.Contains("name=file; filename=photo.jpg", body, StringComparison.Ordinal);
        Assert.DoesNotContain("library", body, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("profile", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UploadViewMediaAsync_ReturnsServerPolicyDetailOnBadRequest()
    {
        using var http = Http(_ => Json(HttpStatusCode.BadRequest, "{\"title\":\"Bad Request\",\"detail\":\"Browser upload is disabled.\"}", "application/problem+json"));
        await using var stream = new MemoryStream([1]);
        var result = await Client(http).UploadViewMediaAsync(stream, "photo.jpg", "image/jpeg");
        Assert.False(result.Success); Assert.Equal("Browser upload is disabled.", result.ErrorMessage);
    }

    [Fact]
    public async Task ViewClient_UsesTrustedCleanScopeAssetDiscoveryAndMutationRoutes()
    {
        var itemId = Guid.NewGuid(); var requests = new List<(HttpMethod Method, string Path)>();
        using var http = Http(request =>
        {
            requests.Add((request.Method, request.RequestUri!.PathAndQuery));
            var json = request.RequestUri.AbsolutePath switch
            {
                "/view/scopes" => "{\"scope\":{\"kind\":0,\"profile_id\":null,\"was_fallback\":false},\"available_scopes\":[]}",
                "/view/assets" => "{\"items\":[],\"next_cursor\":null,\"has_more\":false}",
                "/view/people" => "{\"items\":[],\"next_cursor\":null,\"has_more\":false,\"capability\":{\"state\":\"empty\",\"has_indexed_data\":false,\"automatic_processing_available\":false,\"message\":\"None\",\"evidence_kinds\":[]}}",
                _ => "",
            };
            return Json(HttpStatusCode.OK, json);
        });
        var client = Client(http);
        await client.GetViewScopesAsync(ViewScopeKind.Shared);
        await client.GetViewAssetsAsync(new(ViewScopeKind.Shared, Search: "summer trip", Kinds: ["video"], FavoritesOnly: true));
        await client.GetViewPeopleAsync(new(ViewScopeKind.Mine, Search: "Sarah"));
        await client.SetViewItemFavoriteAsync(itemId, true); await client.ArchiveViewItemAsync(itemId);
        Assert.Contains(requests, r => r.Path.StartsWith("/view/scopes?", StringComparison.Ordinal));
        Assert.Contains(requests, r => r.Path.Contains("/view/assets?scope=shared", StringComparison.Ordinal) && r.Path.Contains("q=summer%20trip", StringComparison.Ordinal) && r.Path.Contains("kind=video", StringComparison.Ordinal));
        Assert.Contains(requests, r => r.Path.Contains("/view/people?scope=mine", StringComparison.Ordinal));
        Assert.Contains(requests, r => r == (HttpMethod.Put, $"/view/items/{itemId:D}/favorite")); Assert.Contains(requests, r => r == (HttpMethod.Post, $"/view/items/{itemId:D}/archive"));
        Assert.DoesNotContain(requests, r => r.Path.Contains("profileId=", StringComparison.OrdinalIgnoreCase) || r.Path.Contains("/view/libraries", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OtherPeople_UsesReadOnlyScopesAndTheDedicatedRoutes()
    {
        var personId = Guid.NewGuid(); var requests = new List<(HttpMethod Method, string Path)>();
        using var http = Http(request =>
        {
            requests.Add((request.Method, request.RequestUri!.PathAndQuery));
            var json = request.RequestUri.AbsolutePath switch
            {
                "/view/other-people" => "{\"households\":[]}",
                "/view/photo-views" => "{\"page\":{\"items\":[],\"offset\":0,\"limit\":25,\"has_more\":false},\"household_wide\":true}",
                "/view/assets" => "{\"items\":[],\"next_cursor\":null,\"has_more\":false}",
                _ => "",
            };
            return Json(HttpStatusCode.OK, json);
        });
        var client = Client(http);

        await client.GetViewAssetsAsync(new(ViewScopeKind.OtherShared, personId));
        Assert.NotNull(await client.GetViewOtherPeopleAsync());
        var views = await client.GetViewPhotoViewsAsync(25, 25);

        Assert.True(views!.HouseholdWide);
        Assert.Contains(requests, r => r.Path.Contains("scope=othershared", StringComparison.Ordinal)
            && r.Path.Contains($"scopeProfileId={personId:D}", StringComparison.Ordinal));
        Assert.Contains(requests, r => r.Method == HttpMethod.Get && r.Path == "/view/other-people");
        Assert.Contains(requests, r => r.Path == "/view/photo-views?offset=25&limit=25");
    }

    [Fact]
    public async Task MineScope_NeverSerializesResolvedOwnerAsProfileScopeParameter()
    {
        var ownerId = Guid.NewGuid();
        var requests = new List<(string Path, string? Body)>();
        using var http = Http(request =>
        {
            requests.Add((request.RequestUri!.PathAndQuery,
                request.Content?.ReadAsStringAsync().GetAwaiter().GetResult()));
            var json = request.Method == HttpMethod.Put
                ? $$"""{"profile_id":"{{ownerId}}","scope":1,"scope_profile_id":"{{ownerId}}","timeline_density":0,"updated_at":null}"""
                : "{\"items\":[],\"next_cursor\":null,\"has_more\":false,\"capability\":{\"state\":\"empty\",\"has_indexed_data\":false,\"automatic_processing_available\":false,\"message\":\"None\",\"evidence_kinds\":[]}}";
            return Json(HttpStatusCode.OK, json);
        });
        var client = Client(http);

        await client.GetViewPlacesAsync(new(ViewScopeKind.Mine, ownerId));
        await client.GetViewAssetsAsync(new(ViewScopeKind.Mine, ownerId));
        await client.UpdateViewPreferencesAsync(
            ViewScopeKind.Mine, ownerId, ViewTimelineDensity.Compact);

        Assert.All(requests, request =>
            Assert.DoesNotContain("scopeProfileId", request.Path, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(requests.Where(request => request.Body is not null), request =>
            request.Body!.Contains("scope_profile_id\":\"", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ItemDetailUpdates_UseOwnedItemRoutesAndReturnRefreshedContract()
    {
        var itemId = Guid.NewGuid();
        var requests = new List<(HttpMethod Method, string Path, string? Body)>();
        using var http = Http(request =>
        {
            requests.Add((request.Method, request.RequestUri!.PathAndQuery,
                request.Content?.ReadAsStringAsync().GetAwaiter().GetResult()));
            return Json(HttpStatusCode.OK, $$"""{"id":"{{itemId}}","library_id":"{{Guid.NewGuid()}}","media_kind":"image","file_name":"photo.jpg","mime_type":"image/jpeg","created_at":"2026-09-22T00:00:00Z","favorite":false,"hidden":false,"source_count":1,"files":[],"tags":[],"thumbnail_url":"/thumbnail","content_url":"/content"}""");
        });
        var client = Client(http);

        await client.UpdateViewItemDescriptionAsync(itemId, "A lake at sunrise");
        await client.UpdateViewItemTagsAsync(itemId, ["lake", "sunrise"]);
        await client.UpdateViewItemLocationAsync(itemId, new(41.1, -87.2, "Lakefront", "Chicago"));

        Assert.Contains(requests, request => request.Method == HttpMethod.Put && request.Path == $"/view/items/{itemId:D}/description" && request.Body!.Contains("lake at sunrise", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(requests, request => request.Method == HttpMethod.Put && request.Path == $"/view/items/{itemId:D}/tags" && request.Body!.Contains("sunrise", StringComparison.Ordinal));
        Assert.Contains(requests, request => request.Method == HttpMethod.Put && request.Path == $"/view/items/{itemId:D}/location" && request.Body!.Contains("Chicago", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GallerySharingClient_UsesCountFreeDiscoveryAndExactReplacementRoutes()
    {
        var galleryId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var requests = new List<(HttpMethod Method, string Path, string? Body)>();
        using var http = Http(request =>
        {
            requests.Add((
                request.Method,
                request.RequestUri!.PathAndQuery,
                request.Content?.ReadAsStringAsync().GetAwaiter().GetResult()));
            if (request.Method == HttpMethod.Put)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            var json = request.RequestUri.AbsolutePath == "/view/share-targets"
                ? $$"""[{"profile_id":"{{targetId}}","display_name":"Sarah","avatar_color":"#7457D9","avatar_url":null}]"""
                : $$"""[{"gallery_id":"{{galleryId}}","profile_id":"{{targetId}}","permission":0,"shared_at":"2026-08-23T12:00:00Z"}]""";
            return Json(HttpStatusCode.OK, json);
        });
        var client = Client(http);

        var targets = await client.GetViewGalleryShareTargetsAsync();
        var shares = await client.GetViewGallerySharesAsync(galleryId);
        var replaced = await client.ReplaceViewGallerySharesAsync(
            galleryId, [new(targetId, ViewGallerySharePermission.Contribute)]);

        Assert.Equal(targetId, Assert.Single(targets!).ProfileId);
        Assert.Equal(targetId, Assert.Single(shares!).ProfileId);
        Assert.True(replaced);
        Assert.Contains(requests, request => request == (HttpMethod.Get, "/view/share-targets", null));
        Assert.Contains(requests, request => request.Method == HttpMethod.Get
            && request.Path == $"/view/galleries/{galleryId:D}/shares");
        var put = Assert.Single(requests, request => request.Method == HttpMethod.Put);
        Assert.Equal($"/view/galleries/{galleryId:D}/shares", put.Path);
        Assert.Contains($"\"profile_id\":\"{targetId:D}\"", put.Body, StringComparison.Ordinal);
        Assert.Contains("\"permission\":1", put.Body, StringComparison.Ordinal);
        Assert.All(requests, request => Assert.DoesNotContain("profileId=", request.Path, StringComparison.OrdinalIgnoreCase));
    }

    private static EngineApiClient Client(HttpClient http) => new(http, NullLogger<EngineApiClient>.Instance);
    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new Stub(respond)) { BaseAddress = new Uri("http://localhost:61495/") };
    private static HttpResponseMessage Json(HttpStatusCode status, string json, string media = "application/json") => new(status) { Content = new StringContent(json, Encoding.UTF8, media) };
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request)); }
}
