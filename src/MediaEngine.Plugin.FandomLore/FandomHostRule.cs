namespace MediaEngine.Plugin.FandomLore;

/// <summary>
/// The only hosts the lore plugin may call: Wikidata for the source lookup and Fandom wikis for the structured
/// page list. Anything else is ignored or refused, so a Library never reaches an arbitrary host through this plugin.
/// </summary>
public static class FandomHostRule
{
    public const string WikidataHost = "www.wikidata.org";

    private const string FandomDomain = "fandom.com";

    /// <summary>True for a subdomain of <c>fandom.com</c>, such as <c>starwars.fandom.com</c>. Bare <c>fandom.com</c> is the company site, not a wiki.</summary>
    public static bool IsFandomHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var value = host.Trim().TrimEnd('.').ToLowerInvariant();
        return value.Length > FandomDomain.Length + 1 && value.EndsWith("." + FandomDomain, StringComparison.Ordinal);
    }

    /// <summary>True when <paramref name="url"/> is an absolute <c>https</c> address on a Fandom host (default port, no credentials).</summary>
    public static bool IsFandomAddress(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && IsFandomAddress(uri);

    /// <inheritdoc cref="IsFandomAddress(string?)"/>
    public static bool IsFandomAddress(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && IsFandomHost(uri.Host);
}
