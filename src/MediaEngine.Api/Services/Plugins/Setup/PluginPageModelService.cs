using System.Text.Json;
using MediaEngine.Api.Services.Plugins.Sources;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Plugins;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Plugins.Setup;

/// <summary>
/// Gathers what the page model needs for one plugin (its file, which keys are set, the values held outside its saved settings) and
/// hands it to <see cref="PluginPageModelBuilder"/> (Plugins and Integrations screens v5 D46). Reads only what is already in memory
/// or in the Library's own stores: no network call is made.
/// </summary>
public sealed class PluginPageModelService(
    PluginCatalog catalog,
    IConfigurationLoader loader,
    ProviderCapabilityCatalogue sources,
    IConfigSecretsStore secrets,
    IEnumerable<IPluginSettingValueSource> valueSources,
    ILogger<PluginPageModelService> logger)
{
    /// <summary>The page model of a plugin or a metadata source, or null when there is none with that id.</summary>
    public async Task<PluginSetupPageResponse?> BuildAsync(string pluginId, CancellationToken ct)
    {
        if (catalog.Get(pluginId) is { Manifest.Hidden: false } registration)
        {
            return await BuildForManifestAsync(registration, ct).ConfigureAwait(false);
        }

        var config = loader.LoadAllProviders().FirstOrDefault(provider =>
            provider.ProviderId is not null && provider.PluginId is null && string.Equals(provider.Name, pluginId, StringComparison.OrdinalIgnoreCase));
        return config is null ? null : await BuildForSourceAsync(config, ct).ConfigureAwait(false);
    }

    private async Task<PluginSetupPageResponse> BuildForManifestAsync(PluginRegistration registration, CancellationToken ct)
    {
        var manifest = registration.Manifest;
        var access = PluginAccessMapper.Map(
            manifest.Declaration,
            [.. manifest.Network.Select(destination => (destination.Host, (string?)destination.Sends))],
            manifest.Secrets.Count > 0,
            PluginAccessMapper.MediaLabelsFor(PluginCategories.ResolveMediaTypes(manifest.MediaTypes)),
            manifest.MediaTypes,
            manifest.Category);
        var configured = await ConfiguredAsync(manifest, ct).ConfigureAwait(false);
        var description = PluginSettingsDescriptionReader.Read(registration.SettingsSchema);
        var external = await ExternalValuesAsync(description, ct).ConfigureAwait(false);
        return PluginPageModelBuilder.Build(PluginPageModelInputs.FromManifest(registration, access, configured, external));
    }

    private async Task<PluginSetupPageResponse> BuildForSourceAsync(ProviderConfiguration config, CancellationToken ct)
    {
        var sheet = sources.Sources().FirstOrDefault(source => string.Equals(source.Name, config.Name, StringComparison.OrdinalIgnoreCase));
        var configured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ProviderListings.HasKey(config))
        {
            configured.UnionWith(config.Onboarding?.Credentials.Select(credential => credential.Key) ?? []);
        }

        var description = PluginSettingsDescriptionReader.Read(config.SettingsSchema);
        var external = await ExternalValuesAsync(description, ct).ConfigureAwait(false);
        return PluginPageModelBuilder.Build(PluginPageModelInputs.FromSourceFile(config, sheet, ProviderListings.IsCore(config.Name), configured, external));
    }

    private async Task<IReadOnlySet<string>> ConfiguredAsync(PluginManifest manifest, CancellationToken ct)
    {
        if (manifest.Secrets.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            return (await secrets.ListNamesAsync(SecretOwner.Plugin(manifest.Id), ct).ConfigureAwait(false)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
        {
            logger.LogWarning("Secrets for plugin {PluginId} could not be read ({ErrorType}); showing none as set", manifest.Id, ex.GetType().Name);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task<IReadOnlyDictionary<string, JsonElement>> ExternalValuesAsync(PluginSettingsDescription description, CancellationToken ct)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in description.Settings.Select(setting => setting.ValueSource).Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var source = valueSources.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (source is null)
            {
                logger.LogWarning("A plugin setting names the value source {ValueSource}, which Library does not have", name);
                continue;
            }

            foreach (var (key, value) in await source.ReadAsync(ct).ConfigureAwait(false))
            {
                values[key] = value;
            }
        }

        return values;
    }
}
