using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Items;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Match tool's series chooser and order list for videos (universal editor v3.1): choosing a series, starting one, putting a series in
/// order by arrow or by drop, and the rename switch. Everything only stages a change for Review changes.
/// </summary>
public sealed class EditorVideoSeriesPanelTests : AsyncBunitContext
{
    private static readonly Guid VideoId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid CourseId = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
    private static readonly Guid OtherId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid A = Guid.Parse("dddddddd-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("dddddddd-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("dddddddd-0000-0000-0000-00000000000c");

    private readonly EditSessionState _session = new(new PendingEditList());
    private VideoSeriesStateResponse _state = new() { Supported = true };
    private VideoOrderResponse _order = new();

    public EditorVideoSeriesPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        _session.Begin([new EditTrailItem(VideoId, "Interview with the chef", "Item")]);
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetVideoSeriesAsync), _ => Task.FromResult<VideoSeriesStateResponse?>(_state));
            stub.SetHandler(nameof(IEngineApiClient.GetVideoOrderAsync), _ => Task.FromResult<VideoOrderResponse?>(_order));
        }));
    }

    [Fact]
    public async Task AVideoOnItsOwn_OffersTheSeriesItCouldJoin_AndStagesTheChoiceOnly()
    {
        _state = new VideoSeriesStateResponse
        {
            Supported = true,
            Targets = [new VideoSeriesTargetDto { Id = CourseId, Title = "Cooking with Sam", VideoCount = 6 }],
        };
        var cut = RenderPanel();

        await cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync(CourseId.ToString()));
        cut.Render();

        var staged = Assert.Single(_session.Pending.Changes);
        Assert.Equal(EditSessionChangeKinds.VideoSeries, staged.Kind);
        Assert.Equal(VideoId, staged.EntityId);
        Assert.Equal(CourseId.ToString(), staged.TargetId);
        Assert.Contains("joins Cooking with Sam", cut.Markup);
        Assert.Contains("not moved or renamed", cut.Markup);
    }

    [Fact]
    public async Task StartingANewSeries_AsksForAName_ThenStagesIt()
    {
        var cut = RenderPanel();

        await cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync("new"));
        cut.Render();
        cut.Find("input:not([role=switch])").Input("Garden talks");
        cut.Find("button:contains('Use this name')").Click();

        var staged = Assert.Single(_session.Pending.Changes);
        Assert.Equal("new:Garden talks", staged.Token);
        Assert.Contains("A new series, Garden talks", cut.Markup);
    }

    [Fact]
    public async Task AVideoInASeries_CanLeaveIt()
    {
        _state = new VideoSeriesStateResponse { Supported = true, InSeries = true, SeriesTitle = "Cooking with Sam" };
        var cut = RenderPanel();

        await cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync("none"));

        var staged = Assert.Single(_session.Pending.Changes);
        Assert.True(EditSessionVideo.IsNoSeries(staged.Token));
    }

    [Fact]
    public void ANonVideo_ShowsNothing()
    {
        var cut = Render<EditorVideoSeriesPanel>(p => p
            .Add(c => c.Subject, new EditorMatchSubject(VideoId, "Music", "track", "track", "Song", null, null, null, new Dictionary<string, string>(), []))
            .Add(c => c.Session, _session)
            .Add(c => c.CanEdit, true));

        Assert.DoesNotContain("tl-vseries", cut.Markup);
    }

    [Fact]
    public void TheArrows_PutTheSeriesInANewOrder_AndStageItWithoutMovingAFile()
    {
        _order = Order(canRename: false);
        var cut = RenderPanel();

        cut.FindAll("button[aria-label='Move Kitchen tour up']")[0].Click();

        var staged = Assert.Single(_session.Pending.Changes);
        Assert.Equal(EditSessionChangeKinds.VideoOrder, staged.Kind);
        Assert.Equal(CourseId, staged.EntityId);
        Assert.Equal([B, A, C], staged.OrderedIds);
        var titles = cut.FindAll(".tl-vseries__title").Select(node => node.TextContent).ToList();
        Assert.Equal(["Kitchen tour", "The first dinner", "Bread day"], titles);
        Assert.Contains("new order", cut.Markup);
    }

    [Fact]
    public async Task ADrop_StagesTheSameOrderTheArrowsWould()
    {
        _order = Order(canRename: false);
        var cut = RenderPanel();

        await cut.InvokeAsync(() => cut.Instance.OnDropAsync(0, 2, 0, 0));

        Assert.Equal([C, A, B], Assert.Single(_session.Pending.Changes).OrderedIds);
    }

    [Fact]
    public void TheRenameSwitch_IsOffByDefault_AndOnlyShownForManagedFolders()
    {
        _order = Order(canRename: false);
        Assert.DoesNotContain("Rename the files to match this order", RenderPanel().Markup);

        _order = Order(canRename: true);
        var cut = RenderPanel();
        Assert.Contains("Rename the files to match this order", cut.Markup);
        Assert.Empty(_session.Pending.Changes);

        cut.Find("input[role=switch]").Change(true);

        var staged = Assert.Single(_session.Pending.Changes);
        Assert.Equal(EditSessionChangeKinds.VideoRename, staged.Kind);
        Assert.Equal(CourseId, staged.EntityId);
    }

    [Fact]
    public void WhenEditingIsNotAllowed_NothingCanBeChanged()
    {
        _order = Order(canRename: true);
        var cut = RenderPanel(canEdit: false);

        Assert.All(cut.FindAll("button[aria-label^='Move ']"), button => Assert.True(button.HasAttribute("disabled")));
    }

    private static VideoOrderResponse Order(bool canRename) => new()
    {
        Groups =
        [
            new VideoOrderGroupDto
            {
                ParentId = CourseId,
                Title = "Cooking with Sam",
                CanRenameFiles = canRename,
                Entries = [new VideoOrderEntryDto { Id = A, Title = "The first dinner" }, new VideoOrderEntryDto { Id = B, Title = "Kitchen tour" }, new VideoOrderEntryDto { Id = C, Title = "Bread day" }],
            },
        ],
    };

    private IRenderedComponent<EditorVideoSeriesPanel> RenderPanel(bool canEdit = true) =>
        Render<EditorVideoSeriesPanel>(p => p
            .Add(c => c.Subject, new EditorMatchSubject(VideoId, "Videos", "default", "video", "Interview with the chef", null, null, null, new Dictionary<string, string>(), []))
            .Add(c => c.Session, _session)
            .Add(c => c.CanEdit, canEdit));
}
