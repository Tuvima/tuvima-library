using MediaEngine.Web.Components.Shared;
using Bunit;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Search;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Integration.Clients;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class Phase3BrowseFoundationTests : AsyncBunitContext
{
    public Phase3BrowseFoundationTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddScoped<MediaEditorLauncherService>();
    }

    [Fact]
    public void Search_RendersResultsAndNoResultsStates()
    {
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetUniversalSearchAsync), args =>
            {
                var query = args?[0]?.ToString();
                var result = new UniversalSearchResultDto(
                    Guid.Parse("31000000-0000-0000-0000-000000000001"),
                    "book",
                    "Book",
                    "Dune",
                    "Frank Herbert",
                    "Frank Herbert",
                    "1965",
                    null,
                    "A science fiction classic.",
                    "/book/31000000-0000-0000-0000-000000000001",
                    "Read",
                    "Exact title match",
                    1);
                return Task.FromResult<UniversalSearchResponseDto?>(string.Equals(query, "dune", StringComparison.OrdinalIgnoreCase)
                    ? new UniversalSearchResponseDto("dune", result,
                    [new UniversalSearchSectionDto("books", "Books", [result], 1, "/read/books?q=dune")], 1)
                    : new UniversalSearchResponseDto(query ?? string.Empty, null, [], 0));
            });
        }));

        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(navigation.GetUriWithQueryParameter("q", "dune"));

        var cut = Render(builder =>
        {
            builder.OpenComponent<AppPopoverHost>(0);
            builder.CloseComponent();
            builder.OpenComponent<SearchPage>(1);
            builder.CloseComponent();
        });
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Dune", cut.Markup);
            Assert.Contains("Frank Herbert", cut.Markup);
            Assert.Single(cut.FindAll(".universal-results__card"));
        });

        navigation.NavigateTo(navigation.GetUriWithQueryParameter("q", "missing"));
        cut.WaitForAssertion(() => Assert.Contains("No results match this search", cut.Markup));
    }

    [Fact]
    public async Task Home_RendersRealShelvesWhenCatalogExists()
    {
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetDisplayHomeAsync), _ => Task.FromResult<DisplayPageDto?>(CreateDisplayPage(
                "home",
                "Home",
                "recently-added",
                "Recently Added",
                "Book",
                "Project Hail Mary",
                "Andy Weir")));
        }));

        var composer = new MediaTileComposerService(Services.GetRequiredService<IEngineApiClient>());

        var page = await composer.BuildHomeAsync();

        Assert.Equal("Project Hail Mary", page.Hero?.Title);
        Assert.Single(page.Shelves);
        Assert.Equal("Recently Added", page.Shelves[0].Title);
        Assert.Equal("Book", page.Catalog[0].MediaKind);
    }

    [Fact]
    public async Task Home_EmptyStateShowsFirstLibraryPromptWhenNoCatalog()
    {
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetDisplayHomeAsync), _ => Task.FromResult<DisplayPageDto?>(new DisplayPageDto(
                "home",
                "Home",
                null,
                null,
                [],
                [])));
        }));

        var composer = new MediaTileComposerService(Services.GetRequiredService<IEngineApiClient>());

        var page = await composer.BuildHomeAsync();

        Assert.Null(page.Hero);
        Assert.Empty(page.Shelves);
        Assert.Empty(page.Catalog);
        Assert.Contains("first story", page.EmptyTitle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Watch_RetriesATransientNullDisplayResponse()
    {
        var requestCount = 0;
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetDisplayBrowseAsync), _ =>
            {
                requestCount++;
                return Task.FromResult<DisplayPageDto?>(requestCount == 1
                    ? null
                    : CreateDisplayPage(
                        "watch",
                        "Watch",
                        "movies",
                        "Movies",
                        "Movie",
                        "Arrival",
                        "Denis Villeneuve"));
            });
        });
        var composer = new MediaTileComposerService(api);

        var page = await composer.BuildWatchAsync();

        Assert.Equal(2, requestCount);
        Assert.Equal("Arrival", page.Hero?.Title);
    }

    [Fact]
    public async Task EngineFailureState_CapturesRetryAfterFromThrottledResponses()
    {
        var state = new EngineApiFailureState();
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
            TimeSpan.FromSeconds(12));

        await state.RecordHttpFailureAsync(
            "GET /api/v1/display/browse",
            response,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(429, state.LastStatusCode);
        Assert.Equal(TimeSpan.FromSeconds(12), state.LastRetryAfter);
    }

    [Theory]
    [InlineData("Book", "/details/work/32000000-0000-0000-0000-000000000001?context=read")]
    [InlineData("Movie", "/details/work/32000000-0000-0000-0000-000000000001?context=watch")]
    [InlineData("TV", "/details/work/32000000-0000-0000-0000-000000000001?context=watch")]
    [InlineData("Music", "/listen/music?browse=songs&track=32000000-0000-0000-0000-000000000001")]
    [InlineData("Audiobook", "/details/work/32000000-0000-0000-0000-000000000001?context=listen")]
    public void SearchResults_RouteToMediaSpecificSurfaces(string mediaType, string expectedRoute)
    {
        var result = new SearchResultDto
        {
            WorkId = Guid.Parse("32000000-0000-0000-0000-000000000001"),
            Title = "Known item",
            MediaType = mediaType,
        };

        Assert.Equal(expectedRoute, MediaNavigation.ForSearchResult(result));
    }

    [Fact]
    public void MediaTiles_DoNotRenderFakeRecommendationsOrProgressWithoutData()
    {
        var page = MediaTileComposerService.FromDisplayPage(CreateDisplayPage(
            "home",
            "Home",
            "read",
            "Read",
            "Book",
            "A Real Book",
            "A Real Author",
            includeProgress: false));

        Assert.DoesNotContain(page.Shelves, shelf => shelf.Title.Contains("Recommended", StringComparison.OrdinalIgnoreCase));
        Assert.All(page.Catalog, card => Assert.Null(card.ProgressPct));
    }

    private static DisplayPageDto CreateDisplayPage(
        string key,
        string title,
        string shelfKey,
        string shelfTitle,
        string mediaType,
        string itemTitle,
        string subtitle,
        bool includeProgress = false)
    {
        var workId = Guid.Parse("33000000-0000-0000-0000-000000000001");
        var assetId = Guid.Parse("33000000-0000-0000-0000-000000000002");
        var details = new DisplayActionDto("openWork", "Details", WorkId: workId, WebUrl: "/book/33000000-0000-0000-0000-000000000001");
        var resume = new DisplayActionDto("readAsset", "Continue", WorkId: workId, AssetId: assetId, WebUrl: "/read/33000000-0000-0000-0000-000000000002");
        var artwork = new DisplayArtworkDto(
            CoverUrl: "/cover.jpg",
            CoverSmallUrl: "/cover-s.jpg",
            CoverMediumUrl: "/cover-m.jpg",
            CoverLargeUrl: "/cover-l.jpg",
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
            CoverWidthPx: 900,
            CoverHeightPx: 1400,
            SquareWidthPx: null,
            SquareHeightPx: null,
            BannerWidthPx: null,
            BannerHeightPx: null,
            BackgroundWidthPx: null,
            BackgroundHeightPx: null,
            AccentColor: "#5DCAA5");
        var card = new DisplayCardDto(
            Id: workId,
            WorkId: workId,
            AssetId: assetId,
            CollectionId: null,
            MediaType: mediaType,
            GroupingType: "work",
            Title: itemTitle,
            Subtitle: subtitle,
            Facts: [subtitle],
            Artwork: artwork,
            PreferredShape: "portrait",
            Presentation: "book",
            TileTextMode: "caption",
            PreviewPlacement: "bottom",
            Progress: includeProgress ? new DisplayProgressDto(48, "48%", DateTimeOffset.UtcNow, resume) : null,
            Actions: [details],
            Flags: new DisplayCardFlagsDto(false, true, false, false),
            SortTimestamp: DateTimeOffset.UtcNow);

        return new DisplayPageDto(
            key,
            title,
            null,
            new DisplayHeroDto(itemTitle, subtitle, "From your library", artwork, includeProgress ? card.Progress : null, [includeProgress ? resume : details]),
            [new DisplayShelfDto(shelfKey, shelfTitle, null, [card], null)],
            [card]);
    }
}
