using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Artwork;
using MediaEngine.Api.Services.Details;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Capabilities;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Processors.Contracts;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Workers;
using Microsoft.Net.Http.Headers;

namespace MediaEngine.Api.Endpoints;

public static class StreamEndpoints
{
    private static readonly Dictionary<string, string> MimeMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".mp4"] = "video/mp4",
            [".m4v"] = "video/x-m4v",
            [".mkv"] = "video/x-matroska",
            [".webm"] = "video/webm",
            [".avi"] = "video/x-msvideo",
            [".mp3"] = "audio/mpeg",
            [".m4a"] = "audio/mp4",
            [".m4b"] = "audio/mp4",
            [".aac"] = "audio/aac",
            [".flac"] = "audio/flac",
            [".ogg"] = "audio/ogg",
            [".wav"] = "audio/wav",
            [".epub"] = "application/epub+zip",
            [".cbz"] = "application/x-cbz",
            [".cbr"] = "application/x-cbr",
            [".pdf"] = "application/pdf",
        };

    public static IEndpointRouteBuilder MapStreamEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/stream")
                       .WithTags("Streaming");

        group.MapGet("/{assetId:guid}", async (
            Guid assetId,
            HttpContext ctx,
            IMediaAssetRepository assetRepo,
            IByteStreamer streamer,
            CancellationToken ct) =>
        {
            var asset = await assetRepo.FindByIdAsync(assetId, ct);
            if (asset is null)
            {
                return ApiErrors.NotFound($"Asset '{assetId}' not found.");
            }

            if (!File.Exists(asset.FilePathRoot))
            {
                return Results.Problem(
                    detail: $"File not found on disk: {asset.FilePathRoot}",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var ext = Path.GetExtension(asset.FilePathRoot);
            var mimeType = MimeMap.GetValueOrDefault(ext, "application/octet-stream");

            await ByteRangeResponder.WriteFileAsync(ctx, streamer, asset.FilePathRoot, mimeType, headOnly: false, ct);
            return Results.Empty;
        })
        .WithName("StreamAsset")
        .WithSummary("Stream a media asset with HTTP 206 byte-range support.")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status206PartialContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.PlaybackRead)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.PlaybackRead)
        .RequireRateLimiting("streaming");

        group.MapGet("/artwork/{variantId:guid}", async (
            Guid variantId,
            string? size,
            HttpContext httpContext,
            IEntityAssetRepository entityAssetRepo,
            IArtworkRenditionService renditions,
            IHttpClientFactory httpFactory,
            CancellationToken ct) =>
        {
            var variant = await entityAssetRepo.FindByIdAsync(variantId, ct);
            if (variant is null)
            {
                return ApiErrors.NotFound($"Artwork variant '{variantId}' not found.");
            }

            var hasRequestedSize = !string.IsNullOrWhiteSpace(size);
            var normalizedSize = NormalizeArtworkSize(size);
            if (hasRequestedSize && normalizedSize is null)
            {
                return ApiErrors.BadRequest("Artwork size must be one of 't', 's', 'm', or 'l'.");
            }

            var resolved = await renditions.ResolveAsync(variant, normalizedSize, ct);
            var localArtworkResult = resolved is null
                ? null
                : ArtworkFileResults.Local(
                    resolved.Path,
                    ArtworkFileResults.ModeFor(
                        httpContext.Request,
                        ArtworkVersionMarker.ForFile(variant.LocalImagePath),
                        resolved.IsRequestedSize));
            if (localArtworkResult is not null)
            {
                return localArtworkResult;
            }

            if (normalizedSize is not null)
            {
                return CreateArtworkPlaceholderResult();
            }

            if (!string.IsNullOrWhiteSpace(variant.ImageUrl)
                && Uri.TryCreate(variant.ImageUrl, UriKind.Absolute, out var imageUri)
                && (imageUri.Scheme == Uri.UriSchemeHttp || imageUri.Scheme == Uri.UriSchemeHttps))
            {
                using var client = httpFactory.CreateClient("cover_download");
                using var response = await client.GetAsync(imageUri, ct);
                if (!response.IsSuccessStatusCode)
                {
                    return CreateArtworkPlaceholderResult();
                }

                var bytes = await BoundedHttpContent.ReadImageAsync(response.Content, ct);
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                return Results.File(bytes, contentType);
            }

            if (!string.IsNullOrWhiteSpace(variant.ImageUrl)
                && variant.ImageUrl.StartsWith("/", StringComparison.Ordinal))
            {
                return Results.Redirect(variant.ImageUrl);
            }

            return CreateArtworkPlaceholderResult();
        })
        .WithName("GetArtworkVariant")
        .WithSummary("Serve artwork by variant id.")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status302Found)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.ArtworkRead)
        .RequireCatalogueArtworkAccess(ApplicationPermissionIds.ArtworkRead);

        group.MapGet("/entity/{entityType}/{entityId:guid}/cover", async (
            string entityType,
            Guid entityId,
            string? size,
            IEntityAssetRepository entityAssetRepo,
            IArtworkRenditionService renditions,
            DetailComposerService detailComposer,
            ICanonicalValueRepository canonicalRepo,
            IHttpClientFactory httpFactory,
            CancellationToken ct) =>
        {
            if (!DetailComposerService.TryParseEntityType(entityType, out var parsedEntityType))
            {
                return ApiErrors.BadRequest($"Unsupported detail entity type '{entityType}'.");
            }

            var preferredVariant = await entityAssetRepo.GetPreferredAsync(entityId.ToString(), "CoverArt", ct);
            var localArtworkResult = await ServeStableAsync(renditions, preferredVariant, size, ct);
            if (localArtworkResult is not null)
            {
                return localArtworkResult;
            }

            var imageUrl = preferredVariant?.ImageUrl;
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                var values = await canonicalRepo.GetByEntityAsync(entityId, ct);
                imageUrl = new[] { "cover_url", "cover", "poster_url", "poster" }
                    .Select(key => values.FirstOrDefault(value => value.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            }
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                var detail = await detailComposer.BuildAsync(
                    parsedEntityType,
                    entityId,
                    DetailPresentationContext.Default,
                    ct);
                imageUrl = detail?.Artwork.CoverUrl
                    ?? detail?.Artwork.PosterUrl
                    ?? detail?.Artwork.HeroArtwork.Url;
            }

            if (!string.IsNullOrWhiteSpace(imageUrl)
                && imageUrl.StartsWith("/", StringComparison.Ordinal)
                && !imageUrl.StartsWith($"/stream/entity/{entityType}/", StringComparison.OrdinalIgnoreCase))
            {
                if (NormalizeArtworkSize(size) is { } rendition && imageUrl.StartsWith("/stream/artwork/", StringComparison.OrdinalIgnoreCase))
                {
                    imageUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(imageUrl, "size", rendition);
                }
                return Results.Redirect(imageUrl);
            }

            if (!string.IsNullOrWhiteSpace(imageUrl)
                && Uri.TryCreate(imageUrl, UriKind.Absolute, out var imageUri)
                && (imageUri.Scheme == Uri.UriSchemeHttp || imageUri.Scheme == Uri.UriSchemeHttps))
            {
                using var client = httpFactory.CreateClient("cover_download");
                using var response = await client.GetAsync(imageUri, ct);
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await BoundedHttpContent.ReadImageAsync(response.Content, ct);
                    var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                    return Results.File(bytes, contentType);
                }
            }

            return CreateArtworkPlaceholderResult();
        })
        .WithName("GetEntityCover")
        .WithSummary("Serve the same managed or canonical cover artwork used by a detail page.")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.ArtworkRead)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.ArtworkRead);

        group.MapGet("/{assetId:guid}/cover", async (
            Guid assetId,
            string? size,
            IMediaAssetRepository assetRepo,
            IWorkRepository workRepo,
            IEntityAssetRepository entityAssetRepo,
            IArtworkRenditionService renditions,
            CancellationToken ct) =>
        {
            var asset = await assetRepo.FindByIdAsync(assetId, ct);
            if (asset is null)
            {
                return ApiErrors.NotFound($"Asset '{assetId}' not found.");
            }

            var ownerEntityId = await ResolveArtworkOwnerEntityIdAsync(assetId, workRepo, ct);
            var preferredVariant = await entityAssetRepo.GetPreferredAsync(ownerEntityId.ToString(), "CoverArt", ct);
            var localArtworkResult = await ServeStableAsync(renditions, preferredVariant, size, ct);
            if (localArtworkResult is not null)
            {
                return localArtworkResult;
            }

            return CreateArtworkPlaceholderResult();
        })
        .WithName("GetAssetCover")
        .WithSummary("Serve the preferred centrally-managed cover artwork for a media asset.")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.ArtworkRead)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.ArtworkRead);
        // NOTE: No rate limit — cover art is small, cacheable, and loaded in bulk on
        // Home/category pages (dozens per reload). The streaming policy (100/min) is
        // sized for true media streams, not static thumbnails.

        group.MapGet("/{assetId:guid}/text-tracks", async (
            Guid assetId,
            IMediaAssetRepository assetRepo,
            ITextTrackRepository textTrackRepo,
            CancellationToken ct) =>
        {
            var asset = await assetRepo.FindByIdAsync(assetId, ct);
            if (asset is null)
            {
                return ApiErrors.NotFound($"Asset '{assetId}' not found.");
            }

            var tracks = await textTrackRepo.GetByAssetAsync(assetId, null, ct);
            return Results.Ok(tracks.Select(t => new TextTrackDto
            {
                Id = t.Id,
                Kind = t.Kind.ToString(),
                Language = t.Language,
                Provider = t.Provider,
                Confidence = t.Confidence,
                SourceFormat = t.SourceFormat,
                NormalizedFormat = t.NormalizedFormat,
                TimingMode = t.TimingMode,
                IsHearingImpaired = t.IsHearingImpaired,
                IsPreferred = t.IsPreferred,
                IsUserOwned = t.IsUserOwned,
                IsLocallyExported = t.SidecarPath is not null,
                Url = $"/stream/{assetId}/text-tracks/{t.Id}",
            }));
        })
        .WithName("GetAssetTextTracks")
        .WithSummary("List lyrics and subtitle tracks available for a media asset.")
        .Produces<IReadOnlyList<TextTrackDto>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.PlaybackRead)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.PlaybackRead);

        group.MapGet("/{assetId:guid}/text-tracks/{trackId:guid}", async (
            Guid assetId,
            Guid trackId,
            ITextTrackRepository textTrackRepo,
            CancellationToken ct) =>
        {
            var track = await textTrackRepo.FindByIdAsync(trackId, ct);
            if (track is null || track.AssetId != assetId)
            {
                return ApiErrors.NotFound("Text track not found for this asset.");
            }

            if (string.IsNullOrWhiteSpace(track.LocalPath) || !File.Exists(track.LocalPath))
            {
                return ApiErrors.NotFound("The managed text-track file is unavailable.");
            }

            var bytes = await File.ReadAllBytesAsync(track.LocalPath, ct);
            var contentType = track.Kind == TextTrackKind.Lyrics
                ? "text/plain; charset=utf-8"
                : "text/vtt; charset=utf-8";
            return Results.File(bytes, contentType, Path.GetFileName(track.LocalPath));
        })
        .WithName("GetAssetTextTrack")
        .WithSummary("Serve one exact managed lyrics or subtitle variant.")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.PlaybackRead)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.PlaybackRead);

        group.MapPost("/{assetId:guid}/text-tracks/{trackId:guid}/preferred", async (
            Guid assetId,
            Guid trackId,
            ITextTrackRepository textTrackRepo,
            IMediaAssetRepository assetRepo,
            ITextTrackExportService exportService,
            CancellationToken ct) =>
        {
            var track = await textTrackRepo.FindByIdAsync(trackId, ct);
            if (track is null || track.AssetId != assetId)
            {
                return ApiErrors.NotFound("Text track not found for this asset.");
            }

            await textTrackRepo.SetPreferredAsync(trackId, ct);

            // A changed lyrics choice replaces the one lyrics file the Library wrote beside the song (only when write-back allows it).
            if (track.Kind == TextTrackKind.Lyrics && await assetRepo.FindByIdAsync(assetId, ct) is { } asset)
            {
                var chosen = await textTrackRepo.FindByIdAsync(trackId, ct) ?? track;
                if (await exportService.ExportPreferredLyricsAsync(asset, chosen, ct) is { Length: > 0 } written)
                {
                    chosen.SidecarPath = written;
                    await textTrackRepo.UpsertAsync(chosen, ct);
                }
            }

            return Results.Ok(new PreferredTextTrackResponse
            {
                asset_id = assetId,
                track_id = trackId,
                preferred = true,
            });
        })
        .WithName("SetPreferredAssetTextTrack")
        .WithSummary("Select one managed lyrics or subtitle variant as preferred for its language.")
        .Produces<PreferredTextTrackResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.PlaybackWrite)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.PlaybackWrite);

        group.MapGet("/{assetId:guid}/plugin-actions", async (
            Guid assetId,
            HttpContext http,
            PlayerPluginActionService actions,
            IRequestAuthorityResolver authorityResolver,
            CancellationToken ct) =>
        {
            var requester = PluginAccessGate.RequesterOf(await authorityResolver.ResolveAsync(http, ct));
            var offered = await actions.ListAsync(assetId, requester, ct);
            return Results.Ok(offered.Select(action => new PlayerPluginActionDto
            {
                plugin_id = action.PluginId,
                plugin_name = action.PluginName,
                action_id = action.ActionId,
                kind = action.Kind,
                label = action.Label,
                slot = action.Slot,
                priority = action.Priority,
                edits = action.Edits,
            }).ToList());
        })
        .WithName("GetPlayerPluginActions")
        .WithSummary("The actions switched-on plugins and sources offer on the player for this item, to the person asking, in menu order.")
        .Produces<IReadOnlyList<PlayerPluginActionDto>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.PlaybackRead)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.PlaybackRead);

        group.MapGet("/{assetId:guid}/text-tracks/alternatives", async (
            Guid assetId,
            string? kind,
            HttpContext http,
            TextTrackEnrichmentWorker textTrackWorker,
            IRequestAuthorityResolver authorityResolver,
            CancellationToken ct) =>
        {
            if (!TryParseTextTrackKind(kind, out var textTrackKind))
            {
                return ApiErrors.BadRequest("kind must be either 'lyrics' or 'subtitles'.");
            }

            var requester = PluginAccessGate.RequesterOf(await authorityResolver.ResolveAsync(http, ct));
            var result = await textTrackWorker.FindAlternativesAsync(assetId, textTrackKind, requester, ct);
            return Results.Ok(new TextTrackAlternativesResponse
            {
                asset_id = assetId,
                kind = textTrackKind.ToString(),
                status = result.Status,
                message = result.Message,
                items = result.Items.Select(item => new TextTrackAlternativeDto
                {
                    provider = item.Provider,
                    provider_name = item.ProviderName,
                    source_id = item.SourceId,
                    language = item.Language,
                    is_timed = item.IsTimed,
                    confidence = item.Confidence,
                    duration_match_score = item.DurationMatchScore,
                    is_hearing_impaired = item.IsHearingImpaired,
                    release_name = item.ReleaseName,
                    preview = item.Preview,
                    is_current = item.IsCurrent,
                }).ToList(),
            });
        })
        .WithName("GetTextTrackAlternatives")
        .WithSummary("Ask the switched-on lyrics or subtitle sources for other results for one item. Fetched live and never stored.")
        .Produces<TextTrackAlternativesResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireClientScope(ClientApiScopes.PlaybackWrite)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.PlaybackWrite);

        group.MapPost("/{assetId:guid}/text-tracks/alternatives/choose", async (
            Guid assetId,
            [Microsoft.AspNetCore.Mvc.FromBody] ChooseTextTrackAlternativeRequest request,
            HttpContext http,
            TextTrackEnrichmentWorker textTrackWorker,
            IRequestAuthorityResolver authorityResolver,
            PluginPeopleActionLog peopleActions,
            CancellationToken ct) =>
        {
            if (!TryParseTextTrackKind(request.kind, out var textTrackKind)
                || string.IsNullOrWhiteSpace(request.provider)
                || string.IsNullOrWhiteSpace(request.source_id))
            {
                return ApiErrors.BadRequest("kind, provider and source_id are required.");
            }

            var authority = await authorityResolver.ResolveAsync(http, ct);
            var result = await textTrackWorker.ChooseAlternativeAsync(
                assetId, textTrackKind, request.provider, request.source_id, PluginAccessGate.RequesterOf(authority), ct);
            if (result.Status == "Updated")
            {
                await peopleActions.RecordAsync(new PluginPeopleAction(
                    authority.ActiveProfileId, request.provider, textTrackKind == TextTrackKind.Lyrics ? "chose-lyrics" : "chose-subtitles",
                    $"Chose other {textTrackKind.ToString().ToLowerInvariant()} from {request.provider}", assetId), ct);
            }

            return Results.Ok(new TextTrackChoiceResponse { asset_id = assetId, status = result.Status, message = result.Message, track_id = result.TrackId });
        })
        .WithName("ChooseTextTrackAlternative")
        .WithSummary("Use one of the results a source offered. The choice is Library-wide; the previous one is kept so Go back works once.")
        .Produces<TextTrackChoiceResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireClientScope(ClientApiScopes.PlaybackWrite)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.PlaybackWrite);

        group.MapPost("/{assetId:guid}/text-tracks/{trackId:guid}/wrong", async (
            Guid assetId,
            Guid trackId,
            HttpContext http,
            TextTrackEnrichmentWorker textTrackWorker,
            ITextTrackRepository textTrackRepo,
            IRequestAuthorityResolver authorityResolver,
            PluginPeopleActionLog peopleActions,
            CancellationToken ct) =>
        {
            var track = await textTrackRepo.FindByIdAsync(trackId, ct);
            if (track is null || track.AssetId != assetId)
            {
                return ApiErrors.NotFound("Text track not found for this asset.");
            }

            var result = await textTrackWorker.MarkWrongAsync(assetId, trackId, ct);
            if (result.Status == "Updated")
            {
                var authority = await authorityResolver.ResolveAsync(http, ct);
                await peopleActions.RecordAsync(new PluginPeopleAction(
                    authority.ActiveProfileId, track.Provider, track.Kind == TextTrackKind.Lyrics ? "marked-lyrics-wrong" : "marked-subtitles-wrong",
                    $"Marked {track.Kind.ToString().ToLowerInvariant()} from {track.Provider} as wrong", assetId), ct);
            }

            return Results.Ok(new TextTrackChoiceResponse { asset_id = assetId, status = result.Status, message = result.Message, track_id = result.TrackId });
        })
        .WithName("MarkTextTrackWrong")
        .WithSummary("Mark a lyrics or subtitle result as wrong so it is never picked again for this item. A person's own file is never removed.")
        .Produces<TextTrackChoiceResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.PlaybackWrite)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.PlaybackWrite);

        group.MapPost("/{assetId:guid}/text-tracks/import", async (
            Guid assetId,
            HttpRequest request,
            TextTrackEnrichmentWorker textTrackWorker,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
            {
                return ApiErrors.BadRequest("A multipart form with a text-track file is required.");
            }

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
            {
                return ApiErrors.BadRequest("A non-empty text-track file is required.");
            }

            if (file.Length > 5 * 1024 * 1024)
            {
                return ApiErrors.BadRequest("Text-track files must be 5 MB or smaller.");
            }

            var kindValue = form["kind"].ToString();
            var kind = string.Equals(kindValue, "lyrics", StringComparison.OrdinalIgnoreCase)
                ? TextTrackKind.Lyrics
                : string.Equals(kindValue, "subtitles", StringComparison.OrdinalIgnoreCase)
                    ? TextTrackKind.Subtitles
                    : (TextTrackKind?)null;
            if (kind is null)
            {
                return ApiErrors.BadRequest("kind must be either 'lyrics' or 'subtitles'.");
            }

            await using var input = file.OpenReadStream();
            using var reader = new StreamReader(input, detectEncodingFromByteOrderMarks: true);
            var content = await reader.ReadToEndAsync(ct);
            var result = await textTrackWorker.ImportAsync(assetId, kind.Value, file.FileName, content, form["language"], ct);
            return result.Status == "AssetMissing"
                ? ApiErrors.NotFound(result.Message)
                : Results.Ok(new RefreshTextTracksResponse
                {
                    asset_id = assetId,
                    enrichment_type = kind.Value == TextTrackKind.Lyrics ? EnrichmentType.TimedLyrics.ToString() : EnrichmentType.Subtitles.ToString(),
                    refreshed = result.Status == "Updated",
                    status = result.Status,
                    before_count = result.BeforeCount,
                    after_count = result.AfterCount,
                    track_id = result.TrackId,
                    message = result.Message,
                });
        })
        .WithName("ImportAssetTextTrack")
        .WithSummary("Import a user-owned lyrics or subtitle file into managed text tracks.")
        .Accepts<IFormFile>("multipart/form-data")
        .Produces<RefreshTextTracksResponse>()
        .DisableAntiforgery()
        .RequireClientScope(ClientApiScopes.PlaybackWrite)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.PlaybackWrite);

        group.MapGet("/{assetId:guid}/lyrics", async (
            Guid assetId,
            ITextTrackRepository textTrackRepo,
            CancellationToken ct) =>
        {
            var track = await textTrackRepo.GetPreferredAsync(assetId, TextTrackKind.Lyrics, null, ct);
            if (track is null || string.IsNullOrWhiteSpace(track.LocalPath) || !File.Exists(track.LocalPath))
            {
                return ApiErrors.NotFound("No synced lyrics found for this asset.");
            }

            var bytes = await File.ReadAllBytesAsync(track.LocalPath, ct);
            return Results.File(bytes, "text/plain; charset=utf-8", Path.GetFileName(track.LocalPath));
        })
        .WithName("GetAssetLyrics")
        .WithSummary("Serve the preferred synchronized lyrics for a media asset.")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.PlaybackRead)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.PlaybackRead);

        group.MapGet("/{assetId:guid}/subtitles", async (
            Guid assetId,
            string? language,
            ITextTrackRepository textTrackRepo,
            CancellationToken ct) =>
        {
            var track = await textTrackRepo.GetPreferredAsync(assetId, TextTrackKind.Subtitles, language, ct);
            if (track is null || string.IsNullOrWhiteSpace(track.LocalPath) || !File.Exists(track.LocalPath))
            {
                return ApiErrors.NotFound("No subtitles found for this asset.");
            }

            var bytes = await File.ReadAllBytesAsync(track.LocalPath, ct);
            return Results.File(bytes, "text/vtt; charset=utf-8", Path.GetFileName(track.LocalPath));
        })
        .WithName("GetAssetSubtitles")
        .WithSummary("Serve the preferred normalized WebVTT subtitles for a media asset.")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.PlaybackRead)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.PlaybackRead);

        group.MapPost("/{assetId:guid}/text-tracks/refresh", async (
            Guid assetId,
            string? kind,
            HttpContext http,
            HttpRequest request,
            IMediaAssetRepository assetRepo,
            IMediaOperationRepository operationRepo,
            IRequestAuthorityResolver authorityResolver,
            CancellationToken ct) =>
        {
            var asset = await assetRepo.FindByIdAsync(assetId, ct);
            if (asset is null)
            {
                return ApiErrors.NotFound($"Asset '{assetId}' not found.");
            }

            TextTrackKind textTrackKind;
            EnrichmentType type;
            if (string.Equals(kind, "subtitles", StringComparison.OrdinalIgnoreCase))
            {
                textTrackKind = TextTrackKind.Subtitles;
                type = EnrichmentType.Subtitles;
            }
            else if (string.Equals(kind, "lyrics", StringComparison.OrdinalIgnoreCase))
            {
                textTrackKind = TextTrackKind.Lyrics;
                type = EnrichmentType.TimedLyrics;
            }
            else
            {
                return ApiErrors.BadRequest("kind must be either 'lyrics' or 'subtitles'.");
            }

            var requestKey = request.Headers["Idempotency-Key"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(requestKey))
            {
                requestKey = Guid.NewGuid().ToString("N");
            }

            if (requestKey.Length > 200)
            {
                return ApiErrors.BadRequest("Idempotency-Key must be 200 characters or fewer.");
            }

            var operation = await operationRepo.EnsureAsync(new MediaOperation
            {
                OperationType = textTrackKind == TextTrackKind.Lyrics
                    ? MediaOperationType.TextTrackLyrics
                    : MediaOperationType.TextTrackSubtitles,
                OperationKind = MediaOperationKind.TextTrack,
                EntityId = assetId,
                EntityKind = EntityType.MediaAsset.ToString(),
                Status = MediaOperationStatus.Queued,
                Stage = MediaOperationStage.Queued,
                QueueName = "text_track",
                CapabilityId = textTrackKind == TextTrackKind.Lyrics
                    ? CapabilityId.TextTrackLyrics
                    : CapabilityId.TextTrackSubtitles,
                CapabilityVersion = "1",
                // The lookup runs later from the durable queue; who asked is kept so each source is used within its own audience then.
                RequestedAudience = PluginDeclarationRules.AudienceWord(
                    PluginAccessGate.RequesterOf(await authorityResolver.ResolveAsync(http, ct))),
                IdempotencyKey = $"manual-text-track:{assetId:D}:{textTrackKind}:{requestKey}",
            }, ct);

            var alreadyFinished = operation.Status is MediaOperationStatus.Succeeded
                or MediaOperationStatus.NoResult
                or MediaOperationStatus.Blocked
                or MediaOperationStatus.FailedTerminal
                or MediaOperationStatus.Cancelled;
            return Results.Accepted($"/operations/{operation.Id:D}", new RefreshTextTracksResponse
            {
                operation_id = operation.Id,
                asset_id = assetId,
                enrichment_type = type.ToString(),
                refreshed = operation.Status == MediaOperationStatus.Succeeded,
                status = operation.Status,
                message = alreadyFinished
                    ? operation.ResultSummary ?? operation.LastError ?? "The text-track refresh finished."
                    : "The text-track refresh was queued and will continue if the editor is closed.",
            });
        })
        .WithName("RefreshAssetTextTracks")
        .WithSummary("Queue a durable lyrics or subtitle refresh for one media asset.")
        .Produces<RefreshTextTracksResponse>(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.PlaybackWrite)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.PlaybackWrite);

        group.MapGet("/{assetId:guid}/cover-thumb", async (
            Guid assetId,
            IMediaAssetRepository assetRepo,
            IWorkRepository workRepo,
            IEntityAssetRepository entityAssetRepo,
            IArtworkRenditionService renditions,
            CancellationToken ct) =>
        {
            var asset = await assetRepo.FindByIdAsync(assetId, ct);
            if (asset is null)
            {
                return ApiErrors.NotFound($"Asset '{assetId}' not found.");
            }

            var ownerEntityId = await ResolveArtworkOwnerEntityIdAsync(assetId, workRepo, ct);
            var preferredVariant = await entityAssetRepo.GetPreferredAsync(ownerEntityId.ToString(), "CoverArt", ct);
            var localArtworkResult = await ServeStableAsync(renditions, preferredVariant, "s", ct);
            if (localArtworkResult is not null)
            {
                return localArtworkResult;
            }

            return CreateArtworkPlaceholderResult();
        })
        .WithName("GetAssetCoverThumb")
        .WithSummary("Serve the centrally-managed derived cover thumbnail for a media asset.")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.ArtworkRead)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.ArtworkRead);
        // NOTE: No rate limit — thumbnails are loaded in bulk on Home/category pages.
        // The 100/min streaming cap was causing 429s on page reloads with many swimlanes.

        group.MapGet("/{assetId:guid}/background", async (
            Guid assetId,
            string? size,
            IMediaAssetRepository assetRepo,
            IWorkRepository workRepo,
            IEntityAssetRepository entityAssetRepo,
            IArtworkRenditionService renditions,
            CancellationToken ct) =>
        {
            var asset = await assetRepo.FindByIdAsync(assetId, ct);
            if (asset is null)
            {
                return ApiErrors.NotFound($"Asset '{assetId}' not found.");
            }

            var ownerEntityId = await ResolveArtworkOwnerEntityIdAsync(assetId, workRepo, ct);
            var preferredVariant = await entityAssetRepo.GetPreferredAsync(ownerEntityId.ToString(), "Background", ct);
            var localArtworkResult = await ServeStableAsync(renditions, preferredVariant, size, ct);
            if (localArtworkResult is not null)
            {
                return localArtworkResult;
            }

            return CreateArtworkPlaceholderResult();
        })
        .WithName("GetAssetBackground")
        .WithSummary("Serve uploaded background artwork for a media asset.")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.ArtworkRead)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.ArtworkRead);

        group.MapGet("/{assetId:guid}/logo", async (
            Guid assetId,
            IMediaAssetRepository assetRepo,
            IWorkRepository workRepo,
            IEntityAssetRepository entityAssetRepo,
            CancellationToken ct) =>
        {
            var asset = await assetRepo.FindByIdAsync(assetId, ct);
            if (asset is null)
            {
                return ApiErrors.NotFound($"Asset '{assetId}' not found.");
            }

            var ownerEntityId = await ResolveArtworkOwnerEntityIdAsync(assetId, workRepo, ct);
            var preferredVariant = await entityAssetRepo.GetPreferredAsync(ownerEntityId.ToString(), "Logo", ct);
            var localArtworkResult = CreateLocalArtworkResult(preferredVariant?.LocalImagePath);
            if (localArtworkResult is not null)
            {
                return localArtworkResult;
            }

            return CreateArtworkPlaceholderResult();
        })
        .WithName("GetAssetLogo")
        .WithSummary("Serve uploaded logo artwork for a media asset.")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.ArtworkRead)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.ArtworkRead);

        return app;
    }

    internal static async Task<Guid> ResolveArtworkOwnerEntityIdAsync(
        Guid assetId,
        IWorkRepository workRepo,
        CancellationToken ct,
        Guid? fallbackOwnerEntityId = null)
    {
        var lineage = await workRepo.GetLineageByAssetAsync(assetId, ct);
        if (lineage is null)
        {
            return fallbackOwnerEntityId ?? assetId;
        }

        return lineage.MediaType switch
        {
            MediaEngine.Domain.Enums.MediaType.Books
                or MediaEngine.Domain.Enums.MediaType.Audiobooks
                or MediaEngine.Domain.Enums.MediaType.Comics => lineage.TargetForSelfScope,
            _ => lineage.TargetForParentScope,
        };
    }

    private static bool TryParseTextTrackKind(string? value, out TextTrackKind kind)
    {
        if (string.Equals(value, "lyrics", StringComparison.OrdinalIgnoreCase))
        {
            kind = TextTrackKind.Lyrics;
            return true;
        }

        if (string.Equals(value, "subtitles", StringComparison.OrdinalIgnoreCase))
        {
            kind = TextTrackKind.Subtitles;
            return true;
        }

        kind = default;
        return false;
    }

    private static string? NormalizeArtworkSize(string? size) =>
        ArtworkVariantHelper.TryParseRenditionSize(size, out var key) ? key : null;

    /// <summary>
    /// Serves the picture at a size for an address that stays the same when the picture changes (an entity's or
    /// a file's current cover, background). These are never kept for a year: the browser checks each use. An
    /// unrecognised size means the original, as it always has on these addresses.
    /// </summary>
    private static async Task<IResult?> ServeStableAsync(
        IArtworkRenditionService renditions,
        EntityAsset? variant,
        string? size,
        CancellationToken ct)
    {
        if (variant is null)
        {
            return null;
        }

        var resolved = await renditions.ResolveAsync(variant, NormalizeArtworkSize(size), ct);
        return resolved is null ? null : ArtworkFileResults.Local(resolved.Path, ArtworkCacheMode.Revalidate);
    }

    private static IResult? CreateLocalArtworkResult(string? path) => ArtworkFileResults.Local(path);

    internal static bool HasRecognizedImageSignature(string path) => ArtworkFileResults.HasRecognizedImageSignature(path);

    internal static string GetArtworkContentType(string path) => ArtworkFileResults.GetArtworkContentType(path);

    internal static IResult CreateArtworkPlaceholderResult() => ArtworkFileResults.Placeholder();
}
