namespace MediaEngine.Domain.Configuration;

/// <summary>
/// Which outside sources a library kind needs before its files can be identified at all (Plugins and Integrations screens v4 D38).
/// Movies and TV Shows are matched by TMDB or TheTVDB, Comics by Comic Vine. Every other kind identifies from its own files or from a
/// source that needs no key (Music, Books, Audiobooks, Photos, Videos), so it is never held. The Engine uses this to hold a folder's
/// import, and the Dashboard uses the same rule to show "Waiting for a source", so the two always agree.
/// </summary>
public static class IdentificationSourceRules
{
    private static readonly string[] FilmAndTelevision = ["tmdb", "tvdb"];
    private static readonly string[] Comics = ["comicvine"];

    /// <summary>The sources that can identify files of <paramref name="kindKey"/>; any one of them is enough. Empty when the kind is never held.</summary>
    public static IReadOnlyList<string> RequiredFor(string? kindKey) => kindKey?.Trim().ToLowerInvariant() switch
    {
        "movies" or "tv" => FilmAndTelevision,
        "comics" => Comics,
        _ => [],
    };

    /// <summary>True when the kind needs a source and none of the sources it accepts is working.</summary>
    public static bool IsHeld(string? kindKey, Func<string, bool> isWorking)
    {
        ArgumentNullException.ThrowIfNull(isWorking);
        var required = RequiredFor(kindKey);
        return required.Count > 0 && !required.Any(isWorking);
    }

    /// <summary>True when a provider is switched on and, if it needs a key, has one saved.</summary>
    public static bool IsWorking(ProviderConfiguration provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.Enabled && (!provider.RequiresApiKey || HasKey(provider));
    }

    /// <summary>True when the provider has a key (or a username and password, for basic delivery) saved.</summary>
    public static bool HasKey(ProviderConfiguration provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return !string.IsNullOrWhiteSpace(provider.HttpClient?.ApiKeyOverride)
            || !string.IsNullOrWhiteSpace(provider.HttpClient?.ApiKey)
            || (string.Equals(provider.HttpClient?.ApiKeyDelivery, "basic", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(provider.HttpClient?.Username)
                && !string.IsNullOrWhiteSpace(provider.HttpClient?.Password));
    }
}
