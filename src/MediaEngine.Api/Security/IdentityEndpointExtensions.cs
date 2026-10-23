namespace MediaEngine.Api.Security;

/// <summary>
/// Endpoint metadata recording which authority class a route requires. Attached by every
/// <c>Require*</c> extension in <see cref="IdentityEndpointExtensions"/> so guardrail
/// tests and diagnostics can discover authority requirements from endpoint metadata
/// instead of re-deriving them from the filter pipeline.
/// </summary>
public sealed record AuthorityRequirementMetadata(string Requirement);

// ── Convenience extension methods ───────────────────────────────────────────

/// <summary>
/// Endpoint guards backed by live user and profile authority policies (roles and access come from the profile being acted as).
/// </summary>
public static class IdentityEndpointExtensions
{
    /// <summary>Restricts the endpoint to Administrators only.</summary>
    public static RouteHandlerBuilder RequireAdmin(this RouteHandlerBuilder builder) =>
        builder.RequireAuthorization(AuthPolicies.Administrator)
               .WithMetadata(new AuthorityRequirementMetadata("effective_administrator_surface"));

    public static RouteHandlerBuilder RequireHumanSelfService(this RouteHandlerBuilder builder) =>
        builder.RequireAuthorization(AuthPolicies.HumanSelfService)
               .WithMetadata(new AuthorityRequirementMetadata("human_self_service"));

    public static RouteGroupBuilder RequireHumanSelfService(this RouteGroupBuilder builder) =>
        builder.RequireAuthorization(AuthPolicies.HumanSelfService)
               .WithMetadata(new AuthorityRequirementMetadata("human_self_service"));

    /// <summary>Restricts every endpoint in the group to Administrators only.</summary>
    public static RouteGroupBuilder RequireAdmin(this RouteGroupBuilder builder) =>
        builder.RequireAuthorization(AuthPolicies.Administrator)
               .WithMetadata(new AuthorityRequirementMetadata("effective_administrator_surface"));

}
