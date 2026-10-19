namespace MediaEngine.Web.Endpoints;

/// <summary>
/// Browsers and other tools ask for <c>/favicon.ico</c> on their own, often without cookies, even though the page names
/// <c>favicon.svg</c> and <c>favicon.png</c>. Without this the request falls through to the signed-in page router and is
/// sent to the sign-in page, which on this computer starts a new Engine session for every icon fetch.
/// </summary>
public static class FaviconEndpoint
{
    public const string Path = "/favicon.ico";

    public static IEndpointConventionBuilder MapFaviconIco(this IEndpointRouteBuilder app) =>
        app.MapGet(Path, (IWebHostEnvironment environment) =>
            environment.WebRootFileProvider.GetFileInfo("favicon.png") is { Exists: true, PhysicalPath: { } icon }
                ? Results.File(icon, "image/png")
                : Results.NotFound())
            .AllowAnonymous();
}
