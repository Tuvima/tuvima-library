using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Details;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Capabilities;
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
    private const string ArtworkPlaceholderSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 320 480" role="img" aria-label="Artwork unavailable">
          <defs>
            <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
              <stop offset="0" stop-color="#1f2937"/>
              <stop offset="0.55" stop-color="#334155"/>
              <stop offset="1" stop-color="#0f172a"/>
            </linearGradient>
          </defs>
          <rect width="320" height="480" rx="16" fill="url(#bg)"/>
          <rect x="68" y="146" width="184" height="188" rx="12" fill="none" stroke="#94a3b8" stroke-width="10" opacity="0.72"/>
          <circle cx="124" cy="204" r="24" fill="#cbd5e1" opacity="0.78"/>
          <path d="M82 308l56-62 38 42 24-28 38 48z" fill="#cbd5e1" opacity="0.78"/>
        </svg>
        """;

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

            ctx.Response.Headers.AcceptRanges = "bytes";
            long totalSize = await streamer.GetFileSizeAsync(asset.FilePathRoot, ct);

            if (ctx.Request.Headers.TryGetValue("Range", out var rangeHeader)
                && TryParseRange(rangeHeader.ToString(), totalSize,
                                 out long rangeStart, out long rangeEnd))
            {
                long length = rangeEnd - rangeStart + 1;
                using var result = await streamer.GetRangeAsync(
                    asset.FilePathRoot, rangeStart, length, ct);

                ctx.Response.StatusCode = StatusCodes.Status206PartialContent;
                ctx.Response.ContentType = mimeType;
                ctx.Response.Headers.ContentRange = result.ContentRangeHeader;
                ctx.Response.Headers.ContentLength = result.ContentLength;
                await result.Content.CopyToAsync(ctx.Response.Body, ct);
                return Results.Empty;
            }
            else
            {
                using var result = await streamer.GetRangeAsync(
                    asset.FilePathRoot, 0, null, ct);

                ctx.Response.ContentType = mimeType;
                ctx.Response.Headers.ContentLength = totalSize;
                await result.Content.CopyToAsync(ctx.Response.Body, ct);
                return Results.Empty;
            }
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
            IEntityAssetRepository entityAssetRepo,
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
                return ApiErrors.BadRequest("Artwork size must be one of 's', 'm', or 'l'.");
            }

            var renditionPath = ResolveArtworkPath(variant, normalizedSize);
            var localArtworkResult = CreateLocalArtworkResult(renditionPath);
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
            DetailComposerService detailComposer,
            IHttpClientFactory httpFactory,
            CancellationToken ct) =>
        {
            if (!DetailComposerService.TryParseEntityType(entityType, out var parsedEntityType))
            {
                return ApiErrors.BadRequest($"Unsupported detail entity type '{entityType}'.");
            }

            var preferredVariant = await entityAssetRepo.GetPreferredAsync(entityId.ToString(), "CoverArt", ct);
            var localArtworkResult = preferredVariant is null
                ? null
                : CreateLocalArtworkResult(ResolveArtworkPath(preferredVariant, NormalizeArtworkSize(size)));
            if (localArtworkResult is not null)
            {
                return localArtworkResult;
            }

            var imageUrl = preferredVariant?.ImageUrl;
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
            var preferredVariant = await entityAssetRepo.GetPreferredAsync(ownerEntityId.ToString(), "CoverArt", ct);
            var localArtworkResult = CreateLocalArtworkResult(preferredVariant?.LocalImagePath);
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
            CancellationToken ct) =>
        {
            var track = await textTrackRepo.FindByIdAsync(trackId, ct);
            if (track is null || track.AssetId != assetId)
            {
                return ApiErrors.NotFound("Text track not found for this asset.");
            }

            await textTrackRepo.SetPreferredAsync(trackId, ct);
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
            HttpRequest request,
            IMediaAssetRepository assetRepo,
            IMediaOperationRepository operationRepo,
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
            CancellationToken ct) =>
        {
            var asset = await assetRepo.FindByIdAsync(assetId, ct);
            if (asset is null)
            {
                return ApiErrors.NotFound($"Asset '{assetId}' not found.");
            }

            var ownerEntityId = await ResolveArtworkOwnerEntityIdAsync(assetId, workRepo, ct);
            var preferredVariant = await entityAssetRepo.GetPreferredAsync(ownerEntityId.ToString(), "CoverArt", ct);
            var thumbPath = preferredVariant is null ? null : ResolveArtworkPath(preferredVariant, "s");
            var localArtworkResult = CreateLocalArtworkResult(thumbPath);
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
            var preferredVariant = await entityAssetRepo.GetPreferredAsync(ownerEntityId.ToString(), "Background", ct);
            var backgroundPath = preferredVariant is null ? null : ResolveArtworkPath(preferredVariant, null);
            var localArtworkResult = CreateLocalArtworkResult(backgroundPath);
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
            var logoPath = preferredVariant is null ? null : ResolveArtworkPath(preferredVariant, null);
            var localArtworkResult = CreateLocalArtworkResult(logoPath);
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

    private static async Task<Guid> ResolveArtworkOwnerEntityIdAsync(
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

    private static string? NormalizeArtworkSize(string? size)
    {
        var normalized = (size ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "s" or "m" or "l" ? normalized : null;
    }

    private static string? ResolveArtworkPath(EntityAsset asset, string? size)
    {
        var requestedPath = (size ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "s" => asset.LocalImagePathSmall,
            "m" => asset.LocalImagePathMedium,
            "l" => asset.LocalImagePathLarge,
            _ => asset.LocalImagePath,
        };

        return !string.IsNullOrWhiteSpace(requestedPath) && File.Exists(requestedPath)
            ? requestedPath
            : asset.LocalImagePath;
    }

    private static IResult? CreateLocalArtworkResult(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        if (!HasRecognizedImageSignature(path))
        {
            return CreateArtworkPlaceholderResult();
        }

        return new CacheableArtworkFileResult(path, GetArtworkContentType(path));
    }

    private static bool HasRecognizedImageSignature(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[12];
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                header.Length,
                FileOptions.SequentialScan);
            var read = stream.Read(header);
            return read >= 2 && header[0] == 0xFF && header[1] == 0xD8
                || read >= 8 && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
                || read >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8))
                || read >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8)
                || read >= 2 && header[0] == (byte)'B' && header[1] == (byte)'M';
        }
        catch
        {
            return false;
        }
    }

    private static string GetArtworkContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/jpeg",
        };

    internal static IResult CreateArtworkPlaceholderResult() =>
        Results.Text(ArtworkPlaceholderSvg, "image/svg+xml");

    private sealed class CacheableArtworkFileResult(string path, string contentType) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            var file = new FileInfo(path);
            var modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            var tag = new EntityTagHeaderValue($"\"{file.Length:x}-{file.LastWriteTimeUtc.Ticks:x}\"");
            httpContext.Response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue
            {
                Public = true,
                MaxAge = TimeSpan.FromDays(1),
            };
            return Results.File(
                    path,
                    contentType,
                    lastModified: modified,
                    entityTag: tag,
                    enableRangeProcessing: false)
                .ExecuteAsync(httpContext);
        }
    }

    /// <summary>
    /// Parses the RFC 7233 Range header value "bytes=start-end".
    /// Both start and end may be absent. Returns false if the header cannot be
    /// parsed or the range is unsatisfiable.
    /// </summary>
    private static bool TryParseRange(
        string rangeHeader,
        long totalSize,
        out long start,
        out long end)
    {
        start = 0;
        end = totalSize > 0 ? totalSize - 1 : 0;

        if (!rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rangePart = rangeHeader["bytes=".Length..];
        var dashIdx = rangePart.IndexOf('-');
        if (dashIdx < 0)
        {
            return false;
        }

        var startStr = rangePart[..dashIdx].Trim();
        var endStr = rangePart[(dashIdx + 1)..].Trim();

        // "bytes=-500" → last 500 bytes (suffix range).
        if (startStr.Length == 0 && long.TryParse(endStr, out long suffixLength))
        {
            start = Math.Max(0, totalSize - suffixLength);
            end = totalSize - 1;
            return totalSize > 0;
        }

        if (!long.TryParse(startStr, out start))
        {
            return false;
        }

        if (endStr.Length == 0)
        {
            end = totalSize - 1;
        }
        else if (!long.TryParse(endStr, out end))
        {
            return false;
        }

        start = Math.Max(0, start);
        end = Math.Min(end, totalSize - 1);
        return start <= end && totalSize > 0;
    }
}
