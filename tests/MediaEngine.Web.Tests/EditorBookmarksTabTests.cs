using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The editor's Bookmarks tab on screen (universal editor v5 D37, WP15, mockups Q and Q2): offered to an administrator alone, two lists with
/// picture rows only for video, every change staged and marked until Save changes, and a show's overview whose chevron picks an episode
/// through the editor's own switch. The Engine is a hand-written stub.
/// </summary>
public sealed class EditorBookmarksTabTests : AsyncBunitContext
{
    private static readonly Guid MovieId = EditingModeTestData.MovieId;
    private static readonly Guid ShowId = Guid.NewGuid();
    private static readonly Guid SeasonOne = Guid.NewGuid();
    private static readonly Guid SeasonTwo = Guid.NewGuid();
    private static readonly Guid PilotId = Guid.NewGuid();
    private static readonly Guid FinaleId = Guid.NewGuid();
    private static readonly Guid Asset = Guid.NewGuid();
    private static readonly Guid Opening = Guid.NewGuid();
    private static readonly Guid Twist = Guid.NewGuid();

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<Guid> _opened = [];
    private EditorBookmarksDto _loaded = Titled(video: true, state: EditorBookmarkStates.Pooled);
    private EditorBookmarkOverviewDto _overview = Overview();
    private readonly DashboardSessionAccessor _accessor = new();
    private readonly IRenderedComponent<AppPopoverHost> _popovers;
    private IRenderedComponent<EditorBookmarksPanel>? _panel;
    private MediaEditorContextDto? _context;

