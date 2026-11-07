using Bunit;
using Bunit.TestDoubles;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Search;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.Watch;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The details page's side of supporting files (universal editor v5 WP11, acceptance A8): the Extras row grouped by group with a "See all" that opens the
/// same list read-only, the "Shown alongside" chip under an episode or lesson row, the course end card's "Homework for this lesson" block, and the
/// "Supporting files" section of Search. The Engine is a hand-written stub; nothing is read or written anywhere.
/// </summary>
public sealed class ExtrasRowTests : AsyncBunitContext
{
    private static readonly Guid TitleId = Guid.Parse("d1000000-0000-0000-0000-000000000001");

    public ExtrasRowTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
    }

    private static DetailExtraCardViewModel Card(string title, string? group = null, string kind = DetailExtraCardKinds.Document, string badge = "PDF",
        string role = "worksheet", string roleLabel = "Worksheet", string? alongsideKey = null, string? alongsideAsset = null, int? pages = 12, long size = 840_000, bool missing = false) => new()
        {
            Id = Guid.NewGuid().ToString("D"),
            Kind = kind,
            Title = title,
            Subtitle = pages is null ? null : $"PDF, {pages} pages",
            FileBadge = badge,
            Role = role,
            RoleLabel = roleLabel,
            Group = group,
            AlongsideKey = alongsideKey,
            AlongsideAssetId = alongsideAsset,
            PageCount = pages,
            SizeBytes = size,
            IsMissing = missing,
        };

    // -- The Extras row: groups and See all -----------------------------------------------------------------------------------------

    [Fact]
    public void TheRow_IsGroupedByGroup_WithTheGroupNameAndCountAboveEachShelf()
    {
        var cut = Render<ExtrasSection>(p => p.Add(c => c.Extras,
        [
            Card("Characters", "Character guides"), Card("Season 1 cast", "Character guides"),
            Card("Behind the signal", "Production", DetailExtraCardKinds.Clip, "VIDEO"),
        ]).Add(c => c.CardsPerGroup, 12));

        var names = cut.FindAll("[data-testid=extras-group-name]").Select(name => name.TextContent.Trim()).ToList();
        Assert.Equal(["Character guides · 2", "Production · 1"], names);
        Assert.Equal(2, cut.FindAll("[data-testid=extras-group]")[0].QuerySelectorAll("a.tl-extras-card").Length);
    }

    [Fact]
    public void AGroupOverTheLimit_ShowsOnlyTheLimit_AndSeeAllAsksThePageToOpenTheList()
    {
        var opened = 0;
        var cards = Enumerable.Range(1, 5).Select(number => Card($"Guide {number}", "Guides")).ToList();
        var cut = Render<ExtrasSection>(p => p.Add(c => c.Extras, cards).Add(c => c.CardsPerGroup, 3)
            .Add(c => c.OnSeeAll, EventCallback.Factory.Create(this, () => opened++)));

        Assert.Equal(3, cut.FindAll("a.tl-extras-card").Count);
        Assert.Equal("See all 5", cut.Find("[data-testid=extras-see-all]").TextContent.Trim());
        cut.Find("[data-testid=extras-see-all]").Click();
        Assert.Equal(1, opened);
    }

    [Fact]
    public void SeeAll_IsNotOffered_WhenEveryCardIsShown()
    {
        var cut = Render<ExtrasSection>(p => p.Add(c => c.Extras, [Card("A", "Guides"), Card("B", "Guides")]).Add(c => c.CardsPerGroup, 12)
            .Add(c => c.OnSeeAll, EventCallback.Factory.Create(this, () => { })));

        Assert.Empty(cut.FindAll("[data-testid=extras-see-all]"));
    }

    [Fact]
    public void ATitleWithNoGroups_IsOnePlainRow_WithNoHeadings()
    {
        var cut = Render<ExtrasSection>(p => p.Add(c => c.Extras, [Card("A"), Card("B"), Card("C")]).Add(c => c.CardsPerGroup, 12));

        Assert.Empty(cut.FindAll("[data-testid=extras-group-name]"));
        Assert.Single(cut.FindAll("[data-testid=extras-group]"));
        Assert.Equal(3, cut.FindAll("a.tl-extras-card").Count);
    }

    [Fact]
    public void FilesInNoGroup_SitUnderMore_AfterTheNamedGroups()
    {
        var cut = Render<ExtrasSection>(p => p.Add(c => c.Extras, [Card("Loose", null), Card("Guide", "Guides")]).Add(c => c.CardsPerGroup, 12));

        var names = cut.FindAll("[data-testid=extras-group-name]").Select(name => name.TextContent.Trim()).ToList();
        Assert.Equal(["Guides · 1", "More · 1"], names);
    }

    [Fact]
    public void ADocumentCard_ShowsItsFileTypeInTheArt()
    {
        var cut = Render<ExtrasSection>(p => p.Add(c => c.Extras, [Card("Workbook")]).Add(c => c.CardsPerGroup, 12));

        Assert.Equal("PDF", cut.Find("[data-testid=extras-type]").TextContent.Trim());
    }

    // -- Which rows a file is shown alongside ---------------------------------------------------------------------------------------

    private static SequenceItemViewModel Row(int? season, int? episode, Guid assetId) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        Title = "Echoes in the Dark",
        EpisodeContext = new DisplayEpisodeContextDto(TitleId, Guid.NewGuid(), assetId, "The Last Signal", "Echoes in the Dark", season, episode,
            DisplayContinuationState.Unstarted, null, null),
    };

    [Fact]
    public void AnEpisodeRow_FindsTheFilesShownAlongsideItBySeasonAndEpisode()
    {
        var guide = Card("Character guide", alongsideKey: "episode:2:3");
        var other = Card("Season 2 guide", alongsideKey: "episode:2:4");

        var found = ExtrasRowRules.ShownAlongside([guide, other, Card("Loose")], Row(2, 3, Guid.NewGuid()));

        Assert.Equal([guide.Id], found.Select(card => card.Id));
    }

    [Fact]
    public void ALessonRow_FindsTheFilesShownAlongsideItByItsMediaAsset()
    {
        var asset = Guid.NewGuid();
        var worksheet = Card("Component exercises", alongsideKey: "file:Section 1/02 Passing parameters.mkv", alongsideAsset: asset.ToString("D"));

        var found = ExtrasRowRules.ShownAlongside([worksheet, Card("Elsewhere", alongsideAsset: Guid.NewGuid().ToString("D"))], Row(null, null, asset));

        Assert.Equal([worksheet.Id], found.Select(card => card.Id));
    }

    [Fact]
    public void AMissingFile_ShowsNoChip()
    {
        var missing = Card("Gone", alongsideKey: "episode:1:1", missing: true);

        Assert.Empty(ExtrasRowRules.ShownAlongside([missing], Row(1, 1, Guid.NewGuid())));
    }

    // -- The chip under a row -------------------------------------------------------------------------------------------------------

    [Fact]
    public void ALessonRow_ShowsItsWorksheetChip_AndOpeningItOpensTheViewer()
    {
        var worksheet = Card("Component exercises", alongsideAsset: Guid.NewGuid().ToString("D"));
        var cut = Render<SequenceEntryContent>(p => p.Add(c => c.Item, Row(null, null, Guid.NewGuid())).Add(c => c.Chips, [worksheet]));

        var chip = cut.Find("[data-testid=alongside-chip]");
        Assert.Equal("Worksheet · PDF", chip.TextContent.Trim());
        chip.Click();

        var navigation = Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith($"/extras/{worksheet.Id}", navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void SeveralFiles_ShowTheFirstChip_AndCountTheRest()
    {
        var cut = Render<SequenceEntryContent>(p => p.Add(c => c.Item, Row(2, 3, Guid.NewGuid())).Add(c => c.Chips, [Card("One"), Card("Two"), Card("Three")]));

        Assert.Single(cut.FindAll("[data-testid=alongside-chip]"));
        Assert.Equal("+2", cut.Find("[data-testid=alongside-more]").TextContent.Trim());
    }

    [Fact]
    public void ARowWithNoFileAlongside_HasNoChip()
    {
        var cut = Render<SequenceEntryContent>(p => p.Add(c => c.Item, Row(2, 3, Guid.NewGuid())));

        Assert.Empty(cut.FindAll("[data-testid=alongside-chips]"));
    }

    // -- See all: the same browser, read-only ---------------------------------------------------------------------------------------

    private static SupportingFileListDto Page() => new()
    {
        Items =
        [
            new SupportingFileRowDto { Id = Guid.NewGuid().ToString("D"), Title = "Workbook", HasOwnTitle = true, FileName = "workbook.pdf", Badge = "PDF", TypeLabel = "PDF", WhereLabel = "Title level", RoleLabel = "Workbook", Group = "Guides" },
            new SupportingFileRowDto { Id = Guid.NewGuid().ToString("D"), Title = "Cheat sheet", HasOwnTitle = true, FileName = "cheat.pdf", Badge = "PDF", TypeLabel = "PDF", WhereLabel = "Season 2", RoleLabel = "Guide" },
        ],
        Total = 2,
        TotalAll = 2,
        Groups = [new SupportingFileGroupDto { Id = "g1", Name = "Guides", FileCount = 1 }],
        UngroupedCount = 1,
        WhereOptions = [new SupportingFileWhereOptionDto { Key = "all", Label = "Whole title", Level = "all", FileCount = 2 }],
    };

    private async Task<(IRenderedComponent<EditorFileBrowserDialog> Cut, Recorder Dialog)> OpenReadOnlyAsync(bool readOnly)
    {
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.ListSupportingFilesAsync), _ => Task.FromResult<SupportingFileListDto?>(Page())));
        var state = new EditorFileBrowserState(api, () => new PendingEditList());
        await state.OpenAsync(TitleId, "The Last Signal", EditorBrowserView.Files);
        var dialog = new Recorder();
        var cut = Render<EditorFileBrowserDialog>(p => p.AddCascadingValue<IAppDialogContext>(dialog).Add(c => c.State, state).Add(c => c.ReadOnly, readOnly));
        return (cut, dialog);
    }

    [Fact]
    public async Task SeeAll_OpensTheBrowserReadOnly_WithNoTicksNoBulkBarAndNoEditActions()
    {
        var (cut, _) = await OpenReadOnlyAsync(readOnly: true);

        Assert.Equal(2, cut.FindAll("[data-file-id]").Count);
        Assert.Empty(cut.FindAll("[data-tick-page]"));
        Assert.Empty(cut.FindAll("[data-testid=file-browser-bulk]"));
        Assert.Empty(cut.FindAll("[data-browser-edit]"));
        Assert.Empty(cut.FindAll("[data-view-switch=folders]"));
        Assert.Equal(2, cut.FindAll("[data-open-file]").Count);
    }

    [Fact]
    public async Task TheEditorsBrowser_StillHasTicksAndEditSelected()
    {
        var (cut, _) = await OpenReadOnlyAsync(readOnly: false);

        Assert.NotEmpty(cut.FindAll("[data-tick-page]"));
        Assert.NotEmpty(cut.FindAll("[data-browser-edit]"));
        Assert.Empty(cut.FindAll("[data-open-file]"));
    }

    [Fact]
    public async Task OpeningARowOfTheReadOnlyList_AnswersWithThatFile()
    {
        var (cut, dialog) = await OpenReadOnlyAsync(readOnly: true);
        var row = cut.FindAll("[data-file-id]")[1];

        row.QuerySelector("[data-open-file]")!.Click();

        var outcome = Assert.IsType<EditorFileBrowserOutcome>(dialog.Result?.Data);
        Assert.Equal(EditorFileBrowserAction.OpenFile, outcome.Action);
        Assert.Equal(Guid.Parse(row.GetAttribute("data-file-id")!), outcome.Id);
    }

    // -- The course end card: Homework for this lesson ------------------------------------------------------------------------------

    private static VideoOwnedEpisode NextLesson() => new(Guid.NewGuid(), "Component events", null, null, null, "s1", "Section 1", 1, 3);

    [Fact]
    public void ACourseEndCard_ListsTheHomeworkOfTheLesson_WithItsBadgeTitleRoleAndPages()
    {
        var cut = Render<VideoEndCard>(p => p.Add(c => c.Next, NextLesson()).Add(c => c.Lesson, true)
            .Add(c => c.HomeworkFiles, [Card("Component exercises")]));

        Assert.Equal("Up next", cut.Find(".video-end-card__title").TextContent);
        var homework = cut.Find("[data-end-card-homework]");
        Assert.Contains("Homework for this lesson", homework.TextContent, StringComparison.Ordinal);
        Assert.Contains("Component exercises", homework.TextContent, StringComparison.Ordinal);
        Assert.Contains("Worksheet · 12 pages", homework.TextContent, StringComparison.Ordinal);
        Assert.Equal("PDF", homework.QuerySelector(".video-end-card__file-type")!.TextContent.Trim());
    }

    [Fact]
    public void ATvEpisodeEndCard_HasNoHomeworkBlock()
    {
        var cut = Render<VideoEndCard>(p => p.Add(c => c.Next, NextLesson()).Add(c => c.Lesson, false));

        Assert.Empty(cut.FindAll("[data-end-card-homework]"));
        Assert.Equal("Next episode", cut.Find("[data-end-card-kind]").TextContent);
    }

    [Fact]
    public void TheLastLesson_ShowsOnlyTheHomework_WithNoUpNext()
    {
        var cut = Render<VideoEndCard>(p => p.Add(c => c.Next, null).Add(c => c.Lesson, true).Add(c => c.HomeworkFiles, [Card("Final project")]));

        Assert.Empty(cut.FindAll("[data-end-card-title]"));
        Assert.Empty(cut.FindAll("[data-end-card-play]"));
        Assert.Single(cut.FindAll("[data-end-card-homework-file]"));
    }

    [Fact]
    public void AfterUpNextIsDismissed_TheHomeworkStaysReachable()
    {
        var cut = Render<VideoEndCard>(p => p.Add(c => c.Next, NextLesson()).Add(c => c.Lesson, true).Add(c => c.UpNextDismissed, true)
            .Add(c => c.HomeworkFiles, [Card("Component exercises")]));

        Assert.Empty(cut.FindAll("[data-end-card-play]"));
        Assert.Single(cut.FindAll("[data-end-card-homework-file]"));
    }

    [Fact]
    public void OpenOnAHomeworkFile_TellsTheHostWhichFile()
    {
        var file = Card("Component exercises");
        DetailExtraCardViewModel? opened = null;
        var cut = Render<VideoEndCard>(p => p.Add(c => c.Next, NextLesson()).Add(c => c.Lesson, true).Add(c => c.HomeworkFiles, [file])
            .Add(c => c.OpenHomework, EventCallback.Factory.Create<DetailExtraCardViewModel>(this, card => opened = card)));

        cut.Find("[data-end-card-homework-open]").Click();

        Assert.Equal(file.Id, opened?.Id);
    }

    [Fact]
    public void TheEndCardState_StopsTheCountdownOnDismiss_ButKeepsTheCardForHomework()
    {
        var identity = new VideoPlaybackIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        var next = Guid.NewGuid();
        var state = new VideoEndCardState();
        state.Observe(identity, next, 90, 100, [], hasHomework: true);
        Assert.True(state.Visible);
        Assert.False(state.Tick(identity, 1, true));
        Assert.Equal(9, state.RemainingSeconds);

        state.Dismiss();
        state.Observe(identity, next, 91, 100, [], hasHomework: true);

        Assert.True(state.Visible);
        Assert.True(state.UpNextDismissed);
        for (var tick = 0; tick < 30; tick++)
        {
            Assert.False(state.Tick(identity, 1, true));
        }

        Assert.Equal(9, state.RemainingSeconds);
    }

    [Fact]
    public void TheEndCardState_ShowsOnTheLastLesson_OnlyWhenThereIsHomework()
    {
        var identity = new VideoPlaybackIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        var state = new VideoEndCardState();

        state.Observe(identity, null, 95, 100, []);
        Assert.False(state.Visible);

        state.Observe(identity, null, 95, 100, [], hasHomework: true);
        Assert.True(state.Visible);
        Assert.False(state.Tick(identity, 1, true));
    }

    // -- Search ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Search_ListsSupportingFilesInTheirOwnSection_WithTypeTitleAndTheTitleTheyBelongTo()
    {
        var fileId = Guid.NewGuid();
        var result = new UniversalSearchResultDto(fileId, "supporting-files", null, "Component exercises", "Worksheet · Section 2 · Build with Blazor", null, null, null,
            "Do these after lesson 2", "/details/tvshow/d1000000-0000-0000-0000-000000000001?context=watch", "Open", "Matched title", 0.8)
        {
            Facts = ["PDF"],
            SupportingFile = new SupportingFileSearchHitDto { OwnerId = TitleId, OwnerName = "Build with Blazor", ScopeLabel = "Section 2", TypeLabel = "PDF", Type = "document", RoleLabel = "Worksheet" },
        };
        var response = new UniversalSearchResponseDto("exercises", null, [new UniversalSearchSectionDto("supporting-files", "Supporting files", [result], 1, null)], 0);
        Services.AddLocalization();

        var cut = Render<UniversalSearchResults>(p => p.Add(c => c.Response, response));

        var section = cut.Find("[data-testid=search-supporting-files]");
        Assert.Equal("Supporting files", section.QuerySelector("h2")!.TextContent.Trim());
        var row = section.QuerySelector("[data-testid=search-supporting-file]")!;
        Assert.Equal($"/extras/{fileId:D}", row.GetAttribute("href"));
        Assert.Equal("PDF", row.QuerySelector(".universal-results__file-type")!.TextContent.Trim());
        Assert.Equal("Component exercises", row.QuerySelector("strong")!.TextContent.Trim());
        Assert.Equal("in Build with Blazor · Section 2", row.QuerySelector("small")!.TextContent.Trim());
    }

    private sealed class Recorder : IAppDialogContext
    {
        public Guid Id { get; } = Guid.NewGuid();
        public AppDialogOptions Options { get; } = new();
        public string? PresentationClass { get; private set; }
        public AppDialogResult? Result { get; private set; }
        public void Close(AppDialogResult? result = null) => Result = result;
        public void Cancel() => Result = AppDialogResult.Cancel();

        public Task CloseAsync(AppDialogResult? result = null)
        {
            Result = result;
            return Task.CompletedTask;
        }

        public Task CancelAsync() => CloseAsync(AppDialogResult.Cancel());
        public Task SetOptionsAsync(AppDialogOptions options) => Task.CompletedTask;

        public Task SetPresentationClassAsync(string? value)
        {
            PresentationClass = value;
            return Task.CompletedTask;
        }

        public void SetCloseGuard(Func<Task<bool>>? guard)
        {
        }

        public IDisposable RegisterCloseGuard(Func<Task<bool>> guard) => new Registration();

        private sealed class Registration : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
