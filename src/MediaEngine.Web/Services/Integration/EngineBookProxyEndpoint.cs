using System.Net;
using System.Security.Claims;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Streams one book or comic file from the Engine to the signed-in browser, with byte ranges.
/// The Engine decides who may read the file (hidden libraries, feature, profile content limit);
/// this route only carries the request across with the Dashboard's credentials resolved at send
/// time by the shared Engine client, and answers "not found" for anything the Engine refuses so
/// a browser cannot probe which books exist.
/// </summary>
public static class EngineBookProxyEndpoint
{
    private const string EngineClientName = "EngineApi";

    // Only these request headers cross to the Engine. Cookies and Authorization never do.
    private static readonly string[] ForwardedRequestHeaders =
        ["Range", "If-Range", "If-None-Match", "If-Modified-Since"];

    // Only these response headers cross back; anything else the Engine sends stays behind.
    private static readonly HashSet<string> ForwardedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type",
        "Content-Length",
        "Content-Range",
        "Accept-Ranges",
        "ETag",
        "Last-Modified",
        "Retry-After",
    };

    public static IEndpointConventionBuilder MapEngineBookProxy(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(string.Empty);
        group.MapGet(EngineBookProxyPath.FileRoute, HandleAsync)
            .WithName("ProxyEngineBookFile")
            .WithSummary("Streams one book or comic file from the Engine through the Dashboard origin, with byte ranges.");
        group.MapGet(EngineBookProxyPath.PagesRoute, HandlePagesAsync)
            .WithName("ProxyEngineComicPages")
            .WithSummary("Carries a comic's page list from the Engine to the signed-in browser.");
        group.MapGet(EngineBookProxyPath.PageRoute, HandlePageAsync)
            .WithName("ProxyEngineComicPage")
            .WithSummary("Carries one comic page picture, at an allowed width, from the Engine to the signed-in browser.");
        return group.RequireAuthorization();
    }

    public static Task HandlePagesAsync(
        HttpContext context,
        IHttpClientFactory httpFactory,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ApplySafeHeaders(context.Response);
        if (!EngineBookProxyPath.TryParsePagesPath(context.Request.Path.Value, withPage: false, out var assetId, out _)
            || context.Request.Query.Count > 0)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        return ProxyAsync(context, httpFactory, loggerFactory, EngineBookProxyPath.ToEnginePagesPath(assetId), assetId, cancellationToken);
    }

    public static Task HandlePageAsync(
        HttpContext context,
        IHttpClientFactory httpFactory,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ApplySafeHeaders(context.Response);
        if (!EngineBookProxyPath.TryParsePagesPath(context.Request.Path.Value, withPage: true, out var assetId, out var page)
            || !EngineBookProxyPath.TryReadWidth(context.Request.Query, out var width))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        // A page picture never changes for the same file, and every request is still authorised by the Engine first,
        // so the browser may keep it briefly for the next turn (spec D18) but never share it.
        context.Response.Headers.CacheControl = "private, max-age=3600";
        return ProxyAsync(context, httpFactory, loggerFactory, EngineBookProxyPath.ToEnginePagePath(assetId, page, width), assetId, cancellationToken);
    }

    public static async Task HandleAsync(
        HttpContext context,
        IHttpClientFactory httpFactory,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        // The id is read back from the strict path, not from the route value, so the Engine call
        // below is always built from a canonical id and the fixed suffix.
        ApplySafeHeaders(context.Response);
        if (!EngineBookProxyPath.TryParseBrowserPath(context.Request.Path.Value, out var assetId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await ProxyAsync(context, httpFactory, loggerFactory, EngineBookProxyPath.ToEnginePath(assetId), assetId, cancellationToken);
    }

    private static async Task ProxyAsync(
        HttpContext context,
        IHttpClientFactory httpFactory,
        ILoggerFactory loggerFactory,
        string enginePath,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        // The Engine decides what this person may read from their session. Without one there is nobody to
        // authorise as, so say so here instead of calling the Engine with the Dashboard's own credential alone.
        if (string.IsNullOrWhiteSpace(context.User.FindFirstValue(DashboardEngineAuthenticationHandler.SessionTokenClaim)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, enginePath);
        foreach (var name in ForwardedRequestHeaders)
        {
            if (context.Request.Headers.TryGetValue(name, out var values) && values.Count > 0)
            {
                request.Headers.TryAddWithoutValidation(name, values.ToArray());
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await httpFactory.CreateClient(EngineClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The browser went away; nothing is left to answer.
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            loggerFactory.CreateLogger(nameof(EngineBookProxyEndpoint))
                .LogWarning(ex, "The Engine could not be reached for book file {AssetId}.", assetId);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using (response)
        {
            var status = MapStatus(response.StatusCode);
            context.Response.StatusCode = status;
            if (status == StatusCodes.Status502BadGateway)
            {
                loggerFactory.CreateLogger(nameof(EngineBookProxyEndpoint))
                    .LogWarning("The Engine answered {Status} for book file {AssetId}.", (int)response.StatusCode, assetId);
                return;
            }

            if (status == StatusCodes.Status404NotFound)
            {
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    // Refusals read as "not found" to the browser; keep the real reason for whoever investigates.
                    loggerFactory.CreateLogger(nameof(EngineBookProxyEndpoint))
                        .LogDebug("The Engine refused book file {AssetId}; answered 404.", assetId);
                }

                return;
            }

            CopyAllowedHeaders(response, context.Response);
            if (status is StatusCodes.Status200OK or StatusCodes.Status206PartialContent)
            {
                await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
            }
        }
    }

    /// <summary>
    /// What a browser may be told. Refused and missing both read as "not found" so the existence
    /// of a book the viewer may not open is not revealed; anything unexpected is a bad gateway.
    /// </summary>
    internal static int MapStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.OK => StatusCodes.Status200OK,
        HttpStatusCode.PartialContent => StatusCodes.Status206PartialContent,
        HttpStatusCode.NotModified => StatusCodes.Status304NotModified,
        HttpStatusCode.RequestedRangeNotSatisfiable => StatusCodes.Status416RangeNotSatisfiable,
        HttpStatusCode.Unauthorized => StatusCodes.Status401Unauthorized,
        HttpStatusCode.TooManyRequests => StatusCodes.Status429TooManyRequests,
        // A damaged, protected or over-limit comic: the reader shows "This file can't be opened".
        HttpStatusCode.UnprocessableEntity => StatusCodes.Status422UnprocessableEntity,
        HttpStatusCode.Forbidden or HttpStatusCode.NotFound => StatusCodes.Status404NotFound,
        _ => StatusCodes.Status502BadGateway,
    };

    // The file is the user's own and may be anything inside a zip or a PDF, so it is never allowed
    // to run as a page on the Dashboard origin, and a cached copy is reused only after a fresh check.
    private static void ApplySafeHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "private, no-cache";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
    }

    private static void CopyAllowedHeaders(HttpResponseMessage source, HttpResponse target)
    {
        // A Vault title's pictures come back "no-store" and must stay that way, whatever the page route set before.
        if (source.Headers.CacheControl?.NoStore == true)
        {
            target.Headers.CacheControl = "private, no-store";
        }

        foreach (var header in source.Headers.Concat(source.Content.Headers))
        {
            if (ForwardedResponseHeaders.Contains(header.Key))
            {
                target.Headers[header.Key] = header.Value.ToArray();
            }
        }
    }
}
