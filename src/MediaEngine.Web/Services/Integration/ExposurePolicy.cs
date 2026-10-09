using System.Net;
using MediaEngine.Domain.Configuration;
using MediaEngine.Web.Services.Configuration;

namespace MediaEngine.Web.Services.Integration;

/// <summary>What the exposure policy decided for one request.</summary>
public enum ExposureDecision
{
    Allow,

    /// <summary>The visitor is farther from the computer than "who can connect" allows (403).</summary>
    NotAvailableHere,

    /// <summary>Allowed from the internet, but only over HTTPS or a verified tunnel (426).</summary>
    UpgradeRequired,
}

/// <summary>
/// The Dashboard's one door rule: a request is allowed when how far away it came from
/// (this computer, home network, remote) is no farther than the "who can connect" setting
/// (this computer, home network, anywhere). Remote visitors always need HTTPS.
/// </summary>
public static class ExposurePolicy
{
    public const string RefusalMessage =
        "Tuvima Library isn't available from here. The owner can change this under Settings > Network.";

    public const string HttpsRequiredMessage = "Remote access requires a verified HTTPS or tunnel path.";

    public static ExposureDecision Evaluate(string? whoCanConnect, IngressKind ingress, bool isHttps)
    {
        if (Rank(ingress) > Rank(whoCanConnect))
        {
            return ExposureDecision.NotAvailableHere;
        }

        return ingress == IngressKind.Remote && !isHttps
            ? ExposureDecision.UpgradeRequired
            : ExposureDecision.Allow;
    }

    private static int Rank(IngressKind ingress) => ingress switch
    {
        IngressKind.ThisComputer => 0,
        IngressKind.HomeNetwork => 1,
        _ => 2,
    };

    // An unknown value is treated as the most restrictive setting (fail closed).
    private static int Rank(string? whoCanConnect) => whoCanConnect?.Trim().ToLowerInvariant() switch
    {
        WhoCanConnectModes.Anywhere => 2,
        WhoCanConnectModes.HomeNetwork => 1,
        _ => 0,
    };
}

/// <summary>
/// Reads <c>who_can_connect</c> from <c>config/network.json</c>, the file the Engine saves. The file is re-read
/// only when it changes, so a new setting applies on the next request with no restart and no Engine round-trip.
/// A missing file means the default (home network); an unreadable or invalid file fails closed to this computer.
/// </summary>
public sealed class ExposureSettingsReader(DashboardConfigurationReader configuration, string configDirectory)
{
    private readonly object _gate = new();
    private DateTime _lastWriteUtc = DateTime.MinValue;
    private long _length = -1;
    private string _whoCanConnect = WhoCanConnectModes.HomeNetwork;

    public string WhoCanConnect
    {
        get
        {
            var path = Path.Combine(configDirectory, "network.json");
            try
            {
                var info = new FileInfo(path);
                lock (_gate)
                {
                    if (!info.Exists)
                    {
                        _whoCanConnect = WhoCanConnectModes.HomeNetwork;
                        _lastWriteUtc = DateTime.MinValue;
                        _length = -1;
                    }
                    else if (info.LastWriteTimeUtc != _lastWriteUtc || info.Length != _length)
                    {
                        _whoCanConnect = configuration.LoadNetwork().WhoCanConnect;
                        _lastWriteUtc = info.LastWriteTimeUtc;
                        _length = info.Length;
                    }

                    return _whoCanConnect;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                // Fail closed: an unreadable or invalid network file only admits this computer. The next request retries.
                lock (_gate)
                {
                    _whoCanConnect = WhoCanConnectModes.ThisComputer;
                    _lastWriteUtc = DateTime.MinValue;
                    _length = -1;
                }

                return WhoCanConnectModes.ThisComputer;
            }
        }
    }
}

public static class ExposurePolicyMiddlewareExtensions
{
    /// <summary>
    /// Enforces <see cref="ExposurePolicy"/> on every Dashboard surface (pages, /_blazor, media proxies, /auth/*,
    /// /api/v1/*, /application-events/*, static assets). Only <c>/health/live</c> is exempt.
    /// </summary>
    public static IApplicationBuilder UseExposurePolicy(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/health/live", StringComparison.OrdinalIgnoreCase))
            {
                await next().ConfigureAwait(false);
                return;
            }

            var ingress = context.RequestServices.GetRequiredService<IngressClassifier>().Classify(context);
            var setting = context.RequestServices.GetRequiredService<ExposureSettingsReader>().WhoCanConnect;
            switch (ExposurePolicy.Evaluate(setting, ingress, context.Request.IsHttps))
            {
                case ExposureDecision.Allow:
                    await next().ConfigureAwait(false);
                    return;
                case ExposureDecision.UpgradeRequired:
                    context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                    context.Response.Headers.CacheControl = "no-store";
                    context.Response.ContentType = "text/plain; charset=utf-8";
                    await context.Response.WriteAsync(ExposurePolicy.HttpsRequiredMessage).ConfigureAwait(false);
                    return;
                default:
                    await WriteRefusalAsync(context).ConfigureAwait(false);
                    return;
            }
        });

    private static async Task WriteRefusalAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.Headers.CacheControl = "no-store";

        var path = context.Request.Path;
        if (path.StartsWithSegments("/api/v1", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/application-events", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("{\"error\":\"not_available_here\"}").ConfigureAwait(false);
            return;
        }

        if (context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(
                "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Tuvima Library</title>" +
                "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"></head>" +
                "<body style=\"font-family:system-ui,sans-serif;max-width:32rem;margin:20vh auto;padding:0 1rem\">" +
                $"<p>{WebUtility.HtmlEncode(ExposurePolicy.RefusalMessage)}</p></body></html>").ConfigureAwait(false);
            return;
        }

        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(ExposurePolicy.RefusalMessage).ConfigureAwait(false);
    }
}
