using System.Net;
using MediaEngine.Web.Services.Configuration;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Web.Tests;

public sealed class OriginalClientContextTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("192.168.1.25", true)]
    [InlineData("10.0.0.25", true)]
    [InlineData("203.0.113.20", false)]
    [InlineData("100.101.102.103", false)]
    public void LocalEntry_UsesProcessedRemoteAddressOnly(string address, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);

        Assert.Equal(expected, Classify(context) != IngressKind.Remote);
    }

    [Fact]
    public void ExplicitTrustedLocalNetwork_AllowsOnlyConfiguredRange()
    {
        var classifier = new IngressClassifier(null, ["203.0.113.0/24"]);
        var trusted = new DefaultHttpContext();
        trusted.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.25");
        var otherPublic = new DefaultHttpContext();
        otherPublic.Connection.RemoteIpAddress = IPAddress.Parse("203.0.114.25");

        Assert.Equal(IngressKind.HomeNetwork, classifier.Classify(trusted));
        Assert.Equal(IngressKind.Remote, classifier.Classify(otherPublic));
    }

    [Fact]
    public void BrowserHeaders_CannotAssertLocalEntry()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.20");
        context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
        context.Request.Headers["X-Tuvima-Original-Client-Is-Local"] = "true";

        Assert.Equal(IngressKind.Remote, Classify(context));
    }

    [Fact]
    public void MissingRemoteAddress_FailsClosed()
    {
        Assert.Equal(IngressKind.Remote, Classify(new DefaultHttpContext()));
    }

    private static IngressKind Classify(HttpContext context) => new IngressClassifier(null, []).Classify(context);
}
