using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.ApplicationServices;
using MediaEngine.Contracts.Universe;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Plugin.FandomLore;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Fandom lore plugin may only look up a Universe the Library already holds, and may only talk to Wikidata and
/// Fandom wikis. Nothing about the user's titles, authors or files is ever in a request (spec universe.md D5, U-WP9).
/// </summary>
public sealed class UniverseLorePrivacyTests : IDisposable
{
    private const string HeldQid = "Q4242";
    private const string UnheldQid = "Q999999";

    private const string WikidataAnswer = """
        {"claims":{"P6262":[
          {"mainsnak":{"datavalue":{"value":"starwars:Luke_Skywalker"}}},
          {"mainsnak":{"datavalue":{"value":"https://evil.example.com/wiki/Luke"}}}
        ]}}
        """;

    private const string FandomAnswer = """
        {"query":{"pages":[
          {"pageid":11,"title":"Luke Skywalker","fullurl":"https://starwars.fandom.com/wiki/Luke_Skywalker",
           "categories":[{"title":"Category:Characters"}],"templates":[]}
        ]}}
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-lore-privacy-tests", Guid.NewGuid().ToString("N"));
    private readonly DatabaseConnection _database;
    private readonly ConfigurationDirectoryLoader _loader;
    private readonly RecordingHandler _handler;
    private readonly PluginUniverseLoreService _service;
    private readonly PluginLoreRepository _loreRepository;
    private readonly FandomLoreDiscoveryOperation _operation;

    public UniverseLorePrivacyTests()
    {
        DapperConfiguration.Configure();
        Directory.CreateDirectory(_root);
        _database = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();

        var config = Path.Combine(_root, "config");
        var library = Path.Combine(_root, "library");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(library);
        _loader = new ConfigurationDirectoryLoader(config);
        _loader.SaveCore(new MediaEngine.Domain.Configuration.CoreConfiguration { LibraryRoot = library });
        var catalog = new PluginCatalog(
            [new FandomLorePlugin()],
            new PluginSettingsService(_loader),
            _loader,
            AllowAllPluginApprovals.Instance,
            NullLogger<PluginCatalog>.Instance);
        catalog.SetEnabled(FandomLorePlugin.PluginId, true);

        _handler = new RecordingHandler(Answer);
        var contexts = new RecordingContextFactory(new HttpClient(_handler, disposeHandler: false));
        var roots = new NarrativeRootRepository(_database);
        var gate = new LibraryUniverseQidGate(roots, new CollectionRepository(_database));
        _loreRepository = new PluginLoreRepository(_database);
        _service = new PluginUniverseLoreService(
            catalog, _loreRepository, roots, gate, contexts, NullLogger<PluginUniverseLoreService>.Instance);
        _operation = new FandomLoreDiscoveryOperation(catalog, contexts, gate);

        roots.UpsertAsync(new NarrativeRoot
        {
            Qid = HeldQid,
            Label = "Zephyrine Cycle Universe",
            Level = "Universe",
            CreatedAt = DateTimeOffset.UtcNow,
        }).GetAwaiter().GetResult();
        SeedUniverseWithWorks();
    }

    public void Dispose()
    {
        _database.Dispose();
        _loader.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task LoreDiscovery_ForAQidTheLibraryDoesNotHold_SendsNothing()
    {
        Assert.Null(await _service.DiscoverSourcesAsync(UnheldQid));
        Assert.Null(await _service.EnrichUniverseAsync(UnheldQid));
        Assert.Null(await _service.AddManualSourceAsync(UnheldQid, "Star Wars", "https://starwars.fandom.com"));

        var refused = await Assert.ThrowsAsync<PluginApplicationGatewayException>(async () =>
            await _operation.InvokeAsync(JsonDocument.Parse($"{{\"universe_qid\":\"{UnheldQid}\"}}").RootElement, CancellationToken.None));
        Assert.Equal(PluginApplicationGatewayFailure.InvalidPayload, refused.Failure);

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task LoreDiscovery_ForAQidTheLibraryDoesNotHold_AnswersNotFoundFromEveryEndpoint()
    {
        using var host = LoreEndpointHost.Create(_service);

        Assert.Equal(StatusCodes.Status404NotFound, (await host.PostAsync($"/universe/{UnheldQid}/lore-sources/discover")).Status);
        Assert.Equal(StatusCodes.Status404NotFound, (await host.PostAsync($"/universe/{UnheldQid}/lore/enrich")).Status);
        Assert.Equal(StatusCodes.Status404NotFound, (await host.PostAsync(
            $"/universe/{UnheldQid}/lore-sources/manual",
            new UniverseLoreManualSourceRequest("Star Wars", "https://starwars.fandom.com"))).Status);

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task LoreDiscovery_ForALibraryUniverse_SendsOnlyItsQid_ToWikidata()
    {
        var sources = await _service.DiscoverSourcesAsync(HeldQid);

        Assert.NotNull(sources);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("www.wikidata.org", request.Uri.Host);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            $"?action=wbgetclaims&format=json&entity={HeldQid}&property=P6262",
            request.Uri.Query);
        Assert.Equal(string.Empty, request.Body);
        var source = Assert.Single(sources!);
        Assert.Equal("starwars.fandom.com", source.SourceKey);
    }

    [Fact]
    public async Task LoreDiscovery_ForARootUniverseCollectionTheLibraryHolds_IsAllowed()
    {
        var sources = await _service.DiscoverSourcesAsync("Q777");

        Assert.NotNull(sources);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task ADiscoveredNonFandomAddress_IsIgnored()
    {
        var sources = await _service.DiscoverSourcesAsync(HeldQid);

        // The Wikidata answer carried a Fandom key and a foreign address; only the Fandom wiki is kept and nothing foreign is called.
        var source = Assert.Single(sources!);
        Assert.Equal("https://starwars.fandom.com", source.BaseUrl);
        Assert.DoesNotContain(_handler.Requests, request => request.Uri.Host.Contains("evil", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://evil.example.com/wiki/Luke")]
    [InlineData("https://fandom.com.evil.example.com/wiki/Luke")]
    [InlineData("http://fandom.com@evil.example.com/")]
    [InlineData("https://evil.example.com/fandom.com")]
    [InlineData("evil.example.com/x:Page")]
    [InlineData("evil.example.com@fandom.com:Page")]
    public async Task ADiscoveredNonFandomAddress_IsIgnored_ByTheProvider(string p6262Value)
    {
        var context = new RecordingContext(new HttpClient(new RecordingHandler(_ => Json(
            JsonSerializer.Serialize(new
            {
                claims = new { P6262 = new[] { new { mainsnak = new { datavalue = new { value = p6262Value } } } } },
            }))), disposeHandler: true));

        var candidates = await new FandomUniverseLoreProvider().DiscoverSourcesAsync(
            new PluginUniverseLoreContext(HeldQid, null), context);

        Assert.Empty(candidates);
    }

    [Theory]
    [InlineData("https://starwars.fandom.com/wiki/Luke", "starwars.fandom.com")]
    [InlineData("http://starwars.fandom.com/wiki/Luke", "starwars.fandom.com")]
    [InlineData("starwars:Luke_Skywalker", "starwars.fandom.com")]
    [InlineData("memory-alpha.fandom.com:Spock", "memory-alpha.fandom.com")]
    public async Task ADiscoveredFandomAddress_IsKept_OnHttps(string p6262Value, string expectedHost)
    {
        var context = new RecordingContext(new HttpClient(new RecordingHandler(_ => Json(
            JsonSerializer.Serialize(new
            {
                claims = new { P6262 = new[] { new { mainsnak = new { datavalue = new { value = p6262Value } } } } },
            }))), disposeHandler: true));

        var candidate = Assert.Single(await new FandomUniverseLoreProvider().DiscoverSourcesAsync(
            new PluginUniverseLoreContext(HeldQid, null), context));

        Assert.Equal($"https://{expectedHost}", candidate.BaseUrl);
        Assert.Equal($"https://{expectedHost}/api.php", candidate.ApiUrl);
    }

    [Theory]
    [InlineData("https://evil.example.com", null)]
    [InlineData("https://fandom.com.evil.example.com", null)]
    [InlineData("evil.example.com/fandom.com", null)]
    [InlineData("https://starwars.fandom.com", "https://evil.example.com/api.php")]
    [InlineData("https://starwars.fandom.com", "http://starwars.fandom.com/api.php")]
    public async Task AManuallyAddedNonFandomAddress_IsRefused(string baseUrl, string? apiUrl)
    {
        var refused = await Assert.ThrowsAsync<UriFormatException>(async () =>
            await _service.AddManualSourceAsync(HeldQid, "Anything", baseUrl, apiUrl));

        Assert.Equal("Only Fandom wiki addresses can be added.", refused.Message);
        Assert.Empty(await _service.GetSourcesAsync(HeldQid));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task AManuallyAddedNonFandomAddress_IsRefusedWithAPlain400_ByTheEndpoint()
    {
        using var host = LoreEndpointHost.Create(_service);

        var response = await host.PostAsync(
            $"/universe/{HeldQid}/lore-sources/manual",
            new UniverseLoreManualSourceRequest("Evil", "https://evil.example.com"));

        Assert.Equal(StatusCodes.Status400BadRequest, response.Status);
        Assert.Contains("Only Fandom wiki addresses can be added.", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AManuallyAddedFandomAddress_IsStoredOnHttps()
    {
        var source = await _service.AddManualSourceAsync(HeldQid, "Star Wars", "starwars.fandom.com");

        Assert.NotNull(source);
        Assert.Equal("https://starwars.fandom.com", source!.BaseUrl);
        Assert.Equal("https://starwars.fandom.com/api.php", source.ApiUrl);
    }

    [Fact]
    public async Task AStoredNonFandomSource_IsNotCalled()
    {
        var source = await _loreRepository.AddManualSourceAsync(
            HeldQid, FandomPluginId, "Evil", "https://evil.example.com", "https://evil.example.com/api.php");
        await _service.SetSourceStatusAsync(HeldQid, source.Id, PluginLoreSourceStatus.Approved, "test");

        var summary = await _service.EnrichUniverseAsync(HeldQid);

        Assert.NotNull(summary);
        Assert.Equal(0, summary!.SourcesEnriched);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task NoTitleListLeavesTheMachine()
    {
        // Distinctive Work titles, authors and file names live in the Library; none may appear in any outbound request.
        var sources = await _service.DiscoverSourcesAsync(HeldQid);
        var source = Assert.Single(sources!);
        await _service.SetSourceStatusAsync(HeldQid, source.Id, PluginLoreSourceStatus.Approved, "test");
        var summary = await _service.EnrichUniverseAsync(HeldQid);
        var viaApplicationService = await _operation.InvokeAsync(
            JsonDocument.Parse($"{{\"universe_qid\":\"{HeldQid}\"}}").RootElement, CancellationToken.None);

        Assert.NotNull(summary);
        Assert.Equal(1, summary!.SourcesEnriched);
        Assert.IsType<MediaEngine.Contracts.Plugins.DiscoverUniverseLoreSourcesResponse>(viaApplicationService);
        Assert.Equal(3, _handler.Requests.Count);

        Assert.All(_handler.Requests, request =>
        {
            Assert.True(
                request.Uri.Host == "www.wikidata.org" || request.Uri.Host.EndsWith(".fandom.com", StringComparison.Ordinal),
                $"Unexpected host {request.Uri.Host}");
            Assert.Equal("https", request.Uri.Scheme);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(string.Empty, request.Body);

            var everything = string.Join(
                "\n",
                request.Uri.AbsoluteUri,
                Uri.UnescapeDataString(request.Uri.AbsoluteUri),
                request.Headers,
                request.Body).ToLowerInvariant();
            foreach (var secret in SecretsInTheLibrary)
            {
                Assert.DoesNotContain(secret.ToLowerInvariant(), everything, StringComparison.Ordinal);
                Assert.DoesNotContain(Uri.EscapeDataString(secret).ToLowerInvariant(), everything, StringComparison.Ordinal);
            }
        });
    }

    private const string FandomPluginId = PluginUniverseLoreService.FandomPluginId;

    private static readonly string[] SecretsInTheLibrary =
    [
        "Zyxwvut Chronicles",
        "Quillfeather Saga",
        "Ottoline Brightwater",
        "Marguerite Vandersloot",
        "Zephyrine Cycle",
        "zyxwvut-chronicles-01.epub",
        "quillfeather-saga-02.m4b",
    ];

    private static HttpResponseMessage Answer(HttpRequestMessage request) =>
        request.RequestUri!.Host == "www.wikidata.org"
            ? Json(WikidataAnswer)
            : Json(FandomAnswer);

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private void SeedUniverseWithWorks()
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO collections (id, collection_type, display_name, wikidata_qid)
            VALUES (@universe, 'Universe', 'Zephyrine Cycle', 'Q777');
            """,
            new { universe = Guid.NewGuid() });

        foreach (var (title, author, file) in new[]
                 {
                     ("Zyxwvut Chronicles", "Ottoline Brightwater", "zyxwvut-chronicles-01.epub"),
                     ("Quillfeather Saga", "Marguerite Vandersloot", "quillfeather-saga-02.m4b"),
                 })
        {
            var work = Guid.NewGuid();
            var edition = Guid.NewGuid();
            var asset = Guid.NewGuid();
            connection.Execute(
                """
                INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@work, 'Books', 'standalone', 'accepted');
                INSERT INTO editions (id, work_id) VALUES (@edition, @work);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at)
                VALUES (@asset, @edition, @hash, @path, CURRENT_TIMESTAMP);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@work, 'title', @title, CURRENT_TIMESTAMP),
                       (@work, 'author', @author, CURRENT_TIMESTAMP);
                """,
                new
                {
                    work,
                    edition,
                    asset,
                    title,
                    author,
                    hash = Guid.NewGuid().ToString("N"),
                    path = $@"C:\Library\Books\{file}",
                });
        }
    }

    private sealed record RecordedRequest(Uri Uri, HttpMethod Method, string Headers, string Body);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.RequestUri!, request.Method, request.Headers.ToString(), body));
            return answer(request);
        }
    }

    private sealed class RecordingHttp(HttpClient client) : IPluginHttpClient
    {
        public Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
            CancellationToken cancellationToken = default) =>
            client.SendAsync(request, completionOption, cancellationToken);
    }

    private sealed class RecordingContextFactory(HttpClient client) : IPluginExecutionContextFactory
    {
        public IPluginExecutionContext Create(string pluginId, string purpose) => new RecordingContext(client, disposeClient: false);

        public IPluginExecutionContext CreateForMedia(string pluginId, string purpose, PluginMediaAssetContext asset) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingContext(HttpClient client, bool disposeClient = false) : IPluginExecutionContext
    {
        public string PluginId => FandomPluginId;

        public IReadOnlyDictionary<string, JsonElement> Settings { get; } = new Dictionary<string, JsonElement>
        {
            ["request_delay_ms"] = JsonSerializer.SerializeToElement(100),
        };

        public IPluginMediaAccess Media => throw new NotSupportedException();
        public IPluginHttpClient Http { get; } = new RecordingHttp(client);
        public IPluginStorage Storage => throw new NotSupportedException();
        public IPluginToolRuntime Tools => throw new NotSupportedException();
        public IPluginAiClient Ai => throw new NotSupportedException();
        public IPluginSecrets Secrets => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            if (disposeClient)
            {
                client.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class HasABody : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed record EndpointResponse(int Status, string Body);

    /// <summary>Runs the real lore endpoint handlers (without the authorization middleware, which only reads metadata).</summary>
    private sealed class LoreEndpointHost : IDisposable
    {
        private readonly WebApplication _app;

        private LoreEndpointHost(WebApplication app) => _app = app;

        public static LoreEndpointHost Create(PluginUniverseLoreService lore)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton(lore);
            var app = builder.Build();
            app.MapUniverseLoreEndpoints();
            return new LoreEndpointHost(app);
        }

        public async Task<EndpointResponse> PostAsync(string path, object? body = null)
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var endpoint = ((IEndpointRouteBuilder)_app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Single(candidate =>
                    candidate.RoutePattern.RawText == "/universe/{qid}/" + string.Join('/', segments.Skip(2))
                    && candidate.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("POST"));

            var context = new DefaultHttpContext { RequestServices = _app.Services };
            context.Request.Method = "POST";
            context.Request.RouteValues["qid"] = segments[1];
            if (body is not null)
            {
                context.Request.ContentType = "application/json";
                var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
                context.Request.ContentLength = bytes.Length;
                context.Request.Body = new MemoryStream(bytes);
                context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new HasABody());
            }

            var response = new MemoryStream();
            context.Response.Body = response;
            await endpoint.RequestDelegate!(context);
            response.Position = 0;
            return new EndpointResponse(context.Response.StatusCode, new StreamReader(response).ReadToEnd());
        }

        public void Dispose() => _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
