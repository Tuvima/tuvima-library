using Bunit;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Plugins > Metadata sources (Developer tools spec WP20): built-in and plugin sources side by side with what each supplies,
/// the restart note, and a comparison of two sources in the Engine's words.
/// </summary>
public sealed class MetadataSourcesPanelTests : AsyncBunitContext
{
    private readonly DashboardSessionAccessor _session = new();
    private PluginSourcesResponse _sources = new();

    public MetadataSourcesPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetPluginSourcesAsync), _ => Task.FromResult<PluginSourcesResponse?>(_sources));
            stub.SetHandler(nameof(IEngineApiClient.ComparePluginSourcesAsync), _ => Task.FromResult<PluginSourceComparisonDto?>(new PluginSourceComparisonDto
            {
                From = "Alpha",
                To = "Beta",
                Both = ["movie artwork"],
                OnlyFrom = ["movie details"],
                OnlyTo = [],
                Keeps = "Everything already in your Library is kept. Nothing is matched again unless you ask.",
            }));
        });
        Services.AddSingleton(api);
        Services.AddSingleton(_session);
        Services.AddSingleton(new DeviceContextService(api));
        Services.AddSingleton(serviceProvider => new UIOrchestratorService(
            api,
            new UniverseStateContainer(),
            new ActiveProfileSessionService(
                serviceProvider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), api,
                authenticationStateProvider: new SessionOnlyAuthentication(), dashboardSession: _session),
            new ConfigurationBuilder().Build(),
            NullLogger<UIOrchestratorService>.Instance,
            dashboardSession: _session));
    }

    [Fact]
    public void EverySource_ShowsWhereItComesFromAndWhatItSupplies()
    {
        _sources = new PluginSourcesResponse
        {
            Sources =
            [
                new PluginSourceDto { Name = "alpha", DisplayName = "Alpha", Enabled = true, Origin = "official", Supplies = ["movie artwork", "movie details"], SendsDataTo = ["api.alpha.example"] },
                new PluginSourceDto { Name = "plugin_x_beta", DisplayName = "Beta", Enabled = false, Origin = "plugin", PluginId = "x", Supplies = ["movie artwork"] },
            ],
        };

        var cut = Render<MetadataSourcesPanel>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='metadata-source']").Count));
        var alpha = cut.Find("[data-source='alpha']");
        Assert.Contains("Built in", alpha.TextContent, StringComparison.Ordinal);
        Assert.Contains("movie artwork, movie details", alpha.TextContent, StringComparison.Ordinal);
        Assert.Contains("api.alpha.example", alpha.TextContent, StringComparison.Ordinal);
        var beta = cut.Find("[data-source='plugin_x_beta']");
        Assert.Contains("From a plugin", beta.TextContent, StringComparison.Ordinal);
        Assert.Contains("Off", beta.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='metadata-sources-restart']"));
    }

    [Fact]
    public void WhenTheEngineSaysARestartIsNeeded_ThePanelSaysSo()
    {
        _sources = new PluginSourcesResponse { RestartNeeded = true };

        var cut = Render<MetadataSourcesPanel>();

        cut.WaitForAssertion(() => Assert.Contains("Restart the Engine", cut.Find("[data-testid='metadata-sources-restart']").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void ComparingTwoSources_ShowsWhatIsSharedAndWhatIsNot()
    {
        _sources = new PluginSourcesResponse
        {
            Sources =
            [
                new PluginSourceDto { Name = "alpha", DisplayName = "Alpha", Enabled = true, Origin = "official" },
                new PluginSourceDto { Name = "beta", DisplayName = "Beta", Enabled = true, Origin = "official" },
            ],
        };
        var cut = Render<MetadataSourcesPanel>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='metadata-sources-compare']")));

        var selects = cut.FindAll("[data-testid='metadata-sources-compare'] select");
        selects[0].Change("alpha");
        selects[1].Change("beta");
        cut.Find("[data-testid='metadata-sources-compare-go']").Click();

        cut.WaitForAssertion(() =>
        {
            var result = cut.Find("[data-testid='metadata-sources-result']").TextContent;
            Assert.Contains("Both supply: movie artwork", result, StringComparison.Ordinal);
            Assert.Contains("Only Alpha supplies: movie details", result, StringComparison.Ordinal);
            Assert.Contains("Only Beta supplies: nothing", result, StringComparison.Ordinal);
            Assert.Contains("kept", result, StringComparison.Ordinal);
        });
    }

    /// <summary>The active person comes from the Dashboard session; the cookie principal adds nothing.</summary>
    private sealed class SessionOnlyAuthentication : Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider
    {
        public override Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(
                new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
    }
}
