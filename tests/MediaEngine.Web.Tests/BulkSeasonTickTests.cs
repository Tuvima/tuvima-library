using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Library;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Ticking rows on a show's season list in Editing Mode (universal editor spec D14): a tickable card ticks anywhere it is clicked
/// (the box stays the keyboard control), and ticks never stay behind, out of sight, when the person switches to another season or
/// series. Staged changes live in the page's pending list, so switching asks nothing. The Engine is a hand-written stub.
/// </summary>
public sealed class BulkSeasonTickTests : AsyncBunitContext
{
    private static readonly Guid Show = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid SeasonOneId = Guid.Parse("31000000-0000-0000-0000-000000000001");
    private static readonly Guid SeasonTwoId = Guid.Parse("32000000-0000-0000-0000-000000000002");
    private static readonly string SeriesTwo = "50000000-0000-0000-0000-000000000005";
    private static readonly Guid FirstOpener = Guid.Parse("41100000-0000-0000-0000-000000000000");
    private static readonly Guid FirstFinale = Guid.Parse("41200000-0000-0000-0000-000000000000");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly Queue<SaveOrDiscardChoice> _answers = new();
    private int _asked;

    public BulkSeasonTickTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(_device);
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.PreviewLibraryBatchEditAsync), _ =>
                Task.FromResult(LibraryBatchEditOutcome<LibraryBatchEditPreview>.Ok(new LibraryBatchEditPreview { AffectedCount = 1 })))));
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
    }

    private sealed record Page(EditingModeContext Mode, BulkEditSelection Selection, IRenderedComponent<SequencePlacementPanel> Panel);

    private async Task<Page> StartAsync(bool editing = true, bool withTwoSeries = false)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var session = new ItemFieldEditSession(Services.GetRequiredService<IEngineApiClient>(), _device);
        var mode = new EditingModeContext(_device, session)
        {
            HasPermission = true,
            AskSaveOrDiscard = () =>
            {
                _asked++;
                return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : SaveOrDiscardChoice.Cancel);
            },
        };
        if (editing)
        {
            Assert.True(await mode.EnterAsync());
        }

        var selection = new BulkEditSelection { MediaType = "TV" };
        var panel = Render<SequencePlacementPanel>(parameters => parameters
            .AddCascadingValue(mode)
            .AddCascadingValue(selection)
            .Add(component => component.Placement, Placement(withTwoSeries))
            .Add(component => component.OnContainerSelected, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<SequenceContainerOptionViewModel>(this, option => _containerPicks.Add(option.ContainerId)))
            .Add(component => component.DetailSurface, true));
        return new Page(mode, selection, panel);
    }

    private static void SwitchToSeasonTwo(IRenderedComponent<SequencePlacementPanel> panel)
    {
        panel.Find("button.tl-season-toggle").Click();
        panel.FindAll("button.tl-season-card").Single(card => card.TextContent.Contains("Season 2", StringComparison.Ordinal)).Click();
    }

    private static string ActiveSeason(IRenderedComponent<SequencePlacementPanel> panel) =>
        panel.Find(".tl-season-toggle__label").TextContent.Trim();

    // -- The card ticks anywhere you click it ---------------------------------------------------

    [Fact]
    public async Task ClickingAnywhereOnATickableCard_TicksItAndAgainUnticksIt()
    {
        var page = await StartAsync();
        var card = page.Panel.FindAll("article[data-tickable='true']").First();

        card.Click();

        Assert.Equal([FirstOpener], page.Selection.Ids);
        page.Panel.WaitForAssertion(() => Assert.Contains("is-selected", page.Panel.FindAll("article.tl-series-item").First().ClassList));

        page.Panel.FindAll("article[data-tickable='true']").First().Click();

        Assert.Equal(0, page.Selection.Count);
    }

    [Fact]
    public async Task TheTickBox_StillTicksOnceAndStaysLabelledForKeyboardAndScreenReaders()
    {
        var page = await StartAsync();
        var box = page.Panel.FindAll("article[data-tickable='true'] input[type=checkbox]").First();

        Assert.Equal("Select First season opener", box.GetAttribute("aria-label"));
        box.Change(true);

        Assert.Equal([FirstOpener], page.Selection.Ids);
    }

    [Fact]
    public async Task OutsideEditingMode_ACardIsNotTickableAndHasNoTickBox()
    {
        var page = await StartAsync(editing: false);

        Assert.Empty(page.Panel.FindAll("[data-tickable]"));
        Assert.Empty(page.Panel.FindAll(".tl-row-select"));
        Assert.Equal(0, page.Selection.Count);
    }

    // -- Ticks do not survive a season switch ---------------------------------------------------

    [Fact]
    public async Task SwitchingSeason_ClearsTheTicksFromTheSeasonBeingLeft()
    {
        var page = await StartAsync();
        page.Panel.FindAll("article[data-tickable='true']").First().Click();
        page.Panel.FindAll("article[data-tickable='true']").Last().Click();
        Assert.Equal([FirstOpener, FirstFinale], page.Selection.Ids);

        SwitchToSeasonTwo(page.Panel);

        page.Panel.WaitForAssertion(() => Assert.Equal("Season 2", ActiveSeason(page.Panel)));
        Assert.Equal(0, page.Selection.Count);
        Assert.Equal(0, _asked);
    }

    [Fact]
    public async Task SwitchingSeason_WithNothingTicked_JustSwitches()
    {
        var page = await StartAsync();

        SwitchToSeasonTwo(page.Panel);

        page.Panel.WaitForAssertion(() => Assert.Equal("Season 2", ActiveSeason(page.Panel)));
        Assert.Equal(0, _asked);
    }

    // -- Ticks do not survive a series switch ---------------------------------------------------

    [Fact]
    public async Task SwitchingToAnotherSeries_ClearsTheTicksAndChoosesTheOtherSeriesWithoutAsking()
    {
        var page = await StartAsync(withTwoSeries: true);
        page.Panel.FindAll("article[data-tickable='true']").First().Click();
        Assert.Equal([FirstOpener], page.Selection.Ids);
        var select = page.Panel.FindComponents<AppSelect>()
            .Single(candidate => candidate.Instance.Options?.Any(option => option.Value == SeriesTwo) == true);

        await page.Panel.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(SeriesTwo));

        Assert.Equal(0, page.Selection.Count);
        Assert.Equal([SeriesTwo], _containerPicks);
        Assert.Equal(0, _asked);
    }

    // -- Fixtures ------------------------------------------------------------------------------

    private readonly List<string> _containerPicks = [];

    private static SequencePlacementViewModel Placement(bool withTwoSeries = false)
    {
        var seasonOne = new[] { Episode("11", "season-1", "1", "First season opener"), Episode("12", "season-1", "2", "First season finale") };
        var seasonTwo = new[] { Episode("21", "season-2", "1", "Second season opener"), Episode("22", "season-2", "2", "Second season finale") };
        return new SequencePlacementViewModel
        {
            ContainerId = Show.ToString("D"),
            SelectedContainerId = Show.ToString("D"),
            ContainerTitle = "Northwatch",
            ContainerLabel = "Show",
            ItemLabel = "Episode",
            ItemPluralLabel = "Episodes",
            GroupLabel = "Season",
            CurrentGroupKey = "season-1",
            CurrentItem = seasonOne[0],
            CanChooseContainer = withTwoSeries,
            AvailableContainers = withTwoSeries
                ?
                [
                    new SequenceContainerOptionViewModel { ContainerId = Show.ToString("D"), ContainerTitle = "Northwatch", IsSelected = true },
                    new SequenceContainerOptionViewModel { ContainerId = SeriesTwo, ContainerTitle = "Another series" },
                ]
                : [],
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
        Id = $"4{id}00000-0000-0000-0000-000000000000",
        EntityType = DetailEntityType.TvEpisode,
        Title = title,
        GroupKey = groupKey,
        GroupTitle = groupKey == "season-1" ? "Season 1" : "Season 2",
        PositionLabel = number,
        PositionNumber = int.Parse(number),
        PositionSort = int.Parse(number),
        PositionText = $"E{number}",
        IsOwned = true,
        IsCurrent = id == "11",
    };
}
