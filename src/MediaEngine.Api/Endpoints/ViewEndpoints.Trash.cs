using MediaEngine.Api.Http;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// What is in a person's own Trash and what emptying it would do. Both routes are for a person signed in to the Dashboard
/// (never a paired app or a service application), for the Mine scope only, and neither changes anything: files leave the
/// disk only through Empty Trash, which is a separate action that must repeat these checks.
/// </summary>
public static partial class ViewEndpoints
{
    private static void MapTrash(RouteGroupBuilder group)
    {
        group.MapGet("/trash/summary", async (IViewRequestProfileContext identity,
            ViewTrashDeletionPlanner planner, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            var refusal = TrashCallerRefusal(authority);
            if (refusal is not null)
            {
                return refusal;
            }

            var (status, summary) = await planner.GetSummaryAsync(authority, ct);
            return status == ViewTrashPlanStatus.Ok ? Results.Ok(summary) : TrashRefusal(status);
        }).WithName("GetViewTrashSummary")
            .WithSummary("Count and size of the signed-in person's own Trash, for the Trash view's size line.")
            .Produces<ViewTrashSummaryDto>();

        group.MapPost("/trash/empty-preview", async (IViewRequestProfileContext identity,
            ViewTrashDeletionPlanner planner, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            var refusal = TrashCallerRefusal(authority);
            if (refusal is not null)
            {
                return refusal;
            }

            var result = await planner.BuildPlanAsync(authority, ct);
            return result.Status == ViewTrashPlanStatus.Ok ? Results.Ok(result.Plan!.Preview) : TrashRefusal(result.Status);
        }).WithName("PreviewViewTrashEmpty")
            .WithSummary("What Empty Trash would do right now for the signed-in person's own Trash, with a fingerprint to confirm. Deletes nothing.")
            .Produces<ViewTrashEmptyPreviewDto>();
    }

    /// <summary>
    /// A person signed in as themselves only: a paired app's device token (DelegatedUserClient) and a service application are
    /// refused with 403, because their photo permissions are for viewing and Trash is managed from the Dashboard.
    /// </summary>
    private static IResult? TrashCallerRefusal(RequestAuthority authority)
    {
        if (!authority.IsAuthenticated)
        {
            return Unauthenticated();
        }

        return authority.PrincipalKind != PrincipalKind.Human
            ? ApiErrors.Forbidden("Only a signed-in person can manage their Trash.")
            : null;
    }

    private static IResult TrashRefusal(ViewTrashPlanStatus status) => status switch
    {
        ViewTrashPlanStatus.Unauthenticated => Unauthenticated(),
        ViewTrashPlanStatus.Forbidden => ApiErrors.Forbidden("View access is not permitted."),
        _ => Missing(),
    };
}
