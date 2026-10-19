using MediaEngine.Api.Security;

namespace MediaEngine.Api.DevSupport;

/// <summary>
/// The one way development-only actions are grouped. They reset, wipe, seed or list the
/// whole library, so every one of them requires an administrator, not just a signed-in user.
/// </summary>
internal static class DevelopmentRouteGroup
{
    public static RouteGroupBuilder MapDevelopmentGroup(this IEndpointRouteBuilder app) =>
        app.MapGroup("/dev")
            .WithTags("Development")
            .RequireEffectiveAdministrator();
}
