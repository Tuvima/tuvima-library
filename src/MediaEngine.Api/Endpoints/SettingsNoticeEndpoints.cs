using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Contracts.Settings;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The Settings attention lines (Needs Review, missing tools) a server administrator dismissed with the X. The
/// choice belongs to the active profile and is taken from the caller's own authority, never from the request.
/// </summary>
public static class SettingsNoticeEndpoints
{
    public static IEndpointRouteBuilder MapSettingsNoticeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/settings/notices").WithTags("Settings");

        group.MapGet("/", async (
            HttpContext http,
            IRequestAuthorityResolver resolver,
            [FromServices] ProfileNoticeService notices,
            CancellationToken ct) =>
        {
            var authority = await resolver.ResolveAsync(http, ct).ConfigureAwait(false);
            return authority.ActiveProfileId is { } profileId
                ? Results.Ok(await notices.ListAsync(profileId, ct).ConfigureAwait(false))
                : Results.Unauthorized();
        })
        .WithName("ListSettingsNotices")
        .WithSummary("Returns the Settings attention lines the active profile dismissed, each with what was true when it was dismissed.")
        .Produces<ProfileNoticesResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .RequireEffectiveAdministrator();

        group.MapPost("/{key}/dismiss", async (
            string key,
            DismissProfileNoticeRequest request,
            HttpContext http,
            IRequestAuthorityResolver resolver,
            [FromServices] ProfileNoticeService notices,
            CancellationToken ct) =>
        {
            var authority = await resolver.ResolveAsync(http, ct).ConfigureAwait(false);
            if (authority.ActiveProfileId is not { } profileId)
            {
                return Results.Unauthorized();
            }

            return await notices.DismissAsync(profileId, key, request.Fingerprint, ct).ConfigureAwait(false)
                ? Results.NoContent()
                : ApiErrors.BadRequest("That is not a notice the Library knows, or its fingerprint is missing or too long.");
        })
        .WithName("DismissSettingsNotice")
        .WithSummary("Remembers that the active profile dismissed a Settings attention line (needs-review or missing-tools) with the given fingerprint.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .RequireEffectiveAdministrator();

        return app;
    }
}
