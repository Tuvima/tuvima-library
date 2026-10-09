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
