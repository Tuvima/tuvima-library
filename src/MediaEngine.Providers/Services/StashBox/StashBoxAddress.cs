using System.Net;

namespace MediaEngine.Providers.Services.StashBox;

/// <summary>
/// Checks a stash-box server address an administrator typed. Only https is allowed, except a server on this machine
/// (localhost or a loopback address). No account details, query or fragment may be part of the address, so a key can
/// never end up in a URL, and the address can only ever point at one host.
/// </summary>
public static class StashBoxAddress
{
    public const int MaxLength = 200;
    public const string DefaultPath = "/graphql";

    public static bool TryNormalize(string? input, out Uri address)
    {
        address = null!;
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength || text.Any(char.IsControl))
        {
            return false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        var local = IsLocalHost(uri);
        var allowed = uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && local);
        if (!allowed)
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        var builder = new UriBuilder(uri) { Path = path.Length == 0 ? DefaultPath : path, Query = string.Empty, Fragment = string.Empty };
        address = builder.Uri;
        return true;
    }

    public static bool IsLocalHost(Uri uri) =>
        uri.IsLoopback
        || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    /// <summary>
    /// True when <paramref name="imageUrl"/> is an https address on the server's own host or one of its sub-hosts (for example a
    /// CDN host such as cdn.example.org for a server at example.org). A server on this machine may serve images over its own scheme.
    /// </summary>
    public static bool IsOwnImageHost(Uri server, Uri imageUrl)
    {
        if (!string.IsNullOrEmpty(imageUrl.UserInfo))
        {
            return false;
        }

        if (IsLocalHost(server))
        {
            return string.Equals(server.Host, imageUrl.Host, StringComparison.OrdinalIgnoreCase)
                && server.Scheme == imageUrl.Scheme && server.Port == imageUrl.Port;
        }

        if (imageUrl.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var serverHost = server.Host.ToLowerInvariant();
        var imageHost = imageUrl.Host.ToLowerInvariant();
        if (imageHost == serverHost)
        {
            return true;
        }

        // The site's own domain: the last two labels of the server host (a bare IP address has none).
        if (IPAddress.TryParse(serverHost.Trim('[', ']'), out _))
        {
            return false;
        }

        var labels = serverHost.Split('.');
        if (labels.Length < 2)
        {
            return false;
        }

        var site = string.Join('.', labels[^2..]);
        return imageHost == site || imageHost.EndsWith("." + site, StringComparison.Ordinal);
    }
}
