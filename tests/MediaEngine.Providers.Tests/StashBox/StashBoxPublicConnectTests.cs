using System.Net;
using System.Net.Sockets;
using MediaEngine.Providers.Services.StashBox;

namespace MediaEngine.Providers.Tests.StashBox;

public sealed class StashBoxPublicConnectTests
{
    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("203.0.113.9", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.1.2.3", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    public void OnlyAPublicInternetAddressIsAllowed(string address, bool expected) =>
        Assert.Equal(expected, StashBoxPublicConnect.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task TheHandler_RefusesToConnectToThisMachine_EvenForAnAddressThatLooksPlausible(string host)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = listener.AcceptTcpClientAsync();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new HttpClient(StashBoxPublicConnect.CreateHandler()) { Timeout = TimeSpan.FromSeconds(10) };

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://{host}:{port}/cover.jpg"));

        Assert.False(accepted.IsCompleted, "The handler opened a connection to a loopback listener.");
    }

    [Fact]
    public void TheHandler_NeverFollowsARedirect() =>
        Assert.False(StashBoxPublicConnect.CreateHandler().AllowAutoRedirect);
}
