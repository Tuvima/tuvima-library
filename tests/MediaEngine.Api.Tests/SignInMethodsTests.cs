using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Api.Tests;

public sealed class SignInMethodsTests
{
    private const string PublicAddress = "https://library.example.com";

    private static NetworkSettings Network(string whoCanConnect, string publicAddress = PublicAddress) =>
        new() { WhoCanConnect = whoCanConnect, Remote = { PublicHostname = publicAddress } };

    private static AuthSettings Policy(bool withProvider = false, bool providerEnabled = true)
    {
        var policy = new AuthSettings
        {
            Mode = "Optional",
            PasswordSignInEnabled = true,
            PasskeySignInEnabled = true,
            ExternalSignInEnabled = true,
        };
        if (withProvider)
        {
            policy.ExternalProviders.Add(new ExternalAuthProviderSettings
            {
                Id = "household-sso",
                Kind = ExternalAuthProviderKinds.OpenIdConnect,
                Enabled = providerEnabled,
                DisplayName = "Household sign-in",
                Authority = "https://id.example.com",
                Issuer = "https://id.example.com",
                ClientId = "tuvima",
            });
        }

        return policy;
    }

    [Fact]
    public void Passkey_IsOffWithoutAPublicAddress_AndOnWithOne()
    {
        var policy = Policy();

        var without = AuthenticationEndpoints.BuildSignInMethods(
            policy, Network(WhoCanConnectModes.HomeNetwork, publicAddress: string.Empty), ClientIngressValues.HomeNetwork, false);
        var with = AuthenticationEndpoints.BuildSignInMethods(
            policy, Network(WhoCanConnectModes.HomeNetwork), ClientIngressValues.HomeNetwork, false);

        Assert.False(without.Passkey);
        Assert.True(without.Password);
        Assert.True(with.Passkey);
    }

    [Fact]
    public void Response_ContainsOnlyMethodFields_NeverAccountOrProfileDetails()
    {
        var json = JsonSerializer.Serialize(AuthenticationEndpoints.BuildSignInMethods(
            Policy(withProvider: true), Network(WhoCanConnectModes.HomeNetwork), ClientIngressValues.HomeNetwork, false));

        using var document = JsonDocument.Parse(json);
        var names = document.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray();

        Assert.Equal(["external_providers", "invitation_code", "passkey", "password"], names);
        Assert.DoesNotContain("@", json);
        Assert.DoesNotContain("email", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("profile", json, StringComparison.OrdinalIgnoreCase);
    }
}
