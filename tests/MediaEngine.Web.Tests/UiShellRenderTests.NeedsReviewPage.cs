using Bunit;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Needs Review on its own page (ingestion-and-readiness D5, WP6): the administrator banner appears across Settings
/// and links to /settings/review, a person who is not a server administrator gets no banner and no review request,
/// and an administrator can open the page and resolve an item.
/// </summary>
public sealed partial class UiShellRenderTests
{
    private static readonly Guid PendingReviewId = Guid.Parse("70000000-0000-0000-0000-000000000001");

    private IRenderedComponent<IComponent> RenderSettingsWithHosts(string section, string? subsection = null) =>
        Render(builder =>
        {
            builder.OpenComponent<AppPopoverHost>(0); builder.CloseComponent();
            builder.OpenComponent<AppDialogHost>(1); builder.CloseComponent();
            builder.OpenComponent<AppToastHost>(2); builder.CloseComponent();
            builder.OpenComponent<Settings>(3);
            builder.AddAttribute(4, nameof(Settings.Section), section);
            if (subsection is not null)
            {
                builder.AddAttribute(5, nameof(Settings.Subsection), subsection);
            }
            builder.CloseComponent();
        });

    private List<string> UseSpiedReviewApi(List<Guid>? dismissed = null)
    {
        var calls = new List<string>();
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.DismissReviewItemAsync), args =>
            {
                dismissed?.Add((Guid)args![0]!);
                return Task.FromResult(true);
            });
            // Share the stub's own call list with the test.
            calls = stub.Calls;
        }));
        return calls;
    }

    private void SignInAsHouseholdAdministrator()
    {
        var authority = ShellAuthority(false, [], ["household.manage"]);
        var session = new DashboardSessionAccessor();
        session.Set("test-session-token", authority.AccountId, authority.ActiveProfileId, Guid.NewGuid(), authority);
        Services.AddScoped(_ => session);
        Services.AddSingleton<IHttpClientFactory>(new SettingsIdentityClientFactory(new DelayedLockHandler(authority, session)));
    }

    [Theory]
    [InlineData("overview", null)]
    [InlineData("ingestion", null)]
    [InlineData("system", "overview")]
    public void Banner_ForAServerAdministrator_AppearsOnSeveralSettingsPages_AndLinksToTheNeedsReviewPage(string section, string? subsection)
    {
        UseLandingApi();

        var host = RenderSettingsWithHosts(section, subsection);

        host.WaitForAssertion(() =>
        {
            var notice = host.Find("[data-settings-frame-slot='notices'] [data-settings-notice='needs-review']");
            Assert.Contains("3 items need review", notice.TextContent, StringComparison.Ordinal);
            Assert.Equal("/settings/review", notice.QuerySelector("a")!.GetAttribute("href"));
        });
    }

    [Theory]
    [InlineData("overview", null)]
    [InlineData("system", "overview")]
    [InlineData("review", null)]
    public void NonAdministrator_SeesNoBanner_AndNoReviewListOrCountIsRequested(string section, string? subsection)
    {
        var calls = UseSpiedReviewApi();
        SignInAsHouseholdAdministrator();

        var host = RenderSettingsWithHosts(section, subsection);

        // Give the page's first loads time to run before reading what was asked of the Engine.
        host.WaitForAssertion(() => Assert.NotEmpty(calls));
        Assert.Empty(host.FindAll("[data-settings-notice]"));
        Assert.Empty(host.FindAll("article.review-row"));
        Assert.DoesNotContain(nameof(IEngineApiClient.GetPendingReviewsAttemptAsync), calls);
        Assert.DoesNotContain(nameof(IEngineApiClient.GetPendingReviewsAsync), calls);
        Assert.DoesNotContain(nameof(IEngineApiClient.GetReviewCountAsync), calls);
    }

    [Fact]
    public void NeedsReviewRoute_ForAServerAdministrator_ListsTheItem_AndDismissingItResolvesIt()
    {
        var dismissed = new List<Guid>();
        UseSpiedReviewApi(dismissed);

        var host = RenderSettingsWithHosts("review");

        host.WaitForAssertion(() => Assert.Single(host.FindAll("article.review-row")));
        Assert.Contains("Unmatched Album", host.Markup, StringComparison.Ordinal);
        Assert.Empty(host.FindAll(".review-pinned__header"));

        host.Find("article.review-row .app-overflow-menu__trigger").Click();
        host.WaitForAssertion(() => Assert.Single(host.FindAll("button.app-menu-item"),
            button => button.TextContent.Contains("Dismiss from review", StringComparison.Ordinal)));
        host.FindAll("button.app-menu-item")
            .Single(button => button.TextContent.Contains("Dismiss from review", StringComparison.Ordinal))
            .Click();

        host.WaitForAssertion(() => Assert.Empty(host.FindAll("article.review-row")));
        Assert.Equal([PendingReviewId], dismissed);
    }

    [Fact]
    public void NeedsReviewRoute_IsOffTheMenu_WithAServerLibraryAndIngestionBreadcrumb()
    {
        UseSpiedReviewApi();

        var host = RenderSettingsWithHosts("review");

        host.WaitForAssertion(() =>
        {
            var trail = host.FindAll("nav.settings-breadcrumbs li, nav.settings-breadcrumbs > *")
                .Select(item => item.TextContent.Trim())
                .Where(text => text.Length > 0)
                .Distinct()
                .ToArray();
            Assert.Equal(["Settings", "Server", "Library and Ingestion", "Needs Review"], trail);
            Assert.DoesNotContain(host.FindAll(".media-section-shell__tree-link"),
                link => link.TextContent.Contains("Needs Review", StringComparison.Ordinal));
        });
    }
}
