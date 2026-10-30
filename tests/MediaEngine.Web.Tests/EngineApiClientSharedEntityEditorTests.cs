using System.Net;
using System.Text;
using MediaEngine.Contracts.Universe;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class EngineApiClientSharedEntityEditorTests
{
    [Fact]
    public async Task SharedEntityContext_RecordsHttpNotFoundFailureSemantics()
    {
        using var http = new HttpClient(new StatusHandler(HttpStatusCode.NotFound)) { BaseAddress = new Uri("http://engine.test/") };
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);
        var target = new SharedEntityEditorTargetDto(SharedEntityEditorTargetKinds.Universe, "Q42", null, "Q42");

        var context = await client.GetSharedEntityEditorContextAsync(target);

        Assert.Null(context);
        Assert.Equal("not_found", client.LastFailureKind);
        Assert.Equal(404, client.LastStatusCode);
        Assert.Equal("/entity-editor/universes/Q42/context", client.LastFailedEndpoint);
        Assert.Contains("HTTP 404", client.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedEntityMethods_UseAuthorizedEntityEditorRoutesAndMultipartUpload()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test/") };
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);
        var root = new SharedEntityEditorTargetDto(SharedEntityEditorTargetKinds.Universe, "Q42", null, "Q42");
        var entity = new SharedEntityEditorTargetDto(SharedEntityEditorTargetKinds.FictionalEntity, "Q42", Guid.Parse("11111111-2222-3333-4444-555555555555"), "Q123");

        await client.GetSharedEntityEditorContextAsync(root);
        await client.GetSharedEntityCategoriesAsync("Q42");
        await client.GetSharedEntitySelectorPageAsync("Q42", "Character", "Bat Man", 20, 25);
        await client.GetSharedEntityDetailsAsync(root);
        await client.UpdateSharedEntityDetailsAsync(root, new("Universe", "description"));
        await client.GetSharedEntityArtworkAsync(root);
        await client.UpdateSharedEntityArtworkAsync(root, new("CoverArt", "https://cdn.test/art.jpg"));
        await client.UploadSharedEntityArtworkAsync(root, "CoverArt", new MemoryStream([1, 2, 3]), "art.jpg", "image/jpeg");
        await client.GetSharedEntityRelationshipsAsync(root);
        await client.GetSharedEntityTimelineAsync(root);
        await client.GetSharedEntitySourcesAsync(root);
        await client.GetSharedEntityHistoryAsync(root);
        await client.GetSharedEntityEnrichmentAsync(root);
        await client.RefreshSharedEntityAsync(root);

        await client.GetSharedEntityEditorContextAsync(entity);
        await client.GetSharedEntityDetailsAsync(entity);
        await client.UpdateSharedEntityDetailsAsync(entity, new("Entity", null));
        await client.GetSharedEntityArtworkAsync(entity);
        await client.UpdateSharedEntityArtworkAsync(entity, new("Logo", "https://cdn.test/logo.png"));
        await client.UploadSharedEntityArtworkAsync(entity, "Logo", new MemoryStream([4, 5]), "logo.png", "image/png");
        await client.GetSharedEntityAppearancesAsync(entity);
        await client.GetSharedEntityRelationshipsAsync(entity);
        await client.GetSharedEntityTimelineAsync(entity);
        await client.GetSharedEntitySourcesAsync(entity);
        await client.GetSharedEntityHistoryAsync(entity);
        await client.GetSharedEntityEnrichmentAsync(entity);
        await client.RefreshSharedEntityAsync(entity);

        Assert.Equal(
        [
            "GET /entity-editor/universes/Q42/context",
            "GET /entity-editor/universes/Q42/categories",
            "GET /entity-editor/universes/Q42/entities?offset=20&limit=25&category=Character&search=Bat%20Man",
            "GET /entity-editor/universes/Q42/details",
            "PUT /entity-editor/universes/Q42/details",
            "GET /entity-editor/universes/Q42/artwork",
            "PUT /entity-editor/universes/Q42/artwork",
            "POST /entity-editor/universes/Q42/artwork/CoverArt/upload",
            "GET /entity-editor/universes/Q42/relationships",
            "GET /entity-editor/universes/Q42/timeline",
            "GET /entity-editor/universes/Q42/sources",
            "GET /entity-editor/universes/Q42/history",
            "GET /entity-editor/universes/Q42/enrichment",
            "POST /entity-editor/universes/Q42/refresh",
            "GET /entity-editor/universes/Q42/entities/11111111-2222-3333-4444-555555555555/context",
            "GET /entity-editor/universes/Q42/entities/11111111-2222-3333-4444-555555555555/details",
            "PUT /entity-editor/universes/Q42/entities/11111111-2222-3333-4444-555555555555/details",
            "GET /entity-editor/universes/Q42/entities/11111111-2222-3333-4444-555555555555/artwork",
            "PUT /entity-editor/universes/Q42/entities/11111111-2222-3333-4444-555555555555/artwork",
            "POST /entity-editor/universes/Q42/entities/11111111-2222-3333-4444-555555555555/artwork/Logo/upload",
            "GET /entity-editor/universes/Q42/entities/11111111-2222-3333-4444-555555555555/appearances",
            "GET /entity-editor/universes/Q42/entities/11111111-2222-3333-4444-555555555555/relationships",
            "GET /entity-editor/universes/Q42/entities/11111111-2222-3333-4444-555555555555/timeline",
            "GET /entity-editor/universes/Q42/entities/11111111-2222-3333-4444-555555555555/sources",
            "GET /entity-editor/universes/Q42/entities/11111111-2222-3333-4444-555555555555/history",
            "GET /entity-editor/universes/Q42/entities/11111111-2222-3333-4444-555555555555/enrichment",
            "POST /entity-editor/universes/Q42/entities/11111111-2222-3333-4444-555555555555/refresh",
        ], handler.Requests.Select(request => request.MethodAndPath));
        Assert.Equal(2, handler.MultipartUploads);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string MethodAndPath, string ContentType)> Requests { get; } = [];
        public int MultipartUploads => Requests.Count(request => request.ContentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add(($"{request.Method.Method.ToUpperInvariant()} {path}", request.Content?.Headers.ContentType?.ToString() ?? string.Empty));
            if (request.Content is MultipartFormDataContent multipart)
            {
                var filePart = Assert.Single(multipart);
                Assert.Equal("file", filePart.Headers.ContentDisposition?.Name?.Trim('"'));
                Assert.Contains(filePart.Headers.ContentType?.MediaType, new[] { "image/jpeg", "image/png" });
            }

            var body = path.EndsWith("/entities?offset=20&limit=25&category=Character&search=Bat%20Man", StringComparison.Ordinal)
                ? "{\"items\":[],\"offset\":20,\"limit\":25,\"total\":0,\"has_more\":false}"
                : path.Contains("/artwork", StringComparison.Ordinal) || path.Contains("/relationships", StringComparison.Ordinal)
                    || path.Contains("/timeline", StringComparison.Ordinal) || path.Contains("/sources", StringComparison.Ordinal)
                    || path.Contains("/history", StringComparison.Ordinal) || path.EndsWith("/categories", StringComparison.Ordinal)
                    ? "[]"
                    : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("{\"title\":\"Not found\",\"detail\":\"Universe context is unavailable.\"}", Encoding.UTF8, "application/problem+json"),
            });
    }
}
