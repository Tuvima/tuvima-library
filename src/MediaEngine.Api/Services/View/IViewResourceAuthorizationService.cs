using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Services.View;

public interface IViewResourceAuthorizationService
{
    Task<ViewAccessOutcome> AuthorizePersonalScopeBootstrapAsync(
        RequestAuthority caller,
        CancellationToken ct = default);

    Task<ViewAccessDecision> AuthorizeAsync(
        RequestAuthority caller,
        ViewResourceRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Decides many items of one kind for the Mine scope at once. The answer is the same as asking
    /// <see cref="AuthorizeAsync"/> for each id; implementations resolve the capability and scope once and look the items up
    /// in bulk. An id that cannot be read is simply absent from <see cref="ViewBulkAccessDecision.AllowedIds"/>.
    /// </summary>
    async Task<ViewBulkAccessDecision> AuthorizeManyAsync(
        RequestAuthority caller,
        ViewResourceKind kind,
        IReadOnlyCollection<Guid> resourceIds,
        CancellationToken ct = default)
    {
        var allowed = new HashSet<Guid>();
        foreach (var id in resourceIds.Where(id => id != Guid.Empty).Distinct())
        {
            var decision = await AuthorizeAsync(
                caller, new ViewResourceRequest(ViewScopeRequest.Mine, kind, id), ct).ConfigureAwait(false);
            if (decision.IsAllowed)
            {
                allowed.Add(id);
            }
            else if (decision.Outcome is ViewAccessOutcome.Forbidden or ViewAccessOutcome.Unauthenticated)
            {
                return new(decision.Outcome, new HashSet<Guid>());
            }
        }

        return new(ViewAccessOutcome.Allowed, allowed);
    }
}

/// <summary>The answer to a bulk authorization: <see cref="Outcome"/> is Forbidden or Unauthenticated when the caller lacks the capability, otherwise Allowed, with the ids that may be read.</summary>
public sealed record ViewBulkAccessDecision(ViewAccessOutcome Outcome, IReadOnlySet<Guid> AllowedIds);

