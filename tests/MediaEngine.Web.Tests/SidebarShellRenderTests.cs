using MediaEngine.Web.Components.Shared;
using System.Net;
using System.Text;
using Bunit;
using MediaEngine.Web.Components.MediaHub;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class SidebarShellRenderTests : AsyncBunitContext
{
    public SidebarShellRenderTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddNativeUiServices();
        var http = new HttpClient(new GalleryHandler()) { BaseAddress = new Uri("http://localhost/") };
        Services.AddSingleton<IEngineApiClient>(new EngineApiClient(http, NullLogger<EngineApiClient>.Instance));
        Services.AddScoped<ActiveProfileAccessor>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddSingleton<ViewWorkspaceService>();
        Services.AddSingleton<ViewAssetDragService>();
    }

    [Fact]
    public void MediaSectionShell_PreservesFlatLaneNavigation()
    {
        var navigation = new[]
        {
            new MediaSectionNavigationGroup("Library",
            [
                new("Discover", "/read", AppMaterialIcons.Outlined.Explore, Exact: true),
                new("Books", "/read/books", AppMaterialIcons.Outlined.MenuBook),
            ]),
        };

        var cut = Render<MediaSectionShell>(parameters => parameters
            .Add(component => component.Title, "Read")
            .Add(component => component.NavigationGroups, navigation)
            .AddChildContent("<section id=\"content-slot\">Content</section>"));

        Assert.Single(cut.FindAll(".media-section-shell"));
        Assert.Single(cut.FindAll(".media-section-shell__rail"));
        Assert.Single(cut.FindAll(".media-section-shell__content"));
        Assert.Equal(2, cut.FindAll("a.media-section-shell__rail-item").Count);
        Assert.Empty(cut.FindAll(".media-section-shell__rail-item--parent"));
        Assert.Empty(cut.FindAll(".media-section-shell__rail-chevron"));
        Assert.NotNull(cut.Find("#content-slot"));
    }

    [Fact]
    public void MediaSectionShell_ProvidesCompactMobileNavigationWithCurrentRoute()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/watch/tv");
        var navigation = new[]
        {
            new MediaSectionNavigationGroup("Library",
            [
                new("Discover", "/watch", AppMaterialIcons.Outlined.Explore, Exact: true),
                new("TV Shows", "/watch/tv", AppMaterialIcons.Outlined.LiveTv),
            ]),
        };

        var cut = Render<MediaSectionShell>(parameters => parameters
            .Add(component => component.Title, "Watch")
            .Add(component => component.NavigationGroups, navigation)
            .AddChildContent("<section>Watch content</section>"));

        Assert.Equal("Watch mobile navigation", cut.Find(".media-section-shell__mobile-nav").GetAttribute("aria-label"));
        Assert.Contains("TV Shows", cut.Find(".media-section-shell__mobile-nav summary").TextContent, StringComparison.Ordinal);
        var current = cut.Find(".media-section-shell__mobile-nav-link[aria-current='page']");
        Assert.Contains("TV Shows", current.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewSectionShell_RendersExactlyFourPrimaryDestinationsAndTracksRoute()
    {
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo("/view");
        var cut = Render<ViewSectionShell>(parameters => parameters
            .AddChildContent("<section id=\"view-slot\">View content</section>"));

        Assert.Equal(4, cut.FindAll("#media-section-nav-view, #media-section-nav-view-galleries, #media-section-nav-view-people, #media-section-nav-view-places").Count);
        Assert.Equal("Photos", cut.Find("#media-section-nav-view").TextContent.Trim());
        Assert.Equal("Galleries", cut.Find("#media-section-nav-view-galleries").TextContent.Trim());
        Assert.Equal("People", cut.Find("#media-section-nav-view-people").TextContent.Trim());
        Assert.Equal("Places", cut.Find("#media-section-nav-view-places").TextContent.Trim());
        Assert.Equal("page", cut.Find("#media-section-nav-view").GetAttribute("aria-current"));

        navigationManager.NavigateTo("/view/places");

        cut.WaitForAssertion(() =>
            Assert.Equal("page", cut.Find("#media-section-nav-view-places").GetAttribute("aria-current")));
        Assert.NotNull(cut.Find("#view-slot"));
    }

    [Fact]
    public void MediaSectionShell_ExpandsActiveBranchAndMarksDeepLink()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/settings/ai/models");
        var cut = Render<MediaSectionShell>(parameters => parameters
            .Add(component => component.Title, "Settings")
            .Add(component => component.AccordionNavigation, true)
            .Add(component => component.NavigationGroups, BuildNestedNavigation())
            .AddChildContent("<section>Settings content</section>"));

        var localAi = cut.Find("#media-section-nav-settings-ai");
        var models = cut.Find("#media-section-nav-settings-ai-models");

        Assert.Contains("media-section-shell--nested", cut.Find(".media-section-shell").ClassList);
        Assert.Equal("A", localAi.TagName);
        Assert.Equal("true", cut.Find("#media-section-nav-settings-ai + button").GetAttribute("aria-expanded"));
        Assert.Equal("page", models.GetAttribute("aria-current"));
        Assert.Contains("is-active", models.ClassList);
        Assert.Equal(2, cut.FindAll(".media-section-shell__rail-item--child").Count);
        Assert.Empty(cut.FindAll(".media-section-shell__rail-item--child .tl-icon"));
    }

    [Fact]
    public void MediaSectionShell_AccordionOpensSelectedParentAndClosesPreviousBranch()
    {
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo("/settings/ai/models");
        var cut = Render<MediaSectionShell>(parameters => parameters
            .Add(component => component.Title, "Settings")
            .Add(component => component.AccordionNavigation, true)
            .Add(component => component.NavigationGroups, BuildNestedNavigation())
            .AddChildContent("<section>Settings content</section>"));

        cut.Find("#media-section-nav-settings-providers").Click();

        Assert.Equal("false", cut.Find("#media-section-nav-settings-ai + button").GetAttribute("aria-expanded"));
        Assert.Equal("true", cut.Find("#media-section-nav-settings-providers + button").GetAttribute("aria-expanded"));
        Assert.EndsWith("/settings/providers", navigationManager.Uri, StringComparison.Ordinal);
        Assert.Single(cut.FindAll(".media-section-shell__rail-children"));
    }

    [Fact]
    public void MediaSectionShell_DeliversTypedPlaylistDropEvent()
    {
        var playlistId = Guid.NewGuid();
        MediaSectionNavigationDropEvent? received = null;
        var navigation = new[]
        {
            new MediaSectionNavigationGroup("Playlists",
            [
                new("Road trip", "/listen/music/playlists/road-trip", AppMaterialIcons.Outlined.PlaylistPlay,
                    DropTarget: new PlaylistNavigationDropTarget(playlistId)),
            ]),
        };

        var cut = Render<MediaSectionShell>(parameters => parameters
            .Add(component => component.Title, "Listen")
            .Add(component => component.NavigationGroups, navigation)
            .Add(component => component.OnNavigationItemDrop,
                EventCallback.Factory.Create<MediaSectionNavigationDropEvent>(this, value => received = value))
            .AddChildContent("<section>Listen content</section>"));

        var target = cut.Find(".media-section-shell__rail-item.is-drop-target");
        target.TriggerEvent("ondrop", new DragEventArgs());

        var playlistTarget = Assert.IsType<PlaylistNavigationDropTarget>(received?.Target);
        Assert.Equal(playlistId, playlistTarget.PlaylistId);
        Assert.Equal("Road trip", received?.Item.Label);
    }

    [Fact]
    public void MediaSectionShell_ExpandsForDragAndAllowsNestedGalleryDropTargets()
    {
        var galleryId = Guid.NewGuid();
        MediaSectionNavigationDropEvent? received = null;
        var navigation = new[]
        {
            new MediaSectionNavigationGroup("View",
            [
                new("Galleries", "/view/galleries", AppMaterialIcons.Outlined.Collections,
                    Children:
                    [
                        new("Family", $"/view/galleries/{galleryId:D}", AppMaterialIcons.Outlined.PhotoAlbum,
                            DropTarget: new ManualGalleryNavigationDropTarget(galleryId)),
                        new("New Gallery", "/view/galleries/new", AppMaterialIcons.Outlined.Add,
                            DropTarget: new NewGalleryNavigationDropTarget()),
                    ]),
            ]),
        };

        var cut = Render<MediaSectionShell>(parameters => parameters
            .Add(component => component.Title, "View")
            .Add(component => component.NavigationGroups, navigation)
            .Add(component => component.OnNavigationItemDrop,
                EventCallback.Factory.Create<MediaSectionNavigationDropEvent>(this, value => received = value))
            .AddChildContent("<section>View content</section>"));

        cut.Find("#media-section-nav-view-galleries").TriggerEvent("ondragenter", new DragEventArgs());
        var nestedTarget = cut.Find($"#media-section-nav-view-galleries-{galleryId:D}");
        nestedTarget.TriggerEvent("ondrop", new DragEventArgs());

        var galleryTarget = Assert.IsType<ManualGalleryNavigationDropTarget>(received?.Target);
        Assert.Equal(galleryId, galleryTarget.GalleryId);
        Assert.Equal(2, cut.FindAll(".media-section-shell__rail-item--child.is-drop-target").Count);
    }

    [Fact]
    public async Task MediaSectionShell_CreatesManualContainerInlineOnEnter()
    {
        var popovers = Render<AppPopoverHost>();
        MediaSectionNavigationCreateEvent? received = null;
        var navigation = new[]
        {
            new MediaSectionNavigationGroup("Playlists", [],
                new MediaSectionNavigationCreateOptions(
                    "New Playlist",
                    "New Smart Playlist",
                    "Playlist name",
                    new ManualPlaylistCreateTarget(),
                    new SmartPlaylistCreateTarget(),
                    new NewPlaylistNavigationDropTarget())),
        };

        var cut = Render<MediaSectionShell>(parameters => parameters
            .Add(component => component.Title, "Listen")
            .Add(component => component.NavigationGroups, navigation)
            .Add(component => component.OnNavigationCreate, createEvent =>
            {
                received = createEvent;
                return Task.FromResult(true);
            })
            .AddChildContent("<section>Listen content</section>"));

        cut.Find(".media-section-shell__create-menu .app-overflow-menu__trigger").Click();
        await cut.FindAll("button.app-menu-item").Single(button => button.TextContent.Contains("New Playlist", StringComparison.Ordinal)).ClickAsync(new());
        var input = cut.Find(".media-section-shell__inline-create input");
        input.Input("Road Trip");
        input.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.IsType<ManualPlaylistCreateTarget>(received?.Target);
        Assert.Equal("Road Trip", received?.Name);
        Assert.Empty(cut.FindAll(".media-section-shell__inline-create"));
    }

    [Fact]
    public async Task MediaSectionShell_UsesInlineDeleteConfirmationForManagedContainers()
    {
        var popovers = Render<AppPopoverHost>();
        var playlistId = Guid.NewGuid();
        MediaSectionNavigationManageEvent? received = null;
        var navigation = new[]
        {
            new MediaSectionNavigationGroup("Playlists",
            [
                new("Road Trip", $"/listen/music/playlists/{playlistId:D}", AppMaterialIcons.Outlined.PlaylistPlay,
                    Management: new MediaSectionNavigationItemManagement(playlistId, "playlist")),
            ]),
        };

        var cut = Render<MediaSectionShell>(parameters => parameters
            .Add(component => component.Title, "Listen")
            .Add(component => component.NavigationGroups, navigation)
            .Add(component => component.OnNavigationItemDelete, manageEvent =>
            {
                received = manageEvent;
                return Task.FromResult(true);
            })
            .AddChildContent("<section>Listen content</section>"));

        cut.Find(".media-section-shell__manage-menu .app-overflow-menu__trigger").Click();
        await cut.FindAll("button.app-menu-item").Single(button => button.TextContent.Contains("Delete", StringComparison.Ordinal)).ClickAsync(new());

        var confirmation = cut.Find(".media-section-shell__delete-confirm");
        Assert.Contains("Delete “Road Trip”?", confirmation.TextContent, StringComparison.Ordinal);
        confirmation.QuerySelector("button.is-confirm")!.Click();
        cut.WaitForAssertion(() => {
            Assert.Equal(playlistId, received?.Management.ContainerId);
            Assert.Empty(cut.FindAll(".media-section-shell__delete-confirm"));
        });
    }

    private static IReadOnlyList<MediaSectionNavigationGroup> BuildNestedNavigation() =>
    [
        new("Admin Settings",
        [
            new("Local AI", "/settings/ai", AppMaterialIcons.Outlined.Memory,
                Children:
                [
                    new("Overview", "/settings/ai/overview", AppMaterialIcons.Outlined.Dashboard, Exact: true),
                    new("Models", "/settings/ai/models", AppMaterialIcons.Outlined.Storage, Exact: true),
                ]),
            new("Providers", "/settings/providers", AppMaterialIcons.Outlined.Storage,
                Children:
                [
                    new("Retail Lookup", "/settings/providers/retail", AppMaterialIcons.Outlined.ShoppingBag, Exact: true),
                    new("Canonical Identity", "/settings/providers/canonical", AppMaterialIcons.Outlined.Hub, Exact: true),
                ]),
        ]),
    ];

    private sealed class GalleryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri?.AbsolutePath.Equals("/profiles", StringComparison.OrdinalIgnoreCase) == true
                ? "[]"
                : "{\"owned\":[],\"shared_with_you\":[]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
