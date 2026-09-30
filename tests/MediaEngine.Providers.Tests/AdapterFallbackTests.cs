using System.Net;
using System.Text;
using System.Text.Json;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Models;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
#pragma warning disable CS0618 // suppress obsolete warnings in test stubs

namespace MediaEngine.Providers.Tests;

/// <summary>
/// Verifies that all external metadata adapters degrade gracefully:
/// they return an empty claim list on network failure rather than throwing.
///
/// Config-driven adapters are loaded from <c>config/providers/</c> and
/// wired to stub HTTP handlers that inject predetermined responses (error status,
/// empty body, or timeout) without touching the network.
///
/// Wikidata remains a coded adapter — its fallback test uses the typed class directly.
///
/// Spec: Phase 9 – External Metadata Adapters § Graceful Failure.
/// </summary>
public sealed class AdapterFallbackTests
{
    [Theory]
    [InlineData("fr", false)]
    [InlineData("en", true)]
    [InlineData(null, false)]
    public async Task AppleBooks_AutomaticMatch_RequiresEditionEvidence(string? language, bool accepted)
    {
        var config = LoadExampleConfig("apple_api");
        var payload = JsonSerializer.Serialize(new { results = new[] { new {
            trackId = 123, trackName = "Regretting You", artistName = "Colleen Hoover", language
        } } });
        var adapter = new ConfigDrivenAdapter(config,
            BuildFactory(config.Name, new RoutingStubHttpMessageHandler(_ => JsonResponse(payload))),
            NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);
        var request = new ProviderLookupRequest {
            EntityId = Guid.NewGuid(), EntityType = EntityType.Work, MediaType = MediaType.Books,
            Title = "Regretting You", Author = "Colleen Hoover", FileLanguage = "en", Language = "en",
            BaseUrl = "https://itunes.apple.com"
        };
        var claims = await adapter.FetchAsync(request);
        Assert.Equal(accepted, claims.Count > 0);
        Assert.NotEmpty(await adapter.SearchAsync(request));
    }

    [Fact]
    public async Task AppleBooks_SearchFailure_IsNotAnEmptyResult()
    {
        var config = LoadExampleConfig("apple_api");
        var adapter = new ConfigDrivenAdapter(config, BuildFactory(config.Name, HttpStatusCode.ServiceUnavailable),
            NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);
        await Assert.ThrowsAsync<AggregateException>(() => adapter.SearchAsync(new ProviderLookupRequest {
            EntityId = Guid.NewGuid(), EntityType = EntityType.Work, MediaType = MediaType.Books,
            Title = "A book", BaseUrl = "https://itunes.apple.com"
        }));
    }

