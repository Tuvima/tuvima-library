using System.Text;
using System.Text.Json;

namespace MediaEngine.Web.Services;

/// <summary>Explicit disposable-fixture opt-in for CSS text-size stress captures.</summary>
public sealed class HomeMediaQaTextMiddleware(RequestDelegate next, IWebHostEnvironment environment)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!ShouldEnable(environment.IsDevelopment(), Environment.GetEnvironmentVariable("TUVIMA_HOME_MEDIA_QA"),
                Environment.GetEnvironmentVariable("TUVIMA_CONFIG_DIR"), context.Request.Query["qaTextScale"].ToString())
            || !HttpMethods.IsGet(context.Request.Method))
        {
            await next(context); return;
        }

        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
            buffer.Position = 0;
            if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
            {
                using var reader = new StreamReader(buffer, Encoding.UTF8, leaveOpen: true);
                var html = await reader.ReadToEndAsync(context.RequestAborted);
                html = html.Replace("</head>", "<style id=\"home-media-qa-text-stress\">html{font-size:200%!important}</style></head>", StringComparison.OrdinalIgnoreCase);
                context.Response.ContentLength = null;
                context.Response.Body = original;
                await context.Response.WriteAsync(html, context.RequestAborted);
            }
            else
            {
                context.Response.Body = original;
                await buffer.CopyToAsync(original, context.RequestAborted);
            }
        }
        finally { context.Response.Body = original; }
    }

    public static bool ShouldEnable(bool development, string? fixtureRoot, string? configDirectory, string? scale)
    {
        if (!development || scale != "200" || string.IsNullOrWhiteSpace(fixtureRoot) || string.IsNullOrWhiteSpace(configDirectory)) return false;
        try
        {
            var root = Path.GetFullPath(fixtureRoot);
            if (!Path.GetFileName(root).StartsWith("fixture-", StringComparison.Ordinal)
                || !string.Equals(Path.GetFullPath(configDirectory), Path.Combine(root, "config"), StringComparison.OrdinalIgnoreCase)) return false;
            using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".home-media-cards-disposable.json")));
            return marker.RootElement.GetProperty("task").GetString() == "home-media-cards-2026-10-03"
                && string.Equals(marker.RootElement.GetProperty("root").GetString(), root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or KeyNotFoundException)
        { return false; }
    }
}
