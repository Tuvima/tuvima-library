using System.Reflection;
using System.Security.Claims;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests;

public sealed class ExternalSignInClaimAndValidationTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));

    [Fact]
    public void OidcClaims_ReadUnmappedEmailAndName()
    {
        var principal = Principal(new Claim("email", "a@example.test"), new Claim("name", "Ada"));

        Assert.Equal("a@example.test", ExternalAuthenticationRegistration.ReadOidcEmail(principal));
        Assert.Equal("Ada", ExternalAuthenticationRegistration.ReadOidcName(principal));
    }

    [Fact]
    public void OidcName_FallsBackToPreferredUsernameThenMappedClaim()
    {
        Assert.Equal("ada", ExternalAuthenticationRegistration.ReadOidcName(
            Principal(new Claim("preferred_username", "ada"))));
        Assert.Equal("Mapped", ExternalAuthenticationRegistration.ReadOidcName(
            Principal(new Claim(ClaimTypes.Name, "Mapped"))));
        Assert.Equal("m@example.test", ExternalAuthenticationRegistration.ReadOidcEmail(
            Principal(new Claim(ClaimTypes.Email, "m@example.test"))));
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/common/v2.0", false)]
    [InlineData("https://login.microsoftonline.com/organizations/v2.0", false)]
    [InlineData("https://login.microsoftonline.com/consumers/v2.0", false)]
    [InlineData("https://login.microsoftonline.com/72f988bf-86f1-41af-91ab-2d7cd011db47/v2.0", true)]
    public void Validate_RequiresMicrosoftTenantId(string authority, bool accepted)
    {
        var validate = typeof(ExternalAuthenticationRegistration).GetMethod(
            "Validate", BindingFlags.NonPublic | BindingFlags.Static)!;
        var provider = new ExternalAuthProviderSettings
        {
            Id = "microsoft",
            Kind = ExternalAuthProviderKinds.OpenIdConnect,
            DisplayName = "Microsoft",
            ClientId = "client",
            Authority = authority,
        };

        var act = () => validate.Invoke(null, [provider, new HashSet<string>()]);

        if (accepted)
        {
            act();
            return;
        }

        var ex = Assert.Throws<TargetInvocationException>(act);
        Assert.Contains("tenant ID", ex.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_DetectsDifferencesFromStartupRegistration()
    {
        var registered = new ExternalAuthProviderSettings
        {
            Id = "idp",
            Kind = "oidc",
            Enabled = true,
            DisplayName = "IdP",
            ClientId = "c",
            Authority = "https://idp.example",
            Scopes = ["openid", "email"],
        };
        var snapshot = new RegisteredExternalProvidersSnapshot([registered]);
        ExternalAuthProviderDto Saved(string clientId = "c", bool enabled = true) => new()
        {
            Id = "idp",
            Kind = "oidc",
            Enabled = enabled,
            DisplayName = "IdP",
            ClientId = clientId,
            Authority = "https://idp.example",
            Scopes = ["email", "openid"],
        };

        Assert.False(snapshot.DiffersFrom([Saved()], "Optional"));
        Assert.True(snapshot.DiffersFrom([Saved(clientId: "other")], "Optional"));
        Assert.True(snapshot.DiffersFrom([Saved(enabled: false)], "Optional"));
        Assert.True(snapshot.DiffersFrom([], "Optional"));
        Assert.False(new RegisteredExternalProvidersSnapshot([]).DiffersFrom([Saved(enabled: false)], "Optional"));
    }

    [Fact]
    public void Snapshot_IgnoresProvidersWhileModeDisallowsExternalSignIn_AndFlagsSecretChanges()
    {
        var snapshot = new RegisteredExternalProvidersSnapshot([]);
        var saved = new ExternalAuthProviderDto
        {
            Id = "idp",
            Kind = "oidc",
            Enabled = true,
            DisplayName = "IdP",
            ClientId = "c",
            Authority = "https://idp.example",
            Scopes = ["openid"],
        };

        Assert.False(snapshot.DiffersFrom([saved], "Local"));
        Assert.True(snapshot.DiffersFrom([saved], "Required"));

        snapshot.MarkSecretChanged();
        Assert.True(snapshot.DiffersFrom([], "Local"));
    }
}
