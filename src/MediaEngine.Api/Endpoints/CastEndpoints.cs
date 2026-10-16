using System.Security.Claims;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Processors.Contracts;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Casting to a home-network TV. A paired phone asks for a cast session (<c>POST /api/v1/playback/{assetId}/cast</c>); the TV
/// then loads the returned paths under <c>/cast/v1/{grant}/</c> with no sign-in. The grant is a short-lived secret for ONE
/// title (see <see cref="CastAccessGrantService"/>); the receiver routes are anonymous on purpose, check the whole grant on
/// every request, and serve GET, HEAD and OPTIONS only. The Dashboard relays them for home-network clients and nobody else.
/// </summary>
public static class CastEndpoints
{
    private const string LoggerName = "MediaEngine.Api.CastReceiver";

    public static IEndpointRouteBuilder MapCastEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/playback/{assetId:guid}/cast", CreateSessionAsync)
            .WithName("CreateCastSession")
            .WithTags("Playback")
            .WithSummary("Start a cast session for one video: a short-lived, single-title link a home-network TV can play with no sign-in.")
            .Produces<CastSessionDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireClientScope(ClientApiScopes.PlaybackRead)
            .RequireCatalogueAssetAccess(ApplicationPermissionIds.PlaybackRead)
            .RequireRateLimiting("cast_grants");

