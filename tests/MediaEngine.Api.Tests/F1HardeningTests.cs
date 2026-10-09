using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Configuration;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Api.Tests;

public sealed class F1HardeningTests
{
    [Fact]
    public void EngineAllowedHosts_IsNotWildcard_AndNamesOnlyLoopback()
    {
        var root = FindRepoRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Api", "appsettings.json")));
        var allowed = document.RootElement.GetProperty("AllowedHosts").GetString();

        Assert.False(string.IsNullOrWhiteSpace(allowed));
        Assert.NotEqual("*", allowed!.Trim());
        var hosts = allowed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(new[] { "localhost", "127.0.0.1", "[::1]" }, hosts);
    }

    [Theory]
    [InlineData("Dockerfile")]
    [InlineData("docker-compose.yml")]
    [InlineData("docker-entrypoint.sh")]
    [InlineData("installer.iss")]
    public void EngineCallers_UseAnAllowedLoopbackHost(string file)
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), file));
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"https?://([A-Za-z0-9.\[\]:+_-]+?):61495"))
        {
            Assert.Contains(match.Groups[1].Value, new[] { "localhost", "127.0.0.1", "0.0.0.0", "+" });
        }
    }

    [Fact]
    public void PasskeyOptions_UsePublicAddressHostAndOnlyThatOrigin_AndFollowChanges()
    {
        using var scope = new TempConfig();
        var configure = new PublicAddressPasskeyOptions(scope.Loader);

        var none = new IdentityOptions();
        configure.Configure(none);
        Assert.Null(none.Passkey.ServerDomain);
        Assert.False(Accepts(none, "https://evil.example"));

        scope.SetPublicAddress("https://tuvima.example.com");
        var first = new IdentityOptions();
        configure.Configure(first);
        Assert.Equal("tuvima.example.com", first.Passkey.ServerDomain);
        Assert.True(Accepts(first, "https://tuvima.example.com"));
        Assert.False(Accepts(first, "https://evil.example"));
        Assert.False(Accepts(first, "http://tuvima.example.com"));

        scope.SetPublicAddress("https://other.example.org");
        var second = new IdentityOptions();
        configure.Configure(second);
        Assert.Equal("other.example.org", second.Passkey.ServerDomain);
        Assert.False(Accepts(second, "https://tuvima.example.com"));
    }

    [Fact]
    public void PairingOrigin_IgnoresForwardedHeaders_AndPrefersPublicAddress()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost", 61495);
        context.Request.Headers["X-Forwarded-Host"] = "evil.example";
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        var withoutAddress = ClientAuthorizationEndpoints.PairingOrigin(context.Request, new NetworkSettings());
        Assert.Equal("http://localhost:61495", withoutAddress);
        Assert.DoesNotContain("evil.example", withoutAddress);

        var network = new NetworkSettings();
        network.Remote.PublicHostname = "https://tuvima.example.com/";
        // A trailing slash is not a valid public address, so the Engine origin is still used.
        Assert.Equal("http://localhost:61495", ClientAuthorizationEndpoints.PairingOrigin(context.Request, network));

        network.Remote.PublicHostname = "https://tuvima.example.com";
        Assert.Equal("https://tuvima.example.com", ClientAuthorizationEndpoints.PairingOrigin(context.Request, network));
    }

    private static bool Accepts(IdentityOptions options, string origin) =>
        options.Passkey.ValidateOrigin!(new PasskeyOriginValidationContext { Origin = origin }).AsTask().GetAwaiter().GetResult();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed class TempConfig : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-f1-{Guid.NewGuid():N}");

        public TempConfig() => Loader = new ConfigurationDirectoryLoader(_path);

        public ConfigurationDirectoryLoader Loader { get; }

        public void SetPublicAddress(string address)
        {
            var network = Loader.LoadNetwork();
            network.Remote.PublicHostname = address;
            Loader.SaveNetwork(network);
        }

        public void Dispose()
        {
            Loader.Dispose();
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
    }
}