    [Fact]
    public async Task AppleBooks_EmptyIsbnLookup_DoesNotLimitTitleCandidates()
    {
        var config = LoadExampleConfig("apple_api");
        var adapter = new ConfigDrivenAdapter(config,
            BuildFactory(config.Name, new RoutingStubHttpMessageHandler(request =>
                JsonResponse(request.RequestUri!.AbsolutePath.Contains("lookup") ? "{\"results\":[]}" :
                    "{\"results\":[{\"trackId\":1,\"trackName\":\"First\"},{\"trackId\":2,\"trackName\":\"Second\"}]}"))),
            NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);
        var results = await adapter.SearchAsync(new ProviderLookupRequest {
            EntityId = Guid.NewGuid(), EntityType = EntityType.Work, MediaType = MediaType.Books,
            Title = "A book", Isbn = "9781542016421", BaseUrl = "https://itunes.apple.com"
        }, 10);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task AppleBooks_Audiobook_MapsDurationFromTrackTimeMillis()
    {
        var config = LoadExampleConfig("apple_api");
        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(_ => JsonResponse("""
                {
                  "resultCount": 1,
                  "results": [
                    {
                      "collectionId": 123,
                      "collectionName": "Project Hail Mary",
                      "artistName": "Andy Weir",
                      "trackTimeMillis": 3600000,
                      "artworkUrl100": "https://example.test/project-hail-mary.jpg"
                    }
                  ]
                }
                """)));
        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        var claims = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Audiobooks,
            Title = "Project Hail Mary",
            Author = "Andy Weir",
            BaseUrl = "https://itunes.apple.com",
        });

        Assert.Contains(claims, claim => claim.Key == MetadataFieldConstants.DurationField
            && claim.Value == "60");
    }

    // ── Apple Books — HTTP 503 ────────────────────────────────────────────────

    [Fact]
    public async Task AppleBooks_Returns_Empty_On_HttpError()
    {
        // Arrange: load config, wire stub returning HTTP 503.
        var config = LoadExampleConfig("apple_api");
        var factory = BuildFactory(config.Name, HttpStatusCode.ServiceUnavailable);
        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        var request = new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Books,
            Title = "Dune",
            Author = "Frank Herbert",
            BaseUrl = "https://itunes.apple.com",
        };

        // Act
        var claims = await adapter.FetchAsync(request);

        // Assert: empty list, no exception.
        Assert.Empty(claims);
    }

    [Fact]
    public async Task AppleBooks_Returns_Empty_On_TransportTimeout()
    {
        var config = LoadExampleConfig("apple_api");
        var adapter = new ConfigDrivenAdapter(
            config,
            BuildTimeoutFactory(config.Name),
            NullLogger<ConfigDrivenAdapter>.Instance,
            NullProviderHealthMonitor.Instance);

        var claims = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Books,
            Title = "Dune",
            Author = "Frank Herbert",
            BaseUrl = "https://itunes.apple.com",
        });

        Assert.Empty(claims);
    }

    [Fact]
    public async Task AppleBooks_PropagatesCallerCancellation()
    {
        var config = LoadExampleConfig("apple_api");
        var adapter = new ConfigDrivenAdapter(
            config,
            BuildTimeoutFactory(config.Name),
            NullLogger<ConfigDrivenAdapter>.Instance,
            NullProviderHealthMonitor.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.FetchAsync(
            new ProviderLookupRequest
            {
                EntityId = Guid.NewGuid(),
                EntityType = EntityType.MediaAsset,
                MediaType = MediaType.Books,
                Title = "Dune",
                Author = "Frank Herbert",
                BaseUrl = "https://itunes.apple.com",
            },
            cancellation.Token));
    }

    [Fact]
    public async Task AppleBooks_FetchAsync_RejectedIsbnLookup_FallsBackToTitleAuthorSearch()
    {
        var config = LoadExampleConfig("apple_api");

        var lookupResponse = """
            {
              "resultCount": 1,
              "results": [
                {
                  "trackId": 1533697459,
                  "trackName": "That Summer",
                  "artistName": "Jennifer Weiner",
                  "releaseDate": "2021-05-11T07:00:00Z"
                }
              ]
            }
            """;

        var searchResponse = """
            {
              "resultCount": 2,
              "results": [
                {
                  "trackId": 1526997052,
                  "trackName": "Project Hail Mary",
                  "language": "en",
                  "artistName": "Andy Weir",
                  "releaseDate": "2021-05-04T07:00:00Z",
                  "artworkUrl100": "https://example.test/project-hail-mary.jpg"
                },
                {
                  "trackId": 1533697459,
                  "trackName": "That Summer",
                  "artistName": "Jennifer Weiner",
                  "releaseDate": "2021-05-11T07:00:00Z"
                }
              ]
            }
            """;

        var requestedUrls = new List<string>();
        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? string.Empty;
                requestedUrls.Add(url);

                return JsonResponse(url.Contains("/lookup?", StringComparison.OrdinalIgnoreCase)
                    ? lookupResponse
                    : searchResponse);
            }));

        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        var claims = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Books,
            Title = "Project Hail Mary",
            Author = "Andy Weir",
            Isbn = "9780593135204",
            BaseUrl = "https://itunes.apple.com",
        });

        Assert.Contains(requestedUrls, url => url.Contains("/lookup?", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(requestedUrls, url => url.Contains("/search?", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.Title && c.Value == "Project Hail Mary");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.Author && c.Value == "Andy Weir");
        Assert.DoesNotContain(claims, c => c.Value == "That Summer");
    }

    [Fact]
    public async Task AppleBooks_FetchAsync_DerivativeUsResults_UsesConfiguredGbFallback()
    {
        var config = LoadExampleConfig("apple_api");

        var emptyLookupResponse = """
            {
              "resultCount": 0,
              "results": []
            }
            """;

        var usDerivativeResponse = """
            {
              "resultCount": 2,
              "results": [
                {
                  "trackId": 6505071899,
                  "trackName": "J.K. Rowling - Harry Potter and the Philosopher's Stone - Summary & Reading Guide",
                  "artistName": "Olivier Tableau Daniel Jacques",
                  "releaseDate": "2024-07-01T07:00:00Z",
                  "description": "A summary and reading guide for the novel."
                },
                {
                  "trackId": 1604673091,
                  "trackName": "Myths and Symbols in J.K. Rowling's Harry Potter and the Philosopher's Stone",
                  "artistName": "Volker Geyer",
                  "releaseDate": "2002-03-23T08:00:00Z",
                  "description": "A chapter by chapter analysis of the novel."
                }
              ]
            }
            """;
        var gbExactResponse = """
            {
              "resultCount": 1,
              "results": [
                {
                  "trackId": 1596234133,
                  "trackName": "Harry Potter and the Philosopher's Stone (Enhanced Edition)",
                  "language": "en",
                  "artistName": "J.K. Rowling",
                  "releaseDate": "2015-11-20T08:00:00Z",
                  "artworkUrl100": "https://example.test/philosophers-stone.jpg"
                }
              ]
            }
            """;

        var requestedUrls = new List<string>();
        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? string.Empty;
                requestedUrls.Add(url);

                if (url.Contains("/lookup?", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(emptyLookupResponse);
                }

                return JsonResponse(url.Contains("country=gb", StringComparison.OrdinalIgnoreCase)
                    ? gbExactResponse
                    : usDerivativeResponse);
            }));

        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        var claims = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Books,
            Title = "Harry Potter and the Philosopher's Stone",
            Author = "J.K. Rowling",
            Isbn = "9780747532699",
            Country = "us",
            Language = "en",
            BaseUrl = "https://itunes.apple.com",
        });

        Assert.Contains(requestedUrls, url => url.Contains("country=us", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(requestedUrls, url => url.Contains("country=gb", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(claims, claim => claim.Key == MetadataFieldConstants.Title
            && claim.Value == "Harry Potter and the Philosopher's Stone (Enhanced Edition)");
    }

    [Fact]
    public async Task AppleBooks_FetchAsync_UsesFileLanguageAndConfiguredJapaneseStorefront()
    {
        var config = LoadExampleConfig("apple_api");
        var requestedUrls = new List<string>();
        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? string.Empty;
                requestedUrls.Add(url);
                return JsonResponse(url.Contains("country=jp", StringComparison.OrdinalIgnoreCase)
                    ? """{"resultCount":1,"results":[{"trackId":1,"trackName":"ノルウェイの森","language":"ja","artistName":"村上春樹"}]}"""
                    : """{"resultCount":0,"results":[]}""");
            }));
        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        var claims = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Books,
            Title = "ノルウェイの森",
            Author = "村上春樹",
            Language = "en",
            FileLanguage = "ja-JP",
            Country = "us",
            BaseUrl = "https://itunes.apple.com",
        });

        Assert.Contains(requestedUrls, url => url.Contains("lang=ja_us", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(requestedUrls, url => url.Contains("country=jp", StringComparison.OrdinalIgnoreCase)
            && url.Contains("lang=ja_jp", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(claims, claim => claim.Key == MetadataFieldConstants.Title
            && claim.Value == "ノルウェイの森");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    public async Task AppleBooks_FetchAsync_UsesConfiguredEuropeanStorefrontForFileLanguage(
        string language)
    {
        var config = LoadExampleConfig("apple_api");
        var requestedUrls = new List<string>();
        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? string.Empty;
                requestedUrls.Add(url);
                return JsonResponse(url.Contains($"country={language}", StringComparison.OrdinalIgnoreCase)
                    ? JsonSerializer.Serialize(new { resultCount = 1, results = new[] { new { trackId = 1, trackName = "Localized Book", artistName = "Example Author", language } } })
                    : """{"resultCount":0,"results":[]}""");
            }));
        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        var claims = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Books,
            Title = "Localized Book",
            Author = "Example Author",
            Language = "en",
            FileLanguage = language,
            Country = "us",
            BaseUrl = "https://itunes.apple.com",
        });

        Assert.Contains(requestedUrls, url => url.Contains($"lang={language}_us", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(requestedUrls, url => url.Contains($"country={language}", StringComparison.OrdinalIgnoreCase)
            && url.Contains($"lang={language}_{language}", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(claims, claim => claim.Key == MetadataFieldConstants.Title
            && claim.Value == "Localized Book");
    }

    [Fact]
    public async Task ComicVine_FetchAsync_PrefersIssueSearch_WhenTitleIsPresent()
    {
        var config = LoadExampleConfig("comicvine");
        config.HttpClient ??= new HttpClientConfig();
        config.HttpClient.ApiKey = "test-key";

        var requestedUrls = new List<string>();
        var issueResponse = """
            {
              "results": [
                {
                  "name": "Batman Year One Part 1",
                  "issue_number": "1",
                  "id": 712097,
                  "cover_date": "1987-02-01",
                  "volume": { "name": "Batman" },
                  "image": { "original_url": "https://example.test/batman-year-one.jpg" }
                }
              ]
            }
            """;

        var volumeResponse = """
            {
              "results": [
                {
                  "name": "Batman: Year One",
                  "id": 112492,
                  "start_year": "1988"
                }
              ]
            }
            """;

        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? string.Empty;
                requestedUrls.Add(url);

                var body = url.Contains("resources=issue", StringComparison.OrdinalIgnoreCase)
                    ? issueResponse
                    : volumeResponse;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            }));

        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        var request = new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Comics,
            Title = "Batman: Year One Part 1",
            Series = "Batman",
            BaseUrl = "https://comicvine.gamespot.com/api",
        };

        var claims = await adapter.FetchAsync(request);

        Assert.NotEmpty(requestedUrls);
        Assert.Contains("resources=issue", requestedUrls[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(claims, c => string.Equals(c.Key, MetadataFieldConstants.Title, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.Value, "Batman Year One Part 1", StringComparison.Ordinal));
        Assert.Contains(claims, c => string.Equals(c.Key, "issue_number", StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.Value, "1", StringComparison.Ordinal));
    }

    // ── Config loading ───────────────────────────────────────────────────────

    [Fact]
    public async Task ComicVine_FetchAsync_PrefersExactSeriesAndIssue_WhenIssueTitleDiffers()
    {
        var config = LoadExampleConfig("comicvine");
        config.HttpClient ??= new HttpClientConfig();
        config.HttpClient.ApiKey = "test-key";

        var issueResponse = """
            {
              "results": [
                {
                  "name": "2 of 6",
                  "issue_number": "2",
                  "id": 111,
                  "cover_date": "2012-10-01",
                  "volume": { "name": "Before Watchmen: Nite Owl" },
                  "image": { "original_url": "https://example.test/before-watchmen.jpg" }
                },
                {
                  "name": "Two Riders Were Approaching...",
                  "issue_number": "2",
                  "id": 222,
                  "cover_date": "1986-10-01",
                  "site_detail_url": "https://comicvine.gamespot.com/watchmen-2-two-riders-were-approaching/4000-222/",
                  "volume": { "name": "Watchmen" },
                  "image": { "original_url": "https://example.test/watchmen-2.jpg" }
                },
                {
                  "name": "Watchmen",
                  "issue_number": "1",
                  "id": 184079,
                  "cover_date": "2005-01-01",
                  "volume": { "name": "Absolute Watchmen" },
                  "image": { "original_url": "https://example.test/absolute-watchmen.jpg" }
                }
              ]
            }
            """;

        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(_ => JsonResponse(issueResponse)));

        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        var claims = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Comics,
            Title = "Watchmen #2",
            Author = "Alan Moore",
            Series = "Watchmen",
            Year = "1986",
            BaseUrl = "https://comicvine.gamespot.com/api",
            Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MetadataFieldConstants.Series] = "Watchmen",
                [MetadataFieldConstants.SeriesPosition] = "2",
            },
        });

        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.Title
            && c.Value == "Two Riders Were Approaching...");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.IssueTitle
            && c.Value == "Two Riders Were Approaching...");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.Series
            && c.Value == "Watchmen");
        Assert.Contains(claims, c => c.Key == "issue_number" && c.Value == "2");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.IssueSourceUrl
            && c.Value == "https://comicvine.gamespot.com/watchmen-2-two-riders-were-approaching/4000-222/");
        Assert.Contains(claims, c => c.Key == BridgeIdKeys.ComicVineId && c.Value == "222");
        Assert.DoesNotContain(claims, c => c.Value == "Absolute Watchmen");
    }

    [Fact]
    public async Task ComicVine_FetchAsync_PrefersOriginalIssueAndKeepsIssueScopedDescription()
    {
        var config = LoadExampleConfig("comicvine");
        config.HttpClient ??= new HttpClientConfig();
        config.HttpClient.ApiKey = "test-key";

        var issueResponse = """
            {
              "results": [
                {
                  "name": "Saga",
                  "issue_number": "1",
                  "id": 111,
                  "cover_date": "2014-03-01",
                  "volume": { "name": "Saga" },
                  "description": "<p>Die fantastische Weltraum-Opera von Brian K. Vaughan und Fiona Staples.</p>",
                  "site_detail_url": "https://comicvine.gamespot.com/saga-1-localized/4000-111/",
                  "image": { "original_url": "https://example.test/saga-de.jpg" }
                },
                {
                  "name": "Chapter One",
                  "issue_number": "1",
                  "id": 222,
                  "cover_date": "2012-03-14",
                  "volume": { "name": "Saga" },
                  "description": "<p>Alana and Marko try to protect their newborn child.</p>",
                  "site_detail_url": "https://comicvine.gamespot.com/saga-1-chapter-one/4000-222/",
                  "image": { "original_url": "https://example.test/saga-en.jpg" }
                }
              ]
            }
            """;

        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(_ => JsonResponse(issueResponse)));

        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        var claims = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Comics,
            Title = "Saga #1",
            Series = "Saga",
            Year = "2012",
            BaseUrl = "https://comicvine.gamespot.com/api",
            Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MetadataFieldConstants.Series] = "Saga",
                [MetadataFieldConstants.SeriesPosition] = "1",
            },
        });

        Assert.Contains(claims, c => c.Key == BridgeIdKeys.ComicVineId && c.Value == "222");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.IssueTitle && c.Value == "Chapter One");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.IssueDescription
            && c.Value == "Alana and Marko try to protect their newborn child.");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.IssueSourceUrl
            && c.Value == "https://comicvine.gamespot.com/saga-1-chapter-one/4000-222/");
        Assert.DoesNotContain(claims, c => c.Key == MetadataFieldConstants.Description);
        Assert.DoesNotContain(claims, c => c.Value.Contains("Weltraum", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ComicVine_FetchAsync_EnrichesIssueMatchWithVolumeSequenceFacts()
    {
        var config = LoadExampleConfig("comicvine");
        config.HttpClient ??= new HttpClientConfig();
        config.HttpClient.ApiKey = "test-key";

        var requestedUrls = new List<string>();
        var issueResponse = """
            {
              "results": [
                {
                  "name": "Chapter One",
                  "issue_number": "1",
                  "id": 222,
                  "cover_date": "2012-03-14",
                  "volume": { "id": 1234, "name": "Saga" },
                  "image": { "original_url": "https://example.test/saga-en.jpg" }
                }
              ]
            }
            """;
        var volumeResponse = """
            {
              "results": {
                "id": 1234,
                "name": "Saga",
                "count_of_issues": 72,
                "start_year": "2012",
                "publisher": { "name": "Image Comics" }
              }
            }
            """;

        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? string.Empty;
                requestedUrls.Add(url);
                return url.Contains("/volume/4050-1234/", StringComparison.OrdinalIgnoreCase)
                    ? JsonResponse(volumeResponse)
                    : JsonResponse(issueResponse);
            }));

        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        var claims = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Comics,
            Title = "Saga #1",
            Series = "Saga",
            BaseUrl = "https://comicvine.gamespot.com/api",
            Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MetadataFieldConstants.Series] = "Saga",
                [MetadataFieldConstants.SeriesPosition] = "1",
            },
        });

        Assert.Contains(requestedUrls, url => url.Contains("resources=issue", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(requestedUrls, url => url.Contains("/volume/4050-1234/", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(claims, c => c.Key == BridgeIdKeys.ComicVineVolumeId && c.Value == "1234");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.SequenceTotal && c.Value == "72");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.SequenceTotalScope && c.Value == "MainSequence");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.SeriesStartYear && c.Value == "2012");
    }

    [Fact]
    public async Task ComicVine_FetchAsync_UsesVolumeFactsToDisambiguateSameNameRuns()
    {
        var config = LoadExampleConfig("comicvine");
        config.HttpClient ??= new HttpClientConfig();
        config.HttpClient.ApiKey = "test-key";

        var issueResponse = """
            {
              "results": [
                {
                  "name": "Chapter One",
                  "issue_number": "1",
                  "id": 111,
                  "cover_date": "2012-03-14",
                  "volume": { "id": 900, "name": "Saga" },
                  "image": { "original_url": "https://example.test/saga-wrong.jpg" }
                },
                {
                  "name": "Chapter One",
                  "issue_number": "1",
                  "id": 222,
                  "cover_date": "2012-03-14",
                  "volume": { "id": 1234, "name": "Saga" },
                  "image": { "original_url": "https://example.test/saga-right.jpg" }
                }
              ]
            }
            """;
        var olderVolumeResponse = """
            {
              "results": {
                "id": 900,
                "name": "Saga",
                "count_of_issues": 12,
                "start_year": "1992",
                "publisher": { "name": "Other Publisher" }
              }
            }
            """;
        var currentVolumeResponse = """
            {
              "results": {
                "id": 1234,
                "name": "Saga",
                "count_of_issues": 72,
                "start_year": "2012",
                "publisher": { "name": "Image Comics" }
              }
            }
            """;

        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? string.Empty;
                if (url.Contains("/volume/4050-900/", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(olderVolumeResponse);
                }

                if (url.Contains("/volume/4050-1234/", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(currentVolumeResponse);
                }

                return JsonResponse(issueResponse);
            }));

        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        var claims = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Comics,
            Title = "Saga #1",
            Series = "Saga",
            Year = "2012",
            BaseUrl = "https://comicvine.gamespot.com/api",
            Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MetadataFieldConstants.Series] = "Saga",
                [MetadataFieldConstants.SeriesPosition] = "1",
                [MetadataFieldConstants.PublisherField] = "Image Comics",
            },
        });

        Assert.Contains(claims, c => c.Key == BridgeIdKeys.ComicVineId && c.Value == "222");
        Assert.Contains(claims, c => c.Key == BridgeIdKeys.ComicVineVolumeId && c.Value == "1234");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.SequenceTotal && c.Value == "72");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.SeriesStartYear && c.Value == "2012");
        Assert.DoesNotContain(claims, c => c.Key == BridgeIdKeys.ComicVineId && c.Value == "111");
    }

    [Fact]
    public async Task ComicVine_FetchAsync_UsesVolumeSearchWhenIssueSearchFindsLocalizedRun()
    {
        var config = LoadExampleConfig("comicvine");
        config.HttpClient ??= new HttpClientConfig();
        config.HttpClient.ApiKey = "test-key";

        var issueSearchResponse = """
            {
              "results": [
                {
                  "name": null,
                  "issue_number": "2",
                  "id": 667131,
                  "cover_date": "2013-12-05",
                  "volume": { "id": 110032, "name": "Saga" },
                  "description": "<p>Die fantastische Weltraum-Opera geht in die zweite Runde!</p>",
                  "image": { "original_url": "https://example.test/saga-localized.jpg" }
                }
              ]
            }
            """;
        var volumeSearchResponse = """
            {
              "results": [
                {
                  "id": 46568,
                  "name": "Saga",
                  "count_of_issues": 72,
                  "start_year": "2012",
                  "publisher": { "name": "Image" }
                },
                {
                  "id": 110032,
                  "name": "Saga",
                  "count_of_issues": 12,
                  "start_year": "2013",
                  "publisher": { "name": "Cross Cult" }
                }
              ]
            }
            """;
        var runScopedIssueResponse = """
            {
              "results": [
                {
                  "name": "Chapter Two",
                  "issue_number": "2",
                  "id": 321316,
                  "cover_date": "2012-04-01",
                  "volume": { "id": 46568, "name": "Saga" },
                  "image": { "original_url": "https://example.test/saga-original.jpg" }
                }
              ]
            }
            """;
        var originalVolumeResponse = """
            {
              "results": {
                "id": 46568,
                "name": "Saga",
                "count_of_issues": 72,
                "start_year": "2012",
                "publisher": { "name": "Image" }
              }
            }
            """;

        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? string.Empty;
                if (url.Contains("resources=volume", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(volumeSearchResponse);
                }

                if (url.Contains("/issues/", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(runScopedIssueResponse);
                }

                if (url.Contains("/volume/4050-46568/", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(originalVolumeResponse);
                }

                return JsonResponse(issueSearchResponse);
            }));

        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        var claims = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Comics,
            Title = "Saga #2",
            Series = "Saga",
            Year = "2012",
            BaseUrl = "https://comicvine.gamespot.com/api",
            Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MetadataFieldConstants.Series] = "Saga",
                [MetadataFieldConstants.SeriesPosition] = "2",
            },
        });

        Assert.Contains(claims, c => c.Key == BridgeIdKeys.ComicVineId && c.Value == "321316");
        Assert.Contains(claims, c => c.Key == BridgeIdKeys.ComicVineVolumeId && c.Value == "46568");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.SequenceTotal && c.Value == "72");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.SeriesStartYear && c.Value == "2012");
        Assert.DoesNotContain(claims, c => c.Key == BridgeIdKeys.ComicVineVolumeId && c.Value == "110032");
    }

    [Fact]
    public async Task ComicVine_FetchAsync_BuildsLightweightAuthoritativeVolumeManifestWithoutImages()
    {
        var config = LoadExampleConfig("comicvine");
        config.HttpClient ??= new HttpClientConfig();
        config.HttpClient.ApiKey = "test-key";
        var requestedUrls = new List<string>();
        var issue = """
            {
              "name": "Chapter One",
              "issue_number": "1",
              "id": 101,
              "cover_date": "2012-03-14",
              "volume": { "id": 900, "name": "Saga" }
            }
            """;
        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? string.Empty;
                requestedUrls.Add(url);
                if (url.Contains("field_list=", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse("""
                        {
                          "number_of_total_results": 2,
                          "results": [
                            { "id": 101, "issue_number": "1", "name": "Chapter One", "cover_date": "2012-03-14", "volume": { "id": 900, "name": "Saga" } },
                            { "id": 102, "issue_number": "2", "name": "Chapter Two", "cover_date": "2012-04-11", "volume": { "id": 900, "name": "Saga" } }
                          ]
                        }
                        """);
                }
                if (url.Contains("resources=volume", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse("""{ "results": [{ "id": 900, "name": "Saga", "count_of_issues": 2, "start_year": "2012" }] }""");
                }

                if (url.Contains("/volume/4050-900/", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse("""{ "results": { "id": 900, "name": "Saga", "count_of_issues": 2, "start_year": "2012" } }""");
                }

                if (url.Contains("/issues/", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse($$"""{ "results": [{{issue}}] }""");
                }

                return JsonResponse($$"""{ "results": [{{issue}}] }""");
            }));
        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        var request = new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Comics,
            Title = "Saga #1",
            Series = "Saga",
            Year = "2012",
            BaseUrl = "https://comicvine.gamespot.com/api",
            Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MetadataFieldConstants.Series] = "Saga",
                [MetadataFieldConstants.SeriesPosition] = "1",
            },
        };
        var claims = await adapter.FetchAsync(request);
        var repeatedClaims = await adapter.FetchAsync(request);

        var manifestClaim = Assert.Single(claims, claim => claim.Key == MetadataFieldConstants.SequenceManifestJson);
        var manifest = JsonSerializer.Deserialize<ProviderSequenceManifest>(manifestClaim.Value);
        Assert.NotNull(manifest);
        Assert.True(manifest.IsAuthoritative);
        Assert.Equal("issues", manifest.ExpectedTotalKind);
        Assert.Equal(2, manifest.Items.Count);
        Assert.Contains(repeatedClaims, claim => claim.Key == MetadataFieldConstants.SequenceManifestJson);
        var manifestUrl = Assert.Single(requestedUrls, url => url.Contains("field_list=", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("image", Uri.UnescapeDataString(manifestUrl), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ComicVine_FetchAsync_TriesMultipleVolumesForRunScopedIssueLookup()
    {
        var config = LoadExampleConfig("comicvine");
        config.HttpClient ??= new HttpClientConfig();
        config.HttpClient.ApiKey = "test-key";

        var requestedUrls = new List<string>();
        var issueSearchResponse = """
            {
              "results": [
                {
                  "name": "Batman: Year One",
                  "issue_number": "1",
                  "id": 111,
                  "cover_date": "1987-02-01",
                  "volume": { "id": 900, "name": "Batman: Year One" },
                  "image": { "original_url": "https://example.test/wrong.jpg" }
                }
              ]
            }
            """;
        var volumeSearchResponse = """
            {
              "results": [
                {
                  "id": 100,
                  "name": "Batman",
                  "count_of_issues": 900,
                  "start_year": "2016",
                  "publisher": { "name": "DC Comics" }
                },
                {
                  "id": 200,
                  "name": "Batman",
                  "count_of_issues": 713,
                  "start_year": "1940",
                  "publisher": { "name": "DC Comics" }
                }
              ]
            }
            """;
        var emptyIssueResponse = """{ "results": [] }""";
        var originalRunIssueResponse = """
            {
              "results": [
                {
                  "name": "Year One, Chapter One: Who I Am, How I Come to Be",
                  "issue_number": "404",
                  "id": 404404,
                  "cover_date": "1987-02-01",
                  "site_detail_url": "https://comicvine.gamespot.com/batman-404/4000-404404/",
                  "volume": { "id": 200, "name": "Batman" },
                  "image": { "original_url": "https://example.test/batman-404.jpg" }
                }
              ]
            }
            """;
        var originalVolumeResponse = """
            {
              "results": {
                "id": 200,
                "name": "Batman",
                "count_of_issues": 713,
                "start_year": "1940",
                "publisher": { "name": "DC Comics" }
              }
            }
            """;

        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? string.Empty;
                requestedUrls.Add(url);
                if (url.Contains("resources=volume", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(volumeSearchResponse);
                }

                if (url.Contains("filter=volume:100", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(emptyIssueResponse);
                }

                if (url.Contains("filter=volume:200", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(originalRunIssueResponse);
                }

                if (url.Contains("/volume/4050-200/", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(originalVolumeResponse);
                }

                return JsonResponse(issueSearchResponse);
            }));

        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        var claims = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Comics,
            Title = "Batman: Year One Part 1",
            Series = "Batman",
            Year = "1987",
            BaseUrl = "https://comicvine.gamespot.com/api",
            Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MetadataFieldConstants.Series] = "Batman",
                [MetadataFieldConstants.SeriesPosition] = "404",
                [MetadataFieldConstants.PublisherField] = "DC Comics",
            },
        });

        Assert.Contains(requestedUrls, url => url.Contains("filter=volume:100", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(requestedUrls, url => url.Contains("filter=volume:200", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(claims, c => c.Key == BridgeIdKeys.ComicVineId && c.Value == "404404");
        Assert.Contains(claims, c => c.Key == BridgeIdKeys.ComicVineVolumeId && c.Value == "200");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.IssueTitle
            && c.Value == "Year One, Chapter One: Who I Am, How I Come to Be");
    }

    [Fact]
    public async Task ComicVine_FetchAsync_AcceptsTrustedVolumeFallbackWithoutIssueQid()
    {
        var config = LoadExampleConfig("comicvine");
        config.HttpClient ??= new HttpClientConfig();
        config.HttpClient.ApiKey = "test-key";

        var issueSearchResponse = """{ "results": [] }""";
        var volumeSearchResponse = """
            {
              "results": [
                {
                  "id": 555,
                  "name": "Akira",
                  "count_of_issues": 6,
                  "start_year": "1982",
                  "publisher": { "name": "Kodansha" }
                }
              ]
            }
            """;
        var volumeDetailResponse = """
            {
              "results": {
                "id": 555,
                "name": "Akira",
                "count_of_issues": 6,
                "start_year": "1982",
                "publisher": { "name": "Kodansha" }
              }
            }
            """;

        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? string.Empty;
                if (url.Contains("resources=volume", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(volumeSearchResponse);
                }

                if (url.Contains("/volume/4050-555/", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(volumeDetailResponse);
                }

                return JsonResponse(issueSearchResponse);
            }));

        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        var claims = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Comics,
            Title = "Akira Vol. 1",
            Series = "Akira",
            Year = "1982",
            BaseUrl = "https://comicvine.gamespot.com/api",
            Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MetadataFieldConstants.Series] = "Akira",
                [MetadataFieldConstants.SeriesPosition] = "1",
                [MetadataFieldConstants.PublisherField] = "Kodansha",
            },
        });

        Assert.Contains(claims, c => c.Key == BridgeIdKeys.ComicVineVolumeId && c.Value == "555");
        Assert.DoesNotContain(claims, c => c.Key == BridgeIdKeys.ComicVineId);
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.Series && c.Value == "Akira");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.IssueNumber && c.Value == "1");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.SequenceTotal && c.Value == "6");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.SeriesStartYear && c.Value == "1982");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.PublisherField && c.Value == "Kodansha");
    }

    [Fact]
    public async Task ComicVine_FetchAsync_ExtractsStructuredCreatorCredits()
    {
        var config = LoadExampleConfig("comicvine");
        config.HttpClient ??= new HttpClientConfig();
        config.HttpClient.ApiKey = "test-key";

        var issueResponse = """
            {
              "results": [
                {
                  "name": "Chapter One",
                  "issue_number": "1",
                  "id": 222,
                  "cover_date": "2012-03-14",
                  "volume": { "id": 1234, "name": "Saga" },
                  "person_credits": [
                    { "name": "Brian K. Vaughan", "role": "Writer" },
                    { "name": "Fiona Staples", "role": "Penciller" }
                  ],
                  "image": { "original_url": "https://example.test/saga-en.jpg" }
                }
              ]
            }
            """;
        var volumeResponse = """
            {
              "results": {
                "id": 1234,
                "name": "Saga",
                "count_of_issues": 72,
                "start_year": "2012",
                "publisher": { "name": "Image Comics" }
              }
            }
            """;

        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? string.Empty;
                return url.Contains("/volume/4050-1234/", StringComparison.OrdinalIgnoreCase)
                    ? JsonResponse(volumeResponse)
                    : JsonResponse(issueResponse);
            }));

        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        var claims = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Comics,
            Title = "Saga #1",
            Series = "Saga",
            Year = "2012",
            BaseUrl = "https://comicvine.gamespot.com/api",
            Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MetadataFieldConstants.Series] = "Saga",
                [MetadataFieldConstants.SeriesPosition] = "1",
            },
        });

        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.Author && c.Value == "Brian K. Vaughan");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.Illustrator && c.Value == "Fiona Staples");
    }

    [Fact]
    public async Task AppleApi_FetchAsync_RejectsMusicTrackFromWrongAlbum()
    {
        var config = LoadExampleConfig("apple_api");

        var requestedUrls = new List<string>();
        var wrongTrackResponse = """
            {
              "resultCount": 1,
              "results": [
                {
                  "wrapperType": "track",
                  "kind": "song",
                  "artistId": 551695,
                  "collectionId": 696590528,
                  "trackId": 696592230,
                  "artistName": "David Bowie",
                  "collectionName": "The Platinum Collection",
                  "trackName": "Beauty and the Beast",
                  "trackCount": 57,
                  "trackNumber": 15,
                  "releaseDate": "2005-11-07T12:00:00Z",
                  "primaryGenreName": "Rock",
                  "artworkUrl100": "https://example.test/bowie-100x100bb.jpg"
                }
              ]
            }
            """;

        var emptyResponse = """{ "resultCount": 0, "results": [] }""";
        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? string.Empty;
                requestedUrls.Add(url);
                return JsonResponse(url.Contains("entity=musicTrack", StringComparison.OrdinalIgnoreCase)
                    ? wrongTrackResponse
                    : emptyResponse);
            }));

        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        var claims = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Music,
            Title = "Beauty and the Beast",
            Artist = "David Bowie",
            Album = "Heroes",
            BaseUrl = "https://itunes.apple.com",
            Country = "us",
            Language = "en",
        });

        Assert.Empty(claims);
        Assert.Contains(requestedUrls, url => url.Contains("entity=musicTrack", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(requestedUrls, url => url.Contains("entity=album", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MusicBrainz_FetchAsync_SelectsReleaseFromRequestedAlbumAndEmitsCoverArt()
    {
        var config = LoadExampleConfig("musicbrainz");
        var requestedUrls = new List<string>();
        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(request =>
            {
                requestedUrls.Add(request.RequestUri?.ToString() ?? string.Empty);
                return JsonResponse("""
                {
                  "recordings": [
                    {
                      "id": "recording-1",
                      "title": "Cornfield Chase",
                      "artist-credit": [{ "name": "Hans Zimmer" }],
                      "releases": [
                        {
                          "id": "wrong-release",
                          "title": "Classical",
                          "status": "Official",
                          "date": "2010-01-01",
                          "track-count": 20,
                          "release-group": { "id": "wrong-group", "primary-type": "Album" },
                          "cover-art-archive": { "artwork": true }
                        },
                        {
                          "id": "interstellar-release",
                          "title": "Interstellar: Original Motion Picture Soundtrack",
                          "status": "Official",
                          "date": "2014-11-18",
                          "track-count": 20,
                          "release-group": { "id": "interstellar-group", "primary-type": "Album" },
                          "cover-art-archive": { "artwork": true }
                        }
                      ]
                    }
                  ]
                }
                """);
            }));

        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        var claims = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Music,
            Title = "Cornfield Chase",
            Artist = "Hans Zimmer",
            Album = "Interstellar: Original Motion Picture Soundtrack",
            BaseUrl = "https://musicbrainz.org/ws/2",
            Country = "us",
            Language = "en",
        });

        Assert.Contains(claims, claim =>
            claim.Key == BridgeIdKeys.MusicBrainzReleaseId
            && claim.Value == "interstellar-release");
        Assert.Contains(claims, claim =>
            claim.Key == MetadataFieldConstants.CoverUrl
            && claim.Value == "https://coverartarchive.org/release/interstellar-release/front-500");
        Assert.DoesNotContain(claims, claim => claim.Value == "wrong-release");

        var decodedUrl = Uri.UnescapeDataString(Assert.Single(requestedUrls));
        Assert.Contains("recording:\"Cornfield Chase\"", decodedUrl, StringComparison.Ordinal);
        Assert.Contains("artist:\"Hans Zimmer\"", decodedUrl, StringComparison.Ordinal);
        Assert.Contains(
            "release:\"Interstellar: Original Motion Picture Soundtrack\"",
            decodedUrl,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigDrivenProvider_FetchAsync_UsesConfiguredRecordingIdLookupWithoutProviderNameBranch()
    {
        var config = LoadExampleConfig("musicbrainz");
        config.Name = "configured_recording_catalog";
        var requestedUrls = new List<string>();
        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(request =>
            {
                requestedUrls.Add(request.RequestUri?.ToString() ?? string.Empty);
                return JsonResponse("""
                    {
                      "id": "60d2246d-2761-4e1f-b30b-2784f00565b1",
                      "title": "Stan",
                      "artist-credit": [{ "name": "Eminem" }, { "name": "Dido" }],
                      "isrcs": ["USIR10000594"],
                      "releases": [
                        {
                          "id": "marshall-mathers-release",
                          "title": "The Marshall Mathers LP",
                          "status": "Official",
                          "date": "2000-05-23",
                          "release-group": { "id": "marshall-mathers-group", "primary-type": "Album" },
                          "cover-art-archive": { "artwork": true }
                        }
                      ]
                    }
                    """);
            }));

        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);
        var claims = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Music,
            Title = "Stan",
            Artist = "Eminem",
            Album = "The Marshall Mathers LP",
            Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BridgeIdKeys.MusicBrainzRecordingId] = "60d2246d-2761-4e1f-b30b-2784f00565b1",
            },
            BaseUrl = "https://musicbrainz.org/ws/2",
        });

        Assert.Contains(claims, claim =>
            claim.Key == BridgeIdKeys.MusicBrainzRecordingId
            && claim.Value == "60d2246d-2761-4e1f-b30b-2784f00565b1");
        Assert.Contains(claims, claim =>
            claim.Key == MetadataFieldConstants.Artist
            && claim.Value == "Eminem; Dido");
        var requestedUrl = Assert.Single(requestedUrls);
        Assert.Contains(
            "/recording/60d2246d-2761-4e1f-b30b-2784f00565b1?",
            requestedUrl,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MusicBrainz_FetchAsync_OmitsMissingOptionalAlbumClause()
    {
        var config = LoadExampleConfig("musicbrainz");
        var requestedUrls = new List<string>();
        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(request =>
            {
                requestedUrls.Add(request.RequestUri?.ToString() ?? string.Empty);
                return JsonResponse("""{ "recordings": [] }""");
            }));

        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Music,
            Title = "4'33\"",
            Artist = "John Cage",
            BaseUrl = "https://musicbrainz.org/ws/2",
            Country = "us",
            Language = "en",
        });

        var decodedUrl = Uri.UnescapeDataString(requestedUrls[0]);
        Assert.Contains("recording:\"4'33\\\"\"", decodedUrl, StringComparison.Ordinal);
        Assert.Contains("artist:\"John Cage\"", decodedUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("release:", decodedUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MusicBrainz_FetchAsync_PreservesStrongRecordingIdentityWithoutReleaseSelection()
    {
        var config = LoadExampleConfig("musicbrainz");
        var requestedUrls = new List<string>();
        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(request =>
            {
                requestedUrls.Add(request.RequestUri?.ToString() ?? string.Empty);
                return JsonResponse("""
                    {
                      "recordings": [
                        {
                          "id": "recording-without-release",
                          "title": "Unreleased Session",
                          "artist-credit": [{ "name": "Example Artist" }],
                          "releases": []
                        }
                      ]
                    }
                    """);
            }));

        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        var claims = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Music,
            Title = "Unreleased Session",
            Artist = "Example Artist",
            BaseUrl = "https://musicbrainz.org/ws/2",
        });

        Assert.Contains(claims, claim =>
            claim.Key == BridgeIdKeys.MusicBrainzRecordingId
            && claim.Value == "recording-without-release");
        Assert.DoesNotContain(claims, claim => claim.Key == BridgeIdKeys.MusicBrainzReleaseId);
        Assert.True(requestedUrls.Count >= 2);
    }

    [Fact]
    public async Task Tmdb_MovieSearch_StoresShortDescriptionButDoesNotSetGenericLanguage()
    {
        var config = LoadExampleConfig("tmdb");
        config.HttpClient ??= new HttpClientConfig();
        config.HttpClient.ApiKey = "test-key";

        var requestedUrls = new List<string>();
        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? string.Empty;
                requestedUrls.Add(url);

                if (url.Contains("/search/movie?", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse("""
                        {
                          "results": [
                            {
                              "id": 129,
                              "title": "Spirited Away",
                              "overview": "An English TMDB overview.",
                              "release_date": "2001-07-20",
                              "poster_path": "/poster.jpg",
                              "vote_average": 8.5,
                              "original_language": "ja"
                            }
                          ]
                        }
                        """);
                }

                if (url.Contains("/movie/129?", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse("""
                        {
                          "id": 129,
                          "overview": "An English TMDB detail overview.",
                          "tagline": "The tunnel led somewhere unexpected.",
                          "runtime": 125
                        }
                        """);
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));

        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        var claims = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Movies,
            Title = "Spirited Away",
            Language = "en",
            Country = "US",
        });

        Assert.Contains(requestedUrls, url => url.Contains("language=en-US", StringComparison.Ordinal));
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.ShortDescription
            && c.Value == "An English TMDB overview.");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.OriginalLanguage
            && c.Value == "ja");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.Tagline
            && c.Value == "The tunnel led somewhere unexpected."
            && c.Confidence >= 0.9);
        Assert.DoesNotContain(claims, c => c.Key == MetadataFieldConstants.Language);
    }

    [Fact]
    public async Task Tmdb_MovieSearch_AddsCollectionSequenceFactsFromCollectionParts()
    {
        var config = LoadExampleConfig("tmdb");
        config.HttpClient ??= new HttpClientConfig();
        config.HttpClient.ApiKey = "test-key";

        var requestedUrls = new List<string>();
        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? string.Empty;
                requestedUrls.Add(url);

                if (url.Contains("/search/movie?", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse("""
                        {
                          "results": [
                            {
                              "id": 78,
                              "title": "Blade Runner",
                              "overview": "A future noir.",
                              "release_date": "1982-06-25"
                            }
                          ]
                        }
                        """);
                }

                if (url.Contains("/movie/78?", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse("""
                        {
                          "id": 78,
                          "title": "Blade Runner",
                          "overview": "A detail overview.",
                          "runtime": 117,
                          "belongs_to_collection": {
                            "id": 422837,
                            "name": "Blade Runner Collection"
                          }
                        }
                        """);
                }

                if (url.Contains("/collection/422837?", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse("""
                        {
                          "id": 422837,
                          "name": "Blade Runner Collection",
                          "parts": [
                            {
                              "id": 335984,
                              "title": "Blade Runner 2049",
                              "release_date": "2017-10-06"
                            },
                            {
                              "id": 78,
                              "title": "Blade Runner",
                              "release_date": "1982-06-25"
                            }
                          ]
                        }
                        """);
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));

        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        var claims = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Movies,
            Title = "Blade Runner",
            Year = "1982",
            Language = "en",
            Country = "US",
        });

        Assert.Contains(requestedUrls, url => url.Contains("/collection/422837?", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(claims, c => c.Key == "tmdb_collection_id" && c.Value == "422837");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.Series && c.Value == "Blade Runner Collection");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.SeriesPosition && c.Value == "1");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.SequenceTotal && c.Value == "2");
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.SequenceTotalScope && c.Value == SequenceCountScope.MainSequence.ToString());
        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.SequenceFormat && c.Value == SequenceFormat.Standard.ToString());
        var manifestClaim = Assert.Single(claims, c => c.Key == MetadataFieldConstants.SequenceManifestJson);
        var manifest = JsonSerializer.Deserialize<ProviderSequenceManifest>(manifestClaim.Value);
        Assert.NotNull(manifest);
        Assert.Equal("tmdb:collection:422837", manifest.ContainerId);
        Assert.True(manifest.IsAuthoritative);
        Assert.Equal(["78", "335984"], manifest.Items.Select(item => item.ExternalId));
        Assert.Equal(["1", "2"], manifest.Items.Select(item => item.Ordinal));
    }

    [Fact]
    public async Task Tmdb_MovieSearch_PreservesCastProfileHints()
    {
        var config = LoadExampleConfig("tmdb");
        config.HttpClient ??= new HttpClientConfig();
        config.HttpClient.ApiKey = "test-key";

        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? string.Empty;

                if (url.Contains("/search/movie?", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse("""
                        {
                          "results": [
                            {
                              "id": 1001,
                              "title": "Test Movie",
                              "overview": "Overview",
                              "release_date": "2024-01-01"
                            }
                          ]
                        }
                        """);
                }

                if (url.Contains("/movie/1001?", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse("""
                        {
                          "id": 1001,
                          "overview": "Detail overview",
                          "credits": {
                            "cast": [
                              {
                                "id": 12345,
                                "name": "Cosmo Jarvis",
                                "character": "John Blackthorne",
                                "order": 0,
                                "profile_path": "/cosmo.jpg"
                              }
                            ],
                            "crew": [
                              {
                                "id": 98765,
                                "name": "Jane Director",
                                "job": "Director",
                                "profile_path": "/jane.jpg"
                              }
                            ]
                          },
                          "production_companies": [
                            {
                              "name": "FX Productions",
                              "logo_path": "/fx.png"
                            }
                          ],
                          "external_ids": {
                            "imdb_id": "tt1234567",
                            "tvdb_id": 7654,
                            "wikidata_id": "Q123456"
                          }
                        }
                        """);
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));

        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        var claims = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Movies,
            Title = "Test Movie",
            Language = "en",
            Country = "US",
        });

        Assert.Contains(claims, c => c.Key == MetadataFieldConstants.CastMember
            && c.Value == "Cosmo Jarvis");
        Assert.Contains(claims, c => c.Key == "cast_member_tmdb_id"
            && c.Value == "12345");
        Assert.Contains(claims, c => c.Key == "cast_member_profile_url"
            && c.Value == "https://image.tmdb.org/t/p/original/cosmo.jpg");
        Assert.Contains(claims, c => c.Key == "cast_member_character"
            && c.Value == "John Blackthorne");
        Assert.Contains(claims, c => c.Key == "director"
            && c.Value == "Jane Director");
        Assert.Contains(claims, c => c.Key == "director_tmdb_id"
            && c.Value == "98765");
        Assert.Contains(claims, c => c.Key == "director_profile_url"
            && c.Value == "https://image.tmdb.org/t/p/original/jane.jpg");
        Assert.Contains(claims, c => c.Key == "studio"
            && c.Value == "FX Productions");
        Assert.Contains(claims, c => c.Key == "studio_logo_url"
            && c.Value == "https://image.tmdb.org/t/p/original/fx.png");
        Assert.Contains(claims, c => c.Key == "production_company"
            && c.Value == "FX Productions");
        Assert.Contains(claims, c => c.Key == BridgeIdKeys.ImdbId
            && c.Value == "tt1234567");
        Assert.Contains(claims, c => c.Key == BridgeIdKeys.TvdbId
            && c.Value == "7654");
        Assert.Contains(claims, c => c.Key == BridgeIdKeys.WikidataQid
            && c.Value == "Q123456");
    }

    [Fact]
    public async Task ComicVine_SearchAsync_VolumeSearch_ExposesVolumeIdentifierForRunAlignment()
    {
        var config = LoadExampleConfig("comicvine");
        config.HttpClient ??= new HttpClientConfig();
        config.HttpClient.ApiKey = "test-key";
        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(_ => JsonResponse("""
                {
                  "results": [
                    {
                      "id": 987,
                      "name": "Retail Comic Run",
                      "start_year": "2024",
                      "publisher": { "name": "Example Comics" }
                    }
                  ]
                }
                """)));
        var adapter = new ConfigDrivenAdapter(
            config,
            factory,
            NullLogger<ConfigDrivenAdapter>.Instance,
            NullProviderHealthMonitor.Instance);

        var result = Assert.Single(await adapter.SearchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.Work,
            MediaType = MediaType.Comics,
            Series = "Retail Comic Run",
            BaseUrl = "https://comicvine.gamespot.com/api",
        }));

        Assert.Equal("987", result.ProviderItemId);
        Assert.Equal("987", Assert.IsType<Dictionary<string, string>>(result.ExtraFields)[BridgeIdKeys.ComicVineVolumeId]);
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static ProviderConfiguration LoadExampleConfig(string providerName)
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "config", "providers", $"{providerName}.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ProviderConfiguration>(json, s_jsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize config: {providerName}");
    }

    private static string FindRepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(AdapterFallbackTests).Assembly.Location);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not find repository root (.git directory)");
    }

    // ── Stub HTTP helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Builds an <see cref="IHttpClientFactory"/> that routes the named client
    /// through a stub handler returning <paramref name="statusCode"/>.
    /// </summary>
    private static IHttpClientFactory BuildFactory(
        string clientName,
        HttpStatusCode statusCode,
        Action<HttpRequestMessage>? onRequest = null)
    {
        var handler = new StubHttpMessageHandler(statusCode, onRequest);
        return BuildFactory(clientName, handler);
    }

    private static IHttpClientFactory BuildFactory(
        string clientName,
        HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(clientName)
                .ConfigurePrimaryHttpMessageHandler(() => handler);
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IHttpClientFactory>();
    }

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    /// <summary>
    /// Builds an <see cref="IHttpClientFactory"/> whose named client throws
    /// <see cref="TaskCanceledException"/> on every request.
    /// </summary>
    private static IHttpClientFactory BuildTimeoutFactory(string clientName)
    {
        var handler = new TimeoutStubHttpMessageHandler();
        var services = new ServiceCollection();
        services.AddHttpClient(clientName)
                .ConfigurePrimaryHttpMessageHandler(() => handler);
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IHttpClientFactory>();
    }
}

