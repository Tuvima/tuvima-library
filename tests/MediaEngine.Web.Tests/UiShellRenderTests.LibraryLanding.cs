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
/// Libraries is one page with tabs (Libraries, Activity, Needs Review, Matching, Rules); the old Library & Ingestion overview
/// is gone and its address is not found (settings-and-setup-simplified D6, D19). Activity is a tab, not a menu row.
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
    public void TheOldLibraryAndIngestionOverviewAddress_IsNotFound_AndIsNotForwarded()
    {
        UseLandingApi();
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/settings/ingestion");
        var visited = new List<string>();
        navigation.LocationChanged += (_, args) => visited.Add(args.Location);

        RenderSettings("ingestion");

        // Without a router the page is not re-routed afterwards, so the redirect is read from the locations visited.
        Assert.Contains(visited, uri => uri.EndsWith("/not-found", StringComparison.Ordinal));
        Assert.DoesNotContain(visited, uri => uri.Contains("/settings/libraries", StringComparison.Ordinal));
    }

    [Fact]
    public void ActivityTab_IsNotInTheMenu_ButItsRouteResolvesWithALibraryLibrariesBreadcrumb()
    {
        UseLandingApi();

        var cut = RenderSettings("library-activity");

        cut.WaitForAssertion(() =>
        {
            var trail = cut.FindAll("nav.settings-breadcrumbs li, nav.settings-breadcrumbs > *")
                .Select(item => item.TextContent.Trim())
                .Where(text => text.Length > 0)
                .Distinct()
                .ToArray();
            Assert.Equal(["Settings", "Library", "Libraries", "Activity"], trail);
            var links = cut.FindAll(".media-section-shell__tree-link");
            Assert.DoesNotContain(links, link => link.TextContent.Contains("Recently Added", StringComparison.Ordinal));
            Assert.DoesNotContain(links, link => link.TextContent.Contains("Activity", StringComparison.Ordinal));
            Assert.Single(links, link => link.TextContent.Trim() == "Libraries");
        });
    }

    [Fact]
    public void LibrariesRow_HasNoNeedsReviewCountBubble()
    {
        UseLandingApi();

        var cut = RenderSettings("libraries");

        cut.WaitForAssertion(() =>
        {
            var row = cut.FindAll(".media-section-shell__tree-link")
                .First(link => link.TextContent.Trim() == "Libraries");
            var badge = row.QuerySelector(".media-section-shell__rail-meta--notification");
            Assert.Null(badge);
        });
    }

    [Fact]
    public void TheOldRecentlyAddedReviewLink_IsNotFound_AndIsNotForwarded()
    {
        UseLandingApi();
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/settings/recently-added?review=expanded");
        var visited = new List<string>();
        navigation.LocationChanged += (_, args) => visited.Add(args.Location);

        var host = Render(builder =>
        {
            builder.OpenComponent<AppPopoverHost>(0); builder.CloseComponent();
            builder.OpenComponent<AppDialogHost>(1); builder.CloseComponent();
            builder.OpenComponent<AppToastHost>(2); builder.CloseComponent();
            builder.OpenComponent<Settings>(3);
            builder.AddAttribute(4, nameof(Settings.Section), "recently-added");
            builder.CloseComponent();
        });
        var cut = host.FindComponent<Settings>();

        // Without a router the page is not re-routed afterwards, so the redirect is read from the locations visited.
        cut.WaitForAssertion(
            () => Assert.Contains(visited, uri => uri.EndsWith("/not-found", StringComparison.Ordinal)));
        Assert.DoesNotContain(visited, uri => uri.EndsWith("/settings/review", StringComparison.Ordinal));
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
