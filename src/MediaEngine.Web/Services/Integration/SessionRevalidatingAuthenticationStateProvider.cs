using System.Security.Claims;
using MediaEngine.Web.Services.Configuration;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Keeps an open Dashboard screen honest. Every minute it asks the Engine whether the screen's sign-in still
/// stands and whether the place the screen was opened from is still allowed; if not, the screen is signed out
/// and the layout sends it to the sign-in page. This is the one Engine check per open screen per minute: it also
/// refreshes what the person may do, so nothing else polls.
/// </summary>
public sealed class SessionRevalidatingAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    DashboardIdentityClient identity,
    DashboardSessionAccessor session,
    ExposureSettingsReader exposure)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<SessionRevalidatingAuthenticationStateProvider>();

    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(60);

    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken) =>
        IsStillSignedInAsync(authenticationState.User, cancellationToken);

    /// <summary>Signs this screen out now. The layout reacts by navigating to the sign-in page.</summary>
    public void SignOutScreen() =>
        SetAuthenticationState(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));

    public async Task<bool> IsStillSignedInAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        if (!session.InitializeFromPrincipal(user))
        {
            return false;
        }

        // The place the screen was opened from must still be allowed by "who can connect". A missing claim is
        // treated as outside the home (fail closed).
        var ingress = user.FindFirstValue(DashboardPrincipalFactory.ClientIngressClaim);
        if (!OpenScreenRegistry.IsIngressAllowed(exposure.WhoCanConnect, ingress))
        {
            return false;
        }

        try
        {
            var check = await identity.RevalidateAuthorityDetailedAsync(session, ct).ConfigureAwait(false);
            return check.Status is SessionCheckStatus.Valid or SessionCheckStatus.Unknown;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The Engine could not be reached. Throttling, timeouts and restarts must not sign everyone out;
            // only a proven revoked sign-in does. The next check retries.
            _logger.LogWarning(exception, "Could not re-check an open screen's sign-in; the next check will retry.");
            return true;
        }
    }
}
