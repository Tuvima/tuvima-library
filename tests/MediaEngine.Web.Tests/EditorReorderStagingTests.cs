using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Items;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Putting videos and sections in order from the editor's header (universal editor v5 D19 and WP8 review): Move down in the item dropdown's
/// Reorder mode and in the Folders view's order list each stage the same one order change the arrows of the Match panel stage, and the
/// Section dropdown's Reorder mode stages the order of the sections. Everything only stages; nothing is sent to the Engine.
/// </summary>
public sealed class EditorReorderStagingTests : AsyncBunitContext
{
    private static readonly Guid SectionTwoId = Guid.Parse("a1000000-0000-0000-0000-0000000000a9");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly EditSessionState _session = new(new PendingEditList());
    private VideoOrderResponse _order = new();

    public EditorReorderStagingTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetVideoOrderAsync), _ => Task.FromResult<VideoOrderResponse?>(_order))));
        Render<AppPopoverHost>();
        _session.Begin([new EditTrailItem(EditorHeaderTestData.EpisodeTwoId, "Graded wash", "Item")]);
    }

    private EditSessionFlow NewFlow()
    {
        var api = Services.GetRequiredService<IEngineApiClient>();
        var fields = new ItemFieldEditSession(api);
        var mode = new EditingModeContext(_device, fields) { HasPermission = true };
        return new EditSessionFlow(_session, mode, new EditSessionSaver(api), () => [], () => Task.FromResult(true));
    }

    private IRenderedComponent<EditorHeaderNav> Show(EditorHeaderDto header) =>
        Render<EditorHeaderNav>(parameters => parameters.Add(nav => nav.Header, header).Add(nav => nav.Flow, NewFlow()));

    private static VideoOrderResponse Lessons() => new()
    {
        Groups =
        [
            new VideoOrderGroupDto
            {
                ParentId = EditorHeaderTestData.SeasonOneId,
                Title = "Washes",
                Entries =
                [
                    new VideoOrderEntryDto { Id = EditorHeaderTestData.EpisodeOneId, Title = "Flat wash" },
                    new VideoOrderEntryDto { Id = EditorHeaderTestData.EpisodeTwoId, Title = "Graded wash" },
                ],
            },
        ],
    };

    [Fact]
    public void MoveDown_InTheItemDropdownsReorderMode_StagesOneVideoOrderChange()
    {
        _order = Lessons();
        var cut = Show(EditorHeaderTestData.CourseLesson());
        cut.Find("#tl-hnav-item").Click();
        cut.Find("#tl-hnav-reorder").Click();

        cut.Find("button[aria-label='Move Flat wash down']").Click();

        var staged = Assert.Single(_session.Pending.Changes);
        Assert.Equal(EditSessionChangeKinds.VideoOrder, staged.Kind);
        Assert.Equal(EditorHeaderTestData.SeasonOneId, staged.EntityId);
        Assert.Equal([EditorHeaderTestData.EpisodeTwoId, EditorHeaderTestData.EpisodeOneId], staged.OrderedIds);
    }

    [Fact]
    public void MoveDown_IsAnnouncedInAPoliteLiveRegion()
    {
        _order = Lessons();
        var cut = Show(EditorHeaderTestData.CourseLesson());
        cut.Find("#tl-hnav-item").Click();
        cut.Find("#tl-hnav-reorder").Click();

        cut.Find("button[aria-label='Move Flat wash down']").Click();

        var live = cut.Find("[data-testid=video-order-announce]");
        Assert.Equal("polite", live.GetAttribute("aria-live"));
        Assert.Equal("Moved Flat wash to position 2", live.TextContent.Trim());
    }

    [Fact]
    public void MoveDown_InTheFoldersViewsOrderList_StagesTheSameChange()
    {
        _order = Lessons();
        var cut = Render<EditorVideoOrderList>(parameters => parameters
            .Add(list => list.EntityId, EditorHeaderTestData.ShowId)
            .Add(list => list.SeriesTitle, "Learn Watercolour")
            .Add(list => list.Session, _session)
            .Add(list => list.CanEdit, true)
            .Add(list => list.Compact, true));

        cut.Find("button[aria-label='Move Flat wash down']").Click();

        var staged = Assert.Single(_session.Pending.Changes);
        Assert.Equal(EditSessionChangeKinds.VideoOrder, staged.Kind);
        Assert.Equal(EditorHeaderTestData.SeasonOneId, staged.EntityId);
        Assert.Equal([EditorHeaderTestData.EpisodeTwoId, EditorHeaderTestData.EpisodeOneId], staged.OrderedIds);
    }

    [Fact]
    public void TheSectionDropdownsReorderMode_StagesTheOrderOfTheSections_OnTheSeries()
    {
        _order = new VideoOrderResponse
        {
            Groups =
            [
                new VideoOrderGroupDto { ParentId = EditorHeaderTestData.SeasonOneId, Title = "Washes", Entries = [new VideoOrderEntryDto { Id = EditorHeaderTestData.EpisodeOneId, Title = "Flat wash" }] },
                new VideoOrderGroupDto { ParentId = SectionTwoId, Title = "Glazing", Entries = [new VideoOrderEntryDto { Id = EditorHeaderTestData.EpisodeTwoId, Title = "Thin glaze" }] },
            ],
        };
        var course = EditorHeaderTestData.CourseLesson();
        var header = new EditorHeaderDto
        {
            Kind = course.Kind,
            Root = course.Root,
            CurrentId = course.CurrentId,
            Label = course.Label,
            ShowHome = true,
            HomeId = course.HomeId,
            SecondLevel = new EditorHeaderSecondLevelDto
            {
                Label = "Section",
                CurrentId = EditorHeaderTestData.SeasonOneId,
                Entries =
                [
                    new EditorHeaderLevelEntryDto { Id = EditorHeaderTestData.SeasonOneId, Title = "Washes", Number = 1, ItemCount = 1 },
                    new EditorHeaderLevelEntryDto { Id = SectionTwoId, Title = "Glazing", Number = 2, ItemCount = 1 },
                ],
            },
            ShowItemDropdown = true,
            Level = course.Level,
            Config = course.Config,
        };
        var cut = Show(header);
        cut.Find("#tl-hnav-second").Click();
        cut.Find("#tl-hnav-reorder-sections").Click();

        cut.Find("[data-testid=reorder-sections] button[aria-label='Move Washes down']").Click();

        var staged = Assert.Single(_session.Pending.Changes);
        Assert.Equal(EditSessionChangeKinds.VideoOrder, staged.Kind);
        Assert.Equal(EditorHeaderTestData.ShowId, staged.EntityId);
        Assert.Equal([SectionTwoId, EditorHeaderTestData.SeasonOneId], staged.OrderedIds);
        var rows = EditReviewRows.Build(_session.Pending, _ => "Learn Watercolour", null);
        Assert.Contains(rows, row => row.After is not null && row.After.Contains("2 sections in a new order", StringComparison.Ordinal));
    }
}
