using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Library;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The supporting-files browser (universal editor v5 WP8, acceptance A7, screens E and F): search, the Where and Type filters, paging from the
/// Engine's page size, ticks, the bulk actions that only stage, Delete that asks once and happens at once, the Folders and files view, and the
/// states (loading, empty, nothing matches, error). The Engine is a hand-written stub; no file is read, moved or removed anywhere.
/// </summary>
public sealed class EditorFileBrowserTests : AsyncBunitContext
{
    private static readonly Guid TitleId = SupportingFileEditorTestData.TitleId;
    private static readonly Guid[] FileIds =
    [
        Guid.Parse("c1000000-0000-0000-0000-0000000000a1"),
        Guid.Parse("c1000000-0000-0000-0000-0000000000a2"),
        Guid.Parse("c1000000-0000-0000-0000-0000000000a3"),
    ];

    private readonly List<SupportingFileListRequest> _lists = [];
    private readonly List<SupportingFileBulkDeleteRequest> _deletes = [];
    private readonly List<string> _calls = [];
    private readonly PendingEditList _pending = new();
    private Func<SupportingFileListRequest, SupportingFileListDto?> _list = _ => Page();
    private EditorFolderTreeDto? _tree;
    private readonly IEngineApiClient _api;
    private readonly RecordingDialogContext _dialog = new();

