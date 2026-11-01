using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Plugins.Sources;

/// <summary>
/// The metadata providers as entries in the Plugins list (Plugins and Integrations screens v3 D15): Apple, TMDB, Stash-box and the
/// rest are tiles like any plugin. A provider keeps its own id (its provider file name) and stays a separate object in the Engine;
/// the list only gains <see cref="PluginSummaryResponse.ListingKind"/> and a <see cref="PluginProviderInfoDto"/>.
/// Providers a plugin supplies are left out, because the plugin's own tile already stands for them.
/// </summary>
public sealed class ProviderListings(IConfigurationLoader loader, ProviderCapabilityCatalogue catalogue)
{
    /// <summary>The two core sources: always on, no switch.</summary>
    private static readonly HashSet<string> CoreSources = new(StringComparer.OrdinalIgnoreCase) { "wikidata_reconciliation", "local_filesystem" };

    public IReadOnlyList<PluginSummaryResponse> List()
    {
        var sheets = catalogue.Sources().ToDictionary(source => source.Name, StringComparer.OrdinalIgnoreCase);
        var result = new List<PluginSummaryResponse>();
        foreach (var config in loader.LoadAllProviders().Where(config => config.ProviderId is not null && config.PluginId is null))
        {
            sheets.TryGetValue(config.Name, out var sheet);
            result.Add(ToSummary(config, sheet));
        }

        return result;
    }

    public static bool IsCore(string name) => CoreSources.Contains(name);

    internal static bool HasKey(ProviderConfiguration config) =>
        !string.IsNullOrWhiteSpace(config.HttpClient?.ApiKeyOverride)
        || !string.IsNullOrWhiteSpace(config.HttpClient?.ApiKey)
        || (string.Equals(config.HttpClient?.ApiKeyDelivery, "basic", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(config.HttpClient?.Username)
            && !string.IsNullOrWhiteSpace(config.HttpClient?.Password));

    private static PluginSummaryResponse ToSummary(ProviderConfiguration config, PluginSourceDto? sheet)
    {
        var core = IsCore(config.Name);
        var media = config.CanHandle?.MediaTypes is { Count: > 0 } listed ? listed : [];
        return new PluginSummaryResponse
        {
            Id = config.Name,
            Name = config.DisplayName ?? config.Name,
            Version = config.Version,
            Description = FirstSentence(config.Onboarding?.Intro?.Summary),
            ShortDescription = PluginDescriptions.Short(config.UiMetadata?.ShortDescription, config.Onboarding?.Intro?.Summary),
            LongDescription = config.Onboarding?.Intro?.Summary ?? "",
            Provides = PluginProvides.Resolve(config.UiMetadata?.Provides).ToList(),
            // The bundled brand icon, unless an administrator uploaded their own (the Engine serves that at the provider icon action).
            IconPath = !string.IsNullOrWhiteSpace(config.CustomIconName)
                ? $"settings/providers/{config.Name}/icon"
                : string.IsNullOrWhiteSpace(config.Icon) ? null : config.Icon,
            Removable = false,
            Enabled = core || config.Enabled,
            IsBuiltIn = true,
            Kind = "official",
            TrustLevel = "official",
            Category = PluginCategories.Resolve(config.UiMetadata?.PluginCategory ?? PluginCategories.Intake),
            MediaTypes = [.. media.Select(MediaKey).Where(key => key is not null).Select(key => key!).Distinct(StringComparer.OrdinalIgnoreCase)],
            ListingKind = "provider",
            Access = sheet?.Access ?? new PluginAccessDto(),
            Provider = new PluginProviderInfoDto
            {
                RequiresKey = config.RequiresApiKey,
                KeyPresent = HasKey(config),
                AlwaysOn = core,
                VaultOnly = config.AdultOnly,
                Supplies = sheet?.Supplies ?? [],
                SendsDataTo = sheet?.SendsDataTo ?? [],
                About = config.Onboarding?.Intro?.Summary ?? "",
                SignupUrl = config.Onboarding?.SignupUrl,
            },
        };
    }

    // The tile shows one line; the whole explanation is on the provider's own page.
    private static string FirstSentence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var end = text.IndexOf(". ", StringComparison.Ordinal);
        return end < 0 ? text : text[..(end + 1)];
    }

    // The provider files say "Movies", "TV", "Videos"; the page speaks in lower-case media keys.
    private static string? MediaKey(string media) => media.Trim().ToLowerInvariant() switch
    {
        "books" or "book" => "books",
        "audiobooks" or "audiobook" => "audiobooks",
        "comics" or "comic" => "comics",
        "movies" or "movie" => "movies",
        "tv" or "television" => "tv",
        "music" => "music",
        "videos" or "video" => "videos",
        "photos" or "photo" => "photos",
        "" => null,
        var other => other,
    };
}
