using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Imports;
using MediaEngine.Ingestion.Services.ImportUploads;
using Microsoft.AspNetCore.Http.Features;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Resumable uploads of big files into the Import folder (Library setup spec D44). The Dashboard relays these from the browser, one
/// 64 MB piece at a time, as the signed-in person. Only an administrator signed in as a person may use them (the same rule as
/// Settings: an application or the Dashboard's service credential alone is refused). A start call is refused before any bytes move
/// when there is no Import folder, no Managed folder for the kind or no room; the refusal carries a <c>code</c> from
/// <see cref="ImportUploadRefusalCodes"/>.
/// </summary>
public static class ImportUploadEndpoints
{
    /// <summary>The endpoint name of the piece action, which the Engine's general rate limit leaves out (a big file is hundreds of pieces).</summary>
    public const string PieceEndpointName = "PutImportUploadPiece";

    // Room left over the piece itself for the request framing, so a piece that is a few bytes over is reported as a bad piece
    // rather than cut off by the server.
    private const long PieceBodySlackBytes = 4096;

    public static IEndpointRouteBuilder MapImportUploadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/imports/uploads")
            .WithTags("Imports")
            // Administrators only (D44), as the signed-in person: not an application, and never the Dashboard's service credential alone.
            .RequireEffectiveAdministrator();

        group.MapPost("/", async (
            ImportUploadStartRequest request,
            HttpContext http,
            IRequestAuthorityResolver resolver,
            ImportUploadService uploads,
            CancellationToken ct) =>
        {
            if (await ResolveOwnerAsync(http, resolver, ct) is not { } owner)
            {
                return ApiErrors.Forbidden("Only an administrator can add files this way.");
            }

            return ToResult(await uploads.StartAsync(owner, request, ct));
        })
        .WithName("StartImportUpload")
        .WithSummary("Starts a resumable upload into the Import folder, or refuses it before any bytes move.")
        .Produces<ImportUploadStartResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
        .ProducesProblem(StatusCodes.Status507InsufficientStorage);

        group.MapGet("/{uploadId:guid}", async (
            Guid uploadId,
            HttpContext http,
            IRequestAuthorityResolver resolver,
            ImportUploadService uploads,
            CancellationToken ct) =>
        {
            if (await ResolveOwnerAsync(http, resolver, ct) is not { } owner)
            {
                return ApiErrors.Forbidden("Only an administrator can add files this way.");
            }

            return ToResult(await uploads.GetStatusAsync(owner, uploadId, ct));
        })
        .WithName("GetImportUploadStatus")
        .WithSummary("The pieces received so far, so a dropped upload resumes without resending them.")
        .Produces<ImportUploadStatusResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{uploadId:guid}/pieces/{index:int}", async (
            Guid uploadId,
            int index,
            HttpContext http,
            IRequestAuthorityResolver resolver,
            ImportUploadService uploads,
            ILogger<ImportUploadService> logger,
            CancellationToken ct) =>
        {
            if (await ResolveOwnerAsync(http, resolver, ct) is not { } owner)
            {
                return ApiErrors.Forbidden("Only an administrator can add files this way.");
            }

            // The request-size limit is raised for this action only, to one piece; every other action keeps the normal limit.
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
            {
                bodyLimit.MaxRequestBodySize = uploads.MaxPieceRequestBytes + PieceBodySlackBytes;
            }

            try
            {
                return ToResult(await uploads.WritePieceAsync(
                    owner, uploadId, index, http.Request.ContentLength, http.Request.Body, ct));
            }
            catch (BadHttpRequestException ex)
            {
                // The body was too big for a piece, or ended before it should: nothing was recorded, so the piece is simply sent again.
                logger.LogInformation(ex, "Piece {Index} of upload {UploadId} was cut short or too big", index, uploadId);
                return Problem(
                    ex.StatusCode == StatusCodes.Status413PayloadTooLarge ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest,
                    ImportUploadRefusalCodes.InvalidPiece,
                    "The piece did not arrive in full. Send it again.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The sender went away mid-piece. The piece is not recorded, so it is sent again when they resume.
                return Results.Empty;
            }
        })
        .WithName(PieceEndpointName)
        .WithSummary("Writes one piece of an upload at its place in the hidden working file. Sending a piece again is harmless.")
        .DisableAntiforgery()
        .Produces<ImportUploadStatusResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{uploadId:guid}/complete", async (
            Guid uploadId,
            HttpContext http,
            IRequestAuthorityResolver resolver,
            ImportUploadService uploads,
            CancellationToken ct) =>
        {
            if (await ResolveOwnerAsync(http, resolver, ct) is not { } owner)
            {
                return ApiErrors.Forbidden("Only an administrator can add files this way.");
            }

            return ToResult(await uploads.CompleteAsync(owner, uploadId, ct));
        })
        .WithName("CompleteImportUpload")
        .WithSummary("Moves the finished file into the kind's Import sub-folder under a safe unique name; the normal filing rules take over.")
        .Produces<ImportUploadCompleteResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{uploadId:guid}", async (
            Guid uploadId,
            HttpContext http,
            IRequestAuthorityResolver resolver,
            ImportUploadService uploads,
            CancellationToken ct) =>
        {
            if (await ResolveOwnerAsync(http, resolver, ct) is not { } owner)
            {
                return ApiErrors.Forbidden("Only an administrator can add files this way.");
            }

            return ToResult(await uploads.CancelAsync(owner, uploadId, ct));
        })
        .WithName("CancelImportUpload")
        .WithSummary("Cancels an upload and deletes its hidden working file.")
        .Produces<ImportUploadStatusResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    /// <summary>True for the piece action, which the Engine's general rate limit leaves out.</summary>
    public static bool IsPieceRoute(HttpContext context) =>
        string.Equals(
            context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName,
            PieceEndpointName,
            StringComparison.Ordinal);

    // An upload belongs to the administrator who started it; nobody else can see or change it.
    private static async Task<Guid?> ResolveOwnerAsync(HttpContext http, IRequestAuthorityResolver resolver, CancellationToken ct)
    {
        var authority = await resolver.ResolveAsync(http, ct);
        return authority.ActiveProfileId is { } id && id != Guid.Empty ? id : null;
    }

    private static IResult ToResult<T>(ImportUploadResult<T> result) =>
        result.Refusal is { } refusal
            ? Problem(
                refusal.Kind switch
                {
                    ImportUploadRefusalKind.BadRequest => StatusCodes.Status400BadRequest,
                    ImportUploadRefusalKind.NotFound => StatusCodes.Status404NotFound,
                    ImportUploadRefusalKind.Conflict => StatusCodes.Status409Conflict,
                    ImportUploadRefusalKind.TooLarge => StatusCodes.Status413PayloadTooLarge,
                    _ => StatusCodes.Status507InsufficientStorage,
                },
                refusal.Code,
                refusal.Message)
            : Results.Ok(result.Value);

    private static IResult Problem(int statusCode, string code, string message) =>
        Results.Problem(
            detail: message,
            statusCode: statusCode,
            title: statusCode switch
            {
                StatusCodes.Status404NotFound => "Not found.",
                StatusCodes.Status409Conflict => "Conflict.",
                StatusCodes.Status413PayloadTooLarge => "Upload too large.",
                StatusCodes.Status507InsufficientStorage => "Insufficient disk space.",
                _ => "Invalid request.",
            },
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
