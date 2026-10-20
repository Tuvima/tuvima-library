namespace MediaEngine.Api.Services.Display;

internal static class DisplayArtworkUrlResolver
{
    public static string? Resolve(string? value, Guid assetId, string kind, string? state)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            if (IsExternalProviderUrl(value))
            {
                return $"/stream/{assetId}/{kind}";
            }

            return value;
        }

        return string.Equals(state, "present", StringComparison.OrdinalIgnoreCase)
            ? $"/stream/{assetId}/{kind}"
            : null;
    }

    /// <summary>True for a provider's http(s) link, which must be swapped for an Engine route before it reaches a browser.</summary>
    internal static bool IsExternalProviderUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
}
