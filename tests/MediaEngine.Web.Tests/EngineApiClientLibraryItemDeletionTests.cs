using System.Net;
using System.Text;
using System.Text.Json;
using MediaEngine.Contracts.Items;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>What the Dashboard's client makes of each answer to POST /library/items/delete.</summary>
public sealed class EngineApiClientLibraryItemDeletionTests
{
    [Fact]
    public async Task ASuccessfulDelete_ReturnsWhatWasDeleted_AndSendsTheWorkIdsAndFingerprint()
    {
        var workId = Guid.NewGuid();
        var handler = new StaticHandler(HttpStatusCode.OK, """
            {"deleted_work_ids":["11111111-2222-3333-4444-555555555555"],"titles":["Dune"],"files_deleted":2,"files_left_on_disk":1,
             "files_failed":[{"file_name":"Dune.mkv","folder_label":"Movies","reason":"The file is in use"}],"bytes_freed":4096,
             "collections_deleted":[{"collection_id":"22222222-2222-3333-4444-555555555555","name":"Road Trip"}]}
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var outcome = await client.DeleteLibraryItemsAsync([workId], "fingerprint-1");

        Assert.True(outcome.IsOk);
        Assert.Equal(["Dune"], outcome.Value!.Titles);
        Assert.Equal(2, outcome.Value.FilesDeleted);
        Assert.Equal(1, outcome.Value.FilesLeftOnDisk);
        Assert.Equal(4096, outcome.Value.BytesFreed);
        Assert.Equal("The file is in use", Assert.Single(outcome.Value.FilesFailed).Reason);
        Assert.Equal("Road Trip", Assert.Single(outcome.Value.CollectionsDeleted).Name);
        Assert.Equal("POST", handler.Method);
        Assert.Equal("/library/items/delete", handler.Path);
        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal(workId, sent.RootElement.GetProperty("work_ids")[0].GetGuid());
        Assert.Equal("fingerprint-1", sent.RootElement.GetProperty("fingerprint").GetString());
    }

    [Fact]
    public async Task AChangedPlan_IsAConflict_CarryingTheFreshPreview()
    {
        var handler = new StaticHandler(HttpStatusCode.Conflict, """
            {"message":"Something changed since you opened this. Here's the updated list.",
             "preview":{"items":[],"files":[],"curated_collections":[],"profiles_with_progress":3,"processing_running":false,"bytes_freed":0,"fingerprint":"fresh"}}
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var outcome = await client.DeleteLibraryItemsAsync([Guid.NewGuid()], "old");

        Assert.False(outcome.IsOk);
        Assert.Equal(LibraryItemDeletionOutcomeStatus.Conflict, outcome.Status);
        Assert.Equal("Something changed since you opened this. Here's the updated list.", outcome.Message);
        Assert.Equal("fresh", outcome.FreshPreview!.Fingerprint);
        Assert.Equal(3, outcome.FreshPreview.ProfilesWithProgress);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, LibraryItemDeletionOutcomeStatus.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, LibraryItemDeletionOutcomeStatus.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, LibraryItemDeletionOutcomeStatus.Invalid)]
    [InlineData(HttpStatusCode.InternalServerError, LibraryItemDeletionOutcomeStatus.Unavailable)]
    public async Task EachRefusal_IsItsOwnOutcome(HttpStatusCode status, LibraryItemDeletionOutcomeStatus expected)
    {
        var handler = new StaticHandler(status, """{"detail":"A plain sentence."}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var outcome = await client.DeleteLibraryItemsAsync([Guid.NewGuid()], "x");

        Assert.False(outcome.IsOk);
        Assert.Equal(expected, outcome.Status);
        Assert.Null(outcome.FreshPreview);
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
