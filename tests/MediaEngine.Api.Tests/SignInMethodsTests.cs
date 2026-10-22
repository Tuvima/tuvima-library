using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Tests;

public sealed class SignInMethodsTests
{
    private const string PublicAddress = "https://library.example.com";

    private static NetworkSettings Network(string publicAddress = PublicAddress) =>
        new() { Remote = { PublicHostname = publicAddress } };

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
            policy, Network(publicAddress: string.Empty), ClientIngressValues.HomeNetwork, false);
        var with = AuthenticationEndpoints.BuildSignInMethods(
            policy, Network(), ClientIngressValues.HomeNetwork, false);

        Assert.False(without.Passkey);
        Assert.True(without.Password);
        Assert.True(with.Passkey);
    }

    [Fact]
    public void Password_IsOffWhenTheOnlyAccountIsTheThisComputerOwnerWithNoPassword()
    {
        var methods = AuthenticationEndpoints.BuildSignInMethods(Policy(), Network(), ClientIngressValues.ThisComputer, false);

        var result = AuthenticationEndpoints.WithPasswordAvailability(methods, [ThisComputerOwner()]);

        Assert.False(result.Password);
        Assert.Equal(methods.Passkey, result.Passkey);
        Assert.Equal(methods.InvitationCode, result.InvitationCode);
    }

    [Fact]
    public void Password_StaysOnWhileSomeEnabledAccountHasAnEmailAndIsNotThisComputerOnly()
    {
        var methods = AuthenticationEndpoints.BuildSignInMethods(Policy(), Network(), ClientIngressValues.HomeNetwork, false);

        Assert.True(AuthenticationEndpoints.WithPasswordAvailability(methods, [ThisComputerOwner(), PasswordAccount()]).Password);
        Assert.False(AuthenticationEndpoints.WithPasswordAvailability(methods, [ThisComputerOwner(), PasswordAccount(enabled: false)]).Password);
        Assert.False(AuthenticationEndpoints.WithPasswordAvailability(methods, []).Password);
    }

    [Fact]
    public void Password_StaysOffWhenTheInstallTurnedItOff_EvenWithAPasswordAccount()
    {
        var policy = Policy();
        policy.PasswordSignInEnabled = false;
        var methods = AuthenticationEndpoints.BuildSignInMethods(policy, Network(), ClientIngressValues.HomeNetwork, false);

        Assert.False(AuthenticationEndpoints.WithPasswordAvailability(methods, [PasswordAccount()]).Password);
    }

    private static Account ThisComputerOwner()
    {
        var account = new Account { Id = Guid.NewGuid(), IsEnabled = true };
        account.MarkThisComputerOnly();
        return account;
    }

    private static Account PasswordAccount(bool enabled = true) => new()
    {
        Id = Guid.NewGuid(),
        Email = "sam@example.com",
        NormalizedEmail = "SAM@EXAMPLE.COM",
        IsEnabled = enabled,
    };

    [Fact]
    public void Response_ContainsOnlyMethodFields_NeverAccountOrProfileDetails()
    {
        var json = JsonSerializer.Serialize(AuthenticationEndpoints.BuildSignInMethods(
            Policy(withProvider: true), Network(), ClientIngressValues.HomeNetwork, false));

        using var document = JsonDocument.Parse(json);
        var names = document.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray();

        Assert.Equal(["external_providers", "invitation_code", "passkey", "password"], names);
        Assert.DoesNotContain("@", json);
        Assert.DoesNotContain("email", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("profile", json, StringComparison.OrdinalIgnoreCase);
    }
}
