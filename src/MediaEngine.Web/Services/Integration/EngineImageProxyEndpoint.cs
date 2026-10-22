namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Carries one Engine picture to the signed-in browser. The browser's "has it changed?" headers
/// reach the Engine so an unchanged picture comes back as a 304 with no body, and the Engine's
/// own Cache-Control, ETag and Last-Modified go back untouched: the Engine alone decides how long
/// a picture may be kept (a long keep only for an address whose change marker still matches).
/// </summary>
public static class EngineImageProxyEndpoint
{
    public static async Task HandleAsync(
        string enginePath,
        HttpContext ctx,
        HttpClient client,
        CancellationToken ct)
    {
        var upstreamPath = $"/{enginePath.TrimStart('/')}";
        if (!EngineImageProxyPath.IsAllowedEnginePath(upstreamPath))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using var request = new HttpRequestMessage(
            HttpMethods.IsHead(ctx.Request.Method) ? HttpMethod.Head : HttpMethod.Get,
            $"{upstreamPath}{ctx.Request.QueryString}");
        CopyRequestHeader(ctx, request, "If-None-Match");
        CopyRequestHeader(ctx, request, "If-Modified-Since");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        ctx.Response.StatusCode = (int)response.StatusCode;
        CopyResponseHeaders(response, ctx.Response);

        if (!HttpMethods.IsHead(ctx.Request.Method) && response.StatusCode != System.Net.HttpStatusCode.NotModified)
        {
            await response.Content.CopyToAsync(ctx.Response.Body, ct);
        }
    }

    private static void CopyRequestHeader(HttpContext ctx, HttpRequestMessage request, string headerName)
    {
        if (ctx.Request.Headers.TryGetValue(headerName, out var values))
        {
            request.Headers.TryAddWithoutValidation(headerName, values.ToArray());
        }
    }

    private static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse target)
    {
        // The Engine's own header text goes back exactly as sent (NonValidated), so its keep rule is not re-ordered or rewritten.
        foreach (var header in source.Headers.NonValidated)
        {
            target.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (var header in source.Content.Headers)
        {
            target.Headers[header.Key] = header.Value.ToArray();
        }

        target.Headers.Remove("transfer-encoding");
    }
}
