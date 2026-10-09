using System.Security.Claims;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Services.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MediaEngine.Web.Services.Integration;

public sealed class DashboardCookieEvents(
    DashboardIdentityClient identity,
    DashboardSessionAccessor session) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var token = context.Principal?.FindFirstValue(DashboardEngineAuthenticationHandler.SessionTokenClaim);
        if (string.IsNullOrWhiteSpace(token))
        {
            context.RejectPrincipal();
            return;
        }

        // Where this request came from. A session made at home is refused (without being erased) when it is
        // used from outside; the Engine makes that call using this value.
        var ingress = context.HttpContext.ClientIngress();
        var validation = await identity.ValidateCookieAsync(token, ingress, context.HttpContext.RequestAborted).ConfigureAwait(false);
        var validated = validation.Response;
        if (validated is null)
        {
            context.RejectPrincipal();
            // Fail this request closed, but only erase the cookie for a proven invalid session.
            // Throttling, timeouts and Engine restarts must not permanently log the user out.
            if (validation.Invalid)
            {
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            }
            return;
        }

        session.LastIngress = ingress;
        session.Set(token, validated.AccountId, validated.ActiveProfileId, validated.SessionId, validated.Authority);
        // The server projection carries live account, grant, unlock, and capability
        // state. Replacing the principal on every validation prevents a retained
        // cookie claim from outliving an access or protection mutation.
        context.ReplacePrincipal(DashboardPrincipalFactory.Create(validated, token, ingress));
        context.ShouldRenew = true;
    }
}

public static class DashboardPrincipalFactory
{
    /// <summary>Claim carrying where the cookie's last request came from, so a Blazor circuit can re-check from the same place.</summary>
    public const string ClientIngressClaim = "tuvima:client_ingress";

    /// <summary>Present while the person must still pick who is using Tuvima; the layout sends every page to the picker.</summary>
    public const string ProfilePendingClaim = "tuvima:profile_pending";

    /// <summary>Claim present while the person is signed in with an administrator-set temporary password and must choose their own.</summary>
    public const string PasswordChangeRequiredClaim = "tuvima:password_change_required";

    public static ClaimsPrincipal Create(AuthSessionResponse response, string ingress) =>
        CreateCore(response.SessionId, response.AccountId, response.ActiveProfileId, response.DisplayName,
            response.Authority, response.AuthenticationMethod, response.SessionToken, ingress, response.PasswordChangeRequired, response.ProfilePending);

    public static ClaimsPrincipal Create(SessionValidationResponse response, string token, string ingress) =>
        CreateCore(response.SessionId, response.AccountId, response.ActiveProfileId, response.DisplayName,
            response.Authority, response.AuthenticationMethod, token, ingress, response.PasswordChangeRequired, response.ProfilePending);

    private static ClaimsPrincipal CreateCore(Guid sessionId, Guid accountId, Guid activeProfileId, string name, DashboardAuthorityResponse authority, string method, string token, string ingress, bool passwordChangeRequired, bool profilePending = false)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, accountId.ToString("D")),
            new Claim(ClaimTypes.Name, name),
            new Claim("tuvima:account_id", accountId.ToString("D")),
            new Claim("tuvima:profile_id", activeProfileId.ToString("D")),
            new Claim("tuvima:active_profile_id", activeProfileId.ToString("D")),
            new Claim("tuvima:session_id", sessionId.ToString("D")),
            new Claim("tuvima:authentication_method", method),
            new Claim(DashboardEngineAuthenticationHandler.SessionTokenClaim, token),
            new Claim(ClientIngressClaim, ingress),
            new Claim("tuvima:account_authorization_version", authority.AccountAuthorizationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim("tuvima:grant_authorization_version", authority.GrantAuthorizationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        if (profilePending)
        {
            claims.Add(new Claim(ProfilePendingClaim, "true"));
        }

        if (passwordChangeRequired)
        {
            claims.Add(new Claim(PasswordChangeRequiredClaim, "true"));
        }

        claims.AddRange(authority.NavigationCapabilities.Select(capability => new Claim("tuvima:navigation", capability)));
        claims.AddRange(authority.ActionCapabilities.Select(capability => new Claim("tuvima:action", capability)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}
