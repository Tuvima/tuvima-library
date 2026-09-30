using System.Net;
using System.Text;
using System.Text.Json;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class EngineApiClientTvdbShowOrderTests
{
    [Fact]
    public async Task ShowOrderPreviewAndApplyUseTheScopedEndpointsAndRevision()
    {
        var entityId = Guid.NewGuid();
        var handler = new CapturingResponseHandler(
            """{"seriesId":"show-42","currentSeasonType":"default","requestedSeasonType":"official","currentRevision":"revision-1","availableSeasonTypes":["default","official"],"affectedScopes":[],"canApply":true,"blockingMessage":null}""",
            """{"seasonType":"official","newRevision":"revision-2","message":"The show order was updated."}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var api = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var preview = await api.GetTvdbShowOrderPreviewAsync(entityId, "official");
        var apply = await api.ApplyTvdbShowOrderAsync(entityId, new ApplyTvdbShowOrderDto("official", "revision-1"));

        Assert.NotNull(preview);
        Assert.Equal("official", preview.RequestedSeasonType);
        Assert.True(preview.CanApply);
        Assert.NotNull(apply);
        Assert.Equal("revision-2", apply.NewRevision);
        Assert.Equal($"/metadata/{entityId}/tvdb-match/order-preview?seasonType=official", handler.Requests[0].PathAndQuery);
        Assert.Equal($"/metadata/{entityId}/tvdb-match/order", handler.Requests[1].PathAndQuery);
        using var body = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("official", body.RootElement.GetProperty("seasonType").GetString());
        Assert.Equal("revision-1", body.RootElement.GetProperty("expectedRevision").GetString());
    }

    private sealed class CapturingResponseHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);
        public List<(string PathAndQuery, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.PathAndQuery, body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json"),
            };
        }
    }
}
