using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The pop-over around a supporting file (universal editor v5 WP7, screens C and D): the tabs a file has, what each one shows, and who sees
/// the red Delete in the footer. The Engine is a hand-written stub; nothing is read, saved or removed anywhere.
/// </summary>
public sealed class EditorSupportingFilePopoverTests : AsyncBunitContext
{
    private static readonly Guid TitleId = SupportingFileEditorTestData.TitleId;
    private static readonly Guid FileId = SupportingFileEditorTestData.FileId;

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly DashboardSessionAccessor _session = new();
    private readonly EditorSupportingFileDto _file = SupportingFileEditorTestData.File();
    private readonly IRenderedComponent<EditorPopover> _popover;
    private readonly List<(Guid Title, Guid File)> _historyReads = [];
    private List<LibraryItemHistoryDto> _history =
    [
        new() { Id = "1", EntityId = FileId, OccurredAt = DateTimeOffset.UtcNow, EventType = "supporting_file_edit", Label = "Details changed", Detail = "Title changed to \"Shooting workbook\".", Category = "manual", ActorLabel = "You" },
        new() { Id = "2", EntityId = FileId, OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-5), EventType = "supporting_file_moved", Label = "File moved", Detail = "Moved from \"Season 02/Workbook.pdf\" to \"Season 01/Workbook.pdf\".", Category = "file", ActorLabel = "You" },
    ];

    public EditorSupportingFilePopoverTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetSupportingFileEditorAsync), _ => Task.FromResult<EditorSupportingFileDto?>(_file));
            stub.SetHandler(nameof(IEngineApiClient.GetSupportingFileHistoryAsync), args =>
            {
                _historyReads.Add(((Guid)args![0]!, (Guid)args[1]!));
                return Task.FromResult<(List<LibraryItemHistoryDto> Items, string? Error)>((_history, null));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetExtraAsync), _ => Task.FromResult<ExtraViewDto?>(new ExtraViewDto
            {
                Id = FileId.ToString("D"),
                Kind = DetailExtraCardKinds.Document,
                Title = "Workbook",
                Items = [new ExtraViewItemDto { Id = FileId.ToString("D"), Title = "Workbook", FileName = "Workbook.pdf", MimeType = "application/pdf" }],
            }));
        }));
        Services.AddSingleton(_device);
        Services.AddSingleton(_session);
        _popover = Render<EditorPopover>();
    }

    private async Task<EditorHost> OpenFileAsync(bool administrator)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var id = Guid.NewGuid();
        _session.Set("test-session-token", id, id, Guid.NewGuid(),
            new DashboardAuthorityResponse(id, id, true, true, 1, 1, administrator, administrator, null, 1, [], [], []));
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
        return host;
    }

    private static IReadOnlyList<string> TabNames(IRenderedComponent<EditorPopover> popover) =>
        [.. popover.FindAll(".tl-editor-window__tabs [role=tab]").Select(tab => tab.TextContent.Trim())];

    [Fact]
    public async Task AFile_HasDetailsPreviewAndHistoryTabs_AndNeverArtworkOrMatch()
    {
        await OpenFileAsync(administrator: true);

        Assert.Equal(["Details", "Preview", "History"], TabNames(_popover));
        Assert.Contains("Workbook", _popover.Find(".tl-editor-header__title").TextContent, StringComparison.Ordinal);
        Assert.Single(_popover.FindAll("[data-supporting-file-chip]"));
    }

    [Fact]
    public async Task ThePreviewTab_ShowsTheDocumentInTheViewer()
    {
        var host = await OpenFileAsync(administrator: true);

        await _popover.InvokeAsync(() => host.Flow.SelectToolAsync(EditTool.Preview));

        _popover.WaitForAssertion(() => Assert.Single(_popover.FindAll("iframe[data-testid=extra-document]")));
        Assert.Empty(_popover.FindAll("[data-supporting-file]"));
    }

    [Fact]
    public async Task TheQuickFactsPreviewButton_OpensThePreviewTab()
    {
        var host = await OpenFileAsync(administrator: true);

        _popover.Find("[data-quick-facts-preview]").Click();

        _popover.WaitForAssertion(() => Assert.Equal(EditTool.Preview, host.Session.ActiveTool));
    }

    [Fact]
    public async Task TheHistoryTab_ListsWhatWasChangedOnTheFile_ReadFromTheFilesOwnHistory()
    {
        var host = await OpenFileAsync(administrator: true);

        await _popover.InvokeAsync(() => host.Flow.SelectToolAsync(EditTool.History));

        _popover.WaitForAssertion(() => Assert.Equal(2, _popover.FindAll("[data-supporting-file-history] .tl-history__row").Count));
        Assert.Equal((TitleId, FileId), Assert.Single(_historyReads));
        var text = _popover.Find("[data-supporting-file-history]").TextContent;
        Assert.Contains("Title changed to \"Shooting workbook\".", text, StringComparison.Ordinal);
        Assert.Contains("Moved from", text, StringComparison.Ordinal);
        Assert.Empty(_popover.FindAll("[data-supporting-file-history] [role=alert]"));
    }

    [Fact]
    public async Task TheHistoryTab_OfAFileWithNoChanges_SaysNothingHasHappenedYet()
    {
        _history = [];
        var host = await OpenFileAsync(administrator: true);

        await _popover.InvokeAsync(() => host.Flow.SelectToolAsync(EditTool.History));

        _popover.WaitForAssertion(() => Assert.Contains("Nothing has happened to this item yet.", _popover.Find("[data-supporting-file-history]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRedDelete_ShowsInTheFooterForAnAdministrator()
    {
        await OpenFileAsync(administrator: true);
        Assert.Single(_popover.FindAll(".tl-editor-window__delete"));
    }

    [Fact]
    public async Task TheRedDelete_IsNotShownToSomeoneWhoMayNotDelete()
    {
        await OpenFileAsync(administrator: false);
        Assert.Empty(_popover.FindAll(".tl-editor-window__delete"));
    }
}
