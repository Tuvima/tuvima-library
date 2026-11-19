using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The "built-in map only" choice (spec view-people-search WP2). It is one person's own setting, read and changed only by that person
/// through the same preference authority as the other View settings; nobody else's setting is ever reachable.
/// </summary>
public static partial class ViewEndpoints
{
    private static void MapMapPreference(RouteGroupBuilder group)
    {
        group.MapGet("/preferences/map", async (IViewRequestProfileContext identity, IViewProfileRepository repository,
            IViewResourceAuthorizationService authorization, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is not { } profileId)
            {
                return Unauthenticated();
            }

            var access = await authorization.AuthorizeAsync(authority,
                new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Preference, null), ct);
            return access.IsAllowed
                ? Results.Ok(new ViewMapPreferenceDto((await repository.GetPreferencesAsync(profileId, ct)).BuiltInMapOnly))
                : Access(access.Outcome);
        }).WithName("GetViewMapPreference").Produces<ViewMapPreferenceDto>();

        group.MapPut("/preferences/map", async ([FromBody] ViewMapPreferenceDto request,
            IViewRequestProfileContext identity, IViewProfileRepository repository,
            IViewResourceAuthorizationService authorization, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is not { } profileId)
            {
                return Unauthenticated();
            }

            var access = await authorization.AuthorizeAsync(authority,
                new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Preference, null, ViewResourceAction.Manage), ct);
            if (!access.IsAllowed)
            {
                return Access(access.Outcome);
            }

            return await repository.SaveBuiltInMapOnlyAsync(profileId, request.BuiltInOnly, ct)
                ? Results.Ok(request)
                : Missing();
        }).WithName("UpdateViewMapPreference").Produces<ViewMapPreferenceDto>();
    }
}
