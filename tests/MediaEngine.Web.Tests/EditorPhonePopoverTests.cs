using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The editor's window on a phone (universal editor v6 section 4a, D22): the phone arrangement is told by the browser (a narrow window) or the
/// device, the footer then holds the write-back line and Discard and Save changes side by side, and Delete moves from the footer into the header's
/// "..." menu with its confirmation unchanged. The device here is a desktop that may edit: whether a phone may open the editor is another packet's.
/// </summary>
public sealed class EditorPhonePopoverTests : AsyncBunitContext
{
    private static readonly Guid TitleId = SupportingFileEditorTestData.TitleId;
    private static readonly Guid FileId = SupportingFileEditorTestData.FileId;

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly DashboardSessionAccessor _session = new();
    private readonly BunitJSModuleInterop _focusModule;
    private IRenderedComponent<EditorPopover> _popover = default!;

    public EditorPhonePopoverTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _focusModule = JSInterop.SetupModule("./js/editor-focus.js");
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetSupportingFileEditorAsync), _ => Task.FromResult<EditorSupportingFileDto?>(SupportingFileEditorTestData.File()));
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
    }

    private async Task<EditorHost> OpenFileAsync(bool narrowWindow)
    {
        _focusModule.Setup<bool>("watchLayout", _ => true).SetResult(narrowWindow);
        _popover = Render<EditorPopover>();
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
        return host;
    }

    [Fact]
    public async Task OnAPhone_TheFooterHasNoDelete_AndTheHeadersMoreMenuHasIt()
    {
        await OpenFileAsync(narrowWindow: true);

        _popover.WaitForAssertion(() => Assert.Single(_popover.FindAll(".tl-editor-window--phone")));
        Assert.Empty(_popover.FindAll(".tl-editor-window__footer .tl-editor-window__delete, .tl-editor-window__footer-start"));
        var more = _popover.Find("[data-testid=phone-header] .app-overflow-menu__trigger, [data-testid=phone-header] .app-row-menu__trigger");
        more.Click();
        var item = _popover.WaitForElement(".app-menu-item");
        Assert.Equal("Delete", item.TextContent.Trim());
    }

    [Fact]
    public async Task OnAPhone_TheFooterIsTheWriteBackLineThenDiscardAndSaveChangesSideBySide()
    {
        await OpenFileAsync(narrowWindow: true);

        _popover.WaitForAssertion(() => Assert.Single(_popover.FindAll(".tl-editor-window--phone")));
        var footer = _popover.Find(".tl-editor-window__footer");
        Assert.NotNull(footer.QuerySelector(".tl-editor-window__where"));
        var actions = footer.QuerySelectorAll(".tl-editor-window__actions > button");
        Assert.Equal(["Discard", "Save changes"], actions.Select(button => button.TextContent.Trim()).ToArray());
    }

    [Fact]
    public async Task OnADesktop_DeleteStaysInTheFooter_AndTheHeaderHasNoMoreMenu()
    {
        await OpenFileAsync(narrowWindow: false);

        Assert.Single(_popover.FindAll(".tl-editor-window__footer .tl-editor-window__delete"));
        Assert.Empty(_popover.FindAll(".tl-editor-window--phone, [data-testid=phone-header]"));
        Assert.Empty(_popover.FindAll(".tl-editor-header .app-overflow-menu__trigger, .tl-editor-header .app-row-menu__trigger"));
    }

    [Fact]
    public async Task WhenTheWindowIsNoLongerNarrow_ThePopoverGoesBackToTheDesktopArrangement()
    {
        await OpenFileAsync(narrowWindow: true);
        _popover.WaitForAssertion(() => Assert.Single(_popover.FindAll(".tl-editor-window--phone")));

        await _popover.InvokeAsync(() => _popover.Instance.NarrowWindowChanged(false));

        _popover.WaitForAssertion(() => Assert.Empty(_popover.FindAll(".tl-editor-window--phone")));
        Assert.Single(_popover.FindAll(".tl-editor-window__footer .tl-editor-window__delete"));
    }

    [Theory]
    [InlineData("phone", true)]
    [InlineData("tablet", true)]
    [InlineData("desktop", false)]
    [InlineData("tv", false)]
    public async Task ThePhoneArrangementFitsAPhoneAndATabletByFormFactor_AndNothingElse(string formFactor, bool expected)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, true));

        Assert.Equal(expected, EditorPhoneArrangement.Applies(_device));
        Assert.True(EditorPhoneArrangement.Applies(_device, narrowWindow: true));
        Assert.False(EditorPhoneArrangement.Applies(null));
    }
}
