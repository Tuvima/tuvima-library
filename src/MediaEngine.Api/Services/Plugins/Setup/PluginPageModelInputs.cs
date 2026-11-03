using System.Text.Json;
using MediaEngine.Api.Services.Plugins.Sources;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins.Setup;

/// <summary>
/// Reads either plugin file format into the one <see cref="PluginPageModelInput"/> (Plugins and Integrations screens v5 D51): a
/// plugin manifest (and the manifests the compiled-in plugins build) on one side, a provider source file on the other. After this
/// step the page model builder cannot tell them apart.
/// </summary>
internal static class PluginPageModelInputs
{
    private static readonly JsonSerializerOptions Json = new();

    public static PluginPageModelInput FromManifest(
        PluginRegistration registration,
        PluginAccessDto access,
        IReadOnlySet<string> configuredCredentials,
        IReadOnlyDictionary<string, JsonElement> externalValues)
    {
        var manifest = registration.Manifest;
        var settings = PluginSettingsDescriptionReader.Read(registration.SettingsSchema);
        var declared = settings.Settings.Select(setting => setting.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notes = manifest.DefaultSettings.Keys
            .Where(key => !declared.Contains(key))
            .Select(key => new PluginSettingsNote(key, "settings_schema", $"The setting \"{key}\" has a default but is not described in settings_schema, so it is not shown."))
            .ToList();
        return new PluginPageModelInput
        {
            PluginId = manifest.Id,
            Name = manifest.Name,
            Definition = registration.ManifestPath is null ? "compiled" : "manifest",
            Version = manifest.Version,
            Description = manifest.Description,
            Kind = KindWord(manifest.Kind),
            TrustLevel = TrustWord(registration.Trust),
            FileName = registration.ManifestPath is null ? null : Path.GetFileName(registration.ManifestPath),
            Provides = manifest.Provides,
            MediaLabels = PluginAccessMapper.MediaLabelsFor(PluginCategories.ResolveMediaTypes(manifest.MediaTypes)),
            Supplies = [.. manifest.Capabilities.Select(capability => capability.Name).Where(name => !string.IsNullOrWhiteSpace(name))],
            Access = access,
            Credentials = PluginCredentialMapper.FromManifest(manifest),
            Destinations = manifest.Network,
            Settings = settings,
            Values = registration.Settings,
            ConfiguredCredentials = configuredCredentials,
            ExternalValues = externalValues,
            ExtraNotes = notes,
        };
    }

    public static PluginPageModelInput FromSourceFile(
        ProviderConfiguration config,
        PluginSourceDto? sheet,
        bool alwaysOn,
        IReadOnlySet<string> configuredCredentials,
        IReadOnlyDictionary<string, JsonElement> externalValues)
    {
        // Every setting of a source file, its tunables included, is described by the file's own settings_schema (D51, D58).
        var settings = PluginSettingsDescriptionReader.Read(config.SettingsSchema);

        var values = TunableValues(config);
        foreach (var (key, value) in externalValues)
        {
            values[key] = value;
        }

        var hosts = sheet?.SendsDataTo ?? config.Endpoints.Values
            .Select(address => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : null)
            .Where(host => host is not null)
            .Select(host => host!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sends = config.RequiresApiKey
            ? "What it is asked about (titles, names and identifiers) and your key"
            : "What it is asked about (titles, names and identifiers)";
        var summary = config.Onboarding?.Intro?.Summary ?? "";
        var defaultActions = new List<PluginDeclaredAction>();
        if (config.Onboarding?.AuthenticationProbe is not null)
        {
            defaultActions.Add(new PluginDeclaredAction(PluginDeclaredActions.TestConnection, PluginSettingsDescriptionReader.DefaultActionLabel(PluginDeclaredActions.TestConnection)));
        }

        if (!alwaysOn)
        {
            defaultActions.Add(new PluginDeclaredAction(PluginDeclaredActions.CheckNow, PluginSettingsDescriptionReader.DefaultActionLabel(PluginDeclaredActions.CheckNow)));
        }

        return new PluginPageModelInput
        {
            PluginId = config.Name,
            Name = config.DisplayName ?? config.Name,
            Definition = "source_file",
            Version = config.Version,
            Description = string.IsNullOrWhiteSpace(summary) ? config.UiMetadata?.ShortDescription ?? "" : summary,
            Kind = "official",
            TrustLevel = "official",
            FileName = $"{config.Name}.json",
            Provides = PluginProvides.Resolve(config.UiMetadata?.Provides),
            MediaLabels = sheet?.MediaTypes ?? [],
            Supplies = sheet?.Supplies ?? [],
            Access = sheet?.Access ?? new PluginAccessDto(),
            Credentials = PluginCredentialMapper.FromSourceFile(config),
            Destinations = [.. hosts.Select(host => new PluginNetworkDestination { Host = host, Sends = sends })],
            Settings = settings,
            Values = values,
            ConfiguredCredentials = configuredCredentials,
            ExternalValues = externalValues,
            DefaultActions = defaultActions,
            NoSettingsCard = alwaysOn,
        };
    }

    /// <summary>The current value of each tunable a source file keeps in its own fields (the file's <c>settings_schema</c> describes them).</summary>
    private static Dictionary<string, JsonElement> TunableValues(ProviderConfiguration config)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["language_strategy"] = JsonSerializer.SerializeToElement(config.LanguageStrategyRaw, Json),
            ["timeout_seconds"] = JsonSerializer.SerializeToElement(config.HttpClient?.TimeoutSeconds ?? 10, Json),
            ["throttle_ms"] = JsonSerializer.SerializeToElement(config.ThrottleMs, Json),
            ["max_concurrency"] = JsonSerializer.SerializeToElement(config.MaxConcurrency, Json),
            ["weight"] = JsonSerializer.SerializeToElement(config.Weight, Json),
        };

        if (PrimaryEndpoint(config) is { } endpoint)
        {
            values["primary_endpoint"] = JsonSerializer.SerializeToElement(endpoint, Json);
        }

        if (config.RateLimit?.RequestsPerSecond is { } rate)
        {
            values["requests_per_second"] = JsonSerializer.SerializeToElement(rate, Json);
        }

        if (config.CacheTtlHours is { } hours)
        {
            values["cache_ttl_hours"] = JsonSerializer.SerializeToElement(hours, Json);
        }

        return values;
    }

    private static string? PrimaryEndpoint(ProviderConfiguration config) =>
        config.Endpoints.GetValueOrDefault("api") ?? config.Endpoints.Values.FirstOrDefault();

    private static string KindWord(PluginKind kind) => kind switch
    {
        PluginKind.Declarative => "declarative",
        PluginKind.Service => "service",
        PluginKind.Sandboxed => "sandboxed",
        _ => "official",
    };

    private static string TrustWord(PluginTrustLevel trust) => trust switch
    {
        PluginTrustLevel.Official => "official",
        PluginTrustLevel.Verified => "verified",
        PluginTrustLevel.Community => "community",
        _ => "private",
    };
}
