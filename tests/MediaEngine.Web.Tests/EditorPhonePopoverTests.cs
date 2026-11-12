using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
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
/// The editor's window on a phone (universal editor v6 section 4a, D22): the phone arrangement is the device's form factor (a phone or a tablet; the
/// tests ask for it with the window's PhoneArrangement setting, because a phone may not open the editor yet, which is another packet's), the footer
/// then holds the write-back line and Discard and Save changes side by side, and Delete moves from the footer into the header's "..." menu with its
/// confirmation unchanged.
/// </summary>
public sealed class EditorPhonePopoverTests : AsyncBunitContext
{
    private static readonly Guid TitleId = SupportingFileEditorTestData.TitleId;
    private static readonly Guid FileId = SupportingFileEditorTestData.FileId;

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly DashboardSessionAccessor _session = new();
    private readonly BunitJSModuleInterop _focusModule;
    private readonly TaskCompletionSource _saveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IRenderedComponent<EditorPopover> _popover = default!;
    private IRenderedComponent<AppDialogHost> _dialogs = default!;

    public EditorPhonePopoverTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _focusModule = JSInterop.SetupModule("./js/editor-focus.js");
        _focusModule.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.RunEditSessionAsync), args => SaveAfterTheGateAsync((EditSessionRequest)args![0]!));
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

    private async Task<LibraryBatchEditOutcome<EditSessionResult>> SaveAfterTheGateAsync(EditSessionRequest request)
    {
        // A preview (Review changes) answers at once; the real save waits until the test lets it through.
        if (!request.PreviewOnly)
        {
            await _saveGate.Task;
        }

        return LibraryBatchEditOutcome<EditSessionResult>.Ok(new EditSessionResult
        {
            PreviewOnly = request.PreviewOnly,
            Valid = true,
            DataSaved = !request.PreviewOnly,
            Changes = request.Changes.Select(change => new EditSessionChangeResult { Id = change.Id, Kind = change.Kind, EntityId = change.EntityId, Status = EditSessionChangeStatuses.Ok }).ToList(),
        });
    }

    private async Task<EditorHost> OpenFileAsync(bool phone)
    {
        _popover = Render<EditorPopover>(parameters => parameters.Add(popover => popover.PhoneArrangement, phone));
        _dialogs = Render<AppDialogHost>();
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
        await OpenFileAsync(phone: true);

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
        await OpenFileAsync(phone: true);

        _popover.WaitForAssertion(() => Assert.Single(_popover.FindAll(".tl-editor-window--phone")));
        var footer = _popover.Find(".tl-editor-window__footer");
        Assert.NotNull(footer.QuerySelector(".tl-editor-window__where"));
        var actions = footer.QuerySelectorAll(".tl-editor-window__actions > button");
        Assert.Equal(["Discard", "Save changes"], actions.Select(button => button.TextContent.Trim()).ToArray());
    }

    [Fact]
    public async Task OnADesktop_DeleteStaysInTheFooter_AndTheHeaderHasNoMoreMenu()
    {
        await OpenFileAsync(phone: false);

        Assert.Single(_popover.FindAll(".tl-editor-window__footer .tl-editor-window__delete"));
        Assert.Empty(_popover.FindAll(".tl-editor-window--phone, [data-testid=phone-header]"));
        Assert.Empty(_popover.FindAll(".tl-editor-header .app-overflow-menu__trigger, .tl-editor-header .app-row-menu__trigger"));
    }

    [Fact]
    public async Task WhenTheDeviceCrossesToADesktopFormFactor_ThePopoverGoesBackToTheDesktopArrangement()
    {
        var host = await OpenFileAsync(phone: true);
        _popover.WaitForAssertion(() => Assert.Single(_popover.FindAll(".tl-editor-window--phone")));

        _popover.Render(parameters => parameters.Add(popover => popover.PhoneArrangement, false));

        _popover.WaitForAssertion(() => Assert.Empty(_popover.FindAll(".tl-editor-window--phone")));
        Assert.Single(_popover.FindAll(".tl-editor-window__footer .tl-editor-window__delete"));
        Assert.True(host.IsOpen);
    }

    [Fact]
    public async Task OnAPhone_TheKeyboardFitIsStartedWhileTheWindowIsOpen_AndStoppedWhenTheArrangementIsLeft()
    {
        await OpenFileAsync(phone: true);
        _popover.WaitForAssertion(() => _focusModule.VerifyInvoke("watchKeyboard"));

        _popover.Render(parameters => parameters.Add(popover => popover.PhoneArrangement, false));

        _popover.WaitForAssertion(() => _focusModule.VerifyInvoke("stopKeyboard"));
    }

    [Fact]
    public async Task OnADesktop_NoKeyboardScriptIsStarted()
    {
        await OpenFileAsync(phone: false);

        Assert.DoesNotContain(_focusModule.Invocations, call => call.Identifier == "watchKeyboard");
    }

    [Fact]
    public async Task OnAPhone_ChoosingDeleteInTheHeadersMenu_AsksTheSameQuestionAsTheDesktopButtonDoes()
    {
        var host = await OpenFileAsync(phone: true);
        _popover.WaitForAssertion(() => Assert.Single(_popover.FindAll(".tl-editor-window--phone")));
        _popover.Find("[data-testid=phone-header] .app-overflow-menu__trigger, [data-testid=phone-header] .app-row-menu__trigger").Click();

        _popover.WaitForElement(".app-menu-item").Click();

        _dialogs.WaitForAssertion(() => Assert.Contains("Delete", _dialogs.FindAll("footer button").Last().TextContent, StringComparison.Ordinal));
        // Keeping the file ends the question: nothing was deleted and the window is still open.
        _dialogs.FindAll("footer button").First().Click();
        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll("footer button")));
        Assert.True(host.IsOpen);
    }

    [Fact]
    public async Task OnAPhone_DeleteInTheHeadersMenuIsDisabledWhileASaveIsRunning()
    {
        var host = await OpenFileAsync(phone: true);
        _popover.WaitForAssertion(() => Assert.Single(_popover.FindAll(".tl-editor-window--phone")));
        host.Session.Pending.SetField(TitleId, "title", "A new title");
        var saving = _popover.InvokeAsync(() => host.Flow.SaveAsync());
        _popover.WaitForAssertion(() => Assert.True(host.Flow.Saving));

        _popover.Find("[data-testid=phone-header] .app-overflow-menu__trigger, [data-testid=phone-header] .app-row-menu__trigger").Click();

        var item = _popover.WaitForElement(".app-menu-item");
        Assert.True(item.HasAttribute("disabled") || item.GetAttribute("aria-disabled") == "true");
        _saveGate.SetResult();
        await saving;
    }

    [Fact]
    public async Task WhenTheArrangementCrosses_TheStagedChangesAndTheOpenFileAreKept()
    {
        var host = await OpenFileAsync(phone: false);
        host.Session.Pending.SetField(TitleId, "title", "A new title");
        _popover.WaitForAssertion(() => Assert.True(host.Session.HasPendingChanges));

        _popover.Render(parameters => parameters.Add(popover => popover.PhoneArrangement, true));

        _popover.WaitForAssertion(() => Assert.Single(_popover.FindAll(".tl-editor-window--phone")));
        Assert.Single(_popover.FindAll("[data-supporting-file]"));
        Assert.Equal(1, host.Session.Pending.ChangeCount);
        Assert.True(host.IsOpen);
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
        Assert.Equal(expected, EditorWindowLayout.For(_device).Phone);
        Assert.False(EditorPhoneArrangement.Applies(null));
        Assert.False(EditorWindowLayout.For(null).Phone);
    }
}
