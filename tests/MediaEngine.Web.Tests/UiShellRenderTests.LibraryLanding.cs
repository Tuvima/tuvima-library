using Bunit;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Library;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Library and Ingestion opens on one live page (library summary, live ingestion, recently added); Recently Added is
/// reached from its "View all" link and is not a menu row (Settings revamp D37).
/// </summary>
public sealed partial class UiShellRenderTests
{
    private void UseLandingApi()
    {
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryOverviewAsync), _ =>
                Task.FromResult<LibraryOverviewDto?>(new LibraryOverviewDto
                {
                    TotalItems = 4,
                    ReviewTotal = 2,
                    MediaTypeCounts = new Dictionary<string, int> { ["Books"] = 3, ["Movies"] = 1 },
                }));
            stub.SetHandler(nameof(IEngineApiClient.GetIngestionPresentationAsync), _ =>
                Task.FromResult<IngestionPresentationSnapshotDto?>(new IngestionPresentationSnapshotDto()));
        });
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddScoped<IngestionLiveDashboardState>();
    }

    private IRenderedComponent<Settings> RenderSettings(string section)
    {
        var host = Render(builder =>
        {
            builder.OpenComponent<AppPopoverHost>(0); builder.CloseComponent();
            builder.OpenComponent<AppDialogHost>(1); builder.CloseComponent();
            builder.OpenComponent<AppToastHost>(2); builder.CloseComponent();
            builder.OpenComponent<Settings>(3);
            builder.AddAttribute(4, nameof(Settings.Section), section);
            builder.CloseComponent();
        });
        return host.FindComponent<Settings>();
    }

    [Fact]
    public void LibraryAndIngestionLandingPage_ShowsTheLibrarySummary_ThenLiveIngestion_ThenTheAddedSummaryWithAViewAllLinkToTheActivityLog()
    {
        UseLandingApi();

        var cut = RenderSettings("ingestion");

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll("[data-testid='library-summary']"));
            Assert.Equal(6, cut.FindAll(".library-summary__media").Count);
            Assert.Contains("2 items need review", cut.Find("[data-testid='library-summary-review']").TextContent, StringComparison.Ordinal);
            Assert.NotEmpty(cut.FindAll(".ingestion-status"));
            var viewAll = cut.Find(".ingestion-added a[href='/settings/system/logs?tab=events&filter=added']");
            Assert.Contains("View all", viewAll.TextContent, StringComparison.Ordinal);

            var markup = cut.Markup;
            var summary = markup.IndexOf("data-testid=\"library-summary\"", StringComparison.Ordinal);
            var live = markup.IndexOf("ingestion-status", StringComparison.Ordinal);
            var recent = markup.IndexOf("ingestion-added", StringComparison.Ordinal);
            Assert.True(summary >= 0 && summary < live && live < recent, "Summary, live ingestion, then the added summary.");
            Assert.DoesNotContain("Continue optional setup", markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void RecentlyAddedPage_IsNotInTheMenu_ButItsRouteResolvesWithAServerLibraryAndIngestionBreadcrumb()
    {
        UseLandingApi();

        var cut = RenderSettings("recently-added");

        cut.WaitForAssertion(() =>
        {
            var trail = cut.FindAll("nav.settings-breadcrumbs li, nav.settings-breadcrumbs > *")
                .Select(item => item.TextContent.Trim())
                .Where(text => text.Length > 0)
                .Distinct()
                .ToArray();
            Assert.Equal(["Settings", "Server", "Library and Ingestion", "Recently Added"], trail);
            var links = cut.FindAll(".media-section-shell__tree-link");
            Assert.DoesNotContain(links, link => link.TextContent.Contains("Recently Added", StringComparison.Ordinal));
            Assert.Single(links, link => link.TextContent.Contains("Library and Ingestion", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void LibraryAndIngestionRow_CarriesTheNeedsReviewCount()
    {
        UseLandingApi();

        var cut = RenderSettings("ingestion");

        cut.WaitForAssertion(() =>
        {
            var row = cut.FindAll(".media-section-shell__tree-link")
                .First(link => link.TextContent.Contains("Library and Ingestion", StringComparison.Ordinal));
            var badge = row.QuerySelector(".media-section-shell__rail-meta--notification");
            Assert.NotNull(badge);
            Assert.Equal("3 items need review", badge!.GetAttribute("aria-label"));
        });
    }

    [Fact]
    public void LegacySettingsReviewRoute_StillRedirectsToRecentlyAddedWithReviewExpanded()
    {
        UseLandingApi();
        var navigation = Services.GetRequiredService<NavigationManager>();
        var visited = new List<string>();
        navigation.LocationChanged += (_, args) => visited.Add(args.Location);

        var cut = RenderSettings("review");

        // Without a router the page is not re-routed afterwards, so the redirect is read from the locations visited.
        cut.WaitForAssertion(
            () => Assert.Contains(visited, uri => uri.EndsWith("/settings/recently-added?review=expanded", StringComparison.Ordinal)));
    }

    [Fact]
    public void NoReferenceToTheRetiredOverviewTabRemainsInTheDashboardSources()
    {
        var root = GetRepoFile("src", "MediaEngine.Web");
        var settingsFolder = Path.Combine(root, "Components", "Settings");

        Assert.False(File.Exists(Path.Combine(settingsFolder, "OverviewTab.razor")));
        Assert.False(File.Exists(Path.Combine(settingsFolder, "OverviewTab.razor.css")));
        var offenders = Directory.EnumerateFiles(settingsFolder, "*.*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "Components", "Pages"), "Settings*.razor"))
            .Where(file => File.ReadAllText(file).Contains("<OverviewTab", StringComparison.Ordinal)
                           || File.ReadAllText(file).Contains("OverviewTab.razor", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(offenders);
    }
}
