using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// One count line for every detail page (issues 1172, 1165, 1191): no group name and count at the top of the page, the total owned
/// count under the list in the same place and form, and one total for a Series (novellas and short stories count).
/// </summary>
public sealed class DetailCountLineTests : AsyncBunitContext
{
    public DetailCountLineTests()
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
    public void ABookSeriesPage_HasNoGroupNameAndCountAtTheTop_AndOneOwnedLineUnderTheList()
    {
        var cut = Render<DetailPrimaryModule>(p => p.Add(c => c.Model, SeriesModel(owned: 11)));

        Assert.Empty(cut.FindAll(".app-collapsible-row__title"));
        Assert.Empty(cut.FindAll(".app-collapsible-row__summary"));
        Assert.DoesNotContain("The Expanse 11 books owned", cut.Markup.Replace("\n", " "));
        Assert.Equal("11 books owned", cut.Find(".tl-detail-owned-line").TextContent.Trim());
        Assert.Single(cut.FindAll(".tl-detail-owned-line"));
    }

    [Fact]
    public void ASeriesCount_IncludesNovellasAndShortStoriesListedAlsoInThisWorld()
    {
        var placement = SeriesPlacement(owned: 11, alsoOwned: 4, alsoMissing: 1);

        var cut = Render<SequencePlacementPanel>(p => p
            .Add(c => c.Placement, placement)
            .Add(c => c.DetailSurface, true));

        Assert.Equal("15 books owned", cut.Find(".tl-series-detail > .tl-detail-owned-line").TextContent.Trim());
    }

    [Fact]
    public void TheSeriesCountRule_IsOneNumberForTheNumberedRunAndAlsoInThisWorld()
    {
        var placement = SeriesPlacement(owned: 11, alsoOwned: 4, alsoMissing: 1);

        Assert.Equal(11, SeriesOwnedCount.InRun(placement));
        Assert.Equal(4, SeriesOwnedCount.InAlsoInThisWorld(placement));
        Assert.Equal(15, SeriesOwnedCount.Of(placement));
        // The known total covers the numbered run and every title listed as also in this world, never less than what is owned.
        Assert.Equal(11 + 5, SeriesOwnedCount.KnownTotal(placement));
    }

    [Fact]
    public void TheSeriesAboutFact_AgreesWithTheLineUnderTheList()
    {
        var model = new DetailPageViewModel
        {
            EntityType = DetailEntityType.BookSeries,
            SequencePlacement = SeriesPlacement(owned: 11, alsoOwned: 4, alsoMissing: 0),
        };

        var rows = DetailFileFacts.BuildSeriesFacts(model, "In your library", "First published", "Latest", "Author", "{0} of {1} {2}");

        Assert.Equal("15 books", rows.Single(row => row.Label == "In your library").Value);
    }

    [Fact]
    public void AnAuthorsAllPage_ShowsItsTitleCountOnce_UnderTheList()
    {
        var cut = Render<DetailPrimaryModule>(p => p.Add(c => c.Model, AuthorModel(titles: 15)));

        Assert.Empty(cut.FindAll(".app-collapsible-row__title"));
        Assert.Empty(cut.FindAll(".app-collapsible-row__summary"));
        Assert.Equal(1, CountOf(cut.Markup, "15 titles"));
        Assert.Equal("15 titles", cut.Find(".tl-detail-owned-line").TextContent.Trim());
        // The row's own controls (sort and find) keep their line.
        Assert.NotEmpty(cut.FindAll(".app-collapsible-row__header .tl-detail-primary-module__controls"));
    }

    [Fact]
    public void AOneTitleList_ReadsInTheSingular()
    {
        var cut = Render<DetailPrimaryModule>(p => p.Add(c => c.Model, AuthorModel(titles: 1)));

        Assert.Equal("1 title", cut.Find(".tl-detail-owned-line").TextContent.Trim());
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static DetailPageViewModel AuthorModel(int titles) => new()
    {
        Id = "50000000-0000-0000-0000-000000000001",
        EntityType = DetailEntityType.Person,
        Title = "James S. A. Corey",
        Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
        PrimaryModule = new DetailPrimaryModuleViewModel { Kind = DetailPrimaryModuleKind.Works, Title = "Library" },
        MediaGroups =
        [
            new MediaGroupingViewModel
            {
                Key = "works",
                Title = "Works",
                Items = Enumerable.Range(1, titles).Select(number => new MediaGroupingItemViewModel
                {
                    Id = $"6{number:D7}-0000-0000-0000-000000000000",
                    EntityType = DetailEntityType.Book,
                    Title = $"Book {number}",
                    IsOwned = true,
                    Lane = "read",
                }).ToList(),
            },
        ],
    };

    private static DetailPageViewModel SeriesModel(int owned) => new()
    {
        Id = "70000000-0000-0000-0000-000000000001",
        EntityType = DetailEntityType.Book,
        Title = "Leviathan Wakes",
        Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
        PrimaryModule = new DetailPrimaryModuleViewModel { Kind = DetailPrimaryModuleKind.Sequence, Title = "The Expanse" },
        SequencePlacement = SeriesPlacement(owned, alsoOwned: 0, alsoMissing: 0),
    };

    private static SequencePlacementViewModel SeriesPlacement(int owned, int alsoOwned, int alsoMissing)
    {
        var run = Enumerable.Range(1, owned).Select(number => Book(number, owned: true, scope: "MainSequence", current: number == 1)).ToArray();
        var also = Enumerable.Range(1, alsoOwned).Select(number => Book(100 + number, owned: true, scope: "Supplementary", current: false))
            .Concat(Enumerable.Range(1, alsoMissing).Select(number => Book(200 + number, owned: false, scope: "Supplementary", current: false)))
            .ToArray();
        return new SequencePlacementViewModel
        {
            ContainerId = "80000000-0000-0000-0000-000000000001",
            SelectedContainerId = "80000000-0000-0000-0000-000000000001",
            ContainerTitle = "The Expanse",
            ContainerLabel = "Series",
            ItemLabel = "Book",
            ItemPluralLabel = "Books",
            CurrentItem = run[0],
            OrderedItems = run,
            AlsoInThisWorld = also,
            TotalKnownItems = owned,
        };
    }

    private static SequenceItemViewModel Book(int number, bool owned, string scope, bool current) => new()
    {
        Id = $"9{number:D7}-0000-0000-0000-000000000000",
        EntityType = DetailEntityType.Book,
        Title = $"Book {number}",
        PositionLabel = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PositionNumber = number,
        PositionSort = number,
        MembershipScope = scope,
        IsOwned = owned,
        IsCurrent = current,
    };
}
