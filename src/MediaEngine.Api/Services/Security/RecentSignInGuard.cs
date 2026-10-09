using System.Security.Claims;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Identity.Contracts;

namespace MediaEngine.Api.Services.Security;

/// <summary>
/// The "signed in recently" check for sensitive account actions. A sign-in older than ten minutes (and not
/// re-confirmed since) is answered with 403 <c>confirm_its_you</c>; the Dashboard asks for the password or passkey,
/// calls <c>POST /auth/confirm</c> and retries the action once.
/// </summary>
public sealed class RecentSignInGuard(IFirstPartyIdentityService identity)
{
    public const string Message = "Confirm it's you to continue.";

    /// <summary>The 403 problem body for a stale sign-in.</summary>
    public static IResult Refusal() => ApiErrors.Forbidden(RecentSignIn.ConfirmItsYouCode, Message);

    /// <summary>
    /// Null when the person signed in or confirmed recently and the action may go ahead; otherwise the refusal to return.
    /// A caller with no first-party session (for example an app token) cannot confirm, so it is refused too.
    /// </summary>
    public async Task<IResult?> RefuseIfStaleAsync(ClaimsPrincipal user, CancellationToken ct) =>
        Guid.TryParse(user.FindFirstValue(TuvimaClaimTypes.SessionId), out var sessionId)
        && await identity.IsRecentlyAuthenticatedAsync(sessionId, ct).ConfigureAwait(false)
            ? null
            : Refusal();

    /// <summary>
    /// Like <see cref="RefuseIfStaleAsync"/> but only for a person's own session: an application or service credential
    /// has no sign-in to confirm and keeps its existing permission checks.
    /// </summary>
    public Task<IResult?> RefuseHumanIfStaleAsync(ClaimsPrincipal user, CancellationToken ct) =>
        user.FindFirstValue(TuvimaClaimTypes.PrincipalKind) == nameof(PrincipalKind.Human)
            ? RefuseIfStaleAsync(user, ct)
            : Task.FromResult<IResult?>(null);
}
