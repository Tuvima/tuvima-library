using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Web.Components.Artwork;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// View on phones: nothing depends on drag or hover. Photos are picked in a Select mode, the action bar sits above the
/// dock, artwork card actions sit behind a three-dot menu, and the filter tabs are a scrolling chip row.
/// </summary>
public sealed class ViewPhotosTouchTests : AsyncBunitContext
{
    private static readonly Guid LibraryId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GalleryId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly string RepoRoot = FindRepoRoot();
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());

    public ViewPhotosTouchTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_device);
    }

    private Task UseAsync(string formFactor, bool touch) =>
        _device.InitialiseAsync(new DeviceContextSnapshot(
            formFactor == "phone" ? "mobile" : "web", formFactor, touch, FinePointer: formFactor == "desktop" && !touch, Hover: formFactor == "desktop" && !touch));

    private static LocalAssetDto Photo(string name)
    {
        var json = "{\"id\":\"" + Guid.NewGuid() + "\",\"library_id\":\"" + LibraryId + "\",\"media_kind\":\"image\",\"title\":\"" + name
            + "\",\"file_name\":\"" + name + ".jpg\",\"mime_type\":\"image/jpeg\",\"created_at\":\"2026-10-01T10:00:00+00:00\",\"captured_at\":\"2026-10-01T10:00:00+00:00\","
            + "\"width\":1600,\"height\":1200,\"source_count\":1,\"files\":[],\"tags\":[],\"thumbnail_url\":\"/t\",\"content_url\":\"/c\",\"favorite\":false,\"hidden\":false}";
        return JsonSerializer.Deserialize<LocalAssetDto>(json)!;
    }

    private IRenderedComponent<ViewPhotoTimeline> RenderTimeline(IReadOnlyList<LocalAssetDto> items, bool selectMode = false,
        IReadOnlySet<Guid>? selected = null, Action<Guid>? toggled = null, Action<LocalAssetDto>? opened = null) =>
        Render<ViewPhotoTimeline>(parameters => parameters
            .Add(timeline => timeline.Items, items)
            .Add(timeline => timeline.Selected, selected ?? new HashSet<Guid>())
            .Add(timeline => timeline.SelectMode, selectMode)
            .Add(timeline => timeline.ThumbnailUrl, _ => "/thumb")
            .Add(timeline => timeline.OnToggleSelection, EventCallback.Factory.Create<Guid>(this, id => toggled?.Invoke(id)))
            .Add(timeline => timeline.OnOpen, EventCallback.Factory.Create<LocalAssetDto>(this, item => opened?.Invoke(item))));

    [Fact]
    public async Task TouchTilesCarryNoDraggableAttribute()
    {
        await UseAsync("phone", touch: true);

        var cut = RenderTimeline([Photo("one"), Photo("two")]);

        Assert.Equal(2, cut.FindAll("article.view-tile").Count);
        Assert.Empty(cut.FindAll("[draggable]"));
    }

    [Fact]
    public async Task MouseTilesStayDraggableForTheDesktopGalleryDrag()
    {
        await UseAsync("desktop", touch: false);

        var cut = RenderTimeline([Photo("one"), Photo("two")]);

        Assert.Equal(2, cut.FindAll("article.view-tile[draggable='true']").Count);
    }

    [Fact]
    public async Task SelectModeMakesEachTileAPressToggleThatReportsTheTap()
    {
        await UseAsync("phone", touch: true);
        var photos = new[] { Photo("one"), Photo("two") };
        var toggled = new List<Guid>();
        var opened = new List<LocalAssetDto>();

        var cut = RenderTimeline(photos, selectMode: true, toggled: toggled.Add, opened: opened.Add);

        Assert.Empty(cut.FindAll(".view-tile__select-control"));
        var surfaces = cut.FindAll(".view-tile__surface");
        Assert.Equal(2, surfaces.Count);
        Assert.All(surfaces, surface => Assert.Equal("false", surface.GetAttribute("aria-pressed")));

        surfaces[0].Click();

        Assert.Single(toggled);
        Assert.Empty(opened);
    }

    [Fact]
    public async Task SelectedTilesShowACheckmarkAndReportPressed()
    {
        await UseAsync("phone", touch: true);
        var photos = new[] { Photo("one"), Photo("two") };

        var cut = RenderTimeline(photos, selectMode: true, selected: new HashSet<Guid> { photos[0].Id });

        var pressed = cut.FindAll(".view-tile__surface").Where(surface => surface.GetAttribute("aria-pressed") == "true").ToList();
        Assert.Single(pressed);
        Assert.Single(cut.FindAll(".view-tile.is-selected"));
        Assert.Equal(2, cut.FindAll(".view-tile__check").Count);
    }

    [Fact]
    public async Task OutsideSelectModeATapOpensThePhotoAndTheSelectControlStaysAvailable()
    {
        await UseAsync("phone", touch: true);
        var photos = new[] { Photo("one") };
        var opened = new List<LocalAssetDto>();

        var cut = RenderTimeline(photos, opened: opened.Add);

        Assert.Single(cut.FindAll(".view-tile__select-control[role='checkbox']"));
        cut.Find(".view-tile__surface").Click();

        Assert.Single(opened);
        Assert.Empty(cut.FindAll(".view-tile__surface[aria-pressed]"));
    }

    [Fact]
    public async Task PhoneActionBarShowsCountAddToGalleryShareDownloadAndCancel()
    {
        await UseAsync("phone", touch: true);
        var downloaded = 0;
        var cleared = 0;

        var cut = Render<ViewSelectionToolbar>(parameters => parameters
            .Add(toolbar => toolbar.Count, 3)
            .Add(toolbar => toolbar.Phone, true)
            .Add(toolbar => toolbar.ManualGalleries, [Gallery()])
            .Add(toolbar => toolbar.ShowSharedLibrary, true)
            .Add(toolbar => toolbar.OnAddToGallery, EventCallback.Factory.Create<Guid>(this, _ => { }))
            .Add(toolbar => toolbar.OnSharedLibrary, EventCallback.Factory.Create(this, () => { }))
            .Add(toolbar => toolbar.OnDownload, EventCallback.Factory.Create(this, () => downloaded++))
            .Add(toolbar => toolbar.OnFavorite, EventCallback.Factory.Create(this, () => { }))
            .Add(toolbar => toolbar.OnTrash, EventCallback.Factory.Create(this, () => { }))
            .Add(toolbar => toolbar.OnClear, EventCallback.Factory.Create(this, () => cleared++)));

        Assert.Contains("view-selection-toolbar--phone", cut.Find("section.view-selection-toolbar").ClassList);
        Assert.Equal("3 selected", cut.Find(".view-selection-toolbar__count").TextContent.Trim());
        Assert.Contains("Add to Gallery", cut.Markup, StringComparison.Ordinal);
        var actions = cut.FindAll(".view-selection-toolbar__action").Select(button => button.TextContent.Trim()).ToList();
        Assert.Equal(["Share", "Download"], actions);
        Assert.Single(cut.FindAll(".app-row-menu__trigger"));
        Assert.Empty(cut.FindAll("[role='menuitem']"));

        // The Gallery menu only renders its items while open, so open it before judging what it offers.
        Assert.Equal(["Holidays"], OpenGalleryMenu(cut));

        cut.FindAll(".view-selection-toolbar__action")[1].Click();
        cut.Find(".view-selection-toolbar__close").Click();

        Assert.Equal(1, downloaded);
        Assert.Equal(1, cleared);
        Assert.Equal("Cancel", cut.Find(".view-selection-toolbar__close").TextContent.Trim());
    }

    [Fact]
    public async Task PhoneBarWithoutAnyGalleryDoesNotOfferToCreateOne()
    {
        await UseAsync("phone", touch: true);

        var cut = Render<ViewSelectionToolbar>(parameters => parameters
            .Add(toolbar => toolbar.Count, 1)
            .Add(toolbar => toolbar.Phone, true)
            .Add(toolbar => toolbar.OnAddToGallery, EventCallback.Factory.Create<Guid>(this, _ => { })));

        Assert.Empty(cut.FindAll("[aria-label='Add selected items to a Gallery']"));
        Assert.DoesNotContain("New Gallery", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DesktopBarKeepsEveryActionInlineAndTheNewGalleryItem()
    {
        await UseAsync("desktop", touch: false);

        var cut = Render<ViewSelectionToolbar>(parameters => parameters
            .Add(toolbar => toolbar.Count, 2)
            .Add(toolbar => toolbar.ShowSharedLibrary, true)
            .Add(toolbar => toolbar.OnCreateGallery, EventCallback.Factory.Create(this, () => { }))
            .Add(toolbar => toolbar.OnSharedLibrary, EventCallback.Factory.Create(this, () => { }))
            .Add(toolbar => toolbar.OnFavorite, EventCallback.Factory.Create(this, () => { }))
            .Add(toolbar => toolbar.OnTrash, EventCallback.Factory.Create(this, () => { }))
            .Add(toolbar => toolbar.OnClear, EventCallback.Factory.Create(this, () => { })));

        Assert.DoesNotContain("view-selection-toolbar--phone", cut.Markup, StringComparison.Ordinal);
        var actions = cut.FindAll(".view-selection-toolbar__action").Select(button => button.TextContent.Trim()).ToList();
        Assert.Equal(["Submit to Shared Library", "Favorite", "Trash"], actions);
        Assert.Empty(cut.FindAll(".app-row-menu__trigger"));
        Assert.Equal("Clear photo selection", cut.Find(".view-selection-toolbar__close").GetAttribute("aria-label"));
        Assert.Equal(["New Gallery"], OpenGalleryMenu(cut));
    }

    [Fact]
    public async Task PhotosPageSelectToggleDrivesSelectModeTheCountAndTheActionBar()
    {
        await UseAsync("phone", touch: true);
        var handler = new PhotosHandler([Photo("one"), Photo("two"), Photo("three")]);
        RegisterPageServices(handler);

        var cut = Render<ViewPage>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("article.view-tile").Count));
        Assert.Empty(cut.FindAll("article.view-tile[draggable]"));
        Assert.Empty(cut.FindAll(".view-selection-toolbar"));
        var toggle = cut.Find(".view-select-toggle");
        Assert.Equal("false", toggle.GetAttribute("aria-pressed"));
        Assert.Contains("Select", toggle.TextContent, StringComparison.Ordinal);

        toggle.Click();

        Assert.Equal("true", cut.Find(".view-select-toggle").GetAttribute("aria-pressed"));
        Assert.Contains("Done", cut.Find(".view-select-toggle").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".view-tile__select-control"));

        cut.FindAll(".view-tile__surface")[0].Click();
        cut.FindAll(".view-tile__surface")[2].Click();

        Assert.Equal("2 selected", cut.Find(".view-selection-toolbar__count").TextContent.Trim());
        Assert.Contains("view-selection-toolbar--phone", cut.Find(".view-selection-toolbar").ClassList);
        Assert.Equal(2, cut.FindAll(".view-tile__surface[aria-pressed='true']").Count);
        Assert.Contains("Add to Gallery", cut.Find(".view-selection-toolbar").TextContent, StringComparison.Ordinal);

        // Phones add to an existing Gallery but are never offered New Gallery (the Gallery editor).
        Assert.Equal(["Holidays"], OpenGalleryMenu(cut));
        Assert.Empty(cut.FindAll("button[aria-label='Create in Galleries']"));
        Assert.Contains("Download", cut.Find(".view-selection-toolbar").TextContent, StringComparison.Ordinal);
        Assert.Contains("Cancel", cut.Find(".view-selection-toolbar").TextContent, StringComparison.Ordinal);

        cut.Find(".view-selection-toolbar__close").Click();

        Assert.Empty(cut.FindAll(".view-selection-toolbar"));
        Assert.Equal("false", cut.Find(".view-select-toggle").GetAttribute("aria-pressed"));
        Assert.NotEmpty(cut.FindAll(".view-tile__select-control"));
    }

    [Fact]
    public async Task PhotosPageOffersNoSelectToggleToAMouse()
    {
        await UseAsync("desktop", touch: false);
        RegisterPageServices(new PhotosHandler([Photo("one")]));

        var cut = Render<ViewPage>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("article.view-tile")));
        Assert.Empty(cut.FindAll(".view-select-toggle"));
        Assert.Single(cut.FindAll("article.view-tile[draggable='true']"));
    }

    [Fact]
    public async Task DesktopPhotosBarOffersNewGalleryButNoDownload()
    {
        await UseAsync("desktop", touch: false);
        RegisterPageServices(new PhotosHandler([Photo("one"), Photo("two")]));

        var cut = Render<ViewPage>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("article.view-tile").Count));
        cut.FindAll(".view-tile__select-control")[0].Click();

        var bar = cut.Find(".view-selection-toolbar");
        Assert.DoesNotContain("view-selection-toolbar--phone", bar.ClassList);
        Assert.DoesNotContain("Download", bar.TextContent, StringComparison.Ordinal);
        Assert.Equal(["Holidays", "New Gallery"], OpenGalleryMenu(cut));
        Assert.Single(cut.FindAll("button[aria-label='Create in Galleries']"));
    }

    [Fact]
    public async Task SectionNavigationOffersNoGalleryEditingOnPhones()
    {
        await UseAsync("phone", touch: true);
        RegisterPageServices(new PhotosHandler([Photo("one")]));

        var cut = Render<ViewPage>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("article.view-tile")));
        Assert.Empty(cut.FindAll("button[aria-label='Create in Galleries']"));
        Assert.Empty(cut.FindAll(".media-section-shell__drag-handle"));
        Assert.Empty(cut.FindAll("[aria-label^='Manage ']"));
    }

    [Fact]
    public async Task SectionNavigationKeepsGalleryEditingOnDesktop()
    {
        await UseAsync("desktop", touch: false);
        RegisterPageServices(new PhotosHandler([Photo("one")]));

        var cut = Render<ViewPage>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("article.view-tile")));
        Assert.Single(cut.FindAll("button[aria-label='Create in Galleries']"));
        Assert.NotEmpty(cut.FindAll(".media-section-shell__drag-handle"));
    }

    [Fact]
    public async Task ArtworkCardsKeepTheirActionsInARowMenuBesideTheCardButton()
    {
        await UseAsync("desktop", touch: false);
        var asset = "one";

        var cut = Render<ArtworkJustifiedGallery<string>>(parameters => parameters
            .Add(gallery => gallery.Items, [asset])
            .Add(gallery => gallery.ItemKey, value => value)
            .Add(gallery => gallery.ImageUrl, _ => "/image")
            .Add(gallery => gallery.Width, _ => 1600)
            .Add(gallery => gallery.Height, _ => 1000)
            .Add(gallery => gallery.Title, value => value)
            .Add(gallery => gallery.RowActions, value => builder =>
            {
                builder.OpenComponent<AppRowMenu>(0);
                builder.AddAttribute(1, nameof(AppRowMenu.AriaLabel), $"Actions for {value}");
                builder.CloseComponent();
            }));

        var item = cut.Find(".artwork-justified-gallery__item");
        Assert.Contains("--artwork-gallery-aspect", item.GetAttribute("style"), StringComparison.Ordinal);
        Assert.Single(item.QuerySelectorAll("button.artwork-justified-gallery__card"));
        Assert.Single(item.QuerySelectorAll(".view-artwork-row-menu .app-row-menu__trigger"));
        Assert.Empty(cut.FindAll(".artwork-justified-gallery__card .app-row-menu__trigger"));
    }

    [Fact]
    public async Task PanoramaArtworkKeepsItsWidthCapWhenItCarriesARowMenu()
    {
        await UseAsync("desktop", touch: false);

        var cut = Render<ArtworkJustifiedGallery<string>>(parameters => parameters
            .Add(gallery => gallery.Items, ["wide", "normal"])
            .Add(gallery => gallery.ItemKey, value => value)
            .Add(gallery => gallery.ImageUrl, _ => "/image")
            .Add(gallery => gallery.Width, value => value == "wide" ? 4000 : 1600)
            .Add(gallery => gallery.Height, _ => 1000)
            .Add(gallery => gallery.Title, value => value)
            .Add(gallery => gallery.RowActions, value => builder => { }));

        var items = cut.FindAll(".artwork-justified-gallery__item");
        Assert.Contains("artwork-justified-gallery__item--panorama", items[0].ClassList);
        Assert.DoesNotContain("artwork-justified-gallery__item--panorama", items[1].ClassList);
        var css = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "wwwroot", "app.css"));
        Assert.Contains(".artwork-justified-gallery__item--panorama {" + Environment.NewLine + "    max-width: min(100%, 32rem);", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArtworkCardsWithoutRowActionsStayPlainButtons()
    {
        await UseAsync("desktop", touch: false);

        var cut = Render<ArtworkJustifiedGallery<string>>(parameters => parameters
            .Add(gallery => gallery.Items, ["one"])
            .Add(gallery => gallery.ItemKey, value => value)
            .Add(gallery => gallery.ImageUrl, _ => "/image")
            .Add(gallery => gallery.Width, _ => 1600)
            .Add(gallery => gallery.Height, _ => 1000)
            .Add(gallery => gallery.Title, value => value));

        Assert.Empty(cut.FindAll(".artwork-justified-gallery__item"));
        Assert.Single(cut.FindAll("button.artwork-justified-gallery__card"));
    }

    [Fact]
    public async Task FilterTabsAreARoleTablistWithoutAFixedMinimumWidth()
    {
        await UseAsync("phone", touch: true);

        var cut = Render<AppDiscoveryFilterBar>(parameters => parameters
            .Add(bar => bar.QuickFilters, builder =>
            {
                builder.OpenElement(0, "button");
                builder.AddAttribute(1, "class", "app-discovery-filter__tab is-active");
                builder.AddContent(2, "All");
                builder.CloseElement();
            }));

        Assert.Single(cut.FindAll(".app-discovery-filter__tabs[role='tablist']"));
        var pageStyles = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "Pages", "ViewPage.razor.css"));
        Assert.DoesNotContain("min-width: 440px", pageStyles, StringComparison.Ordinal);
        var barStyles = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "Shared", "AppDiscoveryFilterBar.razor.css"));
        Assert.Contains("@media(max-width:1050px){.app-discovery-filter{grid-template-columns:1fr}.app-discovery-filter__controls{justify-content:flex-end}.app-discovery-filter__tabs{flex:1;min-width:0}", barStyles, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChipRowScrollsTheActiveChipIntoViewOnlyWhenTheActiveKeyChanges()
    {
        await UseAsync("phone", touch: true);
        var module = JSInterop.SetupModule("./js/scroll-edge-hint.js");
        module.Mode = JSRuntimeMode.Loose;

        var cut = Render<AppDiscoveryFilterBar>(parameters => parameters.Add(bar => bar.ActiveKey, "all"));
        Assert.Single(module.Invocations["showActive"]);

        cut.Render(parameters => parameters.Add(bar => bar.ActiveKey, "all").Add(bar => bar.HasActiveAdvancedFilters, true));
        Assert.Single(module.Invocations["showActive"]);

        cut.Render(parameters => parameters.Add(bar => bar.ActiveKey, "favorites"));
        Assert.Equal(2, module.Invocations["showActive"].Count());
    }

    [Fact]
    public void PhoneChipRowAndTouchSizingLiveInAppCss()
    {
        var css = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "wwwroot", "app.css"));

        Assert.Contains("html[data-form-factor=\"phone\"] .app-discovery-filter__tabs {", css, StringComparison.Ordinal);
        Assert.Contains(":root:root:root .app-discovery-filter__tab,", css, StringComparison.Ordinal);
        Assert.Contains(":root:root .view-select-toggle,", css, StringComparison.Ordinal);
        Assert.Contains(":root:root .maplibregl-ctrl-group button,", css, StringComparison.Ordinal);
        Assert.Contains(":root:root .view-selection-toolbar--phone {", css, StringComparison.Ordinal);
        Assert.Contains("var(--tl-bottom-dock-height, 0px)", css, StringComparison.Ordinal);
    }

    [Fact]
    public void LightboxSwipesBetweenPhotosOnlyForTouchAtFitZoom()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "wwwroot", "js", "media-viewer.js"));

        Assert.Contains("finishSwipe", script, StringComparison.Ordinal);
        Assert.Contains("event.pointerType !== 'mouse'", script, StringComparison.Ordinal);
        Assert.Contains("state.scale > 1", script, StringComparison.Ordinal);
        Assert.Contains(".media-viewer__navigate--next", script, StringComparison.Ordinal);
        Assert.Contains(".media-viewer__navigate--previous", script, StringComparison.Ordinal);
    }

    /// <summary>Opens the selection bar's Gallery menu and returns the labels of the items it offers.</summary>
    private static List<string> OpenGalleryMenu(IRenderedComponent<IComponent> cut)
    {
        cut.Find("[aria-label='Add selected items to a Gallery']").Click();
        return cut.FindAll("[role='menuitem']").Select(item => item.TextContent.Trim()).ToList();
    }

    private void RegisterPageServices(PhotosHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddScoped<ActiveProfileAccessor>();
        var session = new ActiveProfileSessionService(JSInterop.JSRuntime, api);
        Services.AddSingleton(session);
        Services.AddSingleton(new UIOrchestratorService(
            api,
            new UniverseStateContainer(),
            session,
            new ConfigurationBuilder().Build(),
            NullLogger<UIOrchestratorService>.Instance));
        Services.AddSingleton<ViewWorkspaceService>();
        Services.AddSingleton<ViewAssetDragService>();
        Services.AddSingleton(new ViewMediaGrantService(new byte[32], TimeSpan.FromMinutes(1)));
    }

    private static ViewGalleryDto Gallery() =>
        new(GalleryId, Guid.NewGuid(), Guid.NewGuid(), "Holidays", null, MediaEngine.Domain.PersonalMedia.ViewGalleryKind.Manual, null, null, 0, 4, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed class PhotosHandler(IReadOnlyList<LocalAssetDto> photos) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath switch
            {
                "/profiles" => "[]",
                "/view/preferences" => "{\"profile_id\":\"00000000-0000-0000-0000-000000000001\",\"scope\":1,\"scope_profile_id\":null,\"timeline_density\":1,\"viewer_info_open\":true,\"updated_at\":null}",
                "/view/scopes" => "{\"scope\":{\"kind\":1,\"profile_id\":\"00000000-0000-0000-0000-000000000001\",\"was_fallback\":false},\"available_scopes\":[{\"kind\":1,\"profile_id\":\"00000000-0000-0000-0000-000000000001\",\"label\":\"Mine\",\"avatar_color\":null,\"avatar_url\":null}],\"can_browse_other_people\":false,\"can_view_documents\":true}",
                "/view/assets" => JsonSerializer.Serialize(new ViewAssetTimelinePageDto(photos, null, false)),
                "/view/assets/timeline-index" => "{\"buckets\":[]}",
                "/view/galleries" => JsonSerializer.Serialize(new ViewGalleryListResponse([Gallery()], [])),
                _ => "{\"owned\":[],\"shared_with_you\":[]}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