    public EditorFileBrowserTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        _api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.ListSupportingFilesAsync), args =>
            {
                var request = (SupportingFileListRequest)args![1]!;
                _lists.Add(request);
                _calls.Add("list");
                return Task.FromResult(_list(request));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetEditorFolderTreeAsync), _ => Task.FromResult(_tree));
            stub.SetHandler(nameof(IEngineApiClient.GetSupportingFileEditorAsync), args =>
                Task.FromResult<EditorSupportingFileDto?>(SupportingFileEditorTestData.File(
                    (Guid)args![1]!, group: null, mediaType: "TV")));
            stub.SetHandler(nameof(IEngineApiClient.PreviewSupportingFilesChangeAsync), _ =>
            {
                _calls.Add("preview");
                return Task.FromResult(LibraryBatchEditOutcome<SupportingFileBulkUpdateResultDto>.Ok(new SupportingFileBulkUpdateResultDto { Preview = true }));
            });
            stub.SetHandler(nameof(IEngineApiClient.DeleteSupportingFilesAsync), args =>
            {
                var request = (SupportingFileBulkDeleteRequest)args![1]!;
                _deletes.Add(request);
                _calls.Add("delete");
                return Task.FromResult(LibraryBatchEditOutcome<SupportingFileBulkDeleteResultDto>.Ok(new SupportingFileBulkDeleteResultDto
                {
                    Removed = request.Ids.Count,
                    Results = [.. request.Ids.Select(id => new SupportingFileDeleteItemResultDto { Id = id, Status = SupportingFileBulkStatuses.Removed, Disk = SupportingFileDiskOutcomes.Recycled })],
                }));
            });
        });
        Services.AddSingleton(_api);
        Host = Render<AppDialogHost>();
    }

    private IRenderedComponent<AppDialogHost> Host { get; }

    private static SupportingFileRowDto Row(Guid id, string title, string? group = null, string type = SupportingFileListDefaults.TypeDocument) => new()
    {
        Id = id.ToString("D"),
        Title = title,
        HasOwnTitle = true,
        FileName = title + ".pdf",
        Type = type,
        TypeLabel = type == SupportingFileListDefaults.TypeDocument ? "PDF" : "Video",
        Badge = "PDF",
        WhereKey = "root",
        WhereLabel = "Title level",
        Role = "workbook",
        RoleLabel = "Workbook",
        Group = group,
        SizeBytes = 2048,
    };

    private static SupportingFileListDto Page(int page = 1, int total = 238, int pageCount = 10, int pageSize = 25) => new()
    {
        Items = [Row(FileIds[0], "Workbook"), Row(FileIds[1], "Cheat sheet", group: "Guides"), Row(FileIds[2], "Warm-up", type: SupportingFileListDefaults.TypeClip)],
        Total = total,
        TotalAll = total,
        Page = page,
        PageSize = pageSize,
        PageCount = pageCount,
        Groups = [new SupportingFileGroupDto { Id = "g1", Name = "Guides", FileCount = 1 }],
        UngroupedCount = 2,
        WhereOptions =
        [
            new SupportingFileWhereOptionDto { Key = "all", Label = "Whole title", Level = "all", FileCount = total },
            new SupportingFileWhereOptionDto { Key = "season:2", Label = "Season 2", Level = "season", FileCount = 40 },
        ],
    };

    private EditorFileBrowserState NewState() => new(_api, () => _pending);

    private async Task<(EditorFileBrowserState State, IRenderedComponent<EditorFileBrowserDialog> Cut)> OpenAsync(
        EditorBrowserView view = EditorBrowserView.Files)
    {
        var state = NewState();
        await state.OpenAsync(TitleId, SupportingFileEditorTestData.TitleName, view);
        var cut = Render<EditorFileBrowserDialog>(parameters => parameters
            .AddCascadingValue<IAppDialogContext>(_dialog)
            .Add(component => component.State, state)
            .Add(component => component.Session, new EditSessionState(_pending)));
        return (state, cut);
    }

    // -- The list -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Opening_ShowsTheFirstPage_WithTheCountLineAndTheEnginesPageSize()
    {
        var (_, cut) = await OpenAsync();

        Assert.Equal(3, cut.FindAll("[data-file-id]").Count);
        Assert.Contains("of 238", cut.Find("[data-testid=file-browser-count]").TextContent, StringComparison.Ordinal);
        Assert.Equal("1 of 10", cut.Find("[data-testid=file-browser-pager] .tl-fbrowser__page").TextContent.Trim());
        // The Dashboard asks for a page by number only: how many rows a page holds is the Engine's to say.
        Assert.All(_lists, request => Assert.Equal(1, request.Page));
    }

    [Fact]
    public async Task TheNextPage_AsksTheEngineForPageTwo_AndKeepsTheFilters()
    {
        var (state, cut) = await OpenAsync();
        await cut.InvokeAsync(() => state.SetTypeAsync(SupportingFileListDefaults.TypeClip));
        _list = request => Page(page: request.Page);

        cut.Find("[data-page=next]").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, _lists[^1].Page));
        Assert.Equal(SupportingFileListDefaults.TypeClip, _lists[^1].Type);
    }

    [Fact]
    public async Task ChangingAFilter_GoesBackToPageOne()
    {
        var (state, cut) = await OpenAsync();
        _list = request => Page(page: request.Page);
        await cut.InvokeAsync(() => state.GoToPageAsync(3));

        await cut.InvokeAsync(() => state.SetWhereAsync("season:2"));

        Assert.Equal(1, _lists[^1].Page);
        Assert.Equal("season:2", _lists[^1].Where);
    }

    [Fact]
    public async Task Searching_AsksTheEngineWithTheWords()
    {
        var (state, cut) = await OpenAsync();

        await cut.InvokeAsync(() => state.SetQueryAsync("workbook"));

        Assert.Equal("workbook", _lists[^1].Query);
    }

    [Fact]
    public async Task ASingleExactPage_HasNoPager()
    {
        _list = _ => Page(total: 3, pageCount: 1);
        var (_, cut) = await OpenAsync();

        Assert.Empty(cut.FindAll("[data-testid=file-browser-pager]"));
    }

    // -- States ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATitleWithNoSupportingFiles_ShowsTheEmptyState_NotATable()
    {
        _list = _ => new SupportingFileListDto { Total = 0, TotalAll = 0 };
        var (_, cut) = await OpenAsync();

        Assert.Contains("No supporting files yet", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=file-browser-table]"));
    }

    [Fact]
    public async Task AFilterThatMatchesNothing_SaysSo_AndClearFiltersBringsTheListBack()
    {
        _list = request => request.Type is null
            ? Page()
            : new SupportingFileListDto { Total = 0, TotalAll = 238, WhereOptions = Page().WhereOptions };
        var (state, cut) = await OpenAsync();
        await cut.InvokeAsync(() => state.SetTypeAsync(SupportingFileListDefaults.TypeAudio));

        Assert.Single(cut.FindAll("[data-testid=file-browser-nothing]"));
        cut.Find("[data-clear-filters]").Click();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-file-id]").Count));
        Assert.Null(_lists[^1].Type);
    }

    [Fact]
    public async Task AListThatCannotBeRead_ShowsAnError_WithARetry()
    {
        _list = _ => null;
        var (_, cut) = await OpenAsync();

        Assert.Empty(cut.FindAll("[data-file-id]"));
        Assert.NotEmpty(cut.FindAll("[role=alert]"));
        _list = _ => Page();
        cut.Find("button:contains('Retry')").Click();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-file-id]").Count));
    }

    // -- Ticks and the bulk bar -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TickingTheHeader_TicksThePage_AndEditSelectedWaitsForATick()
    {
        var (state, cut) = await OpenAsync();
        Assert.True(cut.Find("[data-browser-edit]").HasAttribute("disabled"));

        await cut.InvokeAsync(() => state.TickPage());

        cut.WaitForAssertion(() => Assert.Equal("3 selected", cut.Find("[data-testid=file-browser-bulk] .tl-fbbulk__count").TextContent.Trim()));
        Assert.False(cut.Find("[data-browser-edit]").HasAttribute("disabled"));
    }

    [Fact]
    public async Task EditSelected_ClosesTheBrowser_AndAnswersWithTheEditSelectedOutcome()
    {
        var (state, cut) = await OpenAsync();
        await cut.InvokeAsync(() => state.TickPage());

        cut.Find("[data-browser-edit]").Click();

        var outcome = Assert.IsType<EditorFileBrowserOutcome>(_dialog.Result?.Data);
        Assert.Equal(EditorFileBrowserAction.EditSelected, outcome.Action);
        Assert.Equal(3, state.Ticked.Count);
    }

    [Fact]
    public async Task SetGroup_OnTickedFiles_OnlyStagesOneChangePerFile_NothingIsSentToTheEngine()
    {
        var (state, cut) = await OpenAsync();
        await cut.InvokeAsync(() => state.TickPage());

        await cut.InvokeAsync(() => state.StageGroup("Workshop"));

        Assert.Equal(3, _pending.SupportingFileStages.Count);
        Assert.All(_pending.SupportingFileStages, stage => Assert.Equal("Workshop", stage.GroupName));
        Assert.DoesNotContain("delete", _calls);
        Assert.DoesNotContain("preview", _calls);
    }

    [Fact]
    public async Task ChoosingAnotherPlace_StagesIt_AndAsksTheEngineOnceWhichFilesWouldMove()
    {
        var (state, cut) = await OpenAsync();
        await cut.InvokeAsync(() => state.TickPage());
        await cut.InvokeAsync(() => state.EnsureFacetsAsync());

        await cut.InvokeAsync(() => state.StageScopeAsync("season:2"));

        Assert.Equal(3, _pending.SupportingFileStages.Count(stage => stage.ScopeKey == "season:2"));
        Assert.Single(_calls, "preview");
    }

    [Fact]
    public async Task Ticks_SurviveAPageChange()
    {
        var (state, cut) = await OpenAsync();
        _list = request => Page(page: request.Page);
        await cut.InvokeAsync(() => state.Toggle(state.Rows[0]));

        await cut.InvokeAsync(() => state.GoToPageAsync(2));

        Assert.Single(state.Ticked);
    }

    // -- Delete ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_AsksOnce_ThenHappensAtOnce_WithoutWaitingForSave()
    {
        var (state, cut) = await OpenAsync();
        await cut.InvokeAsync(() => state.TickPage());

        await Host.InvokeAsync(() => cut.Find("[data-bulk=delete]").Click());

        Host.WaitForAssertion(() => Assert.Contains("Delete 3 files?", Host.Markup, StringComparison.Ordinal));
        Assert.Empty(_deletes);
        Host.Find("footer button:contains('Delete')").Click();

        Host.WaitForAssertion(() => Assert.Single(_deletes));
        Assert.Equal(3, _deletes[0].Ids.Count);
        Assert.Empty(_pending.SupportingFileStages);
    }

    // -- Folders and files ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FoldersAndFiles_ListsFoldersFirst_MarksEachFile_AndKeepsToTheTitlesFolder()
    {
        _tree = new EditorFolderTreeDto
        {
            RootName = "Steph Curry Teaches Shooting",
            Total = 4,
            Entries =
            [
                new EditorFolderEntryDto { Name = "Season 02", Path = "Season 02", IsFolder = true },
                new EditorFolderEntryDto { Name = "Lesson 01.mkv", Path = "Lesson 01.mkv", State = EditorFolderEntryStates.Main, ItemId = Guid.NewGuid() },
                new EditorFolderEntryDto { Name = "Workbook.pdf", Path = "Workbook.pdf", State = EditorFolderEntryStates.Supporting, SupportingFileId = FileIds[0] },
                new EditorFolderEntryDto { Name = "notes.tmp", Path = "notes.tmp", State = EditorFolderEntryStates.Hidden },
                new EditorFolderEntryDto { Name = "odd.bin", Path = "odd.bin", State = EditorFolderEntryStates.Unsorted, ItemId = Guid.NewGuid() },
            ],
        };
        var (_, cut) = await OpenAsync(EditorBrowserView.Folders);

        var entries = cut.FindAll("[data-entry]");
        Assert.Equal("folder", entries[0].GetAttribute("data-state"));
        Assert.Contains("Not shown in the library", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Not sorted yet", cut.Markup, StringComparison.Ordinal);
        // "What is this file?" is offered for the file the Engine could not place, never for a folder or a hidden file.
        Assert.NotEmpty(cut.FindAll("[data-entry=\"odd.bin\"] [data-entry-sort]"));
        Assert.Empty(cut.FindAll("[data-entry=\"notes.tmp\"] [data-entry-sort]"));
        Assert.Empty(cut.FindAll("[data-entry=\"Season 02\"] [data-entry-sort]"));
    }

    [Fact]
    public async Task AFolderThatCannotBeRead_ShowsAnError_NotAnEmptyFolder()
    {
        _tree = null;
        var (_, cut) = await OpenAsync(EditorBrowserView.Folders);

        Assert.NotEmpty(cut.FindAll("[role=alert]"));
        Assert.DoesNotContain("Nothing in this folder", cut.Markup, StringComparison.Ordinal);
    }

    // -- The shared browser frame (D32, WP8a, A13) ---------------------------------------------------------------------------------

    [Fact]
    public async Task TheSupportingFilesList_IsTheSharedBrowserFrame_WithItemRowsAndTheToolbarAcrossTheWindow()
    {
        var (_, cut) = await OpenAsync();

        var frame = cut.Find("[data-testid=browser-frame]");
        Assert.Equal("files", frame.GetAttribute("data-frame"));
        Assert.Single(cut.FindAll("[data-frame-sidebar]"));
        Assert.Equal(3, cut.FindAll("table[role=grid] .app-browser-item").Count);
        Assert.Empty(cut.FindAll(".app-browser-folder"));
        // Screen E: the search and the two filters are one toolbar across the whole window, above the left list.
        Assert.Equal("app-browser__toolbar", frame.Children[0].ClassList.First());
        Assert.Equal(2, cut.FindAll("[data-frame-toolbar] .tl-fbrowser__select").Count);
    }

    [Fact]
    public async Task TheFoldersAndFilesView_IsTheSameFrame_WithFolderRowsAndFilesBesideThem()
    {
        _tree = new EditorFolderTreeDto
        {
            RootName = "Steph Curry Teaches Shooting",
            Total = 2,
            Entries =
            [
                new EditorFolderEntryDto { Name = "Season 02", Path = "Season 02", IsFolder = true },
                new EditorFolderEntryDto { Name = "Workbook.pdf", Path = "Workbook.pdf", State = EditorFolderEntryStates.Supporting, SupportingFileId = FileIds[0] },
            ],
        };
        var (_, cut) = await OpenAsync(EditorBrowserView.Folders);

        Assert.Equal("folders", cut.Find("[data-testid=browser-frame]").GetAttribute("data-frame"));
        Assert.Single(cut.FindAll(".app-browser-folder--folder"));
        Assert.Single(cut.FindAll(".app-browser-folder--file"));
        Assert.Single(cut.FindAll("[data-frame-sidebar]"));
    }

    [Fact]
    public async Task TheFoldersView_AtTheItemsFolder_HasNoPathLineAndNoUpArrow()
    {
        _tree = new EditorFolderTreeDto
        {
            RootName = "Steph Curry Teaches Shooting",
            Total = 1,
            Entries = [new EditorFolderEntryDto { Name = "Season 02", Path = "Season 02", IsFolder = true }],
        };
        var (_, cut) = await OpenAsync(EditorBrowserView.Folders);

        Assert.Empty(cut.FindAll("[data-testid=file-browser-path]"));
        Assert.Empty(cut.FindAll("[data-folder-up]"));
        Assert.Empty(cut.FindAll("[data-testid=file-browser-crumbs]"));
        Assert.DoesNotContain("/media/", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFoldersView_InsideASubFolder_ShowsTheWayBackAtTheTop_StartingAtTheItemsOwnFolder()
    {
        _tree = new EditorFolderTreeDto
        {
            RootName = "Steph Curry Teaches Shooting",
            Total = 1,
            Entries = [new EditorFolderEntryDto { Name = "Season 02", Path = "Season 02", IsFolder = true }],
        };
        var (state, cut) = await OpenAsync(EditorBrowserView.Folders);
        var root = _tree;

        // The Engine answers with the folder it opened.
        _tree = new EditorFolderTreeDto
        {
            RootName = "Steph Curry Teaches Shooting",
            Folder = "Season 02",
            Total = 1,
            Entries = [new EditorFolderEntryDto { Name = "Lesson 01.mkv", Path = "Season 02/Lesson 01.mkv", State = EditorFolderEntryStates.Main, ItemId = Guid.NewGuid() }],
        };
        await cut.InvokeAsync(() => state.OpenFolderAsync("Season 02", 0));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=file-browser-crumbs]")));
        Assert.Equal("Steph Curry Teaches Shooting", cut.Find("[data-crumb='']").TextContent.Trim());
        Assert.Equal("Season 02", cut.Find("[data-crumb-current]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-folder-up]"));

        _tree = root;
        cut.Find("[data-crumb='']").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=file-browser-crumbs]")));
        Assert.Equal(string.Empty, state.Folder);
    }

    [Fact]
    public async Task TheArrowKeysMoveTheHighlight_SpaceTicksTheRow_AndEnterOpensItsPreview()
    {
        var (state, cut) = await OpenAsync();
        var grid = cut.Find("table[role=grid]");
        Assert.Equal("efb-row-0-name", grid.GetAttribute("aria-activedescendant"));

        grid.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowDown" });
        cut.WaitForAssertion(() => Assert.Equal("efb-row-1-name", cut.Find("table[role=grid]").GetAttribute("aria-activedescendant")));

        cut.Find("table[role=grid]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = " " });
        cut.WaitForAssertion(() => Assert.Single(state.Ticked));
        Assert.Equal(FileIds[1].ToString("D"), state.Ticked[0].Id);

        cut.Find("table[role=grid]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        var outcome = Assert.IsType<EditorFileBrowserOutcome>(_dialog.Result?.Data);
        Assert.Equal(EditorFileBrowserAction.OpenFile, outcome.Action);
        Assert.Equal(FileIds[1], outcome.Id);
        Assert.True(outcome.Preview);
    }

    // -- Editing several files together (screen F) ----------------------------------------------------------------------------------

    [Fact]
    public async Task EditingSeveralFiles_ShowsMixedValues_AndLeavesAFieldAloneUntilItIsChanged()
    {
        var (state, _) = await OpenAsync();
        await state.OpenAsync(TitleId, "Title", EditorBrowserView.Files);
        state.TickPage();
        await state.EnsureFacetsAsync();

        var panel = Render<EditorFilesBatchPanel>(parameters => parameters.Add(component => component.State, state));

        Assert.Contains("Title and Description are not offered for several files", panel.Markup, StringComparison.Ordinal);
        Assert.Contains("Mixed", panel.Find("[data-field-key=group]").InnerHtml, StringComparison.Ordinal);
        // The files are of two kinds, so Role is not offered.
        Assert.Empty(panel.FindAll("[data-field-key=role] select, [data-field-key=role] [role=combobox]"));
        Assert.Empty(_pending.SupportingFileStages);
    }

    [Fact]
    public async Task TheFolderButton_OpensTheFoldersViewAtTheHeadersFolder()
    {
        var state = NewState();

        await state.OpenAsync(TitleId, SupportingFileEditorTestData.TitleName, EditorBrowserView.Folders, folder: "Season 2", mediaType: "TV");

        Assert.Equal("Season 2", state.Folder);
        Assert.Equal("TV", state.MediaType);
    }

    [Fact]
    public async Task TickingPastTheLimit_RefusesTheTick_AndSaysSo()
    {
        _list = _ => new SupportingFileListDto
        {
            Items = [.. Enumerable.Range(0, EditorFileBrowserState.MaxTicked + 1).Select(i => Row(Guid.NewGuid(), "File " + i))],
            Total = EditorFileBrowserState.MaxTicked + 1,
            TotalAll = EditorFileBrowserState.MaxTicked + 1,
            Page = 1,
            PageSize = EditorFileBrowserState.MaxTicked + 1,
            PageCount = 1,
        };
        var (state, cut) = await OpenAsync();

        await cut.InvokeAsync(() => state.TickPage());

        Assert.Equal(EditorFileBrowserState.MaxTicked, state.Ticked.Count);
        Assert.True(state.TickCapReached);
    }

    private sealed class RecordingDialogContext : IAppDialogContext
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

    [Fact]
    public async Task TheFoldersAndFilesView_KeepsItsDetailsColumnAtEveryWidth_SoHeadingsAndCellsStayAligned()
    {
        _tree = new EditorFolderTreeDto
        {
            RootName = "Show",
            Total = 2,
            Entries =
            [
                new EditorFolderEntryDto { Name = "Season 02", Path = "Season 02", IsFolder = true },
                new EditorFolderEntryDto { Name = "Workbook.pdf", Path = "Workbook.pdf", State = EditorFolderEntryStates.Supporting, SupportingFileId = FileIds[0] },
            ],
        };
        var (_, cut) = await OpenAsync(EditorBrowserView.Folders);

        Assert.Empty(cut.FindAll(".is-wide-only"));
    }

    [Fact]
    public async Task EachSupportingFileRow_IsNamedByItsNameCell_ForTheScreenReaderCursor()
    {
        var (_, cut) = await OpenAsync();

        var cell = cut.Find("#efb-row-0-name");
        Assert.Equal("gridcell", cell.GetAttribute("role"));
        Assert.Equal("Workbook", cell.GetAttribute("aria-label"));
    }
}
