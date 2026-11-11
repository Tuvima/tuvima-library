using System.Net;
using System.Text;
using System.Text.Json;
using MediaEngine.Contracts.Items;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>What the Dashboard's client sends and reads for deleting one file of an item (universal editor v4).</summary>
public sealed class EngineApiClientLibraryItemFileDeletionTests
{
    [Fact]
    public async Task ThePreview_SendsTheItemAndFile_AndReadsTheFileScope()
    {
        var workId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var handler = new StaticHandler(HttpStatusCode.OK, """
            {"items":[],"files":[],"curated_collections":[],"profiles_with_progress":0,"processing_running":false,"bytes_freed":900,"fingerprint":"fp",
             "file_scope":{"work_id":"11111111-2222-3333-4444-555555555555","asset_id":"22222222-2222-3333-4444-555555555555","file_name":"Show.S01E01.mkv","item_remains":true,"remaining_files":1}}
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var outcome = await client.GetLibraryItemFileDeletionPreviewAsync(workId, assetId);

        Assert.True(outcome.IsOk);
        Assert.True(outcome.Value!.FileScope!.ItemRemains);
        Assert.Equal(1, outcome.Value.FileScope.RemainingFiles);
        Assert.Equal("POST", handler.Method);
        Assert.Equal("/library/items/file-deletion-preview", handler.Path);
        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal(workId, sent.RootElement.GetProperty("work_id").GetGuid());
        Assert.Equal(assetId, sent.RootElement.GetProperty("asset_id").GetGuid());
    }

    [Fact]
    public async Task TheDelete_SendsTheFingerprint_AndReadsWhatWasDeleted()
    {
        var workId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var handler = new StaticHandler(HttpStatusCode.OK, """
            {"deleted_work_ids":[],"titles":["Pilot"],"files_deleted":1,"files_left_on_disk":0,"files_failed":[],"bytes_freed":900,"collections_deleted":[]}
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var outcome = await client.DeleteLibraryItemFileAsync(workId, assetId, "fp");

        Assert.True(outcome.IsOk);
        Assert.Empty(outcome.Value!.DeletedWorkIds);
        Assert.Equal(1, outcome.Value.FilesDeleted);
        Assert.Equal("/library/items/file-delete", handler.Path);
        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal("fp", sent.RootElement.GetProperty("fingerprint").GetString());
        Assert.Equal(assetId, sent.RootElement.GetProperty("asset_id").GetGuid());
    }

    [Fact]
    public async Task AChangedPlan_IsAConflict_CarryingTheFreshPreview()
    {
        var handler = new StaticHandler(HttpStatusCode.Conflict, """
            {"message":"Something changed since you opened this. Here's the updated list.",
             "preview":{"items":[],"files":[],"curated_collections":[],"profiles_with_progress":0,"processing_running":false,"bytes_freed":0,"fingerprint":"fresh"}}
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var outcome = await client.DeleteLibraryItemFileAsync(Guid.NewGuid(), Guid.NewGuid(), "old");

        Assert.Equal(LibraryItemDeletionOutcomeStatus.Conflict, outcome.Status);
        Assert.Equal("fresh", outcome.FreshPreview!.Fingerprint);
    }

    private sealed class StaticHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Method { get; private set; }

        public string? Path { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method.Method;
            Path = request.RequestUri?.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