// ── Stub HTTP handlers ────────────────────────────────────────────────────────

/// <summary>
/// Returns a fixed <see cref="HttpStatusCode"/> with an empty body for every request.
/// </summary>
file sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly Action<HttpRequestMessage>? _onRequest;

    public StubHttpMessageHandler(
        HttpStatusCode statusCode,
        Action<HttpRequestMessage>? onRequest = null)
    {
        _statusCode = statusCode;
        _onRequest = onRequest;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _onRequest?.Invoke(request);
        return Task.FromResult(new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(string.Empty),
        });
    }
}

/// <summary>
/// Throws <see cref="TaskCanceledException"/> on every request to simulate a timeout.
/// </summary>
file sealed class TimeoutStubHttpMessageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
        => throw new TaskCanceledException("Simulated HTTP timeout in test.");
}

/// <summary>
/// Routes requests to a caller-supplied responder so tests can return different
/// payloads for different URLs without touching the network.
/// </summary>
file sealed class RoutingStubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public RoutingStubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => _responder = responder;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
        => Task.FromResult(_responder(request));
}

/// <summary>
/// Minimal <see cref="IConfigurationLoader"/> stub for adapter tests.
/// Returns defaults for all methods — no file I/O.
/// </summary>
file sealed class StubConfigurationLoader : IConfigurationLoader
{
    public CoreConfiguration LoadCore() => new();
    public void SaveCore(CoreConfiguration config) { }
    public ScoringSettings LoadScoring() => new();
    public void SaveScoring(ScoringSettings settings) { }
    public MaintenanceSettings LoadMaintenance() => new();
    public void SaveMaintenance(MaintenanceSettings settings) { }
    public HydrationSettings LoadHydration() => new();
    public void SaveHydration(HydrationSettings settings) { }
    public PipelineConfiguration LoadPipelines() => new();
    public void SavePipelines(PipelineConfiguration config) { }
    public DisambiguationSettings LoadDisambiguation() => new();
    public void SaveDisambiguation(DisambiguationSettings settings) { }
    public MediaTypeConfiguration LoadMediaTypes() => new();
    public void SaveMediaTypes(MediaTypeConfiguration config) { }
    public TranscodingSettings LoadTranscoding() => new();
    public void SaveTranscoding(TranscodingSettings settings) { }
    public FieldPriorityConfiguration LoadFieldPriorities() => new();
    public void SaveFieldPriorities(FieldPriorityConfiguration config) { }
    public LibrariesConfiguration LoadLibraries() => new();
    public ProviderConfiguration? LoadProvider(string name) => null;
    public void SaveProvider(ProviderConfiguration config) { }
    public IReadOnlyList<ProviderConfiguration> LoadAllProviders() => [];
    public T? LoadConfig<T>(string subdirectory, string name) where T : class => null;
    public void SaveConfig<T>(string subdirectory, string name, T config) where T : class { }
    public T? LoadAi<T>() where T : class => default;
    public void SaveAi<T>(T settings) where T : class { }
    public PaletteConfiguration LoadPalette() => new();
    public void SavePalette(PaletteConfiguration palette) { }
}

