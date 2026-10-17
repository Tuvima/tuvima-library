using MediaEngine.Processors.Contracts;

namespace MediaEngine.Api.Http;

/// <summary>
/// One place for HTTP byte-range answers (RFC 7233) so the signed-in stream route and the cast receiver routes behave the
/// same: <c>Accept-Ranges</c>, 206 with <c>Content-Range</c>, whole-file 200, and HEAD without a body.
/// </summary>
public static class ByteRangeResponder
{
    public static async Task WriteFileAsync(
        HttpContext context,
        IByteStreamer streamer,
        string filePath,
        string contentType,
        bool headOnly,
        CancellationToken ct)
    {
        context.Response.Headers.AcceptRanges = "bytes";
        var totalSize = await streamer.GetFileSizeAsync(filePath, ct).ConfigureAwait(false);

        if (context.Request.Headers.TryGetValue("Range", out var rangeHeader)
            && TryParseRange(rangeHeader.ToString(), totalSize, out var rangeStart, out var rangeEnd))
        {
            var length = rangeEnd - rangeStart + 1;
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.ContentType = contentType;
            if (headOnly)
            {
                context.Response.Headers.ContentRange = $"bytes {rangeStart}-{rangeEnd}/{totalSize}";
                context.Response.Headers.ContentLength = length;
                return;
            }

            using var result = await streamer.GetRangeAsync(filePath, rangeStart, length, ct).ConfigureAwait(false);
            context.Response.Headers.ContentRange = result.ContentRangeHeader;
            context.Response.Headers.ContentLength = result.ContentLength;
            await result.Content.CopyToAsync(context.Response.Body, ct).ConfigureAwait(false);
            return;
        }

        context.Response.ContentType = contentType;
        context.Response.Headers.ContentLength = totalSize;
        if (headOnly)
        {
            return;
        }

        using var whole = await streamer.GetRangeAsync(filePath, 0, null, ct).ConfigureAwait(false);
        await whole.Content.CopyToAsync(context.Response.Body, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses the RFC 7233 Range header value "bytes=start-end". Both start and end may be absent. Returns false if the
    /// header cannot be parsed or the range is unsatisfiable.
    /// </summary>
    public static bool TryParseRange(string rangeHeader, long totalSize, out long start, out long end)
    {
        start = 0;
        end = totalSize > 0 ? totalSize - 1 : 0;

        if (!rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rangePart = rangeHeader["bytes=".Length..];
        var dashIdx = rangePart.IndexOf('-');
        if (dashIdx < 0)
        {
            return false;
        }

        var startStr = rangePart[..dashIdx].Trim();
        var endStr = rangePart[(dashIdx + 1)..].Trim();

        // "bytes=-500" is the last 500 bytes (suffix range).
        if (startStr.Length == 0 && long.TryParse(endStr, out var suffixLength))
        {
            start = Math.Max(0, totalSize - suffixLength);
            end = totalSize - 1;
            return totalSize > 0;
        }

        if (!long.TryParse(startStr, out start))
        {
            return false;
        }

        if (endStr.Length == 0)
        {
            end = totalSize - 1;
        }
        else if (!long.TryParse(endStr, out end))
        {
            return false;
        }

        start = Math.Max(0, start);
        end = Math.Min(end, totalSize - 1);
        return start <= end && totalSize > 0;
    }
}
