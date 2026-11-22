using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A show's episode row (spec details-and-tile-fixes D9, issues 1165 and 1172, one layout for every page with a Season or folder
/// dropdown): no row heading line, "N seasons · M episodes" at the top of the
/// page, "N episodes owned" on its own line under the tiles with the collection tile icon, and Jump to at the right edge.
/// </summary>
public sealed class EpisodeRowLayoutTests : AsyncBunitContext
{
    private static readonly Guid Show = Guid.Parse("30000000-0000-0000-0000-000000000009");

    public EpisodeRowLayoutTests()
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
    public void AnEpisodeRow_HasNoHeadingLine()
    {
        var cut = Render<DetailPrimaryModule>(p => p.Add(c => c.Model, Model(episodes: 12)));

        Assert.Empty(cut.FindAll(".app-collapsible-row__header"));
        Assert.Empty(cut.FindAll(".app-collapsible-row__title"));
        Assert.Empty(cut.FindAll("h2"));
        Assert.Equal("Episodes", cut.Find("section.app-collapsible-row").GetAttribute("aria-label"));
    }

    [Fact]
    public void AFolderOfVideos_HasNoHeadingLineEither_AndTheSameOwnedLineUnderTheList()
    {
        var model = Model(episodes: 12, videoFolder: true);

        var cut = Render<DetailPrimaryModule>(p => p.Add(c => c.Model, model));

        Assert.Empty(cut.FindAll(".app-collapsible-row__title"));
        Assert.Empty(cut.FindAll(".app-collapsible-row__summary"));
        Assert.Equal("Videos", cut.Find("section.app-collapsible-row").GetAttribute("aria-label"));
        Assert.Equal("12 videos owned", cut.Find(".tl-detail-owned-line").TextContent.Trim());
        // Nothing at the top right of the dropdown row (issue 1165).
        Assert.Empty(cut.FindAll(".tl-series-detail__ownership"));
    }

    [Fact]
    public void TheOwnedLine_SitsAfterTheTilesWithTheCollectionTileIcon()
    {
        var cut = Render<SequencePlacementPanel>(p => p
            .Add(c => c.Placement, Placement(episodes: 12))
            .Add(c => c.DetailSurface, true));

        var line = cut.Find(".tl-detail-owned-line");
        Assert.Equal("12 episodes owned", line.TextContent.Trim());
        Assert.NotNull(line.QuerySelector("svg"));
        Assert.Equal(cut.Find(".tl-series-detail__carousel"), line.PreviousElementSibling);
        // The count is not in the controls row at the right.
        Assert.Empty(cut.FindAll(".tl-series-detail__ownership"));
        Assert.Empty(cut.FindAll(".tl-series-detail__controls .tl-series-detail__utilities"));
    }

    [Fact]
    public void AOneEpisodeSeason_ReadsInTheSingular()
    {
        var cut = Render<SequencePlacementPanel>(p => p
            .Add(c => c.Placement, Placement(episodes: 1))
            .Add(c => c.DetailSurface, true));

        Assert.Equal("1 episode owned", cut.Find(".tl-detail-owned-line").TextContent.Trim());
    }

    [Fact]
    public void JumpToSelector_IsRightAlignedAfterTheSeasonDropdown()
    {
        var cut = Render<SequencePlacementPanel>(p => p
            .Add(c => c.Placement, Placement(episodes: 12))
            .Add(c => c.DetailSurface, true));

        var controls = cut.Find(".tl-series-detail__controls");
        var children = controls.Children.ToList();
        Assert.Contains("tl-series-detail__selectors", children[0].ClassList);
        Assert.Contains("tl-series-detail__jump--end", children[^1].ClassList);
    }

    [Fact]
    public void TheTopOfThePage_ReadsSeasonsThenEpisodes()
    {
        var metadata = new[] { new MetadataPill { Label = "24 episodes", Kind = "episode_count" } };

        var cut = Render<HeroMetadataPills>(p => p
            .Add(c => c.Metadata, metadata)
            .Add(c => c.EntityType, DetailEntityType.TvShow)
            .Add(c => c.IsWatchHero, true)
            .Add(c => c.UsePrimaryHeroChrome, true)
            .Add(c => c.SeasonCount, 2));

        var text = cut.Find(".tl-detail-watch-metadata-row--facts").TextContent;
        Assert.True(text.IndexOf("2 seasons", StringComparison.Ordinal) is >= 0 && text.IndexOf("2 seasons", StringComparison.Ordinal) < text.IndexOf("24 episodes", StringComparison.Ordinal), text);
    }

    [Fact]
    public void AnAnswerWithoutSeasons_AddsNoSeasonCount()
    {
        var metadata = new[] { new MetadataPill { Label = "24 episodes", Kind = "episode_count" } };

        var cut = Render<HeroMetadataPills>(p => p
            .Add(c => c.Metadata, metadata)
            .Add(c => c.EntityType, DetailEntityType.TvShow)
            .Add(c => c.IsWatchHero, true)
            .Add(c => c.UsePrimaryHeroChrome, true));

        Assert.DoesNotContain("season", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    private static DetailPageViewModel Model(int episodes, bool videoFolder = false) => new()
    {
        Id = Show.ToString("D"),
        EntityType = DetailEntityType.TvShow,
        Title = "Northwatch",
        Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
        PrimaryModule = new DetailPrimaryModuleViewModel
        {
            Kind = DetailPrimaryModuleKind.Episodes,
            Title = videoFolder ? "Videos" : "Episodes",
        },
        SequencePlacement = Placement(episodes, videoFolder),
    };

    private static SequencePlacementViewModel Placement(int episodes, bool videoFolder = false)
    {
        var items = Enumerable.Range(1, episodes).Select(Episode).ToArray();
        return new SequencePlacementViewModel
        {
            ContainerId = Show.ToString("D"),
            SelectedContainerId = Show.ToString("D"),
            ContainerTitle = "Northwatch",
            ContainerLabel = videoFolder ? MediaEngine.Domain.Services.VideoFolderWording.ContainerLabel : "Show",
            ItemLabel = videoFolder ? MediaEngine.Domain.Services.VideoFolderWording.ItemLabel : "Episode",
            ItemPluralLabel = videoFolder ? "Videos" : "Episodes",
            GroupLabel = "Season",
            CurrentGroupKey = "season-1",
            CurrentItem = items[0],
            Groups =
            [
                new SequenceGroupViewModel
                {
                    Key = "season-1",
                    Title = "Season 1",
                    EntityId = Guid.Parse("31000000-0000-0000-0000-000000000009").ToString("D"),
                    OwnedCount = episodes,
                    ItemCount = episodes,
                    TotalKnownItems = episodes,
                    Items = items,
                },
            ],
        };
    }

    private static SequenceItemViewModel Episode(int number) => new()
    {
        Id = $"4{number:D7}-0000-0000-0000-000000000000",
        EntityType = DetailEntityType.TvEpisode,
        Title = $"Episode {number}",
        GroupKey = "season-1",
        GroupTitle = "Season 1",
        PositionLabel = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PositionNumber = number,
        PositionSort = number,
        PositionText = $"E{number}",
        IsOwned = true,
        IsCurrent = number == 1,
    };
}