/// <summary>No-op QID label repository for adapter tests.</summary>
file sealed class NoOpQidLabelRepository : IQidLabelRepository
{
    public Task<string?> GetLabelAsync(string qid, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<IReadOnlyDictionary<string, string>> GetLabelsAsync(IEnumerable<string> qids, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    public Task UpsertAsync(string qid, string label, string? description, string? entityType, CancellationToken ct = default) => Task.CompletedTask;
    public Task UpsertBatchAsync(IReadOnlyList<QidLabel> labels, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<QidLabel>> GetLabelDetailsAsync(IEnumerable<string> qids, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<QidLabel>>([]);
    public Task<IReadOnlyList<QidLabel>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<QidLabel>>([]);
}

/// <summary>No-op resolver cache for adapter tests.</summary>
file sealed class NoOpResolverCacheRepository : IResolverCacheRepository
{
    public Task<ResolverCacheEntry?> FindAsync(string cacheKey, CancellationToken ct = default) => Task.FromResult<ResolverCacheEntry?>(null);
    public Task UpsertAsync(ResolverCacheEntry entry, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> PurgeExpiredAsync(CancellationToken ct = default) => Task.FromResult(0);
}

/// <summary>No-op provider response cache for adapter tests.</summary>
file sealed class NoOpProviderResponseCacheRepository : IProviderResponseCacheRepository
{
    public Task<CachedResponse?> FindAsync(string cacheKey, CancellationToken ct = default) => Task.FromResult<CachedResponse?>(null);
    public Task UpsertAsync(string cacheKey, string providerId, string queryHash, string responseJson, string? etag, int ttlHours, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string?> FindExpiredEtagAsync(string cacheKey, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task RefreshExpiryAsync(string cacheKey, int ttlHours, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> PurgeExpiredAsync(CancellationToken ct = default) => Task.FromResult(0);
    public Task<int> ClearAllAsync(CancellationToken ct = default) => Task.FromResult(0);
    public Task<CacheStats> GetStatsAsync(CancellationToken ct = default) => Task.FromResult(new CacheStats(0, 0, null));
}
