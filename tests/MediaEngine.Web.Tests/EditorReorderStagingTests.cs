using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Items;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Putting videos and sections in order from the editor's header (universal editor v5 D19 and WP8 review, v6 D23): in the item and Section
/// dropdowns' Reorder mode the new order is only held until the shared Reorder bar's Save order stages it as one order change (Cancel and
/// Escape stage nothing), and the Folders view's order list stages as it goes. Everything only stages; nothing is sent to the Engine.
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

    private IRenderedComponent<EditorHeaderNav> EnterReorder(VideoOrderResponse? order = null)
    {
        _order = order ?? Lessons();
        var cut = Show(EditorHeaderTestData.CourseLesson());
        cut.Find("#tl-hnav-item").Click();
        cut.Find("#tl-hnav-reorder").Click();
        return cut;
    }

    private static string[] TitlesInOrder(IRenderedComponent<EditorHeaderNav> cut) =>
        [.. cut.FindAll("[data-testid=reorder-videos] .tl-vorder__title").Select(title => title.TextContent.Trim())];

    private static string[] ItemTitlesInOrder(IRenderedComponent<EditorHeaderNav> cut) =>
        [.. cut.FindAll(".tl-hnav__option[data-state] .tl-hnav__text > b").Select(title => title.TextContent.Trim())];

    [Fact]
    public void MoveDown_InTheItemDropdownsReorderMode_StagesNothingUntilSaveOrder()
    {
        var cut = EnterReorder();

        cut.Find("button[aria-label='Move Flat wash down']").Click();

        Assert.Empty(_session.Pending.Changes);
        Assert.Equal(["Graded wash", "Flat wash"], TitlesInOrder(cut));
    }

    [Fact]
    public void SaveOrder_InTheItemDropdownsReorderMode_StagesOneVideoOrderChange()
    {
        var cut = EnterReorder();
        cut.Find("button[aria-label='Move Flat wash down']").Click();

        cut.Find("[data-reorder-save]").Click();

        var staged = Assert.Single(_session.Pending.Changes);
        Assert.Equal(EditSessionChangeKinds.VideoOrder, staged.Kind);
        Assert.Equal(EditorHeaderTestData.SeasonOneId, staged.EntityId);
        Assert.Equal([EditorHeaderTestData.EpisodeTwoId, EditorHeaderTestData.EpisodeOneId], staged.OrderedIds);
        Assert.Empty(cut.FindAll("[data-reorder-bar]"));
        Assert.Equal(["Graded wash", "Flat wash"], ItemTitlesInOrder(cut));
    }

    [Fact]
    public void SaveOrder_ShowsInReviewChangesAsOneRow()
    {
        var cut = EnterReorder();
        cut.Find("button[aria-label='Move Flat wash down']").Click();
        cut.Find("[data-reorder-save]").Click();

        var row = Assert.Single(EditReviewRows.Build(_session.Pending, _ => "Learn Watercolour", null));

        Assert.Equal(EditSessionChangeKinds.VideoOrder, row.Kind);
    }

    [Fact]
    public void ReorderMode_ShowsSaveOrderAndCancel_AndNoDone()
    {
        var cut = EnterReorder();

        Assert.Equal("Save order", cut.Find("[data-reorder-save]").TextContent.Trim());
        Assert.Equal("Cancel", cut.Find("[data-reorder-cancel]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-reorder-done]"));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() == "Done");
    }

    [Fact]
    public void SaveOrder_IsDisabledUntilTheOrderDiffers_AndAgainWhenItIsPutBack()
    {
        var cut = EnterReorder();
        Assert.True(cut.Find("[data-reorder-save]").HasAttribute("disabled"));

        cut.Find("button[aria-label='Move Flat wash down']").Click();
        Assert.False(cut.Find("[data-reorder-save]").HasAttribute("disabled"));

        cut.Find("button[aria-label='Move Flat wash up']").Click();
        Assert.True(cut.Find("[data-reorder-save]").HasAttribute("disabled"));
    }

    [Fact]
    public void Cancel_PutsTheListBackExactlyAsItWas_AndStagesNothing()
    {
        var cut = EnterReorder();
        cut.Find("button[aria-label='Move Flat wash down']").Click();

        cut.Find("[data-reorder-cancel]").Click();

        Assert.Empty(_session.Pending.Changes);
        Assert.Empty(cut.FindAll("[data-reorder-bar]"));
        Assert.Equal(["Flat wash", "Graded wash"], ItemTitlesInOrder(cut));
        cut.Find("#tl-hnav-reorder").Click();
        Assert.Equal(["Flat wash", "Graded wash"], TitlesInOrder(cut));
    }

    [Fact]
    public void Escape_InReorderMode_PutsTheListBackAndStagesNothing_WithoutAnyEditorToClose()
    {
        var cut = EnterReorder();
        cut.Find("button[aria-label='Move Flat wash down']").Click();

        cut.Find("[data-reorder-bar]").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.Empty(_session.Pending.Changes);
        Assert.Empty(cut.FindAll("[data-reorder-bar]"));
        Assert.Empty(cut.FindAll("[data-testid=reorder-videos]"));

        // Escape closes the list as it does for every dropdown (the editor window itself is not involved); opening it again shows the order untouched.
        cut.Find("#tl-hnav-item").Click();
        Assert.Equal(["Flat wash", "Graded wash"], ItemTitlesInOrder(cut));
    }

    [Fact]
    public async Task ClosingTheListWithAnOutsidePress_WhileReordering_DiscardsTheHeldOrder()
    {
        var cut = EnterReorder();
        cut.Find("button[aria-label='Move Flat wash down']").Click();

        // The browser reports an outside press or Escape to the list's surface the same way (AppPopover.Dismiss).
        var popover = cut.FindComponents<AppPopover>().Single();
        await cut.InvokeAsync(() => popover.Instance.Dismiss());

        Assert.Empty(_session.Pending.Changes);
        Assert.Empty(cut.FindAll("[data-reorder-bar]"));
        cut.Find("#tl-hnav-item").Click();
        cut.Find("#tl-hnav-reorder").Click();
        Assert.Equal(["Flat wash", "Graded wash"], TitlesInOrder(cut));
    }

    [Fact]
    public void TheReorderBar_IsDrawnInsideTheListsOwnSurface_SoPressingItIsNeverAnOutsidePress()
    {
        var cut = EnterReorder();

        Assert.Single(cut.FindAll(".tl-hnav__surface [data-reorder-bar]"));
    }

    [Fact]
    public void WithThirtyVideos_TheReorderRowIsOneLineWithNoHintText()
    {
        var items = Enumerable.Range(1, 30)
            .Select(number => new EditorNavItemDto { Id = Guid.NewGuid(), Kind = "video", Number = number, Title = $"Lesson {number}", DurationSeconds = 600, State = EditorNavItemStates.Unwatched, IsCurrent = number == 2 })
            .ToList();
        var course = EditorHeaderTestData.CourseLesson();
        var header = new EditorHeaderDto
        {
            Kind = course.Kind,
            Root = course.Root,
            CurrentId = course.CurrentId,
            Label = course.Label,
            ShowHome = true,
            HomeId = course.HomeId,
            SecondLevel = course.SecondLevel,
            ShowItemDropdown = true,
            Level = new EditorHeaderLevelDto
            {
                LevelId = course.Level.LevelId,
                LevelTitle = course.Level.LevelTitle,
                Total = 30,
                PageSize = 50,
                Items = items,
                SupportingFiles = new EditorHeaderSupportingFilesDto(),
            },
            Config = course.Config,
        };
        var cut = Show(header);
        cut.Find("#tl-hnav-item").Click();

        var row = cut.Find("#tl-hnav-reorder");

        Assert.Equal("Reorder videos", row.TextContent.Trim());
        Assert.Empty(row.QuerySelectorAll("small"));
        Assert.Single(row.QuerySelectorAll(".tl-hnav__text > *"));
        Assert.DoesNotContain("Nothing changes until you save", cut.Markup, StringComparison.Ordinal);
        // The row is pinned under the list, not inside the part that scrolls, so thirty videos never push it out of sight.
        Assert.Empty(cut.FindAll(".tl-hnav__scroll #tl-hnav-reorder"));
        Assert.Single(cut.FindAll(".tl-hnav__surface #tl-hnav-reorder"));
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
    public void SaveOrder_InTheSectionDropdownsReorderMode_StagesTheOrderOfTheSections_OnTheSeries()
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
        Assert.Empty(_session.Pending.Changes);
        cut.Find("[data-reorder-save]").Click();

        var staged = Assert.Single(_session.Pending.Changes);
        Assert.Equal(EditSessionChangeKinds.VideoOrder, staged.Kind);
        Assert.Equal(EditorHeaderTestData.ShowId, staged.EntityId);
        Assert.Equal([SectionTwoId, EditorHeaderTestData.SeasonOneId], staged.OrderedIds);
        var rows = EditReviewRows.Build(_session.Pending, _ => "Learn Watercolour", null);
        Assert.Contains(rows, row => row.After is not null && row.After.Contains("2 sections in a new order", StringComparison.Ordinal));
    }
}
