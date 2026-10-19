using System.Net;
using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Tests.ProviderRecording;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// A provider with a made-up name gets follow-up lookups from its file alone; nothing in the
/// adapter knows the provider. Covers add modes, caches, skips and error policies.
/// </summary>
public sealed class FollowUpLookupTests
{
    private const string SearchResponse = """{ "results": [ { "id": 7, "name": "Widget", "info": { "maker": "Acme" } } ] }""";

    private static ProviderConfiguration Provider(string followUps, string? cacheTtl = null) =>
        JsonSerializer.Deserialize<ProviderConfiguration>($$"""
            {
              "name": "sample_catalog", "enabled": true, "weight": 1, "domain": "Video",
              "endpoints": { "api": "https://api.sample.test/v1" },
              {{cacheTtl ?? string.Empty}}
              "http_client": { "api_key": "secret-key", "api_key_delivery": "query", "api_key_param_name": "api_key" },
              "can_handle": { "media_types": ["Movies"], "entity_types": ["MediaAsset"] },
              "search_strategies": [ { "name": "by_title", "priority": 1, "required_fields": ["title"],
                "url_template": "{base_url}/search?q={title}&api_key={api_key}", "results_path": "results" } ],
              "field_mappings": [
                { "claim_key": "title", "json_path": "name", "confidence": 0.8 },
                { "claim_key": "sample_id", "json_path": "id", "confidence": 1.0 } ],
              "follow_ups": {{followUps}}
            }
            """)!;

    private static ProviderLookupRequest Request() => new()
    {
        EntityId = Guid.NewGuid(),
        EntityType = EntityType.MediaAsset,
        MediaType = MediaType.Movies,
        Title = "Widget",
        Language = "en",
        Country = "US",
    };

    private static ConfigDrivenAdapter Adapter(
        ProviderConfiguration config, RecordingHandler handler, IProviderResponseCacheRepository? cache = null) =>
        new(config, new RecordingHttpFactory(handler), NullLogger<ConfigDrivenAdapter>.Instance,
            NullProviderHealthMonitor.Instance, cache, new ProviderRateLimiterCoordinator());

    private static RecordingHandler Handler(Func<RecordedRequest, HttpResponseMessage?> detail) => new(request =>
        request.Url.Contains("/search?", StringComparison.Ordinal)
            ? RecordingHandler.Json(SearchResponse)
            : detail(request) ?? RecordingHandler.Status(HttpStatusCode.NotFound));

    private const string Detail = """
        [{ "name": "detail", "inputs": { "id": ["claim:sample_id"] },
           "url": "{base_url}/items/{id}?api_key={api_key}",
           "rules": [ { "claim_key": "title", "json_path": "name", "confidence": 0.8 },
                      { "claim_key": "maker", "json_path": "info.maker", "confidence": 0.7 } ] }]
        """;

    [Fact]
    public async Task FollowUpOfAnUnknownProvider_AddsClaimsAndSkipsDuplicateValues()
    {
        var handler = Handler(_ => RecordingHandler.Json("""{ "name": "Widget", "info": { "maker": "Acme" } }"""));

        var claims = await Adapter(Provider(Detail), handler).FetchAsync(Request());

        Assert.Equal(["GET https://api.sample.test/v1/search?q=Widget", "GET https://api.sample.test/v1/items/7"], handler.Lines);
        Assert.Single(claims, claim => claim.Key == "title");
        Assert.Contains(claims, claim => claim is { Key: "maker", Value: "Acme" });
    }

    [Fact]
    public async Task AlwaysMode_KeepsDuplicates_AndKeyMode_KeepsOnlyTheFirstValue()
    {
        var always = Detail.Replace("\"rules\"", "\"add\": \"always\", \"rules\"", StringComparison.Ordinal);
        var key = Detail.Replace("\"rules\"", "\"add\": \"if_missing_key\", \"rules\"", StringComparison.Ordinal)
            .Replace("\"json_path\": \"name\"", "\"json_path\": \"info.maker\"", StringComparison.Ordinal);
        var response = RecordingHandler.Json("""{ "name": "Widget", "info": { "maker": "Acme" } }""");
        Assert.NotNull(response);

        var alwaysClaims = await Adapter(Provider(always), Handler(_ => RecordingHandler.Json("""{ "name": "Widget", "info": { "maker": "Acme" } }""")))
            .FetchAsync(Request());
        var keyClaims = await Adapter(Provider(key), Handler(_ => RecordingHandler.Json("""{ "name": "Widget", "info": { "maker": "Acme" } }""")))
            .FetchAsync(Request());

        Assert.Equal(2, alwaysClaims.Count(claim => claim.Key == "title"));
        Assert.Single(keyClaims, claim => claim.Key == "title");
    }

