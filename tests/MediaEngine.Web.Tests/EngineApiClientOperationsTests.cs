using System.Net;
using System.Text;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class EngineApiClientOperationsTests
{
    [Fact]
    public async Task OperationsClientMethods_CallDurableOperationEndpoints()
    {
        var requests = new List<HttpRequestMessage>();
        using var httpClient = CreateHttpClient(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.PathAndQuery;
            var json = path switch
            {
                "/operations?limit=100" => """
                    [
                      {
                        "id": "11111111-1111-1111-1111-111111111111",
                        "operation_type": "identity.wikidata_bridge",
                        "operation_kind": "identity",
                        "status": "running",
                        "stage": "entity_fetch",
                        "queue_name": "identity",
                        "priority": 100,
                        "attempt_count": 1,
                        "progress_percent": 42,
                        "items_total": 10,
                        "items_completed": 4,
                        "items_failed": 0,
                        "created_at": "2026-05-24T12:00:00Z",
                        "updated_at": "2026-05-24T12:01:00Z"
                      }
                    ]
                    """,
                "/operations/summary" => """{"queued":3,"running":1}""",
                "/operations/11111111-1111-1111-1111-111111111111" => """
                    {
                      "operation": {
                        "id": "11111111-1111-1111-1111-111111111111",
                        "operation_type": "identity.wikidata_bridge",
                        "operation_kind": "identity",
                        "status": "running",
                        "queue_name": "identity",
                        "priority": 100,
                        "attempt_count": 1,
                        "progress_percent": 42,
                        "items_total": 10,
                        "items_completed": 4,
                        "items_failed": 0,
                        "created_at": "2026-05-24T12:00:00Z",
                        "updated_at": "2026-05-24T12:01:00Z"
                      },
                      "events": [
                        {
                          "id": "22222222-2222-2222-2222-222222222222",
                          "operation_id": "11111111-1111-1111-1111-111111111111",
                          "event_type": "stage_changed",
                          "new_stage": "entity_fetch",
                          "occurred_at": "2026-05-24T12:01:00Z"
                        }
                      ]
                    }
                    """,
                "/assets/33333333-3333-3333-3333-333333333333/capabilities" => """
                    [
                      {
                        "id": "44444444-4444-4444-4444-444444444444",
                        "entity_id": "33333333-3333-3333-3333-333333333333",
                        "entity_kind": "asset",
                        "capability_id": "text_track.lyrics",
                        "capability_kind": "text_track",
                        "status": "no_result",
                        "requiredness": "optional",
                        "artifact_count": 0,
                        "stale": false,
                        "needs_rerun": false,
                        "created_at": "2026-05-24T12:00:00Z",
                        "updated_at": "2026-05-24T12:01:00Z"
                      }
                    ]
                    """,
                "/capabilities/summary" => """{"text_track.lyrics:no_result":1}""",
                "/ingestion/media-groups/55555555-5555-5555-5555-555555555555" => """
                    {
                      "group_id": "55555555-5555-5555-5555-555555555555",
                      "title": "Dune"
                    }
                    """,
                "/ingestion/media-groups/55555555-5555-5555-5555-555555555555/children?offset=0&limit=50" => """
                    {
                      "items": [],
                      "offset": 0,
                      "limit": 50,
                      "has_more": false,
                      "total_count": null,
                      "next_cursor": null
                    }
                    """,
                _ when request.Method == HttpMethod.Post => "{}",
                _ => "{}",
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        var client = new EngineApiClient(httpClient, NullLogger<EngineApiClient>.Instance);
        var operationId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var operations = await client.GetMediaOperationsAsync(limit: 100);
        var summary = await client.GetMediaOperationsSummaryAsync();
        var detail = await client.GetMediaOperationAsync(operationId);
        var capabilities = await client.GetAssetCapabilitiesAsync(Guid.Parse("33333333-3333-3333-3333-333333333333"));
        var capabilitySummary = await client.GetCapabilitySummaryAsync();
        var groupId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var group = await client.GetIngestionMediaGroupAsync(groupId);
        var children = await client.GetIngestionMediaChildrenAsync(groupId);
        var retry = await client.RetryMediaOperationAsync(operationId);
        var cancel = await client.CancelMediaOperationAsync(operationId);

        Assert.Single(operations);
        Assert.Equal("running", operations[0].Status);
        Assert.Equal(3, summary["queued"]);
        Assert.NotNull(detail);
        Assert.Single(detail!.Events);
        Assert.Single(capabilities);
        Assert.Equal("no_result", capabilities[0].Status);
        Assert.Equal(1, capabilitySummary["text_track.lyrics:no_result"]);
        Assert.Equal(groupId, group?.GroupId);
        Assert.NotNull(children);
        Assert.True(retry);
        Assert.True(cancel);
        Assert.Contains(requests, request => request.RequestUri!.PathAndQuery == "/operations/11111111-1111-1111-1111-111111111111/retry");
        Assert.Contains(requests, request => request.RequestUri!.PathAndQuery == "/operations/11111111-1111-1111-1111-111111111111/cancel");
    }

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new StubHttpMessageHandler(responder))
        {
            BaseAddress = new Uri("http://localhost:61495/"),
        };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
