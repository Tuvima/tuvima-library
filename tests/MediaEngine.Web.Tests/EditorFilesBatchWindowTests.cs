using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Library;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// "Editing N files" through the editor's pop-over (universal editor v5 WP8, acceptance A7, screen F, D36): "All supporting files" opens the
/// browser, two ticked files open the framed window with its facts line, tabs and Delete, a group change shows in Review changes, and each
/// file's history is read only when its section is opened. The Engine is a hand-written stub; nothing is read, saved or removed anywhere.
/// </summary>
public sealed class EditorFilesBatchWindowTests : AsyncBunitContext
{
    private static readonly Guid TitleId = SupportingFileEditorTestData.TitleId;
    private static readonly Guid FileId = SupportingFileEditorTestData.FileId;
    private static readonly Guid[] RowIds =
    [
        Guid.Parse("c1000000-0000-0000-0000-0000000000a1"),
        Guid.Parse("c1000000-0000-0000-0000-0000000000a2"),
        Guid.Parse("c1000000-0000-0000-0000-0000000000a3"),
    ];

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly DashboardSessionAccessor _session = new();
    private readonly List<Guid> _historyReads = [];
    private readonly IRenderedComponent<AppDialogHost> _dialogs;
    private readonly IRenderedComponent<EditorPopover> _popover;

    public EditorFilesBatchWindowTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetSupportingFileEditorAsync), args =>
                Task.FromResult<EditorSupportingFileDto?>(SupportingFileEditorTestData.File((Guid)args![1]!, group: null, mediaType: "TV")));
            stub.SetHandler(nameof(IEngineApiClient.ListSupportingFilesAsync), _ => Task.FromResult<SupportingFileListDto?>(Page()));
            stub.SetHandler(nameof(IEngineApiClient.PreviewSupportingFilesChangeAsync), _ =>
                Task.FromResult(LibraryBatchEditOutcome<SupportingFileBulkUpdateResultDto>.Ok(new SupportingFileBulkUpdateResultDto { Preview = true })));
            stub.SetHandler(nameof(IEngineApiClient.GetSupportingFileHistoryAsync), args =>
            {
                _historyReads.Add((Guid)args![1]!);
                return Task.FromResult<(List<LibraryItemHistoryDto> Items, string? Error)>(([], null));
            });
        }));
        Services.AddSingleton(_device);
        Services.AddSingleton(_session);
        _dialogs = Render<AppDialogHost>();
        _popover = Render<EditorPopover>();
    }

    private static SupportingFileRowDto Row(Guid id, string title, string typeLabel) => new()
    {
        Id = id.ToString("D"),
        Title = title,
        HasOwnTitle = true,
        FileName = title + ".pdf",
        Type = SupportingFileListDefaults.TypeDocument,
        TypeLabel = typeLabel,
        Badge = typeLabel,
        WhereKey = "root",
        WhereLabel = "Title level",
        Role = "workbook",
        RoleLabel = "Workbook",
        SizeBytes = 2048,
    };

    private static SupportingFileListDto Page() => new()
    {
        Items = [Row(RowIds[0], "Character guide", "PDF"), Row(RowIds[1], "Episode notes", "PDF"), Row(RowIds[2], "Workbook", "PDF")],
        Total = 3,
        TotalAll = 3,
        Page = 1,
        PageSize = 25,
        PageCount = 1,
        WhereOptions = [new SupportingFileWhereOptionDto { Key = "all", Label = "Whole title", Level = "all", FileCount = 3 }],
    };

    // Opens the editor on a file (which gives it the title's header), then "All supporting files": the browser, two ticks, Edit selected.
    private async Task<EditorHost> OpenEditingTwoFilesAsync()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var id = Guid.NewGuid();
        _session.Set("test-session-token", id, id, Guid.NewGuid(),
            new DashboardAuthorityResponse(id, id, true, true, 1, 1, true, true, null, 1, [], [], []));
        var host = Services.GetRequiredService<EditorHost>();
        var binding = new EditorBinding(_device)
        {
            EntityId = TitleId,
            HasPermission = true,
            ShowHistory = true,
            Trail = () => [new EditTrailItem(TitleId, SupportingFileEditorTestData.TitleName, EditTrailKinds.Show)],
            OpenItem = _ => Task.FromResult(true),
        };
        Assert.True(await host.OpenAsync(binding, TitleId));
        await _popover.InvokeAsync(() => host.HeaderHooks.OpenSupportingFile!(FileId));
        _popover.WaitForAssertion(() => Assert.Single(_popover.FindAll("[data-supporting-file]")));

        // The row the nav's "All supporting files (N)" is; it opens the browser and does not wait for it to close.
        _ = _popover.InvokeAsync(() => host.HeaderHooks.OpenSupportingFiles!(new EditorSupportingFilesRequest(TitleId, null)));
        _dialogs.WaitForAssertion(() => Assert.Single(_dialogs.FindAll("[data-testid=file-browser]")));
        _dialogs.WaitForAssertion(() => Assert.Equal(3, _dialogs.FindAll("tr[data-file-id]").Count));
        _dialogs.Find($"tr[data-file-id='{RowIds[0]:D}'] input[type=checkbox]").Change(true);
        _dialogs.Find($"tr[data-file-id='{RowIds[1]:D}'] input[type=checkbox]").Change(true);
        _dialogs.Find("[data-browser-edit]").Click();
        _popover.WaitForAssertion(() => Assert.Single(_popover.FindAll("[data-testid=files-batch]")));
        return host;
    }

    [Fact]
    public async Task AllSupportingFiles_ThenTwoTicksAndEditSelected_ShowsTheFramedEditingTwoFilesWindow()
    {
        await OpenEditingTwoFilesAsync();

        var header = _popover.Find("[data-testid=files-batch-header]");
        Assert.Equal("Editing 2 files", header.QuerySelector("h2")!.TextContent.Trim());
        Assert.Contains(SupportingFileEditorTestData.TitleName, header.QuerySelector(".tl-editor-header__label")!.TextContent, StringComparison.Ordinal);
        var facts = header.QuerySelector("[data-testid=files-batch-facts]")!.TextContent;
        Assert.Contains("Character guide", facts, StringComparison.Ordinal);
        Assert.Contains("Episode notes", facts, StringComparison.Ordinal);
        Assert.NotEmpty(header.QuerySelectorAll("[data-files-batch-home]"));
        Assert.NotEmpty(header.QuerySelectorAll("[data-files-batch-back]"));
        Assert.Equal(["Details", "History"], _popover.FindAll(".tl-editor-window__tabs [role=tab]").Select(tab => tab.TextContent.Trim()).ToList());
        Assert.Single(_popover.FindAll("[data-files-batch-delete]"));
        Assert.Contains("2 files", _popover.Find(".tl-editor-window__changes").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGroupChange_ShowsInReviewChanges_OneRowPerFile_AndOneCreateGroupRow()
    {
        var host = await OpenEditingTwoFilesAsync();

        await _popover.InvokeAsync(() => host.FileBrowser.StageGroup("Workshop"));

        var rows = EditReviewRows.Build(host.Session.Pending, _ => "Steph Curry Teaches Shooting", null);
        Assert.Equal(2, rows.Count(row => row.FieldKey == SupportingFileFields.Group && row.After == "Workshop"));
        Assert.Single(rows, row => row.FieldKey == SupportingFileFields.CreateGroup);
    }

    [Fact]
    public async Task TheHistoryTab_HasOneCollapsedSectionPerFile_AndReadsAFilesHistoryOnlyWhenItIsOpened()
    {
        await OpenEditingTwoFilesAsync();

        _popover.FindAll(".tl-editor-window__tabs [role=tab]")[1].Click();

        _popover.WaitForAssertion(() => Assert.Single(_popover.FindAll("[data-testid=files-batch-history]")));
        Assert.Empty(_historyReads);
        var panels = _popover.FindAll("[data-testid=files-batch-history] .app-expansion-panel__header, [data-testid=files-batch-history] button[aria-expanded]");
        Assert.Equal(2, panels.Count);
        panels[0].Click();
        _popover.WaitForAssertion(() => Assert.Single(_historyReads));
        Assert.Equal(RowIds[0], _historyReads[0]);
    }

    [Fact]
    public async Task TheGroupField_StagesOnPick_NotWhileTyping_AndClearingAMixedFieldStagesNothing()
    {
        var host = await OpenEditingTwoFilesAsync();
        var autocomplete = _popover.FindComponent<AppAutocomplete<string>>();

        await _popover.InvokeAsync(() => autocomplete.Instance.ValueChanged.InvokeAsync(string.Empty));
        Assert.Empty(host.Session.Pending.SupportingFileStages);

        await _popover.InvokeAsync(() => autocomplete.Instance.ValueChanged.InvokeAsync("Workshop"));
        Assert.Equal(2, host.Session.Pending.SupportingFileStages.Count(stage => stage.GroupName == "Workshop"));

        // The two files had no group, so the field no longer reads Mixed once they share the new one; clearing it takes the change back.
        await _popover.InvokeAsync(() => autocomplete.Instance.ValueChanged.InvokeAsync(string.Empty));
        Assert.DoesNotContain(host.Session.Pending.SupportingFileStages, stage => stage.GroupName == "Workshop");
    }
}
