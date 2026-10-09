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
    public void ExternalProviders_AreListedOnlyWhenEnabledAndAllowedHere()
    {
        var network = Network(WhoCanConnectModes.HomeNetwork);

        var home = AuthenticationEndpoints.BuildSignInMethods(Policy(withProvider: true), network, ClientIngressValues.HomeNetwork, false);
        var remote = AuthenticationEndpoints.BuildSignInMethods(Policy(withProvider: true), network, ClientIngressValues.Remote, true);
        var disabled = AuthenticationEndpoints.BuildSignInMethods(
            Policy(withProvider: true, providerEnabled: false), network, ClientIngressValues.HomeNetwork, false);

        var listed = Assert.Single(home.ExternalProviders);
        Assert.Equal("household-sso", listed.Id);
        Assert.Equal("Household sign-in", listed.DisplayName);
        Assert.Empty(remote.ExternalProviders);
        Assert.Empty(disabled.ExternalProviders);
    }

    [Fact]
    public void RemoteVisitor_GetsNothingWhenWhoCanConnectStopsAtHome_AndInvitationFollowsSignInBeingPossible()
    {
        var policy = Policy(withProvider: true);

        var blocked = AuthenticationEndpoints.BuildSignInMethods(
            policy, Network(WhoCanConnectModes.HomeNetwork), ClientIngressValues.Remote, true);
        var insecure = AuthenticationEndpoints.BuildSignInMethods(
            policy, Network(WhoCanConnectModes.Anywhere), ClientIngressValues.Remote, false);
        var allowed = AuthenticationEndpoints.BuildSignInMethods(
            policy, Network(WhoCanConnectModes.Anywhere), ClientIngressValues.Remote, true);

        Assert.False(blocked.Password || blocked.Passkey || blocked.InvitationCode);
        Assert.Empty(blocked.ExternalProviders);
        Assert.False(insecure.Password || insecure.InvitationCode);
        Assert.True(allowed.Password);
        Assert.True(allowed.InvitationCode);
    }

    [Fact]
    public void MissingPlace_IsTreatedAsOutsideTheHome()
    {
        var result = AuthenticationEndpoints.BuildSignInMethods(
            Policy(), Network(WhoCanConnectModes.HomeNetwork), originalClientIngress: null, originalClientIsHttps: false);

        Assert.False(result.Password);
        Assert.False(result.InvitationCode);
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
