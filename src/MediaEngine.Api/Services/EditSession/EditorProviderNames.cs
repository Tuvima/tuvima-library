using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// The one lookup from a provider's key to the name a person knows it by (universal editor v7 D4): "TMDB", "TheTVDB", "Apple Books",
/// "MusicBrainz", "Wikidata". It reads <c>config/providers/</c> (<c>product_name</c>, then <c>display_name</c>) and never returns the key itself,
/// so a code name such as <c>apple_api</c> cannot reach the Match tab, the header's links or a History line. The header, the current match
/// and the Match tab's results all ask here.
/// </summary>
public sealed class EditorProviderNames(Func<IReadOnlyList<ProviderConfiguration>> providers)
{
    /// <summary>Reads the providers from the config folder (<c>config/providers/</c>).</summary>
    public EditorProviderNames(IConfigurationLoader loader) : this(loader.LoadAllProviders)
    {
    }

    /// <summary>The product name of the provider with this key (its config name or one of its <c>product_aliases</c>); null when no provider has it.</summary>
    /// <param name="providerKey">The key the Engine uses for the source: <c>apple_api</c>, <c>apple_books</c>, <c>tmdb</c> ...</param>
    /// <param name="editorKind">The editor kind of the item (<c>album</c>, <c>song</c> ...), for a source whose product depends on it.</param>
    public string? ProductName(string? providerKey, string? editorKind = null) => Resolve(providers(), providerKey, editorKind);

    /// <summary>The same rule over a list of providers, so it can be tested without a config folder.</summary>
    public static string? Resolve(IReadOnlyList<ProviderConfiguration> configured, string? providerKey, string? editorKind = null)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return null;
        }

        var key = providerKey.Trim();
        var provider = configured.FirstOrDefault(candidate => string.Equals(candidate.Name, key, StringComparison.OrdinalIgnoreCase))
            ?? configured.FirstOrDefault(candidate => candidate.ProductAliases?.Any(alias => string.Equals(alias, key, StringComparison.OrdinalIgnoreCase)) == true);
        if (provider is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(editorKind)
            && provider.ProductNameByKind is { } byKind
            && byKind.FirstOrDefault(pair => string.Equals(pair.Key, editorKind, StringComparison.OrdinalIgnoreCase)) is { Value: { Length: > 0 } named })
        {
            return named;
        }

        var name = !string.IsNullOrWhiteSpace(provider.ProductName) ? provider.ProductName : provider.DisplayName;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }
}
