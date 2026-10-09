using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using MediaEngine.Api.Services;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Identity.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Security;

public static class TuvimaAuthDefaults
{
    public const string Scheme = "Tuvima";
    public const string ServiceHeader = "X-Tuvima-Service-Key";
    public const string SessionHeader = "X-Tuvima-Session";
}

public static class TuvimaClaimTypes
{
    public const string AccountId = "tuvima:account_id";
    public const string ProfileId = "tuvima:profile_id";
    public const string ActiveProfileId = "tuvima:active_profile_id";
    public const string SessionId = "tuvima:session_id";
    public const string AuthenticationMethod = "tuvima:authentication_method";
    public const string DashboardService = "tuvima:dashboard_service";
    public const string DeviceId = "tuvima:device_id";
    public const string DeviceClass = "tuvima:device_class";
    public const string ClientId = "tuvima:client_id";
    public const string ClientVersion = "tuvima:client_version";
    public const string TokenId = "tuvima:token_id";
    public const string Scope = "tuvima:scope";
    public const string PrincipalKind = "tuvima:principal_kind";
    public const string ApplicationId = "tuvima:application_id";
    public const string ApplicationCredentialId = "tuvima:application_credential_id";
    public const string AccountAuthorizationVersion = "tuvima:account_authorization_version";
    public const string GrantAuthorizationVersion = "tuvima:grant_authorization_version";
    public const string ApplicationAuthorizationVersion = "tuvima:application_authorization_version";

    /// <summary>Present on a person's session while their password is an administrator-set temporary one.</summary>
    public const string PasswordChangeRequired = "tuvima:password_change_required";
}

public static class AuthPolicies
{
    public const string Authenticated = "authenticated_user";
    public const string Administrator = "administrator";
    public const string AdministratorEligibility = "effective_administrator";
    public const string HumanSelfService = "human_self_service";
    public const string DashboardService = "dashboard_service";
    public const string DashboardInteractive = "dashboard_interactive";
    public const string IntercomConnect = "intercom_connect";
}

