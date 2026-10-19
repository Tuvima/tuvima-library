using System.Text.Json;
using MediaEngine.Api.Services.Networking;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The two addresses a person types into a Tuvima app (Settings revamp D24). Any signed-in person may read them, so the answer must
/// carry only those two addresses and nothing else from the network settings.
/// </summary>
public sealed class NetworkAppAddressesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tuvima-app-addresses-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigurationDirectoryLoader _configuration;
    private readonly FixedEnvironment _environment = new();
    private readonly NetworkRuntimeState _runtime = new();

    public NetworkAppAddressesTests()
    {
        Directory.CreateDirectory(_directory);
        _configuration = new ConfigurationDirectoryLoader(_directory);
        _environment.Addresses.Add(new NetworkAddressDto { InterfaceId = "eth0", InterfaceName = "Ethernet", Address = "192.168.1.20", AddressFamily = "ipv4" });
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private NetworkAppAddressesDto Read() =>
        new NetworkStatusService(_configuration, _environment, new UnusedTopology(), _runtime, TimeProvider.System).GetAppAddresses();

    [Fact]
    public void AtHome_IsTheDetectedAddressWithTheDashboardPort_AndThereIsNoOutsideAddressWhileLocalOnly()
    {
        var addresses = Read();

        Assert.Equal("http://192.168.1.20:5016", addresses.AtHome);
        Assert.Null(addresses.AwayFromHome);
    }

    [Fact]
    public void AtHome_FollowsTheConfiguredPort()
    {
        _configuration.SaveNetwork(new NetworkSettings { Local = { Port = 6000 } });

        Assert.Equal("http://192.168.1.20:6000", Read().AtHome);
    }

    [Fact]
    public void AtHome_IsNullWhenNoUsableAddressIsFound()
    {
        _environment.Addresses.Clear();

        var addresses = Read();

        Assert.Null(addresses.AtHome);
        Assert.Null(addresses.AwayFromHome);
    }

    [Fact]
    public void AwayFromHome_IsThePublicHostnameAsAnOrigin_OnceAReverseProxyPathIsChosen()
    {
        _configuration.SaveNetwork(new NetworkSettings
        {
            Remote = { ConnectionMode = NetworkConnectionModes.Custom, PublicHostname = "https://tuvima.example.com/" },
        });

        Assert.Equal("https://tuvima.example.com", Read().AwayFromHome);
    }

    [Fact]
    public void AwayFromHome_IsTheTailnetAddress_InTailscaleMode()
    {
        _configuration.SaveNetwork(new NetworkSettings { Remote = { ConnectionMode = NetworkConnectionModes.Tailscale } });
        _runtime.RecordRemoteProvider(new RemoteProviderSnapshot(
            "tailscale", "Tailscale", RemoteProviderState.Connected, "https://host.tailnet.ts.net/", "Connected.", SecureHttps: true));

        Assert.Equal("https://host.tailnet.ts.net", Read().AwayFromHome);
    }

    [Fact]
    public void TheAnswer_CarriesNothingButTheTwoAddresses()
    {
        _configuration.SaveNetwork(new NetworkSettings
        {
            Remote = { ConnectionMode = NetworkConnectionModes.Custom, PublicHostname = "https://tuvima.example.com", TrustedProxies = ["10.0.0.1"] },
        });

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Read()));

        Assert.Equal(["at_home", "away_from_home"], json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.DoesNotContain("10.0.0.1", json.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    private sealed class FixedEnvironment : INetworkEnvironmentService
    {
        public List<NetworkAddressDto> Addresses { get; } = [];

        public IReadOnlyList<NetworkAddressDto> GetUsableAddresses(bool includeIpv6) => Addresses;
    }

    // Reading the app addresses must not need the topology probe that the full status does.
    private sealed class UnusedTopology : INetworkTopologyService
    {
        public NetworkTopologySnapshot GetSnapshot() => throw new InvalidOperationException("The app addresses do not read the network topology.");
    }
}
