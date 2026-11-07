using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Components.Details;
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

    public EditorSupportingFilePopoverTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetSupportingFileEditorAsync), _ => Task.FromResult<EditorSupportingFileDto?>(_file));
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
    public async Task TheHistoryTab_SaysAFilesChangesAreNotKeptYet_InsteadOfListingNothing()
    {
        var host = await OpenFileAsync(administrator: true);

        await _popover.InvokeAsync(() => host.Flow.SelectToolAsync(EditTool.History));

        _popover.WaitForAssertion(() => Assert.Single(_popover.FindAll("[data-supporting-file-history]")));
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
