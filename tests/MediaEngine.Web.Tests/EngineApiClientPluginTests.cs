using System.Net;
using System.Text;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class EngineApiClientPluginTests
{
    [Fact]
    public async Task GetPluginsAsync_PreservesPermissionsAndToolPlatforms()
    {
        using var http = CreateClient(_ => Json("""
            [{
              "id":"plugin.one",
              "name":"Plugin One",
              "version":"1.2.3",
              "description":"Test",
              "enabled":true,
              "is_built_in":false,
              "load_error":null,
              "capabilities":[{"kind":"segment","name":"Segments","description":"Detects segments"}],
              "permissions":["filesystem.read"],
              "tool_requirements":[{
                "id":"ffmpeg",
                "version":"7",
                "executable_name":"ffmpeg",
                "license":"LGPL",
                "source_url":"https://example.test/ffmpeg",
                "platforms":[{
                  "rid":"win-x64",
                  "download_url":"https://example.test/ffmpeg.zip",
                  "sha256":"abc",
                  "relative_executable_path":"bin/ffmpeg.exe"
                }]
              }],
              "ai_permissions":[],
              "settings":{},
              "settings_schema":null,
              "manifest_path":"plugins/plugin.one/plugin.json"
            }]
            """));
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var plugin = Assert.Single(await client.GetPluginsAsync());

        Assert.Equal("filesystem.read", Assert.Single(plugin.Permissions));
        var platform = Assert.Single(Assert.Single(plugin.ToolRequirements).Platforms);
        Assert.Equal("win-x64", platform.Rid);
        Assert.Equal("abc", platform.Sha256);
        Assert.Equal("plugins/plugin.one/plugin.json", plugin.ManifestPath);
    }

    [Fact]
    public async Task GetPluginJobsAsync_PreservesCompleteDurableOperation()
    {
        using var http = CreateClient(_ => Json("""
            [{
              "id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "operation_type":"PluginPlaybackSegmentDetection",
              "operation_kind":"Plugin",
              "entity_id":null,
              "entity_kind":null,
              "batch_id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
              "source_path":"C:/media",
              "capability_id":"plugin.commercial-skip",
              "capability_version":"2",
              "sub_key":"nightly",
              "plugin_id":"plugin.one",
              "plugin_version":"1.2.3",
              "provider_id":"provider",
              "model_id":"model",
              "status":"Succeeded",
              "stage":"Completed",
              "priority":7,
              "queue_name":"plugin",
              "queue_position":3,
              "attempt_count":2,
              "lease_owner":"worker-1",
              "lease_expires_at":"2026-07-26T12:00:00Z",
              "heartbeat_at":"2026-07-26T11:59:00Z",
              "next_retry_at":null,
              "progress_percent":100,
              "items_total":25,
              "items_completed":24,
              "items_failed":1,
              "result_summary":"Detected 9 segments.",
              "last_error":null,
              "missing_reason":null,
              "created_at":"2026-07-26T11:00:00Z",
              "updated_at":"2026-07-26T12:00:00Z",
              "completed_at":"2026-07-26T12:00:00Z"
            }]
            """));
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var job = Assert.Single(await client.GetPluginJobsAsync("plugin.one"));

        Assert.Equal("PluginPlaybackSegmentDetection", job.OperationType);
        Assert.Equal("plugin.one", job.PluginId);
        Assert.Equal("1.2.3", job.PluginVersion);
        Assert.Equal(7, job.Priority);
        Assert.Equal(3, job.QueuePosition);
        Assert.Equal(24, job.ItemsCompleted);
        Assert.Equal(1, job.ItemsFailed);
        Assert.Equal("Detected 9 segments.", job.ResultSummary);
        Assert.Equal("worker-1", job.LeaseOwner);
    }

    [Fact]
    public async Task RunPluginJobsAsync_PreservesSnapshotCountersAndTimestamps()
    {
        using var http = CreateClient(_ => Json("""
            [{
              "id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "pluginId":"plugin.one",
              "jobType":"playback-segment-detection",
              "status":"completed",
              "startedAt":"2026-07-26T11:00:00Z",
              "completedAt":"2026-07-26T12:00:00Z",
              "assetsScanned":24,
              "segmentsWritten":9,
              "error":null
            }]
            """));
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var job = Assert.Single(await client.RunPluginSegmentDetectionJobsAsync());

        Assert.Equal("plugin.one", job.PluginId);
        Assert.Equal("playback-segment-detection", job.JobType);
        Assert.Equal(24, job.AssetsScanned);
        Assert.Equal(9, job.SegmentsWritten);
        Assert.NotNull(job.CompletedAt);
    }

    [Fact]
    public async Task SetPluginEnabledAsync_WhenTheEngineRefuses_ReturnsTheEnginesPlainReason()
    {
        using var http = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(
                """{"title":"Conflict.","status":409,"detail":"Needs Library 2.0.0 or later; this is 1.0.0.","traceId":"abc"}""",
                Encoding.UTF8,
                "application/problem+json"),
        });
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await client.SetPluginEnabledAsync("plugin.one", enabled: true);

        Assert.False(result.Succeeded);
        Assert.Equal("Needs Library 2.0.0 or later; this is 1.0.0.", result.Message);
    }

    [Fact]
    public async Task SetPluginEnabledAsync_WhenTheEngineFailsWithoutAReason_HasNoMessage()
    {
        using var http = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await client.SetPluginEnabledAsync("plugin.one", enabled: true);

        Assert.False(result.Succeeded);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task SetPluginEnabledAsync_WhenTheEngineAgrees_Succeeds()
    {
        using var http = CreateClient(_ => Json("""{"plugin_id":"plugin.one","enabled":true}"""));
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await client.SetPluginEnabledAsync("plugin.one", enabled: true);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task GetPluginsAsync_PreservesCompatibilityAndTrustDetails()
    {
        using var http = CreateClient(_ => Json("""
            [{
              "id":"plugin.one","name":"Plugin One","version":"1.0.0","description":"Test",
              "enabled":false,"is_built_in":false,"load_error":null,
              "kind":"declarative","trust_level":"private",
              "minimum_library_version":"1.2.0","maximum_library_version":"1.9.0",
              "supported_platforms":["linux"],
              "is_compatible":false,"compatibility_problem":"Not available on win-x64.",
              "capabilities":[],"permissions":[],"tool_requirements":[],"ai_permissions":[],
              "settings":{},"settings_schema":null,"manifest_path":null
            }]
            """));
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var plugin = Assert.Single(await client.GetPluginsAsync());

        Assert.Equal("declarative", plugin.Kind);
        Assert.Equal("private", plugin.TrustLevel);
        Assert.Equal("1.2.0", plugin.MinimumLibraryVersion);
        Assert.Equal("1.9.0", plugin.MaximumLibraryVersion);
        Assert.Equal("linux", Assert.Single(plugin.SupportedPlatforms));
        Assert.False(plugin.IsCompatible);
        Assert.Equal("Not available on win-x64.", plugin.CompatibilityProblem);
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new StubHttpMessageHandler(responder))
        {
            BaseAddress = new Uri("http://localhost:61495/"),
        };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
