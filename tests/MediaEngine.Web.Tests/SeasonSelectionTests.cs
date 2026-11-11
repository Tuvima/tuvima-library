using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
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

        Assert.Equal("season-2", SeasonSelect(cut).Instance.Value);
        Assert.Empty(cut.FindAll("button.tl-season-toggle"));
    }

    [Fact]
    public void TheSeasonControlIsADropdownListingEachSeasonWithItsEpisodeCount()
    {
        var cut = RenderShow("season-2");

        var options = SeasonSelect(cut).Instance.Options!.ToList();
        Assert.Equal(["Season 1 · 2 episodes", "Season 2 · 2 episodes"], options.Select(option => option.Label));
        // Switching seasons never swaps the episode row for a row of season pictures.
        Assert.Empty(cut.FindAll(".tl-season-card"));
        Assert.Empty(cut.FindAll(".tl-season-browser"));
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

        Assert.Equal("season-1", SeasonSelect(cut).Instance.Value);
        Assert.Contains("First season opener", cut.Markup);
        Assert.DoesNotContain("Second season opener", cut.Markup);
    }

    private static IRenderedComponent<AppSelect> SeasonSelect(IRenderedComponent<SequencePlacementPanel> panel) =>
        panel.FindComponents<AppSelect>().Single(select => select.Instance.Options!.Any(option => option.Label.StartsWith("Season", StringComparison.Ordinal)));

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
