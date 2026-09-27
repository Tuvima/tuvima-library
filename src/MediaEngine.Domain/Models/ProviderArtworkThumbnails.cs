namespace MediaEngine.Domain.Models;

public static class ProviderArtworkThumbnails
{
    // Unknown providers must not load a full-size original into a picker tile.
    public static string ForCover(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return string.Empty;
        if (uri.Host.Equals("covers.openlibrary.org", StringComparison.OrdinalIgnoreCase))
            return System.Text.RegularExpressions.Regex.Replace(url, "-[LMS]\\.jpg", "-M.jpg", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (uri.Host.Equals("m.media-amazon.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("images-na.ssl-images-amazon.com", StringComparison.OrdinalIgnoreCase))
        {
            var path = System.Text.RegularExpressions.Regex.Replace(uri.AbsolutePath, @"(?:\._[^/]+_)?\.(jpg|jpeg|png)$", "._SL300_.$1", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return path == uri.AbsolutePath ? string.Empty : uri.GetLeftPart(UriPartial.Authority) + path;
        }
        return string.Empty;
    }
}
