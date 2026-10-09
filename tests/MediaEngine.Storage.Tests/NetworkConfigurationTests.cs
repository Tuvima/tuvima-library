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
        Assert.Equal(WhoCanConnectModes.HomeNetwork, settings.WhoCanConnect);
        Assert.False(settings.AllowsInternet);
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
                WhoCanConnect = WhoCanConnectModes.Anywhere,
                Remote = new RemoteNetworkSettings
                {
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
    public void SaveNetwork_RejectsUnknownWhoCanConnect()
    {
        var path = CreateTempDirectory();
        try
        {
            var loader = new ConfigurationDirectoryLoader(path);
            var ex = Assert.Throws<ConfigValidationException>(() =>
                loader.SaveNetwork(new NetworkSettings { WhoCanConnect = "everyone" }));
            Assert.Contains("who_can_connect", ex.Message, StringComparison.Ordinal);
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
                WhoCanConnect = WhoCanConnectModes.Anywhere,
                Local = new LocalNetworkSettings
                {
                    Port = 8096,
                    PreferredServerName = "tuvima-den",
                    DiscoveryEnabled = true,
                },
                Remote = new RemoteNetworkSettings
                {
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
                WhoCanConnect = WhoCanConnectModes.Anywhere,
                Local = new LocalNetworkSettings { Port = 0 },
                Remote = new RemoteNetworkSettings
                {
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

    [Fact]
    public void ProxyPortAndAllowedHostnames_DefaultToOffAndEmpty()
    {
        var settings = new NetworkSettings();

        Assert.Null(settings.Remote.ProxyPort);
        Assert.Empty(settings.Local.AllowedHostnames);
    }

    [Fact]
    public void ProxyPortAndAllowedHostnames_RoundTrip()
    {
        var path = CreateTempDirectory();
        try
        {
            var loader = new ConfigurationDirectoryLoader(path);
            var settings = new NetworkSettings();
            settings.Remote.ProxyPort = 5017;
            settings.Local.AllowedHostnames = ["media.home.arpa", "nas"];
            loader.SaveNetwork(settings);

            var loaded = loader.LoadNetwork();
            Assert.Equal(5017, loaded.Remote.ProxyPort);
            Assert.Equal(["media.home.arpa", "nas"], loaded.Local.AllowedHostnames);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    [InlineData(5016)]
    public void ProxyPort_MustBeAValidPortDifferentFromTheMainPort(int port)
    {
        var path = CreateTempDirectory();
        try
        {
            var loader = new ConfigurationDirectoryLoader(path);
            var settings = new NetworkSettings();
            settings.Remote.ProxyPort = port;

            var exception = Assert.Throws<ConfigValidationException>(() => loader.SaveNetwork(settings));

            Assert.Contains("remote.proxy_port", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("host:5016")]
    [InlineData("a/b")]
    [InlineData("-bad.example")]
    [InlineData("")]
    public void AllowedHostnames_MustBePlainHostnames(string value)
    {
        var path = CreateTempDirectory();
        try
        {
            var loader = new ConfigurationDirectoryLoader(path);
            var settings = new NetworkSettings();
            settings.Local.AllowedHostnames = [value];

            var exception = Assert.Throws<ConfigValidationException>(() => loader.SaveNetwork(settings));

            Assert.Contains("local.allowed_hostnames", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void AllowedHostnames_AreCappedAtThirtyTwo()
    {
        var path = CreateTempDirectory();
        try
        {
            var loader = new ConfigurationDirectoryLoader(path);
            var settings = new NetworkSettings();
            settings.Local.AllowedHostnames = Enumerable.Range(0, 33).Select(i => $"host{i}.example").ToList();

            var exception = Assert.Throws<ConfigValidationException>(() => loader.SaveNetwork(settings));

            Assert.Contains("at most 32", exception.Message, StringComparison.Ordinal);
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
