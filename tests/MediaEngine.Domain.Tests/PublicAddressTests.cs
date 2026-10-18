using MediaEngine.Domain.Configuration;

namespace MediaEngine.Domain.Tests;

public sealed class PublicAddressTests
{
    [Theory]
    [InlineData("https://tuvima.example.com")]
    [InlineData("https://tuvima.example.com/")]
    [InlineData("https://tuvima.example.com:8443")]
    [InlineData("http://localhost:5016")]
    [InlineData("http://127.0.0.1:5016")]
    public void IsValid_AcceptsPublicAddresses(string value) => Assert.True(PublicAddress.IsValid(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tuvima.example.com")]
    [InlineData("http://tuvima.example.com")]
    [InlineData("https://tuvima.example.com/library")]
    [InlineData("https://tuvima.example.com?x=1")]
    [InlineData("https://tuvima.example.com/#top")]
    [InlineData("https://user:pw@tuvima.example.com")]
    public void IsValid_RejectsEverythingElse(string? value) => Assert.False(PublicAddress.IsValid(value));

    [Theory]
    [InlineData("https://tuvima.example.com", "https://tuvima.example.com")]
    [InlineData("  https://Tuvima.Example.com/  ", "https://tuvima.example.com")]
    [InlineData("https://tuvima.example.com:8443/library?x=1#top", "https://tuvima.example.com:8443")]
    [InlineData("https://host.tailnet.ts.net", "https://host.tailnet.ts.net")]
    [InlineData("https://user:pw@tuvima.example.com", "https://tuvima.example.com")]
    [InlineData("https://tuvima.example.com:443", "https://tuvima.example.com")]
    [InlineData("http://192.168.1.20:5016/", "http://192.168.1.20:5016")]
    public void ToOrigin_KeepsOnlySchemeHostAndNonDefaultPort(string value, string expected) =>
        Assert.Equal(expected, PublicAddress.ToOrigin(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tuvima.example.com")]
    [InlineData("ftp://tuvima.example.com")]
    public void ToOrigin_ReturnsNothingWhenThereIsNoWebAddress(string? value) => Assert.Null(PublicAddress.ToOrigin(value));
}
