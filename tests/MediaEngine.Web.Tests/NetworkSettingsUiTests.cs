using System.Net;
using MediaEngine.Domain.Configuration;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace MediaEngine.Web.Tests;

public sealed class NetworkSettingsUiTests
{
    [Fact]
    public void NetworkNavigationUsesCanonicalSectionRoutes()
    {
        var subsections = SettingsNav.GetSubsections(SettingsSection.Network).ToArray();

        Assert.Equal(["overview", "local", "remote", "apps", "streaming", "advanced"], subsections.Select(item => item.Slug));
        Assert.Equal("/settings/network/overview", SettingsNav.RouteFor(SettingsSection.Network));
        Assert.Equal("/settings/network/remote", SettingsNav.RouteFor(SettingsSection.Network, "remote"));
    }

    [Fact]
    public void FirstRunUsesTheVersionedSetupWorkflow()
    {
        var setup = Read(@"src/MediaEngine.Web/Components/Pages/SetupPage.razor");
        var media = Read(@"src/MediaEngine.Web/Components/Setup/SetupMediaStage.razor");

        Assert.Contains("@page \"/setup\"", setup, StringComparison.Ordinal);
        Assert.Contains("UploadBackupAsync", setup, StringComparison.Ordinal);
        Assert.Contains("SetupMediaStage", setup, StringComparison.Ordinal);
        Assert.Contains("AddLibraryWizard Embedded=\"true\"", media, StringComparison.Ordinal);
        Assert.Contains("SetupWorkflow.StepKeys", setup, StringComparison.Ordinal);
    }

