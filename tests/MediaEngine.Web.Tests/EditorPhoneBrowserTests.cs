using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Models;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The supporting-files browser on a phone (universal editor v6 WP17, D19 to D22): full screen, search at the top, the filters in a sheet, a
/// Select control that turns ticking on, the bulk actions in a bar at the bottom and "What is this file?" sorting in the Folders view, all by touch.
/// Every test walks real handlers (a tap on a row, a tap on a menu item); nothing is sent to the Engine and nothing is written anywhere.
/// </summary>
public sealed class EditorPhoneBrowserTests : AsyncBunitContext
{
    private static readonly Guid TitleId = SupportingFileEditorTestData.TitleId;
    private static readonly Guid[] FileIds =
    [
        Guid.Parse("c1000000-0000-0000-0000-0000000000a1"),
        Guid.Parse("c1000000-0000-0000-0000-0000000000a2"),
        Guid.Parse("c1000000-0000-0000-0000-0000000000a3"),
    ];

    private readonly PendingEditList _pending = new();
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly RecordingDialogContext _dialog = new();
    private readonly List<string> _calls = [];
    private readonly IEngineApiClient _api;
    private Func<SupportingFileListRequest, SupportingFileListDto?> _list = _ => Page();
    private EditorFolderTreeDto? _tree;

