using System.Net;
using MediaEngine.Api.Services.Networking;
using MediaEngine.Contracts.Playback;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Api.Tests;

public sealed class NetworkConnectionClassifierTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.50.21")]
    [InlineData("10.2.3.4")]
    [InlineData("172.20.1.8")]
    public void Classify_PrivateAndLoopbackRequestsAsLocal(string address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);

        var actual = new NetworkConnectionClassifier().Classify(context, null, null, null, null, null);

        Assert.Equal(PlaybackConnectionPaths.Local, actual.ConnectionPath);
    }

    [Fact]
    public void Classify_PublicRequestWithProviderAsSecureProviderAndClampsTelemetry()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.25");

        var actual = new NetworkConnectionClassifier().Classify(
            context,
            null,
            "wireguard-home",
            150_000,
            500_000,
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));

        Assert.Equal(PlaybackConnectionPaths.RemoteSecureProvider, actual.ConnectionPath);
        Assert.Equal("wireguard-home", actual.RemoteConnectivityProvider);
        Assert.Equal(100_000, actual.EstimatedBandwidthMbps);
        Assert.Equal(120_000, actual.LatencyMs);
    }

    [Fact]
    public void ClassifierSourceExplicitlyForbidsAuthorizationUse()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/Networking/NetworkConnectionClassifier.cs"));

        Assert.Contains("must not be", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("authentication", source, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRepoFilePath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));
}
