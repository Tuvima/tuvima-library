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

    /// <summary>
    /// Signs this screen out now: the screen's credentials are cleared first, so even a browser that ignores the
    /// redirect can no longer reach the Engine through this circuit, then the layout navigates to sign-in.
    /// </summary>
    public void SignOutScreen()
    {
        EndScreenSession();
        SetAuthenticationState(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));
    }

    public async Task<bool> IsStillSignedInAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        if (!session.InitializeFromPrincipal(user))
        {
            EndScreenSession();
            return false;
        }

        // The place the screen was opened from must still be allowed by "who can connect". A missing claim is
        // treated as outside the home (fail closed).
        var ingress = user.FindFirstValue(DashboardPrincipalFactory.ClientIngressClaim);
        if (await ReadWhoCanConnectAsync(ct).ConfigureAwait(false) is { } whoCanConnect
            && !OpenScreenRegistry.IsIngressAllowed(whoCanConnect, ingress))
        {
            EndScreenSession();
            return false;
        }

        try
        {
            var check = await identity.RevalidateAuthorityDetailedAsync(session, ct).ConfigureAwait(false);
            if (check.Status is SessionCheckStatus.Valid or SessionCheckStatus.Unknown)
            {
                return true;
            }

            EndScreenSession();
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The Engine could not be reached. Throttling, timeouts and restarts must not sign everyone out;
            // only a proven revoked sign-in does. The next check retries.
            _logger.LogWarning(exception, "Could not re-check an open screen's sign-in; the next check will retry.");
            return true;
        }
    }

    /// <summary>
    /// The current "who can connect", or null when the file could not be read twice in a row (for example while
    /// the Engine is saving it). Null means "no verdict": the Engine check still runs and the next minute retries.
    /// </summary>
    private async Task<string?> ReadWhoCanConnectAsync(CancellationToken ct)
    {
        if (exposure.TryRead(out var value))
        {
            return value;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        return exposure.TryRead(out value) ? value : null;
    }

    // Clears the circuit's session so the forwarding handler sends no token: a signed-out screen has no Engine access.
    private void EndScreenSession() => session.Set(null, null, null, null, null);
}
