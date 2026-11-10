using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The photos, videos, documents and galleries editor on the v5 frame (universal editor v5 D25, WP14, acceptance A12): a photo, a document and a
/// gallery open with no Files tab, a photo opened from a gallery lists the gallery's pictures in the item dropdown (eight inline, then All photos) and
/// picking one switches the item, file facts sit in Quick facts, and a phone or tablet never edits. The Engine is a hand-written stub.
/// </summary>
public sealed class ViewEditorV5FrameTests : AsyncBunitContext
{
    private static readonly Guid GalleryId = Guid.Parse("14000000-0000-0000-0000-000000000001");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<LocalAssetDto> _gallery = [.. Enumerable.Range(1, 12).Select(index => Photo($"IMG_{index:00}.jpg"))];

    public ViewEditorV5FrameTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetViewAssetsAsync), _ => Task.FromResult<ViewAssetTimelinePageDto?>(new ViewAssetTimelinePageDto(_gallery, null, false)));
            stub.SetHandler(nameof(IEngineApiClient.EditViewItemsAsync), args =>
            {
                var request = (ViewItemsEditSetRequest)args![0]!;
                return Task.FromResult(new ViewCatalogueResult<ViewItemsEditResultDto>(new ViewItemsEditResultDto(!request.PreviewOnly, true, 1, [])));
            });
        }));
        Services.AddNativeUiServices();
        Render<AppPopoverHost>();
    }

    private static LocalAssetDto Photo(string name = "IMG_1.jpg", string kind = "image", string mime = "image/jpeg") => JsonSerializer.Deserialize<LocalAssetDto>($$"""
        {"id":"{{Guid.NewGuid()}}","library_id":"22222222-2222-2222-2222-222222222222","media_kind":"{{kind}}",
         "title":null,"file_name":"{{name}}","mime_type":"{{mime}}","created_at":"2026-03-02T10:00:00+00:00","captured_at":"2026-03-01T09:00:00+00:00",
         "source_count":1,"files":[{"id":"33333333-3333-3333-3333-333333333333","role":"original","mime_type":"{{mime}}","byte_size":8100000,"source_count":1},
         {"id":"44444444-4444-4444-4444-444444444444","role":"raw","mime_type":"image/x-raw","byte_size":9100000,"source_count":1}],
         "tags":["Trip"],"thumbnail_url":"/t","content_url":"/c","favorite":false,"hidden":false,"description":"Beach","width":6000,"height":4000}
        """)!;

    private async Task<ViewEditorHost> OpenAsync(IReadOnlyList<LocalAssetDto> items, ViewEditGalleryRail? rail = null)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var host = Services.GetRequiredService<ViewEditorHost>();
        Assert.True(await host.OpenItemsAsync(items, rail));
        return host;
    }

    [Fact]
    public async Task APhotoHasDetailsAndHistoryAndNoFilesTab()
    {
        var host = await OpenAsync([_gallery[0]]);

        Assert.Equal([ViewEditTool.Details, ViewEditTool.History], host.Session!.Tools);
    }

    [Fact]
    public async Task ADocumentHasDetailsAndHistoryAndNoFilesTab()
    {
        var host = await OpenAsync([Photo("bill.pdf", "document", "application/pdf")]);

        Assert.Equal([ViewEditTool.Details, ViewEditTool.History], host.Session!.Tools);
    }

    [Fact]
    public async Task AGalleryHasDetailsArtworkAndHistoryAndNoContentsTab()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var host = Services.GetRequiredService<ViewEditorHost>();
        Assert.True(await host.OpenGalleryAsync(null, newSmart: false));

        Assert.Equal([ViewEditTool.Details, ViewEditTool.Artwork, ViewEditTool.History], host.Session!.Tools);
    }

    [Fact]
    public async Task APhotoOutsideAGalleryHasNoFamilyControls()
    {
        var host = await OpenAsync([_gallery[0]]);

        var header = Render<ViewEditHeader>(p => p.Add(x => x.Session, host.Session!));

        Assert.Empty(header.FindAll(".view-hnav"));
        Assert.Empty(header.FindAll("[data-level='Season']"));
    }

    [Fact]
    public async Task APhotoFromAGalleryShowsTheGalleryAsTheLabelAndAHomeButtonAndAnItemDropdown()
    {
        var host = await OpenAsync([_gallery[2]], new ViewEditGalleryRail(GalleryId, "Lisbon 2026", _gallery));

        var header = Render<ViewEditHeader>(p => p.Add(x => x.Session, host.Session!));

        Assert.Equal("Lisbon 2026", header.Find(".tl-editor-header__label").TextContent.Trim());
        Assert.NotEmpty(header.FindAll(".view-hnav__home"));
        Assert.Contains("3 of 12", header.Find("#view-hnav-item").TextContent);
        Assert.Empty(header.FindAll("[data-level='Season']"));
    }

    [Fact]
    public async Task TheItemDropdownListsEightPicturesInlineThenAllPhotosAndPickingOneSwitchesTheItem()
    {
        var host = await OpenAsync([_gallery[0]], new ViewEditGalleryRail(GalleryId, "Lisbon 2026", _gallery));
        var header = Render<ViewEditHeader>(p => p.Add(x => x.Session, host.Session!));

        await header.Find("#view-hnav-item").ClickAsync();

        var popover = header.FindComponent<AppPopover>();
        Assert.Equal(ViewEditorLimits.Shipped.InlineItems, popover.FindAll("[role='option']").Count);
        Assert.NotEmpty(popover.FindAll("#view-hnav-all"));

        await popover.FindAll("[role='option']")[3].ClickAsync();

        Assert.Equal(_gallery[3].Id, host.Session!.Active.Items[0].Id);
    }

    [Fact]
    public async Task AllPhotosOpensTheWholeGalleryAsASearchableList()
    {
        var host = await OpenAsync([_gallery[0]], new ViewEditGalleryRail(GalleryId, "Lisbon 2026", _gallery));
        var header = Render<ViewEditHeader>(p => p.Add(x => x.Session, host.Session!));
        await header.Find("#view-hnav-item").ClickAsync();

        await header.FindComponent<AppPopover>().Find("#view-hnav-all").ClickAsync();

        var popover = header.FindComponent<AppPopover>();
        Assert.Equal(_gallery.Count, popover.FindAll("[role='option']").Count);
        Assert.NotEmpty(popover.FindAll("input[type='search'], input"));
    }

    [Fact]
    public async Task SeveralPhotosFromAGalleryShowTheCountAndABackToTheGalleryButton()
    {
        var host = await OpenAsync([_gallery[0], _gallery[1], _gallery[2]], new ViewEditGalleryRail(GalleryId, "Lisbon 2026", _gallery));

        var header = Render<ViewEditHeader>(p => p.Add(x => x.Session, host.Session!));

        Assert.Contains("3", header.Find(".tl-editor-header__title").TextContent);
        Assert.NotEmpty(header.FindAll(".view-hnav__home"));
        Assert.NotEmpty(header.FindAll("[data-view-back]"));
        Assert.Empty(header.FindAll("#view-hnav-item"));
    }

    [Fact]
    public async Task FileFactsMoveIntoQuickFacts()
    {
        var host = await OpenAsync([_gallery[0]]);

        var facts = Render<ViewEditQuickFacts>(p => p.Add(x => x.Items, host.Session!.Active.Items));

        Assert.Equal("IMG_01.jpg", facts.Find("[data-fact='file'] .tl-quick-facts__value").TextContent.Trim());
        Assert.Equal("2", facts.Find("[data-fact='parts'] .tl-quick-facts__value").TextContent.Trim());
        Assert.NotEmpty(facts.FindAll("[data-fact='dimensions']"));
        Assert.NotEmpty(facts.FindAll("[data-fact='size']"));
    }

    [Fact]
    public async Task ThePopoverDrawsTheSharedFrameWithNoFilesTabAndDeleteInTheFooter()
    {
        var host = await OpenAsync([_gallery[0]], new ViewEditGalleryRail(GalleryId, "Lisbon 2026", _gallery));

        var popover = Render<ViewEditorPopover>();

        var tabs = popover.FindAll("[role='tab']").Select(tab => tab.TextContent.Trim()).ToList();
        Assert.Equal(2, tabs.Count);
        Assert.Contains(tabs, tab => tab.Contains("Details", StringComparison.Ordinal));
        Assert.Contains(tabs, tab => tab.Contains("History", StringComparison.Ordinal));
        Assert.DoesNotContain(tabs, tab => tab.Contains("Files", StringComparison.Ordinal));
        Assert.NotEmpty(popover.FindAll(".tl-editor-window__footer-start .tl-editor-window__delete"));
        host.Close();
    }

    [Fact]
    public async Task SaveChangesShowsReviewBeforeAnythingIsSaved()
    {
        var host = await OpenAsync([_gallery[0]]);
        host.Session!.Active.AddTag("Summer");
        var popover = Render<ViewEditorPopover>();

        var save = popover.FindAll("button").First(button => button.TextContent.Contains("Save changes", StringComparison.Ordinal));
        await save.ClickAsync();

        Assert.NotNull(popover.FindComponent<ReviewChangesView>());
        Assert.True(host.IsOpen);
        Assert.False(host.Session!.Saving);
        Assert.True(host.Session.HasPendingChanges);
        host.Close();
    }

    [Theory]
    [InlineData("phone", true)]
    [InlineData("tablet", true)]
    public async Task APhoneOrTabletNeverEditsAPhoto(string formFactor, bool touch)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", formFactor, touch, FinePointer: false, Hover: false));
        var host = Services.GetRequiredService<ViewEditorHost>();

        Assert.False(await host.OpenItemsAsync([_gallery[0]]));
        Assert.False(host.IsOpen);
    }

    [Fact]
    public void NoViewEditorComponentDrawsItsOwnFrameOrAFilesTabAndTheOldRailIsGone()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "MediaEngine.slnx")))
        {
            root = Path.GetDirectoryName(root);
        }

        var details = Path.Combine(root!, "src", "MediaEngine.Web", "Components", "Details");
        foreach (var path in Directory.EnumerateFiles(details, "View*.razor").Where(path => Path.GetFileName(path).StartsWith("ViewEdit", StringComparison.Ordinal) || Path.GetFileName(path).StartsWith("ViewGallery", StringComparison.Ordinal)))
        {
            var source = File.ReadAllText(path);
            var name = Path.GetFileName(path);
            foreach (var part in new[] { "tl-editor-window__footer", "tl-editor-window__close", "<AppTabs", "<footer" })
            {
                Assert.DoesNotContain(part, source, StringComparison.Ordinal);
            }
            Assert.True(name == "ViewEditorPopover.razor" || !source.Contains("<AppDialogFrame", StringComparison.Ordinal), $"{name} draws its own window; only the pop-over hands the shared frame its parts.");
            Assert.DoesNotContain("ViewEditTool.Files", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ViewEditTool.Contents", source, StringComparison.Ordinal);
        }

        Assert.False(File.Exists(Path.Combine(details, "ViewEditRail.razor")));
        Assert.False(File.Exists(Path.Combine(details, "ViewEditFilesPanel.razor")));
        Assert.False(File.Exists(Path.Combine(details, "ViewGalleryContentsPanel.razor")));
        Assert.DoesNotContain("Files", Enum.GetNames<ViewEditTool>());
        Assert.DoesNotContain("Contents", Enum.GetNames<ViewEditTool>());
    }
}
