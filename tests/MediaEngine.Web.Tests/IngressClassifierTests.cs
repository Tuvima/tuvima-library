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

    [Theory]
    [InlineData("192.168.1.20", IngressKind.HomeNetwork)]
    [InlineData("10.0.0.5", IngressKind.HomeNetwork)]
    [InlineData("203.0.113.50", IngressKind.Remote)]
    [InlineData("2001:db8::1", IngressKind.Remote)]
    [InlineData("fd00::5", IngressKind.HomeNetwork)]
    public void OnlyPrivateAddressesAreTheHomeNetwork(string address, IngressKind expected)
    {
        Assert.Equal(expected, new IngressClassifier(null, []).Classify(IPAddress.Parse(address), MainPort));
    }

    [Fact]
    public void ProxyPort_NullAddress_AndConfiguredProxies_AreRemote()
    {
        var classifier = new IngressClassifier(ProxyPort, [], ["192.168.1.2"]);

        Assert.Equal(IngressKind.Remote, classifier.Classify(IPAddress.Loopback, ProxyPort));
        Assert.Equal(IngressKind.Remote, classifier.Classify((IPAddress?)null, MainPort));
        Assert.Equal(IngressKind.Remote, classifier.Classify(IPAddress.Parse("192.168.1.2"), MainPort));
    }

    [Fact]
    public void ForwardedRequest_IsOutsideEvenFromLoopback()
    {
        var services = new ServiceCollection().AddSingleton(new IngressClassifier(null, [])).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Connection.LocalPort = MainPort;

        Assert.Equal("this_computer", context.ClientIngress());

        context.Request.Headers["X-Forwarded-For"] = "203.0.113.9";

        // Never local, and marked as clearly outside so the away-from-home switch can turn it away.
        Assert.Equal("outside", context.ClientIngress());
        Assert.Equal(IngressKind.Remote, IngressClassifierExtensions.FromWireValue(context.ClientIngress()));
    }

    [Fact]
    public void ContainerGatewayAddress_NotForwarded_IsRemoteButNotClearlyOutside()
    {
        // A home install behind Docker NAT: every visitor, at home or not, shows the bridge gateway.
        var gateway = IPAddress.Parse("172.17.0.1");
        var context = ContextFrom(new IngressClassifier(null, [], null, null, [gateway]), gateway, MainPort);

        Assert.Equal("remote", context.ClientIngress());
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    public void APublicAddress_IsClearlyOutside(string address)
    {
        var context = ContextFrom(new IngressClassifier(null, []), IPAddress.Parse(address), MainPort);

        Assert.Equal("outside", context.ClientIngress());
    }

    [Fact]
    public void ADirectTailscaleAddress_IsRemoteButNotClearlyOutside()
    {
        // 100.64.0.0/10 (carrier-grade NAT, which Tailscale uses): not a private address to the classifier, not a public one either.
        var context = ContextFrom(new IngressClassifier(null, []), IPAddress.Parse("100.101.102.103"), MainPort);

        Assert.Equal("remote", context.ClientIngress());
    }

    [Fact]
    public void AnythingOnTheProxyPort_IsClearlyOutside_EvenWithoutForwardingHeaders()
    {
        var context = ContextFrom(new IngressClassifier(ProxyPort, []), IPAddress.Parse("100.101.102.103"), ProxyPort);

        Assert.Equal("outside", context.ClientIngress());
    }

    [Fact]
    public void AConfiguredProxyOnTheMainPort_IsClearlyOutside()
    {
        var context = ContextFrom(new IngressClassifier(ProxyPort, [], ["192.168.1.2"]), IPAddress.Parse("192.168.1.2"), MainPort);

        Assert.Equal("outside", context.ClientIngress());
    }

    [Fact]
    public void AHomeNetworkAddress_StaysTheHomeNetwork_NotOutside()
    {
        Assert.Equal("home_network", ContextFrom(new IngressClassifier(null, []), IPAddress.Parse("192.168.1.20"), MainPort).ClientIngress());
        Assert.Equal("this_computer", ContextFrom(new IngressClassifier(null, []), IPAddress.Loopback, MainPort).ClientIngress());
    }

    private static DefaultHttpContext ContextFrom(IngressClassifier classifier, IPAddress address, int localPort)
    {
        var services = new ServiceCollection().AddSingleton(classifier).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Connection.RemoteIpAddress = address;
        context.Connection.LocalPort = localPort;
        return context;
    }

    [Fact]
    public void ContainerGateway_IsRemoteUnlessTrusted()
    {
        var gateway = IPAddress.Parse("172.17.0.1");

        Assert.Equal(IngressKind.Remote, new IngressClassifier(null, [], null, null, [gateway]).Classify(gateway, MainPort));
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