    [Fact]
    public async Task MissingInputOrKey_SkipsTheFollowUpWithoutARequest()
    {
        var noInput = Detail.Replace("claim:sample_id", "claim:other_id", StringComparison.Ordinal);
        var handler = Handler(_ => RecordingHandler.Json("{}"));
        await Adapter(Provider(noInput), handler).FetchAsync(Request());
        Assert.DoesNotContain(handler.Lines, line => line.Contains("/items/", StringComparison.Ordinal));

        var keyless = Provider(Detail);
        keyless.HttpClient!.ApiKey = null;
        keyless.RequiresApiKey = false;
        keyless.SearchStrategies![0].UrlTemplate = "{base_url}/search?q={title}";
        var keylessHandler = Handler(_ => RecordingHandler.Json("{}"));
        await Adapter(keyless, keylessHandler).FetchAsync(Request());
        Assert.DoesNotContain(keylessHandler.Lines, line => line.Contains("/items/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MediaTypeFilter_KeepsTheFollowUpToItsKind()
    {
        var tv = Detail.Replace("\"name\": \"detail\",", "\"name\": \"detail\", \"media_types\": [\"TV\"],", StringComparison.Ordinal);
        var handler = Handler(_ => RecordingHandler.Json("{}"));

        await Adapter(Provider(tv), handler).FetchAsync(Request());

        Assert.Single(handler.Lines);
    }

    [Fact]
    public async Task FailingFollowUp_KeepsTheEarlierClaims_UnlessItPropagates()
    {
        var handler = Handler(_ => RecordingHandler.Status(HttpStatusCode.InternalServerError));
        var kept = await Adapter(Provider(Detail), handler).FetchAsync(Request());
        Assert.Contains(kept, claim => claim.Key == "title");

        var propagate = Detail.Replace("\"rules\"", "\"on_error\": \"propagate\", \"rules\"", StringComparison.Ordinal);
        await Assert.ThrowsAsync<MediaEngine.Domain.Jobs.ProviderUnavailableException>(() =>
            Adapter(Provider(propagate), Handler(_ => RecordingHandler.Status(HttpStatusCode.InternalServerError)))
                .FetchAsync(Request()));
    }

    [Fact]
    public async Task ResponseCache_AnswersTheSecondLookupWithoutARequest()
    {
        var cache = new MemoryResponseCache();
        var handler = Handler(_ => RecordingHandler.Json("""{ "name": "Widget", "info": { "maker": "Acme" } }"""));
        var cached = Detail.Replace("\"rules\"", "\"cache\": { \"store\": \"response_cache\" }, \"rules\"", StringComparison.Ordinal);
        var adapter = Adapter(Provider(cached, "\"cache_ttl_hours\": 24,"), handler, cache);

        await adapter.FetchAsync(Request());
        var second = await adapter.FetchAsync(Request());

        Assert.Equal(1, handler.Lines.Count(line => line.Contains("/items/7", StringComparison.Ordinal)));
        Assert.Contains(second, claim => claim is { Key: "maker", Value: "Acme" });
        Assert.Equal(24, cache.LastTtl);
    }

    [Fact]
    public async Task MemoryCache_FetchesOncePerKeyAndForgetsFailures()
    {
        var calls = 0;
        var memory = Detail.Replace("\"rules\"", "\"cache\": { \"store\": \"memory\", \"key\": \"{id}\" }, \"rules\"", StringComparison.Ordinal);
        var adapter = Adapter(Provider(memory), Handler(request =>
            ++calls == 1
                ? RecordingHandler.Status(HttpStatusCode.InternalServerError)
                : RecordingHandler.Json("""{ "name": "Widget", "info": { "maker": "Acme" } }""")));

        var failed = await adapter.FetchAsync(Request());
        var recovered = await adapter.FetchAsync(Request());
        await adapter.FetchAsync(Request());

        Assert.DoesNotContain(failed, claim => claim.Key == "maker");
        Assert.Contains(recovered, claim => claim.Key == "maker");
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task NestedLookup_ReadsItsParentAndKeepsOuterClaimsWhenItFails()
    {
        var nested = """
            [{ "name": "detail", "inputs": { "id": ["claim:sample_id"] }, "url": "{base_url}/items/{id}?api_key={api_key}",
               "rules": [
                 { "claim_key": "maker", "json_path": "info.maker", "confidence": 0.7 },
                 { "kind": "lookup", "lookup": { "name": "maker_page", "inputs": { "maker_id": ["parent:info.maker_id"] },
                     "url": "{base_url}/makers/{maker_id}?api_key={api_key}",
                     "rules": [ { "claim_key": "maker_country", "json_path": "country", "confidence": 0.6 } ] } } ] }]
            """;
        var good = Handler(request => request.Url.Contains("/makers/", StringComparison.Ordinal)
            ? RecordingHandler.Json("""{ "country": "Wakanda" }""")
            : RecordingHandler.Json("""{ "info": { "maker": "Acme", "maker_id": 5 } }"""));
        var bad = Handler(request => request.Url.Contains("/makers/", StringComparison.Ordinal)
            ? RecordingHandler.Status(HttpStatusCode.BadGateway)
            : RecordingHandler.Json("""{ "info": { "maker": "Acme", "maker_id": 5 } }"""));

        var ok = await Adapter(Provider(nested), good).FetchAsync(Request());
        var degraded = await Adapter(Provider(nested), bad).FetchAsync(Request());

        Assert.Contains(ok, claim => claim is { Key: "maker_country", Value: "Wakanda" });
        Assert.Contains(degraded, claim => claim is { Key: "maker", Value: "Acme" });
        Assert.DoesNotContain(degraded, claim => claim.Key == "maker_country");
        Assert.Contains("GET https://api.sample.test/v1/makers/5", good.Lines);
    }

    private sealed class MemoryResponseCache : IProviderResponseCacheRepository
    {
        private readonly Dictionary<string, string> _entries = [];

        public int LastTtl { get; private set; }

        public Task<CachedResponse?> FindAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_entries.TryGetValue(key, out var json)
                ? new CachedResponse(json, null)
                : null);

        public Task UpsertAsync(string key, string provider, string query, string json, string? etag, int ttl, CancellationToken ct = default)
        {
            _entries[key] = json;
            LastTtl = ttl;
            return Task.CompletedTask;
        }

        public Task<string?> FindExpiredEtagAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task RefreshExpiryAsync(string key, int ttl, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> PurgeExpiredAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ClearAllAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<CacheStats> GetStatsAsync(CancellationToken ct = default) => Task.FromResult(new CacheStats(0, 0, null));
    }
}
