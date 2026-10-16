using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
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
    public async Task ResponsiveServiceDoesNotAcceptUnknownNativeClassFromBrowserInput()
    {
        var service = new DeviceContextService(EngineApiClientStub.CreateDefault());

        await service.InitialiseAsync("unknown-native-class");

        Assert.Equal("web", service.DeviceClass);
        Assert.Equal("web", service.Settings.DeviceClass);
    }

    [Theory]
    [InlineData("mobile", "phone", true, "mobile")]
    [InlineData("web", "tablet", true, "web")]
    [InlineData("web", "desktop", false, "web")]
    [InlineData("television", "tv", false, "television")]
    public async Task FormFactorMapsToEngineSettingsKey(string deviceClass, string formFactor, bool touch, string engineKey)
    {
        var requestedKeys = new List<string>();
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(
            nameof(IEngineApiClient.GetResolvedUISettingsAsync),
            args =>
            {
                requestedKeys.Add((string)args![0]!);
                return Task.FromResult<ResolvedUISettingsViewModel?>(null);
            }));
        var service = new DeviceContextService(api);

        await service.InitialiseAsync(new DeviceContextSnapshot(deviceClass, formFactor, touch));

        Assert.Equal(formFactor, service.FormFactor);
        Assert.Equal(engineKey, service.DeviceClass);
        Assert.Equal(touch, service.IsTouch);
        Assert.Equal([engineKey], requestedKeys);
        Assert.Equal(formFactor == "phone", service.IsPhone);
        Assert.Equal(formFactor == "tablet", service.IsTablet);
        Assert.Equal(formFactor == "desktop", service.IsDesktop);
        Assert.Equal(formFactor == "tv", service.IsTv);
    }

    [Fact]
    public async Task TabletReportsTouchWithoutBeingMobile()
    {
        var service = new DeviceContextService(EngineApiClientStub.CreateDefault());

        await service.InitialiseAsync(new DeviceContextSnapshot("web", "tablet", true));

        Assert.True(service.IsTouch);
        Assert.True(service.IsTablet);
        Assert.False(service.IsMobile);
        Assert.False(service.IsPhone);
    }

    [Fact]
    public async Task SnapshotCarriesTheTwoPointerSignalsAndPublishesWhenOnlyThePointerChanges()
    {
        var service = new DeviceContextService(EngineApiClientStub.CreateDefault());
        await service.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var changes = 0;
        service.OnChanged += () => changes++;

        Assert.True(service.HasFinePointer);
        Assert.True(service.CanHover);
        Assert.True(service.CanEdit);

        await service.SwitchDeviceAsync(new DeviceContextSnapshot("web", "desktop", true, FinePointer: false, Hover: false));

        Assert.False(service.HasFinePointer);
        Assert.False(service.CanHover);
        Assert.False(service.CanEdit);
        Assert.True(changes >= 1);
    }

    [Theory]
    [InlineData("native-app")]
    [InlineData("kiosk")]
    public async Task UnrecognisedFormFactorStaysUnknownInsteadOfBecomingADesktop(string formFactor)
    {
        var service = new DeviceContextService(EngineApiClientStub.CreateDefault());

        await service.InitialiseAsync(new DeviceContextSnapshot("web", formFactor, false, FinePointer: true, Hover: true));

        Assert.Equal("unknown", service.FormFactor);
        Assert.False(service.IsDesktop);
        Assert.False(service.IsPhone);
        Assert.False(service.IsTablet);
        Assert.False(service.CanEdit);
    }

    [Fact]
    public async Task TvSnapshotKeepsTelevisionClass()
    {
        var service = new DeviceContextService(EngineApiClientStub.CreateDefault());

        await service.InitialiseAsync(new DeviceContextSnapshot("television", "tv", false));
        await service.SwitchDeviceAsync(new DeviceContextSnapshot("television", "tv", false));

        Assert.True(service.IsTv);
        Assert.True(service.IsTelevision);
        Assert.Equal("television", service.DeviceClass);
    }

    [Fact]
    public async Task SwitchingDesktopToTabletPublishesEvenWhenEngineKeyIsUnchanged()
    {
        var service = new DeviceContextService(EngineApiClientStub.CreateDefault());
        var changes = 0;
        await service.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false));
        service.OnChanged += () => changes++;

        await service.SwitchDeviceAsync(new DeviceContextSnapshot("web", "tablet", true));

        Assert.Equal("tablet", service.FormFactor);
        Assert.True(changes >= 1);
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
