using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The photos, videos and documents editor on a phone (universal editor v6 section 4a, D21, D22): the same frame and phone arrangement as the main
/// editor, with Quick Facts a closed section under the form, Delete in the header's "..." (with the same confirmation) and a field's lock that is
/// tapped for the reason it was locked and Revert. A field is unlocked, changed, reviewed and saved by touch alone. A computer is unchanged.
/// </summary>
public sealed class ViewEditorPhoneTests : AsyncBunitContext
{
    private const string HeaderMenu = ".tl-editor-header .app-overflow-menu__trigger, .tl-editor-header .app-row-menu__trigger";

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<ViewItemsEditSetRequest> _edits = [];
    private readonly IRenderedComponent<AppDialogHost> _dialogs;

    public ViewEditorPhoneTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.EditViewItemsAsync), args =>
            {
                var request = (ViewItemsEditSetRequest)args![0]!;
                _edits.Add(request);
                return Task.FromResult(new ViewCatalogueResult<ViewItemsEditResultDto>(new ViewItemsEditResultDto(!request.PreviewOnly, true, 1, [])));
            });
        }));
        Services.AddSingleton(new DashboardSessionAccessor());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddNativeUiServices();
        Render<AppPopoverHost>();
        _dialogs = Render<AppDialogHost>();
    }

    private static LocalAssetDto Photo() => JsonSerializer.Deserialize<LocalAssetDto>($$"""
        {"id":"{{Guid.NewGuid()}}","library_id":"22222222-2222-2222-2222-222222222222","media_kind":"image",
         "title":null,"file_name":"IMG_1.jpg","mime_type":"image/jpeg","created_at":"2026-03-02T10:00:00+00:00","captured_at":"2026-03-01T09:00:00+00:00",
         "source_count":1,"files":[{"id":"33333333-3333-3333-3333-333333333333","role":"original","mime_type":"image/jpeg","byte_size":8100000,"source_count":1}],
         "tags":["Trip"],"thumbnail_url":"/t","content_url":"/c","favorite":false,"hidden":false,"description":"Beach","width":6000,"height":4000}
        """)!;

    private async Task<(IRenderedComponent<ViewEditorPopover> Popover, ViewEditorHost Host)> OpenAsync(bool phone)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var host = Services.GetRequiredService<ViewEditorHost>();
        Assert.True(await host.OpenItemsAsync([Photo()], null));
        return (Render<ViewEditorPopover>(parameters => parameters.Add(popover => popover.PhoneArrangement, phone)), host);
    }

    private static AngleSharp.Dom.IElement FieldOf(IRenderedComponent<ViewEditorPopover> popover, string label) =>
        popover.FindAll(".tl-editor-field").Single(field => field.QuerySelector(".tl-editor-field__label > span")?.TextContent.Trim() == label);

    [Fact]
    public async Task OnAPhone_TheViewEditorUsesThePhoneFrame_WithQuickFactsClosedAndDeleteInTheHeaderMenu()
    {
        var (popover, _) = await OpenAsync(phone: true);

        popover.WaitForAssertion(() => Assert.Single(popover.FindAll(".tl-editor-window--phone")));
        var body = popover.Find(".view-edit-body");
        Assert.Contains("view-edit-body--phone", body.ClassList);
        Assert.Contains("tl-editor-touch", body.ClassList);
        Assert.Equal("true", body.GetAttribute("data-phone"));
        var toggle = popover.Find("[data-quick-facts-toggle]");
        Assert.Equal("false", toggle.GetAttribute("aria-expanded"));
        Assert.Empty(popover.FindAll(".tl-quick-facts__row"));
        toggle.Click();
        popover.WaitForAssertion(() => Assert.NotEmpty(popover.FindAll(".tl-quick-facts__row")));
        Assert.Empty(popover.FindAll(".tl-editor-window__footer .tl-editor-window__delete"));
    }

    [Fact]
    public async Task OnADesktop_TheViewEditorIsUnchanged()
    {
        var (popover, _) = await OpenAsync(phone: false);

        popover.WaitForAssertion(() => Assert.NotEmpty(popover.FindAll(".view-edit-body")));
        Assert.Empty(popover.FindAll(".tl-editor-window--phone"));
        Assert.DoesNotContain("view-edit-body--phone", popover.Find(".view-edit-body").ClassList);
        Assert.Empty(popover.FindAll("[data-quick-facts-toggle]"));
        Assert.NotEmpty(popover.FindAll(".tl-editor-window__footer-start .tl-editor-window__delete"));
        Assert.Empty(popover.FindAll(HeaderMenu));
    }

    [Fact]
    public async Task OnAPhone_TheHeadersMoreMenuHoldsDelete_AndItOpensTheSameConfirmation()
    {
        var (popover, host) = await OpenAsync(phone: true);
        popover.WaitForAssertion(() => Assert.Single(popover.FindAll(".tl-editor-window--phone")));

        popover.Find(HeaderMenu).Click();
        var item = popover.WaitForElement(".app-menu-item");
        Assert.Equal("Delete", item.TextContent.Trim());
        item.Click();

        // The same question as the footer button asks; keeping the photo ends it with nothing removed and the window still open.
        _dialogs.WaitForAssertion(() => Assert.Contains("Delete", _dialogs.FindAll("footer button").Last().TextContent, StringComparison.Ordinal));
        _dialogs.FindAll("footer button").First().Click();
        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll("footer button")));
        Assert.True(host.IsOpen);
    }

    [Fact]
    public async Task OnAPhone_TheHeadersMoreMenuHasNoDelete_WhenTheItemCannotBeDeleted()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var host = Services.GetRequiredService<ViewEditorHost>();
        // A gallery that is only being made has nothing to delete yet.
        Assert.True(await host.OpenGalleryAsync(null, newSmart: false));
        var popover = Render<ViewEditorPopover>(parameters => parameters.Add(window => window.PhoneArrangement, true));

        popover.WaitForAssertion(() => Assert.Single(popover.FindAll(".tl-editor-window--phone")));
        Assert.Empty(popover.FindAll(HeaderMenu));
        Assert.Empty(popover.FindAll(".tl-editor-window__footer .tl-editor-window__delete"));
    }

    [Fact]
    public async Task OnAPhone_AFieldIsUnlockedChangedReviewedAndSavedByTouchAlone()
    {
        var (popover, host) = await OpenAsync(phone: true);
        popover.WaitForAssertion(() => Assert.Single(popover.FindAll(".tl-editor-window--phone")));

        // Tap the lock; the value is the file's and stays so until it is changed.
        FieldOf(popover, "Date taken").QuerySelector("button.tl-editor-field__lock-toggle")!.Click();
        popover.WaitForAssertion(() => Assert.NotNull(FieldOf(popover, "Date taken").QuerySelector("button.tl-editor-field__lock-reveal")));
        // Tap it again: where the value came from, and Revert, which locks the field again.
        FieldOf(popover, "Date taken").QuerySelector("button.tl-editor-field__lock-reveal")!.Click();
        popover.WaitForAssertion(() => Assert.Contains("the file", popover.Find("[data-testid='tap-reveal-text']").TextContent, StringComparison.Ordinal));
        popover.Find("[data-tap-reveal-action]").Click();
        popover.WaitForAssertion(() => Assert.NotNull(FieldOf(popover, "Date taken").QuerySelector("button.tl-editor-field__lock-toggle:not(.tl-editor-field__lock-reveal)")));
        Assert.False(host.Session!.HasPendingChanges);

        // Unlock, change, Save changes, Review, Save.
        FieldOf(popover, "Date taken").QuerySelector("button.tl-editor-field__lock-toggle")!.Click();
        popover.WaitForAssertion(() => Assert.False(popover.Find("input[aria-label='Date taken']").HasAttribute("readonly")));
        popover.Find("input[aria-label='Date taken']").Input("2026-03-02T09:30");
        popover.WaitForAssertion(() => Assert.True(host.Session!.HasPendingChanges));
        Assert.DoesNotContain(_edits, edit => !edit.PreviewOnly);

        popover.Find("button.tl-editor-window__save").Click();
        popover.WaitForElement(".review-changes--phone");
        popover.WaitForAssertion(() => Assert.False(popover.Find(".review-changes__footer button:last-child").HasAttribute("disabled")));
        popover.Find(".review-changes__footer button:last-child").Click();

        popover.WaitForAssertion(() => Assert.Single(_edits, edit => !edit.PreviewOnly));
    }

    [Fact]
    public async Task OnAPhone_ARevertThatIsStagedIsAButtonThatSaysSo_NotAnArrowNamedByATooltip()
    {
        var (popover, host) = await OpenAsync(phone: true);
        popover.WaitForAssertion(() => Assert.Single(popover.FindAll(".tl-editor-window--phone")));

        popover.Find("textarea").Input("A new description");

        popover.WaitForAssertion(() => Assert.True(host.Session!.HasPendingChanges));
        var revert = popover.WaitForElement("button.app-button--text[aria-label$='Description']");
        Assert.Contains("Revert", revert.TextContent, StringComparison.Ordinal);
    }
}
