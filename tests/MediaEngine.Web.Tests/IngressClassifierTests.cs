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

    [Theory]
    [InlineData("127.0.0.1", IngressKind.ThisComputer)]
    [InlineData("::1", IngressKind.ThisComputer)]
    [InlineData("::ffff:127.0.0.1", IngressKind.ThisComputer)]
    [InlineData("::ffff:192.168.1.5", IngressKind.HomeNetwork)]
    [InlineData("192.168.1.5", IngressKind.HomeNetwork)]
    [InlineData("10.1.2.3", IngressKind.HomeNetwork)]
    [InlineData("172.16.0.9", IngressKind.HomeNetwork)]
    [InlineData("172.17.0.1", IngressKind.HomeNetwork)]
    [InlineData("172.32.0.1", IngressKind.Remote)]
    [InlineData("169.254.10.10", IngressKind.HomeNetwork)]
    [InlineData("fd12::1", IngressKind.HomeNetwork)]
    [InlineData("fc00::1", IngressKind.HomeNetwork)]
    [InlineData("fe80::1", IngressKind.HomeNetwork)]
    [InlineData("100.101.102.103", IngressKind.Remote)]
    [InlineData("8.8.8.8", IngressKind.Remote)]
    [InlineData("2001:db8::1", IngressKind.Remote)]
    public void Classify_UsesTheConnectionAddress(string address, IngressKind expected)
    {
        var classifier = new IngressClassifier(ProxyPort, []);

        Assert.Equal(expected, classifier.Classify(IPAddress.Parse(address), MainPort));
    }

    [Fact]
    public void MissingAddress_FailsClosedAsRemote()
    {
        Assert.Equal(IngressKind.Remote, new IngressClassifier(null, []).Classify((IPAddress?)null, MainPort));
        Assert.Equal(IngressKind.Remote, new IngressClassifier(null, []).Classify(new DefaultHttpContext()));
    }

    [Fact]
    public void TrustedLocalNetwork_CountsAsHomeNetwork_OnlyWithinTheRange()
    {
        var classifier = new IngressClassifier(null, ["203.0.113.0/24", "not-a-network"]);

        Assert.Equal(IngressKind.HomeNetwork, classifier.Classify(IPAddress.Parse("203.0.113.9"), MainPort));
        Assert.Equal(IngressKind.HomeNetwork, classifier.Classify(IPAddress.Parse("::ffff:203.0.113.9"), MainPort));
        Assert.Equal(IngressKind.Remote, classifier.Classify(IPAddress.Parse("203.0.114.9"), MainPort));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1.5")]
    [InlineData("8.8.8.8")]
    public void AnythingOnTheProxyPort_IsRemote(string address)
    {
        var classifier = new IngressClassifier(ProxyPort, ["192.168.1.0/24"]);

        Assert.Equal(IngressKind.Remote, classifier.Classify(IPAddress.Parse(address), ProxyPort));
    }

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
    public async Task ProxyPort_WithoutForwardedFor_IsRemote()
    {
        var context = await RunPipelineAsync(ProxyPort, "127.0.0.1", forwardedFor: null);

        Assert.Equal(IngressKind.Remote, new IngressClassifier(ProxyPort, []).Classify(context));
    }

    [Fact]
    public async Task ProxyPort_WithPublicForwardedFor_IsRemote()
    {
        var context = await RunPipelineAsync(ProxyPort, "127.0.0.1", forwardedFor: "8.8.8.8");

        Assert.Equal(IPAddress.Parse("8.8.8.8"), context.Connection.RemoteIpAddress);
        Assert.Equal(IngressKind.Remote, new IngressClassifier(ProxyPort, []).Classify(context));
    }

    [Fact]
    public async Task ProxyPort_FromAnUntrustedSender_DoesNotHonourForwardedFor()
    {
        var context = await RunPipelineAsync(ProxyPort, "203.0.113.50", forwardedFor: "127.0.0.1");

        Assert.Equal(IPAddress.Parse("203.0.113.50"), context.Connection.RemoteIpAddress);
    }

    [Fact]
    public void MainPortConnectionFromAConfiguredProxy_IsRemote_NotHomeNetwork()
    {
        var classifier = new IngressClassifier(null, [], ["192.168.1.5"], ["172.20.0.0/16"]);

        Assert.Equal(IngressKind.Remote, classifier.Classify(IPAddress.Parse("192.168.1.5"), MainPort));
        Assert.Equal(IngressKind.Remote, classifier.Classify(IPAddress.Parse("::ffff:192.168.1.5"), MainPort));
        Assert.Equal(IngressKind.Remote, classifier.Classify(IPAddress.Parse("172.20.3.4"), MainPort));
        Assert.Equal(IngressKind.HomeNetwork, classifier.Classify(IPAddress.Parse("192.168.1.6"), MainPort));
        Assert.Equal(IngressKind.ThisComputer, classifier.Classify(IPAddress.Loopback, MainPort));
    }

    [Fact]
    public void TrustedLocalNetwork_InMappedIpv6Form_StillMatchesIpv4Clients()
    {
        var classifier = new IngressClassifier(null, ["::ffff:203.0.113.0/120"]);

        Assert.Equal(IngressKind.HomeNetwork, classifier.Classify(IPAddress.Parse("203.0.113.9"), MainPort));
        Assert.Equal(IngressKind.Remote, classifier.Classify(IPAddress.Parse("203.0.114.9"), MainPort));
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
