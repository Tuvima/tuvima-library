namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// The narrowest cross-origin allowance for packaged TV apps (Tizen, webOS) that are not served from the Dashboard's
/// own origin. A packaged app's page origin is <c>null</c> or an app scheme (<c>file:</c>, <c>app:</c>, <c>tizen:</c>,
/// <c>webos:</c>), so the browser asks permission before it calls the app door.
/// <para>
/// Off by default. Any web page (a sandboxed frame, a local file) can present the <c>null</c> origin, so while this is on
/// a website could reach the anonymous pairing surface; a person must still approve the code on <c>/pair</c>. It is
/// therefore a separate opt-in (<c>native_app_access.allow_browser_apps</c>, config file only) that should be on only
/// for TV web apps.
/// </para>
/// <para>
/// When on, the allowance is: only those origins, only <c>/api/v1/**</c> and <c>/.well-known/tuvima</c>, only the
/// methods GET, POST, PUT, DELETE (and the OPTIONS preflight), only the <c>Authorization</c> and <c>Content-Type</c>
/// request headers, and never credentials (apps send their bearer token in a header, not a cookie, so the Dashboard's
/// own sign-in cookie is never usable from another site). A preflight for a route the app door would refuse anyway is
/// not approved. Origins that are ordinary web addresses get nothing.
/// </para>
/// </summary>
public static class NativeAppCors
{
    public const string AllowedMethods = "GET, POST, PUT, DELETE, OPTIONS";
    public const string AllowedHeaders = "Authorization, Content-Type";
    public const string ExposedHeaders = "Retry-After";
    public const string PreflightMaxAgeSeconds = "600";

    private const string ApiPrefix = "/api/v1";
    private const string DiscoveryPath = "/.well-known/tuvima";
    private static readonly string[] AppSchemes = ["file", "app", "tizen", "webos"];
    private static readonly string[] PreflightMethods = ["GET", "POST", "PUT", "DELETE"];
    private static readonly string[] RequestHeaders = ["authorization", "content-type"];

    /// <summary>True for the <c>null</c> origin and for origins that use one of the app schemes with no path, query, or user.</summary>
    public static bool IsAppOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        if (origin == "null")
        {
            return true;
        }

        foreach (var scheme in AppSchemes)
        {
            var prefix = scheme + "://";
            if (!origin.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = origin[prefix.Length..];
            return !rest.Any(c => c is '/' or '\\' or '?' or '#' or '@' || char.IsWhiteSpace(c) || char.IsControl(c));
        }

        return false;
    }

    /// <summary>True when a preflight for <paramref name="method"/> on <paramref name="path"/> may be approved.</summary>
    public static bool IsAllowedRoute(string method, PathString path)
    {
        if (path.Equals(DiscoveryPath, StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);
        }

        if (!path.StartsWithSegments(ApiPrefix, StringComparison.OrdinalIgnoreCase, out var rest))
        {
            return false;
        }

        // The same allow-list the app door uses, so a cross-origin approval never covers a route the door refuses.
        return NativeApiForwardPolicy.Evaluate(method, rest.Value, hasBearerToken: true) == NativeApiForwardDecision.Forward;
    }

    private static bool IsCorsPath(PathString path) =>
        path.Equals(DiscoveryPath, StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments(ApiPrefix, StringComparison.OrdinalIgnoreCase);

    internal static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length == 0
            || !IsCorsPath(context.Request.Path)
            || !IsAppOrigin(origin)
            || context.RequestServices.GetService<INativeAppAccessGate>() is not { AllowBrowserApps: true })
        {
            await next(context);
            return;
        }

        var requestedMethod = context.Request.Headers.AccessControlRequestMethod.ToString();
        if (HttpMethods.IsOptions(context.Request.Method) && requestedMethod.Length > 0)
        {
            if (PreflightMethods.Contains(requestedMethod, StringComparer.OrdinalIgnoreCase)
                && RequestedHeadersAllowed(context.Request.Headers.AccessControlRequestHeaders.ToString())
                && IsAllowedRoute(requestedMethod, context.Request.Path))
            {
                var headers = context.Response.Headers;
                headers.AccessControlAllowOrigin = origin;
                headers.AccessControlAllowMethods = AllowedMethods;
                headers.AccessControlAllowHeaders = AllowedHeaders;
                headers.AccessControlMaxAge = PreflightMaxAgeSeconds;
            }

            AppendVaryOrigin(context.Response);

            // A refused preflight carries no allow headers, which makes the browser block the real request.
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        // Set when the response starts, after the app door has copied the Engine's headers (which can replace Vary).
        context.Response.OnStarting(() =>
        {
            AppendVaryOrigin(context.Response);
            context.Response.Headers.AccessControlAllowOrigin = origin;
            context.Response.Headers.AccessControlExposeHeaders = ExposedHeaders;
            return Task.CompletedTask;
        });
        await next(context);
    }

    private static void AppendVaryOrigin(HttpResponse response)
    {
        var vary = response.Headers.Vary;
        if (!vary.Any(value => value is not null && value.Split(',', StringSplitOptions.TrimEntries)
                .Contains("Origin", StringComparer.OrdinalIgnoreCase)))
        {
            response.Headers.Append("Vary", "Origin");
        }
    }

    private static bool RequestedHeadersAllowed(string requested) =>
        requested.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(name => RequestHeaders.Contains(name, StringComparer.OrdinalIgnoreCase));
}

public static class NativeAppCorsExtensions
{
    /// <summary>Adds <see cref="NativeAppCors"/>. Place it before routing and before the HTTPS redirect so a preflight is answered, never redirected.</summary>
    public static IApplicationBuilder UseNativeAppCors(this IApplicationBuilder app) =>
        app.Use(NativeAppCors.InvokeAsync);
}
