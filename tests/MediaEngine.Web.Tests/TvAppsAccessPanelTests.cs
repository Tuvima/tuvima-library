using Bunit;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Settings > Network > Home network, "Allow Samsung and LG TV apps". The Samsung and LG TV apps are packaged web apps, so the
/// Dashboard's app door only answers them while this switch is on. It is off by default, saves at once like its neighbours, and says
/// plainly why it exists. Nothing here uses a toast.
/// </summary>
public sealed class TvAppsAccessPanelTests : AsyncBunitContext
{
    public TvAppsAccessPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
    }

    private IRenderedComponent<TvAppsAccessPanel> RenderPanel(NetworkSettingsDto settings, Func<Task>? onSave = null) =>
        Render<TvAppsAccessPanel>(p => p
            .Add(x => x.Settings, settings)
            .Add(x => x.SaveRequested, EventCallback.Factory.Create(this, onSave ?? (() => Task.CompletedTask))));

    [Fact]
    public void TheSwitchIsOffByDefault_AndExplainsWhyTheseTvAppsNeedIt()
    {
        var cut = RenderPanel(new NetworkSettingsDto());

        Assert.Contains("Allow Samsung and LG TV apps", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("run as web apps", cut.Markup, StringComparison.Ordinal);
        Assert.False(cut.Find("input[role='switch']").HasAttribute("checked"));
    }

    [Fact]
    public void TheSwitchShowsOn_WhenTheSavedSettingIsOn()
    {
        var settings = new NetworkSettingsDto { NativeAppAccess = { AllowBrowserApps = true } };

        var cut = RenderPanel(settings);

        Assert.True(cut.Find("input[role='switch']").HasAttribute("checked"));
    }

    [Fact]
    public void TurningTheSwitchOn_SetsTheSettingAndAsksForOneSave()
    {
        var settings = new NetworkSettingsDto();
        var saves = 0;
        var cut = RenderPanel(settings, () => { saves++; return Task.CompletedTask; });

        cut.Find("input[role='switch']").Change(true);

        Assert.True(settings.NativeAppAccess.AllowBrowserApps);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void TurningTheSwitchOff_ClearsTheSettingAndLeavesEveryOtherNetworkSettingAlone()
    {
        var settings = new NetworkSettingsDto { NativeAppAccess = { AllowBrowserApps = true } };
        settings.Local.ReachOtherDevices = false;
        settings.Remote.ConnectionMode = "tailscale";
        var cut = RenderPanel(settings);

        cut.Find("input[role='switch']").Change(false);

        Assert.False(settings.NativeAppAccess.AllowBrowserApps);
        Assert.False(settings.Local.ReachOtherDevices);
        Assert.Equal("tailscale", settings.Remote.ConnectionMode);
    }
}
