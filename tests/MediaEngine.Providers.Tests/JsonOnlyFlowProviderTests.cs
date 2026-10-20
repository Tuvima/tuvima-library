using System.Net;
using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Tests.ProviderRecording;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// A TV guide that nobody has heard of is added by a file alone: its own sign-in and token
/// header, its own endpoints and paging, and fetch and search flows over them.
/// </summary>
public sealed class JsonOnlyFlowProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tuvima-json-flow-test", Guid.NewGuid().ToString("N"));

    private const string File = """
        {
          "name": "episode_guide", "display_name": "Episode Guide", "enabled": true, "weight": 1, "domain": "Video",
          "adapter_type": "config_driven", "requires_api_key": true,
          "endpoints": { "api": "https://api.episode-guide.test/v2" },
          "http_client": { "api_key": "guide-key", "timeout_seconds": 10 },
          "can_handle": { "media_types": ["TV"], "entity_types": ["MediaAsset"] },
          "auth": {
            "login": { "path": "/auth", "body": { "key": "{api_key}" }, "token_path": "token", "lifetime_days": 1, "refresh_early_minutes": 5 },
            "deliver": { "header": "X-Guide-Token", "format": "{token}" }
          },
          "api": {
            "allowed_hosts": ["api.episode-guide.test"], "data_path": "result", "cache_minutes": 10,
            "endpoints": {
              "find": { "path": "/shows?title={title}", "args": { "title": "text" } },
              "episodes": {
                "path": "/shows/{id}/episodes?from={page}", "args": { "id": "digits" },
                "paging": { "start": 1, "max_pages": 5, "items_path": "result.items", "next_path": "result.more" }
              }
            }
          },
          "flows": {
            "fetch": {
              "media_types": ["TV"], "requires_credentials": true,
              "steps": [
                { "set": "season", "from": { "int": "@request.season_number" } },
                { "set": "episode", "from": { "int": "@request.episode_number" } },
                { "stop_if": [ { "blank": "$season" }, { "blank": "$episode" } ] },
                { "call": "find", "args": { "title": "@request.show_name" }, "into": "hits" },
                { "pick": "show", "from": "$hits", "take": 5, "candidate_id": ["id"],
                  "tests": [ { "path": "title", "equals": "@request.show_name", "ignore_case": true } ], "fallback": "only_candidate" },
                { "stop_if": [ { "blank": "$show" } ] },
                { "pages": "episodes", "args": { "id": "$show.id" }, "into": "list" },
                { "find": "match", "in": "$list", "where": [ { "path": "season", "number_equals": "$season" }, { "path": "number", "number_equals": "$episode" } ] },
                { "stop_if": [ { "blank": "$match" } ] },
                { "claims": { "add": "always",
                    "root": { "show": "$show", "match": "$match" },
                    "rules": [
                      { "claim_key": "show_name", "json_path": "show.title", "confidence": 0.9 },
                      { "claim_key": "episode_title", "json_path": "match.name", "confidence": 0.9 },
                      { "kind": "items", "path": "match.guests", "where_not_blank": "name",
                        "each": [ { "claim_key": "cast_member", "json_path": "name", "confidence": 0.7 } ] } ] } }
              ]
            },
            "search": {
              "media_types": ["TV"], "requires_credentials": true,
              "steps": [
                { "call": "find", "args": { "title": "@request.show_name" }, "into": "hits" },
                { "each": "hit", "in": "$hits", "do": [
                  { "emit": { "title": "$hit.title", "id": "$hit.id", "confidence": 0.5, "type": "show", "extra": { "guide_id": "$hit.id" } },
                    "stop_at": "@limit" } ] }
              ]
            }
          }
        }
        """;

    private ConfigDrivenAdapter Adapter(RecordingHandler handler, out ConfigurationDirectoryLoader loader, bool withKey = true)
    {
        Directory.CreateDirectory(Path.Combine(_directory, "providers"));
        System.IO.File.WriteAllText(
            Path.Combine(_directory, "providers", "episode_guide.json"),
            withKey ? File : File.Replace("\"api_key\": \"guide-key\", ", string.Empty, StringComparison.Ordinal));
        loader = new ConfigurationDirectoryLoader(_directory);
        var factory = new RecordingHttpFactory(handler);
        var limiter = new ProviderRateLimiterCoordinator();
        return new ConfigDrivenAdapter(
            loader.LoadProvider("episode_guide")!, factory, NullLogger<ConfigDrivenAdapter>.Instance,
            NullProviderHealthMonitor.Instance, rateLimiter: limiter, apiClient: new ProviderApiClient(loader, factory, limiter));
    }

    private static RecordingHandler Handler(Action<RecordedRequest>? observe = null) => new(request =>
    {
        observe?.Invoke(request);
        var path = request.Uri.AbsolutePath;
        if (path == "/v2/auth")
        {
            return RecordingHandler.Json("""{"token":"t-1"}""");
        }

        if (path == "/v2/shows")
        {
            return RecordingHandler.Json("""{"result":[{"id":7,"title":"Orbit"},{"id":8,"title":"Orbit Kids"}]}""");
        }

        return request.Uri.Query.Contains("from=1", StringComparison.Ordinal)
            ? RecordingHandler.Json("""{"result":{"items":[{"season":1,"number":1,"name":"Pilot"}],"more":"2"}}""")
            : RecordingHandler.Json("""{"result":{"items":[{"season":1,"number":2,"name":"Second","guests":[{"name":"Ada"},{"name":""}]}],"more":null}}""");
    });

    [Fact]
    public async Task FetchFlow_SignsInSendsTheTokenInItsOwnHeaderAndBuildsClaims()
    {
        var tokens = new List<string?>();
        var handler = Handler(request => tokens.Add(request.Uri.AbsolutePath == "/v2/auth" ? null : request.Header("X-Guide-Token")));
        var adapter = Adapter(handler, out _);

        var claims = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.TV,
            ShowName = "Orbit",
            SeasonNumber = "1",
            EpisodeNumber = "2",
        });

        Assert.Equal(
            [
                "POST https://api.episode-guide.test/v2/auth | {\"key\":\"guide-key\"}",
                "GET https://api.episode-guide.test/v2/shows?title=Orbit",
                "GET https://api.episode-guide.test/v2/shows/7/episodes?from=1",
                "GET https://api.episode-guide.test/v2/shows/7/episodes?from=2",
            ],
            handler.Lines);
        Assert.Equal([null, "t-1", "t-1", "t-1"], tokens);
        Assert.Equal(
            ["show_name=Orbit@0.9", "episode_title=Second@0.9", "cast_member=Ada@0.7"],
            RecordingText.Claims(claims).Split('\n'));
    }

    [Fact]
    public async Task FetchFlow_AnswersNothingForAnotherMediaTypeOrAnUnknownEpisode()
    {
        var handler = Handler();
        var adapter = Adapter(handler, out _);

        var movie = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Movies,
            ShowName = "Orbit",
            SeasonNumber = "1",
            EpisodeNumber = "2",
        });
        var missing = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.TV,
            ShowName = "Orbit",
            SeasonNumber = "9",
            EpisodeNumber = "9",
        });

        Assert.Empty(movie);
        Assert.Empty(missing);
    }

    [Fact]
    public async Task SearchFlow_EmitsItemsUpToTheLimit()
    {
        var adapter = Adapter(Handler(), out _);

        var items = await adapter.SearchAsync(
            new ProviderLookupRequest { EntityType = EntityType.MediaAsset, MediaType = MediaType.TV, ShowName = "Orbit" },
            limit: 1);

        var item = Assert.Single(items);
        Assert.Equal(("Orbit", "7", "episode_guide", "show"), (item.Title, item.ProviderItemId, item.ProviderName, item.ResultType));
        Assert.Equal("7", item.ExtraFields!["guide_id"]);
    }

    [Fact]
    public async Task Flows_AnswerNothingWithoutCredentials_AndWhenTheProviderFails()
    {
        var failing = new RecordingHandler(_ => RecordingHandler.Status(HttpStatusCode.InternalServerError));
        var adapter = Adapter(failing, out _);
        var request = new ProviderLookupRequest
        {
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.TV,
            ShowName = "Orbit",
            SeasonNumber = "1",
            EpisodeNumber = "2",
        };

        Assert.Empty(await adapter.FetchAsync(request));

        var quiet = Handler();
        var keyless = Adapter(quiet, out _, withKey: false);
        Assert.Empty(await keyless.FetchAsync(request));
        Assert.Empty(quiet.Lines);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { /* temp folder */ }
    }
}
