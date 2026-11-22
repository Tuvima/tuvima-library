using System.Globalization;
using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The page of a Series or a collection (spec group-pages-and-tiles WP2 and WP3, issues 1252 and 1260): the standard hero with the group's
/// covers on the right in their own shape (no tile), the path and pills in words, the members as a numbered cover grid with a title you do not
/// own dimmed, the one "You own 10 of 11 books." line, and the In order and Details tabs.
/// </summary>
public sealed class GroupPageTests : AsyncBunitContext
{
    private static readonly Guid SeriesId = Guid.Parse("50000000-0000-0000-0000-000000000051");
    private bool _showMissingTicked;

    public GroupPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel>
            {
                new(TestSessions.Profile, "Test User", "#C9922E", "Standard", DateTimeOffset.UtcNow),
            }));
            stub.SetHandler(nameof(IEngineApiClient.GetSeriesMissingItemPreferenceAsync), args => Task.FromResult<SeriesMissingItemPreferenceDto?>(
                new SeriesMissingItemPreferenceDto { ProfileId = (Guid)args![0]!, MediaType = (string)args[1]!, ContainerKey = (string)args[2]!, ShowMissing = _showMissingTicked }));
        }));
        Services.AddSingleton(TestSessions.For(kids: false, administrator: false));
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
    }

    // The words ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheOwnedLine_SaysHowManyOfTheSeriesYouOwn()
    {
        Assert.Equal("You own 10 of 11 books.", GroupPageRules.OwnedLine(10, 11, "book"));
        Assert.Equal("You own 1 of 1 book.", GroupPageRules.OwnedLine(1, 1, "book"));
    }

    [Fact]
    public void TheOwnedLine_WithNothingOwned_SaysNoneOfTheTotal()
    {
        Assert.Equal("You own none of the 11 books.", GroupPageRules.OwnedLine(0, 11, "book"));
        Assert.Equal("You own none of the 1 book.", GroupPageRules.OwnedLine(0, 1, "book"));
    }

    [Fact]
    public void TheOwnedLine_NeverClaimsAnOwnedCountAboveItsTotal() =>
        Assert.Equal("You own 12 of 12 books.", GroupPageRules.OwnedLine(12, 11, "book"));

    [Fact]
    public void TheOwnedLine_ForASeries_CountsNovellasAndUsesTheSeriesOwnWords()
    {
        var placement = Placement(owned: 10, total: 11, alsoOwned: 1);

        // 10 numbered + 1 novella owned, of 11 known + 1 listed also in this world.
        Assert.Equal("You own 11 of 12 books.", GroupPageRules.OwnedLine(placement));
    }

    [Fact]
    public void TheYearRange_RunsFromTheFirstToTheLastYear_AndNamesOneYearOnce()
    {
        Assert.Equal("2011 – 2021", GroupPageRules.YearRange(["2011-06-15", null, "2021-11-30", "2014"]));
        Assert.Equal("2014", GroupPageRules.YearRange(["2014-01-01", "2014"]));
        Assert.Null(GroupPageRules.YearRange(["", null, "unknown"]));
    }

    [Fact]
    public void ASeriesPills_AreInWords_TypeThenKindThenYearsThenCount()
    {
        var model = SeriesModel(owned: 10, total: 11);

        var pills = GroupPageRules.Pills(model);

        Assert.Equal(["Series", "Books", "2011 – 2021", "11 books"], pills.Select(pill => pill.Text));
        Assert.True(pills[0].IsType);
        Assert.All(pills.Skip(1), pill => Assert.False(pill.IsType));
    }

    [Theory]
    [InlineData(DetailEntityType.BookSeries, "Read", "/read", "Series")]
    [InlineData(DetailEntityType.ComicSeries, "Read", "/read", "Series")]
    [InlineData(DetailEntityType.MovieSeries, "Watch", "/watch", "Series")]
    [InlineData(DetailEntityType.Collection, "Collections", "/collections", null)]
    public void ThePathAboveTheTitle_NamesWhereTheGroupLives(DetailEntityType type, string label, string href, string? kind)
    {
        Assert.Equal((label, href), GroupPageRules.PathRoot(type));
        Assert.Equal(kind, GroupPageRules.PathKind(type));
    }

    // The hero ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheHero_KeepsTheStandardLayout_WithCoversInTheArtworkSlotAndNoTile()
    {
        var cut = RenderHero(GroupModel(chosenCover: null));

        var slot = cut.Find(".tl-detail-collection-artwork");
        Assert.Equal(3, slot.QuerySelectorAll(".group-hero-covers__cover").Length);
        // A group page's hero is the details hero: no group tile (band, name, count) and no fanned preview.
        Assert.Empty(cut.FindAll(".group-collage"));
        Assert.Empty(cut.FindAll(".media-artwork-group-preview"));
        Assert.Empty(cut.FindAll(".group-collage__band"));
        Assert.Equal("The Expanse", cut.Find("h1").TextContent);
    }

    [Fact]
    public void TheHero_ShowsThePathAndPillsInWords_AndOnePrimaryAction()
    {
        var cut = RenderHero(GroupModel(chosenCover: null));

        var path = cut.Find(".tl-detail-hero__path");
        Assert.Equal("/read", path.QuerySelector("a")!.GetAttribute("href"));
        Assert.Contains("Read", path.TextContent);
        Assert.Contains("Series", path.TextContent);
        Assert.Equal(
            ["Series", "Books", "2011 – 2021", "11 books"],
            cut.FindAll(".tl-detail-group-pill").Select(pill => pill.TextContent.Trim()));
        Assert.Single(cut.FindAll(".tl-detail-group-pill--type"));
        Assert.Single(cut.FindAll(".tl-detail-primary-actions"));
        Assert.Contains("Read next", cut.Find(".tl-detail-action--primary").TextContent);
    }

    [Fact]
    public void TheHero_DrawsEachCoverInItsOwnShape_AtOneHeight_AndNeverCropsOne()
    {
        var model = GroupModel(chosenCover: null, shapes: [DetailEntityType.Book, DetailEntityType.MusicAlbum, DetailEntityType.Movie]);

        var cut = RenderHero(model);

        var covers = cut.FindAll(".group-hero-covers__cover");
        Assert.Equal(["portrait", "square", "portrait"], covers.Select(cover => cover.GetAttribute("data-shape")));
        Assert.Equal(
            ["--a:0.6667;", "--a:1;", "--a:0.6667;"],
            covers.Select(cover => cover.GetAttribute("style")));
        var css = ReadSource("src/MediaEngine.Web/Components/Shared/GroupHeroCovers.razor.css");
        Assert.Contains("object-fit: contain", css);
        Assert.DoesNotContain("object-fit: cover", css);
    }

    [Fact]
    public void TheHero_CountsTheCoversItDoesNotDraw_WithTheSamePlusNChipAsTheTile()
    {
        var cut = RenderHero(GroupModel(chosenCover: null, ownedWithCovers: 8));

        Assert.Equal(3, cut.FindAll(".group-hero-covers__cover").Count);
        Assert.Equal("+5", cut.Find(".group-hero-covers__more").TextContent.Trim());
    }

    [Fact]
    public void TheHero_WithAChosenCover_ShowsItWhole_AndNoMemberCoversOrChip()
    {
        var cut = RenderHero(GroupModel(chosenCover: "/engine-image/stream/artwork/6f6b4d6e-0000-4000-8000-0000000000aa"));

        var chosen = cut.Find(".group-hero-covers__chosen");
        Assert.Contains("6f6b4d6e-0000-4000-8000-0000000000aa", chosen.GetAttribute("src"));
        Assert.Empty(cut.FindAll(".group-hero-covers__cover"));
        Assert.Empty(cut.FindAll(".group-hero-covers__more"));
        Assert.Contains("max-height: var(--h)", ReadSource("src/MediaEngine.Web/Components/Shared/GroupHeroCovers.razor.css"));
    }

    [Fact]
    public void TheHeroCovers_HaveNoHoverAndNoClick()
    {
        var css = ReadSource("src/MediaEngine.Web/Components/Shared/GroupHeroCovers.razor.css");
        var slot = ReadSource("src/MediaEngine.Web/Components/Details/DetailHero.razor.css");

        Assert.DoesNotContain(":hover", css);
        Assert.Contains("pointer-events: none", css);
        Assert.Contains("pointer-events: none", slot[slot.IndexOf(".tl-detail-collection-artwork {", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void TheHeroCovers_WithNoPictures_DrawNothing()
    {
        var cut = Render<GroupHeroCovers>(p => p.Add(c => c.Members, []).Add(c => c.TotalCount, 4));

        Assert.Empty(cut.FindAll(".group-hero-covers"));
    }

    [Fact]
    public void ThePhoneHero_PutsTheCoversOnTop_AndTheTextUnderThem()
    {
        var covers = ReadSource("src/MediaEngine.Web/Components/Shared/GroupHeroCovers.razor.css");
        var slot = ReadSource("src/MediaEngine.Web/Components/Details/DetailHero.razor.css");
        var copy = ReadSource("src/MediaEngine.Web/Components/Details/DetailHeroContent.razor.css");

        // Covers centered across the top, under the top bar; the copy starts under them.
        Assert.Contains("--h: 8.25rem", covers);
        Assert.Contains("justify-content: center", covers[covers.IndexOf("@media (width < 840px)", StringComparison.Ordinal)..]);
        Assert.Contains("top: calc(var(--app-topbar-height, 65px) + 1.25rem)", slot);
        Assert.Contains("padding-top: calc(var(--app-topbar-height, 65px) + 1.25rem + 8.25rem + 1.5rem)", copy);
    }

    // The members ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ASeriesPage_ListsItsMembersAsOrderedCoversWithTheirNumbers()
    {
        var cut = RenderPrimaryModule(SeriesModel(owned: 10, total: 11));

        cut.WaitForAssertion(() => Assert.Equal(10, cut.FindAll(".group-member").Count));
        Assert.Empty(cut.FindAll(".tl-series-detail__strip"));
        Assert.Equal(
            Enumerable.Range(1, 10).Select(number => number.ToString(CultureInfo.InvariantCulture)),
            cut.FindAll(".group-member__number").Select(number => number.TextContent.Trim()));
        // Each cover opens the book's own page.
        Assert.All(cut.FindAll("a.group-member__link"), link => Assert.StartsWith("/details/work/", link.GetAttribute("href")));
    }

    [Fact]
    public void ASeriesTitleYouDoNotOwn_IsDimmedWithADashedOutline_ThatCannotBeOpened_WhenMissingTitlesAreShown()
    {
        // The person's own Show missing choice for this Series is kept per profile; here it is ticked.
        _showMissingTicked = true;

        var cut = RenderPrimaryModule(SeriesModel(owned: 10, total: 11));

        cut.WaitForAssertion(() => Assert.Equal(11, cut.FindAll(".group-member").Count));
        var missing = Assert.Single(cut.FindAll(".group-member.is-missing"));
        Assert.Equal("11", missing.QuerySelector(".group-member__number")!.TextContent.Trim());
        Assert.Empty(missing.QuerySelectorAll("a"));
        Assert.Contains("Not in your library", missing.TextContent);
        var css = ReadSource("src/MediaEngine.Web/Components/Details/GroupMemberCell.razor.css");
        Assert.Contains("dashed", css[css.IndexOf(".group-member.is-missing .group-member__art", StringComparison.Ordinal)..]);
        Assert.Contains("opacity: 0.35", css);
    }

    [Fact]
    public void ASeriesPage_HasOneOwnedLineUnderTheGrid()
    {
        var cut = RenderPrimaryModule(SeriesModel(owned: 10, total: 11));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".group-member")));
        Assert.Equal("You own 10 of 11 books.", cut.Find(".tl-detail-owned-line").TextContent.Trim());
        Assert.Single(cut.FindAll(".tl-detail-owned-line"));
    }

    [Fact]
    public void ASeriesPageWithNothingOwned_SaysYouOwnNoneOfTheBooks()
    {
        var cut = RenderPrimaryModule(SeriesModel(owned: 0, total: 11));

        cut.WaitForAssertion(() => Assert.Equal("You own none of the 11 books.", cut.Find(".tl-detail-owned-line").TextContent.Trim()));
    }

    [Fact]
    public void ABookPageInsideASeries_KeepsTheOldCarouselAndItsOwnedCountLine()
    {
        var model = SeriesModel(owned: 10, total: 11);
        model = new DetailPageViewModel
        {
            Id = model.Id,
            EntityType = DetailEntityType.Book,
            Title = "Leviathan Wakes",
            Tabs = model.Tabs,
            PrimaryModule = model.PrimaryModule,
            SequencePlacement = model.SequencePlacement,
        };

        var cut = RenderPrimaryModule(model);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-series-detail__strip")));
        Assert.Empty(cut.FindAll(".group-member"));
        Assert.Equal("10 books owned", cut.Find(".tl-detail-owned-line").TextContent.Trim());
    }

    [Fact]
    public void ACollectionPage_ListsItsTitlesAsNumberedCovers_WithOneLine()
    {
        var cut = RenderPrimaryModule(CollectionModel(titles: 4));

        Assert.Equal(4, cut.FindAll(".group-member").Count);
        Assert.Equal(["1", "2", "3", "4"], cut.FindAll(".group-member__number").Select(number => number.TextContent.Trim()));
        Assert.Empty(cut.FindAll(".media-shelf-scroller, .tl-detail-primary-module__rail"));
        Assert.Equal("You own 4 titles.", cut.Find(".tl-detail-owned-line").TextContent.Trim());
    }

    [Fact]
    public void ACollectionPage_ForAnImportedList_SaysHowManyOfTheListYouOwn()
    {
        var model = CollectionModel(titles: 4, listMissing: 1);

        var cut = RenderPrimaryModule(model);

        Assert.Equal("You own 4 of 5 titles.", cut.Find(".tl-detail-owned-line").TextContent.Trim());
    }

    [Fact]
    public void AMixedCollection_KeepsEachCoverInItsOwnShape()
    {
        var cut = RenderPrimaryModule(CollectionModel(titles: 3, mixed: true));

        Assert.Equal(["portrait", "square", "portrait"], cut.FindAll(".group-member").Select(cell =>
            cell.ClassList.First(name => name.StartsWith("group-member--", StringComparison.Ordinal))["group-member--".Length..]));
    }

    // The tabs and the loading state ---------------------------------------------------------------------------------------------

    [Fact]
    public void AGroupPage_OnTheDetailsTab_DrawsTheOverviewAndFacts_AndInOrderDrawsOnlyTheMembers()
    {
        var page = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor");

        // Details (group pages only) holds the overview text and the facts; the In order tab body draws nothing under the grid.
        Assert.Contains("CurrentActiveTab is \"details\" && IsGroupPage", page);
        Assert.Contains("In order: the members are the numbered cover grid above", page);
    }

    [Fact]
    public void WhileAGroupPageLoads_TheSkeletonDrawsGreyCoversAndAGreyGridAtFinalSize()
    {
        var cut = Render<DetailHeroSkeleton>(p => p.Add(c => c.IsGroup, true));

        Assert.Equal(3, cut.FindAll(".tl-detail-skeleton__group-cover").Count);
        Assert.Equal(6, cut.FindAll(".tl-detail-skeleton__member").Count);
        Assert.Empty(cut.FindAll(".tl-detail-skeleton__artwork"));
        var css = ReadSource("src/MediaEngine.Web/Components/Details/DetailHeroSkeleton.razor.css");
        // The same cover height and the same grid columns as the real page.
        Assert.Contains("height: clamp(13.75rem, 20vw, 17.5rem)", css);
        Assert.Contains("grid-template-columns: repeat(auto-fill, minmax(8.75rem, 1fr))", css);
        Assert.Contains("grid-template-columns: repeat(auto-fill, minmax(8.75rem, 1fr))", ReadSource("src/MediaEngine.Web/Components/Details/GroupMemberGrid.razor.css"));
    }

    [Fact]
    public void WhileAnyOtherPageLoads_TheSkeletonKeepsItsSingleGreyCover()
    {
        var cut = Render<DetailHeroSkeleton>();

        Assert.Empty(cut.FindAll(".tl-detail-skeleton__group-cover"));
        Assert.Empty(cut.FindAll(".tl-detail-skeleton__member"));
        Assert.NotEmpty(cut.FindAll(".tl-detail-skeleton__artwork"));
    }

    // Helpers ----------------------------------------------------------------------------------------------------------------------

    private IRenderedComponent<DetailHero> RenderHero(DetailPageViewModel model) =>
        Render<DetailHero>(parameters => parameters.Add(p => p.Model, model));

    private IRenderedComponent<DetailPrimaryModule> RenderPrimaryModule(DetailPageViewModel model) =>
        Render<DetailPrimaryModule>(parameters => parameters.Add(p => p.Model, model));

    private static DetailPageViewModel GroupModel(string? chosenCover, int ownedWithCovers = 3, IReadOnlyList<DetailEntityType>? shapes = null)
    {
        var placement = Placement(owned: ownedWithCovers, total: 11, alsoOwned: 0);
        return new DetailPageViewModel
        {
            Id = SeriesId.ToString("D"),
            EntityType = DetailEntityType.BookSeries,
            PresentationContext = DetailPresentationContext.Read,
            Title = "The Expanse",
            Tabs = [new DetailTab { Key = "overview", Label = "In order" }, new DetailTab { Key = "details", Label = "Details" }],
            Artwork = new ArtworkSet { CoverUrl = chosenCover },
            SequencePlacement = placement,
            PrimaryActions = [new DetailAction { Key = "read", Label = "Read next: Book 4" }],
            MediaGroups =
            [
                new MediaGroupingViewModel
                {
                    Key = "books",
                    Title = "Books",
                    Items = Enumerable.Range(1, ownedWithCovers).Select(number => new MediaGroupingItemViewModel
                    {
                        Id = $"9{number:D7}-0000-0000-0000-000000000000",
                        EntityType = shapes is { } list && number <= list.Count ? list[number - 1] : DetailEntityType.Book,
                        Title = $"Book {number}",
                        ArtworkUrl = $"/engine-image/stream/artwork/6f6b4d6e-0000-4000-8000-{number:D12}",
                        IsOwned = true,
                    }).ToList(),
                },
            ],
        };
    }

    private static DetailPageViewModel SeriesModel(int owned, int total) => new()
    {
        Id = SeriesId.ToString("D"),
        EntityType = DetailEntityType.BookSeries,
        PresentationContext = DetailPresentationContext.Read,
        Title = "The Expanse",
        Tabs = [new DetailTab { Key = "overview", Label = "In order" }, new DetailTab { Key = "details", Label = "Details" }],
        PrimaryActions = [new DetailAction { Key = "read", Label = "Read next: Book 4" }],
        PrimaryModule = new DetailPrimaryModuleViewModel { Kind = DetailPrimaryModuleKind.Sequence, Title = "Books" },
        SequencePlacement = Placement(owned, total, alsoOwned: 0),
    };

    // Books 1..total in order; the first <owned> are owned, the rest are not. Published one a year from 2011.
    private static SequencePlacementViewModel Placement(int owned, int total, int alsoOwned)
    {
        var run = Enumerable.Range(1, total).Select(number => new SequenceItemViewModel
        {
            Id = $"9{number:D7}-0000-0000-0000-000000000000",
            EntityType = DetailEntityType.Book,
            Title = $"Book {number}",
            PositionLabel = number.ToString(CultureInfo.InvariantCulture),
            PositionNumber = number,
            PositionSort = number,
            PublicationDate = $"{2010 + number}-05-01",
            MembershipScope = "MainSequence",
            IsOwned = number <= owned,
            IsCurrent = number == 1,
            ArtworkUrl = number <= owned ? $"/engine-image/stream/artwork/6f6b4d6e-0000-4000-8000-{number:D12}" : null,
        }).ToArray();
        var also = Enumerable.Range(1, alsoOwned).Select(number => new SequenceItemViewModel
        {
            Id = $"8{number:D7}-0000-0000-0000-000000000000",
            EntityType = DetailEntityType.Book,
            Title = $"Novella {number}",
            MembershipScope = "Supplementary",
            IsOwned = true,
        }).ToArray();
        return new SequencePlacementViewModel
        {
            ContainerId = SeriesId.ToString("D"),
            SelectedContainerId = SeriesId.ToString("D"),
            ContainerTitle = "The Expanse",
            ContainerLabel = "Series",
            ItemLabel = "Book",
            ItemPluralLabel = "Books",
            CurrentItem = run[0],
            OrderedItems = run,
            AlsoInThisWorld = also,
            TotalKnownItems = total,
        };
    }

    private static DetailPageViewModel CollectionModel(int titles, int listMissing = 0, bool mixed = false) => new()
    {
        Id = "70000000-0000-0000-0000-000000000051",
        EntityType = DetailEntityType.Collection,
        Title = "Favorites",
        Tabs = [new DetailTab { Key = "overview", Label = "In order" }, new DetailTab { Key = "details", Label = "Details" }],
        PrimaryModule = new DetailPrimaryModuleViewModel { Kind = DetailPrimaryModuleKind.CollectionItems, Title = "Items" },
        CollectionInfo = new DetailCollectionInfoViewModel
        {
            List = listMissing == 0
                ? null
                : new DetailCollectionListViewModel
                {
                    EntryCount = titles + listMissing,
                    OwnedCount = titles,
                    Missing = Enumerable.Range(1, listMissing).Select(number => new DetailMissingTitleViewModel { Position = titles + number, Title = $"Not owned {number}" }).ToList(),
                },
        },
        MediaGroups =
        [
            new MediaGroupingViewModel
            {
                Key = "items",
                Title = "Items",
                Items = Enumerable.Range(1, titles).Select(number => new MediaGroupingItemViewModel
                {
                    Id = $"6{number:D7}-0000-0000-0000-000000000000",
                    EntityType = mixed && number == 2 ? DetailEntityType.MusicAlbum : DetailEntityType.Book,
                    Title = $"Title {number}",
                    ArtworkUrl = $"/engine-image/stream/artwork/6f6b4d6e-0000-4000-8000-{number:D12}",
                    IsOwned = true,
                    Lane = "read",
                }).ToList(),
            },
        ],
    };

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
