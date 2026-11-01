using MediaEngine.Api.Security;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Collections;

/// <summary>
/// Who is making a collection request and what they may do with collections (Collections and lists spec D19: everyone makes
/// their own, administrators push to everyone). Resolved once per request from the caller's identity, never from a flag the
/// caller sends.
/// </summary>
/// <param name="Profile">The profile the caller is acting as, or null for an app that acts for nobody.</param>
/// <param name="IsAdministrator">
/// True for an administrator on the Dashboard with the administrator PIN unlocked where one is set, and for a paired app or
/// service application that already cleared the route's collection-write permission.
/// </param>
internal sealed record CollectionCaller(Profile? Profile, bool IsAdministrator)
{
    /// <summary>
    /// Settles the caller for this request. A person on the Dashboard is an administrator only when the same check the
    /// administrator-only routes use passes (including the surface PIN). An application reaches the collection routes only with
    /// the collection-write permission (and a paired app only for an administrator), so it keeps the full rights it had.
    /// </summary>
    public static async Task<CollectionCaller> ResolveAsync(
        HttpContext httpContext,
        Guid? profileId,
        IProfileRepository profiles,
        CancellationToken ct)
    {
        var services = httpContext.RequestServices;
        var authority = await services.GetRequiredService<IRequestAuthorityResolver>()
            .ResolveAsync(httpContext, ct).ConfigureAwait(false);

        Profile? profile = null;
        if (authority.ActiveProfileId is { } activeProfileId && (!profileId.HasValue || profileId.Value == activeProfileId))
        {
            profile = await profiles.GetByIdAsync(activeProfileId, ct).ConfigureAwait(false);
        }

        var isAdministrator = authority.PrincipalKind != PrincipalKind.Human
            || (await services.GetRequiredService<IAccountAccessDecisionService>()
                .EvaluateAdministratorAsync(authority, true, ct).ConfigureAwait(false)).IsAllowed;
        return new CollectionCaller(profile, isAdministrator);
    }
}
