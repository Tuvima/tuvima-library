using System.Security.Claims;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Endpoints;

public static class HlsStreamEndpoints
{
    public static IEndpointRouteBuilder MapHlsStreamEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMethods(
            "/stream/hls/{grant}/{packageId:guid}/{**resourcePath}",
            [HttpMethods.Get, HttpMethods.Head],
            HandleAsync)
            .WithName("GetAdaptiveHlsResource")
            .WithSummary("Serve one resource from a path-scoped adaptive HLS package grant.")
            .AllowAnonymous()
            .RequireRateLimiting("adaptive_streaming");
        return app;
    }

    private static async Task HandleAsync(
        string grant,
        Guid packageId,
        string? resourcePath,
        HttpContext context,
        HlsAccessGrantService grants,
        [Microsoft.AspNetCore.Mvc.FromServices] CatalogueResourceAuthorizationService authorization,
        AdaptiveHlsService hls,
        CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        var validation = await grants.ValidateAsync(grant, packageId, ct).ConfigureAwait(false);
        if (validation is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            validation.Scopes.Select(scope => new Claim(TuvimaClaimTypes.Scope, scope)),
            "hls-grant"));
        if (await authorization.EvaluateAssetAsync(
                validation.Authority,
                validation.AssetId,
                ApplicationPermissionIds.PlaybackRead,
                ct).ConfigureAwait(false) != CatalogueResourceAccess.Allowed)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await using var resource = await hls.OpenResourceAsync(
            packageId,
            validation.AssetId,
            resourcePath ?? string.Empty,
            ct).ConfigureAwait(false);
        if (resource is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = resource.ContentType;
        context.Response.ContentLength = resource.Stream.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await resource.Stream.CopyToAsync(context.Response.Body, ct).ConfigureAwait(false);
        }
    }
}