public sealed class TuvimaAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IFirstPartyIdentityService identity,
    ClientAuthorizationService clientAuthorization,
    IApplicationRepository applications,
    IConfigurationLoader configurationLoader,
    IWebHostEnvironment environment)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Dashboard-to-Engine requests carry a server-held service credential.
        // Prefer that credential when both headers are present so a stale client
        // bearer cannot shadow a valid interactive Dashboard session.
        if (!Request.Headers.ContainsKey(TuvimaAuthDefaults.ServiceHeader)
            && Request.Headers.Authorization.ToString() is { } authorization
            && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var rawToken = authorization["Bearer ".Length..].Trim();
            var client = await clientAuthorization.ValidateAccessTokenAsync(rawToken, Context.RequestAborted).ConfigureAwait(false);
            if (client is null)
            {
                return AuthenticateResult.Fail("Invalid, expired, or revoked bearer token.");
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, client.Account.Id.ToString("D")),
                new(ClaimTypes.Name, client.Device.DeviceName),
                new(TuvimaClaimTypes.PrincipalKind, Domain.Authorization.PrincipalKind.DelegatedUserClient.ToString()),
                new(TuvimaClaimTypes.ApplicationId, client.Application.Id.ToString("D")),
                new(TuvimaClaimTypes.AccountId, client.Account.Id.ToString("D")),
                new(TuvimaClaimTypes.ProfileId, client.Device.ProfileId.ToString("D")),
                new(TuvimaClaimTypes.ActiveProfileId, client.Device.ProfileId.ToString("D")),
                new(TuvimaClaimTypes.DeviceId, client.Device.Id.ToString("D")),
                new(TuvimaClaimTypes.DeviceClass, client.Device.DeviceClass),
                new(TuvimaClaimTypes.ClientId, client.Device.ClientId),
                new(TuvimaClaimTypes.ClientVersion, client.Device.ClientVersion),
                new(TuvimaClaimTypes.TokenId, client.Token.Id.ToString("D")),
                new(TuvimaClaimTypes.AuthenticationMethod, "device_bearer"),
                new(TuvimaClaimTypes.AccountAuthorizationVersion, client.Account.AuthorizationVersion.ToString()),
                new(TuvimaClaimTypes.GrantAuthorizationVersion, client.Grant.AuthorizationVersion.ToString()),
                new(TuvimaClaimTypes.ApplicationAuthorizationVersion, client.Application.AuthorizationVersion.ToString()),
            };
            claims.AddRange(ClientAuthorizationService.SplitScopes(client.Token.Scopes)
                .Select(scope => new Claim(TuvimaClaimTypes.Scope, scope)));
            return Success(claims);
        }

        if (Request.Headers.TryGetValue(TuvimaAuthDefaults.ServiceHeader, out var serviceValues))
        {
            var serviceToken = serviceValues.ToString();
            if (!await identity.ValidateServiceCredentialAsync(serviceToken, Context.RequestAborted).ConfigureAwait(false))
            {
                return AuthenticateResult.Fail("Invalid Dashboard service credential.");
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, "service:dashboard"),
                new(ClaimTypes.Name, "Tuvima Dashboard"),
                new(TuvimaClaimTypes.DashboardService, "true"),
                new(TuvimaClaimTypes.PrincipalKind, Domain.Authorization.PrincipalKind.DashboardTransport.ToString()),
            };

            SessionValidationResult? session = null;
            if (Request.Headers.TryGetValue(TuvimaAuthDefaults.SessionHeader, out var sessionValues)
                && !string.IsNullOrWhiteSpace(sessionValues.ToString()))
            {
                // The Dashboard says where the visitor is when it knows; an account that works only on this computer
                // is refused when it does not.
                var claimedIngress = Request.Headers.TryGetValue(ClientIngressValues.ValidateHeader, out var ingressValues)
                    && !string.IsNullOrWhiteSpace(ingressValues.ToString())
                    ? ClientIngress.Parse(ingressValues.ToString())
                    : null;
                session = await identity.ValidateSessionAsync(sessionValues.ToString(), true, Context.RequestAborted, claimedIngress).ConfigureAwait(false);
                if (session is null)
                {
                    return AuthenticateResult.Fail("Invalid or revoked user session.");
                }

                AddSessionClaims(claims, session);
            }

            return Success(claims);
        }

        if (Request.Headers.TryGetValue("X-Api-Key", out var apiKeyValues))
        {
            var raw = apiKeyValues.ToString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return AuthenticateResult.Fail("API key is empty.");
            }

            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
            var match = await applications.FindApplicationCredentialAsync(hash, DateTimeOffset.UtcNow, Context.RequestAborted).ConfigureAwait(false);
            if (match is null || !match.Application.IsEnabled)
            {
                return AuthenticateResult.Fail("Invalid Application credential.");
            }

            await applications.TouchCredentialUsageAsync(match.Application.Id, match.Credential.Id, DateTimeOffset.UtcNow, Context.RequestAborted).ConfigureAwait(false);
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, $"application:{match.Application.Id:D}"), new(ClaimTypes.Name, match.Application.Name), new(TuvimaClaimTypes.PrincipalKind, Domain.Authorization.PrincipalKind.ServiceApplication.ToString()), new(TuvimaClaimTypes.ApplicationId, match.Application.Id.ToString("D")), new(TuvimaClaimTypes.ApplicationAuthorizationVersion, match.Application.AuthorizationVersion.ToString()) };
            claims.Add(new(TuvimaClaimTypes.ApplicationCredentialId, match.Credential.Id.ToString("D")));
            claims.AddRange(match.Permissions.Select(p => new Claim(TuvimaClaimTypes.Scope, p.Value)));
            return Success(claims);
        }

        var auth = configurationLoader.LoadCore().Auth;
        if (environment.IsDevelopment() && auth.LocalhostBypass && IsLoopback(Context.Connection.RemoteIpAddress))
        {
            return Success([
                new Claim(ClaimTypes.NameIdentifier, "development:localhost"),
                new Claim(ClaimTypes.Name, "Development localhost"),
                new Claim(TuvimaClaimTypes.PrincipalKind, Domain.Authorization.PrincipalKind.HostRecovery.ToString()),
            ]);
        }

        return AuthenticateResult.NoResult();
    }

    private AuthenticateResult Success(IEnumerable<Claim> claims)
    {
        var identity = new ClaimsIdentity(claims, Scheme.Name, ClaimTypes.Name, ClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private static void AddSessionClaims(List<Claim> claims, SessionValidationResult result)
    {
        claims.Add(new Claim(ClaimTypes.NameIdentifier, result.Account.Id.ToString("D")));
        claims.Add(new Claim(ClaimTypes.Name, result.ActiveProfile.DisplayName));
        claims.RemoveAll(claim => claim.Type == TuvimaClaimTypes.PrincipalKind);
        claims.Add(new Claim(TuvimaClaimTypes.PrincipalKind, Domain.Authorization.PrincipalKind.Human.ToString()));
        claims.Add(new Claim(TuvimaClaimTypes.AccountId, result.Account.Id.ToString("D")));
        claims.Add(new Claim(TuvimaClaimTypes.ProfileId, result.ActiveProfile.Id.ToString("D")));
        claims.Add(new Claim(TuvimaClaimTypes.ActiveProfileId, result.ActiveProfile.Id.ToString("D")));
        claims.Add(new Claim(TuvimaClaimTypes.SessionId, result.Session.Id.ToString("D")));
        claims.Add(new Claim(TuvimaClaimTypes.DeviceId, result.Session.DeviceId));
        claims.Add(new Claim(TuvimaClaimTypes.DeviceClass, "web"));
        claims.Add(new Claim(TuvimaClaimTypes.ClientId, result.Session.Client));
        claims.Add(new Claim(TuvimaClaimTypes.AuthenticationMethod, result.Session.AuthenticationMethod));
        claims.Add(new Claim(TuvimaClaimTypes.AccountAuthorizationVersion, result.Account.AuthorizationVersion.ToString()));
        if (result.Account.MustChangePassword)
        {
            claims.Add(new Claim(TuvimaClaimTypes.PasswordChangeRequired, "true"));
        }
    }

    private static bool IsLoopback(System.Net.IPAddress? address) =>
        address is not null && System.Net.IPAddress.IsLoopback(address);
}