    public EditorPhoneBrowserTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton(_device);
        _api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.ListSupportingFilesAsync), args =>
            {
                _calls.Add("list");
                return Task.FromResult(_list((SupportingFileListRequest)args![1]!));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetEditorFolderTreeAsync), _ => Task.FromResult(_tree));
            stub.SetHandler(nameof(IEngineApiClient.GetEditorFileSortingAsync), _ => Task.FromResult<EditorFileSortingDto?>(new EditorFileSortingDto
            {
                EntityId = Guid.NewGuid(),
                CanSort = true,
                FileName = "odd-file.bin",
                TitleKind = EditorFileSortingTitleKinds.Show,
                TitleName = "Masterclass",
                CanBeExtra = true,
                InTitleFolder = true,
                ExtraKinds = [ExtraKinds.Trailer, ExtraKinds.Featurette],
                Guess = new EditorFileGuessDto { ExtraKind = ExtraKinds.Trailer },
            }));
            stub.SetHandler(nameof(IEngineApiClient.PreviewSupportingFilesChangeAsync), _ =>
            {
                _calls.Add("preview");
                return Task.FromResult(LibraryBatchEditOutcome<SupportingFileBulkUpdateResultDto>.Ok(new SupportingFileBulkUpdateResultDto { Preview = true }));
            });
        });
        Services.AddSingleton(_api);
    }

    private static SupportingFileRowDto Row(Guid id, string title, string? group = null) => new()
    {
        Id = id.ToString("D"),
        Title = title,
        HasOwnTitle = true,
        FileName = title + ".pdf",
        Type = SupportingFileListDefaults.TypeDocument,
        TypeLabel = "PDF",
        Badge = "PDF",
        WhereKey = "root",
        WhereLabel = "Title level",
        Role = "workbook",
        RoleLabel = "Workbook",
        Group = group,
        SizeBytes = 2048,
    };

    private static SupportingFileListDto Page() => new()
    {
        Items = [Row(FileIds[0], "Workbook"), Row(FileIds[1], "Cheat sheet", group: "Guides"), Row(FileIds[2], "Warm-up")],
        Total = 3,
        TotalAll = 3,
        Page = 1,
        PageSize = 25,
        PageCount = 1,
        Groups = [new SupportingFileGroupDto { Id = "g1", Name = "Guides", FileCount = 1 }],
        UngroupedCount = 2,
        WhereOptions = [new SupportingFileWhereOptionDto { Key = "all", Label = "Whole title", Level = "all", FileCount = 3 }],
    };

    private async Task<(EditorFileBrowserState State, IRenderedComponent<EditorFileBrowserDialog> Cut)> OpenAsync(
        bool phone = true, EditorBrowserView view = EditorBrowserView.Files)
    {
        await _device.InitialiseAsync(phone
            ? new DeviceContextSnapshot("web", "phone", true)
            : new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var state = new EditorFileBrowserState(_api, () => _pending);
        await state.OpenAsync(TitleId, SupportingFileEditorTestData.TitleName, view);
        var cut = Render<EditorFileBrowserDialog>(parameters => parameters
            .AddCascadingValue<IAppDialogContext>(_dialog)
            .Add(component => component.State, state)
            .Add(component => component.Session, new EditSessionState(_pending))
            .Add(component => component.PhoneArrangement, phone));
        return (state, cut);
    }

    [Fact]
    public async Task ThreeFiles_AreTickedAndGivenAGroup_ByTouchAlone_AndOnlyStagedNothingSent()
    {
        var (state, cut) = await OpenAsync();
        Assert.Empty(cut.FindAll(".app-browser-item input[type=checkbox]"));

        // Select turns ticking on; a tap on a row's name then ticks it (no tiny checkbox to aim at).
        cut.Find("[data-testid=file-browser-select]").Click();
        foreach (var name in cut.FindAll("[data-file-id] .app-browser-item__namecell"))
        {
            name.Click();
        }

        cut.WaitForAssertion(() => Assert.Equal("3 selected", cut.Find("[data-testid=file-browser-bulk] .tl-fbbulk__count").TextContent.Trim()));
        Assert.Equal(3, state.Ticked.Count);

        // Set group is a bottom sheet on a phone; New group asks for a name, Apply stages it for the three files.
        cut.Find("[data-bulk=group] button.app-overflow-menu__trigger, [data-bulk=group] button[aria-haspopup=menu]").Click();
        cut.WaitForElement("[role=menu] [role=menuitem]");
        cut.Find("[role=menu] [role=menuitem]:contains('New group')").Click();
        cut.WaitForElement("[data-testid=file-browser-bulk-ask] input").Input("Workshop");
        cut.Find("[data-testid=file-browser-bulk-ask] button:contains('Apply')").Click();

        cut.WaitForAssertion(() => Assert.Equal(3, _pending.SupportingFileStages.Count));
        Assert.All(_pending.SupportingFileStages, stage => Assert.Equal("Workshop", stage.GroupName));
        Assert.DoesNotContain("preview", _calls);
    }

    [Fact]
    public async Task PickingAnExistingGroup_FromTheSheet_StagesItForEveryTickedFile()
    {
        var (_, cut) = await OpenAsync();
        cut.Find("[data-testid=file-browser-select]").Click();
        // Cheat sheet is already in Guides, so the two that change are the other two.
        var names = cut.FindAll("[data-file-id] .app-browser-item__namecell");
        names[0].Click();
        names[2].Click();

        cut.Find("[data-bulk=group] button[aria-haspopup=menu]").Click();
        cut.WaitForElement("[role=menu] [role=menuitem]");
        cut.Find("[role=menu] [role=menuitem]:contains('Guides')").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, _pending.SupportingFileStages.Count));
        Assert.All(_pending.SupportingFileStages, stage => Assert.Equal("Guides", stage.GroupName));
    }

    [Fact]
    public async Task Done_LeavesSelecting_DropsTheTicks_AndTheBarGoesAway()
    {
        var (state, cut) = await OpenAsync();
        cut.Find("[data-testid=file-browser-select]").Click();
        cut.Find("[data-file-id] .app-browser-item__namecell").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=file-browser-bulk]")));

        cut.Find("[data-testid=file-browser-select]").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=file-browser-bulk]")));
        Assert.Empty(state.Ticked);
        Assert.Empty(cut.FindAll(".app-browser-item input[type=checkbox]"));
    }

    [Fact]
    public async Task WithoutSelect_TappingARow_OpensTheFileInsteadOfTickingIt()
    {
        var (state, cut) = await OpenAsync();

        cut.Find("[data-file-id] .app-browser-item__namecell").Click();

        Assert.Empty(state.Ticked);
        var outcome = Assert.IsType<EditorFileBrowserOutcome>(_dialog.Result?.Data);
        Assert.NotEqual(EditorFileBrowserAction.EditSelected, outcome.Action);
    }

    [Fact]
    public async Task TheFilters_LiveInASheet_NotBesideTheList()
    {
        var (_, cut) = await OpenAsync();
        Assert.Equal(0, cut.Find("[data-testid=file-browser-sidebar]").ChildElementCount);
        Assert.Empty(cut.FindAll("[data-testid=file-browser-filters-sheet]"));

        cut.Find("[data-testid=file-browser-filters-button]").Click();

        cut.WaitForElement("[data-testid=file-browser-filters-sheet]");
        cut.Find("[data-testid=file-browser-filters-done]").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=file-browser-filters-sheet]")));
    }

    [Fact]
    public async Task OnADesktop_ThereIsNoSelectControl_AndEveryRowStillHasItsTick()
    {
        var (_, cut) = await OpenAsync(phone: false);

        Assert.Empty(cut.FindAll("[data-testid=file-browser-select]"));
        Assert.Empty(cut.FindAll("[data-testid=file-browser-filters-button]"));
        Assert.Equal(3, cut.FindAll(".app-browser-item input[type=checkbox]").Count);
    }

    [Fact]
    public async Task WhenTheListCannotBeRead_APlainErrorAndTryAgainShow_AndWhatWasStagedIsKept()
    {
        var (state, cut) = await OpenAsync();
        cut.Find("[data-testid=file-browser-select]").Click();
        foreach (var name in cut.FindAll("[data-file-id] .app-browser-item__namecell"))
        {
            name.Click();
        }

        await cut.InvokeAsync(() => state.StageGroup("Workshop"));
        Assert.Equal(3, _pending.SupportingFileStages.Count);

        // The connection drops: the next read fails.
        _list = _ => null;
        await cut.InvokeAsync(() => state.SetQueryAsync("work"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=file-browser-reload-error]")));
        Assert.Equal(3, _pending.SupportingFileStages.Count);
        _list = _ => Page();
        cut.Find("[data-reload]").Click();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-file-id]").Count));
        Assert.Equal(3, _pending.SupportingFileStages.Count);
    }

    [Fact]
    public async Task InTheFoldersView_AFileTheEngineCouldNotPlace_IsSortedByTouch()
    {
        _tree = new EditorFolderTreeDto
        {
            RootName = "Masterclass",
            Total = 2,
            Entries =
            [
                new EditorFolderEntryDto { Name = "Lesson 01.mkv", Path = "Lesson 01.mkv", State = EditorFolderEntryStates.Main, ItemId = Guid.NewGuid() },
                new EditorFolderEntryDto { Name = "odd-file.bin", Path = "odd.bin", State = EditorFolderEntryStates.Unsorted, ItemId = Guid.NewGuid() },
            ],
        };
        var (_, cut) = await OpenAsync(view: EditorBrowserView.Folders);

        cut.WaitForElement("[data-testid=file-browser-folders]");
        Assert.NotEmpty(cut.FindAll("[data-testid=file-browser-back-to-files]"));
        cut.Find("[data-entry=\"odd.bin\"] [data-entry-sort]").Click();

        cut.WaitForElement("[data-testid=file-sort]");
        Assert.NotEmpty(cut.FindAll("[data-testid=file-sort] button"));
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
}
