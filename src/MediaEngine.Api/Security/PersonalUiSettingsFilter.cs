using MediaEngine.Api.Http;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Security;

/// <summary>Rejects another profile before reading or writing its configuration.</summary>
internal sealed class PersonalUiSettingsFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var authority = await http.RequestServices.GetRequiredService<IRequestAuthorityResolver>()
            .ResolveAsync(http, http.RequestAborted);
        if (authority.PrincipalKind != PrincipalKind.Human || !authority.IsAuthenticated ||
            !authority.AccountEnabled || !authority.ActiveProfileInUserHousehold || !authority.HasHumanContext)
        {
            return ApiErrors.NotFound("Profile not found.");
        }

        var requested = http.Request.RouteValues["profileId"]?.ToString()
            ?? http.Request.Query["profile"].FirstOrDefault();
        if (requested is not null &&
            (!Guid.TryParse(requested, out var profileId) || profileId != authority.ActiveProfileId))
        {
            return ApiErrors.NotFound("Profile not found.");
        }

        return await next(context);
    }
}
