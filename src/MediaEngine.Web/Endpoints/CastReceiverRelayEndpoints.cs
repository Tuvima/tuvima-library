using System.Text.RegularExpressions;
using MediaEngine.Web.Services.Configuration;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Endpoints;

/// <summary>
/// The Dashboard side of casting. A cast receiver (a Chromecast or Google TV) cannot send a bearer token, so the Engine
/// serves it one title at a time from a secret link, <c>/cast/v1/{grant}/...</c>, that carries its own signed grant. The Engine
/// itself answers that address only on this computer; this relay is the one way a TV on the home network reaches it.
/// <para>
/// It exists only while app access is on, answers only a request that came from this computer or the home network, forwards
/// only the range headers (never <c>Authorization</c> or a cookie, so a stray credential cannot ride along), and copies back
/// only the media and CORS headers the receiver needs. The grant itself is checked by the Engine on every request.
/// </para>
/// </summary>
public static class CastReceiverRelayEndpoints
{
    private static readonly Regex GrantShape = new("^[A-Za-z0-9_-]{16,4096}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The only request headers passed to the Engine.</summary>
    internal static readonly string[] ForwardedRequestHeaders = ["Range", "If-Range"];

    /// <summary>The only response headers copied back to the receiver.</summary>
    internal static readonly string[] ReturnedResponseHeaders =
    [
        "Content-Type", "Content-Length", "Content-Range", "Accept-Ranges", "ETag", "Last-Modified", "Cache-Control",
        "Access-Control-Allow-Origin", "Access-Control-Allow-Methods", "Access-Control-Allow-Headers",
        "Access-Control-Expose-Headers", "Access-Control-Max-Age", "Referrer-Policy", "X-Content-Type-Options",
    ];

    public static IEndpointRouteBuilder MapCastReceiverRelay(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/cast/v1/{grant}/{**rest}",
            [HttpMethods.Get, HttpMethods.Head, HttpMethods.Options],
            RelayAsync)
        .WithName("RelayCastReceiver")
        .AllowAnonymous();
        return app;
    }

    private static async Task RelayAsync(
        string grant,
        string? rest,
        HttpContext context,
        IHttpClientFactory clients,
        CancellationToken ct)
    {
        var gate = context.RequestServices.GetService<INativeAppAccessGate>();
        var ingress = context.RequestServices.GetService<IngressClassifier>()?.Classify(context);
        if (gate?.IsEnabled != true
            || ingress is not (IngressKind.ThisComputer or IngressKind.HomeNetwork)
            || !GrantShape.IsMatch(grant)
            || string.IsNullOrEmpty(rest)
            || !NativeApiForwardPolicy.IsSafeSubPath(rest))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), $"/cast/v1/{grant}/{rest.TrimStart('/')}");
        foreach (var name in ForwardedRequestHeaders)
        {
            if (context.Request.Headers.TryGetValue(name, out var value))
            {
                request.Headers.TryAddWithoutValidation(name, value.ToArray());
            }
        }

        // The preflight needs to be answered by the Engine with the same headers a real request gets.
        foreach (var name in new[] { "Origin", "Access-Control-Request-Method", "Access-Control-Request-Headers" })
        {
            if (HttpMethods.IsOptions(context.Request.Method) && context.Request.Headers.TryGetValue(name, out var value))
            {
                request.Headers.TryAddWithoutValidation(name, value.ToArray());
            }
        }

        using var response = await clients.CreateClient("ClientApiProxy")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (ReturnedResponseHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
        }

        if (HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method))
        {
            return;
        }

        await response.Content.CopyToAsync(context.Response.Body, ct).ConfigureAwait(false);
    }
}
