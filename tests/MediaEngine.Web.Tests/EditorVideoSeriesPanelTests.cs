using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Items;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Web;
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
    // By default the Engine says the file stays (a Read-only folder); a missing answer stages nothing, which has its own test.
    private EditorVideoSeriesPlacementDto? _placement = new() { Managed = false };
    private bool _placementFails;

    public EditorVideoSeriesPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        _session.Begin([new EditTrailItem(VideoId, "Interview with the chef", "Item")]);
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetVideoSeriesAsync), _ => Task.FromResult<VideoSeriesStateResponse?>(_state));
            stub.SetHandler(nameof(IEngineApiClient.GetVideoOrderAsync), _ => Task.FromResult<VideoOrderResponse?>(_order));
            stub.SetHandler(nameof(IEngineApiClient.GetVideoSeriesPlacementAsync), _ =>
                _placementFails ? Task.FromException<EditorVideoSeriesPlacementDto?>(new HttpRequestException("down")) : Task.FromResult<EditorVideoSeriesPlacementDto?>(_placement));
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
        // Nothing says the file moves (a Read-only folder, or an Engine that could not say), so the choice lives in Library only.
        Assert.Equal("Set by you. The file stays where it is.", cut.Find("[data-testid=video-series-file]").TextContent);
    }

    // ----- D17: what the chooser says and offers -----

    private static List<string> OptionLabels(IRenderedComponent<EditorVideoSeriesPanel> cut) =>
        [.. cut.FindComponent<AppSelect>().Instance.Options!.Select(option => option.Label)];

    [Fact]
    public void TheChooser_ExplainsItIsForExceptions_UnderTheHeadingMoveThisVideo()
    {
        _state = new VideoSeriesStateResponse { Supported = true };
        var cut = RenderPanel();

        Assert.Contains("Move this video to another series", cut.Find(".tl-vseries__heading").TextContent);
        Assert.Equal(
            "A folder of videos is already a series. Use this only for a video that sits on its own or belongs with a different series.",
            cut.Find("[data-testid=video-series-intro]").TextContent);
    }

    [Fact]
    public void AVideoInAFolderSeries_OffersItsSeriesMarkedFromItsFolder_TheOtherSeries_AStartAndOnItsOwn()
    {
        _state = new VideoSeriesStateResponse
        {
            Supported = true,
            InSeries = true,
            FromFolder = true,
            SeriesTitle = "Cooking with Sam",
            Targets = [new VideoSeriesTargetDto { Id = OtherId, Title = "Garden talks", VideoCount = 1 }],
        };
        var cut = RenderPanel();

        var labels = OptionLabels(cut);
        Assert.Equal(["Cooking with Sam · from its folder", "Garden talks · 1 video", "Start a new series", "On its own"], labels);
    }

    [Fact]
    public void AVideoPutInItsSeriesByAPerson_IsNotMarkedFromItsFolder()
    {
        _state = new VideoSeriesStateResponse { Supported = true, InSeries = true, FromFolder = false, SeriesTitle = "Cooking with Sam" };
        var cut = RenderPanel();

        Assert.Equal("Cooking with Sam", OptionLabels(cut)[0]);
    }

    [Fact]
    public void AVideoOnItsOwn_ShowsOnItsOwnAsWhereTheFolderLeftIt()
    {
        var cut = RenderPanel();

        Assert.Contains("On its own · from its folder", OptionLabels(cut));
    }

    // ----- D24: a Managed folder moves the file, a Read-only one does not -----

    [Fact]
    public async Task InAManagedFolder_ChoosingASeriesAlsoStagesTheFileMove_AndTheFooterTurnsAmber()
    {
        _state = new VideoSeriesStateResponse
        {
            Supported = true,
            Targets = [new VideoSeriesTargetDto { Id = CourseId, Title = "Cooking with Sam", VideoCount = 6 }],
        };
        _placement = new EditorVideoSeriesPlacementDto { Managed = true, FromPath = "/videos/chef.mp4", ToPath = "/videos/Cooking with Sam/chef.mp4" };
        var cut = RenderPanel();

        await cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync(CourseId.ToString()));
        cut.Render();

        Assert.Equal(2, _session.Pending.Count);
        var move = Assert.Single(_session.Pending.Changes, change => change.Kind == EditSessionChangeKinds.FileMove);
        Assert.Equal(VideoId, move.EntityId);
        var series = _session.Pending.FindVideoSeries(VideoId)!;
        Assert.Equal(series.Id, move.TargetId);
        Assert.Equal(EditSessionVideo.MovesWithSeries(CourseId.ToString(), null), move.Token);
        Assert.Equal("/videos/chef.mp4", _session.Pending.Before(move.Id));
        Assert.Equal("/videos/Cooking with Sam/chef.mp4", _session.Pending.Label(move.Id));
        var footer = EditorFooterRules.For(new FileWriteSetting(FileWriteState.Off), _session.Pending);
        Assert.Equal(EditorFooterStatus.WillMove, footer.Status);
        Assert.Equal("/videos/chef.mp4", footer.MoveFrom);
        Assert.Equal("/videos/Cooking with Sam/chef.mp4", footer.MoveTo);
        Assert.Equal("The file moves to the folder Cooking with Sam when you save.", cut.Find("[data-testid=video-series-file]").TextContent);
    }

    [Fact]
    public async Task InAManagedFolder_AnUnstagedChoice_TakesTheFileMoveWithIt()
    {
        _state = new VideoSeriesStateResponse
        {
            Supported = true,
            Targets = [new VideoSeriesTargetDto { Id = CourseId, Title = "Cooking with Sam", VideoCount = 6 }],
        };
        _placement = new EditorVideoSeriesPlacementDto { Managed = true, FromPath = "/videos/chef.mp4", ToPath = "/videos/Cooking with Sam/chef.mp4" };
        var cut = RenderPanel();
        await cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync(CourseId.ToString()));

        await cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync("none"));

        Assert.True(_session.Pending.IsEmpty);
    }

    [Fact]
    public async Task InAManagedFolder_AVideoStartingANewSeriesStagesTheMove_AndSaysTheFolderIsMade()
    {
        _placement = new EditorVideoSeriesPlacementDto { Managed = true, FromPath = "/videos/chef.mp4", ToPath = "/videos/Garden talks/chef.mp4" };
        var cut = RenderPanel();

        await cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync("new"));
        cut.Render();
        cut.Find("input:not([role=switch])").Input("Garden talks");
        cut.Find("button:contains('Use this name')").Click();
        cut.WaitForAssertion(() => Assert.Contains("A new folder, Garden talks, is made for this series.", cut.Markup));

        Assert.Equal(2, _session.Pending.Count);
        Assert.Contains(_session.Pending.Changes, change => change.Kind == EditSessionChangeKinds.FileMove);
    }

    [Fact]
    public async Task InAReadOnlyFolder_ChoosingASeriesStagesOnlyTheSeries_AndTheFooterKeepsItsWriteBackLine()
    {
        _state = new VideoSeriesStateResponse
        {
            Supported = true,
            Targets = [new VideoSeriesTargetDto { Id = CourseId, Title = "Cooking with Sam", VideoCount = 6 }],
        };
        _placement = new EditorVideoSeriesPlacementDto { Managed = false };
        var cut = RenderPanel();

        await cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync(CourseId.ToString()));
        cut.Render();

        var staged = Assert.Single(_session.Pending.Changes);
        Assert.Equal(EditSessionChangeKinds.VideoSeries, staged.Kind);
        Assert.Equal(EditorFooterStatus.WriteBackOff, EditorFooterRules.For(new FileWriteSetting(FileWriteState.Off), _session.Pending).Status);
        Assert.Equal("Set by you. The file stays where it is.", cut.Find("[data-testid=video-series-file]").TextContent);
    }

    [Fact]
    public async Task InAManagedFolder_ASeriesWithNoFolderOfItsOwn_SaysWhyTheFileStays_AndStagesNoMove()
    {
        _state = new VideoSeriesStateResponse
        {
            Supported = true,
            Targets = [new VideoSeriesTargetDto { Id = CourseId, Title = "Cooking with Sam", VideoCount = 6 }],
        };
        _placement = new EditorVideoSeriesPlacementDto { Managed = true, Reason = "That series has no folder of its own yet, so the file stays where it is." };
        var cut = RenderPanel();

        await cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync(CourseId.ToString()));
        cut.Render();

        Assert.Single(_session.Pending.Changes);
        Assert.Equal(
            "Set by you. That series has no folder of its own yet, so the file stays where it is.",
            cut.Find("[data-testid=video-series-file]").TextContent);
    }

    [Fact]
    public async Task ANameTakenAtTheDestination_IsToldNow_AndTheMoveStaysStagedSoSaveChangesStopsOnIt()
    {
        _state = new VideoSeriesStateResponse
        {
            Supported = true,
            Targets = [new VideoSeriesTargetDto { Id = CourseId, Title = "Cooking with Sam", VideoCount = 6 }],
        };
        _placement = new EditorVideoSeriesPlacementDto
        {
            Managed = true,
            FromPath = "/videos/chef.mp4",
            ToPath = "/videos/Cooking with Sam/chef.mp4",
            Reason = "A file named chef.mp4 is already in that folder, so this video cannot move there.",
        };
        var cut = RenderPanel();

        await cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync(CourseId.ToString()));
        cut.Render();

        var line = cut.Find("[data-testid=video-series-file]");
        Assert.Contains("already in that folder", line.TextContent);
        Assert.Contains("tl-vseries__file--problem", line.ClassName);
        Assert.Contains(_session.Pending.Changes, change => change.Kind == EditSessionChangeKinds.FileMove);
    }

    // ----- The preview decides whether anything is staged -----

    private void OneSeriesToJoin() => _state = new VideoSeriesStateResponse
    {
        Supported = true,
        Targets =
        [
            new VideoSeriesTargetDto { Id = CourseId, Title = "Cooking with Sam", VideoCount = 6 },
            new VideoSeriesTargetDto { Id = OtherId, Title = "Garden talks", VideoCount = 3 },
        ],
    };

    [Fact]
    public async Task WhenTheEngineCannotSayWhereTheFileGoes_NothingIsStaged_AndThePersonCanTryAgain()
    {
        OneSeriesToJoin();
        _placementFails = true;
        var cut = RenderPanel();

        await cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync(CourseId.ToString()));
        cut.Render();

        Assert.True(_session.Pending.IsEmpty);
        Assert.Equal("Library could not check where the file would go, so nothing was changed.", cut.Find("[data-testid=video-series-problem]").TextContent);
        Assert.DoesNotContain("stays where it is", cut.Markup);

        _placementFails = false;
        _placement = new EditorVideoSeriesPlacementDto { Managed = true, FromPath = "/videos/chef.mp4", ToPath = "/videos/Cooking with Sam/chef.mp4" };
        cut.Find("[data-testid=video-series-try-again]").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, _session.Pending.Count));
        Assert.Empty(cut.FindAll("[data-testid=video-series-problem]"));
    }

    [Fact]
    public async Task WhenTheEngineRefusesTheChoice_NothingIsStaged_AndTheReasonIsShown()
    {
        OneSeriesToJoin();
        _placement = new EditorVideoSeriesPlacementDto { Managed = true, Blocked = true, Reason = "A folder cannot be named CON." };
        var cut = RenderPanel();

        await cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync(CourseId.ToString()));
        cut.Render();

        Assert.True(_session.Pending.IsEmpty);
        Assert.Equal("A folder cannot be named CON.", cut.Find("[data-testid=video-series-problem]").TextContent);
    }

    [Fact]
    public async Task AMatchWaitingToBeSaved_StopsTheSeriesChoice_WithAPlainLine_AndStagesNothingMore()
    {
        OneSeriesToJoin();
        _session.Pending.SetTarget(EditSessionChangeKinds.FileMove, VideoId, null, null, "/videos/Chef Interview.mp4", null, "/videos/chef.mp4");
        _placement = new EditorVideoSeriesPlacementDto { Managed = true, FromPath = "/videos/chef.mp4", ToPath = "/videos/Cooking with Sam/chef.mp4" };
        var cut = RenderPanel();

        await cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync(CourseId.ToString()));
        cut.Render();

        var only = Assert.Single(_session.Pending.Changes);
        Assert.Equal(EditSessionChangeKinds.FileMove, only.Kind);
        Assert.Contains("Save the match first, then change the series.", cut.Find("[data-testid=video-series-problem]").TextContent);
    }

    [Fact]
    public async Task ChoosingAnotherSeries_ReplacesTheMove_SoItNamesTheNewChoice_AndTheOldMoveIsGone()
    {
        OneSeriesToJoin();
        _placement = new EditorVideoSeriesPlacementDto { Managed = true, FromPath = "/videos/chef.mp4", ToPath = "/videos/Cooking with Sam/chef.mp4" };
        var cut = RenderPanel();
        await cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync(CourseId.ToString()));

        _placement = new EditorVideoSeriesPlacementDto { Managed = true, FromPath = "/videos/chef.mp4", ToPath = "/videos/Garden talks/chef.mp4" };
        await cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync(OtherId.ToString()));
        cut.Render();

        Assert.Equal(2, _session.Pending.Count);
        var series = _session.Pending.FindVideoSeries(VideoId)!;
        var move = Assert.Single(_session.Pending.Changes, change => change.Kind == EditSessionChangeKinds.FileMove);
        Assert.Equal(OtherId.ToString(), series.TargetId);
        Assert.Equal(series.Id, move.TargetId);
        Assert.Equal(EditSessionVideo.MovesWithSeries(OtherId.ToString(), null), move.Token);
        Assert.NotEqual(EditSessionVideo.MovesWithSeries(CourseId.ToString(), null), move.Token);
        Assert.Equal("/videos/Garden talks/chef.mp4", _session.Pending.Label(move.Id));
    }

    // ----- Cancel and Escape -----

    [Fact]
    public async Task Cancel_GoesBackWithNothingStaged_AndHidesItself()
    {
        _state = new VideoSeriesStateResponse
        {
            Supported = true,
            Targets = [new VideoSeriesTargetDto { Id = CourseId, Title = "Cooking with Sam", VideoCount = 6 }],
        };
        _placement = new EditorVideoSeriesPlacementDto { Managed = true, FromPath = "/videos/chef.mp4", ToPath = "/videos/Cooking with Sam/chef.mp4" };
        var cut = RenderPanel();
        Assert.Empty(cut.FindAll("[data-testid=video-series-cancel]"));
        await cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync(CourseId.ToString()));
        cut.Render();
        Assert.Equal(2, _session.Pending.Count);

        cut.Find("[data-testid=video-series-cancel]").Click();

        Assert.True(_session.Pending.IsEmpty);
        Assert.Empty(cut.FindAll("[data-testid=video-series-cancel]"));
        Assert.Empty(cut.FindAll("[data-testid=video-series-file]"));
    }

    [Fact]
    public async Task Escape_CancelsTheVisitsChange_AndTheChooserTellsTheWindowToLeaveEscapeAlone()
    {
        _state = new VideoSeriesStateResponse
        {
            Supported = true,
            Targets = [new VideoSeriesTargetDto { Id = CourseId, Title = "Cooking with Sam", VideoCount = 6 }],
        };
        var cut = RenderPanel();
        // Nothing changed yet: Escape belongs to the editor window as it always has.
        Assert.False(cut.Find("[data-testid=video-series]").HasAttribute("data-app-escape-owner"));
        await cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync(CourseId.ToString()));
        cut.Render();
        Assert.Equal("field", cut.Find("[data-testid=video-series]").GetAttribute("data-app-escape-owner"));

        cut.Find("[data-testid=video-series]").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.True(_session.Pending.IsEmpty);
        Assert.False(cut.Find("[data-testid=video-series]").HasAttribute("data-app-escape-owner"));
    }

    [Fact]
    public async Task Cancel_PutsBackWhatWasStagedWhenThePanelOpened_NotJustNothing()
    {
        _state = new VideoSeriesStateResponse
        {
            Supported = true,
            Targets =
            [
                new VideoSeriesTargetDto { Id = CourseId, Title = "Cooking with Sam", VideoCount = 6 },
                new VideoSeriesTargetDto { Id = OtherId, Title = "Garden talks", VideoCount = 3 },
            ],
        };
        _session.Pending.SetTarget(EditSessionChangeKinds.VideoSeries, VideoId, CourseId.ToString(), null, "Cooking with Sam", null, EditReviewRows.NotInASeries);
        _session.Pending.SetVideoSeriesMove(VideoId, "/videos/chef.mp4", "/videos/Cooking with Sam/chef.mp4");
        var cut = RenderPanel();

        await cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync(OtherId.ToString()));
        cut.Render();
        Assert.Equal(OtherId.ToString(), _session.Pending.FindVideoSeries(VideoId)!.TargetId);
        cut.Find("[data-testid=video-series-cancel]").Click();

        Assert.Equal(CourseId.ToString(), _session.Pending.FindVideoSeries(VideoId)!.TargetId);
        var move = Assert.Single(_session.Pending.Changes, change => change.Kind == EditSessionChangeKinds.FileMove);
        Assert.Equal("/videos/chef.mp4", _session.Pending.Before(move.Id));
        Assert.Equal("/videos/Cooking with Sam/chef.mp4", _session.Pending.Label(move.Id));
    }

    [Fact]
    public async Task Cancel_WhenAMatchWasStagedMeanwhile_PutsBackNeitherTheSeriesChangeNorItsMove_AndSaysWhy()
    {
        _state = new VideoSeriesStateResponse
        {
            Supported = true,
            Targets =
            [
                new VideoSeriesTargetDto { Id = CourseId, Title = "Cooking with Sam", VideoCount = 6 },
                new VideoSeriesTargetDto { Id = OtherId, Title = "Garden talks", VideoCount = 3 },
            ],
        };
        _session.Pending.SetTarget(EditSessionChangeKinds.VideoSeries, VideoId, CourseId.ToString(), null, "Cooking with Sam", null, EditReviewRows.NotInASeries);
        _session.Pending.SetVideoSeriesMove(VideoId, "/videos/chef.mp4", "/videos/Cooking with Sam/chef.mp4");
        var cut = RenderPanel();
        await cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync(OtherId.ToString()));
        // While the chooser is open a match for the same video is staged (its file moves for the match).
        _session.Pending.SetTarget(EditSessionChangeKinds.FileMove, VideoId, null, null, "/videos/Chef Interview.mp4", null, "/videos/chef.mp4");
        cut.Render();

        cut.Find("[data-testid=video-series-cancel]").Click();

        var only = Assert.Single(_session.Pending.Changes);
        Assert.Equal(EditSessionChangeKinds.FileMove, only.Kind);
        Assert.Null(_session.Pending.FindVideoSeries(VideoId));
        Assert.Contains("Save the match first", cut.Find("[data-testid=video-series-problem]").TextContent);
    }

    [Fact]
    public async Task Cancel_WhileNamingANewSeries_LeavesNothingStaged()
    {
        var cut = RenderPanel();
        await cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync("new"));
        cut.Render();

        cut.Find("[data-testid=video-series-cancel]").Click();

        Assert.True(_session.Pending.IsEmpty);
        Assert.Empty(cut.FindAll("input:not([role=switch])"));
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
    public void AVideoInNoSeries_CarriesTheNotMatchedMark_UntilItJoinsOne()
    {
        var cut = RenderPanel();
        Assert.Contains("Not matched", cut.Find("[data-testid=video-not-matched]").TextContent);

        _state = new VideoSeriesStateResponse { Supported = true, InSeries = true, SeriesTitle = "Cooking with Sam" };
        Assert.Empty(RenderPanel().FindAll("[data-testid=video-not-matched]"));
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
        var titles = cut.FindAll(".tl-vorder__title").Select(node => node.TextContent).ToList();
        Assert.Equal(["Kitchen tour", "The first dinner", "Bread day"], titles);
        Assert.Contains("new order", cut.Markup);
    }

    [Fact]
    public async Task ADrop_StagesTheSameOrderTheArrowsWould()
    {
        _order = Order(canRename: false);
        var cut = RenderPanel();

        await cut.InvokeAsync(() => cut.FindComponent<EditorVideoOrderList>().Instance.OnDropAsync(0, 2, 0, 0));

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
    public void WithTheRenameSwitchOn_TheSentenceDoesNotSayFilesStayAsTheyAre()
    {
        _order = Order(canRename: true);
        var cut = RenderPanel();

        cut.Find("input[role=switch]").Change(true);

        var sentence = cut.Find("[data-testid=video-order-happen]").TextContent;
        Assert.Contains("renamed to match", sentence);
        Assert.DoesNotContain("not moved or renamed", sentence);
    }

    [Fact]
    public void EveryVideoShowsAVisibleGripToDragBy()
    {
        _order = Order(canRename: false);
        var cut = RenderPanel();

        Assert.All(cut.FindAll(".tl-vorder__grip"), grip => Assert.NotEmpty(grip.QuerySelectorAll("svg")));
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
