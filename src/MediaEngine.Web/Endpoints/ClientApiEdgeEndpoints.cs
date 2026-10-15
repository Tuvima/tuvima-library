using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Configuration;
using MediaEngine.Web.Services.Configuration;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Antiforgery;

namespace MediaEngine.Web.Endpoints;

public static class ClientApiEdgeEndpoints
{
    public static IEndpointRouteBuilder MapClientApiEdge(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/tuvima", (HttpRequest request) => AppAccessEnabled(request.HttpContext)
            ? Results.Ok(new TuvimaDiscoveryResponse
            {
                ServerId = Environment.MachineName,
                ServerName = Environment.MachineName,
                ApiBaseUrl = $"{request.Scheme}://{request.Host}/api/v1",
                VerificationUri = $"{request.Scheme}://{request.Host}/pair",
            })
            : Results.NotFound())
        .WithName("DiscoverTuvimaPublicEdge")
        .AllowAnonymous();

        app.MapGet("/pair", async (
            string? user_code,
            HttpContext context,
            IHttpClientFactory clients,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!AppAccessEnabled(context))
            {
                return Results.NotFound();
            }

            var code = user_code?.Trim() ?? string.Empty;
            var (review, signedOut) = await ReviewPairingAsync(clients, code, ct);
            if (signedOut)
            {
                return Results.Challenge();
            }

            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Content(PairingPage(code, review, tokens.RequestToken), "text/html; charset=utf-8");
        })
        .WithName("PairClientDevice");

        app.MapPost("/pair", async (
            HttpContext context,
            IHttpClientFactory clients,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!AppAccessEnabled(context))
            {
                return Results.NotFound();
            }

            await antiforgery.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(ct);
            var code = form["user_code"].ToString();
            var approved = string.Equals(form["decision"], "approve", StringComparison.Ordinal);
            var scopes = ApprovedScopes(form["scope"], form["photos"]);

            // Approving with nothing ticked would pair a device that can do nothing, so it is refused here, before the Engine
            // is asked, and the person is shown the screen again. This is the authority; the screen's warning is only a hint.
            if (approved && scopes.Length == 0)
            {
                return await RefusedApprovalAsync(context, clients, antiforgery, code, NothingTickedMessage, ct);
            }

            using var response = await clients.CreateClient("EngineIdentity").PostAsJsonAsync(
                "/api/v1/pairing/decision",
                new PairingDecisionRequest { UserCode = code, Approved = approved, Scopes = scopes },
                ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return Results.Challenge();
            }

            // The Engine refuses an approval it cannot honour (for example photos for an app that may not use them) with a 400
            // and a plain-language reason; show that on the screen so the person can change the choice or deny.
            if (approved && response.StatusCode == HttpStatusCode.BadRequest
                && await ReadProblemDetailAsync(response, ct) is { } refusal)
            {
                return await RefusedApprovalAsync(context, clients, antiforgery, code, refusal, ct);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Results.Content(PairingResultPage("Pairing failed", "The code expired or was already used."), "text/html; charset=utf-8", statusCode: 400);
            }

            return Results.Content(PairingResultPage(
                approved ? "Device paired" : "Pairing denied",
                approved ? "The device can now finish signing in. You may close this page." : "The device was not granted access."),
                "text/html; charset=utf-8");
        })
        .WithName("DecideClientDevicePairing");

        app.MapMethods("/api/v1/{**clientPath}",
            [HttpMethods.Get, HttpMethods.Head, HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete],
            ProxyClientApiAsync)
        .WithName("ProxyClientApiV1")
        .AllowAnonymous();

        app.MapMethods(ApplicationEventClientMethods.HubPath + "/{**hubPath}",
            [HttpMethods.Get, HttpMethods.Post], ProxyApplicationEventsAsync)
        .WithName("ProxyApplicationEvents")
        .AllowAnonymous();

        return app;
    }

    /// <summary>What the person sees when they approve with nothing ticked.</summary>
    internal const string NothingTickedMessage = "Choose at least one thing this device may do, or deny the request.";

    private static async Task<(PairingReviewResponse? Review, bool SignedOut)> ReviewPairingAsync(
        IHttpClientFactory clients,
        string code,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return (null, false);
        }

        using var response = await clients.CreateClient("EngineIdentity")
            .GetAsync($"/api/v1/pairing/review/{Uri.EscapeDataString(code)}", ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return (null, true);
        }

        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync<PairingReviewResponse>(cancellationToken: ct), false)
            : (null, false);
    }

    /// <summary>Shows the pairing screen again with <paramref name="message"/> above the choices; nothing was approved.</summary>
    private static async Task<IResult> RefusedApprovalAsync(
        HttpContext context,
        IHttpClientFactory clients,
        IAntiforgery antiforgery,
        string code,
        string message,
        CancellationToken ct)
    {
        var (review, signedOut) = await ReviewPairingAsync(clients, code, ct);
        if (signedOut)
        {
            return Results.Challenge();
        }

        if (review is null)
        {
            return Results.Content(PairingResultPage("Pairing failed", "The code expired or was already used."), "text/html; charset=utf-8", statusCode: 400);
        }

        var tokens = antiforgery.GetAndStoreTokens(context);
        return Results.Content(PairingPage(code, review, tokens.RequestToken, message), "text/html; charset=utf-8", statusCode: 400);
    }

    private static async Task<string?> ReadProblemDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            return body?["detail"]?.GetValue<string>() is { Length: > 0 } detail ? detail : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Not a problem body: the generic "pairing failed" page is the fallback.
            return null;
        }
    }

    private static async Task ProxyApplicationEventsAsync(
        string? hubPath,
        HttpContext context,
        IHttpClientFactory clients,
        CancellationToken ct)
    {
        if (!AppAccessEnabled(context))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!TryReadBearer(context.Request, out var bearer))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!NativeApiForwardPolicy.IsSafeSubPath(hubPath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var suffix = string.IsNullOrWhiteSpace(hubPath) ? string.Empty : "/" + hubPath.TrimStart('/');
        var upstreamPath = ApplicationEventClientMethods.HubPath + suffix + context.Request.QueryString;
        if (context.WebSockets.IsWebSocketRequest)
        {
            using var http = clients.CreateClient("ClientApiProxy");
            var baseAddress = http.BaseAddress ?? throw new InvalidOperationException("Client API proxy base address is missing.");
            var builder = new UriBuilder(new Uri(baseAddress, upstreamPath))
            {
                Scheme = baseAddress.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            };
            using var upstream = new ClientWebSocket();
            upstream.Options.CollectHttpResponseDetails = true;
            upstream.Options.SetRequestHeader("Authorization", "Bearer " + bearer);
            foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols)
            {
                upstream.Options.AddSubProtocol(protocol);
            }

            try
            {
                await upstream.ConnectAsync(builder.Uri, ct).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                context.Response.StatusCode = upstream.HttpStatusCode switch
                {
                    HttpStatusCode.Unauthorized => StatusCodes.Status401Unauthorized,
                    HttpStatusCode.Forbidden => StatusCodes.Status403Forbidden,
                    _ => StatusCodes.Status502BadGateway,
                };
                return;
            }
            using var downstream = await context.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol).ConfigureAwait(false);
            using var closed = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var toEngine = PumpWebSocketAsync(downstream, upstream, closed.Token);
            var toClient = PumpWebSocketAsync(upstream, downstream, closed.Token);
            await Task.WhenAny(toEngine, toClient).ConfigureAwait(false);
            closed.CancelAfter(TimeSpan.FromSeconds(5));
            try { await Task.WhenAll(toEngine, toClient).ConfigureAwait(false); }
            catch (OperationCanceledException) when (closed.IsCancellationRequested)
            {
                // The bounded shutdown cancels the pump still waiting after its peer has closed.
            }
            catch (WebSocketException) when (closed.IsCancellationRequested ||
                                             downstream.State is WebSocketState.Aborted or WebSocketState.Closed ||
                                             upstream.State is WebSocketState.Aborted or WebSocketState.Closed)
            {
                // A disconnected peer can abort the remaining receive while the other pump finishes.
            }
            finally
            {
                await closed.CancelAsync().ConfigureAwait(false);
            }
            return;
        }

        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), upstreamPath);
        if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            request.Content = new StreamContent(context.Request.Body);
            if (MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType))
            {
                request.Content.Headers.ContentType = contentType;
            }
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await clients.CreateClient("ClientApiProxy")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        context.Response.StatusCode = (int)response.StatusCode;
        CopyResponseHeaders(response, context.Response);
        await response.Content.CopyToAsync(context.Response.Body, ct).ConfigureAwait(false);
    }

    private static async Task PumpWebSocketAsync(WebSocket source, WebSocket destination, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (!ct.IsCancellationRequested &&
               source.State is WebSocketState.Open or WebSocketState.CloseSent &&
               destination.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            var message = await source.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (message.MessageType == WebSocketMessageType.Close)
            {
                await destination.CloseOutputAsync(
                    message.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                    message.CloseStatusDescription,
                    ct).ConfigureAwait(false);
                return;
            }
            await destination.SendAsync(
                buffer.AsMemory(0, message.Count), message.MessageType, message.EndOfMessage, ct).ConfigureAwait(false);
        }
    }

    private static bool TryReadBearer(HttpRequest request, out string token)
    {
        token = string.Empty;
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out var value) ||
            !value.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(value.Parameter))
        {
            return false;
        }

        token = value.Parameter;
        return true;
    }

    private static async Task ProxyClientApiAsync(
        string? clientPath,
        HttpContext context,
        IHttpClientFactory clients,
        CancellationToken ct)
    {
        if (!AppAccessEnabled(context))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var decision = NativeApiForwardPolicy.Evaluate(
            context.Request.Method, clientPath, TryReadBearer(context.Request, out _));
        if (decision != NativeApiForwardDecision.Forward)
        {
            context.Response.StatusCode = decision == NativeApiForwardDecision.Unauthorized
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status404NotFound;
            return;
        }

        if (NativeApiForwardPolicy.IsStartPairing(context.Request.Method, clientPath)
            && context.RequestServices.GetService<NativeAppPairingThrottle>() is { } throttle
            && !throttle.TryAcquire(context.Connection.RemoteIpAddress?.ToString() ?? "unknown"))
        {
            context.Response.Headers.RetryAfter = "60";
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsJsonAsync(new OAuthErrorResponse
            {
                Error = "temporarily_unavailable",
                ErrorDescription = "Too many pairing requests. Try again in a minute.",
                Interval = 60,
            }, ct);
            return;
        }

        var path = clientPath!.TrimStart('/');
        var upstreamPath = path.StartsWith("stream/", StringComparison.OrdinalIgnoreCase)
            ? $"/stream/{path["stream/".Length..]}"
            : path.StartsWith("persons/", StringComparison.OrdinalIgnoreCase)
                ? $"/persons/{path["persons/".Length..]}"
                : path.StartsWith("library/portraits/", StringComparison.OrdinalIgnoreCase)
                    ? $"/library/portraits/{path["library/portraits/".Length..]}"
                    : $"/api/v1/{path}";
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), upstreamPath + context.Request.QueryString);

        if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            request.Content = new StreamContent(context.Request.Body);
            if (MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType))
            {
                request.Content.Headers.ContentType = contentType;
            }
        }

        CopyRequestHeader(context.Request, request, "Authorization");
        CopyRequestHeader(context.Request, request, "Accept");
        CopyRequestHeader(context.Request, request, "Range");
        CopyRequestHeader(context.Request, request, "If-Range");
        CopyRequestHeader(context.Request, request, "If-None-Match");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", context.Request.Scheme);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", context.Request.Host.Value);

        using var response = await clients.CreateClient("ClientApiProxy")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        context.Response.StatusCode = (int)response.StatusCode;
        CopyResponseHeaders(response, context.Response);
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            var json = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode && IsDeviceAuthorization(clientPath))
            {
                var network = context.RequestServices.GetService<DashboardConfigurationReader>()?.LoadNetwork() ?? new NetworkSettings();
                try
                {
                    json = RewriteVerificationUris(json, PairingOrigin(context.Request, network));
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                    // Best effort: an Engine body that is not the expected shape is passed through unchanged.
                    context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("MediaEngine.Web.ClientApiEdge")
                        .LogWarning(ex, "Could not rewrite the pairing links in the Engine response; returning it unchanged.");
                }
            }

            json = json.Replace("\"/stream/", "\"/api/v1/stream/", StringComparison.Ordinal)
                .Replace("\"/playback/", "\"/api/v1/playback/", StringComparison.Ordinal)
                .Replace("\"/persons/", "\"/api/v1/persons/", StringComparison.Ordinal)
                .Replace("\"/library/portraits/", "\"/api/v1/library/portraits/", StringComparison.Ordinal);
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.Headers.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes, ct);
            return;
        }

        await response.Content.CopyToAsync(context.Response.Body, ct);
    }

    private static bool IsDeviceAuthorization(string? clientPath) =>
        string.Equals(clientPath?.Trim('/'), "oauth/device_authorization", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The origin apps are told to open for pairing: the public address when one is set, otherwise this
    /// request's own origin, which has already passed the Dashboard's host allow-list.
    /// </summary>
    internal static string PairingOrigin(HttpRequest request, NetworkSettings network) =>
        network.HasValidPublicAddress()
            ? network.Remote.PublicHostname!.Trim().TrimEnd('/')
            : $"{request.Scheme}://{request.Host}";

    /// <summary>Points <c>verification_uri</c> and <c>verification_uri_complete</c> at <paramref name="origin"/>, keeping path and query.</summary>
    internal static string RewriteVerificationUris(string json, string origin)
    {
        if (JsonNode.Parse(json) is not JsonObject body)
        {
            return json;
        }

        foreach (var name in new[] { "verification_uri", "verification_uri_complete" })
        {
            if (body[name]?.GetValue<string>() is { } value && Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                body[name] = origin.TrimEnd('/') + uri.PathAndQuery;
            }
        }

        return body.ToJsonString();
    }

    /// <summary>
    /// The app door's connection to the private Engine, shared by the Dashboard and its tests so both use
    /// the same hardening: no Dashboard credential, no stored cookies, and Engine redirects handed back
    /// instead of followed.
    /// </summary>
    public static IHttpClientBuilder AddClientApiProxyClient(this IServiceCollection services, Uri engineAddress) =>
        services.AddHttpClient("ClientApiProxy", client => client.BaseAddress = engineAddress)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });

    // Fails closed: with no gate registered, or the switch off, the app door does not exist.
    private static bool AppAccessEnabled(HttpContext context) =>
        context.RequestServices.GetService<INativeAppAccessGate>()?.IsEnabled == true;

    // Engine response headers pass through, except anything that could set state on the app's side
    // of the Dashboard (cookies) or describe the hop-by-hop connection.
    private static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse target)
    {
        foreach (var header in source.Headers.Concat(source.Content.Headers))
        {
            target.Headers[header.Key] = header.Value.ToArray();
        }

        target.Headers.Remove("transfer-encoding");
        target.Headers.Remove("Set-Cookie");
    }

    private static void CopyRequestHeader(HttpRequest source, HttpRequestMessage destination, string name)
    {
        if (source.Headers.TryGetValue(name, out var value))
        {
            destination.Headers.TryAddWithoutValidation(name, value.ToArray());
        }
    }

    /// <summary>The form field of the one "Photos and personal videos" consent item; the three photo scopes never travel as their own fields.</summary>
    internal const string PhotosField = "photos";

    /// <summary>
    /// The scopes the person approved: the ticked ordinary scopes, plus the three photo scopes only when the separate
    /// photo item was ticked. A photo scope posted as an ordinary <c>scope</c> field is dropped, so photo access can only
    /// come from that one explicit tick.
    /// </summary>
    internal static string[] ApprovedScopes(IEnumerable<string?> ticked, string? photosTick)
    {
        var scopes = ticked
            .Where(value => !string.IsNullOrWhiteSpace(value) && !ClientApiScopes.Photos.Contains(value, StringComparer.Ordinal))
            .Select(value => value!)
            .ToList();
        if (string.Equals(photosTick, "allow", StringComparison.Ordinal))
        {
            scopes.AddRange(ClientApiScopes.Photos);
        }

        return [.. scopes];
    }

    private static string PairingPage(string code, PairingReviewResponse? review, string? antiforgeryToken, string? refusal = null)
    {
        var html = HtmlEncoder.Default;

        // The photo item only appears when the app asked for photos and its Application may use them; otherwise ticking it
        // would show "Device paired" and the device would then fail to sign in.
        var photoScopes = review is { OffersPhotoScopes: true }
            ? review.RequestedScopes.Where(scope => ClientApiScopes.Photos.Contains(scope, StringComparer.Ordinal)).ToArray()
            : [];
        var photoItem = photoScopes.Length == 0
            ? string.Empty
            : $"""
                <div class="photos"><label><input type=checkbox name="{PhotosField}" value="allow"> <strong>Allow access to my photos and videos</strong></label>
                <p>Photos and personal videos: browse your own photos and videos, view and download originals, and your galleries (including galleries shared with you).</p>
                <p class="note">Leave this unticked and the device cannot see any of them. Asked for: {string.Join(", ", photoScopes.Select(scope => html.Encode(scope)))}.</p></div>
                """;
        var body = review is null
            ? $"""
                <h1>Pair a device</h1>
                <p>Enter the code shown on your television or native client.</p>
                <form method="get"><label>Pairing code <input name="user_code" value="{html.Encode(code)}" autocomplete="one-time-code" required></label><button>Continue</button></form>
                {(!string.IsNullOrWhiteSpace(code) ? "<p class=error>That code is invalid or expired.</p>" : string.Empty)}
                """
            : $"""
                <h1>Pair {html.Encode(review.DeviceName)}</h1>
                <p><strong>{html.Encode(review.ClientName)}</strong> {html.Encode(review.ClientVersion)} identifies as a {html.Encode(review.DeviceClass)} device.</p>
                <p>Grant only the access you expect:</p>
                {(refusal is null ? string.Empty : $"<p class=error role=alert>{html.Encode(refusal)}</p>")}
                <form method="post">
                  <input type="hidden" name="__RequestVerificationToken" value="{html.Encode(antiforgeryToken ?? string.Empty)}">
                  <input type="hidden" name="user_code" value="{html.Encode(code)}">
                  <ul>{string.Join(string.Empty, review.RequestedScopes.Where(scope => !ClientApiScopes.Photos.Contains(scope, StringComparer.Ordinal)).Select(scope => $"<li><label><input type=checkbox name=scope value=\"{html.Encode(scope)}\" checked> {html.Encode(scope)}</label></li>"))}</ul>
                  {photoItem}
                  <p class="note" id="approve-hint">{html.Encode(NothingTickedMessage)} Approving with nothing ticked is refused.</p>
                  <button name="decision" value="approve" aria-describedby="approve-hint">Approve device</button>
                  <button class="secondary" name="decision" value="deny">Deny</button>
                </form>
                """;
        return Layout(body);
    }

    private static string PairingResultPage(string title, string message) =>
        Layout($"<h1>{HtmlEncoder.Default.Encode(title)}</h1><p>{HtmlEncoder.Default.Encode(message)}</p>");

    private static string Layout(string body) => """
        <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
        <title>Tuvima Library device pairing</title>
        <style>body{font:16px system-ui;background:#0e0b17;color:#f7f3ff;max-width:42rem;margin:10vh auto;padding:2rem}form{display:grid;gap:1rem}input{font:inherit;padding:.7rem}button{font:inherit;padding:.8rem 1rem;background:#8852FC;color:white;border:0;border-radius:.5rem}button.secondary{background:#40384f}li{margin:.5rem 0}.error{color:#fca5a5}.photos{border:1px solid #40384f;border-radius:.5rem;padding:1rem}.photos p{margin:.5rem 0 0}.note{color:#b9b0cc;font-size:.9rem}</style>
        <main>
        """ + body + """
        </main></html>
        """;
}

/// <summary>
/// Keeps the native app door's own "not found" answer. The Dashboard turns every 404 into a re-run of its
/// <c>/not-found</c> page, which needs a sign-in, so a closed door would send a visitor to the sign-in page
/// instead. Door paths skip that re-run; every other page still gets the normal not-found page.
/// </summary>
public static class DoorStatusCodePagesExtensions
{
    public static IApplicationBuilder UseStatusCodePagesExceptDoors(this IApplicationBuilder app, string statusCodePagePath) =>
        app.UseWhen(
            context => !IsDoorPath(context.Request.Path),
            branch => branch.UseStatusCodePagesWithReExecute(statusCodePagePath, createScopeForStatusCodePages: true));

    public static bool IsDoorPath(PathString path) =>
        path.StartsWithSegments("/.well-known/tuvima", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/pair", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/v1", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments(ApplicationEventClientMethods.HubPath, StringComparison.OrdinalIgnoreCase);
}