    [Fact]
    public void RetiredNetworkWizardFlagAndSetupCompletionFieldAreRemoved()
    {
        var layout = Read(@"src/MediaEngine.Web/Shared/MainLayout.razor");
        var appSettings = Read(@"src/MediaEngine.Web/appsettings.json");
        var network = Read(@"src/MediaEngine.Contracts/Settings/NetworkSettingsContracts.cs");

        Assert.DoesNotContain("NetworkSetupWizardEnabled", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("NetworkSetupWizardEnabled", appSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("setup_completed", network, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteAccessOffersOnlySupportedSecurePathsAndKeepsRouterToolsAdvanced()
    {
        var remote = Read(@"src/MediaEngine.Web/Components/Settings/RemoteAccessSettingsPanel.razor");
        var advanced = Read(@"src/MediaEngine.Web/Components/Settings/AdvancedNetworkSettingsPanel.razor");

        Assert.Contains("Local network only — Default", remote, StringComparison.Ordinal);
        Assert.Contains("Tailscale Serve", remote, StringComparison.Ordinal);
        Assert.Contains("HTTPS reverse proxy", remote, StringComparison.Ordinal);
        Assert.Contains("GetRemoteAccessReadinessAsync", remote, StringComparison.Ordinal);
        Assert.DoesNotContain("secure-provider", remote, StringComparison.Ordinal);
        Assert.DoesNotContain("Automatic Router Configuration", remote, StringComparison.Ordinal);
        Assert.Contains("Port Forwarding &amp; Router Mapping", advanced, StringComparison.Ordinal);
        Assert.Contains("PCP, NAT-PMP, and UPnP", advanced, StringComparison.Ordinal);
        Assert.Contains("ManualPortForwardingDialog", advanced, StringComparison.Ordinal);
    }

    [Fact]
    public void AppsAndDevicesPanelGatesTheSwitchOnAnywhereAndListsPairedDevices()
    {
        var panel = Read(@"src/MediaEngine.Web/Components/Settings/AppsAndDevicesPanel.razor");
        var devices = Read(@"src/MediaEngine.Web/Components/Settings/PairedDevicesSection.razor");
        var host = Read(@"src/MediaEngine.Web/Components/Settings/NetworkRemoteAccessSettings.razor");
        var account = Read(@"src/MediaEngine.Web/Components/Settings/AccountSettingsTab.razor");

        Assert.Contains("Disabled=\"@(!AllowsInternet || _saving)\"", panel, StringComparison.Ordinal);
        Assert.Contains("Settings.WhoCanConnect == \"anywhere\"", panel, StringComparison.Ordinal);
        Assert.Contains("Apps connect over the internet address. Set Who can connect to Anywhere first.", panel, StringComparison.Ordinal);
        Assert.Contains("Address to type in the app", panel, StringComparison.Ordinal);
        Assert.Contains("TestRemoteNetworkAsync", panel, StringComparison.Ordinal);
        Assert.Contains("secure_account_first", panel, StringComparison.Ordinal);
        Assert.Contains("<PairedDevicesSection", panel, StringComparison.Ordinal);
        Assert.Contains("<AppTable", devices, StringComparison.Ordinal);
        Assert.Contains("RevokeManagedDeviceAsync", devices, StringComparison.Ordinal);
        Assert.Contains("Revoke this device?", devices, StringComparison.Ordinal);
        Assert.Contains("<PairedDevicesSection", account, StringComparison.Ordinal);
        Assert.Contains("<AppsAndDevicesPanel", host, StringComparison.Ordinal);
        Assert.Contains("Settings &gt; Network &gt; Apps &amp; devices", account, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionTestDialogOwnsItsPortalRenderedLayoutAndCannotOverflowHorizontally()
    {
        var dialog = Read(@"src/MediaEngine.Web/Components/Settings/NetworkTestDialog.razor");
        var dialogStyles = Read(@"src/MediaEngine.Web/Components/Settings/NetworkTestDialog.razor.css");
        var dialogHost = Read(@"src/MediaEngine.Web/Components/Shared/AppDialog.razor");
        var shellStyles = Read(@"src/MediaEngine.Web/Components/Shared/AppDialogShell.razor.css");
        var appStyles = Read(@"src/MediaEngine.Web/wwwroot/app.css");
        var settingsStyles = Read(@"src/MediaEngine.Web/Components/Settings/NetworkRemoteAccessSettings.razor.css");

        Assert.Contains("network-test-dialog__content", dialog, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: auto minmax(0, 1fr)", dialogStyles, StringComparison.Ordinal);
        Assert.Contains("overflow-wrap: anywhere", dialogStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("network-test-dialog__check", settingsStyles, StringComparison.Ordinal);
        Assert.Contains("width: min(100%, 760px)", shellStyles, StringComparison.Ordinal);
        Assert.Contains("overflow-x: hidden", shellStyles, StringComparison.Ordinal);
        Assert.Contains("app-dialog-host", dialogHost, StringComparison.Ordinal);
        Assert.Contains(".tl-dialog.app-dialog-host > .tl-dialog-content", appStyles, StringComparison.Ordinal);
        Assert.Contains("background: transparent !important", appStyles, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardHonorsForwardedHeadersOnlyFromConfiguredProxyAddresses()
    {
        var source = Read(@"src/MediaEngine.Web/Program.cs");

        Assert.Contains("ForwardedHeaderConfiguration.Configure", source, StringComparison.Ordinal);
        Assert.DoesNotContain("app.UseForwardedHeaders()", source, StringComparison.Ordinal);
        Assert.Contains("app.UseForwardedHeadersOnProxyPort(proxyPort)", source, StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("app.UseForwardedHeadersOnProxyPort(proxyPort)", StringComparison.Ordinal)
            < source.IndexOf("app.UseHsts()", StringComparison.Ordinal));
    }

    [Fact]
    public void ForwardedHeaderConfigurationSupportsExactDockerCidrAndAllowedHosts()
    {
        var options = new ForwardedHeadersOptions();
        ForwardedHeaderConfiguration.Configure(options, new RemoteNetworkSettings
        {
            PublicHostname = "https://library.example.test",
            TrustedProxies = ["172.20.0.2"],
            TrustedProxyNetworks = ["172.21.0.0/24"],
        }, "https://tuvima.example.ts.net");

        Assert.Equal(1, options.ForwardLimit);
        Assert.Contains(IPAddress.Parse("172.20.0.2"), options.KnownProxies);
        Assert.Contains(IPAddress.Parse("172.20.0.2").MapToIPv6(), options.KnownProxies);
        Assert.Contains(System.Net.IPNetwork.Parse("172.21.0.0/24"), options.KnownIPNetworks);
        Assert.Contains(System.Net.IPNetwork.Parse("::ffff:172.21.0.0/120"), options.KnownIPNetworks);
        Assert.Contains("library.example.test", options.AllowedHosts);
        Assert.Contains("tuvima.example.ts.net", options.AllowedHosts);
    }

    [Fact]
    public void WhoCanConnectOffersThreePlainChoicesAndAnywhereWaitsForTheChecklist()
    {
        var remote = Read(@"src/MediaEngine.Web/Components/Settings/RemoteAccessSettingsPanel.razor");

        Assert.Contains("Only a browser on this computer.", remote, StringComparison.Ordinal);
        Assert.Contains("Devices on your home Wi-Fi or network.", remote, StringComparison.Ordinal);
        Assert.Contains("Also from the internet, through a secure address.", remote, StringComparison.Ordinal);
        Assert.Contains("!AnywhereReady", remote, StringComparison.Ordinal);
        Assert.Contains("Disabled=\"@(disabled && Settings.WhoCanConnect != value)\"", remote, StringComparison.Ordinal);
        Assert.Contains("Fix: @item.FixLabel", remote, StringComparison.Ordinal);
        Assert.Contains("Test remote access", remote, StringComparison.Ordinal);
        Assert.Contains("ThisComputerRequests.SecureAccountFirstCode", remote, StringComparison.Ordinal);
        Assert.DoesNotContain("Security Check", remote, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteAccessHasNoSecondOnOffSwitchAndOverviewShowsWhoCanConnect()
    {
        var advanced = Read(@"src/MediaEngine.Web/Components/Settings/AdvancedNetworkSettingsPanel.razor");
        var overview = Read(@"src/MediaEngine.Web/Components/Settings/NetworkOverviewPanel.razor");

        Assert.DoesNotContain("Enable direct remote access", advanced, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.Remote.PublicHostname", advanced, StringComparison.Ordinal);
        Assert.Contains("WhoCanConnectSummary", overview, StringComparison.Ordinal);
    }

    [Fact]
    public void ChecklistUsesPlainLabelsAndFixLinks()
    {
        var readiness = new MediaEngine.Contracts.Settings.RemoteAccessReadinessDto
        {
            Checks =
            [
                new() { Key = "authentication", Status = "failed", Detail = "Save recovery codes for an administrator before opening Tuvima to the internet." },
                new() { Key = "public-address", Status = "failed", Detail = "Set the public address." },
                new() { Key = "https-endpoint", Status = "passed", Detail = "Verified." },
            ],
        };

        var items = RemoteAccessChecklist.Build(readiness);

        Assert.Equal(["An administrator can sign in", "Recovery codes saved", "Public address set", "Secure connection"], items.Select(i => i.Label));
        Assert.True(items[0].Passed);
        Assert.False(items[1].Passed);
        Assert.Equal(RemoteAccessChecklist.AccountHref, items[1].FixHref);
        Assert.Equal(RemoteAccessChecklist.PublicAddressHref, items[2].FixHref);
        Assert.True(items[3].Passed);

        var noAdmin = new MediaEngine.Contracts.Settings.RemoteAccessReadinessDto
        {
            Checks = [new() { Key = "authentication", Status = "failed", Detail = "No administrator can sign in yet. Add a password or passkey to an administrator account." }],
        };
        var noAdminItems = RemoteAccessChecklist.Build(noAdmin);
        Assert.False(noAdminItems[0].Passed);
        Assert.Equal(RemoteAccessChecklist.UsersHref, noAdminItems[0].FixHref);
        Assert.Equal("This computer only", RemoteAccessChecklist.WhoCanConnectSummary("this_computer"));
    }

    private static string Read(string relativePath) => File.ReadAllText(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath)));
}
