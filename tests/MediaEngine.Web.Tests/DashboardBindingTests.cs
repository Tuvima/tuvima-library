using MediaEngine.Domain.Configuration;
using MediaEngine.Web.Services.Configuration;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Home network spec D8: where the Dashboard listens. On the home network by default; the "Reach from other devices" switch off binds it to
/// this computer again, whatever the hosting settings say. The Engine is not decided here and always stays local.
/// </summary>
public sealed class DashboardBindingTests
{
    private static NetworkSettings Network(bool reach = true, int port = 5016)
    {
        var network = new NetworkSettings();
        network.Local.ReachOtherDevices = reach;
        network.Local.Port = port;
        return network;
    }

    [Fact]
    public void ANewInstall_ListensOnTheHomeNetworkByDefault()
    {
        Assert.True(new NetworkSettings().Local.ReachOtherDevices);
        Assert.Equal("http://0.0.0.0:5016", DashboardBinding.Resolve(new NetworkSettings(), null, null, null, inContainer: false));
    }

    [Fact]
    public void WithTheSwitchOff_TheDashboardListensOnThisComputerOnly()
    {
        Assert.Equal("http://127.0.0.1:5016", DashboardBinding.Resolve(Network(reach: false), null, null, null, inContainer: false));
        Assert.Equal("http://127.0.0.1:6000", DashboardBinding.Resolve(Network(reach: false, port: 6000), "   ", null, null, inContainer: false));
    }

    [Theory]
    [InlineData("http://+:5016", "http://127.0.0.1:5016")]
    [InlineData("http://*:5016", "http://127.0.0.1:5016")]
    [InlineData("http://0.0.0.0:5016", "http://127.0.0.1:5016")]
    [InlineData("http://[::]:5016", "http://127.0.0.1:5016")]
    [InlineData("http://192.168.1.20:5016", "http://127.0.0.1:5016")]
    [InlineData("http://0.0.0.0:5016;https://localhost:7062", "http://127.0.0.1:5016;https://localhost:7062")]
    [InlineData("http://0.0.0.0:5016;http://+:5016", "http://127.0.0.1:5016")]
    [InlineData("http://localhost:5016", null)]
    [InlineData("http://127.0.0.1:5016", null)]
    [InlineData("http://[::1]:5016", null)]
    public void WithTheSwitchOff_ExplicitHostingAddressesAreNarrowedToThisComputer(string explicitUrls, string? expected)
    {
        Assert.Equal(expected, DashboardBinding.Resolve(Network(reach: false), explicitUrls, null, null, inContainer: false));
    }

    [Fact]
    public void WithTheSwitchOn_ExplicitHostingAddressesStayAuthoritative()
    {
        Assert.Null(DashboardBinding.Resolve(Network(), "http://+:5016", null, null, inContainer: false));
        Assert.Null(DashboardBinding.Resolve(Network(), "http://localhost:5016", null, null, inContainer: false));
    }

    [Fact]
    public void WithTheSwitchOff_ANetworkWideProxyListenerIsNarrowedToo()
    {
        var urls = DashboardBinding.Resolve(Network(reach: false), null, "http://0.0.0.0:5017", 5017, inContainer: false);

        Assert.Equal("http://127.0.0.1:5016;http://127.0.0.1:5017", urls);
    }

    [Fact]
    public void AProxyListenerIsAddedOnceAndOnlyWhenTheHostingAddressesLackIt()
    {
        Assert.Equal("http://+:5016;http://localhost:5017", DashboardBinding.Resolve(Network(), "http://+:5016", "http://localhost:5017", 5017, inContainer: false));
        Assert.Null(DashboardBinding.Resolve(Network(), "http://+:5016;http://localhost:5017", "http://localhost:5017", 5017, inContainer: false));
    }

    [Fact]
    public void AContainerStaysReachable_BecauseItsPublishedPortWouldStopWorkingOtherwise()
    {
        Assert.True(DashboardBinding.ListensOnNetwork(Network(reach: false), inContainer: true));
        Assert.Null(DashboardBinding.Resolve(Network(reach: false), "http://+:5016", null, null, inContainer: true));
        Assert.Equal("http://0.0.0.0:5016", DashboardBinding.Resolve(Network(reach: false), null, null, null, inContainer: true));
    }

    [Fact]
    public void TheEngineIsNeverPartOfTheDashboardsAddresses()
    {
        var all = new[]
        {
            DashboardBinding.Resolve(Network(), null, null, null, false),
            DashboardBinding.Resolve(Network(reach: false), null, null, null, false),
            DashboardBinding.Resolve(Network(reach: false), "http://+:5016", null, null, false),
        };

        Assert.All(all.OfType<string>(), urls => Assert.DoesNotContain("61495", urls, StringComparison.Ordinal));
    }

    [Fact]
    public void TheLaunchersLeaveTheDashboardAddressToSettingsAndKeepTheEngineLocal()
    {
        var launcher = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "Start-TuvimaApp.ps1"));

        // The Dashboard is no longer forced to localhost: its address comes from config/network.json (the switch).
        Assert.DoesNotContain("$env:ASPNETCORE_URLS = \"http://localhost:5016\"", launcher, StringComparison.Ordinal);
        Assert.Contains("Remove-Item Env:ASPNETCORE_URLS", launcher, StringComparison.Ordinal);
        // The Engine keeps its own local-only addresses.
        Assert.Contains("$env:ASPNETCORE_URLS = \"https://localhost:61494;http://localhost:61495\"", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShippedNetworkConfigurationTurnsTheSwitchOnAndHasNotYetAskedAboutTheFirewall()
    {
        foreach (var file in new[] { "config/network.json", "docker/config/network.json" })
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), file.Replace('/', Path.DirectorySeparatorChar))));
            var local = document.RootElement.GetProperty("local");

            Assert.True(local.GetProperty("reach_other_devices").GetBoolean(), file);
            Assert.False(local.GetProperty("firewall_prompt_answered").GetBoolean(), file);
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
