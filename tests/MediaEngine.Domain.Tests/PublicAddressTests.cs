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
}
