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

        Assert.Equal(["overview", "local", "remote", "advanced"], subsections.Select(item => item.Slug));
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
        Assert.Contains("LibraryFoldersPanel Embedded=\"true\"", media, StringComparison.Ordinal);
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

        Assert.Contains("Not set up — Default", remote, StringComparison.Ordinal);
        Assert.Contains("Tailscale Serve", remote, StringComparison.Ordinal);
        Assert.Contains("HTTPS reverse proxy", remote, StringComparison.Ordinal);
        Assert.Contains("Test remote access", remote, StringComparison.Ordinal);
        Assert.Contains("TestRemoteNetworkAsync", remote, StringComparison.Ordinal);
        Assert.DoesNotContain("secure-provider", remote, StringComparison.Ordinal);
        Assert.DoesNotContain("Automatic Router Configuration", remote, StringComparison.Ordinal);
        Assert.Contains("Port Forwarding &amp; Router Mapping", advanced, StringComparison.Ordinal);
        Assert.Contains("PCP, NAT-PMP, and UPnP", advanced, StringComparison.Ordinal);
        Assert.Contains("ManualPortForwardingDialog", advanced, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDevicesPageListsPairedDevicesAndNoNetworkPageCarriesTheConnectAnAppCard()
    {
        var devices = Read(@"src/MediaEngine.Web/Components/Settings/PairedDevicesSection.razor");
        var host = Read(@"src/MediaEngine.Web/Components/Settings/NetworkRemoteAccessSettings.razor");
        var account = Read(@"src/MediaEngine.Web/Components/Settings/SecuritySettingsPage.razor");
        var page = Read(@"src/MediaEngine.Web/Components/Settings/DevicesTab.razor");

        Assert.Contains("<AppTable", devices, StringComparison.Ordinal);
        Assert.Contains("RevokeManagedDeviceAsync", devices, StringComparison.Ordinal);
        Assert.Contains("Sign this device out?", devices, StringComparison.Ordinal);
        Assert.DoesNotContain("<PairedDevicesSection", account, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppsAndDevicesPanel", host, StringComparison.Ordinal);
        Assert.DoesNotContain("AppsAndDevicesPanel", page, StringComparison.Ordinal);
        Assert.Contains("<PairedDevicesSection", page, StringComparison.Ordinal);
        Assert.Contains("<SignedInSessionsSection", page, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "MediaEngine.Web", "Components", "Settings", "AppsAndDevicesPanel.razor"))));
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
    public void RemoteAccessHasNoConnectionRuleChoiceOrOutsideChecklist()
    {
        var remote = Read(@"src/MediaEngine.Web/Components/Settings/RemoteAccessSettingsPanel.razor");

        Assert.Contains("How the internet reaches Library", remote, StringComparison.Ordinal);
        Assert.Contains("Test remote access", remote, StringComparison.Ordinal);
        Assert.DoesNotContain("Who can connect", remote, StringComparison.Ordinal);
        Assert.DoesNotContain("Anywhere", remote, StringComparison.Ordinal);
        Assert.DoesNotContain("network-checklist", remote, StringComparison.Ordinal);
        Assert.DoesNotContain("Security Check", remote, StringComparison.Ordinal);
    }

    [Fact]
    public void NoNetworkSettingsPanelMentionsTheRemovedRuleOrAppSwitch()
    {
        foreach (var file in new[]
        {
            "NetworkOverviewPanel.razor", "RemoteAccessSettingsPanel.razor", "AdvancedNetworkSettingsPanel.razor",
        })
        {
            var source = Read($@"src/MediaEngine.Web/Components/Settings/{file}");

            Assert.DoesNotContain("Who can connect", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("WhoCanConnect", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Anywhere", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Allow apps", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RemoteAccessHasNoSecondOnOffSwitchAndOverviewShowsConnectionStatusOnly()
    {
        var advanced = Read(@"src/MediaEngine.Web/Components/Settings/AdvancedNetworkSettingsPanel.razor");
        var overview = Read(@"src/MediaEngine.Web/Components/Settings/NetworkOverviewPanel.razor");

        Assert.DoesNotContain("Enable direct remote access", advanced, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.Remote.PublicHostname", advanced, StringComparison.Ordinal);
        var host = Read(@"src/MediaEngine.Web/Components/Settings/NetworkRemoteAccessSettings.razor");

        Assert.Contains("Test Connection", overview, StringComparison.Ordinal);
        Assert.Contains("Connection Status", overview, StringComparison.Ordinal);
        // The server addresses and uptime moved off the Network overview (the System summary shows them), and the
        // streaming limits moved to Streaming; Network has no Streaming page.
        Assert.DoesNotContain("Server information", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("Server Address", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("Who can connect", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("StreamingLimits", host, StringComparison.Ordinal);
        Assert.DoesNotContain("\"streaming\"", host, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupServerStageNoLongerAsksToKeepTheDashboardPrivate()
    {
        var stage = Read(@"src/MediaEngine.Web/Components/Setup/SetupServerStage.razor");

        Assert.DoesNotContain("private network until the first administrator", stage, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) => File.ReadAllText(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath)));
}
