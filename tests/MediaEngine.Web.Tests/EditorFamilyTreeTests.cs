using Bunit;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The family tree in the pop-over's left column (universal editor v3 WP2, D3, D11): rows with a tick, an amber "not matched" and a purple
/// dot for changes waiting; clicking a row switches the right side and keeps every item's staged changes; the rows are one tab stop with
/// arrow keys; long lists page in; the column is a dropdown below 1280px. The Engine is a hand-written stub; nothing is written anywhere.
/// </summary>
public sealed class EditorFamilyTreeTests : AsyncBunitContext
{
    private static readonly Guid ShowId = EditTrailTestData.ShowId;
    private static readonly Guid SeasonOne = EditTrailTestData.SeasonOneId;
    private static readonly Guid SeasonTwo = EditTrailTestData.SeasonTwoId;
    private static readonly Guid EpisodeOne = EditTrailTestData.EpisodeOneId;
    private static readonly Guid EpisodeTwo = EditTrailTestData.EpisodeTwoId;
    private static readonly Guid EpisodeThree = EditTrailTestData.EpisodeThreeId;

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<EditTrailItem> _opened = [];

    public EditorFamilyTreeTests()
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

    private async Task<(EditorHost Host, IRenderedComponent<EditorFamilyTree> Tree)> ShowAsync(List<string>? asked = null)
    {
        var api = EngineApiClientStub.Create(stub => EditTrailTestData.RegisterFamily(stub, asked));
        var host = await OpenAsync(api, EpisodeTwo, EpisodeTwoTrail);
        var tree = Render<EditorFamilyTree>(parameters => parameters
            .Add(tree => tree.Family, host.Family)
            .Add(tree => tree.Flow, host.Flow)
            .Add(tree => tree.CanMatch, true));
        tree.WaitForAssertion(() => Assert.Contains(tree.FindAll(".tl-family__row"), row => RowName(row) == "Episode 2"));
        return (host, tree);
    }

