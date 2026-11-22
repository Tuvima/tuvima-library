using System.Net;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Domain.Tests;

/// <summary>What counts as clear evidence that a request came from outside the home (the away-from-home off switch turns away only those).</summary>
public sealed class OutsideRequestEvidenceTests
{
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("203.0.113.9")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void APublicAddress_IsClearlyOutside(string address)
    {
        Assert.True(OutsideRequestEvidence.IsClearlyOutside(forwarded: false, viaProxy: false, IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("10.0.0.5")]
    [InlineData("172.17.0.1")] // the Docker bridge gateway
    [InlineData("172.31.255.1")]
    [InlineData("192.168.1.20")]
    [InlineData("169.254.1.1")]
    [InlineData("100.64.0.1")] // carrier-grade NAT, which Tailscale uses
    [InlineData("100.127.255.254")]
    [InlineData("fe80::1")]
    [InlineData("fd7a:115c:a1e0::1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::ffff:192.168.1.20")]
    public void AddressesThatCanBeSomeoneAtHome_AreNotEvidenceOnTheirOwn(string address)
    {
        Assert.False(OutsideRequestEvidence.IsClearlyOutside(forwarded: false, viaProxy: false, IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("172.15.0.1")]
    public void TheEdgesOfThePrivateRanges_AreStillPublic(string address)
    {
        Assert.True(OutsideRequestEvidence.IsPubliclyRoutable(IPAddress.Parse(address)));
    }

    [Fact]
    public void ForwardingEvidence_OrTheProxyPort_IsEnoughWhateverTheAddress()
    {
        Assert.True(OutsideRequestEvidence.IsClearlyOutside(forwarded: true, viaProxy: false, IPAddress.Loopback));
        Assert.True(OutsideRequestEvidence.IsClearlyOutside(forwarded: false, viaProxy: true, IPAddress.Parse("100.101.102.103")));
        Assert.True(OutsideRequestEvidence.IsClearlyOutside(forwarded: true, viaProxy: false, null));
    }

    [Fact]
    public void AMissingAddressWithNoForwarding_IsNotEvidence()
    {
        Assert.False(OutsideRequestEvidence.IsClearlyOutside(forwarded: false, viaProxy: false, null));
    }

    [Fact]
    public void AContainerGatewayThatHappensToBePublic_IsNotEvidenceByItself()
    {
        var gateway = IPAddress.Parse("8.8.8.8");

        Assert.False(OutsideRequestEvidence.IsClearlyOutside(forwarded: false, viaProxy: false, gateway, [gateway]));
        Assert.True(OutsideRequestEvidence.IsClearlyOutside(forwarded: true, viaProxy: false, gateway, [gateway]));
    }

    [Theory]
    [InlineData("outside", true)]
    [InlineData(" OUTSIDE ", true)]
    [InlineData("remote", false)]
    [InlineData("home_network", false)]
    [InlineData("this_computer", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheOutsidePlace_IsClearlyOutside(string? place, bool expected)
    {
        Assert.Equal(expected, ClientIngress.IsClearlyOutside(place));
    }

    [Fact]
    public void TheOutsidePlace_NeverCountsAsLocal_AndKeepsItsEvidenceOnlyWhereAsked()
    {
        Assert.False(ClientIngress.IsOnLocalNetwork(ClientIngress.Outside));
        Assert.Equal(ClientIngress.Remote, ClientIngress.ParseCurrent(ClientIngress.Outside));
        Assert.Equal(ClientIngress.Outside, ClientIngress.ParseCurrentKeepingEvidence(ClientIngress.Outside));
        Assert.Equal(ClientIngress.Remote, ClientIngress.ParseCurrentKeepingEvidence("somewhere-else"));
        Assert.Equal(ClientIngress.Remote, ClientIngress.ParseCurrentKeepingEvidence(null));
        Assert.False(ClientIngress.TokenMayBeUsedFrom(ClientIngress.LanOnlyTokenPrefix + "abc", ClientIngress.Outside));
    }
}