        var methods = new[] { HttpMethods.Get, HttpMethods.Head, HttpMethods.Options };
        app.MapMethods("/cast/v1/{grant}/media", methods, MediaAsync)
            .WithName("GetCastMedia")
            .WithTags("Cast receiver")
            .WithSummary("Serve the granted video as it is, with byte ranges, to a cast receiver.")
            .AllowAnonymous()
            .RequireRateLimiting("streaming");
        app.MapMethods("/cast/v1/{grant}/hls/{packageId:guid}/{**resourcePath}", methods, HlsAsync)
            .WithName("GetCastHlsResource")
            .WithTags("Cast receiver")
            .WithSummary("Serve one resource of the granted title's prepared HLS package to a cast receiver.")
            .AllowAnonymous()
            .RequireRateLimiting("adaptive_streaming");
        app.MapMethods("/cast/v1/{grant}/text/{trackId:guid}", methods, TextAsync)
            .WithName("GetCastTextTrack")
            .WithTags("Cast receiver")
            .WithSummary("Serve one managed subtitle track of the granted title as WebVTT to a cast receiver.")
            .AllowAnonymous()
            .RequireRateLimiting("streaming");
        app.MapMethods("/cast/v1/{grant}/poster", methods, PosterAsync)
            .WithName("GetCastPoster")
            .WithTags("Cast receiver")
            .WithSummary("Serve the granted title's cover to a cast receiver.")
            .AllowAnonymous()
            .RequireRateLimiting("streaming");
        return app;
    }

    // ------------------------------------------------------------------ the phone's call ------------------------------

    private static async Task<IResult> CreateSessionAsync(
        Guid assetId,
        CastSessionRequestDto? request,
        HttpContext context,
        ClaimsPrincipal user,
        IRequestAuthorityResolver authorities,
        CastSessionService sessions,
        CancellationToken ct)
    {
        if (request is null || !string.Equals(request.Receiver, CastReceivers.GoogleCastDefault, StringComparison.Ordinal))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Unsupported receiver",
                detail: $"Only '{CastReceivers.GoogleCastDefault}' is supported.");
        }

        var authority = await authorities.ResolveAsync(context, ct).ConfigureAwait(false);
        if (authority.PrincipalKind != PrincipalKind.DelegatedUserClient)
        {
            // A signed-in Dashboard session (or an Application) never starts a cast session: only a paired device does.
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Paired device required",
                detail: "Casting can be started from a paired app only.");
        }

        var tokenId = Guid.TryParse(user.FindFirstValue(TuvimaClaimTypes.TokenId), out var parsed) && parsed != Guid.Empty ? parsed : (Guid?)null;
        var scopes = user.FindAll(TuvimaClaimTypes.Scope).Select(claim => claim.Value).ToArray();
        try
        {
            var session = await sessions.CreateAsync(assetId, authority, tokenId, scopes, ct).ConfigureAwait(false);
            if (session is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found",
                    detail: $"Asset '{assetId}' not found.");
            }

            // The answer carries the secret link: it must never be cached or kept by anything between the phone and the Engine.
            context.Response.Headers.CacheControl = "private, no-store";
            return Results.Ok(session);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Casting not allowed",
                detail: "This device cannot cast this title.");
        }
    }

    // ------------------------------------------------------------------ the TV's calls --------------------------------

    private static async Task MediaAsync(
        string grant,
        HttpContext context,
        CastAccessGrantService grants,
        ICastAssetAuthorizer authorizer,
        IMediaAssetRepository assets,
        IByteStreamer streamer,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var access = await BeginAsync(grant, context, grants, authorizer, loggers, ct).ConfigureAwait(false);
        if (access is null)
        {
            return;
        }

        // A grant made for the HLS package never serves the file itself.
        if (access.PackageId is not null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var asset = await assets.FindByIdAsync(access.AssetId, ct).ConfigureAwait(false);
        if (asset is null || !File.Exists(asset.FilePathRoot))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var contentType = string.Equals(Path.GetExtension(asset.FilePathRoot), ".webm", StringComparison.OrdinalIgnoreCase)
            ? CastSessionService.ContentTypeWebm
            : CastSessionService.ContentTypeMp4;
        await ByteRangeResponder.WriteFileAsync(context, streamer, asset.FilePathRoot, contentType,
            HttpMethods.IsHead(context.Request.Method), ct).ConfigureAwait(false);
    }

    private static async Task HlsAsync(
        string grant,
        Guid packageId,
        string? resourcePath,
        HttpContext context,
        CastAccessGrantService grants,
        ICastAssetAuthorizer authorizer,
        AdaptiveHlsService hls,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var access = await BeginAsync(grant, context, grants, authorizer, loggers, ct).ConfigureAwait(false);
        if (access is null)
        {
            return;
        }

        // The grant names one package; any other package id is refused, even for the same title.
        if (access.PackageId != packageId)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await using var resource = await hls.OpenResourceAsync(packageId, access.AssetId, resourcePath ?? string.Empty, ct)
            .ConfigureAwait(false);
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

    private static async Task TextAsync(
        string grant,
        Guid trackId,
        HttpContext context,
        CastAccessGrantService grants,
        ICastAssetAuthorizer authorizer,
        ITextTrackRepository textTracks,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var access = await BeginAsync(grant, context, grants, authorizer, loggers, ct).ConfigureAwait(false);
        if (access is null)
        {
            return;
        }

        // Subtitles of this title only: never lyrics, never another asset's track.
        var track = await textTracks.FindByIdAsync(trackId, ct).ConfigureAwait(false);
        if (track is null || track.AssetId != access.AssetId || track.Kind != TextTrackKind.Subtitles ||
            string.IsNullOrWhiteSpace(track.LocalPath) || !File.Exists(track.LocalPath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var bytes = await File.ReadAllBytesAsync(track.LocalPath, ct).ConfigureAwait(false);
        context.Response.ContentType = "text/vtt; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
    }

    private static async Task PosterAsync(
        string grant,
        HttpContext context,
        CastAccessGrantService grants,
        ICastAssetAuthorizer authorizer,
        IWorkRepository works,
        IEntityAssetRepository entityAssets,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var access = await BeginAsync(grant, context, grants, authorizer, loggers, ct).ConfigureAwait(false);
        if (access is null)
        {
            return;
        }

        // The asset's effective cover, found exactly as the signed-in cover route finds it; nothing else is ever listed.
        var owner = await StreamEndpoints.ResolveArtworkOwnerEntityIdAsync(access.AssetId, works, ct).ConfigureAwait(false);
        var preferred = await entityAssets.GetPreferredAsync(owner.ToString(), "CoverArt", ct).ConfigureAwait(false);
        var path = preferred?.LocalImagePath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) && StreamEndpoints.HasRecognizedImageSignature(path))
        {
            context.Response.ContentType = StreamEndpoints.GetArtworkContentType(path);
            context.Response.ContentLength = new FileInfo(path).Length;
            if (!HttpMethods.IsHead(context.Request.Method))
            {
                await context.Response.SendFileAsync(path, ct).ConfigureAwait(false);
            }

            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    // ------------------------------------------------------------------ shared prologue -------------------------------

    /// <summary>
    /// Sets the headers every receiver answer carries (no caching, no referrer, no sniffing, CORS for a cast receiver with no
    /// credentials), answers an OPTIONS preflight, and checks the grant and the catalogue on every request. Returns null once
    /// the response is complete (preflight or refusal). A bad grant is 403; the grant is never logged, only a short fingerprint.
    /// </summary>
    private static async ValueTask<CastGrantValidation?> BeginAsync(
        string grant,
        HttpContext context,
        CastAccessGrantService grants,
        ICastAssetAuthorizer authorizer,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var headers = context.Response.Headers;
        headers.CacheControl = "private, no-store";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Access-Control-Allow-Origin"] = "*";
        headers["Access-Control-Expose-Headers"] = "Content-Length, Content-Range, Accept-Ranges";

        if (HttpMethods.IsOptions(context.Request.Method))
        {
            headers["Access-Control-Allow-Methods"] = "GET, HEAD, OPTIONS";
            headers["Access-Control-Allow-Headers"] = "Range";
            headers["Access-Control-Max-Age"] = "600";
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return null;
        }

        var logger = loggers.CreateLogger(LoggerName);
        var validation = await grants.ValidateAsync(grant, ct).ConfigureAwait(false);
        if (validation is null)
        {
            logger.LogDebug("Cast receiver refused grant {GrantFingerprint}.", CastAccessGrantService.LogId(grant));
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return null;
        }

        // The catalogue's own limits apply on every request, so a Kids limit that is lowered stops the TV too.
        if (!await authorizer.AllowsPlaybackAsync(validation.Authority, validation.AssetId, ct).ConfigureAwait(false))
        {
            logger.LogDebug("Cast receiver: the catalogue no longer allows grant {GrantFingerprint}.", CastAccessGrantService.LogId(grant));
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return null;
        }

        return validation;
    }
}
