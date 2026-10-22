using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Details.Internals;

/// <summary>
/// Effective permissions for detail-page actions. Presentation context is
/// deliberately excluded: an admin-looking surface is not authorization.
/// <see cref="IsHumanAdministrator"/> is narrower than <see cref="CanManageMetadata"/>: only a signed-in effective administrator, never a
/// service application or paired app acting with a permission. The Unmatched item routes demand exactly that.
/// </summary>
internal readonly record struct DetailActionAuthorizationContext(bool CanManageMetadata, bool IsHumanAdministrator = false)
{
    public bool Allows(string actionKey) => actionKey switch
    {
        "edit" or "identify" or "change-thumbnail" => CanManageMetadata,
        // The Unmatched actions of a single item need an effective administrator in person: the item routes they call refuse anyone else.
        DetailUnmatchedActions.RetryKey or DetailUnmatchedActions.KeepKey or DetailUnmatchedActions.LookUpAgainKey => IsHumanAdministrator,
        _ => false,
    };
}

internal static class DetailActionAuthorizationPolicy
{
    public static async Task<DetailActionAuthorizationContext> ResolveAsync(
        RequestAuthority authority,
        IAccountAccessDecisionService accounts,
        IAuthorizationEvaluator evaluator,
        CancellationToken ct)
    {
        if (authority.PrincipalKind == PrincipalKind.Human)
        {
            var administrator = (await accounts.EvaluateAdministratorAsync(
                authority, requireSurfaceUnlock: true, ct).ConfigureAwait(false)).IsAllowed;
            return new(administrator, administrator);
        }

        if (authority.PrincipalKind is not (PrincipalKind.DelegatedUserClient or PrincipalKind.ServiceApplication))
        {
            return new(false);
        }

        var application = await evaluator.EvaluateAsync(
            authority,
            new AuthorizationRequirement(ApplicationPermission: ApplicationPermissionIds.MetadataWrite),
            resource: null,
            ct).ConfigureAwait(false);
        if (!application.IsAllowed)
        {
            return new(false);
        }

        if (authority.PrincipalKind == PrincipalKind.ServiceApplication)
        {
            return new(true);
        }

        return new((await accounts.EvaluateAdministratorAsync(
            authority, requireSurfaceUnlock: true, ct).ConfigureAwait(false)).IsAllowed);
    }
}
