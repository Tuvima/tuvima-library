using System.Net;
using System.Text;
using Bunit;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// View browsing (library-setup-groups WP8b-2, D36 and D48): a control for a kind the profile may not use is never drawn, and in the
/// Shared Library photos and documents can be narrowed to the people who added them, shown as bare profile-name chips.
/// The Engine is a hand-written handler; it already filters the items itself.
/// </summary>
public sealed class ViewBrowseKindVisibilityTests : AsyncBunitContext
{
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Cara = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid Tax = Guid.Parse("dddddddd-0000-0000-0000-000000000004");

    private readonly BrowseHandler _handler = new();

    public ViewBrowseKindVisibilityTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);
        Services.AddSingleton<IEngineApiClient>(api);
        var device = new DeviceContextService(api);
        device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true)).GetAwaiter().GetResult();
        Services.AddSingleton(device);
        Services.AddScoped<ActiveProfileAccessor>();
        var session = new ActiveProfileSessionService(JSInterop.JSRuntime, api);
        Services.AddSingleton(session);
        Services.AddScoped<ActiveProfileSessionService>(_ => session);
        Services.AddSingleton(new UIOrchestratorService(api, new UniverseStateContainer(), session, new ConfigurationBuilder().Build(), NullLogger<UIOrchestratorService>.Instance));
        Services.AddSingleton<ViewWorkspaceService>();
        Services.AddSingleton<ViewAssetDragService>();
        Services.AddSingleton(new ViewMediaGrantService(new byte[32], TimeSpan.FromMinutes(1)));
    }

    private static string Row(Guid id, string name) => "{\"profile_id\":\"" + id + "\",\"name\":\"" + name + "\",\"item_count\":2}";

    private void ThreeContributors() => _handler.AddedBy = "[" + Row(Alice, "Alice") + "," + Row(Bob, "Bob") + "," + Row(Cara, "Cara") + "]";

    private static void OpenFilters(IRenderedComponent<ViewPage> cut) => cut.Find("button[aria-label='More photo filters']").Click();

    private static IReadOnlyList<string> QuickFilters(IRenderedComponent<ViewPage> cut) =>
        cut.FindAll(".app-discovery-filter__tabs .app-discovery-filter__tab").Select(tab => tab.TextContent.Trim()).ToList();

    private static IReadOnlyList<string> ChipTexts(IRenderedComponent<ViewPage> cut, string group) =>
        cut.FindAll($".view-filter-chips[aria-label='{group}'] .app-chip").Select(chip => chip.TextContent.Trim()).ToList();

    private string LastAssetsRequest() => _handler.Requests.Last(request => request.StartsWith("/view/assets?", StringComparison.Ordinal));

    [Fact]
    public void APersonWithPhotosAndPersonalVideosBothOffSeesNoPhotosEntry()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/view/galleries");
        _handler.CanViewPhotos = false;
        _handler.CanViewPersonalVideos = false;

        var shell = Render<ViewSectionShell>(parameters => parameters.AddChildContent("<p>content</p>"));

        shell.WaitForAssertion(() => Assert.NotEmpty(shell.FindAll("#media-section-nav-view-galleries")));
        Assert.Empty(shell.FindAll("#media-section-nav-view"));
    }

    [Fact]
    public void APersonWithOnlyPersonalVideosOnStillSeesThePhotosEntry()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/view/galleries");
        _handler.CanViewPhotos = false;
        _handler.CanViewPersonalVideos = true;

        var shell = Render<ViewSectionShell>(parameters => parameters.AddChildContent("<p>content</p>"));

        shell.WaitForAssertion(() => Assert.Equal("Photos", shell.Find("#media-section-nav-view").TextContent.Trim()));
    }

    [Fact]
    public void TheTimelineSaysItIsNotAvailableAndAsksForNothingWhenNeitherKindIsAllowed()
    {
        _handler.CanViewPhotos = false;
        _handler.CanViewPersonalVideos = false;

        var cut = Render<ViewPage>();

        cut.WaitForAssertion(() => Assert.Contains("Photos are not available for this profile", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".app-discovery-filter"));
        Assert.DoesNotContain(_handler.Requests, request => request.StartsWith("/view/assets", StringComparison.Ordinal));
    }

    [Fact]
    public void TheVideosQuickFilterIsAbsentWhenPersonalVideosAreOff()
    {
        _handler.CanViewPersonalVideos = false;

        var cut = Render<ViewPage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(QuickFilters(cut)));
        Assert.Equal(["All", "Favorites", "Archive"], QuickFilters(cut));
    }

    [Fact]
    public void TheVideosQuickFilterShowsWhenPersonalVideosAreOn()
    {
        var cut = Render<ViewPage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(QuickFilters(cut)));
        Assert.Equal(["All", "Favorites", "Videos", "Archive"], QuickFilters(cut));
    }

    [Fact]
    public void TheDocumentsFilterIsAbsentWhenDocumentsAreOffAndPresentWhenOn()
    {
        _handler.CanViewDocuments = false;
        var off = Render<ViewPage>();
        off.WaitForAssertion(() => Assert.NotEmpty(QuickFilters(off)));
        OpenFilters(off);
        Assert.Equal(["Hidden", "Trash"], off.FindAll(".app-discovery-filter__advanced .app-discovery-filter__tab").Select(tab => tab.TextContent.Trim()));

        _handler.CanViewDocuments = true;
        Services.GetRequiredService<ViewWorkspaceService>().Reset();
        var on = Render<ViewPage>();
        on.WaitForAssertion(() => Assert.NotEmpty(QuickFilters(on)));
        OpenFilters(on);
        Assert.Equal(["Hidden", "Trash", "Documents"], on.FindAll(".app-discovery-filter__advanced .app-discovery-filter__tab").Select(tab => tab.TextContent.Trim()));
    }

    [Fact]
    public void InTheSharedLibraryEachContributingProfileGetsABareNameChip()
    {
        _handler.ScopeKind = 0;
        ThreeContributors();

        var cut = Render<ViewPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(QuickFilters(cut)));
        OpenFilters(cut);

        cut.WaitForAssertion(() => Assert.Equal(["Alice", "Bob", "Cara"], ChipTexts(cut, "Added by")));
        Assert.DoesNotContain("Added by Alice", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ChoosingTwoContributorsSendsExactlyThoseTwoAndClearingSendsNone()
    {
        _handler.ScopeKind = 0;
        ThreeContributors();
        var cut = Render<ViewPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(QuickFilters(cut)));
        OpenFilters(cut);
        cut.WaitForAssertion(() => Assert.Equal(3, ChipTexts(cut, "Added by").Count));

        cut.FindAll(".view-filter-chips[aria-label='Added by'] .app-chip")[0].Click();
        cut.FindAll(".view-filter-chips[aria-label='Added by'] .app-chip")[1].Click();

        cut.WaitForAssertion(() => Assert.Equal(2, AddedByValues(LastAssetsRequest()).Count));
        Assert.Equal(new[] { Alice, Bob }.Order(), AddedByValues(LastAssetsRequest()).Order());
        Assert.Equal(["true", "true", "false"], cut.FindAll(".view-filter-chips[aria-label='Added by'] .app-chip").Select(chip => chip.GetAttribute("aria-pressed")!));

        cut.FindAll(".view-filter-chips[aria-label='Added by'] .app-chip")[1].Click();
        cut.WaitForAssertion(() => Assert.Equal([Alice], AddedByValues(LastAssetsRequest())));
        cut.FindAll(".view-filter-chips[aria-label='Added by'] .app-chip")[0].Click();
        cut.WaitForAssertion(() => Assert.Empty(AddedByValues(LastAssetsRequest())));
    }

    [Fact]
    public void WithOneContributingProfileThereIsNoAddedByRow()
    {
        _handler.ScopeKind = 0;
        _handler.AddedBy = "[" + Row(Alice, "Alice") + "]";

        var cut = Render<ViewPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(QuickFilters(cut)));
        OpenFilters(cut);

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.StartsWith("/view/assets/added-by", StringComparison.Ordinal)));
        Assert.Empty(cut.FindAll(".view-filter-chips[aria-label='Added by']"));
    }

    [Fact]
    public void InYourOwnSpaceTheEngineIsNeverAskedWhoAddedWhat()
    {
        ThreeContributors();

        var cut = Render<ViewPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(QuickFilters(cut)));
        OpenFilters(cut);

        Assert.DoesNotContain(_handler.Requests, request => request.StartsWith("/view/assets/added-by", StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".view-filter-chips[aria-label='Added by']"));
    }

    [Fact]
    public void ChoosingATagSendsThatTagWithTheTimelineAndOnlyTagsInUseAreOffered()
    {
        _handler.Catalogue = "{\"tags\":[{\"id\":\"" + Tax + "\",\"name\":\"Holiday\",\"colour\":null,\"usage_count\":4,\"can_manage\":true},"
            + "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"Unused\",\"colour\":null,\"usage_count\":0,\"can_manage\":true}],"
            + "\"correspondents\":[],\"document_types\":[],\"fields\":[]}";
        var cut = Render<ViewPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(QuickFilters(cut)));
        OpenFilters(cut);
        cut.WaitForAssertion(() => Assert.Equal(["Holiday"], ChipTexts(cut, "Tags")));
        Assert.Contains(_handler.Requests, request => request.StartsWith("/view/catalogue", StringComparison.Ordinal)
            && request.Contains("kind=image", StringComparison.Ordinal) && request.Contains("kind=video", StringComparison.Ordinal));

        cut.Find(".view-filter-chips[aria-label='Tags'] .app-chip").Click();

        cut.WaitForAssertion(() => Assert.Contains($"tag={Tax:D}", LastAssetsRequest(), StringComparison.Ordinal));
    }

    [Fact]
    public void TheDocumentsPageInTheSharedLibraryAlsoOffersTheNameChipsAndSendsTheChosenOnes()
    {
        _handler.ScopeKind = 0;
        ThreeContributors();

        var cut = Render<ViewDocumentsPage>();

        cut.WaitForAssertion(() => Assert.Equal(["Alice", "Bob", "Cara"],
            cut.FindAll(".view-filter-chips[aria-label='Added by'] .app-chip").Select(chip => chip.TextContent.Trim())));
        Assert.Contains(_handler.Requests, request => request.StartsWith("/view/assets/added-by", StringComparison.Ordinal)
            && request.Contains("kind=document", StringComparison.Ordinal));

        cut.FindAll(".view-filter-chips[aria-label='Added by'] .app-chip")[2].Click();

        cut.WaitForAssertion(() => Assert.Equal([Cara], AddedByValues(LastAssetsRequest())));
        Assert.Contains("kind=document", LastAssetsRequest(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheDocumentsPageHasNoAddedByRowWithOnlyOneContributor()
    {
        _handler.ScopeKind = 0;
        _handler.AddedBy = "[" + Row(Bob, "Bob") + "]";

        var cut = Render<ViewDocumentsPage>();

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.StartsWith("/view/assets/added-by", StringComparison.Ordinal)));
        cut.WaitForAssertion(() => Assert.Contains("No documents yet", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".view-filter-chips"));
    }

    private static List<Guid> AddedByValues(string request) =>
        System.Web.HttpUtility.ParseQueryString(new Uri("http://localhost" + request).Query).GetValues("added_by")?.Select(Guid.Parse).ToList() ?? [];

    private sealed class BrowseHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public int ScopeKind { get; set; } = 1;
        public bool CanViewPhotos { get; set; } = true;
        public bool CanViewPersonalVideos { get; set; } = true;
        public bool CanViewDocuments { get; set; } = true;
        public string AddedBy { get; set; } = "[]";
        public string Catalogue { get; set; } = "{\"tags\":[],\"correspondents\":[],\"document_types\":[],\"fields\":[]}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request.RequestUri!.PathAndQuery);
            }

            var kind = ScopeKind.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var json = request.RequestUri!.AbsolutePath switch
            {
                "/profiles" => "[]",
                "/view/preferences" => "{\"profile_id\":\"00000000-0000-0000-0000-000000000001\",\"scope\":" + kind + ",\"scope_profile_id\":null,\"timeline_density\":1,\"viewer_info_open\":true,\"updated_at\":null}",
                "/view/scopes" => "{\"scope\":{\"kind\":" + kind + ",\"profile_id\":\"00000000-0000-0000-0000-000000000001\",\"was_fallback\":false},"
                    + "\"available_scopes\":[{\"kind\":" + kind + ",\"profile_id\":\"00000000-0000-0000-0000-000000000001\",\"label\":\"Mine\",\"avatar_color\":null,\"avatar_url\":null}],"
                    + "\"can_browse_other_people\":false,\"can_view_documents\":" + Flag(CanViewDocuments)
                    + ",\"can_view_photos\":" + Flag(CanViewPhotos) + ",\"can_view_personal_videos\":" + Flag(CanViewPersonalVideos) + "}",
                "/view/assets" => "{\"items\":[],\"next_cursor\":null,\"has_more\":false}",
                "/view/assets/timeline-index" => "{\"buckets\":[]}",
                "/view/assets/added-by" => AddedBy,
                "/view/catalogue" => Catalogue,
                _ => "{\"owned\":[],\"shared_with_you\":[]}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }

        private static string Flag(bool value) => value ? "true" : "false";
    }
}
