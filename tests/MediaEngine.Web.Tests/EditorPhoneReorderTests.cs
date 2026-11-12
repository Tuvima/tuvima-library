using System.Net.Http;
using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Items;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Putting videos in order on a phone (universal editor v6 WP17, D19 to D22): a row has a drag handle for a finger and a "..." menu with Move up and
/// Move down, and the shared Reorder bar's Save order stages the one change. The walk goes through the real header sheet and the real row menu;
/// nothing is sent to the Engine. A failed read says so in one plain line with Try again and never touches what is already staged.
/// </summary>
public sealed class EditorPhoneReorderTests : AsyncBunitContext
{
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly EditSessionState _session = new(new PendingEditList());
    private Func<Task<VideoOrderResponse?>> _read = () => Task.FromResult<VideoOrderResponse?>(Lessons());

    public EditorPhoneReorderTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetVideoOrderAsync), _ => _read())));
        Render<AppPopoverHost>();
        Render<AppDialogHost>();
        _session.Begin([new EditTrailItem(EditorHeaderTestData.EpisodeTwoId, "Graded wash", "Item")]);
    }

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

    private async Task<IRenderedComponent<EditorHeaderNav>> EnterReorderOnAPhoneAsync()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "phone", true));
        var api = Services.GetRequiredService<IEngineApiClient>();
        var mode = new EditingModeContext(_device, new ItemFieldEditSession(api)) { HasPermission = true };
        var flow = new EditSessionFlow(_session, mode, new EditSessionSaver(api), () => [], () => Task.FromResult(true));
        var cut = Render<EditorHeaderNav>(parameters => parameters
            .AddCascadingValue(EditorWindowLayout.PhoneSheet)
            .Add(nav => nav.Header, EditorHeaderTestData.CourseLesson())
            .Add(nav => nav.Flow, flow)
            .Add(nav => nav.Phone, true)
            .Add(nav => nav.PhoneLabel, "Learn Watercolour"));
        cut.Find("#tl-hnav-phone").Click();
        cut.WaitForElement("#tl-hnav-reorder").Click();
        cut.WaitForElement("[data-testid=reorder-videos] .tl-vorder__row");
        return cut;
    }

    private static string[] TitlesInOrder(IRenderedComponent<EditorHeaderNav> cut) =>
        [.. cut.FindAll("[data-testid=reorder-videos] .tl-vorder__title").Select(title => title.TextContent.Trim())];

    private static void OpenRowMenu(IRenderedComponent<EditorHeaderNav> cut, string title) =>
        cut.Find($"[data-vo-title='{title}'] button[aria-haspopup=dialog]").Click();

    [Fact]
    public async Task MoveDown_FromTheRowsMenu_SwapsTwoVideos_AndSaveOrderStagesOneChange_ByTouchAlone()
    {
        var cut = await EnterReorderOnAPhoneAsync();
        Assert.Equal(["Flat wash", "Graded wash"], TitlesInOrder(cut));
        // On a phone the arrows are not drawn: the handle and the "..." menu are the two ways to move a video.
        Assert.Empty(cut.FindAll("[data-testid=reorder-videos] .tl-vorder__arrows"));
        Assert.NotEmpty(cut.FindAll("[data-testid=reorder-videos] [data-vo-handle]"));

        OpenRowMenu(cut, "Flat wash");
        cut.WaitForElement("[role=menu] [role=menuitem]");
        cut.Find("[role=menu] [role=menuitem]:contains('Move down')").Click();

        cut.WaitForAssertion(() => Assert.Equal(["Graded wash", "Flat wash"], TitlesInOrder(cut)));
        Assert.Empty(_session.Pending.Changes);

        cut.Find("[data-reorder-save]").Click();

        var staged = Assert.Single(_session.Pending.Changes);
        Assert.Equal(EditSessionChangeKinds.VideoOrder, staged.Kind);
        Assert.Equal([EditorHeaderTestData.EpisodeTwoId, EditorHeaderTestData.EpisodeOneId], staged.OrderedIds);
    }

    [Fact]
    public async Task MoveUp_FromTheRowsMenu_PutsTheLaterVideoFirst()
    {
        var cut = await EnterReorderOnAPhoneAsync();

        OpenRowMenu(cut, "Graded wash");
        cut.WaitForElement("[role=menu] [role=menuitem]");
        cut.Find("[role=menu] [role=menuitem]:contains('Move up')").Click();

        cut.WaitForAssertion(() => Assert.Equal(["Graded wash", "Flat wash"], TitlesInOrder(cut)));
    }

    [Fact]
    public async Task TheFirstVideosMenu_HasMoveUpSwitchedOff()
    {
        var cut = await EnterReorderOnAPhoneAsync();

        OpenRowMenu(cut, "Flat wash");
        cut.WaitForElement("[role=menu] [role=menuitem]");
        Assert.True(cut.Find("[role=menu] [role=menuitem]:contains('Move up')").HasAttribute("disabled")
            || cut.Find("[role=menu] [role=menuitem]:contains('Move up')").GetAttribute("aria-disabled") == "true");
    }

    [Fact]
    public async Task Cancel_AfterAMove_StagesNothing()
    {
        var cut = await EnterReorderOnAPhoneAsync();
        OpenRowMenu(cut, "Flat wash");
        cut.WaitForElement("[role=menu] [role=menuitem]");
        cut.Find("[role=menu] [role=menuitem]:contains('Move down')").Click();
        cut.WaitForAssertion(() => Assert.Equal(["Graded wash", "Flat wash"], TitlesInOrder(cut)));

        cut.Find("[data-reorder-cancel]").Click();

        Assert.Empty(_session.Pending.Changes);
    }

    [Fact]
    public async Task WhenTheOrderCannotBeRead_APlainLineAndTryAgainShow_AndTryAgainBringsTheListBack()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "phone", true));
        _read = () => Task.FromException<VideoOrderResponse?>(new HttpRequestException("offline"));
        var cut = Render<EditorVideoOrderList>(parameters => parameters
            .AddCascadingValue(EditorWindowLayout.PhoneSheet)
            .Add(list => list.EntityId, EditorHeaderTestData.ShowId)
            .Add(list => list.SeriesTitle, "Learn Watercolour")
            .Add(list => list.Session, _session)
            .Add(list => list.CanEdit, true)
            .Add(list => list.Compact, true));

        cut.WaitForElement("[data-testid=video-order-failed]");
        Assert.Empty(cut.FindAll("[data-vo-row]"));
        _read = () => Task.FromResult<VideoOrderResponse?>(Lessons());
        cut.Find("[data-testid=video-order-retry]").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-vo-row]").Count));
        Assert.Empty(cut.FindAll("[data-testid=video-order-failed]"));
    }
}
