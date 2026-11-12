using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The photos, videos and documents editor on a phone (universal editor v6 section 4a, D21, D22): the same frame and phone arrangement as the main
/// editor, with Quick Facts a closed section under the form and Delete in the header's "...". A computer is unchanged.
/// </summary>
public sealed class ViewEditorPhoneTests : AsyncBunitContext
{
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());

    public ViewEditorPhoneTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(EngineApiClientStub.CreateDefault());
        Services.AddNativeUiServices();
        Render<AppPopoverHost>();
    }

    private static LocalAssetDto Photo() => JsonSerializer.Deserialize<LocalAssetDto>($$"""
        {"id":"{{Guid.NewGuid()}}","library_id":"22222222-2222-2222-2222-222222222222","media_kind":"image",
         "title":null,"file_name":"IMG_1.jpg","mime_type":"image/jpeg","created_at":"2026-03-02T10:00:00+00:00","captured_at":"2026-03-01T09:00:00+00:00",
         "source_count":1,"files":[{"id":"33333333-3333-3333-3333-333333333333","role":"original","mime_type":"image/jpeg","byte_size":8100000,"source_count":1}],
         "tags":["Trip"],"thumbnail_url":"/t","content_url":"/c","favorite":false,"hidden":false,"description":"Beach","width":6000,"height":4000}
        """)!;

    private async Task<IRenderedComponent<ViewEditorPopover>> OpenAsync(bool narrow)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var host = Services.GetRequiredService<ViewEditorHost>();
        Assert.True(await host.OpenItemsAsync([Photo()], null));
        var popover = Render<ViewEditorPopover>();
        await popover.InvokeAsync(() => popover.Instance.NarrowWindowChanged(narrow));
        return popover;
    }

    [Fact]
    public async Task OnAPhone_TheViewEditorUsesThePhoneFrame_WithQuickFactsClosedAndDeleteInTheHeaderMenu()
    {
        var popover = await OpenAsync(narrow: true);

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
        var popover = await OpenAsync(narrow: false);

        popover.WaitForAssertion(() => Assert.NotEmpty(popover.FindAll(".view-edit-body")));
        Assert.Empty(popover.FindAll(".tl-editor-window--phone"));
        Assert.DoesNotContain("view-edit-body--phone", popover.Find(".view-edit-body").ClassList);
        Assert.Empty(popover.FindAll("[data-quick-facts-toggle]"));
        Assert.NotEmpty(popover.FindAll(".tl-editor-window__footer-start .tl-editor-window__delete"));
    }
}
