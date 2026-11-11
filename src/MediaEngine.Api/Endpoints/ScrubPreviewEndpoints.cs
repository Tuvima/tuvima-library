using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Scrub previews (Unified player v2 D9a): the index and sprite sheets of a video, served behind exactly the access checks that
/// streaming the file itself uses (profile access, Vault, Kid limits). A profile that may not play the file gets 404, never a hint
/// that previews exist. Also the administrator's switch and space-used report.
/// </summary>
public static class ScrubPreviewEndpoints
{
    public static IEndpointRouteBuilder MapScrubPreviewEndpoints(this IEndpointRouteBuilder app)
    {
        var stream = app.MapGroup("/stream").WithTags("Streaming");

        stream.MapGet("/{assetId:guid}/scrub/index.json", (
            Guid assetId,
            HttpContext context,
            ScrubPreviewCache store,
            IConfigurationLoader configuration) =>
                Serve(context, configuration, store.ResolveIndexFile(assetId), "application/json"))
            .WithName("GetScrubPreviewIndex")
            .WithSummary("The scrub-preview index of a video: frame spacing, frame size and the sprite sheets.")
            .Produces<ScrubPreviewIndexDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireClientScope(ClientApiScopes.PlaybackRead)
            .RequireCatalogueAssetAccess(ApplicationPermissionIds.PlaybackRead, deniedAsNotFound: true)
            .RequireRateLimiting("streaming");

        stream.MapGet("/{assetId:guid}/scrub/{sheet}", (
            Guid assetId,
            string sheet,
            HttpContext context,
            ScrubPreviewCache store,
            IConfigurationLoader configuration) =>
                Serve(context, configuration, store.ResolveSheetFile(assetId, sheet), "image/jpeg"))
            .WithName("GetScrubPreviewSheet")
            .WithSummary("One scrub-preview sprite sheet of a video.")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireClientScope(ClientApiScopes.PlaybackRead)
            .RequireCatalogueAssetAccess(ApplicationPermissionIds.PlaybackRead, deniedAsNotFound: true)
            .RequireRateLimiting("streaming");

        var settings = app.MapGroup("/settings/playback/scrub-previews").WithTags("Settings");

        settings.MapGet("/", (ScrubPreviewSettingsService service) => Results.Ok(service.GetStatus()))
            .WithName("GetScrubPreviewSettings")
            .WithSummary("Whether scrub previews are made, how much space they use and whether FFmpeg is there to make them.")
            .Produces<ScrubPreviewStatusDto>(StatusCodes.Status200OK)
            .RequireEffectiveAdministrator();

        settings.MapPut("/", (UpdateScrubPreviewSettingRequestDto request, ScrubPreviewSettingsService service) =>
                Results.Ok(service.SetEnabled(request.Enabled)))
            .WithName("UpdateScrubPreviewSettings")
            .WithSummary("Turns scrub-preview generation on or off. Off removes every preview that was made.")
            .Produces<ScrubPreviewStatusDto>(StatusCodes.Status200OK)
            .RequireEffectiveAdministrator();

        return app;
    }

    private static IResult Serve(HttpContext context, IConfigurationLoader configuration, string? path, string contentType)
    {
        if (path is null || !configuration.LoadTranscoding().ScrubPreviews.Enabled)
        {
            return ApiErrors.NotFound("No scrub preview is available for this item.");
        }

        context.Response.Headers.CacheControl = "private, max-age=3600";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(path, contentType);
    }
}
