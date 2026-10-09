namespace MediaEngine.Domain.Configuration;

/// <summary>
/// Rules for the single public address (<c>network.remote.public_hostname</c>) that passkeys,
/// linked sign-in and password-reset links are built from.
/// </summary>
public static class PublicAddress
{
    public const string RuleMessage =
        "The public address must be an https address with no path, query or fragment, for example https://tuvima.example.com. Plain http is allowed only for localhost.";

    /// <summary>
    /// True when <paramref name="origin"/> is the same origin as the public address: same scheme, host and port.
    /// Hosts are compared in their ASCII (punycode) form, which is what browsers send, so an address typed with
    /// Unicode letters still matches. A missing or invalid address never matches.
    /// </summary>
    public static bool IsSameOrigin(string? publicAddress, string? origin)
    {
        if (!IsValid(publicAddress)
            || !Uri.TryCreate(publicAddress!.Trim(), UriKind.Absolute, out var address)
            || !Uri.TryCreate(origin?.Trim(), UriKind.Absolute, out var other))
        {
            return false;
        }

        return string.Equals(address.Scheme, other.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(address.IdnHost, other.IdnHost, StringComparison.OrdinalIgnoreCase)
            && address.Port == other.Port;
    }

    /// <summary>
    /// True for an absolute origin with no path, query, fragment or credentials. The scheme must be
    /// https; plain http is accepted only for a loopback host (local development).
    /// </summary>
    public static bool IsValid(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && (uri.AbsolutePath.Length == 0 || uri.AbsolutePath == "/")
            && !text.Contains('?')
            && !text.Contains('#');
    }
}
