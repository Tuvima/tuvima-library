using System.Net;
using System.Text;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class EngineApiClientSelectionHistoryTests
{
    [Fact]
    public async Task EmptySuccessfulSelectionHistory_RemainsAnEmptyTimeline()
    {
        var parentId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var handler = new HistoryHandler(HttpStatusCode.OK,
            $$"""{"parent_entity_id":"{{parentId:D}}","selected_asset_ids":["{{assetId:D}}"],"items":[],"has_events":false}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var (history, error) = await client.GetMediaEditorSelectionHistoryAsync(parentId, [assetId]);

        Assert.Null(error);
        Assert.NotNull(history);
        Assert.Empty(history.Items);
        Assert.Equal($"/metadata/{parentId:D}/owned-children/history", handler.Path);
        Assert.Contains(assetId.ToString("D"), handler.RequestBody);
        Assert.Contains("asset_ids", handler.RequestBody);
    }

    [Fact]
    public async Task ServiceFailure_IsNotPresentedAsNoEvents()
    {
        var handler = new HistoryHandler(HttpStatusCode.ServiceUnavailable, "{}");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var (history, error) = await client.GetMediaEditorSelectionHistoryAsync(Guid.NewGuid(), [Guid.NewGuid()]);

        Assert.Null(history);
        Assert.Contains("temporarily unavailable", error);
    }

    private sealed class HistoryHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            RequestBody = request.Content is null ? string.Empty :
                await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
