using Bunit;
using MediaEngine.Contracts.System;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Developer Tools on a phone (Settings alignment D13): an administrator opens each tester at phone size and sees the tool itself,
/// never the "use a larger screen" page. The static half is <see cref="SettingsPhoneGateGuardrailTests"/>.
/// </summary>
public sealed partial class UiShellRenderTests
{
    private static readonly string[] LargerScreenWording =
    [
        "larger screen",
        "desktop or tablet",
        "computer or tablet",
        "withheld from mobile",
    ];

    [Theory]
    [InlineData("developer", "[data-temporary-tool='provider-tester']")]
    [InlineData("enrichment-tester", "[data-temporary-tool='enrichment-tester']")]
    [InlineData("development-data", "[data-temporary-page='development-tools']")]
    public async Task DeveloperTools_OnAPhone_ShowTheToolAndNoLargerScreenMessage(string address, string toolSelector)
    {
        UseDevelopmentTools(new DevelopmentToolsResponse(EnrichmentTester: true, DevelopmentData: true));
        // The Enrichment Tester reads through the circuit's own HTTP client factory; it only draws its form here and sends nothing.
        Services.AddHttpClient("EngineApi", client => client.BaseAddress = new Uri("http://engine.test"));
        Services.AddScoped<DashboardCircuitHttpClientFactory>();
        await Services.GetRequiredService<DeviceContextService>().SwitchDeviceAsync(PhoneDevice);

        var cut = OpenSettingsAt(address);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(toolSelector)));
        foreach (var phrase in LargerScreenWording)
        {
            Assert.DoesNotContain(phrase, cut.Markup, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task DeveloperTools_OnAPhone_AreListedInThePhoneSettingsSelector()
    {
        UseDevelopmentTools(new DevelopmentToolsResponse(EnrichmentTester: true, DevelopmentData: true));
        await Services.GetRequiredService<DeviceContextService>().SwitchDeviceAsync(PhoneDevice);

        var cut = OpenSettingsAt("developer");

        cut.WaitForAssertion(() => Assert.Contains("/settings/developer", PhoneSelectorRoutes(cut)));
    }
}
