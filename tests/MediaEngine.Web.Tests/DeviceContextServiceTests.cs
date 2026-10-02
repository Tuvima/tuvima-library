using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

public sealed class DeviceContextServiceTests
{
    [Fact]
    public async Task ResponsiveSwitchPublishesImmediatelyAndOlderSettingsCannotOverwriteLatestClass()
    {
        var webSettings = new TaskCompletionSource<ResolvedUISettingsViewModel?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mobileSettings = new TaskCompletionSource<ResolvedUISettingsViewModel?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(
            nameof(IEngineApiClient.GetResolvedUISettingsAsync),
            args =>
            {
                var deviceClass = (string)args![0]!;
                return deviceClass switch
                {
                    "web" => webSettings.Task,
                    "mobile" => mobileSettings.Task,
                    _ => Task.FromResult<ResolvedUISettingsViewModel?>(null),
                };
            }));
        var service = new DeviceContextService(api);
        var publishedClasses = new List<string>();
        service.OnChanged += () => publishedClasses.Add(service.DeviceClass);

        var switchToMobile = service.SwitchDeviceAsync("mobile");
        Assert.Equal("mobile", service.DeviceClass);
        Assert.Equal("mobile", service.Settings.DeviceClass);
        Assert.True(service.IsInitialised);
        Assert.Equal(["mobile"], publishedClasses);

        var switchBackToWeb = service.SwitchDeviceAsync("web");
        Assert.Equal("web", service.DeviceClass);
        Assert.Equal("web", service.Settings.DeviceClass);
        Assert.Equal(["mobile", "web"], publishedClasses);

        webSettings.SetResult(new ResolvedUISettingsViewModel { DeviceClass = "web", AccentColor = "web-settings" });
        await switchBackToWeb;
        mobileSettings.SetResult(new ResolvedUISettingsViewModel { DeviceClass = "mobile", AccentColor = "stale-mobile-settings" });
        await switchToMobile;

        Assert.Equal("web", service.DeviceClass);
        Assert.Equal("web", service.Settings.DeviceClass);
        Assert.Equal("web-settings", service.Settings.AccentColor);
        Assert.Equal(["mobile", "web", "web"], publishedClasses);
    }

    [Fact]
    public async Task ResponsiveServiceDoesNotAcceptNativeDeviceClassesFromBrowserInput()
    {
        var service = new DeviceContextService(EngineApiClientStub.CreateDefault());

        await service.InitialiseAsync("television");
        await service.SwitchDeviceAsync("automotive");

        Assert.Equal("web", service.DeviceClass);
        Assert.Equal("web", service.Settings.DeviceClass);
    }

    [Fact]
    public async Task ResponsiveSwitchDuringInitialSettingsLoadWins()
    {
        var webSettings = new TaskCompletionSource<ResolvedUISettingsViewModel?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mobileSettings = new TaskCompletionSource<ResolvedUISettingsViewModel?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(
            nameof(IEngineApiClient.GetResolvedUISettingsAsync),
            args =>
            {
                var deviceClass = (string)args![0]!;
                return deviceClass switch
                {
                    "web" => webSettings.Task,
                    "mobile" => mobileSettings.Task,
                    _ => Task.FromResult<ResolvedUISettingsViewModel?>(null),
                };
            }));
        var service = new DeviceContextService(api);

        var initialise = service.InitialiseAsync("web");
        var switchToMobile = service.SwitchDeviceAsync("mobile");
        Assert.Equal("mobile", service.DeviceClass);
        mobileSettings.SetResult(new ResolvedUISettingsViewModel { DeviceClass = "mobile", AccentColor = "mobile-settings" });
        await switchToMobile;
        webSettings.SetResult(new ResolvedUISettingsViewModel { DeviceClass = "web", AccentColor = "stale-web-settings" });
        await initialise;

        Assert.Equal("mobile", service.DeviceClass);
        Assert.Equal("mobile", service.Settings.DeviceClass);
        Assert.Equal("mobile-settings", service.Settings.AccentColor);
    }
}
