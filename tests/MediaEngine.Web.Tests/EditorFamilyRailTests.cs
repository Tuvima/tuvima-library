using Bunit;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The rail of dropdowns across the top of the editor (universal editor v4 D2, acceptance A2): from an episode it reaches the show, another
/// season and another episode; changes staged on each survive the switching; a film shows no rail; long lists are searched and paged; the
/// crumbs carry the matched and waiting marks. The Engine is a hand-written stub; nothing is written anywhere.
/// </summary>
public sealed class EditorFamilyRailTests : AsyncBunitContext
{
    private static readonly Guid ShowId = EditTrailTestData.ShowId;
    private static readonly Guid SeasonOne = EditTrailTestData.SeasonOneId;
    private static readonly Guid SeasonTwo = EditTrailTestData.SeasonTwoId;
    private static readonly Guid EpisodeOne = EditTrailTestData.EpisodeOneId;
    private static readonly Guid EpisodeTwo = EditTrailTestData.EpisodeTwoId;
    private static readonly Guid EpisodeThree = EditTrailTestData.EpisodeThreeId;

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<EditTrailItem> _opened = [];

    public EditorFamilyRailTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
    }

    private async Task<EditorHost> OpenAsync(IEngineApiClient api, Guid entityId, IReadOnlyList<EditTrailItem> trail, string mediaType = "TV")
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var host = new EditorHost(api, _device, new ServiceCollection().BuildServiceProvider(), NullLogger<EditorHost>.Instance);
        var binding = new EditorBinding(_device)
        {
            EntityId = entityId,
            HasPermission = true,
            MediaTypes = () => [mediaType],
            Trail = () => trail,
            OpenItem = item =>
            {
                _opened.Add(item);
                return Task.FromResult(true);
            },
        };
        Assert.True(await host.OpenAsync(binding, entityId));
        return host;
    }

    private static IReadOnlyList<EditTrailItem> EpisodeTwoTrail =>
    [
        new(ShowId, "Le Continental", EditTrailKinds.Show),
        new(SeasonOne, "Season 1", EditTrailKinds.Season),
        new(EpisodeTwo, "Episode 2", EditTrailKinds.Episode),
    ];

    private async Task<(EditorHost Host, IRenderedComponent<EditorFamilyRail> Rail)> ShowAsync(List<string>? asked = null)
    {
        var api = EngineApiClientStub.Create(stub => EditTrailTestData.RegisterFamily(stub, asked));
        var host = await OpenAsync(api, EpisodeTwo, EpisodeTwoTrail);
        var rail = Render<EditorFamilyRail>(parameters => parameters
            .Add(rail => rail.Family, host.Family)
            .Add(rail => rail.Flow, host.Flow)
            .Add(rail => rail.CanMatch, true));
        rail.WaitForAssertion(() => Assert.Equal(["Show", "Season", "Episode"], Levels(rail)));
        return (host, rail);
    }

    private static List<string> Levels(IRenderedComponent<EditorFamilyRail> rail) =>
        rail.FindAll(".tl-rail__crumb[data-level]").Select(crumb => crumb.GetAttribute("data-level")!.Split(' ')[0]).ToList();

    private static AngleSharp.Dom.IElement Crumb(IRenderedComponent<EditorFamilyRail> rail, string level) =>
        rail.FindAll(".tl-rail__crumb[data-level]").First(crumb => crumb.GetAttribute("data-level")!.Split(' ')[0] == level);

    private static string OptionName(AngleSharp.Dom.IElement option) =>
        option.QuerySelector(".tl-rail__text > span")!.TextContent.Trim().Split(" · ").Last();

    private static AngleSharp.Dom.IElement Option(IRenderedComponent<EditorFamilyRail> rail, string name) =>
        rail.FindAll(".tl-rail__option").First(option => OptionName(option) == name);

    private static void Pick(IRenderedComponent<EditorFamilyRail> rail, string level, string name)
    {
        Crumb(rail, level).Click();
        rail.WaitForAssertion(() => Assert.Contains(rail.FindAll(".tl-rail__option"), option => OptionName(option) == name));
        Option(rail, name).Click();
    }

    [Fact]
    public async Task AnEpisode_ShowsTheRail_WithTheShowSeasonAndEpisodeAsDropdowns()
    {
        var (_, rail) = await ShowAsync();

        Assert.Equal(["Le Continental", "Season 1", "Episode 2"], rail.FindAll(".tl-rail__crumb[data-level]").Select(crumb => crumb.QuerySelector(".tl-rail__text > span")!.TextContent.Trim()));
        Assert.Equal("Episode 2", Crumb(rail, "Episode").GetAttribute("data-level"));
        Assert.Equal("true", Crumb(rail, "Episode").GetAttribute("aria-current"));
        Assert.Equal("false", Crumb(rail, "Season").GetAttribute("aria-expanded"));
        // The only show has nothing to choose from, so its crumb is not a dropdown.
        Assert.Null(Crumb(rail, "Show").GetAttribute("aria-haspopup"));
        Assert.Equal("listbox", Crumb(rail, "Episode").GetAttribute("aria-haspopup"));
    }

    [Fact]
    public async Task TheRail_ReachesTheShow_AnotherSeason_AndAnotherEpisode_KeepingEveryItemsStagedChanges()
    {
        var (host, rail) = await ShowAsync();
        host.Session.Pending.SetField(EpisodeTwo, "description", "Edited first.");

        // Another episode of the same season.
        Pick(rail, "Episode", "Episode 1");
        rail.WaitForAssertion(() => Assert.Equal(EpisodeOne, host.Session.ActiveItem!.EntityId));
        host.Session.Pending.SetField(EpisodeOne, "description", "Edited second.");

        // The episode left behind lists its waiting change in the dropdown.
        Crumb(rail, "Episode").Click();
        rail.WaitForAssertion(() => Assert.Contains(rail.FindAll(".tl-rail__option"), option => OptionName(option) == "Episode 2"));
        Assert.Equal("1 change", Option(rail, "Episode 2").QuerySelector(".tl-rail__waiting")!.TextContent.Trim());
        Crumb(rail, "Episode").Click();

        // The show above it.
        Crumb(rail, "Show").Click();
        rail.WaitForAssertion(() => Assert.Equal(ShowId, host.Session.ActiveItem!.EntityId));
        host.Session.Pending.SetField(ShowId, "tagline", "A hotel");

        // Another season, opened as it is picked.
        rail.WaitForAssertion(() => Assert.Contains(Levels(rail), level => level == "Season"));
        Pick(rail, "Season", "Season 2");
        rail.WaitForAssertion(() => Assert.Equal(SeasonTwo, host.Session.ActiveItem!.EntityId));

        // Its episode, chosen from the empty "Choose an episode" dropdown the rail offers under a season.
        Assert.Equal("Choose an episode", Crumb(rail, "Episode").QuerySelector(".tl-rail__text > span")!.TextContent.Trim());
        Pick(rail, "Episode", "Episode 3");
        rail.WaitForAssertion(() => Assert.Equal(EpisodeThree, host.Session.ActiveItem!.EntityId));
        host.Session.Pending.SetField(EpisodeThree, "description", "Edited third.");

        Assert.Equal(4, host.Session.Pending.ChangeCount);
        Assert.Equal([EpisodeOne, ShowId, SeasonTwo, EpisodeThree], _opened.Select(item => item.EntityId));
        // The show and the episode being edited carry the waiting dot on their crumbs; the dropdown lists the count against the item.
        Assert.NotNull(Crumb(rail, "Show").QuerySelector(".tl-rail__dot"));
        Assert.NotNull(Crumb(rail, "Episode").QuerySelector(".tl-rail__dot"));
    }

    [Fact]
    public async Task ADropdown_ListsSiblingsWithTheirMatchedMark_AndTheCurrentOneIsSelected()
    {
        var (_, rail) = await ShowAsync();

        Crumb(rail, "Episode").Click();

        rail.WaitForAssertion(() => Assert.Contains(rail.FindAll(".tl-rail__option"), option => OptionName(option) == "Episode 1"));
        Assert.Equal(["Episode 1", "Episode 2"], rail.FindAll(".tl-rail__option").Select(OptionName));
        Assert.NotNull(Option(rail, "Episode 1").QuerySelector(".tl-rail__mark--ok"));
        var unmatched = Option(rail, "Episode 2");
        Assert.NotNull(unmatched.QuerySelector(".tl-rail__mark--warn"));
        Assert.Equal("Not matched", unmatched.QuerySelector("small")!.TextContent.Trim());
        Assert.Equal("true", unmatched.GetAttribute("aria-selected"));
        Assert.Equal("false", Option(rail, "Episode 1").GetAttribute("aria-selected"));
    }

    [Fact]
    public async Task OpeningADropdown_ClosesTheOther_AndAClickOutsideOrEscapeClosesIt()
    {
        var (_, rail) = await ShowAsync();

        Crumb(rail, "Season").Click();
        rail.WaitForAssertion(() => Assert.Equal("true", Crumb(rail, "Season").GetAttribute("aria-expanded")));
        Crumb(rail, "Episode").Click();
        rail.WaitForAssertion(() => Assert.Equal("true", Crumb(rail, "Episode").GetAttribute("aria-expanded")));
        Assert.Equal("false", Crumb(rail, "Season").GetAttribute("aria-expanded"));
        Assert.Single(rail.FindAll(".tl-rail__panel"));

        rail.Find(".tl-rail__panel").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        rail.WaitForAssertion(() => Assert.Empty(rail.FindAll(".tl-rail__panel")));

        Crumb(rail, "Season").Click();
        // A click anywhere else is heard by the shared popover, which reports it as a dismissal.
        await rail.InvokeAsync(() => rail.FindComponent<AppPopover>().Instance.Dismiss());
        rail.WaitForAssertion(() => Assert.Empty(rail.FindAll(".tl-rail__panel")));
    }

    [Fact]
    public async Task PickingAnItemThatIsNotMatched_OpensItOnMatch()
    {
        var (host, rail) = await ShowAsync();
        Pick(rail, "Episode", "Episode 1");
        rail.WaitForAssertion(() => Assert.Equal(EpisodeOne, host.Session.ActiveItem!.EntityId));
        Assert.Equal(EditTool.Details, host.Session.ActiveTool);

        // Episode 2 is not matched: picking it from the rail goes straight to Match, where the search is already filled in.
        Pick(rail, "Episode", "Episode 2");

        rail.WaitForAssertion(() => Assert.Equal(EpisodeTwo, host.Session.ActiveItem!.EntityId));
        rail.WaitForAssertion(() => Assert.Equal(EditTool.Match, host.Session.ActiveTool));
    }

    [Fact]
    public async Task WhileTheFamilyIsRead_SkeletonsHoldThePlace_AndTheRightSideIsAlreadyOpen()
    {
        var gate = new TaskCompletionSource<MediaEditorNavigatorDto?>();
        var api = EngineApiClientStub.Create(stub =>
        {
            EditTrailTestData.RegisterFamily(stub);
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorNavigatorAsync), _ => gate.Task);
        });

        var host = await OpenAsync(api, EpisodeTwo, EpisodeTwoTrail);
        var rail = Render<EditorFamilyRail>(parameters => parameters.Add(rail => rail.Family, host.Family).Add(rail => rail.Flow, host.Flow));

        rail.WaitForAssertion(() => Assert.NotEmpty(rail.FindAll(".tl-rail__skeleton")));
        Assert.True(host.IsOpen);
        Assert.Equal(EpisodeTwo, host.Session.ActiveItem!.EntityId);
        Assert.Empty(rail.FindAll(".tl-rail__crumb"));

        gate.SetResult(null);
        rail.WaitForAssertion(() => Assert.Empty(rail.FindAll(".tl-rail")));
    }

    [Fact]
    public async Task AFilm_ShowsNoRail_AndNeverAsksTheEngineForAFamily()
    {
        var api = EngineApiClientStub.Create(stub => EditTrailTestData.RegisterFamily(stub));
        var film = Guid.NewGuid();

        var host = await OpenAsync(api, film, [new EditTrailItem(film, "Dune", "Item")], "Movies");
        var rail = Render<EditorFamilyRail>(parameters => parameters.Add(rail => rail.Family, host.Family).Add(rail => rail.Flow, host.Flow));

        Assert.False(host.Family.Visible);
        Assert.Empty(rail.FindAll(".tl-rail"));
        Assert.DoesNotContain(((EngineApiClientStub)(object)api).Calls, call => call == nameof(IEngineApiClient.GetMediaEditorNavigatorAsync));
    }

    [Fact]
    public async Task TheFamily_SitsInsideTheHeader_TheShowAsAFixedTileAndSeasonAndEpisodeAsDropdownsUnderTheTitle()
    {
        var api = EngineApiClientStub.Create(stub => EditTrailTestData.RegisterFamily(stub));
        var host = await OpenAsync(api, EpisodeTwo, EpisodeTwoTrail);

        Services.AddSingleton(api);
        var cut = Render<EditorPanelHost>(parameters => parameters
            .Add(panel => panel.Mode, host.Mode)
            .Add(panel => panel.Flow, host.Flow)
            .Add(panel => panel.ItemHeader, builder =>
            {
                builder.OpenComponent<EditorItemHeader>(0);
                builder.AddComponentParameter(1, nameof(EditorItemHeader.Flow), host.Flow);
                builder.AddComponentParameter(2, nameof(EditorItemHeader.Fields), host.Mode.Fields);
                builder.AddComponentParameter(3, nameof(EditorItemHeader.Family), host.Family);
                builder.AddComponentParameter(4, nameof(EditorItemHeader.HasFamily), true);
                builder.AddComponentParameter(5, nameof(EditorItemHeader.Tile), (RenderFragment)(inner =>
                {
                    inner.OpenComponent<EditorFamilyRail>(0);
                    inner.AddComponentParameter(1, nameof(EditorFamilyRail.Family), host.Family);
                    inner.AddComponentParameter(2, nameof(EditorFamilyRail.Flow), host.Flow);
                    inner.AddComponentParameter(3, nameof(EditorFamilyRail.Part), EditorFamilyRailPart.Tile);
                    inner.CloseComponent();
                }));
                builder.AddComponentParameter(6, nameof(EditorItemHeader.Selects), (RenderFragment)(inner =>
                {
                    inner.OpenComponent<EditorFamilyRail>(0);
                    inner.AddComponentParameter(1, nameof(EditorFamilyRail.Family), host.Family);
                    inner.AddComponentParameter(2, nameof(EditorFamilyRail.Flow), host.Flow);
                    inner.AddComponentParameter(3, nameof(EditorFamilyRail.Part), EditorFamilyRailPart.Selects);
                    inner.CloseComponent();
                }));
                builder.CloseComponent();
            })
            .Add(panel => panel.ChildContent, _ => builder => builder.AddMarkupContent(0, "<div class=\"content-marker\"></div>")));
        cut.WaitForAssertion(() => Assert.Equal(["Show"], cut.FindAll("header.tl-editor-header .tl-editor-header__tile .tl-rail__crumb").Select(LevelOf)));
        cut.WaitForAssertion(() => Assert.Equal(["Season", "Episode"], cut.FindAll("header.tl-editor-header .tl-editor-header__selects .tl-rail__crumb").Select(LevelOf)));

        // The show is a fixed tile (no list), the dropdowns are listboxes, and there is no separate rail row above the header.
        var tile = cut.Find(".tl-editor-header__tile .tl-rail__crumb--tile");
        Assert.Null(tile.GetAttribute("aria-haspopup"));
        Assert.Equal("Le Continental", tile.QuerySelector(".tl-rail__text > span")!.TextContent.Trim());
        Assert.All(cut.FindAll(".tl-editor-header__selects .tl-rail__crumb"), crumb => Assert.Equal("listbox", crumb.GetAttribute("aria-haspopup")));
        Assert.Empty(cut.FindAll(".tl-editor-window__top"));
        Assert.Empty(cut.FindAll(".tl-rail__sep"));
        // A show's seasons and episodes draw no second picture beside the show's tile.
        Assert.Empty(cut.FindAll(".tl-editor-header__cover"));

        var html = cut.Markup;
        var header = html.IndexOf("tl-editor-header", StringComparison.Ordinal);
        var tools = html.IndexOf("tl-editor-window__tabs", StringComparison.Ordinal);
        var content = html.IndexOf("content-marker", StringComparison.Ordinal);
        var footer = html.IndexOf("tl-editor-window__footer", StringComparison.Ordinal);
        Assert.True(header >= 0 && header < tools && tools < content && content < footer);
        Assert.Contains("Discard", cut.Find(".tl-editor-window__cancel").TextContent);
    }

    [Fact]
    public async Task TheShowTile_IsTheItemBeingEdited_WhenTheShowItselfIsOpen_AndStillGoesToTheShowFromAnEpisode()
    {
        var api = EngineApiClientStub.Create(stub => EditTrailTestData.RegisterFamily(stub));
        var host = await OpenAsync(api, EpisodeTwo, EpisodeTwoTrail);
        var tile = Render<EditorFamilyRail>(parameters => parameters
            .Add(rail => rail.Family, host.Family)
            .Add(rail => rail.Flow, host.Flow)
            .Add(rail => rail.Part, EditorFamilyRailPart.Tile));
        tile.WaitForElement(".tl-rail__crumb--tile");
        Assert.Null(tile.Find(".tl-rail__crumb--tile").GetAttribute("aria-current"));

        tile.Find(".tl-rail__crumb--tile").Click();

        tile.WaitForAssertion(() => Assert.Contains(_opened, item => item.EntityId == ShowId));
    }

    private static string LevelOf(AngleSharp.Dom.IElement crumb) => crumb.GetAttribute("data-level")!.Split(' ')[0];
}
