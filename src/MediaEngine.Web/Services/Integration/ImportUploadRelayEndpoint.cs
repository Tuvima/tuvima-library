using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using MediaEngine.Domain.Configuration;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Carries a resumable upload (Library setup spec D44) from the browser to the Engine: start, one 64 MB piece at a time, status, complete
/// and cancel. The browser only ever talks to the Dashboard. Each call goes to the Engine as the signed-in person (their session, with
/// the Dashboard's service credential beside it), never as the service credential alone, and the Engine decides who may upload.
/// A piece is streamed straight through without being held in memory or on disk. Whatever the Engine answers, including a refusal such
/// as "set an Import folder first", comes back to the browser unchanged. A piece has about five minutes; the other calls less.
/// </summary>
public static class ImportUploadRelayEndpoint
{
    /// <summary>The name of the Engine client used for uploads: the usual sign-in handlers, but no 100-second limit.</summary>
    public const string EngineClientName = "EngineUpload";

    /// <summary>The browser-facing address of the relay.</summary>
    public const string BrowserBase = "/import-uploads";

    /// <summary>How long one piece may take from the browser to the Engine and back.</summary>
    public static readonly TimeSpan PieceTimeout = TimeSpan.FromMinutes(5);

    /// <summary>How long start, status, complete and cancel may take.</summary>
    public static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(100);

    // A piece is never bigger than the largest piece size the Engine accepts; the start call is a few short lines of JSON.
    private const long PieceBodySlackBytes = 4096;
    private const long StartBodyLimitBytes = 16 * 1024;

    // Only these response headers cross back to the browser; anything else the Engine sends stays behind.
    private static readonly string[] ForwardedResponseHeaders = ["Retry-After"];

    private const string RefusalCodeAntiforgery = "antiforgery_failed";
    private const string RefusalCodeEngineUnreachable = "engine_unreachable";
    private const string RefusalCodeBodyTooLarge = "body_too_large";

    public static IEndpointConventionBuilder MapImportUploadRelay(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(BrowserBase).RequireAuthorization();

        group.MapPost("/", (HttpContext context, IHttpClientFactory factory, IAntiforgery antiforgery, ILoggerFactory loggers) =>
            RelayAsync(context, factory, antiforgery, loggers, HttpMethod.Post, "/imports/uploads", hasBody: true, isPiece: false))
            .WithName("RelayStartImportUpload");

        group.MapGet("/{uploadId:guid}", (Guid uploadId, HttpContext context, IHttpClientFactory factory, IAntiforgery antiforgery, ILoggerFactory loggers) =>
            RelayAsync(context, factory, antiforgery, loggers, HttpMethod.Get, EnginePath(uploadId), hasBody: false, isPiece: false))
            .WithName("RelayGetImportUploadStatus");

        group.MapPut("/{uploadId:guid}/pieces/{index:int}", (Guid uploadId, int index, HttpContext context, IHttpClientFactory factory, IAntiforgery antiforgery, ILoggerFactory loggers) =>
            RelayAsync(context, factory, antiforgery, loggers, HttpMethod.Put, EnginePath(uploadId) + "/pieces/" + index.ToString(CultureInfo.InvariantCulture), hasBody: true, isPiece: true))
            .WithName("RelayPutImportUploadPiece");

        group.MapPost("/{uploadId:guid}/complete", (Guid uploadId, HttpContext context, IHttpClientFactory factory, IAntiforgery antiforgery, ILoggerFactory loggers) =>
            RelayAsync(context, factory, antiforgery, loggers, HttpMethod.Post, EnginePath(uploadId) + "/complete", hasBody: false, isPiece: false))
            .WithName("RelayCompleteImportUpload");

        group.MapDelete("/{uploadId:guid}", (Guid uploadId, HttpContext context, IHttpClientFactory factory, IAntiforgery antiforgery, ILoggerFactory loggers) =>
            RelayAsync(context, factory, antiforgery, loggers, HttpMethod.Delete, EnginePath(uploadId), hasBody: false, isPiece: false))
            .WithName("RelayCancelImportUpload");

        return group;
    }

    // The Engine path is built from the parsed id and index, never from the text the browser sent.
    private static string EnginePath(Guid uploadId) => "/imports/uploads/" + uploadId.ToString("D", CultureInfo.InvariantCulture);

