using MediaEngine.Domain;
using Microsoft.Net.Http.Headers;

namespace MediaEngine.Api.Endpoints;

/// <summary>How long a browser may keep a served picture.</summary>
internal enum ArtworkCacheMode
{
    /// <summary>Kept, but checked with the Engine on each use (a cheap "not changed" answer).</summary>
    Revalidate,

    /// <summary>Kept for a year without asking: the address carries the picture's current change marker.</summary>
    Immutable,
}

/// <summary>
/// The one place Engine picture files are turned into responses, with the browser keep rules
/// (right-sized pictures spec D4/D5). Pictures belong to signed-in people, so they are only ever
/// kept by the viewer's own browser (<c>private</c>), never by a shared cache.
/// </summary>
internal static class ArtworkFileResults
{
    public const string ImmutableCacheControl = "private, max-age=31536000, immutable";
    public const string RevalidateCacheControl = "private, no-cache";
    public const string PlaceholderCacheControl = "no-store";

    private const string PlaceholderSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 320 480" role="img" aria-label="Artwork unavailable">
          <defs>
            <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
              <stop offset="0" stop-color="#1f2937"/>
              <stop offset="0.55" stop-color="#334155"/>
              <stop offset="1" stop-color="#0f172a"/>
            </linearGradient>
          </defs>
          <rect width="320" height="480" rx="16" fill="url(#bg)"/>
          <rect x="68" y="146" width="184" height="188" rx="12" fill="none" stroke="#94a3b8" stroke-width="10" opacity="0.72"/>
          <circle cx="124" cy="204" r="24" fill="#cbd5e1" opacity="0.78"/>
          <path d="M82 308l56-62 38 42 24-28 38 48z" fill="#cbd5e1" opacity="0.78"/>
        </svg>
        """;

    /// <summary>
    /// Serves a local picture file, or null when there is no file. A file that is not a recognised
    /// picture gets the placeholder instead.
    /// </summary>
    public static IResult? Local(string? path, ArtworkCacheMode mode = ArtworkCacheMode.Revalidate)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        if (!HasRecognizedImageSignature(path))
        {
            return Placeholder();
        }

        return new CacheableArtworkFileResult(path, GetArtworkContentType(path), mode);
    }

    /// <summary>
    /// The keep rule for a served picture: a year only when the exact size asked for was served and
    /// the address's change marker matches the picture's current marker; otherwise checked on each use,
    /// so a stand-in copy (D5) or an out-of-date address is never kept past the next look.
    /// </summary>
    public static ArtworkCacheMode ModeFor(HttpRequest request, string? currentMarker, bool servedRequestedSize)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestedMarker = request.Query[ArtworkVersionMarker.QueryKey].ToString();
        return servedRequestedSize
               && !string.IsNullOrWhiteSpace(currentMarker)
               && string.Equals(requestedMarker, currentMarker, StringComparison.Ordinal)
            ? ArtworkCacheMode.Immutable
            : ArtworkCacheMode.Revalidate;
    }

    /// <summary>The neutral "no picture" stand-in. Never kept, so the real picture shows once it exists.</summary>
    public static IResult Placeholder() => new PlaceholderResult();

    internal static bool HasRecognizedImageSignature(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[12];
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                header.Length,
                FileOptions.SequentialScan);
            var read = stream.Read(header);
            return read >= 2 && header[0] == 0xFF && header[1] == 0xD8
                || read >= 8 && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
                || read >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8))
                || read >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8)
                || read >= 2 && header[0] == (byte)'B' && header[1] == (byte)'M';
        }
        catch (IOException)
        {
            // An unreadable file is served as the placeholder rather than failing the page.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above: no access means no picture, not an error page.
            return false;
        }
    }

    internal static string GetArtworkContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/jpeg",
        };

    private sealed class CacheableArtworkFileResult(string path, string contentType, ArtworkCacheMode mode) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            var file = new FileInfo(path);
            var modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            var tag = new EntityTagHeaderValue($"\"{file.Length:x}-{file.LastWriteTimeUtc.Ticks:x}\"");
            httpContext.Response.Headers.CacheControl = mode == ArtworkCacheMode.Immutable
                ? ImmutableCacheControl
                : RevalidateCacheControl;
            return Results.File(
                    path,
                    contentType,
                    lastModified: modified,
                    entityTag: tag,
                    enableRangeProcessing: false)
                .ExecuteAsync(httpContext);
        }
    }

    private sealed class PlaceholderResult : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.CacheControl = PlaceholderCacheControl;
            return Results.Text(PlaceholderSvg, "image/svg+xml").ExecuteAsync(httpContext);
        }
    }
}
