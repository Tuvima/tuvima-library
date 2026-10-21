using Bunit;
using MediaEngine.Contracts.Details;
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
/// A movie's Series row on its details page: it shows only when another movie of the Series is in the library, and it is
/// one full-width rail of every movie rather than a window of six.
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
    public void AMovieWhoseSeriesHasNoOtherOwnedMovie_GetsNoSeriesRow()
    {
        var placement = Placement(DetailEntityType.Movie,
            Item(DetailEntityType.Movie, 1, "28 Days Later", owned: false),
            Item(DetailEntityType.Movie, 2, "28 Weeks Later", owned: false),
            Item(DetailEntityType.Movie, 3, "28 Years Later", current: true));

        Assert.False(DetailSequenceRowRule.ShowsRow(DetailEntityType.Movie, placement));
    }

    [Fact]
    public void AMovieWithAnotherOwnedMovieInItsSeries_KeepsTheRow()
    {
        var placement = Placement(DetailEntityType.Movie,
            Item(DetailEntityType.Movie, 1, "28 Days Later"),
            Item(DetailEntityType.Movie, 3, "28 Years Later", current: true));

        Assert.True(DetailSequenceRowRule.ShowsRow(DetailEntityType.Movie, placement));
    }

    [Fact]
    public void AMovieThatCanSwitchToAnotherSeries_KeepsTheRowForTheSelector()
    {
        var placement = Placement(DetailEntityType.Movie, Item(DetailEntityType.Movie, 3, "28 Years Later", current: true));
        placement = new SequencePlacementViewModel
        {
            ContainerId = placement.ContainerId,
            SelectedContainerId = placement.SelectedContainerId,
            CurrentItem = placement.CurrentItem,
            OrderedItems = placement.OrderedItems,
            CanChooseContainer = true,
            AvailableContainers =
            [
                new SequenceContainerOptionViewModel { ContainerId = "a", ContainerTitle = "28 Days Later", IsSelected = true },
                new SequenceContainerOptionViewModel { ContainerId = "b", ContainerTitle = "Danny Boyle films" },
            ],
        };

        Assert.True(DetailSequenceRowRule.ShowsRow(DetailEntityType.Movie, placement));
    }

    [Fact]
    public void AMovieSeriesPageHoldingOneMovie_StillListsIt()
    {
        // On a Series' own page the placement's current item is its first owned movie, not the page itself.
        var placement = Placement(DetailEntityType.Movie,
            Item(DetailEntityType.Movie, 1, "28 Days Later", owned: false),
            Item(DetailEntityType.Movie, 3, "28 Years Later", current: true));

        Assert.True(DetailSequenceRowRule.ShowsRow(DetailEntityType.MovieSeries, placement));
    }

    [Fact]
    public void ABookAloneInItsSeries_KeepsItsRow()
    {
        var placement = Placement(DetailEntityType.Book,
            Item(DetailEntityType.Book, 1, "Leviathan Wakes", current: true),
            Item(DetailEntityType.Book, 2, "Caliban's War", owned: false));

        Assert.True(DetailSequenceRowRule.ShowsRow(DetailEntityType.Book, placement));
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
    public void AMoviePage_DropsTheSeriesRowAndKeepsTheWholeStage_WhenNoOtherMovieIsOwned(bool otherMovieOwned)
    {
        var api = EngineApiClientStub.CreateDefault();
        Services.AddSingleton<IDetailsOverlayHistory>(new NoHistory());
        Services.AddSingleton(TimeProvider.System);
        Services.AddScoped<DetailsOverlayState>();
        Services.AddSingleton(new DeviceContextService(api));
        Services.AddNativeUiServices();
        Services.AddScoped<MediaEditorLauncherService>();
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
            SequencePlacement = Placement(DetailEntityType.Movie,
                Item(DetailEntityType.Movie, 1, "28 Days Later", owned: otherMovieOwned),
                current),
        };

        var cut = Render<DetailPage>(p => p.Add(page => page.Model, model));

        var stage = cut.Find("section.tl-detail-stage").ClassList;
        Assert.Equal(otherMovieOwned, stage.Contains("tl-detail-stage--with-primary"));
        Assert.Equal(!otherMovieOwned, stage.Contains("tl-detail-stage--identity-only"));
        Assert.Equal(otherMovieOwned ? 1 : 0, cut.FindAll("#detail-primary-module").Count);
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
