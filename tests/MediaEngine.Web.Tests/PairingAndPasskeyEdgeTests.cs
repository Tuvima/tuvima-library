using System.Text.Json.Nodes;
using MediaEngine.Domain.Configuration;
using MediaEngine.Web.Endpoints;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Web.Tests;

public sealed class PairingAndPasskeyEdgeTests
{
    private const string EngineResponse =
        "{\"device_code\":\"abc\",\"user_code\":\"ABCD-EFGH\",\"verification_uri\":\"http://127.0.0.1:61495/pair\",\"verification_uri_complete\":\"http://127.0.0.1:61495/pair?user_code=ABCD-EFGH\",\"expires_in\":600}";

    [Fact]
    public void PairingOrigin_UsesPublicAddress_OtherwiseTheAdmittedRequestOrigin()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("tv.home.example", 7062);

        Assert.Equal("https://tv.home.example:7062", ClientApiEdgeEndpoints.PairingOrigin(context.Request, new NetworkSettings()));

        var network = new NetworkSettings();
        network.Remote.PublicHostname = "https://tuvima.example.com";
        Assert.Equal("https://tuvima.example.com", ClientApiEdgeEndpoints.PairingOrigin(context.Request, network));
    }

    [Fact]
    public void RewriteVerificationUris_ReplacesTheEngineOriginAndKeepsPathAndQuery()
    {
        var rewritten = JsonNode.Parse(ClientApiEdgeEndpoints.RewriteVerificationUris(EngineResponse, "https://tv.home.example:7062"))!;

        Assert.Equal("https://tv.home.example:7062/pair", (string?)rewritten["verification_uri"]);
        Assert.Equal("https://tv.home.example:7062/pair?user_code=ABCD-EFGH", (string?)rewritten["verification_uri_complete"]);
        Assert.Equal("abc", (string?)rewritten["device_code"]);
        Assert.Equal(600, (int?)rewritten["expires_in"]);
    }

    [Fact]
    public void PasskeyRequest_DoesNotForwardTheVisitorsHostOrOrigin()
    {
        using var request = DashboardIdentityClient.PasskeyRequest(HttpMethod.Post, "/auth/passkeys/login/options", new { });

        Assert.Null(request.Headers.Host);
        Assert.False(request.Headers.Contains("Origin"));
    }

    [Theory]
    [InlineData("https", "tuvima.example.com", true)]
    [InlineData("https", "TUVIMA.example.com:443", true)]
    [InlineData("http", "tuvima.example.com", false)]
    [InlineData("https", "localhost:7062", false)]
    [InlineData("http", "192.168.1.20:5016", false)]
    [InlineData("https", "evil.example", false)]
    public void PasskeyOfferedOnlyAtThePublicOrigin(string scheme, string host, bool offered)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        var network = new NetworkSettings();
        network.Remote.PublicHostname = "https://tuvima.example.com";

        Assert.Equal(offered, PasskeyOriginGate.IsPublicOrigin(context.Request, network));
    }

    [Fact]
    public void PasskeyNeverOfferedWithoutAPublicAddress()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost", 7062);

        Assert.False(PasskeyOriginGate.IsPublicOrigin(context.Request, new NetworkSettings()));
    }
}
