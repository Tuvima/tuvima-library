using MediaEngine.Providers.Services.StashBox;

namespace MediaEngine.Api.Services.Settings;

/// <summary>
/// Keeps a saved Stash-box account key with the address it was saved for. The provider configuration route can edit a provider's
/// endpoints, and the default Stash-box server's key is sent to whatever address its endpoint names, so changing that address
/// through the generic route would send the stored key to a host the person never gave it to (security review L6). A different
/// server is added in the Stash-box settings, where it gets its own key.
/// </summary>
public static class ProviderEndpointPinning
{
    public const string StashBoxAddressMessage =
        "The Stash-box address cannot be changed here. Add your own server in the Stash-box settings, with its own key.";

    /// <summary>
    /// True when <paramref name="requested"/> would change an endpoint of the named provider that holds a pinned key.
    /// Re-sending the saved address, or an address that differs only in case or a trailing slash, is not a change.
    /// </summary>
    public static bool ChangesPinnedEndpoint(
        string providerName,
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string>? requested)
    {
        if (requested is null || !string.Equals(providerName, StashBoxSettings.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var (key, value) in requested)
        {
            if (!current.TryGetValue(key, out var saved) || !Same(saved, value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Same(string? saved, string? requested) =>
        string.Equals(Canonical(saved), Canonical(requested), StringComparison.OrdinalIgnoreCase);

    private static string Canonical(string? value) => (value ?? string.Empty).Trim().TrimEnd('/');
}
