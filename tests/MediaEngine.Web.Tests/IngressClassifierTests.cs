using System.Net;
using MediaEngine.Domain.Configuration;
using MediaEngine.Web.Services.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class IngressClassifierTests
{
    private const int MainPort = 5016;
    private const int ProxyPort = 5017;

    [Fact]
    public void ProxyPortOff_DoesNotMakeAnyPortRemote()
    {
        Assert.Equal(IngressKind.ThisComputer, new IngressClassifier(null, []).Classify(IPAddress.Loopback, ProxyPort));
    }

    [Fact]
    public async Task MainPort_IgnoresForwardedForFromLoopback()
    {
        var context = await RunPipelineAsync(MainPort, "127.0.0.1", forwardedFor: "192.168.1.9");

        Assert.Equal(IPAddress.Loopback, context.Connection.RemoteIpAddress);
        Assert.Equal(IngressKind.ThisComputer, new IngressClassifier(ProxyPort, []).Classify(context));
    }

    [Fact]
    public async Task ProxyPort_FromAnUntrustedSender_DoesNotHonourForwardedFor()
    {
        var context = await RunPipelineAsync(ProxyPort, "203.0.113.50", forwardedFor: "127.0.0.1");

        Assert.Equal(IPAddress.Parse("203.0.113.50"), context.Connection.RemoteIpAddress);
    }

    [Fact]
    public void ContainerGateway_CanBeTrustedExplicitly()
    {
        var gateway = IPAddress.Parse("172.17.0.1");
        var classifier = new IngressClassifier(null, ["172.17.0.1/32"], null, null, [gateway]);

        Assert.Equal(IngressKind.HomeNetwork, classifier.Classify(gateway, MainPort));
    }

    [Fact]
    public void ContainerGatewayParser_ReadsDefaultRoutesOnly()
    {
        var gateways = ContainerGateway.Parse([
            "Iface\tDestination\tGateway\tFlags",
            "eth0\t00000000\t010011AC\t0003",
            "eth0\t000011AC\t00000000\t0001",
        ]);

        Assert.Equal([IPAddress.Parse("172.17.0.1")], gateways);
    }

    [Fact]
    public void ProxyPort_ResolvesFromEnvironmentThenSettingsThenTailscaleDefault()
    {
        var network = new NetworkSettings();

        Assert.Null(ProxyPortConfiguration.Resolve(network, null, null));
        Assert.Equal(5017, ProxyPortConfiguration.Resolve(network, null, "https://x.ts.net"));
        Assert.Equal(6000, ProxyPortConfiguration.Resolve(network, "6000", "https://x.ts.net"));
        Assert.Null(ProxyPortConfiguration.Resolve(network, "nonsense", null));
        Assert.Null(ProxyPortConfiguration.Resolve(network, "5016", null));

        network.Remote.ProxyPort = 7000;
        Assert.Equal(7000, ProxyPortConfiguration.Resolve(network, null, "https://x.ts.net"));
        Assert.Equal(6000, ProxyPortConfiguration.Resolve(network, "6000", null));
    }

    [Theory]
    [InlineData("http://+:5017", 5017, true)]
    [InlineData("http://*:5017/", 5017, true)]
    [InlineData("http://0.0.0.0:5016;http://[::]:5017", 5017, true)]
    [InlineData("http://localhost:5016", 5017, false)]
    [InlineData("http://0.0.0.0:50170", 5017, false)]
    public void UrlsContainPort_RecognisesCommonBindingForms(string urls, int port, bool expected)
    {
        Assert.Equal(expected, ProxyPortConfiguration.UrlsContainPort(urls, port));
    }

    [Fact]
    public void ProxyPortBinding_StaysOnLoopbackUnlessANonLoopbackProxyIsTrusted()
    {
        Assert.Equal("http://localhost:5017", ProxyPortConfiguration.BindUrl(new RemoteNetworkSettings(), 5017));
        Assert.Equal("http://localhost:5017", ProxyPortConfiguration.BindUrl(
            new RemoteNetworkSettings { TrustedProxies = ["127.0.0.1", "::1"] }, 5017));
        Assert.Equal("http://0.0.0.0:5017", ProxyPortConfiguration.BindUrl(
            new RemoteNetworkSettings { TrustedProxies = ["172.20.0.2"] }, 5017));
        Assert.Equal("http://0.0.0.0:5017", ProxyPortConfiguration.BindUrl(
            new RemoteNetworkSettings { TrustedProxyNetworks = ["172.21.0.0/24"] }, 5017));
    }

    [Fact]
    public void TailscalePreset_ProxiesToThePresetProxyPort()
    {
        var root = FindRepoRoot();
        var serve = File.ReadAllText(Path.Combine(root, "deploy", "tailscale", "config", "serve.json"));
        var compose = File.ReadAllText(Path.Combine(root, "deploy", "tailscale", "docker-compose.tailscale.yml"));

        Assert.Contains($"http://127.0.0.1:{ProxyPortConfiguration.TailscalePresetPort}", serve, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1:5016", serve, StringComparison.Ordinal);
        Assert.Contains($"TUVIMA_PROXY_PORT: \"{ProxyPortConfiguration.TailscalePresetPort}\"", compose, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static async Task<HttpContext> RunPipelineAsync(int localPort, string remote, string? forwardedFor)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<ForwardedHeadersOptions>(options =>
            ForwardedHeaderConfiguration.Configure(options, new RemoteNetworkSettings(), null));
        using var provider = services.BuildServiceProvider();

        var app = new ApplicationBuilder(provider);
        app.UseForwardedHeadersOnProxyPort(ProxyPort);
        var pipeline = app.Build();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Connection.LocalPort = localPort;
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        if (forwardedFor is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        await pipeline(context);
        return context;
    }
}