    public EditorBookmarksTabTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(Api());
        Services.AddSingleton(_accessor);
        _popovers = Render<AppPopoverHost>();
    }

    private IEngineApiClient Api() => EngineApiClientStub.Create(stub =>
    {
        stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(_context ?? EditingModeTestData.MovieContext()));
        stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
        stub.SetHandler(nameof(IEngineApiClient.GetEditorBookmarksAsync), _ => Task.FromResult<EditorBookmarksDto?>(_loaded));
        stub.SetHandler(nameof(IEngineApiClient.GetEditorBookmarkOverviewAsync), _ => Task.FromResult<EditorBookmarkOverviewDto?>(_overview));
    });

    // ---------------------------------------------------------------- fixtures

    private static EditorBookmarkRowDto Row(Guid id, string name, double seconds, bool inEveryone, string owner = "Mira", bool video = true) => new()
    {
        Id = id,
        AssetId = Asset,
        PositionSeconds = seconds,
        Name = name,
        OwnerName = owner,
        InEveryone = inEveryone,
        FrameUrl = video ? $"/api/v1/player/bookmarks/{id:D}/frame?v=1" : null,
        CreatedAt = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero).AddSeconds(seconds),
    };

    private static EditorBookmarksDto Titled(bool video, string state) => new()
    {
        WorkId = MovieId,
        State = state,
        AssetId = Asset,
        HasTimes = true,
        CanExchange = true,
        HasFrames = video,
        RunningTimeSeconds = 9000,
        Everyone = [Row(Opening, "Opening", 724, true, video: video)],
        FromProfiles = [Row(Opening, "Opening", 724, true, video: video), Row(Twist, "The twist", 3100, false, "Ben", video)],
    };

    private static EditorBookmarkOverviewDto Overview() => new()
    {
        RootId = ShowId,
        RootTitle = "Le Continental",
        EveryoneTotal = 7,
        MemberCount = 3,
        DefinitiveCount = 1,
        Groups =
        [
            new EditorBookmarkOverviewGroupDto
            {
                GroupId = SeasonOne,
                Title = "Season 1",
                Number = 1,
                BookmarkCount = 5,
                Rows = [new EditorBookmarkOverviewRowDto { WorkId = PilotId, Code = "S1 E1", Title = "Pilot", EveryoneCount = 5, State = EditorBookmarkStates.Definitive, ProfileCount = 6 }],
            },
            new EditorBookmarkOverviewGroupDto
            {
                GroupId = SeasonTwo,
                Title = "Season 2",
                Number = 2,
                BookmarkCount = 2,
                Rows = [new EditorBookmarkOverviewRowDto { WorkId = FinaleId, Code = "S2 E1", Title = "The Finale", EveryoneCount = 2, ProfileCount = 2 }],
            },
        ],
    };

    private void SignIn(bool administrator)
    {
        var profile = Guid.NewGuid();
        _accessor.Set("token", Guid.NewGuid(), profile, Guid.NewGuid(),
            new DashboardAuthorityResponse(profile, profile, true, true, 1, 1, administrator, administrator, null, 1, [], [], []));
    }

    private sealed record Editor(EditingModeContext Mode, EditSessionState Session, EditSessionFlow Flow);

    private async Task<Editor> StartEditorAsync(EditTrailItem active)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var api = Services.GetRequiredService<IEngineApiClient>();
        var fields = new ItemFieldEditSession(api);
        var mode = new EditingModeContext(_device, fields) { HasPermission = true };
        var session = new EditSessionState(new PendingEditList());
        var flow = new EditSessionFlow(session, mode, new EditSessionSaver(api), () => [active], () => Task.FromResult(false))
        {
            OpenItem = item =>
            {
                _opened.Add(item.EntityId);
                return Task.FromResult(true);
            },
        };
        session.Begin([active]);
        return new Editor(mode, session, flow);
    }

    private IRenderedComponent<EditorBookmarksPanel> RenderPanel(Editor editor, bool hasFamily = false) =>
        _panel = Render<EditorBookmarksPanel>(parameters => parameters
            .Add(panel => panel.Flow, editor.Flow)
            .Add(panel => panel.HasFamily, hasFamily));

    private static EditTrailItem Movie => new(MovieId, "Dune: Part One", "Movie");

    // ---------------------------------------------------------------- the tab is an administrator's alone

    private async Task<IRenderedComponent<CascadingValue<EditingModeContext>>> RenderWindowAsync()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var context = EditingModeTestData.MovieContext();
        context.Header = EditorHeaderTestData.Movie(withBookmarks: true);
        _context = context;
        var api = Services.GetRequiredService<IEngineApiClient>();
        var fields = new ItemFieldEditSession(api);
        var mode = new EditingModeContext(_device, fields) { HasPermission = true };
        mode.Loader = () => fields.LoadAsync(MovieId, "Movies", "movie", null);
        var session = new EditSessionState(new PendingEditList());
        var flow = new EditSessionFlow(session, mode, new EditSessionSaver(api), () => [Movie], () => Task.FromResult(false));
        Assert.True(await mode.EnterAsync());
        await flow.SelectToolAsync(EditTool.Details);
        return Render<CascadingValue<EditingModeContext>>(parameters => parameters
            .Add(host => host.Value, mode)
            .Add(host => host.ChildContent, builder =>
            {
                builder.OpenComponent<EditorPanelHost>(0);
                builder.AddComponentParameter(1, nameof(EditorPanelHost.Mode), mode);
                builder.AddComponentParameter(2, nameof(EditorPanelHost.Flow), flow);
                builder.AddComponentParameter(3, nameof(EditorPanelHost.ShowHistory), true);
                builder.CloseComponent();
            }));
    }

    [Fact]
    public async Task TheBookmarksTab_IsOfferedToAnAdministrator_BetweenMatchAndHistory()
    {
        SignIn(administrator: true);

        var window = await RenderWindowAsync();

        var tabs = window.FindAll(".tl-editor-tab").Select(tab => tab.TextContent.Trim()).ToList();
        Assert.Contains("Bookmarks", tabs);
        Assert.True(tabs.IndexOf("Bookmarks") < tabs.IndexOf("History"));
        Assert.True(tabs.IndexOf("Bookmarks") > tabs.IndexOf("Match"));
    }

    [Fact]
    public async Task TheBookmarksTab_IsNotOfferedToAnyoneElse()
    {
        SignIn(administrator: false);

        var window = await RenderWindowAsync();

        var tabs = window.FindAll(".tl-editor-tab").Select(tab => tab.TextContent.Trim()).ToList();
        Assert.Contains("History", tabs);
        Assert.DoesNotContain("Bookmarks", tabs);
    }

    [Fact]
    public async Task TheBookmarksTab_IsNotOfferedWhenNobodyIsSignedIn()
    {
        var window = await RenderWindowAsync();

        Assert.DoesNotContain("Bookmarks", window.FindAll(".tl-editor-tab").Select(tab => tab.TextContent.Trim()));
    }

    // ---------------------------------------------------------------- one title

    [Fact]
    public async Task ATitle_ShowsEveryonesBookmarksAbove_AndTheProfilesOwnBelow()
    {
        var editor = await StartEditorAsync(Movie);

        var cut = RenderPanel(editor);

        cut.WaitForState(() => cut.FindAll("ul.tl-bookmarks__list").Count == 2);
        var lists = cut.FindAll("ul.tl-bookmarks__list");
        Assert.Single(lists[0].QuerySelectorAll("li.tl-bookmarks__row"));
        Assert.Equal(2, lists[1].QuerySelectorAll("li.tl-bookmarks__row").Length);
        Assert.Contains("Everyone's bookmarks", cut.Markup);
        Assert.Contains("From profiles", cut.Markup);
        Assert.Contains("Pooled from every profile's bookmarks", cut.Markup);
        Assert.Contains("12:04", lists[0].TextContent);
        Assert.Contains("from Ben", lists[1].TextContent);
    }

    [Fact]
    public async Task AVideoTitle_HasAPictureColumn_AndOtherKindsDoNot()
    {
        var editor = await StartEditorAsync(Movie);
        var video = RenderPanel(editor);
        video.WaitForState(() => video.FindAll(".tl-bookmarks__frame").Count == 3);
        Assert.Equal($"/engine-bookmark-frame/{Opening:D}?v=1", video.FindAll(".tl-bookmarks__frame img")[0].GetAttribute("src"));

        _loaded = Titled(video: false, state: EditorBookmarkStates.Pooled);
        var audio = RenderPanel(editor);
        audio.WaitForState(() => audio.FindAll("li.tl-bookmarks__row").Count == 3);
        Assert.Empty(audio.FindAll(".tl-bookmarks__frame"));
    }

    [Fact]
    public async Task AddToEveryones_StagesTheOperation_MarksTheRowStaged_AndNothingIsSaved()
    {
        var editor = await StartEditorAsync(Movie);
        var cut = RenderPanel(editor);
        cut.WaitForState(() => cut.FindAll("ul.tl-bookmarks__list").Count == 2);

        cut.FindAll("ul.tl-bookmarks__list")[1].QuerySelectorAll("button").First(button => button.TextContent.Trim() == "Add to Everyone's").Click();

        var op = Assert.Single(editor.Session.Pending.BookmarkOps(MovieId));
        Assert.Equal(EditorBookmarkOpKinds.Add, op.Kind);
        Assert.Equal(Twist, op.BookmarkId);
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Staged: added", cut.Markup);
            Assert.Contains("+1 staged", cut.Markup);
            Assert.Contains("Definitive set", cut.Markup);
            Assert.Equal(2, cut.FindAll("ul.tl-bookmarks__list")[0].QuerySelectorAll("li.tl-bookmarks__row").Length);
        });
        var row = EditReviewRows.Build(editor.Session.Pending, _ => "Dune", null).Single();
        Assert.Equal("Added to Everyone's: 51:40 The twist", row.After);
    }

    [Fact]
    public async Task ARowAlreadyInEveryones_OffersNoAddButton()
    {
        var editor = await StartEditorAsync(Movie);
        var cut = RenderPanel(editor);
        cut.WaitForState(() => cut.FindAll("ul.tl-bookmarks__list").Count == 2);

        var lower = cut.FindAll("ul.tl-bookmarks__list")[1];

        Assert.Single(lower.QuerySelectorAll(".tl-bookmarks__in"));
        Assert.Single(lower.QuerySelectorAll("button"));
    }

    [Fact]
    public async Task RemoveFromEveryones_ShowsTheRowStruckThrough_WithUndo_AndUndoPutsItBack()
    {
        var editor = await StartEditorAsync(Movie);
        editor.Session.Pending.StageBookmarkOp(MovieId, new EditorBookmarkOp { Kind = EditorBookmarkOpKinds.Remove, BookmarkId = Opening }, "Removed from Everyone's: 12:04 Opening");
        var cut = RenderPanel(editor);
        cut.WaitForState(() => cut.FindAll("ul.tl-bookmarks__list").Count == 2);

        var upper = cut.FindAll("ul.tl-bookmarks__list")[0];
        Assert.Equal("removed", upper.QuerySelector("li.tl-bookmarks__row")!.GetAttribute("data-mark"));
        Assert.Contains("Staged: removed", upper.TextContent);

        upper.QuerySelectorAll("button").First(button => button.TextContent.Trim() == "Undo").Click();

        Assert.Empty(editor.Session.Pending.BookmarkOps(MovieId));
        cut.WaitForAssertion(() => Assert.DoesNotContain("Staged: removed", cut.Markup));
    }

    [Fact]
    public async Task TheRowMenu_RenamesInPlace_AndStagesOneRenameWithoutSaving()
    {
        var editor = await StartEditorAsync(Movie);
        var cut = RenderPanel(editor);
        cut.WaitForState(() => cut.FindAll("ul.tl-bookmarks__list").Count == 2);

        cut.FindAll("ul.tl-bookmarks__list")[0].QuerySelector(".app-row-menu__trigger")!.Click();
        FindMenuItem("Rename").Click();
        cut.WaitForElement("li.tl-bookmarks__row input");
        cut.Find("li.tl-bookmarks__row input").Input("Cold open");
        cut.FindAll("li.tl-bookmarks__row button").First(button => button.TextContent.Trim() == "Done").Click();

        var op = Assert.Single(editor.Session.Pending.BookmarkOps(MovieId));
        Assert.Equal(EditorBookmarkOpKinds.Rename, op.Kind);
        Assert.Equal("Cold open", op.Name);
        cut.WaitForAssertion(() => Assert.Contains("Staged: renamed", cut.Markup));
    }

    [Fact]
    public async Task ChangeTime_AcceptsATimeInsideTheTitle_AndRefusesOnePastItsEnd()
    {
        var editor = await StartEditorAsync(Movie);
        var cut = RenderPanel(editor);
        cut.WaitForState(() => cut.FindAll("ul.tl-bookmarks__list").Count == 2);

        cut.FindAll("ul.tl-bookmarks__list")[0].QuerySelector(".app-row-menu__trigger")!.Click();
        FindMenuItem("Change time").Click();
        cut.WaitForElement("li.tl-bookmarks__row input");
        cut.Find("li.tl-bookmarks__row input").Input("3:00:00");
        cut.FindAll("li.tl-bookmarks__row button").First(button => button.TextContent.Trim() == "Done").Click();
        Assert.Empty(editor.Session.Pending.BookmarkOps(MovieId));
        cut.WaitForAssertion(() => Assert.Contains("falls inside the title", cut.Markup));

        cut.Find("li.tl-bookmarks__row input").Input("12:30");
        cut.FindAll("li.tl-bookmarks__row button").First(button => button.TextContent.Trim() == "Done").Click();

        var op = Assert.Single(editor.Session.Pending.BookmarkOps(MovieId));
        Assert.Equal(EditorBookmarkOpKinds.ChangeTime, op.Kind);
        Assert.Equal(750, op.PositionSeconds);
    }

    [Fact]
    public async Task StartOverFromProfiles_IsInTheEveryonesMenu_OnlyOnADefinitiveSet_AndStagesTheOperationWithAHint()
    {
        var editor = await StartEditorAsync(Movie);
        var pooled = RenderPanel(editor);
        pooled.WaitForState(() => pooled.FindAll("ul.tl-bookmarks__list").Count == 2);
        Assert.Empty(pooled.FindAll(".tl-bookmarks__actions .app-row-menu__trigger"));
        Assert.DoesNotContain("Start over from profiles", pooled.Markup);

        _loaded = Titled(video: true, state: EditorBookmarkStates.Definitive);
        var definitive = RenderPanel(editor);
        definitive.WaitForState(() => definitive.FindAll("ul.tl-bookmarks__list").Count == 2);
        // Not a button among the everyday actions: it sits behind the header's "..." menu.
        Assert.DoesNotContain(definitive.FindAll(".tl-bookmarks__actions > button"), button => button.TextContent.Contains("Start over", StringComparison.Ordinal));
        Assert.DoesNotContain("Bookmarks at the same spot are merged when you save.", definitive.Markup);
        definitive.Find(".tl-bookmarks__actions .app-row-menu__trigger").Click();
        FindMenuItem("Start over from profiles").Click();

        definitive.WaitForAssertion(() =>
        {
            Assert.Equal(EditorBookmarkOpKinds.StartOver, Assert.Single(editor.Session.Pending.BookmarkOps(MovieId)).Kind);
            Assert.Empty(definitive.FindAll(".tl-bookmarks__actions .app-row-menu__trigger"));
            Assert.Contains("Pooled from every profile's bookmarks", definitive.Markup);
            Assert.Contains("Bookmarks at the same spot are merged when you save.", definitive.Markup);
        });
    }

    [Fact]
    public async Task ExportAndImport_SitAtTheTopOfATitleThatIsNotInAFamily_AndNowhereElse()
    {
        var editor = await StartEditorAsync(Movie);
        var alone = RenderPanel(editor, hasFamily: false);
        alone.WaitForState(() => alone.FindAll("ul.tl-bookmarks__list").Count == 2);
        Assert.Contains(alone.FindAll(".tl-bookmarks__actions button"), button => button.TextContent.Trim() == "Export");
        Assert.Contains(alone.FindAll(".tl-bookmarks__actions button"), button => button.TextContent.Trim() == "Import");

        var episode = await StartEditorAsync(new EditTrailItem(PilotId, "Pilot", EditTrailKinds.Episode));
        _loaded = Titled(video: true, state: EditorBookmarkStates.Pooled) with { WorkId = PilotId };
        var inAShow = RenderPanel(episode, hasFamily: true);
        inAShow.WaitForState(() => inAShow.FindAll("ul.tl-bookmarks__list").Count == 2);
        Assert.DoesNotContain(inAShow.FindAll("button"), button => button.TextContent.Trim() is "Export" or "Import");
    }

    [Fact]
    public async Task ABookTitle_HasExportAndImport_ButNoTimeChange_AndShowsItsPlaceInTheText()
    {
        var chapter = Row(Opening, "Chapter one", 0, true, video: false) with { PositionKind = "text", ChapterTitle = "The Beginning" };
        _loaded = Titled(video: false, state: EditorBookmarkStates.Pooled) with { HasTimes = false, CanExchange = true, Everyone = [chapter], FromProfiles = [chapter] };
        var editor = await StartEditorAsync(new EditTrailItem(MovieId, "A book", "Book"));

        var cut = RenderPanel(editor);

        cut.WaitForState(() => cut.FindAll("ul.tl-bookmarks__list").Count == 2);
        Assert.Contains(cut.FindAll(".tl-bookmarks__actions button"), button => button.TextContent.Trim() == "Export");
        Assert.Contains(cut.FindAll(".tl-bookmarks__actions button"), button => button.TextContent.Trim() == "Import");
        Assert.Contains("The Beginning", cut.Markup);
        cut.FindAll("ul.tl-bookmarks__list")[0].QuerySelector(".app-row-menu__trigger")!.Click();
        Assert.Contains(MenuItems().Select(item => item.TextContent.Trim()), label => label == "Rename");
        Assert.Contains(MenuItems().Select(item => item.TextContent.Trim()), label => label == "Remove from Everyone's");
        Assert.DoesNotContain(MenuItems().Select(item => item.TextContent.Trim()), label => label == "Change time");
    }

    [Fact]
    public async Task AComicTitle_HasNoExportOrImport()
    {
        var page = Row(Opening, "Page twelve", 12, true, video: false) with { PositionKind = "page" };
        _loaded = Titled(video: false, state: EditorBookmarkStates.Pooled) with { HasTimes = false, CanExchange = false, Everyone = [page], FromProfiles = [page] };
        var editor = await StartEditorAsync(new EditTrailItem(MovieId, "A comic", "Comic"));

        var cut = RenderPanel(editor);

        cut.WaitForState(() => cut.FindAll("ul.tl-bookmarks__list").Count == 2);
        Assert.DoesNotContain(cut.FindAll(".tl-bookmarks__actions button"), button => button.TextContent.Trim() is "Export" or "Import");
    }

    [Theory]
    [InlineData(EditorFamilyKind.Show, true)]
    [InlineData(EditorFamilyKind.Album, true)]
    [InlineData(EditorFamilyKind.Series, false)]
    [InlineData(EditorFamilyKind.None, false)]
    public void OnlyAShowOrAnAlbumHoldsTheFilesForItsMembers(EditorFamilyKind kind, bool holds) =>
        Assert.Equal(holds, EditorBookmarksPanel.FamilyHoldsTheFiles(kind));

    [Theory]
    [InlineData("Audiobook")]
    [InlineData("Video")]
    [InlineData("Book")]
    public async Task ATitleInASeriesOrAVideoFolder_KeepsExportAndImportOnItsOwnTab(string kind)
    {
        // A Series family (audiobooks or books in a series, videos in a video folder) is not a show or an album, so the title has the files itself.
        var editor = await StartEditorAsync(new EditTrailItem(MovieId, "Part two", kind));
        var cut = RenderPanel(editor, hasFamily: EditorBookmarksPanel.FamilyHoldsTheFiles(EditorFamilyKind.Series));

        cut.WaitForState(() => cut.FindAll("ul.tl-bookmarks__list").Count == 2);
        Assert.Contains(cut.FindAll(".tl-bookmarks__actions button"), button => button.TextContent.Trim() == "Export");
        Assert.Contains(cut.FindAll(".tl-bookmarks__actions button"), button => button.TextContent.Trim() == "Import");
    }

    // ---------------------------------------------------------------- the compare view of an imported file

    private static BookmarkSetCompareDto Compared() => new()
    {
        Name = "Everyone's bookmarks",
        BookmarkCount = 3,
        Match = BookmarkPackMatchResults.Match,
        RowsReady = true,
        Rows =
        [
            new BookmarkSetCompareRowDto
            {
                Index = 0,
                Incoming = new BookmarkSetItemDto { PositionSeconds = 726, Note = "Opening scene" },
                Yours = new BookmarkSetExistingDto { Id = Opening, PositionSeconds = 724, Name = "Opening", IsGlobal = true },
                GapSeconds = 2,
                Closeness = BookmarkClosenessLabels.SameSpot,
                DefaultChoice = BookmarkRowChoices.Yours,
            },
            new BookmarkSetCompareRowDto
            {
                Index = 1,
                Incoming = new BookmarkSetItemDto { PositionSeconds = 5400, Note = "Finale" },
                Closeness = BookmarkClosenessLabels.New,
                DefaultChoice = BookmarkRowChoices.Incoming,
            },
            new BookmarkSetCompareRowDto
            {
                Index = 2,
                Incoming = new BookmarkSetItemDto { PositionSeconds = 60, Note = "Titles" },
                Closeness = BookmarkClosenessLabels.New,
                DefaultChoice = BookmarkRowChoices.Skip,
            },
        ],
    };

    [Fact]
    public void TakeAllNew_TicksOnlyTheBookmarksNothingIsNear_AndTheyAreTakenWithoutReplacingAnything()
    {
        IReadOnlyList<EditorBookmarkImportItem>? taken = null;
        var cut = Render<EditorBookmarkCompare>(parameters => parameters
            .Add(compare => compare.Compare, Compared())
            .Add(compare => compare.OnTake, EventCallback.Factory.Create<IReadOnlyList<EditorBookmarkImportItem>>(this, items => taken = items)));

        cut.FindAll(".tl-bookmarks__actions button").First(button => button.TextContent.Trim() == "Take all new").Click();
        cut.FindAll(".tl-bookmarks__actions button").First(button => button.TextContent.Trim().StartsWith("Add 2", StringComparison.Ordinal)).Click();

        Assert.NotNull(taken);
        Assert.Equal([5400d, 60d], taken!.Select(item => item.PositionSeconds).OrderByDescending(seconds => seconds).ToArray());
        Assert.All(taken, item => Assert.Null(item.ReplacesBookmarkId));
    }

    [Fact]
    public void ABooksRows_ShowTheirChapter_AndAreTakenWithTheirReaderLocationAndChapter()
    {
        const string cfi = "epubcfi(/6/8!/4/2/1:4)";
        var book = Compared() with
        {
            Rows =
            [
                new BookmarkSetCompareRowDto
                {
                    Index = 0,
                    Incoming = new BookmarkSetItemDto { PositionSeconds = 3, Locator = cfi, ChapterTitle = "The Siege", Note = "The wall falls" },
                    Yours = new BookmarkSetExistingDto { Id = Opening, PositionSeconds = 3, Name = "Chapter bookmark", Locator = "epubcfi(/6/8!/4/6/1:0)" },
                    Closeness = BookmarkClosenessLabels.Close,
                    DefaultChoice = BookmarkRowChoices.Yours,
                },
                new BookmarkSetCompareRowDto
                {
                    Index = 1,
                    Incoming = new BookmarkSetItemDto { PositionSeconds = 9, Locator = "epubcfi(/6/20!/4/2/1:0)", Note = "Ending" },
                    Closeness = BookmarkClosenessLabels.New,
                    DefaultChoice = BookmarkRowChoices.Incoming,
                },
            ],
        };
        IReadOnlyList<EditorBookmarkImportItem>? taken = null;
        var cut = Render<EditorBookmarkCompare>(parameters => parameters
            .Add(compare => compare.Compare, book)
            .Add(compare => compare.OnTake, EventCallback.Factory.Create<IReadOnlyList<EditorBookmarkImportItem>>(this, items => taken = items)));

        // The place is a chapter, never a clock time.
        Assert.Contains("The Siege", cut.Markup);
        Assert.Contains("In the text", cut.Markup);
        Assert.DoesNotContain("0:03", cut.Markup);
        cut.FindAll("li.tl-bookmarks__row")[0].QuerySelector("input[type=checkbox]")!.Change(true);
        cut.FindAll(".tl-bookmarks__actions button").First(button => button.TextContent.Trim().StartsWith("Add 2", StringComparison.Ordinal)).Click();

        Assert.NotNull(taken);
        var replacing = Assert.Single(taken!, item => item.ReplacesBookmarkId == Opening);
        Assert.Equal(cfi, replacing.Locator);
        Assert.Equal(3, replacing.ChapterIndex);
        Assert.Null(replacing.EndSeconds);
        var added = Assert.Single(taken!, item => item.ReplacesBookmarkId is null);
        Assert.Equal(9, added.ChapterIndex);
        Assert.Equal("epubcfi(/6/20!/4/2/1:0)", added.Locator);
    }

    [Fact]
    public void AVideoRowsPictureFromTheFile_TravelsWithTheImportedBookmark()
    {
        var withPicture = new BookmarkSetCompareRowDto
        {
            Index = 0,
            Incoming = new BookmarkSetItemDto { PositionSeconds = 60, Note = "Titles", FrameBase64 = "/9j/4AAQ" },
            Closeness = BookmarkClosenessLabels.New,
        };

        var item = EditorBookmarkWording.ImportItem(withPicture, replaceNearest: false);

        Assert.Equal("/9j/4AAQ", item.FrameBase64);
        Assert.Null(item.Locator);
        Assert.Null(item.ChapterIndex);
    }

    [Fact]
    public void AnImportedBooksBookmark_ShowsInTheListAsAPlaceInTheText_WhileItIsStaged()
    {
        var loaded = Titled(video: false, state: EditorBookmarkStates.Pooled) with { HasTimes = false, Everyone = [], FromProfiles = [] };
        var op = new EditorBookmarkOp
        {
            Id = "i1",
            Kind = EditorBookmarkOpKinds.Import,
            Items = [new EditorBookmarkImportItem { PositionSeconds = 3, ChapterIndex = 3, Locator = "epubcfi(/6/8!/4/2/1:4)", ChapterTitle = "The Siege", Name = "The wall falls" }],
        };

        var view = EditorBookmarkProjection.Apply(loaded, [op]);

        var row = Assert.Single(view.Everyone).Row;
        Assert.Equal("text", row.PositionKind);
        Assert.Equal(3, row.ChapterIndex);
        Assert.Equal("The Siege", EditorBookmarkWording.Place(row));
    }

    [Fact]
    public void ATickedRowBesideABookmarkInEveryones_ReplacesIt()
    {
        IReadOnlyList<EditorBookmarkImportItem>? taken = null;
        var cut = Render<EditorBookmarkCompare>(parameters => parameters
            .Add(compare => compare.Compare, Compared())
            .Add(compare => compare.OnTake, EventCallback.Factory.Create<IReadOnlyList<EditorBookmarkImportItem>>(this, items => taken = items)));

        cut.FindAll("li.tl-bookmarks__row")[0].QuerySelector("input[type=checkbox]")!.Change(true);
        cut.FindAll(".tl-bookmarks__actions button").First(button => button.TextContent.Trim().StartsWith("Add", StringComparison.Ordinal)).Click();

        var replaced = Assert.Single(taken!, item => item.ReplacesBookmarkId is not null);
        Assert.Equal(Opening, replaced.ReplacesBookmarkId);
        Assert.Equal("Opening scene", replaced.Name);
    }

    [Fact]
    public void AFileForAnotherTitle_CannotBeTaken()
    {
        var wrong = Compared() with { Match = BookmarkPackMatchResults.WrongTitle, Reason = "This file is for another film." };
        var cut = Render<EditorBookmarkCompare>(parameters => parameters.Add(compare => compare.Compare, wrong));

        Assert.Contains("This file is for another film.", cut.Markup);
        Assert.All(cut.FindAll(".tl-bookmarks__actions button"), button => Assert.True(button.HasAttribute("disabled")));
    }

    // ---------------------------------------------------------------- a show

    [Fact]
    public async Task AShowsOverview_GroupsEpisodesBySeason_WithStagedCountsAndExportAtTheTop()
    {
        var editor = await StartEditorAsync(new EditTrailItem(ShowId, "Le Continental", EditTrailKinds.Show));
        editor.Session.Pending.StageBookmarkOp(PilotId, new EditorBookmarkOp { Kind = EditorBookmarkOpKinds.Add, BookmarkId = Twist }, "Added to Everyone's: 51:40 The twist");

        var cut = RenderPanel(editor, hasFamily: true);

        cut.WaitForState(() => cut.FindAll(".tl-bookmarks__profiles h3").Count == 2);
        Assert.Equal(["Season 1 5", "Season 2 2"], cut.FindAll(".tl-bookmarks__profiles h3").Select(heading => string.Join(' ', heading.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).ToArray());
        Assert.Contains("S1 E1", cut.Markup);
        Assert.Contains("+1 staged", cut.Markup);
        Assert.Contains(cut.FindAll(".tl-bookmarks__actions button"), button => button.TextContent.Trim() == "Export the show");
        Assert.Contains(cut.FindAll(".tl-bookmarks__actions button"), button => button.TextContent.Trim() == "Import for the show");
    }

    [Fact]
    public async Task AnOverviewRow_ShowsEveryonesCount_TheSetState_AndTheProfilesCount_ForBothStates()
    {
        var editor = await StartEditorAsync(new EditTrailItem(ShowId, "Le Continental", EditTrailKinds.Show));

        var cut = RenderPanel(editor, hasFamily: true);

        cut.WaitForState(() => cut.FindAll(".tl-bookmarks__profiles li.tl-bookmarks__row").Count == 2);
        var headings = cut.FindAll(".tl-bookmarks__cols")[0].Children.Select(cell => cell.TextContent.Trim()).Where(text => text.Length > 0).ToArray();
        Assert.Equal(["Episode", "Title", "Everyone's", "Set", "From profiles"], headings);
        var definitive = cut.FindAll("li.tl-bookmarks__row")[0];
        Assert.Equal("5", definitive.QuerySelector("[data-col=everyone]")!.TextContent.Trim());
        Assert.Equal("Definitive set", definitive.QuerySelector("[data-col=set]")!.TextContent.Trim());
        Assert.Equal("6", definitive.QuerySelector("[data-col=profiles]")!.TextContent.Trim());
        var pooled = cut.FindAll("li.tl-bookmarks__row")[1];
        Assert.Equal("2", pooled.QuerySelector("[data-col=everyone]")!.TextContent.Trim());
        Assert.Equal("Pooled from profiles", pooled.QuerySelector("[data-col=set]")!.TextContent.Trim());
        Assert.Equal("2", pooled.QuerySelector("[data-col=profiles]")!.TextContent.Trim());
    }

    [Fact]
    public async Task AStagedChange_ChangesTheSetStateOfThatEpisodesRow_UntilSave()
    {
        var editor = await StartEditorAsync(new EditTrailItem(ShowId, "Le Continental", EditTrailKinds.Show));
        editor.Session.Pending.StageBookmarkOp(FinaleId, new EditorBookmarkOp { Kind = EditorBookmarkOpKinds.Add, BookmarkId = Twist }, "Added to Everyone's: 51:40 The twist");
        editor.Session.Pending.StageBookmarkOp(PilotId, new EditorBookmarkOp { Kind = EditorBookmarkOpKinds.StartOver }, EditorBookmarkWording.StartOverLine);

        var cut = RenderPanel(editor, hasFamily: true);

        cut.WaitForState(() => cut.FindAll(".tl-bookmarks__profiles li.tl-bookmarks__row").Count == 2);
        Assert.Equal("Pooled from profiles (staged)", cut.FindAll("li.tl-bookmarks__row")[0].QuerySelector("[data-col=set]")!.TextContent.Trim());
        Assert.Equal("Definitive (staged)", cut.FindAll("li.tl-bookmarks__row")[1].QuerySelector("[data-col=set]")!.TextContent.Trim());
    }

    [Fact]
    public async Task ASeasonsOverview_HasNoShowExportOrImport()
    {
        var editor = await StartEditorAsync(new EditTrailItem(SeasonOne, "Season 1", EditTrailKinds.Season));

        var cut = RenderPanel(editor, hasFamily: true);

        cut.WaitForState(() => cut.FindAll(".tl-bookmarks__profiles h3").Count == 2);
        Assert.Empty(cut.FindAll(".tl-bookmarks__actions"));
    }

    [Fact]
    public async Task TheChevron_PicksThatEpisodeThroughTheEditorsSwitch_StayingOnTheTab()
    {
        var editor = await StartEditorAsync(new EditTrailItem(ShowId, "Le Continental", EditTrailKinds.Show));
        var cut = RenderPanel(editor, hasFamily: true);
        cut.WaitForState(() => cut.FindAll(".tl-bookmarks__row-action button").Count == 2);

        cut.FindAll(".tl-bookmarks__row-action button")[1].Click();

        cut.WaitForAssertion(() => Assert.Equal(FinaleId, editor.Session.ActiveItem!.EntityId));
        Assert.Equal([FinaleId], _opened);
    }

    // ---------------------------------------------------------------- helpers

    private IReadOnlyList<AngleSharp.Dom.IElement> MenuItems() =>
        [.. _popovers.FindAll("[role='menuitem']"), .. _panel?.FindAll("[role='menuitem']") ?? []];

    private AngleSharp.Dom.IElement FindMenuItem(string label)
    {
        AngleSharp.Dom.IElement? found = null;
        _popovers.WaitForAssertion(() =>
        {
            found = MenuItems().FirstOrDefault(item => item.TextContent.Contains(label, StringComparison.Ordinal));
            Assert.NotNull(found);
        });
        return found!;
    }
}
