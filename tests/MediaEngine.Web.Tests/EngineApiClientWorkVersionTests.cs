using System.Net;
using System.Text;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class EngineApiClientWorkVersionTests
{
    [Fact]
    public async Task ReadsBoundedSelectorAndPreservesDirectAssetLaunchIdentity()
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var handler = new CaptureHandler($$"""
            {"work_id":"{{workId:D}}","work_title":"Owned Book","selected_entity_id":"{{assetId:D}}",
             "selected_entity_type":"Asset","is_truncated":false,"editions":[{"edition_id":"{{editionId:D}}",
             "label":"EPUB","collapse":true,"asset_count":1,"assets":[{"asset_id":"{{assetId:D}}",
             "edition_id":"{{editionId:D}}","file_name":"owned.epub","technical_label":"EPUB"}]}]}
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await client.GetMediaEditorWorkVersionsAsync(assetId);

        Assert.NotNull(result);
        Assert.Equal(workId, result.WorkId);
        Assert.Equal(assetId, result.SelectedEntityId);
        Assert.Equal("Asset", result.SelectedEntityType);
        Assert.True(Assert.Single(result.Editions).Collapse);
        Assert.Equal("EPUB", Assert.Single(result.Editions).Assets.Single().TechnicalLabel);
        Assert.Equal($"/metadata/{assetId:D}/work-versions", handler.Path);
    }

    private sealed class CaptureHandler(string responseBody) : HttpMessageHandler
    {
        public string? Path { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }
}
