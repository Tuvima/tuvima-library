using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests;

public sealed class SignInPageMethodsTests
{
    private static readonly RegisteredExternalAuthProvider[] Registered =
    [
        new("household-sso", "Household sign-in", "oidc", "Tuvima.External.household-sso", "/signin-tuvima-household-sso"),
        new("other", "Other provider", "oidc", "Tuvima.External.other", "/signin-tuvima-other"),
    ];

    private static string Page(SignInMethodsResponse? methods, bool atPublicOrigin = true) =>
        DashboardAuthenticationEndpoints.LoginPage(
            "token", methods, Registered, "11111111-2222-3333-4444-555555555555", "/", atPublicOrigin);

    [Fact]
    public void PasskeyButton_IsAbsentWhenTheEngineSaysNo_EvenAtThePublicOrigin()
    {
        var html = Page(new SignInMethodsResponse(true, false, [], true));

        Assert.DoesNotContain("id=\"passkey-login\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"password\"", html);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void PasskeyButton_NeedsBothTheEngineAndThePublicOrigin(bool atPublicOrigin, bool expected)
    {
        var html = Page(new SignInMethodsResponse(true, true, [], true), atPublicOrigin);

        Assert.Equal(expected, html.Contains("id=\"passkey-login\"", StringComparison.Ordinal));
    }

    [Fact]
    public void ProviderButtons_AreShownOnlyForProvidersTheEngineAllowsHere()
    {
        var html = Page(new SignInMethodsResponse(
            true, false, [new AccountExternalProviderResponse("household-sso", "Household sign-in")], true));

        Assert.Contains("/auth/external/household-sso", html);
        Assert.DoesNotContain("/auth/external/other", html);
    }

    [Fact]
    public void InvitationLink_IsShownBelowTheForm()
    {
        var html = Page(new SignInMethodsResponse(true, false, [], true));

        Assert.Contains("Have an invitation code?", html);
        Assert.Contains("href=\"/auth/invite\"", html);
    }

    [Fact]
    public void PasswordForm_IsHiddenWhenPasswordSignInIsUnavailableHere()
    {
        var html = Page(new SignInMethodsResponse(false, false, [], true));

        Assert.DoesNotContain("name=\"password\"", html);
        Assert.Contains("Have an invitation code?", html);
    }

    [Fact]
    public void WhenTheEngineCannotAnswer_OnlyPasswordSignInIsOffered()
    {
        var html = Page(null);

        Assert.Contains("name=\"password\"", html);
        Assert.DoesNotContain("id=\"passkey-login\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/auth/external/", html);
    }
}
