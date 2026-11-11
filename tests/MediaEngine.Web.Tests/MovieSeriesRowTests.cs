using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A movie's Series row on its details page: it never collapses, and it is one full-width rail of every movie rather
/// than a window of six.
/// </summary>
public sealed class MovieSeriesRowTests : AsyncBunitContext
{
    private static readonly Guid Series = Guid.Parse("50000000-0000-0000-0000-000000000028");

    public MovieSeriesRowTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.CreateDefault());
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
    }

    [Fact]
    public void AMovieSeries_ListsEveryMovieInOneScrollingRail()
    {
        var movies = Enumerable.Range(1, 8)
            .Select(number => Item(DetailEntityType.Movie, number, $"Movie {number}", current: number == 3))
            .ToArray();

        var cut = Render<SequencePlacementPanel>(p => p
            .Add(c => c.Placement, Placement(DetailEntityType.Movie, movies))
            .Add(c => c.DetailSurface, true));

        Assert.Contains("is-scrolling-rail", cut.Find("section.tl-series-detail").ClassList);
        Assert.Equal(8, cut.Find(".tl-series-detail__strip").Children.Length);
        Assert.Contains(JSInterop.Invocations, call => call.Identifier == "registerMediaShelfBoundaries");
    }

    [Fact]
    public void UnderTheSharedRowHeading_JumpToSitsInTheControlsRowAndFitsItsEntries()
    {
        var movies = Enumerable.Range(1, 12)
            .Select(number => Item(DetailEntityType.Movie, number, $"Movie {number}", current: number == 3))
            .ToArray();

        var cut = Render<SequencePlacementPanel>(p => p
            .Add(c => c.Placement, Placement(DetailEntityType.Movie, movies))
            .Add(c => c.DetailSurface, true)
            .Add(c => c.ShowIdentity, false));

        Assert.Empty(cut.FindAll(".tl-series-detail__heading"));
        Assert.Single(cut.FindAll(".tl-series-detail__controls .tl-series-detail__jump--fit"));
    }

    [Fact]
    public void ABookSeries_KeepsItsWindowOfSix()
    {
        var books = Enumerable.Range(1, 8)
            .Select(number => Item(DetailEntityType.Book, number, $"Book {number}", current: number == 1))
            .ToArray();

        var cut = Render<SequencePlacementPanel>(p => p
            .Add(c => c.Placement, Placement(DetailEntityType.Book, books))
            .Add(c => c.DetailSurface, true));

        Assert.DoesNotContain("is-scrolling-rail", cut.Find("section.tl-series-detail").ClassList);
        Assert.Equal(6, cut.Find(".tl-series-detail__strip").Children.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMoviePage_ShowsItsSeriesRowOpen_WithoutACollapseButton(bool otherMovieOwned)
    {
        var api = EngineApiClientStub.CreateDefault();
        Services.AddSingleton<IDetailsOverlayHistory>(new NoHistory());
        Services.AddSingleton(TimeProvider.System);
        Services.AddScoped<DetailsOverlayState>();
        Services.AddSingleton(new DeviceContextService(api));
        Services.AddNativeUiServices();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddSingleton(new SavedItemService(api));
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(_ => new PlaybackSessionController(null!, null!));
        Services.AddSingleton(_ => new ProviderCatalogueService(api, new MemoryCache(new MemoryCacheOptions())));
        Render<AppPopoverHost>();
        var current = Item(DetailEntityType.Movie, 3, "28 Years Later", current: true);
        var model = new DetailPageViewModel
        {
            Id = current.Id,
            EntityType = DetailEntityType.Movie,
            Title = current.Title,
            Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
            PrimaryModule = new DetailPrimaryModuleViewModel { Kind = DetailPrimaryModuleKind.Sequence, Title = "28 Days Later" },
            SequencePlacement = otherMovieOwned
                ? Placement(DetailEntityType.Movie, Item(DetailEntityType.Movie, 1, "28 Days Later"), current)
                : Placement(DetailEntityType.Movie, current),
        };

        var cut = Render<DetailPage>(p => p.Add(page => page.Model, model));

        var row = cut.Find("#detail-primary-module .app-collapsible-row");
        Assert.False(row.ClassList.Contains("is-collapsed"));
        Assert.Null(row.QuerySelector("button.app-collapsible-row__toggle"));
        Assert.Equal("28 Days Later", row.QuerySelector(".app-collapsible-row__title")!.TextContent);
    }

    [Fact]
    public void ASeriesRow_ShowsNoCheckmarksOnItsMovies()
    {
        var movies = new[]
        {
            Item(DetailEntityType.Movie, 1, "Movie 1", current: false),
            Item(DetailEntityType.Movie, 2, "Movie 2"),
            Item(DetailEntityType.Movie, 3, "Movie 3", current: true),
        };
        movies[0] = new SequenceItemViewModel
        {
            Id = movies[0].Id,
            EntityType = movies[0].EntityType,
            Title = movies[0].Title,
            IsOwned = true,
            PositionLabel = movies[0].PositionLabel,
            PositionNumber = movies[0].PositionNumber,
            PositionSort = movies[0].PositionSort,
            ProgressState = LibraryProgressState.Completed,
        };

        var cut = Render<SequencePlacementPanel>(p => p
            .Add(c => c.Placement, Placement(DetailEntityType.Movie, movies))
            .Add(c => c.DetailSurface, true));

        Assert.Empty(cut.FindAll(".tl-series-item__completed-badge"));
    }

    private sealed class NoHistory : IDetailsOverlayHistory
    {
        public ValueTask<bool> GoBackAsync(int steps) => ValueTask.FromResult(true);
    }

    private static SequencePlacementViewModel Placement(DetailEntityType kind, params SequenceItemViewModel[] items) => new()
    {
        ContainerId = Series.ToString("D"),
        SelectedContainerId = Series.ToString("D"),
        ContainerTitle = "28 Days Later",
        ContainerLabel = "Series",
        ItemLabel = kind == DetailEntityType.Movie ? "Movie" : "Book",
        ItemPluralLabel = kind == DetailEntityType.Movie ? "Movies" : "Books",
        CurrentItem = items.First(item => item.IsCurrent),
        OrderedItems = items,
    };

    private static SequenceItemViewModel Item(DetailEntityType kind, int number, string title, bool owned = true, bool current = false) => new()
    {
        Id = $"8{number:D7}-0000-0000-0000-000000000000",
        EntityType = kind,
        Title = title,
        PositionLabel = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PositionNumber = number,
        PositionSort = number,
        IsOwned = owned,
        IsCurrent = current,
    };
}
