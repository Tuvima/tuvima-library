using System.Reflection;
using Bunit;
using MediaEngine.Contracts.Collections;
using MediaEngine.Web.Components.Collections;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The collection and playlist builder on a phone (universal editor v6 section 4a, brought to the builder): a phone or a tablet form factor draws it as a
/// full-screen sheet with a one-line header, the tabs in one row, Delete collection in the header's "..." menu and Cancel and Save side by side; the
/// create flow shows its step on the header's second line. The desktop arrangement is the rail, the rail's Delete and no "..." menu, as before.
/// </summary>
public sealed class CollectionEditorPhoneTests : AsyncBunitContext
{
    private static readonly Guid CollectionId = Guid.Parse("00000000-0000-0000-0000-0000000000c2");

    private readonly List<Guid> _deleted = [];
    private readonly List<CollectionGalleryReferenceDto> _galleries = [];
    private readonly DeviceContextService _device;
    private readonly BunitJSModuleInterop _focusModule;

    public CollectionEditorPhoneTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _focusModule = JSInterop.SetupModule("./js/editor-focus.js");
        _focusModule.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddSingleton<ICollectionPersonalMediaClient>(new EmptyPersonalMediaClient(_galleries));
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.DeleteCollectionAsync), args =>
            {
                _deleted.Add((Guid)args![0]!);
                return Task.FromResult(true);
            })));
        _device = Services.GetRequiredService<DeviceContextService>();
    }

    private static CollectionEditorLaunchRequest ExistingCollection(string collectionType = "Custom", string membershipMode = "Manual") => new()
    {
        EditingCollection = new CollectionListItemViewModel
        {
            Id = CollectionId,
            Name = "Weekend picks",
            CollectionType = collectionType,
            MembershipMode = membershipMode,
            OwnerKind = "Library",
            Audience = "Everyone",
        },
    };

    private IRenderedComponent<CollectionEditorShell> RenderEditor(CollectionEditorLaunchRequest request, bool phone) =>
        Render<CollectionEditorShell>(parameters => parameters
            .Add(shell => shell.PhoneArrangement, phone)
            .Add(shell => shell.Request, request));

    private static IReadOnlyList<string> ButtonTexts(AngleSharp.Dom.IElement parent) =>
        parent.QuerySelectorAll("button").Select(button => button.TextContent.Trim()).ToList();

    private static FieldInfo UnsavedField(IRenderedComponent<CollectionEditorShell> editor) =>
        editor.Instance.GetType().GetField("_hasUnsavedChanges", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void OnAPhone_TheBuilderIsOneSheetWithoutTheFramedWindowsHostClass()
    {
        var editor = RenderEditor(ExistingCollection(), phone: true);

        var workspace = editor.Find(".collection-editor-workspace");
        Assert.Contains("collection-editor-workspace--phone", workspace.ClassList);
        // The editor's own phone class, so its keyboard script and safe-area rules find this sheet too.
        Assert.Contains("tl-editor-window--phone", workspace.ClassList);
        Assert.Single(editor.FindAll(".app-dialog-host.sme-dialog--phone"));
        Assert.Empty(editor.FindAll(".sme-dialog"));
    }

    [Fact]
    public void OnAPhone_TheHeaderIsOneLineWithTheTitleAndClose()
    {
        var editor = RenderEditor(ExistingCollection(), phone: true);

        var header = editor.Find("[data-testid=phone-header]");
        Assert.Equal("Weekend picks", header.QuerySelector(".sme-title")!.TextContent.Trim());
        Assert.Null(header.QuerySelector(".sme-kicker, .sme-subtitle, .collection-editor-header-icon"));
        var close = header.QuerySelector("button.sme-close");
        Assert.NotNull(close);
        Assert.Equal("Close editor", close.GetAttribute("aria-label"));
    }

    [Fact]
    public void OnAPhone_TheTabsAreTheSharedRowAndTheRailAndItsDeleteAreGone()
    {
        var editor = RenderEditor(ExistingCollection(), phone: true);

        Assert.Empty(editor.FindAll(".collection-editor-rail, .collection-editor-rail-footer, .collection-editor-delete-button"));
        var tabs = editor.FindAll(".collection-editor-tabs .tl-tabs-toolbar [role=tab]");
        Assert.Equal(["Details", "Artwork", "Contents", "Companions", "History"], tabs.Select(tab => tab.TextContent.Trim()).ToArray());
        Assert.Equal("true", tabs[0].GetAttribute("aria-selected"));

        tabs[2].Click();

        editor.WaitForAssertion(() => Assert.Equal("true", editor.FindAll(".collection-editor-tabs [role=tab]")[2].GetAttribute("aria-selected")));
        Assert.Contains("How it fills up", editor.Find(".collection-editor-body").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void OnAPhone_DeleteCollectionIsInTheHeadersMoreMenuAndNotInTheFooter()
    {
        var editor = RenderEditor(ExistingCollection(), phone: true);

        Assert.DoesNotContain("Delete collection", ButtonTexts(editor.Find(".collection-editor-footer")));
        editor.Find("[data-testid=phone-header] .app-row-menu__trigger").Click();

        var item = editor.WaitForElement(".app-menu-item");
        Assert.Equal("Delete collection", item.TextContent.Trim());
    }

    [Fact]
    public void OnAPhone_ACollectionThatCannotBeDeletedHasNoMoreMenu()
    {
        var editor = RenderEditor(ExistingCollection("Series"), phone: true);

        Assert.Empty(editor.FindAll(".app-row-menu__trigger, .app-overflow-menu__trigger"));
        Assert.Single(editor.FindAll("[data-testid=phone-header] button.sme-close"));
    }

    [Fact]
    public async Task OnAPhone_DeleteCollectionAsksFirstAndThenDeletes()
    {
        var host = Render<AppDialogHost>();
        var editor = RenderEditor(ExistingCollection(), phone: true);
        editor.Find("[data-testid=phone-header] .app-row-menu__trigger").Click();
        editor.WaitForElement(".app-menu-item").Click();

        host.WaitForElement(".app-confirm");
        Assert.Equal("Delete Weekend picks?", host.Find(".app-dialog-shell__title").TextContent.Trim());
        Assert.Equal("The titles in it stay in your library.", host.Find(".app-confirm__message").TextContent.Trim());
        Assert.Empty(_deleted);

        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Delete").Click();

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (_deleted.Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The collection was not deleted.");
            await Task.Delay(10);
        }

        Assert.Equal([CollectionId], _deleted);
    }

    [Fact]
    public void OnAPhone_TheFooterHoldsCancelAndSaveAsTheDesktopOnesDo()
    {
        var phone = RenderEditor(ExistingCollection(), phone: true);
        var desktop = RenderEditor(ExistingCollection(), phone: false);

        var phoneButtons = ButtonTexts(phone.Find(".collection-editor-footer-actions"));
        Assert.Equal(["Cancel", "Save"], phoneButtons);
        Assert.Equal(phoneButtons, ButtonTexts(desktop.Find(".collection-editor-footer-actions")));
    }

    [Fact]
    public void OnAPhone_LeavingWithUnsavedChangesStillAsksFirst()
    {
        var host = Render<AppDialogHost>();
        var editor = RenderEditor(ExistingCollection(), phone: true);
        UnsavedField(editor).SetValue(editor.Instance, true);

        editor.Find("[data-testid=phone-header] button.sme-close").Click();

        host.WaitForElement(".app-confirm");
        Assert.Equal("Discard your changes?", host.Find(".app-dialog-shell__title").TextContent.Trim());
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Keep editing").Click();
        host.WaitForAssertion(() => Assert.Empty(host.FindAll(".app-confirm")));
        Assert.Equal(true, UnsavedField(editor).GetValue(editor.Instance));
    }

    [Fact]
    public void OnAPhone_TheCreateFlowShowsItsStepOnTheHeaderAndKeepsItsFooter()
    {
        var editor = RenderEditor(new CollectionEditorLaunchRequest(), phone: true);

        var header = editor.Find("[data-testid=phone-header]");
        Assert.Equal("New collection", header.QuerySelector(".sme-title")!.TextContent.Trim());
        Assert.Equal("Step 1 of 3, How it fills up", editor.Find("[data-testid=phone-step]").TextContent.Trim());
        Assert.Empty(editor.FindAll(".collection-flow__steps"));
        Assert.Equal(["Cancel", "Next"], ButtonTexts(editor.Find("[data-flow-footer] .collection-editor-footer-actions")));
        Assert.Empty(editor.FindAll(".app-row-menu__trigger"));
    }

    [Fact]
    public void WhenTheDeviceTurns_WhatWasStagedInTheCompanionsAndOtherFieldsIsKept()
    {
        _galleries.Add(new CollectionGalleryReferenceDto(Guid.NewGuid(), "Family Trip", "manual", DateTimeOffset.UtcNow));
        _galleries.Add(new CollectionGalleryReferenceDto(Guid.NewGuid(), "Recent Favorites", "manual", DateTimeOffset.UtcNow));
        var editor = RenderEditor(ExistingCollection(), phone: true);
        editor.WaitForAssertion(() => Assert.Equal(2, editor.FindAll(".collection-personal-media__gallery").Count));
        var tabs = editor.FindAll(".collection-editor-tabs [role=tab]");
        tabs.Single(tab => tab.TextContent.Trim() == "Companions").Click();
        editor.FindAll(".collection-personal-media__gallery")[1].Click();
        editor.FindAll(".collection-personal-media__gallery")[0].Click();
        Assert.Equal(2, editor.FindAll(".collection-personal-media__gallery.is-selected").Count);
        Assert.Equal(true, UnsavedField(editor).GetValue(editor.Instance));

        // A tablet turned: the arrangement changes to the desktop one and back, and the ticks (a reloaded list would have none) are still there.
        editor.Render(parameters => parameters.Add(shell => shell.PhoneArrangement, false));
        Assert.Empty(editor.FindAll(".collection-editor-tabs"));
        Assert.Single(editor.FindAll(".collection-editor-rail"));
        Assert.Equal(2, editor.FindAll(".collection-personal-media__gallery.is-selected").Count);

        editor.Render(parameters => parameters.Add(shell => shell.PhoneArrangement, true));
        Assert.Single(editor.FindAll(".collection-editor-tabs"));
        Assert.Equal(2, editor.FindAll(".collection-personal-media__gallery.is-selected").Count);
        Assert.Equal(true, UnsavedField(editor).GetValue(editor.Instance));
    }

    [Fact]
    public void OnAPhone_TheBodyIsTheOneTabPanelAndTheTabsNameIt()
    {
        var editor = RenderEditor(ExistingCollection(), phone: true);

        var body = editor.Find(".collection-editor-body");
        Assert.Equal("tabpanel", body.GetAttribute("role"));
        var tabs = editor.FindAll(".collection-editor-tabs [role=tab]");
        Assert.All(tabs, tab => Assert.Equal(body.Id, tab.GetAttribute("aria-controls")));
        Assert.Equal(tabs[0].Id, body.GetAttribute("aria-labelledby"));
        Assert.Empty(editor.FindAll(".collection-editor-tabs [role=tabpanel], .collection-editor-tabs .tl-tab-panel"));

        var desktop = RenderEditor(ExistingCollection(), phone: false);
        Assert.Null(desktop.Find(".collection-editor-body").GetAttribute("role"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheRuleBuilderDrawsMoveButtonsForItsGroupsOnlyOnAPhone(bool phone)
    {
        var editor = RenderEditor(ExistingCollection(membershipMode: "Smart"), phone);
        editor.FindAll("[role=tab], .collection-editor-rail button").Single(tab => tab.TextContent.Trim() == "Contents").Click();

        editor.WaitForAssertion(() => Assert.Single(editor.FindComponents<CollectionRuleBuilder>()));
        Assert.Equal(phone, editor.FindComponent<CollectionRuleBuilder>().Instance.MoveButtons);
    }

    [Fact]
    public void OnADesktop_TheRailAndItsDeleteStayAndThereIsNoMoreMenu()
    {
        var editor = RenderEditor(ExistingCollection(), phone: false);

        Assert.Single(editor.FindAll(".collection-editor-rail"));
        Assert.Contains("Delete collection", ButtonTexts(editor.Find(".collection-editor-rail-footer")));
        Assert.Empty(editor.FindAll("[data-testid=phone-header], .collection-editor-tabs, .app-row-menu__trigger, .tl-editor-window--phone"));
        Assert.Single(editor.FindAll(".app-dialog-host.sme-dialog"));
        Assert.Empty(editor.FindAll(".sme-dialog--phone"));
        Assert.Single(editor.FindAll(".collection-editor-header .sme-kicker"));
    }

    [Fact]
    public async Task OnAPhoneDevice_TheDialogIsFullScreenAndGoesBackWhenTheDeviceBecomesADesktop()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));
        var host = Render<AppDialogHost>();
        var service = Services.GetRequiredService<AppDialogService>();

        await host.InvokeAsync(() => service.ShowAsync<CollectionEditorShell>("Collection", new AppDialogParameters<CollectionEditorShell> { { editor => editor.Request, ExistingCollection() } }));

        host.WaitForAssertion(() => Assert.Single(host.FindAll("dialog.app-native-dialog--fullscreen")));
        Assert.Single(host.FindAll("dialog.sme-dialog--phone"));
        Assert.Single(host.FindAll(".collection-editor-workspace--phone"));

        await host.InvokeAsync(() => _device.SwitchDeviceAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true)));

        host.WaitForAssertion(() => Assert.Empty(host.FindAll("dialog.app-native-dialog--fullscreen")));
        Assert.Empty(host.FindAll(".collection-editor-workspace--phone"));
        Assert.Single(host.FindAll("dialog.sme-dialog"));
    }

    [Theory]
    [InlineData("phone", true)]
    [InlineData("tablet", true)]
    [InlineData("desktop", null)]
    public async Task TheLauncherAsksForAFullScreenSheetOnAPhoneOrTabletOnly(string formFactor, bool? fullScreen)
    {
        var desktop = formFactor == "desktop";
        await _device.InitialiseAsync(new DeviceContextSnapshot(desktop ? "web" : "mobile", formFactor, !desktop, FinePointer: desktop, Hover: desktop));
        var dialogs = new SpyDialogService();
        var launcher = new CollectionEditorLauncherService(dialogs, device: _device);

        await launcher.TryOpenAsync(new CollectionEditorLaunchRequest());

        Assert.Equal(fullScreen, dialogs.LastOptions!.FullScreen);
    }

    private sealed class SpyDialogService : IAppDialogService
    {
        public AppDialogOptions? LastOptions { get; private set; }

        public Task<IAppDialogReference> ShowAsync<TComponent>(string? title = null, AppDialogParameters? parameters = null, AppDialogOptions? options = null) where TComponent : IComponent
        {
            LastOptions = options;
            return Task.FromResult<IAppDialogReference>(null!);
        }

        public Task<IAppDialogReference> ShowAsync<TComponent>(string? title, AppDialogOptions options) where TComponent : IComponent => ShowAsync<TComponent>(title, null, options);

        public Task<bool?> ShowMessageBoxAsync(string? title, string message, string yesText = "OK", string? noText = null, string? cancelText = null, AppDialogOptions? options = null) => Task.FromResult<bool?>(null);

        public Task<bool?> ShowMessageBoxAsync(string? title, MarkupString message, string yesText = "OK", string? noText = null, string? cancelText = null, AppDialogOptions? options = null) => Task.FromResult<bool?>(null);
    }

    private sealed class EmptyPersonalMediaClient(List<CollectionGalleryReferenceDto> galleries) : ICollectionPersonalMediaClient
    {
        public string? LastError => null;
        public Task<IReadOnlyList<CollectionGalleryReferenceDto>> GetEligibleGalleriesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CollectionGalleryReferenceDto>>(galleries.ToList());
        public Task<IReadOnlyList<CollectionPersonalMediaSourceDto>> GetSourcesAsync(Guid collectionId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CollectionPersonalMediaSourceDto>>([]);
        public Task<CollectionPersonalMediaSourceDto?> AddSourceAsync(Guid collectionId, CollectionPersonalMediaSourceWriteRequest request, CancellationToken ct = default) => Task.FromResult<CollectionPersonalMediaSourceDto?>(null);
        public Task<CollectionPersonalMediaSourceDto?> UpdateSourceAsync(Guid collectionId, Guid sourceId, CollectionPersonalMediaSourceWriteRequest request, CancellationToken ct = default) => Task.FromResult<CollectionPersonalMediaSourceDto?>(null);
        public Task<bool> RemoveSourceAsync(Guid collectionId, Guid sourceId, CancellationToken ct = default) => Task.FromResult(false);
    }
}
