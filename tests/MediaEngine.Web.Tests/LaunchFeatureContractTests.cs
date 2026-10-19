using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Tests;

public sealed class LaunchFeatureContractTests : IDisposable
{
    public LaunchFeatureContractTests() => SettingsNav.ConfigureEnvironment(productionMode: false);

    public void Dispose() => SettingsNav.ConfigureEnvironment(productionMode: false);

    [Fact]
    public void EveryLiveSettingsSurfaceMeetsTheFivePartLaunchContract()
    {
        var incompleteLive = SettingsNav.AllItems
            .Where(item => item.Status == SettingsStatusKind.Live)
            .Where(item => !SettingsNav.MeetsLaunchContract(item.Value))
            .Select(item => item.Label)
            .ToArray();

        Assert.Empty(incompleteLive);
    }

    [Fact]
    public void ProductionHidesPartialExperimentalPlannedAndPlaceholderSurfaces()
    {
        SettingsNav.ConfigureEnvironment(productionMode: true);

        Assert.True(SettingsNav.IsVisible(SettingsSection.AdminOverview, true));
        Assert.True(SettingsNav.IsVisible(SettingsSection.Delivery, true));
        Assert.True(SettingsNav.IsVisible(SettingsSection.Access, true));
        Assert.False(SettingsNav.IsVisible(SettingsSection.Plugins, true));
        Assert.False(SettingsNav.IsVisible(SettingsSection.DevHarness, true));
        Assert.False(SettingsNav.IsVisible(SettingsSection.ProviderTester, true));

        var directRoute = SettingsNav.ResolveRoute("plugins", true);
        Assert.False(directRoute.RequestedSectionAllowed);
        Assert.NotEqual(SettingsSection.Plugins, directRoute.Section);
    }
}
