using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Details.Internals;

/// <summary>
/// Effective permissions for detail-page actions. Presentation context is
/// deliberately excluded: an admin-looking surface is not authorization.
/// </summary>
internal readonly record struct DetailActionAuthorizationContext(bool CanManageMetadata)
{
    public bool Allows(string actionKey) => actionKey switch
    {
        "edit" or "identify" or "change-thumbnail" => CanManageMetadata,
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
            return new((await accounts.EvaluateAdministratorAsync(
                authority, requireSurfaceUnlock: true, ct).ConfigureAwait(false)).IsAllowed);
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