    private static string RowName(AngleSharp.Dom.IElement row) => row.QuerySelector(".tl-family__title")!.TextContent.Trim();

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<EditorFamilyTree> tree, string name) =>
        tree.FindAll(".tl-family__row").First(row => RowName(row) == name);

    [Fact]
    public async Task TheColumn_NamesTheFamily_AndMarksMatchedNotMatchedAndTheOpenedItem()
    {
        var (_, tree) = await ShowAsync();

        Assert.Equal("This show", tree.Find(".tl-family__heading").TextContent.Trim());
        Assert.Equal("tree", tree.Find("ul.tl-family__list").GetAttribute("role"));
        Assert.Equal(["Le Continental", "Season 1", "Episode 1", "Episode 2", "Season 2"], tree.FindAll(".tl-family__row").Select(RowName));
        Assert.NotNull(Row(tree, "Episode 1").QuerySelector(".tl-family__state--ok"));
        var warn = Row(tree, "Episode 2").QuerySelector(".tl-family__state--warn")!;
        Assert.Equal("not matched", warn.TextContent.Trim());
        Assert.Contains("No show, season or episode", warn.GetAttribute("title"));
        Assert.Equal("true", Row(tree, "Episode 2").GetAttribute("aria-selected"));
        Assert.Equal("false", Row(tree, "Episode 1").GetAttribute("aria-selected"));
        Assert.Equal("1", Row(tree, "Le Continental").GetAttribute("aria-level"));
        Assert.Equal("3", Row(tree, "Episode 2").GetAttribute("aria-level"));
    }

    [Fact]
    public async Task ClickingARow_SwitchesTheItem_AndKeepsTheChangesStagedOnTheOthers()
    {
        var (host, tree) = await ShowAsync();
        host.Session.Pending.SetField(EpisodeTwo, "description", "Edited first.");

        Row(tree, "Episode 1").Click();
        tree.WaitForAssertion(() => Assert.Equal(EpisodeOne, host.Session.ActiveItem!.EntityId));
        host.Session.Pending.SetField(EpisodeOne, "description", "Edited second.");
        Row(tree, "Le Continental").Click();
        tree.WaitForAssertion(() => Assert.Equal(ShowId, host.Session.ActiveItem!.EntityId));
        host.Session.Pending.SetField(ShowId, "tagline", "A hotel");

        Assert.Equal(3, host.Session.Pending.ChangeCount);
        Assert.Equal([EpisodeOne, ShowId], _opened.Select(item => item.EntityId));
        var dotted = tree.FindAll(".tl-family__row").Where(row => row.QuerySelector(".tl-family__dot") is not null).Select(RowName);
        Assert.Equal(["Episode 1", "Episode 2", "Le Continental"], dotted.Order(StringComparer.Ordinal));
        Assert.Equal("true", Row(tree, "Le Continental").GetAttribute("aria-selected"));
    }

    [Fact]
    public async Task ClickingASeason_SwitchesToItAndOpensItsFiles_InOneStep()
    {
        var asked = new List<string>();
        var (host, tree) = await ShowAsync(asked);

        Row(tree, "Season 2").Click();

        tree.WaitForAssertion(() => Assert.Contains(tree.FindAll(".tl-family__row"), row => RowName(row) == "Episode 3"));
        Assert.Equal(SeasonTwo, host.Session.ActiveItem!.EntityId);
        Assert.Equal([ShowId, SeasonTwo], host.Session.Trail.Select(step => step.EntityId));
        Assert.Contains($"children:{SeasonTwo:N}:page1", asked);
    }

    [Fact]
    public async Task TheRows_AreOneTabStop_AndArrowKeysMoveFocusBetweenThem()
    {
        var (_, tree) = await ShowAsync();

        Assert.Single(tree.FindAll(".tl-family__row[tabindex='0']"));
        Assert.Equal("Episode 2", RowName(tree.Find(".tl-family__row[tabindex='0']")));

        Row(tree, "Episode 2").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowUp" });

        tree.WaitForAssertion(() => Assert.Equal("Episode 1", RowName(tree.Find(".tl-family__row[tabindex='0']"))));
        Assert.Single(tree.FindAll(".tl-family__row[tabindex='0']"));
        Assert.Contains(JSInterop.Invocations, call => call.Identifier == "tuvimaFocusFamilyRow" && call.Arguments[0]?.ToString() == $"tl-family-row-{EpisodeOne:N}");

        Row(tree, "Episode 1").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Home" });
        tree.WaitForAssertion(() => Assert.Equal("Le Continental", RowName(tree.Find(".tl-family__row[tabindex='0']"))));
        Row(tree, "Le Continental").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "End" });
        tree.WaitForAssertion(() => Assert.Equal("Season 2", RowName(tree.Find(".tl-family__row[tabindex='0']"))));
    }

    [Fact]
    public async Task RightOpensAClosedSeason_LeftClosesIt_AndEnterPicksTheRow()
    {
        var (host, tree) = await ShowAsync();

        Row(tree, "Season 2").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowRight" });
        tree.WaitForAssertion(() => Assert.Equal("true", Row(tree, "Season 2").GetAttribute("aria-expanded")));
        Assert.Contains(tree.FindAll(".tl-family__row"), row => RowName(row) == "Episode 3");

        Row(tree, "Season 2").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowLeft" });
        tree.WaitForAssertion(() => Assert.Equal("false", Row(tree, "Season 2").GetAttribute("aria-expanded")));
        Assert.DoesNotContain(tree.FindAll(".tl-family__row"), row => RowName(row) == "Episode 3");

        Row(tree, "Episode 1").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        tree.WaitForAssertion(() => Assert.Equal(EpisodeOne, host.Session.ActiveItem!.EntityId));
    }

    [Fact]
    public async Task WhileTheFamilyIsRead_RowSkeletonsShow_AndTheRightSideIsAlreadyOpen()
    {
        var gate = new TaskCompletionSource<MediaEditorNavigatorDto?>();
        var api = EngineApiClientStub.Create(stub =>
        {
            EditTrailTestData.RegisterFamily(stub);
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorNavigatorAsync), _ => gate.Task);
        });

        var host = await OpenAsync(api, EpisodeTwo, EpisodeTwoTrail);
        var tree = Render<EditorFamilyTree>(parameters => parameters.Add(tree => tree.Family, host.Family).Add(tree => tree.Flow, host.Flow));

        tree.WaitForAssertion(() => Assert.NotEmpty(tree.FindAll(".tl-family__skeleton")));
        Assert.True(host.IsOpen);
        Assert.Equal(EpisodeTwo, host.Session.ActiveItem!.EntityId);
        Assert.Empty(tree.FindAll(".tl-family__row"));

        gate.SetResult(null);
        tree.WaitForAssertion(() => Assert.Empty(tree.FindAll(".tl-family")));
    }

    [Fact]
    public async Task AFilm_HasNoFamilyColumn()
    {
        var api = EngineApiClientStub.Create(stub => EditTrailTestData.RegisterFamily(stub));
        var film = Guid.NewGuid();

        var host = await OpenAsync(api, film, [new EditTrailItem(film, "Dune", "Item")], "Movies");
        var tree = Render<EditorFamilyTree>(parameters => parameters.Add(tree => tree.Family, host.Family).Add(tree => tree.Flow, host.Flow));

        Assert.False(host.Family.Visible);
        Assert.Empty(tree.FindAll(".tl-family"));
        // A film never even asks the Engine for a family.
        Assert.DoesNotContain(((EngineApiClientStub)(object)api).Calls, call => call == nameof(IEngineApiClient.GetMediaEditorNavigatorAsync));
    }

    [Fact]
    public async Task BelowTwelveEightyPixels_TheColumnIsADropdown_OpenedByItsButton_AndClosedWhenARowIsPicked()
    {
        var (host, tree) = await ShowAsync();
        var toggle = tree.Find("button.tl-family__toggle");
        Assert.Equal("false", toggle.GetAttribute("aria-expanded"));
        Assert.Contains("Episode 2", toggle.TextContent);

        toggle.Click();
        Assert.Equal("true", tree.Find("button.tl-family__toggle").GetAttribute("aria-expanded"));
        Assert.Contains("is-open", tree.Find("aside.tl-family").ClassName);

        Row(tree, "Episode 1").Click();
        tree.WaitForAssertion(() => Assert.Equal(EpisodeOne, host.Session.ActiveItem!.EntityId));
        tree.WaitForAssertion(() => Assert.DoesNotContain("is-open", tree.Find("aside.tl-family").ClassName));
    }

    [Fact]
    public async Task APanelHost_PutsTheTreeBeforeTheToolsTheContentAndTheFooter_ForTheTabOrder()
    {
        var api = EngineApiClientStub.Create(stub => EditTrailTestData.RegisterFamily(stub));
        var host = await OpenAsync(api, EpisodeTwo, EpisodeTwoTrail);

        var cut = Render<EditorPanelHost>(parameters => parameters
            .Add(panel => panel.Mode, host.Mode)
            .Add(panel => panel.Flow, host.Flow)
            .Add(panel => panel.TreeContent, builder =>
            {
                builder.OpenComponent<EditorFamilyTree>(0);
                builder.AddComponentParameter(1, nameof(EditorFamilyTree.Family), host.Family);
                builder.AddComponentParameter(2, nameof(EditorFamilyTree.Flow), host.Flow);
                builder.CloseComponent();
            })
            .Add(panel => panel.ChildContent, _ => builder => builder.AddMarkupContent(0, "<div class=\"content-marker\"></div>")));
        cut.WaitForElement(".tl-family__row");

        var html = cut.Markup;
        var header = html.IndexOf("tl-editor-panel__header", StringComparison.Ordinal);
        var tree = html.IndexOf("tl-family", StringComparison.Ordinal);
        var tools = html.IndexOf("tl-editor-window__tabs", StringComparison.Ordinal);
        var content = html.IndexOf("content-marker", StringComparison.Ordinal);
        var footer = html.IndexOf("tl-editor-window__footer", StringComparison.Ordinal);
        Assert.True(header >= 0 && header < tree && tree < tools && tools < content && content < footer);
    }
}
