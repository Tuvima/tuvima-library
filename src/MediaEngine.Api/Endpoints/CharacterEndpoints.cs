using MediaEngine.Api.Http;
using MediaEngine.Api.Models;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Artwork;
using MediaEngine.Contracts.Characters;
using MediaEngine.Domain;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Library character endpoints — the character portrait image stream,
/// and manual Stage 3 trigger.
///
/// <list type="bullet">
/// <item><c>GET  /library/portraits/{portraitId}</c> — serves a character portrait image</item>
/// <item><c>POST /library/enrichment/universe/trigger</c> — manual Stage 3 trigger</item>
/// </list>
/// </summary>
public static class CharacterEndpoints
{
    public static IEndpointRouteBuilder MapCharacterEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/library")
                       .WithTags("Library Characters");

        // GET /library/portraits/{portraitId}
        // Serves a character portrait from local storage, downloading and caching if needed.
        group.MapGet("/portraits/{portraitId:guid}", async (
            Guid portraitId,
            string? size,
            HttpContext httpContext,
            ICharacterPortraitRepository portraitRepo,
            IHttpClientFactory httpFactory,
            AssetPathService assetPaths,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var portrait = await portraitRepo.FindByIdAsync(portraitId, ct);
            if (portrait is null)
            {
                return ApiErrors.NotFound($"Portrait '{portraitId}' not found.");
            }

            if (string.IsNullOrWhiteSpace(portrait.LocalImagePath) || !File.Exists(portrait.LocalImagePath))
            {
                if (string.IsNullOrWhiteSpace(portrait.ImageUrl)
                    || !Uri.TryCreate(portrait.ImageUrl, UriKind.Absolute, out var imageUri)
                    || (imageUri.Scheme != Uri.UriSchemeHttp && imageUri.Scheme != Uri.UriSchemeHttps))
                {
                    return ApiErrors.NotFound("Portrait image source not found.");
                }

                using var client = httpFactory.CreateClient("cover_download");
                using var response = await client.GetAsync(imageUri, ct);
                if (!response.IsSuccessStatusCode)
                {
                    return ApiErrors.NotFound("Portrait image source could not be retrieved.");
                }

                var bytesFromSource = await BoundedHttpContent.ReadImageAsync(response.Content, ct);
                if (bytesFromSource.Length == 0)
                {
                    return ApiErrors.NotFound("Portrait image source was empty.");
                }

                var localPath = string.IsNullOrWhiteSpace(portrait.LocalImagePath)
                    ? assetPaths.GetCharacterPortraitPath(
                        portrait.PersonId,
                        portrait.FictionalEntityId,
                        MediaMimeTypes.InferImageExtension(portrait.ImageUrl) ?? ".jpg")
                    : portrait.LocalImagePath;

                AssetPathService.EnsureDirectory(localPath);
                await BoundedHttpContent.WriteFileAtomicallyAsync(localPath, bytesFromSource, ct);

                portrait.LocalImagePath = localPath;
                portrait.UpdatedAt = DateTimeOffset.UtcNow;
                await portraitRepo.UpsertAsync(portrait, ct);
            }

            // Served at the size asked for: a copy made the first time and kept beside the managed pictures.
            var resolved = await CharacterPortraitRenditions.ResolveAsync(
                portraitId,
                portrait.LocalImagePath,
                size,
                assetPaths,
                loggerFactory.CreateLogger("MediaEngine.Api.CharacterPortraits"),
                ct);
            return ArtworkFileResults.Local(
                       resolved.Path,
                       ArtworkFileResults.ModeFor(
                           httpContext.Request,
                           ArtworkVersionMarker.ForFile(portrait.LocalImagePath),
                           resolved.IsRequestedSize))
                   ?? ApiErrors.NotFound("Portrait image not found.");
        })
        .WithName("GetCharacterPortrait")
        .Produces(StatusCodes.Status200OK)
        .RequireClientScope(ApplicationPermissionIds.ArtworkRead.Value)
        .RequireCatalogueCharacterPortraitAccess(ApplicationPermissionIds.ArtworkRead);

        // POST /library/enrichment/universe/trigger
        // Manually trigger Stage 3 universe enrichment on the next cycle.
        group.MapPost("/enrichment/universe/trigger", async (
            IMetadataHarvestingService harvesting,
            CancellationToken ct) =>
        {
            // Enqueue a single representative work for immediate processing by setting
            // a short-circuit flag via the harvest queue.  Since UniverseEnrichmentService
            // runs as a BackgroundService we can't call it directly; instead we use
            // IMetadataHarvestingService to enqueue a "universe_sweep" hint.
            await harvesting.EnqueueAsync(new MediaEngine.Domain.Models.HarvestRequest
            {
                EntityId = Guid.Empty,
                EntityType = MediaEngine.Domain.Enums.EntityType.Character,
                MediaType = MediaEngine.Domain.Enums.MediaType.Unknown,
                Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["trigger_type"] = "universe_sweep",
                    ["requested_by"] = "library_manual",
                },
            }, ct);

            return Results.Ok(new UniverseEnrichmentTriggerResponse(triggered: true, message: "Universe enrichment sweep queued."));
        })
        .Produces<UniverseEnrichmentTriggerResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataEnrichmentRun);

        return app;
    }
}

