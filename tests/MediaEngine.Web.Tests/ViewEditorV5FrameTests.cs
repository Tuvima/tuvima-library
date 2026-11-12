using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Editor;
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

    // The numbers the Engine carries on the gallery's photo read (config/editor.json); the Dashboard holds none of them.
    private const int EngineInline = 8;
    private const int EnginePage = 50;

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<LocalAssetDto> _gallery = [.. Enumerable.Range(1, 12).Select(index => Photo($"IMG_{index:00}.jpg"))];
    private readonly List<ViewItemsEditSetRequest> _edits = [];
    private readonly Dictionary<string, TaskCompletionSource<List<LocalAssetDto>>> _searches = [];
    private int _galleryItemCount = 12;

    public ViewEditorV5FrameTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetViewAssetsAsync), args => GalleryPageAsync((ViewAssetQueryOptions)args![0]!));
            stub.SetHandler(nameof(IEngineApiClient.GetViewGalleryAsync), _ => Task.FromResult<ViewGalleryDto?>(
                new ViewGalleryDto(GalleryId, Guid.NewGuid(), Guid.NewGuid(), "Lisbon 2026", null, ViewGalleryKind.Manual, null, null, 0, _galleryItemCount, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
            stub.SetHandler(nameof(IEngineApiClient.EditViewItemsAsync), args =>
            {
                var request = (ViewItemsEditSetRequest)args![0]!;
                _edits.Add(request);
                return Task.FromResult(new ViewCatalogueResult<ViewItemsEditResultDto>(new ViewItemsEditResultDto(!request.PreviewOnly, true, 1, [])));
            });
        }));
        Services.AddNativeUiServices();
        Render<AppPopoverHost>();
    }

    private async Task<ViewAssetTimelinePageDto?> GalleryPageAsync(ViewAssetQueryOptions options)
    {
        var limits = new ViewEditorLimitsDto(EngineInline, EnginePage);
        if (options.Search is { } search && _searches.TryGetValue(search, out var pending))
        {
            return new ViewAssetTimelinePageDto(await pending.Task, null, false, limits);
        }

        return new ViewAssetTimelinePageDto(_gallery, null, false, limits);
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
        var host = await OpenAsync([_gallery[2]], new ViewEditGalleryRail(GalleryId, "Lisbon 2026", _gallery, _gallery.Count));

        var header = Render<ViewEditHeader>(p => p.Add(x => x.Session, host.Session!));

        Assert.Equal("Lisbon 2026", header.Find(".tl-editor-header__label").TextContent.Trim());
        Assert.NotEmpty(header.FindAll(".view-hnav__home"));
        Assert.Contains("3 of 12", header.Find("#view-hnav-item").TextContent);
        Assert.Equal("Go to gallery Lisbon 2026", header.Find(".view-hnav__home").GetAttribute("aria-label"));
        // Editor v6 D18: the home button is an outlined button in the normal icon color, not a filled accent tile.
        var home = header.Find(".view-hnav__home");
        Assert.Contains("app-icon-button--outlined", home.ClassList);
        Assert.DoesNotContain("app-icon-button--filled", home.ClassList);
        Assert.DoesNotContain("app-tone--primary", home.ClassList);
        Assert.Empty(header.FindAll("[data-level='Season']"));
    }

    [Fact]
    public async Task TheItemDropdownListsEightPicturesInlineThenAllPhotosAndPickingOneSwitchesTheItem()
    {
        var host = await OpenAsync([_gallery[0]], new ViewEditGalleryRail(GalleryId, "Lisbon 2026", _gallery, _gallery.Count));
        var header = Render<ViewEditHeader>(p => p.Add(x => x.Session, host.Session!));

        await header.Find("#view-hnav-item").ClickAsync();

        var popover = header.FindComponent<AppPopover>();
        Assert.Equal(EngineInline, popover.FindAll("[role='option']").Count);
        Assert.NotEmpty(popover.FindAll("#view-hnav-all"));

        await popover.FindAll("[role='option']")[3].ClickAsync();

        Assert.Equal(_gallery[3].Id, host.Session!.Active.Items[0].Id);
    }

    [Fact]
    public async Task AllPhotosOpensTheWholeGalleryAsASearchableList()
    {
        var host = await OpenAsync([_gallery[0]], new ViewEditGalleryRail(GalleryId, "Lisbon 2026", _gallery, _gallery.Count));
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
        var host = await OpenAsync([_gallery[0], _gallery[1], _gallery[2]], new ViewEditGalleryRail(GalleryId, "Lisbon 2026", _gallery, _gallery.Count));

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
        var host = await OpenAsync([_gallery[0]], new ViewEditGalleryRail(GalleryId, "Lisbon 2026", _gallery, _gallery.Count));

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
        Assert.True(host.Session!.HasPendingChanges);
        Assert.DoesNotContain(_edits, edit => !edit.PreviewOnly);

        // Review's own Save button is the one that writes: one check, then one save, and the window closes.
        var confirm = popover.FindAll("button").First(button => button.TextContent.Contains("Save 1 change", StringComparison.Ordinal));
        await confirm.ClickAsync();

        popover.WaitForAssertion(() => Assert.False(host.IsOpen));
        Assert.Single(_edits, edit => !edit.PreviewOnly);
        Assert.False(_edits[^1].PreviewOnly);
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

        var details = Path.Combine(root!, "src", "MediaEngine.Web", "Components", "Editor");
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

    [Fact]
    public async Task NavigatingInsideTheWindowKeepsTheOpenersClosedAnswerUntilTheFinalClose()
    {
        var host = await OpenAsync([_gallery[0]], new ViewEditGalleryRail(GalleryId, "Lisbon 2026", _gallery, _gallery.Count));
        var closedEvents = new List<ViewEditClosedArgs>();
        host.Closed += closedEvents.Add;
        var answer = host.WhenClosedAsync();

        Assert.True(await host.OpenGalleryByIdAsync(GalleryId));
        Assert.True(host.Session!.IsGallery);
        Assert.False(answer.IsCompleted);
        Assert.True(await host.OpenPhotoInGalleryAsync(_gallery[1], GalleryId, "Lisbon 2026", _gallery.Count));
        Assert.False(answer.IsCompleted);
        Assert.Same(answer, host.WhenClosedAsync());

        host.ItemsDeleted([_gallery[1].Id]);
        Assert.False(answer.IsCompleted);
        host.Close();

        var closed = await answer;
        Assert.Single(closedEvents);
        Assert.Contains(_gallery[1].Id, closed.DeletedIds);
        Assert.True(closed.Changed);
        Assert.Contains(_gallery[0].Id, closed.ItemIds);
    }

    [Fact]
    public async Task ASaveAfterMovingAroundReportsEveryItemTheWindowTouched()
    {
        var host = await OpenAsync([_gallery[0]], new ViewEditGalleryRail(GalleryId, "Lisbon 2026", _gallery, _gallery.Count));
        var answer = host.WhenClosedAsync();
        await host.OpenGalleryByIdAsync(GalleryId);
        await host.OpenPhotoInGalleryAsync(_gallery[4], GalleryId, "Lisbon 2026", _gallery.Count);
        host.Session!.Active.AddTag("Summer");

        Assert.True(await host.Session.SaveAsync());

        var closed = await answer;
        Assert.True(closed.Changed);
        Assert.Contains(_gallery[0].Id, closed.ItemIds);
        Assert.Contains(_gallery[4].Id, closed.ItemIds);
        Assert.NotNull(closed.Saved);
    }

    [Fact]
    public async Task HomeAndAPickAskAboutUnsavedChangesFirstAndStayPutWhenTheAnswerIsNo()
    {
        var host = await OpenAsync([_gallery[0]], new ViewEditGalleryRail(GalleryId, "Lisbon 2026", _gallery, _gallery.Count));
        var answers = new Queue<bool>([false, false, true]);
        var asked = 0;
        host.ConfirmDiscard = () =>
        {
            asked++;
            return Task.FromResult(answers.Dequeue());
        };
        host.Session!.Active.AddTag("Summer");
        var original = host.Session;

        Assert.False(await host.OpenGalleryByIdAsync(GalleryId));
        Assert.False(await host.OpenPhotoInGalleryAsync(_gallery[2], GalleryId, "Lisbon 2026", _gallery.Count));
        Assert.Same(original, host.Session);
        Assert.Equal(2, asked);
        Assert.Equal(1, original.ChangeCount);

        Assert.True(await host.OpenGalleryByIdAsync(GalleryId));
        Assert.Equal(3, asked);
        Assert.True(host.Session!.IsGallery);
    }

    [Fact]
    public async Task APhotoShowsItsPositionAgainstTheGalleriesRealSizeNotTheLoadedPage()
    {
        _galleryItemCount = 96;
        var host = await OpenAsync([_gallery[2]], new ViewEditGalleryRail(GalleryId, "Lisbon 2026", _gallery, 96, HasMore: true));

        var header = Render<ViewEditHeader>(p => p.Add(x => x.Session, host.Session!));
        await header.Find("#view-hnav-item").ClickAsync();

        Assert.Contains("3 of 96", header.Find("#view-hnav-item").TextContent);
        Assert.Contains("All photos (96)", header.FindComponent<AppPopover>().Find("#view-hnav-all").TextContent);
    }

    [Fact]
    public async Task ANewGalleryListsItsStartingPhotosInTheItemDropdownAndSaysHowManyInTheFacts()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var host = Services.GetRequiredService<ViewEditorHost>();
        Assert.True(await host.OpenGalleryAsync(null, startingMembers: [_gallery[0], _gallery[1], _gallery[2]]));

        var header = Render<ViewEditHeader>(p => p.Add(x => x.Session, host.Session!));

        Assert.Contains("Gallery · 3 photos", header.Find(".tl-editor-header__facts").TextContent);
        Assert.NotEmpty(header.FindAll(".view-hnav__home.is-current"));
        await header.Find("#view-hnav-item").ClickAsync();
        Assert.Equal(3, header.FindComponent<AppPopover>().FindAll("[role='option']").Count);
    }

    [Fact]
    public async Task AnOlderSearchAnsweringLateNeverReplacesTheNewerOne()
    {
        var slow = new TaskCompletionSource<List<LocalAssetDto>>();
        _searches["lis"] = slow;
        _searches["lisb"] = new TaskCompletionSource<List<LocalAssetDto>>();
        _searches["lisb"].SetResult([_gallery[5] with { Description = "Found one" }, _gallery[6] with { Description = "Found two" }]);
        var host = await OpenAsync([_gallery[0]], new ViewEditGalleryRail(GalleryId, "Lisbon 2026", _gallery, _gallery.Count));
        var header = Render<ViewEditHeader>(p => p.Add(x => x.Session, host.Session!));
        await header.Find("#view-hnav-item").ClickAsync();
        await header.FindComponent<AppPopover>().Find("#view-hnav-all").ClickAsync();
        var search = header.FindComponent<AppSearchField>();

        var first = header.InvokeAsync(() => search.Instance.ValueChanged.InvokeAsync("lis"));
        await header.InvokeAsync(() => search.Instance.ValueChanged.InvokeAsync("lisb"));
        slow.SetResult([_gallery[8] with { Description = "Stale" }]);
        await first;

        var titles = header.FindComponent<AppPopover>().FindAll("[role='option'] b").Select(option => option.TextContent.Trim()).ToList();
        Assert.Equal(["Found one", "Found two"], titles);
    }

    [Fact]
    public async Task FileFactsSitInQuickFactsAndTheHeaderLineCarriesOnlyKindDatePlace()
    {
        var withPlace = _gallery[0] with { LocationCity = "Lisbon", LocationCountry = "Portugal" };
        var host = await OpenAsync([withPlace]);

        var header = Render<ViewEditHeader>(p => p.Add(x => x.Session, host.Session!));

        var line = header.Find(".tl-editor-header__facts").TextContent;
        Assert.Contains("Photo", line);
        Assert.Contains("Lisbon, Portugal", line);
        Assert.DoesNotContain("6000", line);
        Assert.DoesNotContain("MB", line);
    }

    [Fact]
    public async Task ASelectionShowsItsFormatsOnTheHeaderLine()
    {
        var jpeg = _gallery[0] with { Files = [] };
        var jpegTwo = _gallery[1] with { Files = [] };
        var pair = _gallery[2];
        var host = await OpenAsync([jpeg, jpegTwo, pair], new ViewEditGalleryRail(GalleryId, "Lisbon 2026", _gallery, _gallery.Count));

        var header = Render<ViewEditHeader>(p => p.Add(x => x.Session, host.Session!));

        Assert.Equal("Lisbon 2026", header.Find(".tl-editor-header__label").TextContent.Trim());
        Assert.Contains("2 JPEG", header.Find(".tl-editor-header__facts").TextContent);
        Assert.Contains("1 RAW + ", header.Find(".tl-editor-header__facts").TextContent);
    }

    [Fact]
    public async Task TheFooterSaysFileWriteBackIsOffInTheLibraryEditorsWords()
    {
        var host = await OpenAsync([_gallery[0]]);

        var popover = Render<ViewEditorPopover>();

        var where = popover.Find(".tl-editor-window__where");
        Assert.Contains("File write-back off", where.TextContent);
        Assert.Contains("Edits save in Library", where.TextContent);
        host.Close();
    }

    [Fact]
    public async Task DateTakenAndPlaceStartLockedAndUnlockingThemStagesNothing()
    {
        var host = await OpenAsync([_gallery[0]]);

        var panel = Render<ViewEditDetailsPanel>(p => p.Add(x => x.Stage, host.Session!.Active));

        Assert.Equal("true", panel.Find("[data-field-key='date']").GetAttribute("data-locked"));
        Assert.Equal("true", panel.Find("[data-field-key='place']").GetAttribute("data-locked"));
        Assert.Equal("true", panel.Find("[data-field-key='people']").GetAttribute("data-locked"));
        Assert.Contains("Unlock", panel.Find("[data-field-key='date'] .tl-editor-field__lock-toggle").GetAttribute("aria-label"));

        await panel.Find("[data-field-key='date'] .tl-editor-field__lock-toggle").ClickAsync();

        Assert.Equal("false", panel.Find("[data-field-key='date']").GetAttribute("data-locked"));
        Assert.Equal(0, host.Session!.Active.ChangeCount);
    }

    [Fact]
    public async Task ChangingAnUnlockedDateShowsRevertAndRevertPutsItBackLocked()
    {
        var host = await OpenAsync([_gallery[0]]);
        var panel = Render<ViewEditDetailsPanel>(p => p.Add(x => x.Stage, host.Session!.Active));
        await panel.Find("[data-field-key='date'] .tl-editor-field__lock-toggle").ClickAsync();

        host.Session!.Active.SetDate(new DateTimeOffset(2026, 3, 5, 10, 0, 0, TimeSpan.Zero));
        panel.Render();

        Assert.Equal("yours", panel.Find("[data-field-key='date'] [data-lock-state]").GetAttribute("data-lock-state"));
        var revert = panel.FindAll("[data-field-key='date'] button").First(button => button.TextContent.Contains("Revert", StringComparison.Ordinal));
        await revert.ClickAsync();

        Assert.Equal(0, host.Session.Active.ChangeCount);
        Assert.Equal("false", panel.Find("[data-field-key='date']").GetAttribute("data-locked"));
    }

    [Fact]
    public async Task AGroupsDateIsAShiftSoOnlyItsPlaceStartsLocked()
    {
        var host = await OpenAsync([_gallery[0], _gallery[1]]);
        var panel = Render<ViewEditDetailsPanel>(p => p.Add(x => x.Stage, host.Session!.Active));

        Assert.Equal("false", panel.Find("[data-field-key='date']").GetAttribute("data-locked"));
        Assert.Equal("true", panel.Find("[data-field-key='place']").GetAttribute("data-locked"));
        Assert.Equal("false", panel.Find("[data-field-key='people']").GetAttribute("data-locked"));
    }

    [Fact]
    public void TheEngineCarriesTheEditorLimitsOnTheGalleryPhotoReadAndTheDashboardHoldsNoNumbers()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "MediaEngine.slnx")))
        {
            root = Path.GetDirectoryName(root);
        }

        var host = File.ReadAllText(Path.Combine(root!, "src", "MediaEngine.Web", "Services", "Editing", "ViewEdit", "ViewEditorHost.cs"));
        Assert.DoesNotContain("Shipped", host, StringComparison.Ordinal);
        Assert.DoesNotContain("GetEditorKindConfigAsync", host, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"new ViewEditorLimits\(\s*\d", host);
    }
}
