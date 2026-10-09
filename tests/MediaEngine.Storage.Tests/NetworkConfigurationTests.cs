using MediaEngine.Domain.Configuration;
using MediaEngine.Storage;
using MediaEngine.Storage.Configuration;

namespace MediaEngine.Storage.Tests;

public sealed class NetworkConfigurationTests
{
    [Fact]
    public void NewNetworkSettingsAreLocalOnlyAndDoNotAttemptRouterMapping()
    {
        var settings = new NetworkSettings();

        Assert.Equal("3.0", settings.SchemaVersion);
        Assert.False(settings.Remote.Enabled);
        Assert.Equal(NetworkConnectionModes.LocalOnly, settings.Remote.ConnectionMode);
        Assert.False(settings.Remote.AutomaticRouterConfiguration);
    }

    [Fact]
    public void NativeAppAccess_IsOffByDefault_AndNeedsRemoteAccessToBeSaved()
    {
        Assert.False(new NetworkSettings().NativeAppAccess.Enabled);

        var path = CreateTempDirectory();
        try
        {
            var loader = new ConfigurationDirectoryLoader(path);
            var withoutRemote = new NetworkSettings { NativeAppAccess = new NativeAppAccessSettings { Enabled = true } };
            var ex = Assert.Throws<ConfigValidationException>(() => loader.SaveNetwork(withoutRemote));
            Assert.Contains(ex.ValidationMessages, m => m.Contains("native_app_access", StringComparison.Ordinal));

            loader.SaveNetwork(new NetworkSettings
            {
                Remote = new RemoteNetworkSettings
                {
                    Enabled = true,
                    ConnectionMode = NetworkConnectionModes.Custom,
                    PublicHostname = "https://media.example.test",
                },
                NativeAppAccess = new NativeAppAccessSettings { Enabled = true },
            });
            Assert.True(loader.LoadNetwork().NativeAppAccess.Enabled);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void SaveNetwork_RoundTripsDesiredState()
    {
        var path = CreateTempDirectory();
        try
        {
            var loader = new ConfigurationDirectoryLoader(path);
            loader.SaveNetwork(new NetworkSettings
            {
                Local = new LocalNetworkSettings
                {
                    Port = 8096,
                    PreferredServerName = "tuvima-den",
                    DiscoveryEnabled = true,
                },
                Remote = new RemoteNetworkSettings
                {
                    Enabled = true,
                    ConnectionMode = NetworkConnectionModes.Custom,
                    PublicHostname = "https://media.example.test",
                },
                Streaming = new NetworkStreamingSettings
                {
                    RemoteQuality = RemoteStreamingQualities.Hd720,
                    ReservedUploadMbps = 12,
                },
            });

            var actual = loader.LoadNetwork();

            Assert.Equal(8096, actual.Local.Port);
            Assert.Equal("tuvima-den", actual.Local.PreferredServerName);
            Assert.Equal("https://media.example.test", actual.Remote.PublicHostname);
            Assert.Equal(RemoteStreamingQualities.Hd720, actual.Streaming.RemoteQuality);
            Assert.DoesNotContain("setup_completed", File.ReadAllText(Path.Combine(path, "network.json")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void SaveNetwork_RejectsUnsafePortAndInsecureCustomAddress()
    {
        var path = CreateTempDirectory();
        try
        {
            var loader = new ConfigurationDirectoryLoader(path);
            var settings = new NetworkSettings
            {
                Local = new LocalNetworkSettings { Port = 0 },
                Remote = new RemoteNetworkSettings
                {
                    Enabled = true,
                    ConnectionMode = NetworkConnectionModes.Custom,
                    PublicHostname = "http://media.example.test",
                    TrustedProxies = ["not-an-ip"],
                },
            };

            var exception = Assert.Throws<ConfigValidationException>(() => loader.SaveNetwork(settings));

            Assert.Contains("local.port", exception.Message, StringComparison.Ordinal);
            Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
            Assert.Contains("trusted_proxies", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void SaveNetwork_HardRejectsRemovedSecureProviderMode()
    {
        var path = CreateTempDirectory();
        try
        {
            var loader = new ConfigurationDirectoryLoader(path);
            var settings = new NetworkSettings();
            settings.Remote.ConnectionMode = "secure-provider";

            var exception = Assert.Throws<ConfigValidationException>(() => loader.SaveNetwork(settings));

            Assert.Contains("connection_mode is unsupported", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima-network-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
