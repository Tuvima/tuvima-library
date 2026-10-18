using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A season opened on its own (D13) arrives as its show's page with <c>CurrentGroupKey</c> naming that season. The
/// show's season control and episode list must open on it, not on the first season or the one being watched.
/// </summary>
public sealed class SeasonSelectionTests : AsyncBunitContext
{
    private static readonly Guid Show = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid SeasonOneId = Guid.Parse("31000000-0000-0000-0000-000000000001");
    private static readonly Guid SeasonTwoId = Guid.Parse("32000000-0000-0000-0000-000000000002");

    public SeasonSelectionTests()
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
    public void TheSeasonControlOpensOnTheRequestedSeason()
    {
        var cut = RenderShow("season-2");

        var toggle = cut.Find("button.tl-season-toggle");
        Assert.Equal("Season 2", cut.Find(".tl-season-toggle__label").TextContent.Trim());
        Assert.Contains("Season 2 selected", toggle.GetAttribute("aria-label"));
    }

    [Fact]
    public void OnlyTheRequestedSeasonsChipIsSelectedInTheSeasonList()
    {
        var cut = RenderShow("season-2");

        cut.Find("button.tl-season-toggle").Click();

        var chips = cut.FindAll("button.tl-season-card");
        Assert.Equal(["Season 1", "Season 2"], chips.Select(ChipLabel));
        var selected = Assert.Single(chips, chip => chip.ClassList.Contains("is-selected"));
        Assert.Equal("Season 2", ChipLabel(selected));
        Assert.True(selected.HasAttribute("aria-selected"));
        Assert.True(selected.HasAttribute("aria-current"));
        Assert.All(chips.Where(chip => !ReferenceEquals(chip, selected)), chip =>
        {
            Assert.False(chip.HasAttribute("aria-selected"));
            Assert.False(chip.HasAttribute("aria-current"));
        });
    }

    [Fact]
    public void TheEpisodeListShowsTheRequestedSeasonsEpisodesOnly()
    {
        var cut = RenderShow("season-2");

        var text = cut.Markup;
        Assert.Contains("Second season opener", text);
        Assert.Contains("Second season finale", text);
        Assert.DoesNotContain("First season opener", text);
        Assert.DoesNotContain("First season finale", text);
    }

    [Fact]
    public void WithoutARequestedSeasonTheShowOpensOnTheSeasonTheEngineChose()
    {
        var cut = RenderShow("season-1");

        Assert.Equal("Season 1", cut.Find(".tl-season-toggle__label").TextContent.Trim());
        Assert.Contains("First season opener", cut.Markup);
        Assert.DoesNotContain("Second season opener", cut.Markup);
    }

    private static string ChipLabel(AngleSharp.Dom.IElement chip) => chip.QuerySelector("strong")!.TextContent.Trim();

    private IRenderedComponent<SequencePlacementPanel> RenderShow(string currentGroupKey) =>
        Render<SequencePlacementPanel>(p => p
            .Add(c => c.Placement, Placement(currentGroupKey))
            .Add(c => c.DetailSurface, true));

    private static SequencePlacementViewModel Placement(string currentGroupKey)
    {
        var seasonOne = new[] { Episode("11", "season-1", "1", "First season opener"), Episode("12", "season-1", "2", "First season finale") };
        var seasonTwo = new[] { Episode("21", "season-2", "1", "Second season opener"), Episode("22", "season-2", "2", "Second season finale") };
        var current = currentGroupKey == "season-2" ? seasonTwo[0] : seasonOne[0];
        return new SequencePlacementViewModel
        {
            ContainerId = Show.ToString("D"),
            SelectedContainerId = Show.ToString("D"),
            ContainerTitle = "Northwatch",
            ContainerLabel = "Show",
            ItemLabel = "Episode",
            ItemPluralLabel = "Episodes",
            GroupLabel = "Season",
            CurrentGroupKey = currentGroupKey,
            CurrentItem = current,
            Groups =
            [
                Group("season-1", "Season 1", SeasonOneId, seasonOne),
                Group("season-2", "Season 2", SeasonTwoId, seasonTwo),
            ],
        };
    }

    private static SequenceGroupViewModel Group(string key, string title, Guid seasonId, SequenceItemViewModel[] items) => new()
    {
        Key = key,
        Title = title,
        EntityId = seasonId.ToString("D"),
        OwnedCount = items.Length,
        ItemCount = items.Length,
        TotalKnownItems = items.Length,
        Items = items,
    };

    private static SequenceItemViewModel Episode(string id, string groupKey, string number, string title) => new()
    {
        Id = $"4{id}000000-0000-0000-0000-000000000000",
        EntityType = DetailEntityType.TvEpisode,
        Title = title,
        GroupKey = groupKey,
        GroupTitle = groupKey == "season-1" ? "Season 1" : "Season 2",
        PositionLabel = number,
        PositionNumber = int.Parse(number),
        PositionSort = int.Parse(number),
        PositionText = $"E{number}",
        IsOwned = true,
        IsCurrent = groupKey == "season-2" ? id == "21" : id == "11",
    };
}
