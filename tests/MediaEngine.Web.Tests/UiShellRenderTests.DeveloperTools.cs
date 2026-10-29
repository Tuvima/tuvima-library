using Bunit;
using MediaEngine.Contracts.System;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Developer Tools (Settings revamp D35): it opens on the Provider Tester, and the Enrichment Tester and Development data
/// sub-pages exist only when the running Engine says it is a development build that offers their actions. A release
/// Engine drops them from the menu and the phone page selector, and their addresses show the not-available state.
/// </summary>
public sealed partial class UiShellRenderTests
{
    private const string NotAvailableTitle = "Not available in this build";

    // Registers an Engine that answers the development-tools question as given; null stands for an Engine that cannot be asked.
    private EngineApiClientStub UseDevelopmentTools(DevelopmentToolsResponse? answer)
    {
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetDevelopmentToolsAsync), _ => Task.FromResult(answer)));
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddMemoryCache();
        Services.AddScoped<ProviderCatalogueService>();
        return (EngineApiClientStub)(object)api;
    }

    private IRenderedComponent<Settings> OpenSettingsAt(string section)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/settings/{section}");
        return Render<Settings>(parameters => parameters.Add(page => page.Section, section));
    }

    private static string[] MenuLabels(IRenderedComponent<Settings> cut) =>
        cut.FindAll(".media-section-shell__tree-label").Select(label => label.TextContent.Trim()).ToArray();

    private static string[] TabLabels(IRenderedComponent<Settings> cut) =>
        cut.FindAll(".tl-tab").Select(tab => tab.TextContent.Trim()).ToArray();

    private static string[] PhoneSelectorRoutes(IRenderedComponent<Settings> cut) =>
        cut.FindAll("#settings-mobile-page option").Select(option => option.GetAttribute("value")!).ToArray();

    [Fact]
    public void DevelopmentEngine_OffersBothDevelopmentSubPages_InTheMenuAndThePhoneSelector()
    {
        UseDevelopmentTools(new DevelopmentToolsResponse(EnrichmentTester: true, DevelopmentData: true));

        var cut = OpenSettingsAt("developer");

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Developer Tools", MenuLabels(cut));
            var tabs = TabLabels(cut);
            Assert.Equal(["Provider Tester", "Enrichment Tester", "Development data"], tabs);

            // The phone selector lists pages, and Developer Tools is one page.
            Assert.Contains("/settings/developer", PhoneSelectorRoutes(cut));
        });
    }

    [Fact]
    public void ReleaseEngine_KeepsTheProviderTester_ButDropsBothDevelopmentSubPagesFromMenuAndPhoneSelector()
    {
        var engine = UseDevelopmentTools(new DevelopmentToolsResponse(EnrichmentTester: false, DevelopmentData: false));

        var cut = OpenSettingsAt("developer");

        cut.WaitForAssertion(() =>
        {
            // Developer Tools opens on the Provider Tester, which uses ordinary search actions and needs no development build.
            Assert.Single(cut.FindAll("[data-temporary-tool='provider-tester']"));
            Assert.Contains(nameof(IEngineApiClient.GetDevelopmentToolsAsync), engine.Calls);

            Assert.Contains("Developer Tools", MenuLabels(cut));
            var labels = TabLabels(cut);
            Assert.DoesNotContain("Enrichment Tester", labels);
            Assert.DoesNotContain("Development data", labels);

            var routes = PhoneSelectorRoutes(cut);
            Assert.Contains("/settings/developer", routes);
            Assert.DoesNotContain("/settings/enrichment-tester", routes);
            Assert.DoesNotContain("/settings/development-data", routes);
        });
    }

    [Fact]
    public void EngineThatCannotBeAsked_TreatsTheDevelopmentSubPagesAsNotAvailable()
    {
        UseDevelopmentTools(answer: null);

        var cut = OpenSettingsAt("developer");

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll("[data-temporary-tool='provider-tester']"));
            var labels = TabLabels(cut);
            Assert.DoesNotContain("Enrichment Tester", labels);
            Assert.DoesNotContain("Development data", labels);
        });
    }

    [Theory]
    [InlineData("enrichment-tester")]
    [InlineData("development-data")]
    [InlineData("dev-harness")]
    public void ReleaseEngine_ShowsTheNotAvailableState_AtTheDevelopmentSubPageAddresses(string address)
    {
        UseDevelopmentTools(new DevelopmentToolsResponse(EnrichmentTester: false, DevelopmentData: false));

        var cut = OpenSettingsAt(address);

        cut.WaitForAssertion(() => Assert.Contains(NotAvailableTitle, cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-temporary-tool='enrichment-tester']"));
        Assert.Empty(cut.FindAll("[data-temporary-page='development-tools']"));
        // The address stays where it is (no silent redirect to another page) and no menu row is greyed out in its place.
        Assert.DoesNotContain(TabLabels(cut), label => label is "Enrichment Tester" or "Development data");
    }

    [Fact]
    public void DevelopmentEngine_OpensTheDevelopmentDataPage_WithoutTheNotAvailableState()
    {
        UseDevelopmentTools(new DevelopmentToolsResponse(EnrichmentTester: true, DevelopmentData: true));

        var cut = OpenSettingsAt("development-data");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-temporary-page='development-tools']")));
        Assert.DoesNotContain(NotAvailableTitle, cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Development data", TabLabels(cut));
    }

    [Fact]
    public void DevelopmentDataIsOfferedOnItsOwn_WithoutTheEnrichmentTester()
    {
        UseDevelopmentTools(new DevelopmentToolsResponse(EnrichmentTester: false, DevelopmentData: true));

        var cut = OpenSettingsAt("developer");

        cut.WaitForAssertion(() =>
        {
            var labels = TabLabels(cut);
            Assert.Contains("Development data", labels);
            Assert.DoesNotContain("Enrichment Tester", labels);
        });
    }

    [Fact]
    public void NonAdministrator_SeesNoDeveloperGroup_AndTheEngineIsNeverAskedAboutDevelopmentTools()
    {
        var engine = UseDevelopmentTools(new DevelopmentToolsResponse(EnrichmentTester: true, DevelopmentData: true));
        var profile = Guid.NewGuid();
        var authority = new MediaEngine.Contracts.Authentication.DashboardAuthorityResponse(
            profile, profile, true, true, 1, 1, true, false, null, 1,
            [new MediaEngine.Contracts.Authentication.AccountProfileGrantDto(profile, profile, "Profile", null, true, true, true,
                new MediaEngine.Contracts.Authentication.ProfileAdminProtectionResponse(false, "None", null, 1), 1, DateTimeOffset.UtcNow)],
            [], []);
        var session = new DashboardSessionAccessor();
        session.Set("member-session", profile, profile, Guid.NewGuid(), authority);
        Services.AddScoped(_ => session);
        Services.AddSingleton<IHttpClientFactory>(new SettingsIdentityClientFactory(new DelayedLockHandler(authority, session)));
        Services.AddScoped<DashboardIdentityClient>();

        var cut = OpenSettingsAt("overview");

        cut.WaitForAssertion(() => Assert.Contains("Profile", MenuLabels(cut)));
        var labels = MenuLabels(cut);
        Assert.DoesNotContain("Developer Tools", labels);
        Assert.DoesNotContain("Enrichment Tester", labels);
        Assert.DoesNotContain("Development data", labels);
        Assert.DoesNotContain(PhoneSelectorRoutes(cut), route => route.Contains("developer", StringComparison.Ordinal)
            || route.Contains("enrichment-tester", StringComparison.Ordinal)
            || route.Contains("development-data", StringComparison.Ordinal));
        Assert.DoesNotContain(nameof(IEngineApiClient.GetDevelopmentToolsAsync), engine.Calls);
    }
}
