using Bunit;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Contracts.System;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// <c>/settings/plugins?plugin={id}</c> opens the Plugins page at that plugin's card (the Fandom lore plugin and the backup
/// destinations link here); an id that is not installed opens the page as usual.
/// </summary>
public sealed partial class UiShellRenderTests
{
    private void UseInstalledPlugins(params string[] ids)
    {
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetDevelopmentToolsAsync), _ =>
                Task.FromResult<DevelopmentToolsResponse?>(new DevelopmentToolsResponse(EnrichmentTester: false, DevelopmentData: false)));
            stub.SetHandler(nameof(IEngineApiClient.GetPluginsAsync), _ =>
                Task.FromResult<IReadOnlyList<PluginSummaryResponse>>(
                    ids.Select(id => new PluginSummaryResponse { Id = id, Name = "Plugin " + id, Description = "Does something useful." }).ToList()));
        });
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddMemoryCache();
        Services.AddScoped<ProviderCatalogueService>();
        Services.AddScoped<MediaEngine.Web.Services.Settings.ApprovalFlow>();
    }

    [Fact]
    public void SettingsPage_PluginsAddressWithAKnownPluginId_RingsAndOpensThatPluginsCard()
    {
        UseInstalledPlugins("tuvima.alpha", "tuvima.fandom-lore");
        Services.GetRequiredService<NavigationManager>().NavigateTo("/settings/plugins?plugin=tuvima.fandom-lore");

        var cut = Render<Settings>(parameters => parameters.Add(page => page.Section, "plugins"));

        cut.WaitForAssertion(() =>
        {
            var focused = Assert.Single(cut.FindAll("[data-focused=true]"));
            Assert.Equal("plugin-card-tuvima-fandom-lore", focused.Id);
        });
    }

    [Fact]
    public void SettingsPage_PluginsAddressWithAnUnknownPluginId_OpensThePluginsPageNormally()
    {
        UseInstalledPlugins("tuvima.alpha");
        Services.GetRequiredService<NavigationManager>().NavigateTo("/settings/plugins?plugin=no.such.plugin");

        var cut = Render<Settings>(parameters => parameters.Add(page => page.Section, "plugins"));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-state-card=plugin]")));
        Assert.Empty(cut.FindAll("[data-focused]"));
        Assert.DoesNotContain("Configure Plugin", cut.Markup, StringComparison.Ordinal);
    }
}