    private static async Task RelayAsync(
        HttpContext context,
        IHttpClientFactory factory,
        IAntiforgery antiforgery,
        ILoggerFactory loggers,
        HttpMethod method,
        string enginePath,
        bool hasBody,
        bool isPiece)
    {
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";

        // The Engine decides what this person may do from their session. Without one there is nobody to act as, so say so here
        // instead of calling the Engine with the Dashboard's own credential alone.
        if (string.IsNullOrWhiteSpace(context.User.FindFirstValue(DashboardEngineAuthenticationHandler.SessionTokenClaim)))
        {
            response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // Anything that changes something needs the page's antiforgery token (the RequestVerificationToken header), so another
        // site cannot start, send or cancel an upload on a signed-in administrator's behalf.
        if (method != HttpMethod.Get && !await antiforgery.IsRequestValidAsync(context).ConfigureAwait(false))
        {
            await WriteRefusalAsync(response, StatusCodes.Status400BadRequest, RefusalCodeAntiforgery, "The request could not be checked. Reload the page and try again.", context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (isPiece)
        {
            if (context.Request.ContentLength is null)
            {
                response.StatusCode = StatusCodes.Status411LengthRequired;
                return;
            }

            // The request-size limit is raised for the piece route only, to the largest piece the Engine accepts.
            SetBodyLimit(context, ImportUploadLimits.MaxPieceBytes + PieceBodySlackBytes);
        }
        else if (hasBody)
        {
            SetBodyLimit(context, StartBodyLimitBytes);
        }

        using var request = new HttpRequestMessage(method, enginePath);
        if (hasBody)
        {
            request.Content = new StreamContent(context.Request.Body);
            request.Content.Headers.ContentType = isPiece
                ? new MediaTypeHeaderValue("application/octet-stream")
                : new MediaTypeHeaderValue("application/json");
            if (context.Request.ContentLength is { } length)
            {
                request.Content.Headers.ContentLength = length;
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(isPiece ? PieceTimeout : ControlTimeout);

        HttpResponseMessage engineResponse;
        try
        {
            engineResponse = await factory.CreateClient(EngineClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The browser went away; nothing is left to answer. The Engine does not record a piece that did not arrive in full.
            return;
        }
        catch (Exception ex) when (TooLargeBody(ex))
        {
            // The browser sent more than this call allows. That is the browser's mistake, not an unreachable Engine, on every system.
            await WriteRefusalAsync(response, StatusCodes.Status413PayloadTooLarge, RefusalCodeBodyTooLarge, "That upload call was bigger than it may be.", context.RequestAborted).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            loggers.CreateLogger(nameof(ImportUploadRelayEndpoint))
                .LogWarning(ex, "The Engine could not be reached for an upload call ({Method} {Path}).", method, enginePath);
            await WriteRefusalAsync(response, StatusCodes.Status502BadGateway, RefusalCodeEngineUnreachable, "The Library's Engine could not be reached. Try again in a moment.", context.RequestAborted).ConfigureAwait(false);
            return;
        }

        using (engineResponse)
        {
            // Whatever the Engine said, including a refusal and its reason code, reaches the browser as it was.
            response.StatusCode = (int)engineResponse.StatusCode;
            if (engineResponse.Content.Headers.ContentType is { } contentType)
            {
                response.ContentType = contentType.ToString();
            }

            foreach (var name in ForwardedResponseHeaders)
            {
                if (engineResponse.Headers.TryGetValues(name, out var values))
                {
                    response.Headers[name] = values.ToArray();
                }
            }

            await engineResponse.Content.CopyToAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static bool TooLargeBody(Exception ex)
    {
        for (var inner = ex; inner is not null; inner = inner.InnerException)
        {
            if (inner is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
            {
                return true;
            }
        }

        return false;
    }

    private static void SetBodyLimit(HttpContext context, long bytes)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = bytes;
        }
    }

    private static async Task WriteRefusalAsync(HttpResponse response, int statusCode, string code, string detail, CancellationToken ct)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/problem+json";
        await response.WriteAsJsonAsync(
            new Dictionary<string, object?> { ["status"] = statusCode, ["detail"] = detail, ["code"] = code },
            options: null,
            contentType: "application/problem+json",
            cancellationToken: ct).ConfigureAwait(false);
    }
}
