using MediaEngine.Domain.Configuration;

namespace MediaEngine.Domain.Tests;

public sealed class ExternalIssuerMatcherTests
{
    [Fact]
    public void ConfiguredIssuer_RequiresExactMatchAfterTrim()
    {
        Assert.True(ExternalIssuerMatcher.Matches(" https://idp.example/ ", "", "https://idp.example/"));
        Assert.False(ExternalIssuerMatcher.Matches("https://idp.example", "", "https://idp.example/"));
        Assert.False(ExternalIssuerMatcher.Matches("https://idp.example/", "", "https://idp.example"));
        Assert.False(ExternalIssuerMatcher.Matches("https://idp.example", "", "https://IDP.example"));
    }

    [Fact]
    public void ConfiguredIssuer_TakesPrecedenceOverAuthority()
    {
        Assert.False(ExternalIssuerMatcher.Matches("https://issuer.example", "https://authority.example", "https://authority.example"));
    }

    [Fact]
    public void AuthorityFallback_AllowsExactlyOneTrailingSlashDifference()
    {
        Assert.True(ExternalIssuerMatcher.Matches("", "https://idp.example/realm", "https://idp.example/realm"));
        Assert.True(ExternalIssuerMatcher.Matches("", "https://idp.example/realm", "https://idp.example/realm/"));
        Assert.True(ExternalIssuerMatcher.Matches("", "https://idp.example/realm/", "https://idp.example/realm"));
        Assert.False(ExternalIssuerMatcher.Matches("", "https://idp.example/realm", "https://idp.example/realm//"));
    }

    [Theory]
    [InlineData("https://evil.example/realm")]
    [InlineData("https://idp.example/other")]
    [InlineData("")]
    [InlineData(null)]
    public void DifferentOrMissingIssuer_Fails(string? token)
    {
        Assert.False(ExternalIssuerMatcher.Matches("", "https://idp.example/realm", token));
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/common/v2.0")]
    [InlineData("https://login.microsoftonline.com/organizations/v2.0")]
    [InlineData("https://login.microsoftonline.com/consumers/v2.0/")]
    [InlineData("https://LOGIN.microsoftonline.com/Common/v2.0")]
    [InlineData("https://login.windows.net/common/v2.0")]
    [InlineData("https://login.microsoftonline.us/organizations/v2.0")]
    public void MultiTenantMicrosoftAuthority_IsRejectedWithTenantIdMessage(string authority)
    {
        var error = ExternalIssuerMatcher.GetMicrosoftTenantError(authority);

        Assert.NotNull(error);
        Assert.Contains("tenant ID", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/72f988bf-86f1-41af-91ab-2d7cd011db47/v2.0")]
    [InlineData("https://accounts.google.com")]
    [InlineData("https://idp.example/common/v2.0")]
    public void TenantSpecificOrOtherAuthority_IsAccepted(string authority)
    {
        Assert.Null(ExternalIssuerMatcher.GetMicrosoftTenantError(authority));
    }
}
