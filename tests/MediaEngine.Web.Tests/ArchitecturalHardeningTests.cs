using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Integration.Clients;
using MediaEngine.Web.Services.Playback;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class ArchitecturalHardeningTests
{
    [Fact]
    public void ArtworkProxy_ForwardsLiveHumanContextForResourceAuthorization()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src/MediaEngine.Web/Program.cs"));

        Assert.Contains("AddHttpClient(\"EngineArtwork\", ConfigureEngineClient)", source, StringComparison.Ordinal);
        Assert.Contains("CreateClient(\"EngineArtwork\")", source, StringComparison.Ordinal);
        Assert.Contains(
            "AddHttpClient(\"EngineArtwork\", ConfigureEngineClient)\n    .AddHttpMessageHandler<DashboardEngineAuthenticationHandler>()",
            source.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    private static readonly string RepoRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void IntegrationClients_AreRegisteredWithConfiguredEngineHttpClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<MediaEngine.Web.Services.Branding.StreamingServiceLogoResolver>();
        services.AddHttpClient<IEngineApiClient, EngineApiClient>(client =>
        {
            client.BaseAddress = new Uri("http://engine.test");
            client.DefaultRequestHeaders.Add("X-Api-Key", "secret");
        });

        using var provider = services.BuildServiceProvider();
        var client = Assert.IsType<EngineApiClient>(provider.GetRequiredService<IEngineApiClient>());

        Assert.Equal("http://engine.test/cover", client.ToAbsoluteEngineUrl("/cover"));
    }

    [Fact]
    public async Task EngineApiClient_DelegatesSystemStatusAndPreservesFailureSnapshot()
    {
        using var httpClient = CreateHttpClient(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("Engine warming up", Encoding.UTF8, "text/plain"),
            });
        var client = new EngineApiClient(httpClient, NullLogger<EngineApiClient>.Instance);

        var status = await client.GetSystemStatusAsync();

        Assert.Null(status);
        Assert.Equal("GET /system/status", client.LastFailedEndpoint);
        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, client.LastStatusCode);
        Assert.Equal("http_failure", client.LastFailureKind);
        Assert.Contains("Engine warming up", client.LastError);
    }

    [Fact]
    public async Task AuthenticationSettingsPut_UsesSharedFailureEnvelope()
    {
        using var httpClient = CreateHttpClient(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("Administrator unlock required", Encoding.UTF8, "text/plain"),
            });
        var client = new EngineApiClient(httpClient, NullLogger<EngineApiClient>.Instance);

        Assert.Null(await client.UpdateAuthSettingsAsync(new UpdateAuthSettingsRequest()));

        Assert.Equal("PUT /settings/security/auth", client.LastFailedEndpoint);
        Assert.Equal((int)HttpStatusCode.Forbidden, client.LastStatusCode);
        Assert.Equal("unauthorized", client.LastFailureKind);
        Assert.Contains("Administrator unlock required", client.LastError);
    }

    [Fact]
    public async Task EngineApiClient_DelegatesProviderCatalogue()
    {
        const string json = """
            [
              { "providerId": "open-library", "name": "open_library", "displayName": "Open Library", "enabled": true }
            ]
            """;
        using var httpClient = CreateHttpClient(request =>
        {
            Assert.Equal("http://localhost:61495/providers/catalogue", request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });
        var client = new EngineApiClient(httpClient, NullLogger<EngineApiClient>.Instance);

        var catalogue = await client.GetProviderCatalogueAsync();

        var provider = Assert.Single(catalogue);
        Assert.Equal("open_library", provider.Name);
        Assert.Equal("Open Library", provider.DisplayName);
    }

    [Fact]
    public async Task EngineApiClient_NormalizesDisplayPreviewItemArtworkUrls()
    {
        var workId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var card = new DisplayCardDto(
            Id: workId,
            WorkId: workId,
            AssetId: null,
            CollectionId: null,
            MediaType: "Book",
            GroupingType: "work",
            Title: "Foundation",
            Subtitle: null,
            Facts: [],
            Artwork: EmptyDisplayArtwork(),
            PreferredShape: "portrait",
            Presentation: "book",
            TileTextMode: "caption",
            PreviewPlacement: "smart",
            Progress: null,
            Actions: [],
            Flags: new DisplayCardFlagsDto(false, true, false, false),
            SortTimestamp: DateTimeOffset.Parse("2026-06-01T12:00:00Z"))
        {
            PreviewItems =
            [
                new DisplayCardPreviewItemDto(
                    WorkId: workId,
                    AssetId: null,
                    Title: "Foundation",
                    ImageUrl: "/stream/artwork/11111111-1111-1111-1111-111111111111?size=s",
                    Shape: "portrait",
                    Position: "1"),
            ],
        };
        var page = new DisplayPageDto(
            Key: "read",
            Title: "Read",
            Subtitle: null,
            Hero: null,
            Shelves: [new DisplayShelfDto("series", "Series", null, [card], null)],
            Catalog: [card]);

        using var httpClient = CreateHttpClient(request =>
        {
            Assert.StartsWith("http://localhost:61495/api/v1/display/browse", request.RequestUri!.ToString(), StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(page, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json"),
            };
        });
        var client = new EngineApiClient(httpClient, NullLogger<EngineApiClient>.Instance);

        var result = await client.GetDisplayBrowseAsync(lane: "read", includeCatalog: true);

        Assert.NotNull(result);
        Assert.Equal(
            "/engine-image/stream/artwork/11111111-1111-1111-1111-111111111111?size=s",
            result.Catalog.Single().PreviewItems.Single().ImageUrl);
        Assert.Equal(
            "/engine-image/stream/artwork/11111111-1111-1111-1111-111111111111?size=s",
            result.Shelves.Single().Items.Single().PreviewItems.Single().ImageUrl);
    }

    [Fact]
    public async Task ProviderCatalogueService_UsesMemoryCacheAndInvalidates()
    {
        var api = CountingEngineApiClient.Create();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ProviderCatalogueService(api.Client, cache);

        var first = await service.GetCatalogueAsync();
        var second = await service.GetCatalogueAsync();
        service.Invalidate();
        var third = await service.GetCatalogueAsync();

        Assert.Same(first, second);
        Assert.NotSame(first, third);
        Assert.Equal(2, api.CatalogueCalls);
    }

    [Fact]
    public async Task ProviderCatalogueService_BuildsExactSourceLinksFromCatalogueConfiguration()
    {
        var api = CountingEngineApiClient.Create();
        api.Catalogue =
        [
            new ProviderCatalogueDto
            {
                Name = "musicbrainz",
                DisplayName = "MusicBrainz",
                ExternalLinks = new Dictionary<string, ProviderExternalLinkDto>(StringComparer.OrdinalIgnoreCase)
                {
                    ["musicbrainz_release_id"] = new()
                    {
                        Label = "MusicBrainz Release",
                        UrlTemplate = "https://musicbrainz.org/release/{value}",
                    },
                },
            },
            new ProviderCatalogueDto
            {
                Name = "wikidata_reconciliation",
                DisplayName = "Wikidata",
                ExternalLinks = new Dictionary<string, ProviderExternalLinkDto>(StringComparer.OrdinalIgnoreCase)
                {
                    ["wikidata_qid"] = new()
                    {
                        Label = "Wikidata",
                        UrlTemplate = "https://www.wikidata.org/wiki/{value}",
                    },
                },
            },
        ];
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ProviderCatalogueService(api.Client, cache);
        await service.GetCatalogueAsync();

        var links = service.GetExternalUrls(new Dictionary<string, string>
        {
            ["musicbrainz_release_id"] = "d6dbf151-f092-4df7-89ab-f0da3ebe539c",
            ["wikidata_qid"] = "Q153607",
        });

        Assert.Contains(links, link =>
            link.Label == "MusicBrainz Release"
            && link.Url == "https://musicbrainz.org/release/d6dbf151-f092-4df7-89ab-f0da3ebe539c");
        Assert.Contains(links, link =>
            link.Label == "Wikidata"
            && link.Url == "https://www.wikidata.org/wiki/Q153607");
    }

    [Fact]
    public void ProviderCatalogueService_IsScopedBecauseItUsesCircuitScopedEngineApiState()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot, "src/MediaEngine.Web/Program.cs"));

        Assert.Contains("AddScoped<ProviderCatalogueService>", program);
        Assert.DoesNotContain("AddSingleton<ProviderCatalogueService>", program);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<MediaEngine.Web.Services.Branding.StreamingServiceLogoResolver>();
        services.AddScoped<EngineApiFailureState>();
        services.AddHttpClient<IEngineApiClient, EngineApiClient>(client =>
        {
            client.BaseAddress = new Uri("http://engine.test");
        });
        services.AddScoped<ProviderCatalogueService>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ProviderCatalogueService>());
    }

    [Fact]
    public void Dashboard_ProxiesEngineMediaStreamsForBrowserPlayback()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot, "src/MediaEngine.Web/Program.cs"));
        var playback = File.ReadAllText(Path.Combine(RepoRoot, "src/MediaEngine.Web/Services/Playback/PlaybackSessionController.cs"));
        var host = File.ReadAllText(Path.Combine(RepoRoot, "src/MediaEngine.Web/Components/Listen/ListenNowPlayingBar.razor"));

        Assert.Contains("app.MapMethods(\"/engine-stream/{assetId:guid}\"", program, StringComparison.Ordinal);
        Assert.Contains("HttpCompletionOption.ResponseHeadersRead", program, StringComparison.Ordinal);
        Assert.Contains("CopyRequestHeader(ctx, request, \"Range\")", program, StringComparison.Ordinal);
        Assert.Contains("CopyResponseHeaders(response, ctx.Response)", program, StringComparison.Ordinal);
        Assert.Contains("CurrentBrowserStreamUrl", playback, StringComparison.Ordinal);
        Assert.Contains("/engine-stream/{assetId:D}", playback, StringComparison.Ordinal);
        Assert.Contains("listenPlayback.startAudio", host, StringComparison.Ordinal);
        Assert.Contains("@ref=\"_audioRef\"", host, StringComparison.Ordinal);
    }

    [Fact]
    public void ListenPageState_TracksDefaultsAndSelectionTransitions()
    {
        var state = new ListenPageState();
        var trackId = Guid.NewGuid();

        Assert.Equal("music", state.ActiveMode);
        Assert.True(state.Loading);

        state.SetMode("audiobooks");
        state.SetSearch("dune");
        state.SetSort("title", descending: false);
        state.ReplaceTrackSelection([trackId]);
        state.SetError("failed");

        Assert.Equal("audiobooks", state.ActiveMode);
        Assert.Equal("dune", state.Search);
        Assert.Equal("title", state.SortColumn);
        Assert.False(state.SortDescending);
        Assert.Contains(trackId, state.SelectedTrackIds);
        Assert.False(state.Loading);
        Assert.Equal("failed", state.Error);
    }

    [Fact]
    public void PerformanceSourceGuardrails_RemainInPlace()
    {
        var appJs = File.ReadAllText(Path.Combine(RepoRoot, "src/MediaEngine.Web/wwwroot/app.js"));
        var universeTab = File.ReadAllText(Path.Combine(RepoRoot, "src/MediaEngine.Web/Components/Details/UniverseTab.razor"));

        Assert.Contains("__mediaTileHoverFrame", appJs);
        Assert.Contains("cancelAnimationFrame", appJs);
        Assert.Contains("ShouldRender", universeTab);
    }

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new StubHttpMessageHandler(responder))
        {
            BaseAddress = new Uri("http://localhost:61495"),
        };

    private static DisplayArtworkDto EmptyDisplayArtwork() =>
        new(
            CoverUrl: null,
            CoverSmallUrl: null,
            CoverMediumUrl: null,
            CoverLargeUrl: null,
            SquareUrl: null,
            SquareSmallUrl: null,
            SquareMediumUrl: null,
            SquareLargeUrl: null,
            BannerUrl: null,
            BannerSmallUrl: null,
            BannerMediumUrl: null,
            BannerLargeUrl: null,
            BackgroundUrl: null,
            BackgroundSmallUrl: null,
            BackgroundMediumUrl: null,
            BackgroundLargeUrl: null,
            LogoUrl: null,
            CoverWidthPx: null,
            CoverHeightPx: null,
            SquareWidthPx: null,
            SquareHeightPx: null,
            BannerWidthPx: null,
            BannerHeightPx: null,
            BackgroundWidthPx: null,
            BackgroundHeightPx: null,
            AccentColor: null);

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private class CountingEngineApiClient : DispatchProxy
    {
        public int CatalogueCalls { get; private set; }
        public IReadOnlyList<ProviderCatalogueDto>? Catalogue { get; set; }
        public IEngineApiClient Client => (IEngineApiClient)(object)this;

        public static CountingEngineApiClient Create()
        {
            var proxy = DispatchProxy.Create<IEngineApiClient, CountingEngineApiClient>();
            return (CountingEngineApiClient)(object)proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IEngineApiClient.GetProviderCatalogueAsync))
            {
                CatalogueCalls++;
                if (Catalogue is not null)
                {
                    return Task.FromResult(Catalogue);
                }
                return Task.FromResult<IReadOnlyList<ProviderCatalogueDto>>(
                [
                    new ProviderCatalogueDto
                    {
                        Name = $"provider_{CatalogueCalls}",
                        DisplayName = $"Provider {CatalogueCalls}",
                    },
                ]);
            }

            if (targetMethod?.ReturnType == typeof(string))
            {
                return string.Empty;
            }

            if (targetMethod?.ReturnType == typeof(string))
            {
                return null;
            }

            if (targetMethod?.ReturnType.IsGenericType == true && targetMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = targetMethod.ReturnType.GetGenericArguments()[0];
                var method = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType);
                return method.Invoke(null, [resultType.IsValueType ? Activator.CreateInstance(resultType) : null]);
            }

            return null;
        }
    }
}
