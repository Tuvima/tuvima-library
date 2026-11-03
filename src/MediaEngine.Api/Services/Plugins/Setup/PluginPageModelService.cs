using System.Text.Json;
using MediaEngine.Api.Services.Plugins.Files;
using MediaEngine.Api.Services.Plugins.Sources;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Plugins;
using MediaEngine.Storage.Configuration;
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
    PluginActivityLimits activityLimits,
    PluginFileService files,
    ILogger<PluginPageModelService> logger)
{
    /// <summary>The page model of a plugin or a metadata source, or null when there is none with that id.</summary>
    public async Task<PluginSetupPageResponse?> BuildAsync(string pluginId, CancellationToken ct)
    {
        if (catalog.Get(pluginId) is { Manifest.Hidden: false } registration)
        {
            return await BuildForManifestAsync(registration, ct).ConfigureAwait(false);
        }

        var config = FindSource(pluginId, out var unreadable);
        if (config is not null)
        {
            return await BuildForSourceAsync(config, ct).ConfigureAwait(false);
        }

        // One source file failed the loader's checks. The page still opens, with only the Plugin file card, which shows the problem
        // and (for an administrator on a computer) lets the file be fixed or reset (v5 D55).
        return unreadable && await files.StateAsync(pluginId, ct).ConfigureAwait(false) is { } state
            ? PluginPageModelBuilder.Build(new PluginPageModelInput { PluginId = pluginId, Name = pluginId, File = state })
            : null;
    }

    private ProviderConfiguration? FindSource(string pluginId, out bool unreadable)
    {
        unreadable = false;
        try
        {
            return loader.LoadAllProviders().FirstOrDefault(provider =>
                provider.ProviderId is not null && provider.PluginId is null && string.Equals(provider.Name, pluginId, StringComparison.OrdinalIgnoreCase));
        }
        catch (ConfigValidationException ex)
        {
            logger.LogWarning("A provider file failed its checks, so the page of {PluginId} shows only its Plugin file card: {Reason}", pluginId, ex.Message);
            unreadable = true;
            return null;
        }
    }

    /// <summary>
    /// The value source that holds a setting the plugin's JSON says is kept elsewhere (<c>value_source</c>), or null when the plugin,
    /// the setting or the source does not exist. The page's changes to such a setting go through it.
    /// </summary>
    public IPluginSettingValueSource? ValueSourceFor(string pluginId, string settingKey)
    {
        var description = DescriptionFor(pluginId);
        var name = description?.Settings.FirstOrDefault(setting => string.Equals(setting.Key, settingKey, StringComparison.OrdinalIgnoreCase))?.ValueSource;
        return string.IsNullOrEmpty(name)
            ? null
            : valueSources.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private PluginSettingsDescription? DescriptionFor(string pluginId)
    {
        if (catalog.Get(pluginId) is { Manifest.Hidden: false } registration)
        {
            return PluginSettingsDescriptionReader.Read(registration.SettingsSchema);
        }

        var config = FindSource(pluginId, out _);
        return config is null ? null : PluginSettingsDescriptionReader.Read(config.SettingsSchema);
    }

    /// <summary>True when a plugin or a metadata source with this id has a page.</summary>
    public bool Exists(string pluginId) => DescriptionFor(pluginId) is not null;

    /// <summary>Saves how much of the plugin's Activity list is kept. Throws <see cref="ArgumentOutOfRangeException"/> for a number the page does not allow.</summary>
    public PluginActivityCap SetActivityCap(string pluginId, int maxEntries, int maxDays) => activityLimits.Set(pluginId, maxEntries, maxDays);

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
        return PluginPageModelBuilder.Build(PluginPageModelInputs.FromManifest(registration, access, configured, external) with
        {
            ActivityCap = activityLimits.Get(manifest.Id),
            File = await files.StateAsync(manifest.Id, ct).ConfigureAwait(false),
        });
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
        return PluginPageModelBuilder.Build(PluginPageModelInputs.FromSourceFile(config, sheet, ProviderListings.IsCore(config.Name), configured, external) with
        {
            ActivityCap = activityLimits.Get(config.Name),
            File = await files.StateAsync(config.Name, ct).ConfigureAwait(false),
        });
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
