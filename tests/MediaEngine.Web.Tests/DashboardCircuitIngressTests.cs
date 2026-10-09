using System.Security.Claims;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests;

public sealed class DashboardCircuitIngressTests
{
    [Fact]
    public void CircuitSeededFromThePrincipal_ReChecksFromWhereTheCookieLastValidated()
    {
        var claims = new[]
        {
            new Claim(DashboardEngineAuthenticationHandler.SessionTokenClaim, "token"),
            new Claim("tuvima:account_id", Guid.NewGuid().ToString("D")),
            new Claim("tuvima:active_profile_id", Guid.NewGuid().ToString("D")),
            new Claim("tuvima:session_id", Guid.NewGuid().ToString("D")),
            new Claim(DashboardPrincipalFactory.ClientIngressClaim, ClientIngressValues.HomeNetwork),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var session = new DashboardSessionAccessor();

        Assert.True(session.InitializeFromPrincipal(principal));

        Assert.Equal(ClientIngressValues.HomeNetwork, session.LastIngress);
    }

    [Fact]
    public void CircuitSeededFromAPrincipalWithoutTheClaim_HasNoRememberedIngress()
    {
        var claims = new[]
        {
            new Claim(DashboardEngineAuthenticationHandler.SessionTokenClaim, "token"),
            new Claim("tuvima:account_id", Guid.NewGuid().ToString("D")),
        };
        var session = new DashboardSessionAccessor();

        Assert.True(session.InitializeFromPrincipal(new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))));

        Assert.Null(session.LastIngress);
    }
}
